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

    /// <summary>Clicking away saves the rename.</summary>
    private void NameBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (DataContext is SensorDetailsViewModel { IsEditingName: true } details)
            details.CommitRenameCommand.Execute(null);
    }
}
