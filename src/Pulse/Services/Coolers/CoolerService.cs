using System.IO;
using Pulse.Models;
using Pulse.Services.Processes;

namespace Pulse.Services.Coolers;

/// <summary>Settings → USB coolers: whether Pulse reads coolers itself, and how it shares them.</summary>
public enum CoolerAccess
{
    /// <summary>Read, except while another app that talks to coolers is running (the default).</summary>
    Automatic,

    /// <summary>Read even with other cooler software running. They may briefly lose control of the cooler.</summary>
    Always,

    /// <summary>Never open USB coolers.</summary>
    Off,
}

public sealed class CoolerSettings
{
    public CoolerAccess Access { get; set; } = CoolerAccess.Automatic;
}

public enum CoolerState
{
    Reading,

    /// <summary>Left alone because another app is using it (or just stopped using it).</summary>
    Paused,

    /// <summary>Turned off in settings.</summary>
    Off,

    /// <summary>Reads keep failing; Pulse tries again after a pause.</summary>
    Failing,
}

/// <summary>What Pulse is doing with one cooler, for Settings and the cooling card.</summary>
public sealed record CoolerStatus(string Key, string Name, CoolerState State, string Detail);

/// <summary>When to touch a cooler, given the setting and what's running. Pure, so it's easy to test.</summary>
public static class CoolerPolicy
{
    /// <summary>After the other app quits, wait this long: iCUE restarts its service, updaters come and go.</summary>
    public static readonly TimeSpan ResumeDelay = TimeSpan.FromSeconds(10);

    public static CoolerStatus? Hold(KnownCooler cooler, string key, CoolerAccess access, IReadOnlyList<string> conflicts,
        DateTime? lastConflict, DateTime now)
    {
        if (access == CoolerAccess.Off)
            return new CoolerStatus(key, cooler.Name, CoolerState.Off, "Not read: turned off in Settings");
        if (access == CoolerAccess.Always)
            return null;
        if (conflicts.Count > 0)
            return new CoolerStatus(key, cooler.Name, CoolerState.Paused,
                $"Left alone while {Join(conflicts)} {(conflicts.Count == 1 ? "is" : "are")} running, so they don't fight over it");
        if (lastConflict is { } then && now - then < ResumeDelay)
            return new CoolerStatus(key, cooler.Name, CoolerState.Paused, "Starting to read in a moment");
        return null;
    }

    private static string Join(IReadOnlyList<string> names) =>
        names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];

    /// <summary>How long to wait before trying a failing cooler again: 5 s, growing to a minute.</summary>
    public static TimeSpan Backoff(int failures) => TimeSpan.FromSeconds(Math.Min(60, 5 * Math.Max(1, failures)));
}

