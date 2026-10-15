using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Pulse.ViewModels;

namespace Pulse.Views;

public partial class SensorsView : UserControl
{
    public SensorsView()
    {
        InitializeComponent();
        SizeChanged += (_, e) => FitToWidth(e.NewSize.Width);
    }

    /// <summary>Below 680 px the min and max columns go, so sensor names keep enough room.</summary>
    private void FitToWidth(double width)
    {
        var narrow = width < 680;
        Resources["RangeColumn"] = new GridLength(narrow ? 0 : 130);
        Resources["RangeVisibility"] = narrow ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>When the rename box appears, focus it with the name selected, ready to type over.</summary>
    private void RenameBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true || sender is not TextBox box)
            return;

        // Wait until layout has made the box focusable.
        Dispatcher.BeginInvoke(() =>
        {
            box.Focus();
            Keyboard.Focus(box);
            box.SelectAll();
        }, DispatcherPriority.Input);
    }

    /// <summary>Clicking away saves the rename, like renaming a file in Explorer.</summary>
    private void RenameBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox { DataContext: SensorRowViewModel { IsEditing: true } row })
            row.CommitRenameCommand.Execute(null);
    }
}
