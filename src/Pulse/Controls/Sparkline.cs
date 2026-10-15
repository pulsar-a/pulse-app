using System.Windows;
using System.Windows.Media;

namespace Pulse.Controls;

/// <summary>
/// A small area chart of recent values. New samples enter from the right, so a short history
/// fills only the right side of the chart instead of being stretched.
/// </summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<double>), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(
        nameof(Color), typeof(Color), typeof(Sparkline),
        new FrameworkPropertyMetadata(Colors.DeepSkyBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CapacityProperty = DependencyProperty.Register(
        nameof(Capacity), typeof(int), typeof(Sparkline),
        new FrameworkPropertyMetadata(60, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Fixed top of the scale; NaN scales to the largest visible value.</summary>
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Values
    {
        get => (IReadOnlyList<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public Color Color
    {
        get => (Color)GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    public int Capacity
    {
        get => (int)GetValue(CapacityProperty);
        set => SetValue(CapacityProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        // Transparent background so the whole area is hit-testable for tooltips.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));

        var values = Values;
        if (values is null || values.Count < 2 || ActualWidth <= 0 || ActualHeight <= 0)
            return;

        var max = double.IsNaN(Maximum) ? values.Max() * 1.15 : Maximum;
        if (max <= 0) max = 1;

        var height = ActualHeight - 2;
        var step = ActualWidth / Math.Max(Capacity - 1, 1);
        var points = new Point[values.Count];
        for (var i = 0; i < values.Count; i++)
        {
            var x = ActualWidth - (values.Count - 1 - i) * step;
            var y = 1 + height - Math.Clamp(values[i] / max, 0, 1) * height;
            points[i] = new Point(x, y);
        }

        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(points[0], false, false);
            ctx.PolyLineTo(points[1..], true, true);
        }
        line.Freeze();

        var area = new StreamGeometry();
        using (var ctx = area.Open())
        {
            ctx.BeginFigure(new Point(points[0].X, ActualHeight), true, true);
            ctx.PolyLineTo(points, false, true);
            ctx.LineTo(new Point(points[^1].X, ActualHeight), false, true);
        }
        area.Freeze();

        var fill = new LinearGradientBrush(
            Color.FromArgb(0x55, Color.R, Color.G, Color.B),
            Color.FromArgb(0x00, Color.R, Color.G, Color.B),
            90);
        dc.DrawGeometry(fill, null, area);
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color), 1.6) { LineJoin = PenLineJoin.Round }, line);
    }
}
