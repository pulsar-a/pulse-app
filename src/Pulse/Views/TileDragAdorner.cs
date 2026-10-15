using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace Pulse.Views;

/// <summary>
/// Drag feedback over the dashboard tiles: a see-through copy of the dragged card that follows the
/// pointer, and a bar where it will land. Draws only; it never takes the mouse.
/// </summary>
internal sealed class TileDragAdorner : Adorner
{
    private const double GhostScale = 0.55;

    private readonly ImageBrush _ghost;
    private readonly Size _ghostSize;
    private readonly Vector _grab;
    private readonly Brush _markerBrush;
    private readonly Brush _backing;
    private readonly Pen _outline;
    private Point _pointer;
    private Rect? _marker;

    /// <param name="grab">Where on the card it was picked up, so the copy stays under the same spot.</param>
    /// <param name="backing">A solid surface under the copy: cards are see-through (they sit on Mica), and the
    /// card underneath would show through the copy without it.</param>
    public TileDragAdorner(UIElement adorned, FrameworkElement card, Point grab, Brush markerBrush, Brush backing, Brush outline)
        : base(adorned)
    {
        IsHitTestVisible = false;
        _backing = backing;
        _outline = new Pen(outline, 1);
        _outline.Freeze();
        _ghost = new ImageBrush(Snapshot(card)) { Stretch = Stretch.Fill };
        _ghost.Freeze();
        _ghostSize = new Size(card.ActualWidth * GhostScale, card.ActualHeight * GhostScale);
        _grab = new Vector(grab.X * GhostScale, grab.Y * GhostScale);
        _markerBrush = markerBrush;
    }

    /// <summary>A still picture of the card as it looked when picked up (before it fades in place).</summary>
    private static ImageSource Snapshot(FrameworkElement card)
    {
        var dpi = VisualTreeHelper.GetDpi(card);
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(card.ActualWidth * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Ceiling(card.ActualHeight * dpi.DpiScaleY)),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            dc.DrawRectangle(new VisualBrush(card), null, new Rect(0, 0, card.ActualWidth, card.ActualHeight));
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    public void Update(Point pointer, Rect? marker)
    {
        _pointer = pointer;
        _marker = marker;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_marker is { } m)
            dc.DrawRoundedRectangle(_markerBrush, null, m, m.Width / 2, m.Width / 2);

        var rect = new Rect(_pointer - _grab, _ghostSize);
        dc.PushOpacity(0.92);
        dc.DrawRoundedRectangle(_backing, _outline, rect, 10, 10);
        dc.DrawRoundedRectangle(_ghost, null, rect, 10, 10);
        dc.Pop();
    }
}
