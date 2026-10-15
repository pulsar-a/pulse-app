using System.Windows;
using System.Windows.Media.Animation;

namespace Pulse.Controls;

/// <summary>
/// VU-meter style peak hold: the peak marker jumps up with the value, stays at the highest
/// recent level for <see cref="Hold"/>, then drops back to the current value with a gravity-like ease.
/// </summary>
internal sealed class PeakHold(UIElement target, DependencyProperty peakProperty)
{
    public static readonly TimeSpan Hold = TimeSpan.FromSeconds(1.6);
    private static readonly TimeSpan Rise = TimeSpan.FromMilliseconds(450);
    private static readonly TimeSpan Fall = TimeSpan.FromMilliseconds(650);

    private double _peak;
    private DateTime _heldUntil;

    public void Update(double value)
    {
        var now = DateTime.UtcNow;

        if (value >= _peak)
        {
            // New high: the marker rides up with the bar and starts a fresh hold.
            _peak = value;
            _heldUntil = now + Hold;
            target.BeginAnimation(peakProperty, Motion.Instrument(value, Rise, new CubicEase { EasingMode = EasingMode.EaseOut }));
            return;
        }

        var holdLeft = _heldUntil - now;
        var fall = Motion.Instrument(value, Fall, new QuadraticEase { EasingMode = EasingMode.EaseIn });
        fall.BeginTime = holdLeft > TimeSpan.Zero ? holdLeft : TimeSpan.Zero;
        target.BeginAnimation(peakProperty, fall);

        // Once the hold is over, the peak tracks the value until it rises again.
        if (holdLeft <= TimeSpan.Zero)
            _peak = value;
    }
}
