using System.Diagnostics;
using Pulse.Services;
using Pulse.Services.Updates;
using Velopack;

namespace Pulse;

/// <summary>
/// The entry point, ahead of WPF. The order matters:
/// <list type="number">
/// <item>Velopack first: the installer and updater start Pulse with hook arguments (install, update, uninstall),
/// which are handled here and exit, before anything else runs.</item>
/// <item>Elevation (see <see cref="Elevation"/>): an already running copy is just brought to the front;
/// otherwise a non-elevated launch starts an elevated copy and exits.</item>
/// <item>Then the WPF app, as before.</item>
/// </list>
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ => UninstallCleanup.Run())
            .Run();

        if (args.Contains(UninstallCleanup.ElevatedFlag, StringComparer.OrdinalIgnoreCase))
            return UninstallCleanup.RunElevatedPart();

        if (Elevation.Decide(args, Elevation.IsElevated, Elevation.CanElevate, Debugger.IsAttached) == Elevation.Plan.Elevate
            && Environment.ProcessPath is { } exe)
        {
            if (SingleInstance.SignalIfRunning())
                return ExitCodes.AlreadyRunning;
            var startInTray = AppArguments.Parse(args).StartInTray;
            if (Elevation.Relaunch(exe, args, startInTray))
                return 0;
            // The UAC prompt was declined: run without admin rights (fewer sensors, and a banner saying why).
        }

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
