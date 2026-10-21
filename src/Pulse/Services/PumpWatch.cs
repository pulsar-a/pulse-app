using System.IO;
using LibreHardwareMonitor.Hardware;
using Microsoft.Data.Sqlite;
using Pulse.Models;
using Pulse.Services.History;

namespace Pulse.Services;

/// <summary>
/// The pump reads unlike itself since Pulse started: most likely the cooling changed while the PC was off
/// (a new cooler, the pump moved to another header) rather than the pump failing.
/// </summary>
/// <param name="SensorId">The pump role's sensor.</param>
/// <param name="Name">Its display name, "Fan #2".</param>
/// <param name="Rpm">What it has read since Pulse started; null when it isn't there at all.</param>
/// <param name="Low">The slowest it ran before.</param>
/// <param name="High">The fastest it ran before.</param>
/// <param name="SuggestedId">A header or cooler channel that started spinning since: probably the pump now.</param>
public sealed record PumpChange(string SensorId, string Name, float? Rpm, float Low, float High,
    string? SuggestedId = null, string? SuggestedName = null, float? SuggestedRpm = null)
{
    /// <summary>Something still spins on the old pump sensor (fans now, or a different pump).</summary>
    public bool IsSpinning => Rpm >= PumpWatch.SpinningRpm;

    /// <summary>"Fan #2 has read about 500 rpm since Pulse started. Before, it ran at 2,510–2,590 rpm."</summary>
    public string Description => (Rpm is { } rpm
        ? $"{Name} has read about {Speed(rpm)} since Pulse started."
        : $"{Name} hasn't reported since Pulse started.") + $" Before, it ran at {Range}.";

    /// <summary>"Fan #6 started spinning at 2,100 rpm." Null without a suggestion.</summary>
    public string? Suggestion => SuggestedName is null ? null : $"{SuggestedName} started spinning at {Speed(SuggestedRpm ?? 0)}.";

    /// <summary>The extra sentences an alert about the pump carries while this stands.</summary>
    public string AlertNote => Suggestion is { } suggestion
        ? $"{Description} {suggestion} If you moved the pump there, choose it in the Cooling card."
        : $"{Description} If you changed or re-plugged your cooler, choose the pump header in the Cooling card.";

    private string Range => Round(Low) == Round(High) ? $"about {Speed(Low)}" : $"{Round(Low):N0}–{Speed(High)}";

    private static string Speed(float rpm) => $"{Round(rpm):N0} rpm";

    /// <summary>To the nearest 10 rpm: the last digit is noise and makes the sentence harder to read.</summary>
    private static float Round(float rpm) => MathF.Round(rpm / 10) * 10;
}

/// <summary>What the alerts need from the pump watch: whether the startup check is done, and what it found.</summary>
public sealed record PumpStatus(bool Settled, PumpChange? Change);

/// <summary>
/// Learns what the pump normally reads and which fans spin alongside it, and checks it again each time Pulse
/// starts. A pump that stops while Pulse watches is a failure; one that already reads very differently when Pulse
/// starts (or is gone, or reads 0 while a header that never spun now does) is far more often a hardware change:
/// a new cooler whose fans now sit on the old pump header, a pump moved to the pump connector, a USB cooler kept
/// only for its fans. Then <see cref="Change"/> says so, and the alert and the Cooling card ask instead of alarming.
///
/// Pure logic with the clock passed in, like <see cref="AlertEngine"/>. Lives on the UI thread.
/// </summary>
public sealed class PumpWatch
{
    /// <summary>Below this a pump or fan counts as stopped.</summary>
    public const float SpinningRpm = 200;

    /// <summary>How long Pulse watches the pump after starting before deciding whether it looks like before.</summary>
    public static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(10);

    // Pumps on a curve stay inside the range they've been seen in; this far outside it isn't the same pump.
    private const float Slower = 0.6f;
    private const float Faster = 1.6f;

    /// <summary>Learning and recovering use the median of this many readings, so a spike or dip doesn't count.</summary>
    private const int Window = 5;

    private static readonly string[] PumpWords = ["pump", "aio"];

    private readonly SensorCatalog _catalog;
    private readonly List<float> _startup = [];
    private readonly Queue<float> _recent = new();
    private string? _watching;
    private bool _started;
    private DateTime _since;
    private bool _settled = true;
    private Task<PumpProfile?>? _fromHistory;

    public PumpWatch(SensorCatalog catalog) => _catalog = catalog;

    /// <summary>
    /// Reads a profile from history (pump id, alert limit, before when): for a pump Pulse watched before it
    /// learned profiles. Set by the app once history is open; without it, a pump with no profile is learned from now.
    /// </summary>
    public Func<string, double, DateTime, Task<PumpProfile?>>? History { get; set; }

    /// <summary>Set when the startup check found the pump reading unlike itself; cleared once resolved.</summary>
    public PumpChange? Change { get; private set; }

    public PumpStatus Status => new(_settled, Change);

