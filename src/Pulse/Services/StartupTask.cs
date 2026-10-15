using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

namespace Pulse.Services;

/// <summary>
/// "Start with Windows", done with a scheduled task rather than a Run key. Pulse asks for admin rights,
/// so a Run-key autostart would show the admin prompt at every sign-in; a task set to "run with highest
/// privileges" starts it elevated without one. Creating such a task needs admin rights, which Pulse has
/// when it runs normally.
///
/// The task is the source of truth: settings don't store whether it's on.
/// </summary>
public static class StartupTask
{
    public const string TaskName = "Pulse";

    /// <summary>The path the task launches, or null if there's no task.</summary>
    public static async Task<string?> GetRegisteredPathAsync()
    {
        var (exitCode, output) = await RunSchtasksAsync("/Query", "/TN", TaskName, "/XML");
        if (exitCode != 0)
            return null;
        var match = Regex.Match(output, "<Command>(.*?)</Command>", RegexOptions.Singleline);
        return match.Success ? Unescape(match.Groups[1].Value.Trim()) : null;
    }

    public static async Task<bool> IsEnabledAsync() => await GetRegisteredPathAsync() is not null;

    /// <summary>Creates (or replaces) the task so Pulse starts in the tray when the current user signs in.</summary>
    public static async Task<bool> EnableAsync(string exePath)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var xml = BuildTaskXml(exePath, identity.User?.Value ?? identity.Name);
        var file = Path.Combine(Path.GetTempPath(), $"pulse-task-{Guid.NewGuid():N}.xml");
        try
        {
            // schtasks expects the XML file in UTF-16, as Task Scheduler itself exports it.
            await File.WriteAllTextAsync(file, xml, Encoding.Unicode);
            var (exitCode, output) = await RunSchtasksAsync("/Create", "/TN", TaskName, "/XML", file, "/F");
            if (exitCode != 0)
                Log.Write($"Couldn't create the startup task (exit code {exitCode}): {output.Trim()}");
            else
                Log.Write($"Startup task created for {exePath}");
            return exitCode == 0;
        }
        finally
        {
            try { File.Delete(file); }
            catch (IOException) { }
        }
    }

    public static async Task<bool> DisableAsync()
    {
        var (exitCode, output) = await RunSchtasksAsync("/Delete", "/TN", TaskName, "/F");
        if (exitCode != 0)
            Log.Write($"Couldn't remove the startup task (exit code {exitCode}): {output.Trim()}");
        else
            Log.Write("Startup task removed");
        return exitCode == 0;
    }

    /// <summary>
    /// If Pulse was moved since the task was created, the task would launch a missing file.
    /// Re-point it at the current exe. Does nothing if there's no task or it's already correct.
    /// </summary>
    public static async Task RepairPathAsync(string exePath)
    {
        var registered = await GetRegisteredPathAsync();
        if (registered is null || string.Equals(registered, exePath, StringComparison.OrdinalIgnoreCase))
            return;
        Log.Write($"Startup task points at {registered}; updating it to {exePath}");
        await EnableAsync(exePath);
    }

    /// <summary>The Task Scheduler definition. Public for tests.</summary>
    public static string BuildTaskXml(string exePath, string userId)
    {
        var command = SecurityElement.Escape(exePath);
        var directory = SecurityElement.Escape(Path.GetDirectoryName(exePath) ?? "");
        var user = SecurityElement.Escape(userId);
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Starts Pulse in the notification area when you sign in.</Description>
                <URI>\{TaskName}</URI>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{user}</UserId>
                  <Delay>PT5S</Delay>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{user}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>5</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{command}</Command>
                  <Arguments>{AppArguments.TrayFlag}</Arguments>
                  <WorkingDirectory>{directory}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    // Notes on the settings above: Task Scheduler's defaults would stop Pulse after 3 days
    // (ExecutionTimeLimit), not start it on battery, and run it at below-normal priority (7);
    // priority 5 is normal. The 5 s delay lets sign-in settle before hardware is opened.

    private static string Unescape(string text) =>
        text.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"").Replace("&apos;", "'").Replace("&amp;", "&");

    private static async Task<(int ExitCode, string Output)> RunSchtasksAsync(params string[] args)
    {
        var info = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
            info.ArgumentList.Add(arg);

        try
        {
            using var process = Process.Start(info)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return (process.ExitCode, await stdout + await stderr);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (-1, ex.Message);
        }
    }
}
