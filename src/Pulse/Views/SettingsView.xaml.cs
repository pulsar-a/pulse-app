using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Pulse.ViewModels;

namespace Pulse.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        // Tunnelling, so the keys reach the recorder before buttons or Tab navigation use them.
        PreviewKeyDown += OnPreviewKeyDown;
        // Leaving the page mid-recording cancels it, so the old shortcut isn't left switched off.
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is false && DataContext is SettingsViewModel { IsRecordingShortcut: true } vm)
                vm.SetShortcutCommand.Execute(null);
        };
        DataContextChanged += (_, _) =>
        {
            if (DataContext is SettingsViewModel vm)
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(SettingsViewModel.IsRecordingShortcut))
                        ShowRecording(vm.IsRecordingShortcut);
                };
        };
    }

    /// <summary>While recording a shortcut, every key goes to the recorder (Alt arrives as a "system" key).</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not SettingsViewModel { IsRecordingShortcut: true } vm) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        e.Handled = vm.RecordShortcutKey(Keyboard.Modifiers, key);
    }

    /// <summary>Highlights the box while it listens, and gives it the keyboard so keys land here.</summary>
    private void ShowRecording(bool recording)
    {
        ShortcutBox.BorderBrush = (Brush)FindResource(recording ? "Pulse.Info" : "Pulse.CardBorder");
        if (recording)
            Keyboard.Focus(ShortcutBox);
    }
}
