using System.Windows;
using Pulse.Controls;

namespace Pulse.Tests;

/// <summary>The drop position while dragging a dashboard tile, from the tiles' resting places.</summary>
public class TileDropTests
{
    // Two rows: four 300-wide gauges, then cores (800) and cooling (400).
    private static readonly (string Id, Rect Bounds)[] Tiles =
    [
        ("processor", new Rect(0, 0, 300, 400)),
        ("graphics", new Rect(300, 0, 300, 400)),
        ("memory", new Rect(600, 0, 300, 400)),
        ("power", new Rect(900, 0, 300, 400)),
        ("cores", new Rect(0, 400, 800, 300)),
        ("cooling", new Rect(800, 400, 400, 300)),
    ];

    [Fact]
    public void Left_half_of_a_tile_drops_before_it()
    {
        var target = TileDrop.Compute(Tiles, "power", new Point(650, 200));

        Assert.Equal("memory", target!.BeforeId);
        Assert.Equal(600, target.Marker.Left + TileDrop.MarkerWidth / 2);
    }

    [Fact]
    public void Right_half_drops_after_it()
    {
        var target = TileDrop.Compute(Tiles, "processor", new Point(1100, 300));

        Assert.Equal("cores", target!.BeforeId);
        Assert.Equal(1200, target.Marker.Left + TileDrop.MarkerWidth / 2);
    }

    [Fact]
    public void After_the_last_tile_means_the_end()
    {
        var target = TileDrop.Compute(Tiles, "processor", new Point(1150, 600));
        Assert.Null(target!.BeforeId);
    }

    [Fact]
    public void Over_itself_or_its_own_edges_nothing_moves()
    {
        Assert.Null(TileDrop.Compute(Tiles, "graphics", new Point(400, 100)));
        Assert.Null(TileDrop.Compute(Tiles, "graphics", new Point(250, 100)));   // right half of the tile before it
        Assert.Null(TileDrop.Compute(Tiles, "graphics", new Point(620, 100)));   // left half of the tile after it
    }

    [Fact]
    public void In_a_gap_or_below_everything_the_nearest_tile_counts()
    {
        var below = TileDrop.Compute(Tiles, "processor", new Point(100, 900));
        Assert.Equal("cores", below!.BeforeId);
    }

    [Fact]
    public void The_answer_depends_only_on_the_pointer()
    {
        // The tiles don't move during a drag, so the same spot always gives the same answer: no trembling.
        var first = TileDrop.Compute(Tiles, "cooling", new Point(320, 50));
        var again = TileDrop.Compute(Tiles, "cooling", new Point(320, 50));
        Assert.Equal(first, again);
        Assert.Equal("graphics", first!.BeforeId);
    }
}
