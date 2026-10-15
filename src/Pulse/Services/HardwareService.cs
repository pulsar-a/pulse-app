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
    /// </summary>
    internal static HardwareSnapshot TakeSnapshot(
        Computer computer,
        IReadOnlyDictionary<int, IReadOnlyList<VolumeInfo>> volumes,
        Func<IHardware, bool>? shouldUpdate = null)
    {
        foreach (var hardware in computer.Hardware)
        {
            if (shouldUpdate is null || shouldUpdate(hardware))
                Update(hardware);
        }

        return new HardwareSnapshot(
            computer.Hardware.Where(IsInteresting).Select(Capture).ToList(),
            volumes,
            DateTime.Now);
    }

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
                    volumes = QueryVolumes();
                    nextVolumeRefresh = now + VolumeRefresh;
                }

                // LHM looks for drives only once, in Open(). When another tool (ASUS Armoury Crate, for one)
                // is talking to the disk controller at that moment, SATA drives can be missed for the
                // whole session. If Windows reports more fixed drives than LHM found, look again a few times.
                if (now >= nextRescan && storageRescans < MaxStorageRescans && CountStorage(computer) < expectedDrives)
                {
                    storageRescans++;
                    nextRescan = now + RescanSpacing;
                    computer.IsStorageEnabled = false;
                    computer.IsStorageEnabled = true;
                    Log.Write($"Drive re-scan {storageRescans}: " + string.Join(", ",
                        computer.Hardware.Where(h => h.HardwareType == HardwareType.Storage).Select(h => h.Identifier)));
                }

                TimeSpan delay;
                HardwareSnapshot snapshot;
                lock (_scheduleGate)
                {
                    snapshot = TakeSnapshot(computer, volumes, hardware =>
                    {
                        var id = hardware.Identifier.ToString();
                        if (!_schedule.IsDue(id, hardware.HardwareType, now))
                            return false;
                        _schedule.MarkRead(id, now);
                        return true;
                    });
                    delay = _schedule.TickInterval;
                }
                _stream.Publish(snapshot);

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
            Log.Write($"Polling failed: {ex}");
            Failed?.Invoke(ex);
        }
        finally
        {
            computer.Close();
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
