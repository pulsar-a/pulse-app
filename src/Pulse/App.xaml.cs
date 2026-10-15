using System.Windows;
using Pulse.Services;
using Pulse.ViewModels;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Pulse;

public partial class App : Application
{
    private HardwareService? _hardware;
    private SettingsService? _settings;
    private SingleInstance? _instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instance = SingleInstance.TryAcquire();
        if (_instance is null)
        {
            // Another Pulse is running and has been asked to come to the front.
            Shutdown(ExitCodes.AlreadyRunning);
            return;
        }

        // Developer tool: write one snapshot to JSON (used for test fixtures) and exit, without any UI.
        if (e.Args is ["--dump-snapshot", var dumpPath])
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

        _hardware = new HardwareService();
        var viewModel = new MainViewModel(_hardware, settings, Dispatcher);
        MainWindow = new MainWindow(viewModel);
        MainWindow.Show();

        _instance.ListenForActivation(() => Dispatcher.BeginInvoke(BringToFront));
        _hardware.Start();
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

    protected override void OnExit(ExitEventArgs e)
    {
        _hardware?.Dispose();
        _settings?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
