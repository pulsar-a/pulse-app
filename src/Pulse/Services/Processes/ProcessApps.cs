namespace Pulse.Services.Processes;

/// <summary>
/// All processes of one program (by executable name), added up: Chrome's 30 processes are one row. This is
/// what the dashboard tile and the Processes page show.
/// </summary>
/// <param name="Key">The executable name in lower case ("chrome.exe").</param>
/// <param name="Gpu">The members' GPU use added up (capped at 100); null without GPU counters.</param>
/// <param name="CanEnd">False for Windows' own processes and services, and for Pulse itself.</param>
public sealed record AppUsage(
    string Key,
    string ImageName,
    IReadOnlyList<ProcessUsage> Members,
    double Cpu,
    double? Gpu,
    long Memory,
    bool CanEnd)
{
    public int Count => Members.Count;
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
            foreach (var member in members)
            {
                cpu += member.Cpu;
                memory += member.Memory;
                if (member.Gpu is { } g)
                {
                    hasGpu = true;
                    gpuSum += g;
                }
                canEnd &= CanEnd(member, session, self);
            }
            result.Add(new AppUsage(name.ToLowerInvariant(), members[0].Name, members, Math.Min(100, cpu),
                hasGpu ? Math.Min(100, gpuSum) : null, memory, canEnd));
        }
        return result;
    }
}
