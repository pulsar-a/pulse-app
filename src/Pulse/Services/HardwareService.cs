using System.Management;
using System.Security.Principal;
using LibreHardwareMonitor.Hardware;
using Pulse.Models;

namespace Pulse.Services;

/// <summary>
/// Owns the LibreHardwareMonitor <see cref="Computer"/> and polls it on a background thread.
/// All access to LHM objects happens inside the polling loop; the UI only sees immutable snapshots.
/// </summary>
public sealed class HardwareService : IDisposable
{
    private static readonly TimeSpan VolumeRefresh = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RescanSpacing = TimeSpan.FromSeconds(5);

    private readonly CancellationTokenSource _cts = new();
    private readonly SensorStream _stream;
    private readonly PollSchedule _schedule = new();
    private readonly object _scheduleGate = new();
    private Task? _loop;

    /// <param name="stream">Every snapshot is published here; consumers subscribe to the stream.</param>
    public HardwareService(SensorStream stream) => _stream = stream;

    /// <summary>Keeps last good values through dropped readings (a PSU shared with iCUE, for one).</summary>
    private readonly ReadingHold _hold = new();

    /// <summary>Raised on a background thread if opening or polling the hardware fails.</summary>
    public event Action<Exception>? Failed;

    /// <summary>The user's update interval. Takes effect on the next tick.</summary>
    public TimeSpan Interval
    {
        get { lock (_scheduleGate) return _schedule.Interval; }
        set { lock (_scheduleGate) _schedule.Interval = value; }
    }

    /// <summary>True while the window is minimized or hidden: polling slows down to save power.</summary>
    public bool IsIdle
    {
        get { lock (_scheduleGate) return _schedule.IsIdle; }
        set { lock (_scheduleGate) _schedule.IsIdle = value; }
    }

    public static bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    /// <summary>PawnIO is the kernel driver LHM uses for CPU, Super I/O and SMBus access.</summary>
    public static bool IsDriverInstalled
    {
        get
        {
            try { return LibreHardwareMonitor.PawnIo.PawnIo.IsInstalled; }
            catch { return false; }
        }
    }

    public void Start() => _loop ??= Task.Run(() => RunAsync(_cts.Token));

    /// <summary>An LHM computer with every hardware group Pulse reads turned on. Call Open() before use.</summary>
    internal static Computer CreateComputer() => new()
    {
        IsCpuEnabled = true,
        IsGpuEnabled = true,
        IsMemoryEnabled = true,
        IsMotherboardEnabled = true,
        IsControllerEnabled = true,
        IsStorageEnabled = true,
        IsNetworkEnabled = true,
        IsPsuEnabled = true,
        IsBatteryEnabled = true,
    };

    /// <summary>
    /// Updates the hardware (all of it, or only what <paramref name="shouldUpdate"/> says is due),
    /// then copies every sensor into an immutable snapshot. Hardware that wasn't updated keeps its
    /// previous readings.
    ///
    /// Each device is read on its own: if one throws (a USB device that just vanished), it's reported to
    /// <paramref name="failed"/> and shows no values in this snapshot, and every other device still reads.
    /// </summary>
    internal static HardwareSnapshot TakeSnapshot(
        Computer computer,
        IReadOnlyDictionary<int, IReadOnlyList<VolumeInfo>> volumes,
        Func<IHardware, bool>? shouldUpdate = null,
        Action<IHardware, Exception>? failed = null,
        Action<IHardware>? succeeded = null) =>
        TakeSnapshot(computer.Hardware.ToList(), volumes, shouldUpdate, failed, succeeded);

    internal static HardwareSnapshot TakeSnapshot(
        IReadOnlyList<IHardware> devices,
        IReadOnlyDictionary<int, IReadOnlyList<VolumeInfo>> volumes,
        Func<IHardware, bool>? shouldUpdate = null,
        Action<IHardware, Exception>? failed = null,
        Action<IHardware>? succeeded = null)
    {
        HashSet<IHardware>? broken = null;
        foreach (var hardware in devices)
        {
            if (shouldUpdate is not null && !shouldUpdate(hardware))
                continue;
            try
            {
                Update(hardware);
                succeeded?.Invoke(hardware);
            }
            catch (Exception ex)
            {
                (broken ??= []).Add(hardware);
                failed?.Invoke(hardware, ex);
            }
        }

        return new HardwareSnapshot(
            devices.Where(IsInteresting).Select(h => broken?.Contains(h) == true ? WithoutValues(Capture(h)) : Capture(h)).ToList(),
            volumes,
            DateTime.Now);
    }

    /// <summary>A device whose read failed: its sensors without values (held briefly by <see cref="ReadingHold"/>).</summary>
    private static HardwareReading WithoutValues(HardwareReading hardware) => hardware with
    {
        Sensors = hardware.Sensors.Select(s => s with { Value = null }).ToList(),
        SubHardware = hardware.SubHardware.Select(WithoutValues).ToList(),
    };

