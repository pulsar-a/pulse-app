using System.Diagnostics;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibreHardwareMonitor.Hardware;
using Pulse.Models;
using Pulse.Services;

namespace Pulse.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private const string PawnIoUrl = "https://pawnio.eu";

    private readonly Dispatcher _dispatcher;
    private HardwareSnapshot? _pending;
    private int _applyScheduled;

    private readonly SettingsService _settings;
    private readonly SensorCatalog _catalog;
    private readonly HardwareService _hardware;
    private readonly SensorStream _stream;

    public MainViewModel(HardwareService hardware, SensorStream stream, SettingsService settings, Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _settings = settings;
        _hardware = hardware;
        _stream = stream;
        var driverInstalled = HardwareService.IsDriverInstalled;

        _catalog = new SensorCatalog(settings);
        Dashboard = new DashboardViewModel(driverInstalled, _catalog, stream);
        Sensors = new SensorsViewModel(_catalog, id => settings.Update(s => s.Startup.TraySensorId = id));
        Settings = new SettingsViewModel(settings, _catalog, driverInstalled, stream);

        ApplySettings();
        settings.Changed += ApplySettings;

        ShowElevationBanner = !HardwareService.IsElevated;
        ShowDriverBanner = !driverInstalled;

        stream.SnapshotPublished += OnSnapshotPublished;
        hardware.Failed += ex => _dispatcher.BeginInvoke(() =>
        {
            ErrorText = $"Couldn't read sensors: {ex.Message}";
            IsLoading = false;
        });
    }

    /// <summary>
    /// False while the window is minimized or hidden. Pulse then stops updating the UI entirely and polls
    /// more slowly; the sensor stream keeps recording, so history has no holes when the window comes back.
    /// </summary>
    public bool IsWindowActive
    {
        get => _isWindowActive;
        set
        {
            if (_isWindowActive == value)
                return;
            _isWindowActive = value;
            _hardware.IsIdle = !value;
            Log.Write(value ? "Window shown: live updates resumed" : "Window hidden: idle mode");

            // Catch up straight away with the newest reading instead of waiting for the next tick.
            if (value && _stream.Latest is { } latest)
                OnSnapshotPublished(latest);
        }
    }

    private volatile bool _isWindowActive = true;

    public DashboardViewModel Dashboard { get; }
    public SensorsViewModel Sensors { get; }
    public SensorCatalog Catalog => _catalog;
    public SettingsViewModel Settings { get; }

    [ObservableProperty] public partial bool IsDashboardSelected { get; set; } = true;
    [ObservableProperty] public partial bool IsSensorsSelected { get; set; }
    [ObservableProperty] public partial bool IsSettingsSelected { get; set; }

    partial void OnIsSensorsSelectedChanged(bool value) => Sensors.IsActive = value;

    partial void OnIsDashboardSelectedChanged(bool value)
    {
        if (value && _stream.Latest is { } latest)
            Dashboard.Apply(latest);
    }

    partial void OnIsSettingsSelectedChanged(bool value)
    {
        if (value)
            Settings.Refresh();
    }

    /// <summary>
    /// Pushes settings to where they take effect. Units are read globally by the formatter, and the interval
    /// by the polling loop; both apply from the next reading.
    /// </summary>
    private void ApplySettings()
    {
        SensorFormat.TemperatureUnit = _settings.Current.Units.Temperature;
        var interval = _settings.Current.General.UpdateInterval;
        _hardware.Interval = interval;
        Dashboard.SampleInterval = interval;
    }

    [ObservableProperty] public partial string MachineName { get; set; } = "Pulse";
    [ObservableProperty] public partial string StatusText { get; set; } = "Detecting hardware…";
    [ObservableProperty] public partial bool IsLoading { get; set; } = true;
    [ObservableProperty] public partial string? ErrorText { get; set; }

    [ObservableProperty] public partial bool ShowElevationBanner { get; set; }
    [ObservableProperty] public partial bool ShowDriverBanner { get; set; }

    [RelayCommand]
    private void DismissDriverBanner() => ShowDriverBanner = false;

    [RelayCommand]
    private void DismissElevationBanner() => ShowElevationBanner = false;

    /// <summary>
    /// Opens the PawnIO site through Explorer so the browser starts unelevated,
    /// even though Pulse itself runs as administrator.
    /// </summary>
    [RelayCommand]
    private static void OpenPawnIo() => Process.Start("explorer.exe", PawnIoUrl);

    /// <summary>
    /// Called on the polling thread. Only the newest snapshot is applied; if the UI falls behind,
    /// older ones are dropped instead of queuing up.
    /// </summary>
    private void OnSnapshotPublished(HardwareSnapshot snapshot)
    {
        // Nobody is looking: skip the UI work. The newest snapshot is applied when the window returns.
        if (!_isWindowActive)
            return;

        Volatile.Write(ref _pending, snapshot);
        if (Interlocked.Exchange(ref _applyScheduled, 1) == 0)
            _dispatcher.BeginInvoke(ApplyPending, DispatcherPriority.Background);
    }

    private void ApplyPending()
    {
        Interlocked.Exchange(ref _applyScheduled, 0);
        if (Interlocked.Exchange(ref _pending, null) is not { } snapshot)
            return;

        var sw = Stopwatch.StartNew();
        var board = snapshot.OfType(HardwareType.Motherboard).FirstOrDefault();
        MachineName = board?.Name ?? Environment.MachineName;
        StatusText = $"Live · updated {snapshot.Timestamp:HH:mm:ss}";

        _catalog.Update(snapshot);
        // Only the page on screen does UI work; the others catch up when opened (history comes from the stream).
        if (IsDashboardSelected || Dashboard.IsLoading)
            Dashboard.Apply(snapshot);
        Sensors.Apply(snapshot);
        IsLoading = false;

        if (sw.ElapsedMilliseconds > 30)
            Log.Write($"Applying a snapshot took {sw.ElapsedMilliseconds} ms on the UI thread");
        _applyStats.Add(sw.Elapsed.TotalMilliseconds);
    }

    private readonly ApplyStats _applyStats = new();

    /// <summary>Logs a once-a-minute summary of how long snapshots take to apply on the UI thread.</summary>
    private sealed class ApplyStats
    {
        private DateTime _windowStart = DateTime.UtcNow;
        private double _total;
        private double _max;
        private int _count;

        public void Add(double ms)
        {
            _total += ms;
            _max = Math.Max(_max, ms);
            _count++;

            if (DateTime.UtcNow - _windowStart < TimeSpan.FromMinutes(1))
                return;
            Log.Write($"UI: applied {_count} snapshots in the last minute, average {_total / _count:0.0} ms, slowest {_max:0.0} ms");
            _windowStart = DateTime.UtcNow;
            _total = _max = 0;
            _count = 0;
        }
    }
}
