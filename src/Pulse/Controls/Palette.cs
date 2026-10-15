using System.Windows.Media;

namespace Pulse.Controls;

/// <summary>Category accents and the temperature/load heat scale used across the app.</summary>
public static class Palette
{
    public static readonly Color Cpu = Color.FromRgb(0xFF, 0x7A, 0x45);
    public static readonly Color Gpu = Color.FromRgb(0x3D, 0xDC, 0x97);
    public static readonly Color Memory = Color.FromRgb(0xA7, 0x8B, 0xFA);
    public static readonly Color Power = Color.FromRgb(0xFF, 0xC0, 0x48);
    public static readonly Color Network = Color.FromRgb(0x4F, 0xA8, 0xFF);
    public static readonly Color Storage = Color.FromRgb(0x38, 0xBD, 0xF8);
    public static readonly Color Fans = Color.FromRgb(0x2D, 0xD4, 0xBF);

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
