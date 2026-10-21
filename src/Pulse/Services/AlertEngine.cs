using LibreHardwareMonitor.Hardware;
using Pulse.Models;

namespace Pulse.Services;

public enum AlertEventKind
{
    Raised,
    Cleared,
}

/// <summary>Something an alert rule noticed. <see cref="Notify"/> is false when a cooldown keeps it quiet.</summary>
public sealed record AlertEvent(
    string RuleId,
    string TargetKey,
    AlertEventKind Kind,
    AlertSeverity Severity,
    string Title,
    string Message,
    DateTime Time,
    bool Notify);

/// <summary>
/// Evaluates alert rules against each snapshot. Pure logic with the clock passed in, so every path
/// (spike, sustained breach, flapping, sensor disappearing) is testable without hardware or timers.
///
/// Per rule and target (a drive, say), the state goes: OK → pending (breach seen, waiting out the hold
/// time) → firing (alert raised) → OK again when the breach ends (alert cleared). After notifying, a rule
/// stays quiet for its cooldown, so a reading hovering around the threshold doesn't spam notifications.
/// </summary>
public sealed class AlertEngine
{
    private enum Status { Ok, Pending, Firing }

    private sealed class TargetState
    {
        public Status Status;
        public DateTime PendingSince;
        public DateTime LastNotified = DateTime.MinValue;
        public bool HasSeenValue;

        /// <summary>The severity it fired with when not the rule's own (a pump that may only have moved).</summary>
        public AlertSeverity? FiredAs;
    }

    private readonly Dictionary<(string Rule, string Target), TargetState> _states = new();

    /// <summary>Rule ids and targets currently firing.</summary>
    public IReadOnlyCollection<(string Rule, string Target)> Firing =>
        _states.Where(kv => kv.Value.Status == Status.Firing).Select(kv => kv.Key).ToList();

    public bool IsCriticalFiring(IEnumerable<AlertRule> rules) =>
        rules.Any(r => _states.Any(kv => kv.Key.Rule == r.Id && kv.Value.Status == Status.Firing
                                         && (kv.Value.FiredAs ?? r.Severity) == AlertSeverity.Critical));

    /// <summary>
    /// One reading to check: a target key (for per-drive state), a label, the value and the threshold.
    /// The rest overrides the rule for the pump while <see cref="PumpWatch"/> thinks the cooling changed:
    /// <paramref name="Breach"/> decides without the value, <paramref name="Note"/> is added to the message,
    /// and an alert with its own <paramref name="Severity"/> notifies only once.
    /// </summary>
    private sealed record Check(string Key, string Label, float? Value, double Threshold, SensorType Type, string? SensorId = null,
        bool? Breach = null, AlertSeverity? Severity = null, string? Title = null, string? Note = null);

    /// <param name="pump">The pump watch's view; null treats the pump role as checked and unchanged.</param>
    public IReadOnlyList<AlertEvent> Evaluate(
        HardwareSnapshot snapshot, IEnumerable<AlertRule> rules, string? pumpSensorId, DateTime now, PumpStatus? pump = null)
    {
        var events = new List<AlertEvent>();

        foreach (var rule in rules.Where(r => r.Enabled))
        {
            if (rule.Target == AlertTarget.Pump)
            {
                ForgetOtherPumps(rule, pumpSensorId, now, events);
                // Pulse just started and is still comparing the pump with how it read before: no verdict yet.
                if (pump is { Settled: false })
                    continue;
            }

            foreach (var check in Resolve(rule, snapshot, pumpSensorId, pump?.Change))
            {
                var key = (rule.Id, check.Key);
                // Not read on purpose (a cooler Pulse leaves to iCUE): unknown, not missing. Nothing changes.
                if (check.Value is null && check.SensorId is { } unread && snapshot.IsUnread(unread))
                    continue;
                if (!_states.TryGetValue(key, out var state))
                    _states[key] = state = new TargetState();

                Step(rule, check, state, now, events);
            }
        }

        // Rules that were turned off or deleted: clear anything they had firing and forget their state.
        var enabledIds = rules.Where(r => r.Enabled).Select(r => r.Id).ToHashSet();
        foreach (var (key, state) in _states.Where(kv => !enabledIds.Contains(kv.Key.Rule)).ToList())
        {
            if (state.Status == Status.Firing)
            {
                var name = rules.FirstOrDefault(r => r.Id == key.Rule)?.Name ?? "Alert";
                events.Add(new AlertEvent(key.Rule, key.Target, AlertEventKind.Cleared, AlertSeverity.Warning,
                    $"{name}: turned off", "", now, Notify: false));
            }
            _states.Remove(key);
        }

        return events;
    }

