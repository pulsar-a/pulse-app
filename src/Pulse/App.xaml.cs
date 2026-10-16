using System.ComponentModel;
using System.Windows;
using Pulse.Services;
using Pulse.Services.History;
using Pulse.Services.Processes;
using Pulse.ViewModels;
using Pulse.Views;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Pulse;

public partial class App : Application
{
    private HardwareService? _hardware;
    private SettingsService? _settings;
    private SingleInstance? _instance;
    private TrayIcon? _tray;
    private HistoryRecorder? _history;
    private MiniController? _mini;
    private ProcessMonitor? _processes;
    private Pulse.Services.Coolers.CoolerService? _coolers;
    private Pulse.Services.Web.WebHost? _web;
    private Pulse.Services.Updates.UpdateService? _updates;
    private System.Windows.Threading.DispatcherTimer? _updateTimer;
    private bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var arguments = AppArguments.Parse(e.Args);

        _instance = SingleInstance.TryAcquire();
        if (_instance is null)
        {
            // Another Pulse is running and has been asked to come to the front.
            Shutdown(ExitCodes.AlreadyRunning);
            return;
        }

        // Developer tool: write one snapshot to JSON (used for test fixtures) and exit, without any UI.
        if (arguments.DumpSnapshotPath is { } dumpPath)
        {
            Shutdown(SnapshotDump.Run(dumpPath));
            return;
        }


        Log.Start();
        // WPF draws on the graphics card when it can (tier 2); worth knowing when someone reports slow drawing.
        Log.Write((System.Windows.Media.RenderCapability.Tier >> 16) switch
        {
            2 => "Rendering on the graphics card (full hardware acceleration)",
            1 => "Rendering partly on the graphics card",
            _ => "Rendering in software (no usable graphics acceleration)",
        });
        UiWatchdog.Start(Dispatcher);

        // A monitoring app should keep running: log UI errors instead of dying on them.
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Write($"Unhandled UI error: {args.Exception}");
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Write($"Unhandled error (terminating: {args.IsTerminating}): {args.ExceptionObject}");
        var settings = _settings = new SettingsService();
        settings.Load();
        // Dark, light, or whatever Windows uses (Settings → Appearance); before any window is built.
        var theme = settings.Current.Appearance.Theme;
        ThemeService.Apply(theme);

        var stream = new SensorStream();
        // USB coolers the sensor library doesn't support (Corsair Commander): read here, shared politely with iCUE and the like.
        _coolers = new Pulse.Services.Coolers.CoolerService(settings);
        _hardware = new HardwareService(stream, _coolers);
        var viewModel = new MainViewModel(_hardware, stream, settings, Dispatcher);
        MainWindow = new MainWindow(viewModel);
        // Watching Windows' setting needs a window; switching in Settings applies straight away.
        ThemeService.Apply(theme, MainWindow);
        settings.Changed += () =>
        {
            if (settings.Current.Appearance.Theme == theme) return;
            theme = settings.Current.Appearance.Theme;
            ThemeService.Apply(theme, MainWindow);
        };
        MainWindow.Closing += OnMainWindowClosing;

        _tray = new TrayIcon(settings, viewModel.Catalog, stream, Dispatcher, ToggleWindow, BringToFront, ExitPulse);

