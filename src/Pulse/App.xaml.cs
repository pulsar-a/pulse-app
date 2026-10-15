using System.Windows;
using Pulse.Services;
using Pulse.ViewModels;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Pulse;

public partial class App : Application
{
    private HardwareService? _hardware;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
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
        var settings = new SettingsService();
        settings.Load();

        _hardware = new HardwareService();
        var viewModel = new MainViewModel(_hardware, settings, Dispatcher);
        MainWindow = new MainWindow(viewModel);
        MainWindow.Show();

        _hardware.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hardware?.Dispose();
        base.OnExit(e);
    }
}
