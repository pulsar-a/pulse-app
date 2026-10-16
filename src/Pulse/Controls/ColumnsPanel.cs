using System.Windows;
using System.Windows.Controls;

namespace Pulse.Controls;

/// <summary>
/// Equal-width columns in one row, as many as fit at <see cref="MinColumnWidth"/>; the children that don't
/// fit are left out (in order, so the most important goes first). Used by the Top processes tile: at full
/// width it shows CPU, GPU and memory side by side, in a half-width tile just the first two.
/// </summary>
public sealed class ColumnsPanel : Panel
{
    public static readonly DependencyProperty MinColumnWidthProperty = DependencyProperty.Register(
        nameof(MinColumnWidth), typeof(double), typeof(ColumnsPanel),
        new FrameworkPropertyMetadata(240.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ColumnGapProperty = DependencyProperty.Register(
        nameof(ColumnGap), typeof(double), typeof(ColumnsPanel),
        new FrameworkPropertyMetadata(24.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MinColumnWidth
    {
        get => (double)GetValue(MinColumnWidthProperty);
        set => SetValue(MinColumnWidthProperty, value);
    }

    public double ColumnGap
    {
        get => (double)GetValue(ColumnGapProperty);
        set => SetValue(ColumnGapProperty, value);
    }

    /// <summary>How many of <paramref name="count"/> columns fit in <paramref name="width"/>: at least one.</summary>
    public static int Fit(int count, double width, double minColumnWidth, double gap)
    {
        if (count == 0) return 0;
        var fit = (int)Math.Floor((width + gap) / (Math.Max(1, minColumnWidth) + gap));
        return Math.Clamp(fit, 1, count);
    }

    private int _shown;

    // The left-out columns are put just past the right edge, where the clip hides them (and their clicks).
    public ColumnsPanel() => ClipToBounds = true;

    protected override Size MeasureOverride(Size availableSize)
    {
        var count = InternalChildren.Count;
        var width = double.IsInfinity(availableSize.Width) ? count * (MinColumnWidth + ColumnGap) - ColumnGap : availableSize.Width;
        _shown = Fit(count, width, MinColumnWidth, ColumnGap);
        var columnWidth = _shown == 0 ? 0 : (width - (_shown - 1) * ColumnGap) / _shown;

        double height = 0;
        for (var i = 0; i < count; i++)
        {
            var child = InternalChildren[i];
            child.Measure(new Size(columnWidth, availableSize.Height));
            if (i < _shown)
                height = Math.Max(height, child.DesiredSize.Height);
        }
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columnWidth = _shown == 0 ? 0 : (finalSize.Width - (_shown - 1) * ColumnGap) / _shown;
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            InternalChildren[i].Arrange(i < _shown
                ? new Rect(i * (columnWidth + ColumnGap), 0, columnWidth, finalSize.Height)
                : new Rect(finalSize.Width + ColumnGap, 0, columnWidth, finalSize.Height));
        }
        return finalSize;
    }
}