    private static void Step(AlertRule rule, Check check, TargetState state, DateTime now, List<AlertEvent> events)
    {
        bool? breach;
        if (check.Value is { } v && !float.IsNaN(v))
        {
            state.HasSeenValue = true;
            breach = check.Breach ?? (rule.Condition == AlertCondition.Above ? v > check.Threshold : v < check.Threshold);
        }
        else
        {
            // A reading that was there and vanished counts as a breach only where the rule says so (the pump).
            // Otherwise it's unknown, and the state is left as it was.
            breach = rule.AlertWhenMissing && state.HasSeenValue ? true : null;
        }

        if (breach is null)
            return;

        if (breach == false)
        {
            if (state.Status == Status.Firing)
                events.Add(Event(rule, check, AlertEventKind.Cleared, $"{check.Label} is back to normal",
                    Describe(check), now, notify: false));
            state.Status = Status.Ok;
            return;
        }

        // Firing already, unless it fired softer than it now should (the user said the changed pump is the pump).
        if (state.Status == Status.Firing && (check.Severity ?? rule.Severity) <= (state.FiredAs ?? rule.Severity))
            return;
        if (state.Status == Status.Firing)
        {
            // Raised again at full strength, and heard: the softer notification doesn't count against the cooldown.
            state.Status = Status.Ok;
            state.LastNotified = DateTime.MinValue;
        }

        if (state.Status == Status.Ok)
        {
            state.Status = Status.Pending;
            state.PendingSince = now;
        }

        // Still pending: has the breach lasted long enough?
        if (now - state.PendingSince < TimeSpan.FromSeconds(rule.HoldSeconds))
            return;

        state.Status = Status.Firing;
        state.FiredAs = check.Severity;
        // A softened alert is a question for the user: asked once, not every cooldown.
        var notify = check.Severity is null
            ? now - state.LastNotified >= TimeSpan.FromMinutes(rule.CooldownMinutes)
            : state.LastNotified == DateTime.MinValue;
        if (notify)
            state.LastNotified = now;
        events.Add(Event(rule, check, AlertEventKind.Raised, Title(rule, check), Describe(check), now, notify));
    }

    /// <summary>
    /// The pump role moved to another sensor: what was firing for the old one no longer applies, so it's
    /// cleared and forgotten instead of keeping the tray red for a header that isn't the pump.
    /// </summary>
    private void ForgetOtherPumps(AlertRule rule, string? pumpSensorId, DateTime now, List<AlertEvent> events)
    {
        foreach (var (key, state) in _states.Where(kv => kv.Key.Rule == rule.Id && kv.Key.Target != pumpSensorId).ToList())
        {
            if (state.Status == Status.Firing)
                events.Add(new AlertEvent(rule.Id, key.Target, AlertEventKind.Cleared, state.FiredAs ?? rule.Severity,
                    $"{rule.Name}: pump header changed", "", now, Notify: false));
            _states.Remove(key);
        }
    }

