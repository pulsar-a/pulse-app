using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Pulse.ViewModels;

namespace Pulse.Views;

public partial class SensorDetailsView : UserControl
{
    public SensorDetailsView() => InitializeComponent();

    /// <summary>When the rename box appears, focus it with the name selected.</summary>
    private void NameBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
            Dispatcher.BeginInvoke(() =>
            {
                NameBox.Focus();
                Keyboard.Focus(NameBox);
                NameBox.SelectAll();
            }, DispatcherPriority.Input);
    }

    /// <summary>
    /// Pin, or unpin when already pinned. With pinned tiles on the dashboard, a menu asks whether the
    /// sensor gets its own tile or joins one of them.
    /// </summary>
    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SensorDetailsViewModel details) return;
        if (details.IsPinned)
        {
            details.UnpinCommand.Execute(null);
            return;
        }
        if (details.PinChoices.Count <= 1)
        {
            details.PinToCommand.Execute(null);
            return;
        }

        var menu = PinButton.ContextMenu!;
        menu.DataContext = details;
        menu.PlacementTarget = PinButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>Clicking away saves the rename.</summary>
    private void NameBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (DataContext is SensorDetailsViewModel { IsEditingName: true } details)
            details.CommitRenameCommand.Execute(null);
    }
}