    /// <summary>Raised when <see cref="Change"/> appears or goes away.</summary>
    public event Action? ChangeUpdated;

    private SettingsService Settings => _catalog.Settings;
    private PumpProfile? Profile => Settings.Current.Sensors.Pump;

    public void Observe(HardwareSnapshot snapshot, IReadOnlyList<AlertRule> rules, DateTime now)
    {
        var id = _catalog.RoleSensor(SensorRoles.Pump);
        var limit = rules.FirstOrDefault(r => r.Enabled && r.Target == AlertTarget.Pump)?.Threshold ?? 0;
        if (!_started || id != _watching)
            Watch(id, limit, now, atStartup: !_started);
        _started = true;
        if (id is not { Length: > 0 })
            return;

        var pump = snapshot.Hardware.SelectMany(h => h.AllSensors).FirstOrDefault(s => s.Id == id);
        // Not read on purpose (a cooler left to iCUE): that says nothing about the pump. The startup check waits.
        if (pump is null && snapshot.IsUnread(id))
        {
            if (!_settled)
                _since = now;
            return;
        }
        var rpm = pump is { HasValue: true } ? pump.Value : null;

        if (!_settled)
        {
            if (rpm is { } r)
                _startup.Add(r);
            // Missing so far may only mean not found yet (USB coolers turn up a few seconds in): give it longer.
            var waited = now - _since;
            if (waited < SettleTime
                || ((_startup.Count == 0 || _fromHistory is { IsCompleted: false }) && waited < 3 * SettleTime))
                return;
            _settled = true;
            Decide(snapshot, id, pump);
            return;
        }

        if (rpm is not { } value)
        {
            _recent.Clear();
            return;
        }
        _recent.Enqueue(value);
        while (_recent.Count > Window)
            _recent.Dequeue();
        if (_recent.Count < Window)
            return;
        var typical = Median(_recent);

        if (Change is not null)
        {
            // Back to its usual speed (a header that woke up late, say): nothing changed after all.
            if (Profile is { } known && known.SensorId == id && IsNormal(typical, known))
            {
                Log.Write($"Pump: {id} reads {typical:N0} rpm again, as before");
                SetChange(null);
            }
            return;
        }
        if (typical >= Math.Max(limit, SpinningRpm))
            Learn(id, typical, snapshot);
    }

    /// <summary>"This is the pump": it reads differently because it is a different pump. Learn it again from now.</summary>
    public void Confirm()
    {
        if (Change is null || _watching is not { Length: > 0 } id)
            return;
        Log.Write($"Pump: {id} confirmed as the pump; learning its speed again");
        Settings.Update(s => s.Sensors.Pump = new PumpProfile { SensorId = id });
        _recent.Clear();
        SetChange(null);
    }

    /// <summary>Makes <paramref name="sensorId"/> the pump (the suggested header, say), learned from now.</summary>
    public void Use(string sensorId)
    {
        _catalog.SetRole(SensorRoles.Pump, sensorId);
        SetChange(null);
    }

    private void Watch(string? id, double limit, DateTime now, bool atStartup)
    {
        _watching = id;
        _since = now;
        _startup.Clear();
        _recent.Clear();
        _fromHistory = null;
        _settled = true;
        SetChange(null);
        if (id is not { Length: > 0 })
            return;

        if (Profile is { } known && known.SensorId == id)
        {
            // The same pump as last time: see whether it still reads like it before alerting about it.
            _settled = !atStartup || known.High <= 0;
        }
        else if (Profile is null && atStartup && History is { } history)
        {
            _fromHistory = history(id, limit, now);
            _settled = false;
        }
        else
        {
            // A newly picked pump: whatever it reads now is how it reads.
            Settings.Update(s => s.Sensors.Pump = new PumpProfile { SensorId = id });
        }
    }

    private void Decide(HardwareSnapshot snapshot, string id, SensorReading? pump)
    {
        if (_fromHistory is not null)
        {
            var learned = _fromHistory.IsCompletedSuccessfully ? _fromHistory.Result : null;
            _fromHistory = null;
            Settings.Update(s => s.Sensors.Pump = learned ?? new PumpProfile { SensorId = id });
            if (learned is not null)
                Log.Write($"Pump: learned {learned.Low:N0}–{learned.High:N0} rpm for {id} from history");
        }
        if (Profile is not { High: > 0 } known || known.SensorId != id)
            return;

        float? rpm = _startup.Count > 0 ? Median(_startup) : null;
        var suggestion = Suggest(snapshot, id, known);
        var changed = rpm switch
        {
            // Gone: a different cooler, or a different board.
            null => true,
            // Stopped, and something new spins: moved, most likely. Stopped with nothing new is a stopped pump.
            < SpinningRpm => suggestion is not null,
            { } r => !IsNormal(r, known),
        };
        if (!changed)
            return;

        var change = new PumpChange(id, _catalog.DisplayName(id, pump?.Name ?? "The pump"), rpm, known.Low, known.High,
            suggestion?.Id, suggestion is null ? null : _catalog.DisplayName(suggestion), suggestion?.Value);
        Log.Write($"Pump: cooling looks different. {change.Description}{(change.Suggestion is { } s ? " " + s : "")}");
        SetChange(change);
    }

