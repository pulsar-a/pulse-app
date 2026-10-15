using System.Windows;
using System.Windows.Controls;

namespace Pulse.Controls;

/// <summary>
/// Equal-width items in one row, like a one-row UniformGrid, that wraps into more rows (still equal
/// widths, filled evenly) when the items would get narrower than <see cref="MinItemWidth"/>.
/// Used for the per-core strip, so 24 cores stay readable in a narrow window.
/// </summary>
public sealed class StripPanel : Panel
{
    public static readonly DependencyProperty MinItemWidthProperty = DependencyProperty.Register(
        nameof(MinItemWidth), typeof(double), typeof(StripPanel),
        new FrameworkPropertyMetadata(28.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty RowGapProperty = DependencyProperty.Register(
        nameof(RowGap), typeof(double), typeof(StripPanel),
        new FrameworkPropertyMetadata(10.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MinItemWidth
    {
        get => (double)GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    public double RowGap
    {
        get => (double)GetValue(RowGapProperty);
        set => SetValue(RowGapProperty, value);
    }

    /// <summary>How many items go in a row: all of them if they fit, otherwise split evenly over rows.</summary>
    public static int PerRow(int count, double width, double minItemWidth)
    {
        if (count == 0) return 1;
        var fit = Math.Max(1, (int)Math.Floor(width / Math.Max(1, minItemWidth)));
        if (fit >= count) return count;
        var rows = (int)Math.Ceiling(count / (double)fit);
        return (int)Math.Ceiling(count / (double)rows);
    }

    private int _perRow = 1;
    private double _rowHeight;

    protected override Size MeasureOverride(Size availableSize)
    {
        var count = InternalChildren.Count;
        var width = double.IsInfinity(availableSize.Width) ? count * MinItemWidth : availableSize.Width;
        _perRow = PerRow(count, width, MinItemWidth);
        var itemWidth = width / _perRow;

        _rowHeight = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(itemWidth, double.PositiveInfinity));
            _rowHeight = Math.Max(_rowHeight, child.DesiredSize.Height);
        }
        var rows = count == 0 ? 0 : (int)Math.Ceiling(count / (double)_perRow);
        return new Size(width, rows * _rowHeight + Math.Max(0, rows - 1) * RowGap);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var itemWidth = finalSize.Width / _perRow;
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var row = i / _perRow;
            var col = i % _perRow;
            InternalChildren[i].Arrange(new Rect(col * itemWidth, row * (_rowHeight + RowGap), itemWidth, _rowHeight));
        }
        return finalSize;
    }
}