    private async Task RunAsync(CancellationToken ct)
    {
        var computer = CreateComputer();

        try
        {
            computer.Open();
            Log.Write("Hardware found: " + string.Join(", ", computer.Hardware.Select(h => $"{h.Name} ({h.Identifier})")));

            var expectedDrives = CountFixedDrives();
            Log.Write($"Windows reports {expectedDrives} internal fixed drive(s); LHM found {CountStorage(computer)}");
            var storageRescans = 0;
            var started = DateTime.UtcNow;
            var nextRescan = started + RescanSpacing;
            var nextVolumeRefresh = started;
            IReadOnlyDictionary<int, IReadOnlyList<VolumeInfo>> volumes = new Dictionary<int, IReadOnlyList<VolumeInfo>>();

            while (!ct.IsCancellationRequested)
            {
                var now = DateTime.UtcNow;

                // Drive letters rarely change, so a WMI query every half minute is plenty.
                if (now >= nextVolumeRefresh)
                {
                    nextVolumeRefresh = now + VolumeRefresh;
                    try
                    {
                        volumes = QueryVolumes();
                    }
                    catch (Exception ex)
                    {
                        // Keep the letters we had; a WMI hiccup mustn't stop the sensors.
                        Log.Write($"Couldn't list drive letters: {ex.Message}");
                    }
                }

                // LHM looks for drives only once, in Open(). When another tool (ASUS Armoury Crate, for one)
                // is talking to the disk controller at that moment, SATA drives can be missed for the
                // whole session. If Windows reports more fixed drives than LHM found, look again a few times.
                if (now >= nextRescan && storageRescans < MaxStorageRescans && CountStorage(computer) < expectedDrives)
                {
                    storageRescans++;
                    nextRescan = now + RescanSpacing;
                    Reopen(computer, HardwareType.Storage);
                }

                TimeSpan delay;
                HardwareSnapshot snapshot;
                var reopen = new HashSet<HardwareType>();
                // Decide what to read under the lock, then read without it. The UI thread takes this lock to
                // change the interval or idle mode; holding it through the reads (a USB PSU fighting with
                // iCUE can take seconds) froze the UI on every settings change.
                HashSet<IHardware> toRead;
                lock (_scheduleGate)
                {
                    toRead = computer.Hardware.Where(hardware =>
                    {
                        var id = hardware.Identifier.ToString();
                        if (!_schedule.IsDue(id, hardware.HardwareType, now) || !_health.CanRead(id, now))
                            return false;
                        _schedule.MarkRead(id, now);
                        return true;
                    }).ToHashSet();
                    delay = _schedule.TickInterval;
                }

                snapshot = TakeSnapshot(computer, volumes, toRead.Contains,
                    failed: (hardware, ex) =>
                    {
                        if (_health.Failed(hardware.Identifier.ToString(), hardware.Name, ex, now) == FailureAction.Reopen)
                            reopen.Add(hardware.HardwareType);
                    },
                    succeeded: hardware => _health.Succeeded(hardware.Identifier.ToString(), hardware.Name));
                _stream.Publish(_hold.Apply(snapshot));

                // Reopen what keeps failing, and keep trying for a group that came back empty (the device
                // was still gone when it was reopened) until it's there again.
                foreach (var (type, due) in _retryReopen.ToList())
                {
                    if (now >= due) reopen.Add(type);
                }
                foreach (var type in reopen)
                {
                    if (Reopen(computer, type))
                        _retryReopen.Remove(type);
                    else
                        _retryReopen[type] = now + DeviceHealth.ReopenSpacing;
                }

                // Keep a steady rhythm: subtract the time this tick took from the wait.
                var elapsed = DateTime.UtcNow - now;
                await Task.Delay(delay > elapsed ? delay - elapsed : TimeSpan.Zero, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // Only opening the hardware can end up here now; a device failing mid-run is handled per device.
            Log.Write($"Polling failed: {ex}");
            Failed?.Invoke(ex);
        }
        finally
        {
            computer.Close();
        }
    }

    private readonly DeviceHealth _health = new();
    private readonly Dictionary<HardwareType, DateTime> _retryReopen = new();

    /// <summary>
    /// Closes and reopens one group of hot-pluggable devices, for a device that keeps failing after it
    /// was reset. CPU, motherboard and memory are never reopened (they don't vanish, and reopening them
    /// goes through the driver); their failures are just retried.
    /// </summary>
    /// <returns>True when the group is open again with at least one device in it.</returns>
    private static bool Reopen(Computer computer, HardwareType type)
    {
        try
        {
            switch (type)
            {
                case HardwareType.Psu:
                    computer.IsPsuEnabled = false;
                    computer.IsPsuEnabled = true;
                    break;
                case HardwareType.Cooler:
                    computer.IsControllerEnabled = false;
                    computer.IsControllerEnabled = true;
                    break;
                case HardwareType.Storage:
                    computer.IsStorageEnabled = false;
                    computer.IsStorageEnabled = true;
                    break;
                case HardwareType.Network:
                    computer.IsNetworkEnabled = false;
                    computer.IsNetworkEnabled = true;
                    break;
                case HardwareType.Battery:
                    computer.IsBatteryEnabled = false;
                    computer.IsBatteryEnabled = true;
                    break;
                default:
                    return true;
            }
            var found = computer.Hardware.Where(h => h.HardwareType == type).Select(h => h.Name).ToList();
            Log.Write(found.Count > 0 ? $"Reopened {type}: {string.Join(", ", found)}" : $"Reopened {type}: nothing found yet, will try again");
            return found.Count > 0;
        }
        catch (Exception ex)
        {
            Log.Write($"Couldn't reopen {type}: {ex.Message}. Will try again.");
            return false;
        }
    }

    private const int MaxStorageRescans = 3;

    private static int CountStorage(Computer computer) =>
        computer.Hardware.Count(h => h.HardwareType == HardwareType.Storage);

    /// <summary>Internal (non-USB) fixed disks according to Windows, or 0 if WMI can't be queried.</summary>
    private static int CountFixedDrives()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT InterfaceType FROM Win32_DiskDrive WHERE MediaType = 'Fixed hard disk media'");
            return searcher.Get().Cast<ManagementBaseObject>()
                .Count(d => !string.Equals(d["InterfaceType"] as string, "USB", StringComparison.OrdinalIgnoreCase));
        }
        catch (ManagementException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Lettered volumes per Windows disk number. LHM names storage hardware after that same
    /// number (/nvme/2, /ssd/0), which is how the dashboard matches them up.
    /// </summary>
    internal static IReadOnlyDictionary<int, IReadOnlyList<VolumeInfo>> QueryVolumes()
    {
        try
        {
            var labels = new Dictionary<char, string>();
            using (var disks = new ManagementObjectSearcher("SELECT DeviceID, VolumeName FROM Win32_LogicalDisk"))
            {
                foreach (var disk in disks.Get())
                {
                    if (disk["DeviceID"] is string { Length: > 0 } id)
                        labels[char.ToUpperInvariant(id[0])] = disk["VolumeName"] as string ?? "";
                }
            }

            var result = new Dictionary<int, List<VolumeInfo>>();
            using var partitions = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage", "SELECT DiskNumber, DriveLetter FROM MSFT_Partition");
            foreach (var partition in partitions.Get())
            {
                var letter = Convert.ToChar(partition["DriveLetter"] ?? '\0');
                if (!char.IsLetter(letter))
                    continue;

                var diskNumber = Convert.ToInt32(partition["DiskNumber"]);
                letter = char.ToUpperInvariant(letter);
                if (!result.TryGetValue(diskNumber, out var list))
                    result[diskNumber] = list = [];
                list.Add(new VolumeInfo(letter, labels.GetValueOrDefault(letter, "")));
            }

            return result.ToDictionary(
                kv => kv.Key,
                kv => (IReadOnlyList<VolumeInfo>)kv.Value.OrderBy(v => v.Letter).ToList());
        }
        catch (Exception ex) when (ex is ManagementException or InvalidCastException or FormatException)
        {
            return new Dictionary<int, IReadOnlyList<VolumeInfo>>();
        }
    }

    private static void Update(IHardware hardware)
    {
        hardware.Update();
        foreach (var sub in hardware.SubHardware)
            Update(sub);
    }

    private static HardwareReading Capture(IHardware hardware) => new(
        hardware.Identifier.ToString(),
        hardware.Name,
        hardware.HardwareType,
        hardware.Sensors
            .Select(s => new SensorReading(
                s.Identifier.ToString(), s.Name, s.SensorType, s.Index, s.Value, s.Min, s.Max))
            .ToList(),
        hardware.SubHardware.Select(Capture).ToList());

    /// <summary>
    /// Windows exposes every network filter driver as its own adapter (QoS, WFP, VirtualBox...).
    /// They duplicate the real adapter's numbers, so skip them.
    /// </summary>
    private static bool IsInteresting(IHardware hardware)
    {
        if (hardware.HardwareType != HardwareType.Network)
            return true;

        string[] noise = ["Filter", "Scheduler", "Kernel Debugger", "Loopback", "Pseudo"];
        return !noise.Any(n => hardware.Name.Contains(n, StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(3)); }
        catch (AggregateException) { }
        _cts.Dispose();
    }
}