    private void Learn(string id, float typical, HardwareSnapshot snapshot)
    {
        if (Profile is not { } known || known.SensorId != id)
            return;
        var low = known.High <= 0 ? typical : Math.Min(known.Low, typical);
        var high = Math.Max(known.High, typical);
        var fans = Spinning(snapshot, id).Select(s => s.Id).Where(f => !known.Fans.Contains(f)).ToList();
        // Save only real news, not every rpm of jitter: a range wider by more than 1%, or a fan not seen before.
        if (known.High > 0 && fans.Count == 0 && low >= known.Low * 0.99f && high <= known.High * 1.01f)
            return;
        Settings.Update(s =>
        {
            known.Low = low;
            known.High = high;
            known.Fans.AddRange(fans);
        });
    }

    /// <summary>The likeliest new home of the pump: a header or cooler channel spinning now that never did alongside it.</summary>
    private SensorReading? Suggest(HardwareSnapshot snapshot, string id, PumpProfile known) =>
        Spinning(snapshot, id)
            .Where(s => !known.Fans.Contains(s.Id))
            // Named like a pump, by the board or by the user, first.
            .OrderByDescending(s => PumpWords.Any(w => _catalog.DisplayName(s).Contains(w, StringComparison.OrdinalIgnoreCase)))
            // Pumps run faster than most fans.
            .ThenByDescending(s => s.Value)
            .FirstOrDefault();

    /// <summary>
    /// Spinning fan sensors where a pump could be: motherboard headers and USB coolers (not graphics cards or
    /// power supplies, whose fans can't be the CPU pump).
    /// </summary>
    private static IEnumerable<SensorReading> Spinning(HardwareSnapshot snapshot, string pumpId) =>
        snapshot.Hardware.SelectMany(WithOwners)
            .Where(x => x.Owner.Type is HardwareType.SuperIO or HardwareType.EmbeddedController or HardwareType.Cooler)
            .Select(x => x.Sensor)
            .Where(s => s.Type == SensorType.Fan && s.Id != pumpId && s.Value >= SpinningRpm);

    private static IEnumerable<(HardwareReading Owner, SensorReading Sensor)> WithOwners(HardwareReading hardware) =>
        hardware.Sensors.Select(s => (hardware, s)).Concat(hardware.SubHardware.SelectMany(WithOwners));

    private static bool IsNormal(float rpm, PumpProfile known) => rpm >= known.Low * Slower && rpm <= known.High * Faster;

    private static float Median(IEnumerable<float> values)
    {
        var sorted = values.Order().ToList();
        return sorted[sorted.Count / 2];
    }

    private void SetChange(PumpChange? change)
    {
        if (change == Change)
            return;
        Change = change;
        ChangeUpdated?.Invoke();
    }

    /// <summary>
    /// A profile from the history database, for a pump Pulse watched before it learned profiles: the middle 80% of
    /// the minutes it ran healthy, and the fans that spun the last time it read like that. Null without enough history.
    /// </summary>
    public static PumpProfile? FromHistory(string databasePath, string pumpId, double limit, DateTime before, int days)
    {
        try
        {
            var healthy = HistoryDatabase.ReadMinutes(databasePath, pumpId, before.AddDays(-days), before)
                .Where(m => m.Avg >= Math.Max(limit, SpinningRpm))
                .ToList();
            // Half an hour of it at least, or it isn't "normal" yet.
            if (healthy.Count < 30)
                return null;
            var speeds = healthy.Select(m => (float)m.Avg).Order().ToList();
            var low = speeds[speeds.Count / 10];
            var high = speeds[speeds.Count * 9 / 10];
            var last = healthy.Last(m => m.Avg >= low && m.Avg <= high).Time;

            var fans = HistoryDatabase.ReadSensors(databasePath)
                .Where(s => s.Type == SensorType.Fan && s.Id != pumpId
                            && !s.Id.StartsWith("/gpu", StringComparison.Ordinal) && !s.Id.StartsWith("/psu", StringComparison.Ordinal))
                .Where(s => HistoryDatabase.ReadMinutes(databasePath, s.Id, last.AddMinutes(-1), last) is { Count: > 0 } m
                            && m[^1].Avg >= SpinningRpm)
                .Select(s => s.Id)
                .ToList();
            return new PumpProfile { SensorId = pumpId, Low = low, High = high, Fans = fans };
        }
        catch (Exception ex) when (ex is SqliteException or IOException or InvalidOperationException)
        {
            Log.Write($"Pump: couldn't read its history ({ex.Message}); learning it from now");
            return null;
        }
    }
}
