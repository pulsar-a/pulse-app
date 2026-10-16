using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using Pulse.Services.Web;

namespace Pulse.Services.Updates;

/// <summary>What uninstalling removes, besides the app's own folder (which the uninstaller deletes).</summary>
public interface ICleanupSystem
{
    bool HasStartupTask();
    bool RemoveStartupTask();
    bool HasFirewallRule();
    bool RemoveFirewallRule();
    void RemoveToastIdentity();
    bool KeepData();
    void RemoveData();

    /// <summary>Runs the parts that need administrator rights in an elevated copy (one UAC prompt), within the time given.</summary>
    bool RunElevated(TimeSpan timeout);
}

/// <summary>
/// Leaves nothing behind on uninstall. The uninstaller runs Pulse with <c>--veloapp-uninstall</c> before it
/// deletes the app's folder, unelevated, with no UI, and kills it after 30 seconds. So:
/// <list type="number">
/// <item>What the user's own rights can remove goes first: the toast identity, and the startup task if Windows lets it.</item>
/// <item>What needs admin (the startup task, created elevated, and the web view's firewall rule) goes to an elevated
/// copy of Pulse with <see cref="ElevatedFlag"/>: one UAC prompt, only when there's something to remove.</item>
/// <item>The data folder (settings, history, log) goes only if the user chose that in Settings → About; the default is to keep it.</item>
/// </list>
/// </summary>
public static class UninstallCleanup
{
    public const string ElevatedFlag = "--uninstall-cleanup";
    private static readonly TimeSpan ElevatedTimeout = TimeSpan.FromSeconds(20);

    public static void Run() => Run(new WindowsCleanup());

    /// <summary>The steps, against any system (tests pass a fake). Returns what was left behind, for the log.</summary>
    public static IReadOnlyList<string> Run(ICleanupSystem system)
    {
        var left = new List<string>();
        Try(system.RemoveToastIdentity, "toast identity", left);

        // A task created elevated usually can't be deleted without admin; try anyway, it's free.
        if (system.HasStartupTask())
            system.RemoveStartupTask();
        if ((system.HasStartupTask() || system.HasFirewallRule()) && !system.RunElevated(ElevatedTimeout))
        {
            if (system.HasStartupTask()) left.Add("startup task \"Pulse\"");
            if (system.HasFirewallRule()) left.Add($"firewall rule \"{WebNetwork.FirewallRuleName}\"");
        }

        if (!system.KeepData())
            Try(system.RemoveData, "data folder", left);
        return left;
    }

    /// <summary>The elevated copy: removes the task and the firewall rule, then exits.</summary>
    public static int RunElevatedPart()
    {
        var system = new WindowsCleanup();
        if (system.HasStartupTask()) system.RemoveStartupTask();
        if (system.HasFirewallRule()) system.RemoveFirewallRule();
        return system.HasStartupTask() || system.HasFirewallRule() ? 1 : 0;
    }

    private static void Try(Action step, string what, List<string> left)
    {
        try
        {
            step();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            left.Add(what);
        }
    }

    /// <summary>The real system: schtasks, netsh, the registry and the data folder.</summary>
    private sealed class WindowsCleanup : ICleanupSystem
    {
        public bool HasStartupTask() => StartupTask.GetRegisteredPathAsync().GetAwaiter().GetResult() is not null;
        public bool RemoveStartupTask() => StartupTask.DisableAsync().GetAwaiter().GetResult();
        public bool HasFirewallRule() => WebNetwork.HasFirewallRule();
        public bool RemoveFirewallRule() => WebNetwork.RemoveFirewallRule();

        public void RemoveToastIdentity() =>
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\AppUserModelId\{Notifier.AppUserModelId}", throwOnMissingSubKey: false);

        /// <summary>Read straight from the file: settings aren't loaded in a hook. Keep unless the user said otherwise.</summary>
        public bool KeepData()
        {
            try
            {
                var path = Path.Combine(SettingsService.DefaultFolder, "settings.json");
                return !File.Exists(path)
                       || JsonNode.Parse(File.ReadAllText(path))?["Updates"]?["KeepDataOnUninstall"]?.GetValue<bool>() != false;
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException or FormatException)
            {
                return true;
            }
        }

        public void RemoveData() => Directory.Delete(SettingsService.DefaultFolder, recursive: true);

        public bool RunElevated(TimeSpan timeout)
        {
            if (Environment.ProcessPath is not { } exe) return false;
            try
            {
                using var process = Process.Start(new ProcessStartInfo(exe, ElevatedFlag) { UseShellExecute = true, Verb = "runas" });
                return process is not null && process.WaitForExit(timeout) && process.ExitCode == 0;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return false;   // declined, or no prompt possible
            }
        }
    }
}