    private static IEnumerable<Check> Resolve(AlertRule rule, HardwareSnapshot snapshot, string? pumpSensorId, PumpChange? change = null)
    {
        var all = snapshot.Hardware.SelectMany(h => h.AllSensors);
        switch (rule.Target)
        {
            case AlertTarget.Sensor when rule.SensorId is { } id:
                var sensor = all.FirstOrDefault(s => s.Id == id);
                yield return new Check(id, rule.Name, sensor?.Value, rule.Threshold, sensor?.Type ?? SensorType.Temperature, id);
                break;

            case AlertTarget.Pump when pumpSensorId is { Length: > 0 }:
                var pump = all.FirstOrDefault(s => s.Id == pumpSensorId);
                var check = new Check(pumpSensorId, "Pump", pump?.Value, rule.Threshold, SensorType.Fan, pumpSensorId);
                if (change?.SensorId == pumpSensorId)
                {
                    // Something still spins there, just not like the pump did: most likely the cooling changed while
                    // the PC was off. A warning, once, that stays up until the user answers, instead of a critical
                    // alarm every few minutes. Stopped (or gone) stays critical: it may be exactly that.
                    check = pump?.Value >= PumpWatch.SpinningRpm
                        ? check with { Breach = true, Severity = AlertSeverity.Warning, Title = "Pump reading changed", Note = change.AlertNote }
                        : check with { Note = change.AlertNote };
                }
                yield return check;
                break;

            case AlertTarget.CpuPackage:
                var cpu = snapshot.OfType(HardwareType.Cpu).FirstOrDefault()?.First(SensorType.Temperature, "CPU Package", "Core Max");
                if (cpu is not null)
                    yield return new Check("cpu", "CPU", cpu.Value, rule.Threshold, SensorType.Temperature, cpu.Id);
                break;

            case AlertTarget.GpuHotSpot:
                var hotSpot = snapshot.OfType(HardwareType.GpuNvidia, HardwareType.GpuAmd, HardwareType.GpuIntel)
                    .Select(g => g.First(SensorType.Temperature, "GPU Hot Spot")).FirstOrDefault(s => s is not null);
                if (hotSpot is not null)
                    yield return new Check("gpu-hotspot", "GPU hot spot", hotSpot.Value, rule.Threshold, SensorType.Temperature, hotSpot.Id);
                break;

            case AlertTarget.DriveTemperature:
                foreach (var drive in snapshot.OfType(HardwareType.Storage))
                {
                    var temp = drive.AllSensors.FirstOrDefault(s => s.Type == SensorType.Temperature && s.HasValue
                                                                   && !s.Name.Contains("Warning") && !s.Name.Contains("Critical"));
                    if (temp is null)
                        continue;
                    // NVMe drives report the temperature their maker considers too hot; prefer that.
                    var warning = drive.Value(SensorType.Temperature, "Warning Temperature");
                    yield return new Check(drive.Id, drive.Name.Trim(), temp.Value, warning ?? rule.Threshold, SensorType.Temperature, temp.Id);
                }
                break;
        }
    }

    /// <summary>
    /// The "above" limit of the first enabled alert watching <paramref name="sensorId"/>, if any. The details
    /// panel uses it for "time above the limit".
    /// </summary>
    public static double? AboveThresholdFor(string sensorId, HardwareSnapshot snapshot, IEnumerable<AlertRule> rules, string? pumpSensorId) =>
        rules.Where(r => r.Enabled && r.Condition == AlertCondition.Above)
            .SelectMany(r => Resolve(r, snapshot, pumpSensorId))
            .FirstOrDefault(c => c.SensorId == sensorId)?.Threshold;

    private static string Title(AlertRule rule, Check check) => check.Title ?? rule.Target switch
    {
        AlertTarget.DriveTemperature => $"{check.Label} is too hot",
        _ => rule.Name,
    };

    private static string Describe(Check check)
    {
        // Decided without the limit (the cooling changed): the note says it all.
        if (check is { Breach: not null, Note: { } explanation })
            return explanation;
        var value = check.Value is { } v
            ? SensorFormat.Format(check.Type, v)
            : "no reading";
        var limit = SensorFormat.Format(check.Type, (float)check.Threshold);
        var message = $"{check.Label}: {value} (limit {limit})";
        return check.Note is { } note ? $"{message}. {note}" : message;
    }

    private static AlertEvent Event(AlertRule rule, Check check, AlertEventKind kind, string title, string message,
        DateTime now, bool notify) =>
        new(rule.Id, check.Key, kind, check.Severity ?? rule.Severity, title, message, now, notify);
}
