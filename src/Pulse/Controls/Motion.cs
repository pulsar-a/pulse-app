using System.Windows.Media.Animation;

namespace Pulse.Controls;

/// <summary>
/// Shared animation settings. Gauges and bars animate on every reading, so together they are almost
/// always moving; at WPF's default 60 fps that kept the render thread busy. Values that change once a
/// second look just as smooth at 30 fps, at roughly half the cost.
/// </summary>
public static class Motion
{
    public const int InstrumentFrameRate = 30;

    /// <summary>A value animation for gauges, bars and peak markers.</summary>
    public static DoubleAnimation Instrument(double to, TimeSpan duration, IEasingFunction? easing = null)
    {
        var animation = new DoubleAnimation(to, duration) { EasingFunction = easing };
        Timeline.SetDesiredFrameRate(animation, InstrumentFrameRate);
        return animation;
    }
}
