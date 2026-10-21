using System.Diagnostics;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibreHardwareMonitor.Hardware;
using Pulse.Models;
using Pulse.Services;
using Pulse.Services.Processes;

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
        Dashboard = new DashboardViewModel(driverInstalled, _catalog, stream) { OpenDetails = OpenDetails };
        Sensors = new SensorsViewModel(_catalog, id => settings.Update(s => s.Startup.TraySensorId = id), CreateAlert, OpenDetails)
        {
            Pin = id => Dashboard.Pin(id),
        };
        Settings = new SettingsViewModel(settings, _catalog, driverInstalled, stream);
        Search = new SensorSearchViewModel(_catalog, stream, OpenDetails);
        Dashboard.RequestPinSearch = () => Search.Open(id => Dashboard.Pin(id), "Pin a sensor to the dashboard");
        Dashboard.OpenProcesses = () => IsProcessesSelected = true;
        Dashboard.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DashboardViewModel.ShowsProcesses)) UpdateProcessSampling();
        };

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
            UpdateIdle();
            Log.Write(value ? "Window shown: live updates resumed" : "Window hidden: idle mode");
            UpdateProcessSampling();

            // Catch up straight away with the newest reading instead of waiting for the next tick.
            if (value && _stream.Latest is { } latest)
                OnSnapshotPublished(latest);
        }
    }

    private volatile bool _isWindowActive = true;
    private volatile bool _isMiniActive;

    /// <summary>The mini window is showing: keep reading at full speed even with the main window hidden.</summary>
    public bool IsMiniActive
    {
        get => _isMiniActive;
        set
        {
            if (_isMiniActive == value) return;
            _isMiniActive = value;
            UpdateIdle();
        }
    }

    /// <summary>A phone or browser is watching the web view: keep reading at full speed even with the window hidden.</summary>
    public bool IsRemoteActive
    {
        get => _isRemoteActive;
        set
        {
            if (_isRemoteActive == value) return;
            _isRemoteActive = value;
            UpdateIdle();
        }
    }

    private volatile bool _isRemoteActive;

    private void UpdateIdle() => _hardware.IsIdle = !_isWindowActive && !_isMiniActive && !_isRemoteActive;

    // ---- Phone and web view (step 12) -----------------------------------------------------------

    private DashboardViewModel? _webDashboard;

    /// <summary>The history behind the details panel, also read by the web view's charts.</summary>
    public Services.History.HistoryStore? HistoryStore { get; private set; }

    /// <summary>The web view's own dashboard: never shown, it turns snapshots into the same cards the window has.</summary>
    public DashboardViewModel CreateWebDashboard() =>
        _webDashboard = new DashboardViewModel(HardwareService.IsDriverInstalled, _catalog, _stream)
        {
            CoolerStatuses = Dashboard.CoolerStatuses,
            PumpWatch = Dashboard.PumpWatch,
        };

    /// <summary>The name in the header: the motherboard, or the PC's name.</summary>
    public static string MachineNameOf(HardwareSnapshot snapshot) =>
        snapshot.OfType(HardwareType.Motherboard).FirstOrDefault()?.Name ?? Environment.MachineName;

    /// <summary>Opens or closes the mini window; set by the app.</summary>
    public Action? ToggleMini { get; set; }

    [RelayCommand]
    private void ToggleMiniWindow() => ToggleMini?.Invoke();

    public DashboardViewModel Dashboard { get; }
    public SensorsViewModel Sensors { get; }
    public SensorCatalog Catalog => _catalog;
    public SettingsViewModel Settings { get; }

    /// <summary>The Ctrl+K sensor search.</summary>
    public SensorSearchViewModel Search { get; }

    /// <summary>Alerts and the bell in the header. Created after the tray icon, which it falls back to for notifications.</summary>
    [ObservableProperty] public partial AlertCenter? Alerts { get; private set; }

    /// <summary>The sensor details panel. Created once history exists, since its charts read from it.</summary>
    [ObservableProperty] public partial SensorDetailsViewModel? Details { get; private set; }

    public void AttachHistory(Services.History.HistoryRecorder recorder, Services.History.HistoryStore store)
    {
        Settings.AttachHistory(recorder);
        HistoryStore = store;
        Details = new SensorDetailsViewModel(store, _stream, _catalog, _settings, CreateAlert) { PinBoard = Dashboard, CardAccent = Dashboard.CardAccent };
    }

    /// <summary>Opens the details panel for a sensor: from a dashboard card, a sensor row, or the search.</summary>
    public void OpenDetails(string sensorId) => Details?.Open(sensorId);

    public void AttachAlerts(AlertCenter alerts)
    {
        Alerts = alerts;
        Settings.AttachAlerts(alerts);
        Dashboard.PumpWatch = alerts.Pump;
        if (_webDashboard is not null)
            _webDashboard.PumpWatch = alerts.Pump;
    }

    // ---- Diagnostics (step 14) ------------------------------------------------------------------

    /// <summary>
    /// The Copy diagnostics report: how Pulse is set up, what hardware it found, the USB coolers, the settings
    /// and the log's tail, with private details taken out. UI thread.
    /// </summary>
    public string BuildDiagnostics(double displayScale)
    {
        _settings.Flush();
        string json;
        try
        {
            json = System.IO.File.Exists(_settings.FilePath) ? System.IO.File.ReadAllText(_settings.FilePath) : "{}";
        }
        catch (System.IO.IOException)
        {
            json = "{}";
        }
        var facts = new DiagnosticsFacts(
            Settings.Version,
            Diagnostics.WindowsVersion(),
            HardwareService.IsElevated,
            HardwareService.IsDriverInstalled,
            (System.Windows.Media.RenderCapability.Tier >> 16) switch
            {
                2 => "graphics card (tier 2)",
                1 => "partly on the graphics card (tier 1)",
                _ => "software (tier 0)",
            },
            displayScale,
            ThemeService.Current.ToString(),
            System.Globalization.CultureInfo.CurrentCulture.Name,
            _settings.Current.General.UpdateInterval);
        return Diagnostics.Report(facts, _stream.Latest, Dashboard.CoolerStatuses,
            Diagnostics.Tail(Log.FilePath, Diagnostics.LogLines), json,
            new Redaction(_settings.Current.Web.Token, Environment.MachineName, Environment.UserName,
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
    }

    // ---- USB coolers (step 11) -----------------------------------------------------------------

    /// <summary>Shows what Pulse does with each USB cooler on the cooling card and in Settings, as it changes.</summary>
    public void AttachCoolers(Services.Coolers.CoolerService coolers)
    {
        void Apply()
        {
            Dashboard.CoolerStatuses = coolers.Statuses;
            if (_webDashboard is not null)
                _webDashboard.CoolerStatuses = coolers.Statuses;
            Settings.SetCoolers(coolers.Statuses);
            if (_stream.Latest is { } latest && IsDashboardSelected)
                Dashboard.Apply(latest);
        }
        coolers.StatusChanged += () => _dispatcher.BeginInvoke(Apply);
        Apply();
    }

    // ---- Processes (step 10) -------------------------------------------------------------------

    private ProcessMonitor? _processes;
    private ProcessList? _pendingProcesses;
    private int _processesScheduled;

    /// <summary>The Processes page. Created with the process sampler, which the app starts after the window.</summary>
    [ObservableProperty] public partial ProcessesViewModel? Processes { get; private set; }

    public void AttachProcesses(ProcessMonitor monitor)
    {
        _processes = monitor;
        Processes = new ProcessesViewModel(_settings, monitor.Info, ProcessMonitor.End) { IsActive = IsProcessesSelected };
        monitor.Updated += OnProcessesUpdated;
        UpdateProcessSampling();
    }

    /// <summary>
    /// Processes are sampled only while someone can see them: the window is up and shows the Processes page,
    /// or the dashboard with its Top processes tile.
    /// </summary>
    private void UpdateProcessSampling()
    {
        if (_processes is null) return;
        _processes.IsWanted = _isWindowActive && (IsProcessesSelected || (IsDashboardSelected && Dashboard.ShowsProcesses));
    }

    /// <summary>On the sampling thread. Like sensor snapshots, only the newest list is applied.</summary>
    private void OnProcessesUpdated(ProcessList list)
    {
        Volatile.Write(ref _pendingProcesses, list);
        if (Interlocked.Exchange(ref _processesScheduled, 1) == 0)
            _dispatcher.BeginInvoke(ApplyProcesses, DispatcherPriority.Background);
    }

    private void ApplyProcesses()
    {
        Interlocked.Exchange(ref _processesScheduled, 0);
        if (Interlocked.Exchange(ref _pendingProcesses, null) is not { } list || _processes is null)
            return;
        Dashboard.ProcessesCard.Apply(list, _processes.Info);
        Processes?.Apply(list);
    }

    /// <summary>The bell's "Alert settings" link.</summary>
    [RelayCommand]
    private void OpenAlertSettings() => IsSettingsSelected = true;

    /// <summary>
    /// All sensors → "Alert when above…": a new rule with a starting threshold a bit above the current
    /// reading, then Settings opens so it can be adjusted.
    /// </summary>
    private void CreateAlert(string sensorId)
    {
        if (_catalog.Find(sensorId) is not { } info)
            return;

        var current = _stream.Latest?.Hardware.SelectMany(h => h.AllSensors).FirstOrDefault(s => s.Id == sensorId)?.Value ?? 0;
        var threshold = info.Type == LibreHardwareMonitor.Hardware.SensorType.Temperature
            ? Math.Ceiling(current + 10)
            : Math.Ceiling(Math.Max(current * 1.2, current + 1));

        _settings.Update(s => s.Alerts.Rules.Add(new AlertRule
        {
            Name = $"{info.DisplayName} too high",
            Target = AlertTarget.Sensor,
            SensorId = sensorId,
            Condition = AlertCondition.Above,
            Threshold = threshold,
            HoldSeconds = 10,
        }));
        Log.Write($"Alert rule added for {sensorId} above {threshold}");
        IsSettingsSelected = true;
        Settings.Refresh();
    }

    [ObservableProperty] public partial bool IsDashboardSelected { get; set; } = true;
    [ObservableProperty] public partial bool IsSensorsSelected { get; set; }
    [ObservableProperty] public partial bool IsSettingsSelected { get; set; }
    [ObservableProperty] public partial bool IsProcessesSelected { get; set; }

    partial void OnIsSensorsSelectedChanged(bool value) => Sensors.IsActive = value;

    partial void OnIsDashboardSelectedChanged(bool value)
    {
        if (value && _stream.Latest is { } latest)
            Dashboard.Apply(latest);
        UpdateProcessSampling();
    }

    partial void OnIsProcessesSelectedChanged(bool value)
    {
        if (Processes is not null)
            Processes.IsActive = value;
        UpdateProcessSampling();
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
        if (interval == Dashboard.SampleInterval) return;
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
        MachineName = MachineNameOf(snapshot);
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
