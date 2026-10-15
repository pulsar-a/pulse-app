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
    }

    private readonly Dictionary<(string Rule, string Target), TargetState> _states = new();

    /// <summary>Rule ids and targets currently firing.</summary>
    public IReadOnlyCollection<(string Rule, string Target)> Firing =>
        _states.Where(kv => kv.Value.Status == Status.Firing).Select(kv => kv.Key).ToList();

    public bool IsCriticalFiring(IEnumerable<AlertRule> rules) =>
        rules.Any(r => r.Severity == AlertSeverity.Critical
                       && _states.Any(kv => kv.Key.Rule == r.Id && kv.Value.Status == Status.Firing));

    /// <summary>One reading to check: a target key (for per-drive state), a label, the value and the threshold.</summary>
    private sealed record Check(string Key, string Label, float? Value, double Threshold, SensorType Type, string? SensorId = null);

    public IReadOnlyList<AlertEvent> Evaluate(
        HardwareSnapshot snapshot, IEnumerable<AlertRule> rules, string? pumpSensorId, DateTime now)
    {
        var events = new List<AlertEvent>();
        var active = new HashSet<(string, string)>();

        foreach (var rule in rules.Where(r => r.Enabled))
        {
            foreach (var check in Resolve(rule, snapshot, pumpSensorId))
            {
                var key = (rule.Id, check.Key);
                active.Add(key);
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
            breach = rule.Condition == AlertCondition.Above ? v > check.Threshold : v < check.Threshold;
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
                events.Add(Event(rule, check.Key, AlertEventKind.Cleared, $"{check.Label} is back to normal",
                    Describe(check), now, notify: false));
            state.Status = Status.Ok;
            return;
        }

        if (state.Status == Status.Firing)
            return;

        if (state.Status == Status.Ok)
        {
            state.Status = Status.Pending;
            state.PendingSince = now;
        }

        // Still pending: has the breach lasted long enough?
        if (now - state.PendingSince < TimeSpan.FromSeconds(rule.HoldSeconds))
            return;

        state.Status = Status.Firing;
        var notify = now - state.LastNotified >= TimeSpan.FromMinutes(rule.CooldownMinutes);
        if (notify)
            state.LastNotified = now;
        events.Add(Event(rule, check.Key, AlertEventKind.Raised, Title(rule, check), Describe(check), now, notify));
    }

    private static IEnumerable<Check> Resolve(AlertRule rule, HardwareSnapshot snapshot, string? pumpSensorId)
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
                yield return new Check(pumpSensorId, "Pump", pump?.Value, rule.Threshold, SensorType.Fan, pumpSensorId);
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

    private static string Title(AlertRule rule, Check check) => rule.Target switch
    {
        AlertTarget.DriveTemperature => $"{check.Label} is too hot",
        _ => rule.Name,
    };

    private static string Describe(Check check)
    {
        var value = check.Value is { } v
            ? SensorFormat.Format(check.Type, v)
            : "no reading";
        var limit = SensorFormat.Format(check.Type, (float)check.Threshold);
        return $"{check.Label}: {value} (limit {limit})";
    }

    private static AlertEvent Event(AlertRule rule, string target, AlertEventKind kind, string title, string message,
        DateTime now, bool notify) =>
        new(rule.Id, target, kind, rule.Severity, title, message, now, notify);
}
