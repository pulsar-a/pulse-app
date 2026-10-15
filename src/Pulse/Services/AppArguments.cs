namespace Pulse.Services;

/// <summary>
/// Command-line options:
/// <c>--tray</c> starts hidden in the notification area (used by the start-with-Windows task);
/// <c>--dump-snapshot &lt;file&gt;</c> writes one snapshot to JSON and exits (developer tool).
/// </summary>
public sealed record AppArguments(bool StartInTray, string? DumpSnapshotPath)
{
    public const string TrayFlag = "--tray";
    public const string DumpSnapshotFlag = "--dump-snapshot";

    public static AppArguments Parse(IReadOnlyList<string> args)
    {
        var tray = false;
        string? dump = null;
        for (var i = 0; i < args.Count; i++)
        {
            if (string.Equals(args[i], TrayFlag, StringComparison.OrdinalIgnoreCase))
                tray = true;
            else if (string.Equals(args[i], DumpSnapshotFlag, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
                dump = args[++i];
        }
        return new AppArguments(tray, dump);
    }
}