        // Alerts: toasts (falling back to the tray balloon); clicking one opens Pulse; a critical alert turns the tray red.
        var notifier = new Notifier((title, message) => _tray.ShowNotification(title, message));
        notifier.OpenRequested += () => Dispatcher.BeginInvoke(BringToFront);
        var alerts = new AlertCenter(settings, viewModel.Catalog, stream, notifier, Dispatcher);
        alerts.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(AlertCenter.IsCriticalActive))
                _tray.SetAlarm(alerts.IsCriticalActive);
        };
        viewModel.AttachAlerts(alerts);

        // History: every minute of every sensor, on disk (Settings → History).
        _history = new HistoryRecorder(stream, settings);
        viewModel.AttachHistory(_history, new HistoryStore(stream, _history.DatabasePath));

        // What's using the CPU and GPU: sampled only while the Top processes tile or the Processes page is on screen.
        _processes = new ProcessMonitor();
        viewModel.AttachProcesses(_processes);
        _processes.Start();

        // The mini window: always on top, for games (Settings → Mini window).
        _mini = new MiniController(settings, new MiniViewModel(viewModel.Catalog, stream, settings, Dispatcher),
            active => viewModel.IsMiniActive = active,
            () =>
            {
                BringToFront();
                viewModel.IsSettingsSelected = true;
            });
        viewModel.ToggleMini = _mini.Toggle;
        viewModel.Settings.AttachMini(_mini, () => viewModel.Search.Open(
            id => SettingsViewModel.AddMiniItem(settings, id), "Add a sensor to the mini window"));
        _tray.Mini = _mini;
        _tray.ShowDiagnostics = ShowDiagnostics;
        viewModel.Settings.OpenDiagnostics = ShowDiagnostics;
        MainWindow.Activated += (_, _) => _mini.SetPulseInFront(true);
        MainWindow.Deactivated += (_, _) => _mini.SetPulseInFront(false);

        // --tray (from the start-with-Windows task) or "Start minimized": stay in the tray until asked.
        var startHidden = arguments.StartInTray || settings.Current.Startup.StartMinimized;
        if (startHidden && _tray.IsVisible)
        {
            viewModel.IsWindowActive = false;
            Log.Write("Started in the tray");
        }
        else
        {
            if (startHidden)
                MainWindow.WindowState = WindowState.Minimized;
            MainWindow.Show();
        }

        _instance.ListenForActivation(() => Dispatcher.BeginInvoke(BringToFront));
        _hardware.Start();
        _coolers.Start();
        viewModel.AttachCoolers(_coolers);

        // The phone and web view: off until turned on in Settings; then a read-only page on the local network.
        var feed = new Pulse.Services.Web.WebFeed(Dispatcher, stream, viewModel.CreateWebDashboard,
            () => viewModel.Alerts?.Items ?? Enumerable.Empty<AlertItem>(), MainViewModel.MachineNameOf);
        var server = new Pulse.Services.Web.WebServer(feed, () => viewModel.HistoryStore, () => settings.Current.Web.Token);
        server.StreamsChanged += () => Dispatcher.BeginInvoke(() => viewModel.IsRemoteActive = server.Streams > 0);
        _web = new Pulse.Services.Web.WebHost(settings, server);
        viewModel.Settings.AttachWeb(_web);
        _web.Apply();

        // Updates: a daily check once installed (never on a copy built from source), installed when Pulse exits.
        _updates = new Pulse.Services.Updates.UpdateService(settings, () => alerts.IsCriticalActive);
        viewModel.Settings.AttachUpdates(_updates, RestartToUpdate);
        _updateTimer = new System.Windows.Threading.DispatcherTimer { Interval = Pulse.Services.Updates.UpdatePolicy.FirstCheckDelay };
        _updateTimer.Tick += async (_, _) =>
        {
            _updateTimer.Interval = TimeSpan.FromHours(1);
            await _updates.CheckIfDueAsync();
        };
        _updateTimer.Start();

        // If Pulse was moved since "Start with Windows" was turned on, point the task at the new location.
        if (HardwareService.IsElevated && Environment.ProcessPath is { } exe)
            _ = StartupTask.RepairPathAsync(exe);
    }

    /// <summary>
    /// Closing the window hides it to the tray (unless the user chose "Exit" or turned the tray icon off).
    /// The first time, a notification explains that Pulse is still running.
    /// </summary>
    private void OnMainWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting || _settings is null || _tray is null)
            return;

        var startup = _settings.Current.Startup;
        if (!startup.ShowTrayIcon || !startup.CloseToTray)
        {
            Log.Write("Window closed: exiting");
            return;
        }

        e.Cancel = true;
        MainWindow?.Hide();
        Log.Write("Window closed: still running in the tray");

        if (!startup.CloseNoticeShown)
        {
            _tray.ShowNotification("Pulse is still running",
                "It's in the notification area. Click the icon to open it, or right-click to exit.");
            _settings.Update(s => s.Startup.CloseNoticeShown = true);
        }
    }

    private void ToggleWindow()
    {
        if (MainWindow is { IsVisible: true, WindowState: not WindowState.Minimized, IsActive: true })
            MainWindow.Hide();
        else
            BringToFront();
    }

    private void BringToFront()
    {
        if (MainWindow is not { } window)
            return;

        window.Show();
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Activate();

        // Activate() alone won't take focus from another app; a brief Topmost flip will.
        window.Topmost = true;
        window.Topmost = false;
        window.Focus();
    }

    /// <summary>The diagnostics report, in a window to check before copying.</summary>
    private void ShowDiagnostics()
    {
        if (MainWindow?.DataContext is not MainViewModel viewModel)
            return;
        var owner = MainWindow is { IsVisible: true } ? MainWindow : null;
        var scale = owner is null ? 1 : System.Windows.Media.VisualTreeHelper.GetDpi(owner).DpiScaleX;
        var window = new DiagnosticsWindow(viewModel.BuildDiagnostics(scale));
        if (owner is not null)
            window.Owner = owner;
        else
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.Show();
        window.Activate();
    }

    private void ExitPulse()
    {
        _exiting = true;
        MainWindow?.Close();
        Shutdown();
    }

    /// <summary>Settings → About → Restart to update: shut down as on exit, then hand over to the updater.</summary>
    private void RestartToUpdate()
    {
        if (_updates is null) return;
        _exiting = true;
        _updateTimer?.Stop();
        DisposeServices();
        // The updater restarts Pulse, which then elevates itself again (through the startup task when there is one).
        if (!_updates.RestartToUpdate())
            Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DisposeServices();
        base.OnExit(e);
    }

    private bool _disposed;

    private void DisposeServices()
    {
        if (_disposed) return;
        _disposed = true;
        // Remove the tray icon first, so it doesn't linger in the notification area.
        _tray?.Dispose();
        _mini?.Dispose();
        _web?.Dispose();
        _processes?.Dispose();
        _hardware?.Dispose();
        // Its reads always end by handing the cooler back to its own curves; this just stops the loop.
        _coolers?.Dispose();
        // After polling stops, so the minute in progress is complete when it's written.
        _history?.Dispose();
        _settings?.Dispose();
        _instance?.Dispose();
    }
}
