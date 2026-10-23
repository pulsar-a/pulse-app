using System.Windows;
using System.Windows.Media;

namespace Pulse.Controls;

/// <summary>
/// Category accents and the temperature/load heat scale used across the app.
/// Accents come from the theme tokens (Themes/Dark.xaml); the fallbacks are only used when no
/// application is running, as in tests.
/// </summary>
public static class Palette
{
    public static Color Cpu => Accent("Cpu", Color.FromRgb(0xFF, 0x7A, 0x45));
    public static Color Gpu => Accent("Gpu", Color.FromRgb(0x3D, 0xDC, 0x97));
    public static Color Memory => Accent("Memory", Color.FromRgb(0xA7, 0x8B, 0xFA));
    public static Color Power => Accent("Power", Color.FromRgb(0xFF, 0xC0, 0x48));
    public static Color Network => Accent("Network", Color.FromRgb(0x4F, 0xA8, 0xFF));
    public static Color Storage => Accent("Storage", Color.FromRgb(0x38, 0xBD, 0xF8));
    public static Color Fans => Accent("Fans", Color.FromRgb(0x2D, 0xD4, 0xBF));
    public static Color Board => Accent("Board", Color.FromRgb(0xF4, 0x72, 0xB6));

    /// <summary>
    /// The accent for a sensor, matching the dashboard's color code: what it belongs to comes first
    /// (anything on the graphics card is green, memory purple, power yellow, network blue), and only
    /// sensors on shared chips like the motherboard fall back to their kind (teal fans, warm temperatures).
    /// </summary>
    public static Color ForSensor(LibreHardwareMonitor.Hardware.SensorType type, LibreHardwareMonitor.Hardware.HardwareType? hardware)
    {
        if (type == LibreHardwareMonitor.Hardware.SensorType.Power)
            return Power;
        return hardware switch
        {
            LibreHardwareMonitor.Hardware.HardwareType.Cpu => Cpu,
            LibreHardwareMonitor.Hardware.HardwareType.GpuNvidia or LibreHardwareMonitor.Hardware.HardwareType.GpuAmd
                or LibreHardwareMonitor.Hardware.HardwareType.GpuIntel => Gpu,
            LibreHardwareMonitor.Hardware.HardwareType.Memory => Memory,
            LibreHardwareMonitor.Hardware.HardwareType.Storage => Storage,
            LibreHardwareMonitor.Hardware.HardwareType.Network => Network,
            LibreHardwareMonitor.Hardware.HardwareType.Psu => Power,
            // The cooling card is blue.
            LibreHardwareMonitor.Hardware.HardwareType.Cooler => Network,
            _ => ForSensor(type),
        };
    }

    /// <summary>The accent for a kind of sensor: warm for temperatures, teal for fans, yellow for power...</summary>
    public static Color ForSensor(LibreHardwareMonitor.Hardware.SensorType type) => type switch
    {
        LibreHardwareMonitor.Hardware.SensorType.Temperature => Cpu,
        LibreHardwareMonitor.Hardware.SensorType.Load or LibreHardwareMonitor.Hardware.SensorType.Control
            or LibreHardwareMonitor.Hardware.SensorType.Level => Gpu,
        LibreHardwareMonitor.Hardware.SensorType.Fan or LibreHardwareMonitor.Hardware.SensorType.Flow => Fans,
        LibreHardwareMonitor.Hardware.SensorType.Power or LibreHardwareMonitor.Hardware.SensorType.Voltage
            or LibreHardwareMonitor.Hardware.SensorType.Current or LibreHardwareMonitor.Hardware.SensorType.Energy => Power,
        LibreHardwareMonitor.Hardware.SensorType.Throughput => Network,
        LibreHardwareMonitor.Hardware.SensorType.Clock or LibreHardwareMonitor.Hardware.SensorType.Frequency => Storage,
        _ => Memory,
    };

    private static Color Accent(string name, Color fallback) =>
        Application.Current?.TryFindResource($"Pulse.Accent.{name}.Color") is Color color ? color : fallback;

    private static readonly (double Stop, Color Color)[] HeatStops =
    [
        (0.00, Color.FromRgb(0x3B, 0x82, 0xF6)),
        (0.35, Color.FromRgb(0x2D, 0xD4, 0xBF)),
        (0.60, Color.FromRgb(0xFA, 0xCC, 0x15)),
        (0.80, Color.FromRgb(0xFB, 0x92, 0x3C)),
        (1.00, Color.FromRgb(0xEF, 0x44, 0x44)),
    ];

    private static readonly Dictionary<int, SolidColorBrush> HeatCache = new();

    /// <summary>Maps 0..1 onto blue → teal → yellow → orange → red.</summary>
    public static SolidColorBrush Heat(double t)
    {
        // 50 cached steps are indistinguishable from a continuous scale and avoid a brush per tile per tick.
        var key = (int)Math.Round(Math.Clamp(double.IsNaN(t) ? 0 : t, 0, 1) * 50);
        if (HeatCache.TryGetValue(key, out var cached))
            return cached;

        var x = key / 50.0;
        var i = 1;
        while (i < HeatStops.Length - 1 && x > HeatStops[i].Stop) i++;
        var (s0, c0) = HeatStops[i - 1];
        var (s1, c1) = HeatStops[i];
        var f = (x - s0) / (s1 - s0);
        var color = Color.FromRgb(Lerp(c0.R, c1.R, f), Lerp(c0.G, c1.G, f), Lerp(c0.B, c1.B, f));

        var brush = new SolidColorBrush(color);
        brush.Freeze();
        HeatCache[key] = brush;
        return brush;
    }

    /// <summary>Heat for a temperature: 30 °C and below is coolest, 95 °C and above is hottest.</summary>
    public static SolidColorBrush Temperature(double celsius) => Heat((celsius - 30) / 65);

    private static byte Lerp(byte a, byte b, double f) => (byte)Math.Round(a + (b - a) * f);
}