/// <summary>
/// Reads the USB coolers Pulse supports itself (see <see cref="KnownCoolers"/>) every 2 s on a background
/// thread, and hands their readings to the sensor polling, which adds them to each snapshot as Cooler
/// hardware. The cooling card, alerts, history and the rest then treat them like any other sensor.
///
/// Sharing: coolers have one controller and many apps that want it. By default Pulse stays off a cooler
/// while its vendor's software (iCUE, with its background service) or another cooler tool is running, and
/// says so; it starts reading a little after they quit.
/// </summary>
public sealed class CoolerService : IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(15);

    private readonly SettingsService _settings;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, Device> _devices = new();
    private Task? _loop;

    private sealed class Device(HidCoolerLink.Found found)
    {
        public HidCoolerLink.Found Found { get; set; } = found;
        public ICoolerDriver? Driver;
        public DateTime? LastConflict;
        public int Failures;
        public DateTime RetryAt;

        public void Close()
        {
            Driver?.Dispose();
            Driver = null;
        }
    }

    public CoolerService(SettingsService settings) => _settings = settings;

    /// <summary>The latest reading of every cooler being read. Replaced, never changed, so any thread can read it.</summary>
    public IReadOnlyList<HardwareReading> Readings { get; private set; } = [];

    public IReadOnlyList<CoolerStatus> Statuses { get; private set; } = [];

    /// <summary>The hardware ids of coolers left alone on purpose (paused or turned off): see <see cref="HardwareSnapshot.Unread"/>.</summary>
    public IReadOnlyCollection<string> Unread { get; private set; } = [];

    /// <summary>Raised on the background thread when a cooler appears, goes, or changes state.</summary>
    public event Action? StatusChanged;

    public void Start() => _loop ??= Task.Run(() => RunAsync(_cts.Token));

    private async Task RunAsync(CancellationToken ct)
    {
        var nextScan = DateTime.MinValue;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var now = DateTime.UtcNow;
                if (now >= nextScan)
                {
                    nextScan = now + ScanInterval;
                    Rescan();
                }
                Tick(now);
                await Task.Delay(Interval, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            foreach (var device in _devices.Values)
                device.Close();
        }
    }

    private void Rescan()
    {
        List<HidCoolerLink.Found> found;
        try
        {
            found = HidCoolerLink.Scan();
        }
        catch (Exception ex)
        {
            Log.Write($"Couldn't list USB coolers: {ex.Message}");
            return;
        }

        foreach (var f in found)
        {
            if (_devices.TryGetValue(f.Key, out var known))
                known.Found = f;
            else
            {
                _devices[f.Key] = new Device(f);
                Log.Write($"USB cooler found: {f.Cooler.Name} ({f.Cooler.VendorId:X4}:{f.Cooler.ProductId:X4})");
            }
        }
        foreach (var gone in _devices.Keys.Where(k => found.All(f => f.Key != k)).ToList())
        {
            Log.Write($"USB cooler gone: {_devices[gone].Found.Cooler.Name}");
            _devices[gone].Close();
            _devices.Remove(gone);
        }
    }

    private void Tick(DateTime now)
    {
        var access = _settings.Current.Coolers.Access;
        // Only look at what's running when it matters: some cooler is plugged in and the setting asks.
        var processes = _devices.Count > 0 && access == CoolerAccess.Automatic
            ? NativeProcesses.Snapshot().Select(p => p.Name).Distinct().ToList()
            : [];

        var readings = new List<HardwareReading>();
        var statuses = new List<CoolerStatus>();
        var unread = new List<string>();
        foreach (var (key, device) in _devices)
        {
            var cooler = device.Found.Cooler;
            var conflicts = KnownCoolers.ConflictsAmong(cooler, processes);
            if (conflicts.Count > 0)
                device.LastConflict = now;

            if (CoolerPolicy.Hold(cooler, key, access, conflicts, device.LastConflict, now) is { } held)
            {
                if (device.Driver is not null)
                    Log.Write($"{cooler.Name}: {held.Detail}");
                device.Close();
                statuses.Add(held);
                unread.Add(cooler.IdFor(device.Found.Serial));
                continue;
            }
            if (now < device.RetryAt)
            {
                statuses.Add(new CoolerStatus(key, cooler.Name, CoolerState.Failing, "Couldn't read it; trying again shortly"));
                continue;
            }
            statuses.Add(Read(key, device, now, readings));
        }

        Readings = readings;
        Unread = unread;
        if (!statuses.SequenceEqual(Statuses))
        {
            Statuses = statuses;
            StatusChanged?.Invoke();
        }
    }

    private static CoolerStatus Read(string key, Device device, DateTime now, List<HardwareReading> readings)
    {
        var cooler = device.Found.Cooler;
        try
        {
            if (device.Driver is null)
            {
                var link = HidCoolerLink.TryOpen(device.Found.Device)
                           ?? throw new IOException("Windows wouldn't open it");
                device.Driver = cooler.Create(link, device.Found.Serial);
                Log.Write($"{cooler.Name}: reading");
            }
            readings.Add(device.Driver.Read());
            device.Failures = 0;
            var firmware = (device.Driver as CorsairCommander)?.FirmwareVersion;
            return new CoolerStatus(key, cooler.Name, CoolerState.Reading,
                firmware is null ? "Reading every 2 seconds" : $"Reading every 2 seconds · firmware {firmware}");
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or CoolerProtocolException or UnauthorizedAccessException
                                       or ObjectDisposedException)
        {
            device.Failures++;
            device.RetryAt = now + CoolerPolicy.Backoff(device.Failures);
            device.Close();
            if (device.Failures is 1 or 3 || device.Failures % 20 == 0)
                Log.Write($"{cooler.Name}: read failed ({device.Failures} in a row): {ex.Message}");
            return new CoolerStatus(key, cooler.Name, CoolerState.Failing, $"Couldn't read it: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(3)); }
        catch (AggregateException) { }
        _cts.Dispose();
    }
}
