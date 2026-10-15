using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Pulse.Controls;

/// <summary>
/// Lays out dashboard tiles on a column grid, left to right, starting a new row when the next tile
/// doesn't fit. Each tile is <see cref="SpanProperty"/> columns wide; a row that isn't full is stretched
/// so the tiles in it fill the width (hide a card and its neighbours widen instead of leaving a hole).
/// All tiles in a row share the row's height, like cards in a grid row.
///
/// Responsive: each tile can say how narrow it may get (<see cref="MinTileWidthProperty"/>). When the
/// panel is too narrow for that, the tile takes more columns, so in a small window four gauges become
/// three, then two, then one per row, and nothing is ever squeezed or clipped.
///
/// With <see cref="AnimateMoves"/> on (edit mode), tiles glide from their old place and size to the new
/// ones when the order or sizes change: a resized tile grows or shrinks smoothly while its neighbours
/// move aside, instead of everything jumping at once.
/// </summary>
public sealed class TileFlowPanel : Panel
{
    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(
        nameof(Columns), typeof(int), typeof(TileFlowPanel),
        new FrameworkPropertyMetadata(12, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty SpanProperty = DependencyProperty.RegisterAttached(
        "Span", typeof(int), typeof(TileFlowPanel),
        new FrameworkPropertyMetadata(3, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    /// <summary>The narrowest a tile may be, in pixels; it takes extra columns rather than get narrower.</summary>
    public static readonly DependencyProperty MinTileWidthProperty = DependencyProperty.RegisterAttached(
        "MinTileWidth", typeof(double), typeof(TileFlowPanel),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    public static readonly DependencyProperty AnimateMovesProperty = DependencyProperty.Register(
        nameof(AnimateMoves), typeof(bool), typeof(TileFlowPanel), new PropertyMetadata(false));

    public TileFlowPanel() => Unloaded += (_, _) => SetTicking(false);

    public int Columns
    {
        get => (int)GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    public bool AnimateMoves
    {
        get => (bool)GetValue(AnimateMovesProperty);
        set => SetValue(AnimateMovesProperty, value);
    }

    public static int GetSpan(DependencyObject element) => (int)element.GetValue(SpanProperty);
    public static void SetSpan(DependencyObject element, int value) => element.SetValue(SpanProperty, value);

    public static double GetMinTileWidth(DependencyObject element) => (double)element.GetValue(MinTileWidthProperty);
    public static void SetMinTileWidth(DependencyObject element, double value) => element.SetValue(MinTileWidthProperty, value);

    private static readonly TimeSpan GlideDuration = TimeSpan.FromMilliseconds(280);
    private static readonly IEasingFunction GlideEase = new CubicEase { EasingMode = EasingMode.EaseInOut };

    private readonly ConditionalWeakTable<UIElement, Placement> _placements = new();
    private bool _ticking;

    /// <summary>Where a tile is drawn now, and where it's heading.</summary>
    private sealed class Placement
    {
        public Rect? Shown;
        public Rect From;
        public Rect To;
        public long StartTicks;
    }
    private List<Row> _rows = [];

    private sealed record Row(List<(UIElement Child, double Width)> Tiles)
    {
        public double Height { get; set; }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 1200 : availableSize.Width;
        _rows = BuildRows(width);

        double total = 0;
        foreach (var row in _rows)
        {
            row.Height = 0;
            foreach (var (child, w) in row.Tiles)
            {
                child.Measure(new Size(w, double.PositiveInfinity));
                row.Height = Math.Max(row.Height, child.DesiredSize.Height);
            }
            total += row.Height;
        }
        return new Size(width, total);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        // The width can differ from the measured one; recompute widths but keep the measured heights.
        var rows = BuildRows(finalSize.Width);
        var now = Environment.TickCount64;
        var moving = false;
        var y = 0.0;
        for (var r = 0; r < rows.Count; r++)
        {
            var height = r < _rows.Count ? _rows[r].Height : rows[r].Tiles.Max(t => t.Child.DesiredSize.Height);
            var x = 0.0;
            foreach (var (child, w) in rows[r].Tiles)
            {
                moving |= Place(child, new Rect(x, y, w, height), now);
                x += w;
            }
            y += height;
        }

        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility != Visibility.Collapsed) continue;
            child.Arrange(new Rect());
            _placements.Remove(child);
        }

        SetTicking(moving);
        return finalSize;
    }

    /// <summary>Groups the visible children into rows and works out each tile's width.</summary>
    private List<Row> BuildRows(double width)
    {
        var children = InternalChildren.Cast<UIElement>().Where(c => c.Visibility != Visibility.Collapsed).ToList();
        var plan = Plan(children.Select(c => (GetSpan(c), GetMinTileWidth(c))).ToList(), width, Columns);
        return plan.Select(row => new Row(row.Select(t => (children[t.Index], t.Width)).ToList())).ToList();
    }

    /// <summary>
    /// The layout itself, as plain numbers: which tiles share a row and how wide each one is.
    /// A tile gets its own span, or more columns if that would make it narrower than its minimum.
    /// A new row starts when the next tile doesn't fit; then the row is stretched to the full width.
    /// </summary>
    public static List<List<(int Index, double Width)>> Plan(IReadOnlyList<(int Span, double MinWidth)> tiles, double width, int columns)
    {
        columns = Math.Max(1, columns);
        var column = width / columns;
        var rows = new List<List<(int, double)>>();
        var current = new List<(int Index, int Span)>();
        var used = 0;

        void Close()
        {
            if (current.Count == 0) return;
            var scale = width / current.Sum(t => t.Span);
            rows.Add(current.Select(t => (t.Index, t.Span * scale)).ToList());
            current = [];
            used = 0;
        }

        for (var i = 0; i < tiles.Count; i++)
        {
            var (span, minWidth) = tiles[i];
            var needed = column > 0 ? (int)Math.Ceiling(minWidth / column - 1e-9) : columns;
            span = Math.Clamp(Math.Max(span, needed), 1, columns);
            if (used + span > columns) Close();
            current.Add((i, span));
            used += span;
        }
        Close();
        return rows;
    }

    /// <summary>
    /// Arranges a tile at its target, or part of the way there while it glides (edit mode only).
    /// A new target mid-glide starts from wherever the tile is right now, so quick changes stay smooth.
    /// Returns true while the tile is still moving.
    /// </summary>
    private bool Place(UIElement child, Rect target, long now)
    {
        var placement = _placements.GetOrCreateValue(child);
        if (!AnimateMoves || placement.Shown is not { } shown)
        {
            placement.Shown = placement.From = placement.To = target;
            child.Arrange(target);
            return false;
        }

        if (!Same(placement.To, target))
        {
            placement.From = shown;
            placement.To = target;
            placement.StartTicks = now;
        }

        var t = Math.Clamp((now - placement.StartTicks) / GlideDuration.TotalMilliseconds, 0, 1);
        var rect = t >= 1 ? target : Lerp(placement.From, target, GlideEase.Ease(t));
        placement.Shown = rect;
        child.Arrange(rect);
        return t < 1;
    }

    /// <summary>Re-arranges every frame while something is gliding, and stops as soon as nothing is.</summary>
    private void SetTicking(bool on)
    {
        if (on == _ticking) return;
        _ticking = on;
        if (on) CompositionTarget.Rendering += OnRendering;
        else CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e) => InvalidateArrange();

    private static Rect Lerp(Rect a, Rect b, double t) => new(
        a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t,
        Math.Max(0, a.Width + (b.Width - a.Width) * t), Math.Max(0, a.Height + (b.Height - a.Height) * t));

    private static bool Same(Rect a, Rect b) =>
        Math.Abs(a.X - b.X) < 0.5 && Math.Abs(a.Y - b.Y) < 0.5 && Math.Abs(a.Width - b.Width) < 0.5 && Math.Abs(a.Height - b.Height) < 0.5;
}
