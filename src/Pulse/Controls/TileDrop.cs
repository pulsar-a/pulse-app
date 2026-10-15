using System.Windows;

namespace Pulse.Controls;

/// <summary>Where a dragged tile would land: before <see cref="BeforeId"/> (null = at the end), shown by <see cref="Marker"/>.</summary>
public sealed record TileDropTarget(string? BeforeId, Rect Marker);

/// <summary>
/// Works out the drop position for a dragged dashboard tile from where the tiles sit while nothing moves.
/// The tiles stay still during the drag, so the answer only depends on the pointer: no feedback loop
/// between moving tiles and the tile under the pointer, which is what made live reordering tremble.
/// </summary>
public static class TileDrop
{
    public const double MarkerWidth = 4;

    /// <param name="tiles">Every tile's id and bounds, in order.</param>
    /// <returns>The drop position, or null when the tile would stay where it is.</returns>
    public static TileDropTarget? Compute(IReadOnlyList<(string Id, Rect Bounds)> tiles, string draggedId, Point pointer)
    {
        if (tiles.Count == 0) return null;

        // The tile under the pointer, or the nearest one when it's in a gap or below the last row.
        var index = 0;
        var best = double.MaxValue;
        for (var i = 0; i < tiles.Count; i++)
        {
            var distance = Distance(tiles[i].Bounds, pointer);
            if (distance < best)
            {
                best = distance;
                index = i;
            }
        }

        var target = tiles[index];
        if (target.Id == draggedId) return null;

        var after = pointer.X > target.Bounds.Left + target.Bounds.Width / 2;
        var beforeIndex = after ? index + 1 : index;
        var draggedIndex = -1;
        for (var i = 0; i < tiles.Count; i++)
        {
            if (tiles[i].Id == draggedId) draggedIndex = i;
        }

        // Dropping right before or right after itself changes nothing.
        if (beforeIndex == draggedIndex || beforeIndex == draggedIndex + 1)
            return null;

        var b = target.Bounds;
        var x = after ? b.Right : b.Left;
        var marker = new Rect(x - MarkerWidth / 2, b.Top + 14, MarkerWidth, Math.Max(0, b.Height - 28));
        return new TileDropTarget(beforeIndex < tiles.Count ? tiles[beforeIndex].Id : null, marker);
    }

    private static double Distance(Rect rect, Point p)
    {
        var dx = Math.Max(Math.Max(rect.Left - p.X, 0), p.X - rect.Right);
        var dy = Math.Max(Math.Max(rect.Top - p.Y, 0), p.Y - rect.Bottom);
        return dx * dx + dy * dy;
    }
}
