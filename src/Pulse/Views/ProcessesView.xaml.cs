using System.Windows;
using System.Windows.Controls;
using Pulse.ViewModels;
using Wpf.Ui.Controls;

namespace Pulse.Views;

public partial class ProcessesView : UserControl
{
    public ProcessesView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is ProcessesViewModel processes)
                processes.Confirm = ConfirmEndAsync;
        };
    }

    /// <summary>End task always asks first: ending a program loses whatever it hasn't saved.</summary>
    private async Task<bool> ConfirmEndAsync(ProcessRowViewModel row)
    {
        var count = row.App?.Count ?? 1;
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = $"End {row.Name}?",
            Content = count > 1
                ? $"All {count} of its processes will close straight away. Anything it hasn't saved will be lost."
                : "It will close straight away. Anything it hasn't saved will be lost.",
            PrimaryButtonText = "End task",
            PrimaryButtonAppearance = ControlAppearance.Danger,
            CloseButtonText = "Cancel",
            Owner = Window.GetWindow(this),
        };
        return await box.ShowDialogAsync() == Wpf.Ui.Controls.MessageBoxResult.Primary;
    }
}
