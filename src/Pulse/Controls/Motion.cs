using System.ComponentModel;
using System.Windows;
using System.Windows.Media.Animation;

namespace Pulse.Controls;

/// <summary>
/// Shared animation settings.
///
/// Respects Windows' "Animation effects" setting (Settings → Accessibility → Visual effects): when it's
/// off, nothing in Pulse moves. Gauges and bars jump to their values, panels appear without sliding, the
/// splash shows its trace without beating, and numbers change without rolling. Every animation in Pulse
/// goes through <see cref="Enabled"/> (code) or <see cref="Current"/> (XAML bindings).
///
/// Gauges and bars animate on every reading, so together they are almost always moving; at WPF's default
/// 60 fps that kept the render thread busy. Values that change once a second look just as smooth at 30 fps,
/// at roughly half the cost.
/// </summary>
public sealed class Motion : INotifyPropertyChanged
{
    public const int InstrumentFrameRate = 30;

    private static bool? _override;

    private Motion()
    {
        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SystemParameters.ClientAreaAnimation))
                Raise();
        };
    }

    /// <summary>For XAML: <c>{Binding AnimationsEnabled, Source={x:Static controls:Motion.Current}}</c>.</summary>
    public static Motion Current { get; } = new();

    /// <summary>Windows' "Animation effects" setting (or a test's override).</summary>
    public static bool Enabled => _override ?? SystemParameters.ClientAreaAnimation;

    public bool AnimationsEnabled => Enabled;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised when the setting changes, for code that started something long-running (the splash heartbeat).</summary>
    public static event Action? Changed;

    /// <summary>Tests and screenshots force motion on or off; null follows Windows again.</summary>
    public static void Override(bool? enabled)
    {
        _override = enabled;
        Current.Raise();
    }

    private void Raise()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AnimationsEnabled)));
        Changed?.Invoke();
    }

    /// <summary>The duration to use: the given one, or zero (an instant change) with animations off.</summary>
    public static TimeSpan Duration(TimeSpan duration) => Enabled ? duration : TimeSpan.Zero;

    /// <summary>A value animation for gauges, bars and peak markers. Instant with animations off.</summary>
    public static DoubleAnimation Instrument(double to, TimeSpan duration, IEasingFunction? easing = null)
    {
        var animation = new DoubleAnimation(to, Duration(duration)) { EasingFunction = easing };
        Timeline.SetDesiredFrameRate(animation, InstrumentFrameRate);
        return animation;
    }

    /// <summary>A one-off UI animation (panels, pages, pop-ups). Instant with animations off.</summary>
    public static DoubleAnimation Ui(double? from, double to, TimeSpan duration, IEasingFunction? easing = null) =>
        new() { From = Enabled ? from : null, To = to, Duration = Duration(duration), EasingFunction = easing };
}
