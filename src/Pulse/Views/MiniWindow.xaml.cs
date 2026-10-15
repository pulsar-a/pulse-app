using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Pulse.Services;
using Pulse.ViewModels;

namespace Pulse.Views;

/// <summary>
/// The mini window itself: a borderless, always-on-top readout. It never takes focus (clicking it
/// doesn't pull you out of a game), stays out of Alt+Tab and the taskbar, and can let clicks through.
/// Where it opens and what it does on its menu is up to <see cref="MiniController"/>.
/// </summary>
public partial class MiniWindow : Window
{
    private readonly SettingsService _settings;

    public MiniWindow(MiniViewModel viewModel, SettingsService settings)
    {
        _settings = settings;
        DataContext = viewModel;
        InitializeComponent();
    }

    public IntPtr Handle { get; private set; }

    /// <summary>Raised after the user drags it somewhere new.</summary>
    public event Action? Moved;

    public event Action? SettingsRequested;
    public event Action? CloseRequested;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Handle = new WindowInteropHelper(this).Handle;
        // No focus stealing, no Alt+Tab entry.
        NativeWindows.SetExtendedStyle(Handle, NativeWindows.WS_EX_NOACTIVATE | NativeWindows.WS_EX_TOOLWINDOW, true);
        ApplyClickThrough(_settings.Current.Mini.ClickThrough);
    }

    public void ApplyClickThrough(bool on)
    {
        if (Handle != IntPtr.Zero)
            NativeWindows.SetExtendedStyle(Handle, NativeWindows.WS_EX_TRANSPARENT, on);
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        DragMove();
        Moved?.Invoke();
    }

    private void Menu_Opened(object sender, RoutedEventArgs e) => HorizontalItem.IsChecked = _settings.Current.Mini.Horizontal;

    private void Horizontal_Click(object sender, RoutedEventArgs e) =>
        _settings.Update(s => s.Mini.Horizontal = HorizontalItem.IsChecked);

    private void Opacity_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag } && double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var opacity))
            _settings.Update(s => s.Mini.Opacity = opacity);
    }

    private void ClickThrough_Click(object sender, RoutedEventArgs e) => _settings.Update(s => s.Mini.ClickThrough = true);

    private void Settings_Click(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
}
