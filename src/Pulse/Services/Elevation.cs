using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Pulse.Services;

/// <summary>
/// Pulse asks for administrator rights itself, rather than through its manifest (which says <c>asInvoker</c>).
///
/// With <c>highestAvailable</c> in the manifest, Windows refuses to start Pulse from any non-elevated program
/// for an admin user (error 740), and that includes the installer and updater, which start the app for their
/// hooks and to restart it after an update. So Pulse starts as whoever launched it and, when it isn't
/// elevated but could be, starts an elevated copy of itself:
/// <list type="number">
/// <item>through the "Start with Windows" task when there is one, which runs elevated without a prompt;</item>
/// <item>otherwise with the usual UAC prompt. Declining it isn't fatal: Pulse runs with fewer sensors, as for a
/// standard user.</item>
/// </list>
/// Standard users (who would be asked for someone else's password) run as they are, as before.
/// </summary>
public static class Elevation
{
    /// <summary>Skips elevation (for development and troubleshooting).</summary>
    public const string NoElevateFlag = "--no-elevate";

    /// <summary>Marks the elevated copy, so it never tries again.</summary>
    public const string RelaunchedFlag = "--elevated";

    public enum Plan
    {
        /// <summary>Already elevated, can't be, or shouldn't try: carry on.</summary>
        RunAsIs,

        /// <summary>Start an elevated copy, then exit.</summary>
        Elevate,
    }

    /// <summary>What to do at start. Pure, so the rules are tested on their own.</summary>
    /// <param name="canElevate">An administrator running with UAC's limited token.</param>
    public static Plan Decide(IReadOnlyList<string> args, bool isElevated, bool canElevate, bool debuggerAttached)
    {
        if (isElevated || !canElevate || debuggerAttached)
            return Plan.RunAsIs;
        foreach (var arg in args)
        {
            if (arg.Equals(NoElevateFlag, StringComparison.OrdinalIgnoreCase)
                || arg.Equals(RelaunchedFlag, StringComparison.OrdinalIgnoreCase)
                || arg.StartsWith("--veloapp-", StringComparison.OrdinalIgnoreCase)
                || arg.Equals(AppArguments.DumpSnapshotFlag, StringComparison.OrdinalIgnoreCase))
                return Plan.RunAsIs;
        }
        return Plan.Elevate;
    }

    public static bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    /// <summary>True for an administrator whose process runs with UAC's limited token: a prompt would elevate them.</summary>
    public static bool CanElevate
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return GetTokenInformation(identity.Token, TokenElevationType, out var type, sizeof(int), out _)
                   && type == TokenElevationTypeLimited;
        }
    }

    /// <summary>
    /// Starts the elevated copy: through the startup task if it exists and points at this exe (no prompt),
    /// otherwise with UAC. Returns false if neither worked (the prompt was declined, say).
    /// </summary>
    /// <param name="startInTray">The launch asked for the tray (the task always starts Pulse there).</param>
    public static bool Relaunch(string exePath, IReadOnlyList<string> args, bool startInTray)
    {
        if (TryStartupTask(exePath, startInTray))
            return true;
        try
        {
            Process.Start(new ProcessStartInfo(exePath)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = string.Join(" ", args.Append(RelaunchedFlag).Select(Quote)),
            })?.Dispose();
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return false;   // the user said no: run without admin rights
        }
    }

    /// <summary>
    /// The task runs Pulse elevated with <c>--tray</c>. For a normal launch the new copy is then asked to show its
    /// window, the way a second launch does, once it's up.
    /// </summary>
    private static bool TryStartupTask(string exePath, bool startInTray)
    {
        string? registered;
        try
        {
            registered = StartupTask.GetRegisteredPathAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
        if (registered is null || !string.Equals(Path.GetFullPath(registered), Path.GetFullPath(exePath), StringComparison.OrdinalIgnoreCase))
            return false;
        if (!StartupTask.RunNowAsync().GetAwaiter().GetResult())
            return false;
        if (!startInTray)
            SingleInstance.WaitThenActivate(TimeSpan.FromSeconds(20));
        return true;
    }

    private static string Quote(string arg) => arg.Contains(' ') || arg.Contains('"') ? $"\"{arg.Replace("\"", "\\\"")}\"" : arg;

    private const int TokenElevationType = 18;
    private const int TokenElevationTypeLimited = 3;
    private const int ErrorCancelled = 1223;

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, out int info, int length, out int returned);
}
