using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Pulse.Controls;

/// <summary>
/// A skeleton placeholder: a soft block with a highlight sweeping across it, shown while data loads.
/// </summary>
public sealed class Shimmer : Border
{
    // Defaults match the dark theme; Themes/Styles.xaml sets them from theme tokens.
    public static readonly DependencyProperty BaseColorProperty = DependencyProperty.Register(
        nameof(BaseColor), typeof(Color), typeof(Shimmer),
        new PropertyMetadata(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF), (d, _) => ((Shimmer)d).UpdateColors()));

    public static readonly DependencyProperty HighlightColorProperty = DependencyProperty.Register(
        nameof(HighlightColor), typeof(Color), typeof(Shimmer),
        new PropertyMetadata(Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF), (d, _) => ((Shimmer)d).UpdateColors()));

    private readonly GradientStop[] _baseStops;
    private readonly GradientStop _highlightStop;

    public Shimmer()
    {
        _baseStops = [new(BaseColor, 0.0), new(BaseColor, 0.35), new(BaseColor, 0.65), new(BaseColor, 1.0)];
        _highlightStop = new GradientStop(HighlightColor, 0.5);

        var sweep = new TranslateTransform(-1, 0);
        Background = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5),
            MappingMode = BrushMappingMode.RelativeToBoundingBox,
            RelativeTransform = sweep,
            GradientStops = [_baseStops[0], _baseStops[1], _highlightStop, _baseStops[2], _baseStops[3]],
        };

        // Sliding the gradient from one width left to one width right carries the highlight band
        // from off the left edge to off the right edge; outside the gradient the base color pads it.
        // With Windows animations off, placeholders stay still (the base color alone still reads as "loading").
        Loaded += (_, _) => sweep.BeginAnimation(TranslateTransform.XProperty, Motion.Enabled ? Sweep() : null);
        Unloaded += (_, _) => sweep.BeginAnimation(TranslateTransform.XProperty, null);
        IsVisibleChanged += (_, e) =>
        {
            // Don't animate placeholders nobody can see.
            if (e.NewValue is false)
                sweep.BeginAnimation(TranslateTransform.XProperty, null);
            else if (IsLoaded && Motion.Enabled)
                sweep.BeginAnimation(TranslateTransform.XProperty, Sweep());
        };
    }

    private static DoubleAnimation Sweep()
    {
        var sweep = new DoubleAnimation(-1, 1, TimeSpan.FromSeconds(1.4)) { RepeatBehavior = RepeatBehavior.Forever };
        Timeline.SetDesiredFrameRate(sweep, Motion.InstrumentFrameRate);
        return sweep;
    }

    public Color BaseColor
    {
        get => (Color)GetValue(BaseColorProperty);
        set => SetValue(BaseColorProperty, value);
    }

    public Color HighlightColor
    {
        get => (Color)GetValue(HighlightColorProperty);
        set => SetValue(HighlightColorProperty, value);
    }

    private void UpdateColors()
    {
        // Called from the property metadata before the constructor has created the stops.
        if (_baseStops is null) return;
        foreach (var stop in _baseStops)
            stop.Color = BaseColor;
        _highlightStop.Color = HighlightColor;
    }
}
