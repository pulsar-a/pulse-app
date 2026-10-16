namespace Pulse.Services.Processes;

/// <summary>Task Manager's three sections.</summary>
public enum ProcessKind
{
    /// <summary>Has a window on the desktop.</summary>
    App,

    /// <summary>Everything else that isn't part of Windows: services, updaters, helpers, tray tools.</summary>
    Background,

    /// <summary>Part of Windows itself: from the Windows folder, or one of its core processes.</summary>
    Windows,
}

/// <summary>How the Processes page is grouped; switched on the page itself.</summary>
public sealed class ProcessesSettings
{
    /// <summary>Sections for apps, background processes and Windows processes, like Task Manager.</summary>
    public bool GroupByType { get; set; } = true;

    /// <summary>One row per program (Chrome's processes added up); off shows every process on its own.</summary>
    public bool CombinePrograms { get; set; } = true;

    /// <summary>Sections the user folded away.</summary>
    public List<ProcessKind> Collapsed { get; set; } = [];
}

/// <summary>
/// All processes of one program (by executable name), added up: Chrome's 30 processes are one row. This is
/// what the dashboard tile and the Processes page show.
/// </summary>
/// <param name="Key">The executable name in lower case ("chrome.exe").</param>
/// <param name="Gpu">The members' GPU use added up (capped at 100); null without GPU counters.</param>
/// <param name="CanEnd">False for Windows' own processes and services, and for Pulse itself.</param>
/// <param name="Kind">Which section it goes in; set by <see cref="ProcessApps.Classify"/>.</param>
public sealed record AppUsage(
    string Key,
    string ImageName,
    IReadOnlyList<ProcessUsage> Members,
    double Cpu,
    double? Gpu,
    long Memory,
    bool CanEnd,
    ProcessKind Kind = ProcessKind.Background)
{
    public int Count => Members.Count;

    /// <summary>One member as a row of its own (with "combine processes" off): same program, same section.</summary>
    public AppUsage Single(ProcessUsage member) =>
        this with { Key = $"{Key}/{member.Pid}/{member.CreateTime}", Members = [member], Cpu = member.Cpu, Gpu = member.Gpu,
            Memory = member.Memory, CanEnd = member.CanEnd };
}

public static class ProcessApps
{
    /// <summary>
    /// Processes Windows can't do without: ending one logs you out or crashes the system. They run in
    /// session 0 anyway, except these, which also run in the user's session.
    /// </summary>
    private static readonly HashSet<string> Critical = new(StringComparer.OrdinalIgnoreCase)
    {
        "csrss.exe", "winlogon.exe", "dwm.exe", "fontdrvhost.exe", "smss.exe", "wininit.exe", "lsass.exe",
        "services.exe", "System", "Registry", "Memory Compression", "Secure System", "LsaIso.exe",
        "sihost.exe", "ctfmon.exe", "svchost.exe", "conhost.exe", "taskhostw.exe", "RuntimeBroker.exe",
        "ShellExperienceHost.exe", "StartMenuExperienceHost.exe", "SearchHost.exe", "TextInputHost.exe",
        "LockApp.exe", "dllhost.exe", "WUDFHost.exe", "audiodg.exe", "userinit.exe",
    };

    /// <summary>Whether Pulse lets the user end this process: their own programs, not Windows or services.</summary>
    /// <param name="session">The session Pulse runs in (the user's desktop).</param>
    public static bool CanEnd(ProcessUsage process, int session, int self) =>
        process.SessionId != 0 && process.SessionId == session && process.Pid != self && process.Pid > 4
        && !Critical.Contains(process.Name);

    public static List<AppUsage> Group(IReadOnlyList<ProcessUsage> processes, int session, int self)
    {
        var groups = new Dictionary<string, List<ProcessUsage>>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in processes)
        {
            if (!groups.TryGetValue(process.Name, out var members))
                groups[process.Name] = members = [];
            members.Add(process);
        }

        var result = new List<AppUsage>(groups.Count);
        foreach (var (name, members) in groups)
        {
            double cpu = 0, gpuSum = 0;
            long memory = 0;
            var hasGpu = false;
            var canEnd = true;
            for (var i = 0; i < members.Count; i++)
            {
                var member = members[i] = members[i] with { CanEnd = CanEnd(members[i], session, self) };
                cpu += member.Cpu;
                memory += member.Memory;
                if (member.Gpu is { } g)
                {
                    hasGpu = true;
                    gpuSum += g;
                }
                canEnd &= member.CanEnd;
            }
            result.Add(new AppUsage(name.ToLowerInvariant(), members[0].Name, members, Math.Min(100, cpu),
                hasGpu ? Math.Min(100, gpuSum) : null, memory, canEnd));
        }
        return result;
    }

    /// <summary>
    /// Sorts programs into Task Manager's sections. A program with a window on the desktop is an app (all its
    /// processes go with it, as in Task Manager). Otherwise it's a Windows process when it's one of Windows'
    /// core processes or runs from the Windows folder, and a background process when not.
    /// </summary>
    /// <param name="hasWindow">Whether a PID has a visible window.</param>
    /// <param name="pathOf">A program's executable path, when known.</param>
    /// <param name="windowsFolder">C:\Windows, normally.</param>
    public static List<AppUsage> Classify(IReadOnlyList<AppUsage> apps, Func<int, bool> hasWindow, Func<string, string?> pathOf,
        string windowsFolder)
    {
        var folder = windowsFolder.TrimEnd('\\') + "\\";
        var result = new List<AppUsage>(apps.Count);
        foreach (var app in apps)
        {
            var kind = app.Members.Any(m => hasWindow(m.Pid)) ? ProcessKind.App
                : IsWindows(app, pathOf(app.Key), folder) ? ProcessKind.Windows
                : ProcessKind.Background;
            result.Add(app.Kind == kind ? app : app with { Kind = kind });
        }
        return result;
    }

    private static bool IsWindows(AppUsage app, string? path, string windowsFolder)
    {
        if (Critical.Contains(app.ImageName) || app.Members.Any(m => m.Pid <= 4))
            return true;
        if (path is not null)
            return path.StartsWith(windowsFolder, StringComparison.OrdinalIgnoreCase);
        // No path: Windows wouldn't say, which it does for its own protected processes (and some services).
        return app.Members.All(m => m.SessionId == 0);
    }
}
