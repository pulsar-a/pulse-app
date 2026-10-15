using System.Text.Json.Serialization;
using System.Windows;

namespace Pulse.Services;

/// <summary>The mini window: a small always-on-top readout for gaming or working (Settings → Mini window).</summary>
public sealed class MiniSettings
{
    /// <summary>The default readouts: resolved from the hardware each tick, so they work on any PC.</summary>
    public static readonly string[] AutoItems = [MiniItems.Cpu, MiniItems.Gpu, MiniItems.Pump, MiniItems.Power];

    public const string DefaultHotkey = "Ctrl+Alt+P";

    /// <summary>Shown items in order: sensor ids, or the "auto:" items. Null means <see cref="AutoItems"/>.</summary>
    public List<string>? Items { get; set; }

    public bool Horizontal { get; set; }

    /// <summary>0.4 to 1.</summary>
    public double Opacity { get; set; } = 0.9;

    /// <summary>Clicks go through to whatever is underneath (turn it off from the tray menu or Settings).</summary>
    public bool ClickThrough { get; set; }

    /// <summary>Hide the mini window while Pulse's main window is in front, where it would be redundant. Off by default.</summary>
    public bool HideWhenPulseActive { get; set; }

    /// <summary>Global shortcut that shows or hides it, like "Ctrl+Alt+P"; empty turns the shortcut off.</summary>
    public string Hotkey { get; set; } = DefaultHotkey;

    /// <summary>Whether it was open when Pulse last ran, so it comes back.</summary>
    public bool Open { get; set; }

    /// <summary>Where it was on each monitor (by monitor), so docking and undocking a laptop doesn't lose it.</summary>
    public Dictionary<string, MiniPosition> Positions { get; set; } = new();

    /// <summary>The monitor it was last on.</summary>
    public string? Monitor { get; set; }

    [JsonIgnore]
    public IReadOnlyList<string> ItemsOrDefault => Items is { } items ? items : AutoItems;
}

public sealed class MiniPosition
{
    public double X { get; set; }
    public double Y { get; set; }
}

/// <summary>The automatic readouts, which follow whatever hardware is there.</summary>
public static class MiniItems
{
    public const string Cpu = "auto:cpu";
    public const string Gpu = "auto:gpu";
    public const string Pump = "auto:pump";
    public const string Power = "auto:power";

    public static bool IsAuto(string item) => item.StartsWith("auto:", StringComparison.Ordinal);

    public static string Describe(string item) => item switch
    {
        Cpu => "CPU temperature",
        Gpu => "GPU temperature",
        Pump => "Pump speed",
        Power => "System power",
        _ => item,
    };
}

/// <summary>A monitor's work area in device-independent units, keyed so it can be recognised next time.</summary>
public sealed record MonitorArea(string Key, Rect WorkArea, bool IsPrimary);

/// <summary>Where the mini window opens. Pure logic, so it's easy to test.</summary>
public static class MiniPlacement
{
    private const double Margin = 24;

    /// <summary>
    /// Its saved spot on the monitor it was last on, if that monitor is still there (pulled inside the
    /// screen if needed); otherwise the top-right corner of the primary monitor.
    /// </summary>
    public static Point Choose(IReadOnlyList<MonitorArea> monitors, MiniSettings settings, Size window)
    {
        if (settings.Monitor is { } key && monitors.FirstOrDefault(m => m.Key == key) is { } monitor
            && settings.Positions.TryGetValue(key, out var saved))
            return Clamp(new Point(saved.X, saved.Y), monitor.WorkArea, window);

        var primary = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.FirstOrDefault();
        if (primary is null)
            return new Point(Margin, Margin);
        var area = primary.WorkArea;
        return new Point(area.Right - window.Width - Margin, area.Top + Margin);
    }

    public static Point Clamp(Point p, Rect area, Size window) => new(
        Math.Clamp(p.X, area.Left, Math.Max(area.Left, area.Right - window.Width)),
        Math.Clamp(p.Y, area.Top, Math.Max(area.Top, area.Bottom - window.Height)));
}
