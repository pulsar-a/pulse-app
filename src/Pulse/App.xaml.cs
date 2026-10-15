using System.ComponentModel;
using System.Windows;
using Pulse.Services;
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

        ApplicationThemeManager.Apply(ApplicationTheme.Dark, WindowBackdropType.Mica, true);

        Log.Start();
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

        var stream = new SensorStream();
        _hardware = new HardwareService(stream);
        var viewModel = new MainViewModel(_hardware, stream, settings, Dispatcher);
        MainWindow = new MainWindow(viewModel);
        MainWindow.Closing += OnMainWindowClosing;

        _tray = new TrayIcon(settings, viewModel.Catalog, stream, Dispatcher, ToggleWindow, BringToFront, ExitPulse);

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

    private void ExitPulse()
    {
        _exiting = true;
        MainWindow?.Close();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Remove the tray icon first, so it doesn't linger in the notification area.
        _tray?.Dispose();
        _hardware?.Dispose();
        _settings?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
