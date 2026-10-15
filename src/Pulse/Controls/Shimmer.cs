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
    private static readonly Color BaseColor = Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF);
    private static readonly Color HighlightColor = Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF);

    public Shimmer()
    {
        var sweep = new TranslateTransform(-1, 0);
        Background = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5),
            MappingMode = BrushMappingMode.RelativeToBoundingBox,
            RelativeTransform = sweep,
            GradientStops =
            {
                new GradientStop(BaseColor, 0.0),
                new GradientStop(BaseColor, 0.35),
                new GradientStop(HighlightColor, 0.5),
                new GradientStop(BaseColor, 0.65),
                new GradientStop(BaseColor, 1.0),
            },
        };

        // Sliding the gradient from one width left to one width right carries the highlight band
        // from off the left edge to off the right edge; outside the gradient the base color pads it.
        Loaded += (_, _) => sweep.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(-1, 1, TimeSpan.FromSeconds(1.4)) { RepeatBehavior = RepeatBehavior.Forever });
        Unloaded += (_, _) => sweep.BeginAnimation(TranslateTransform.XProperty, null);
        IsVisibleChanged += (_, e) =>
        {
            // Don't animate placeholders nobody can see.
            if (e.NewValue is false)
                sweep.BeginAnimation(TranslateTransform.XProperty, null);
            else if (IsLoaded)
                sweep.BeginAnimation(TranslateTransform.XProperty,
                    new DoubleAnimation(-1, 1, TimeSpan.FromSeconds(1.4)) { RepeatBehavior = RepeatBehavior.Forever });
        };
    }
}
