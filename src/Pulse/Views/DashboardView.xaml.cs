using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Pulse.Controls;
using Pulse.ViewModels;

namespace Pulse.Views;

public partial class DashboardView : UserControl
{
    private const string TileFormat = "Pulse.DashboardTile";

    private Point? _dragStart;

    public DashboardView() => InitializeComponent();

    private DashboardViewModel? Dashboard => DataContext as DashboardViewModel;

    // ---- Drag and drop (edit mode) ------------------------------------------------------------
    //
    // The tiles stay where they are while dragging. A copy of the card follows the pointer and a bar
    // shows where it will land, worked out from the tiles' resting places (TileDrop). The move happens
    // once, on drop, and then the tiles glide into their new places.

    private TileDragAdorner? _dragAdorner;
    private TileDropTarget? _dropTarget;
    private bool _dropped;

    private void Tile_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _dragStart = e.GetPosition(this);

    private void Tile_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragStart is not { } start
            || sender is not FrameworkElement { DataContext: DashboardTileViewModel tile } source
            || TileList.ItemContainerGenerator.ContainerFromItem(tile) is not FrameworkElement container)
            return;

        var moved = e.GetPosition(this) - start;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _dragStart = null;
        _dropTarget = null;
        _dropped = false;

        var layer = AdornerLayer.GetAdornerLayer(TileList);
        _dragAdorner = new TileDragAdorner(TileList, CardOf(container), e.GetPosition(container), (Brush)FindResource("Pulse.Info"),
            (Brush)FindResource("Pulse.PopupBackground"), (Brush)FindResource("Pulse.CardBorderHover"));
        _dragAdorner.Update(e.GetPosition(TileList), null);
        layer?.Add(_dragAdorner);
        tile.IsDragging = true;
        try
        {
            DragDrop.DoDragDrop(source, new DataObject(TileFormat, tile), DragDropEffects.Move);
        }
        finally
        {
            layer?.Remove(_dragAdorner);
            _dragAdorner = null;
            tile.IsDragging = false;
            if (_dropped && _dropTarget is { } target)
                Dashboard?.MoveTile(tile.Id, target.BeforeId);
            _dropTarget = null;
        }
    }

    /// <summary>The card inside a tile, without the edit-mode veil on top (the template's first layer).</summary>
    private static FrameworkElement CardOf(FrameworkElement container) =>
        VisualTreeHelper.GetChildrenCount(container) > 0 && VisualTreeHelper.GetChild(container, 0) is Grid { Children.Count: > 0 } root
            && root.Children[0] is FrameworkElement card
            ? card
            : container;

    private void TileList_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(TileFormat) is not DashboardTileViewModel dragged || Dashboard is not { } dashboard)
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        var pointer = e.GetPosition(TileList);
        _dropTarget = TileDrop.Compute(TileBounds(dashboard), dragged.Id, pointer);
        _dragAdorner?.Update(pointer, _dropTarget?.Marker);
        e.Effects = DragDropEffects.Move;
        AutoScroll(e);
    }

    /// <summary>Leaving the tiles cancels the drop position, so letting go elsewhere changes nothing.</summary>
    private void TileList_DragLeave(object sender, DragEventArgs e)
    {
        var pointer = e.GetPosition(TileList);
        if (new Rect(TileList.RenderSize).Contains(pointer)) return;
        _dropTarget = null;
        _dragAdorner?.Update(pointer, null);
    }

    private void TileList_Drop(object sender, DragEventArgs e)
    {
        _dropped = true;
        e.Handled = true;
    }

    /// <summary>Each visible tile's place in the list, as laid out (nothing glides during a drag).</summary>
    private List<(string Id, Rect Bounds)> TileBounds(DashboardViewModel dashboard)
    {
        var bounds = new List<(string, Rect)>();
        foreach (var tile in dashboard.Tiles)
        {
            if (TileList.ItemContainerGenerator.ContainerFromItem(tile) is FrameworkElement container && container.IsVisible)
                bounds.Add((tile.Id, container.TransformToAncestor(TileList).TransformBounds(new Rect(container.RenderSize))));
        }
        return bounds;
    }

    /// <summary>Scrolls when the pointer nears the top or bottom edge, so a card can go anywhere.</summary>
    private void AutoScroll(DragEventArgs e)
    {
        const double edge = 60, step = 18;
        var y = e.GetPosition(TilesScroll).Y;
        if (y < edge)
            TilesScroll.ScrollToVerticalOffset(TilesScroll.VerticalOffset - step);
        else if (y > TilesScroll.ActualHeight - edge)
            TilesScroll.ScrollToVerticalOffset(TilesScroll.VerticalOffset + step);
    }

    // ---- Size menu ----------------------------------------------------------------------------

    private void SizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { ContextMenu: { } menu } button) return;
        menu.DataContext = button.DataContext;
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    // ---- Naming a pinned group ----------------------------------------------------------------

    private void GroupTitle_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => SaveGroupTitle(sender);

    private void GroupTitle_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        SaveGroupTitle(sender);
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    private void SaveGroupTitle(object sender)
    {
        if (sender is TextBox { Tag: string tileId } box)
            Dashboard?.RenameTile(tileId, box.Text);
    }
}
