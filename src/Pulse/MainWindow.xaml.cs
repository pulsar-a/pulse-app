using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Pulse.ViewModels;
using Wpf.Ui.Controls;

namespace Pulse;

public partial class MainWindow : FluentWindow
{
    /// <summary>Show at least one full heartbeat, even when the first readings arrive instantly.</summary>
    private static readonly TimeSpan MinimumSplash = TimeSpan.FromSeconds(1.5);

    /// <summary>If the hardware takes this long, drop the splash and show the skeleton UI instead.</summary>
    private static readonly TimeSpan MaximumSplash = TimeSpan.FromSeconds(6);

    private readonly Stopwatch _sinceLaunch = Stopwatch.StartNew();
    private readonly DispatcherTimer _splashTimeout;
    private bool _splashDismissed;

    public MainWindow(MainViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();

        viewModel.PropertyChanged += OnViewModelPropertyChanged;

        _splashTimeout = new DispatcherTimer { Interval = MaximumSplash };
        _splashTimeout.Tick += (_, _) => DismissSplash();
        _splashTimeout.Start();

        DashboardPage.IsVisibleChanged += (_, e) => { if (e.NewValue is true) PlayEntrance(DashboardPage); };
        SensorsPage.IsVisibleChanged += (_, e) => { if (e.NewValue is true) PlayEntrance(SensorsPage); };
        SettingsPage.IsVisibleChanged += (_, e) => { if (e.NewValue is true) PlayEntrance(SettingsPage); };
    }

    private async void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.IsLoading) || ((MainViewModel)DataContext).IsLoading)
            return;

        var remaining = MinimumSplash - _sinceLaunch.Elapsed;
        if (remaining > TimeSpan.Zero)
            await Task.Delay(remaining);
        DismissSplash();
    }

    private void DismissSplash()
    {
        if (_splashDismissed)
            return;
        _splashDismissed = true;
        _splashTimeout.Stop();

        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(380)) { EasingFunction = ease };
        fade.Completed += (_, _) =>
        {
            Splash.Visibility = Visibility.Collapsed;
            Splash.BeginAnimation(OpacityProperty, null);
            // Stop the endless heartbeat so a hidden splash doesn't keep animating.
            HeartbeatStory.Storyboard.Remove(Splash);
        };

        // The logo swells slightly as it fades, like the screen breathing out.
        var grow = new DoubleAnimation(1.25, TimeSpan.FromMilliseconds(380)) { EasingFunction = ease };
        var scale = (ScaleTransform)SplashLogo.RenderTransform;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        Splash.BeginAnimation(OpacityProperty, fade);

        if (DashboardPage.IsVisible)
            PlayEntrance(DashboardPage);
    }

    /// <summary>Fades a page in while it rises a few pixels into place.</summary>
    private static void PlayEntrance(FrameworkElement page)
    {
        var rise = new TranslateTransform(0, 16);
        page.RenderTransform = rise;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
        rise.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(16, 0, TimeSpan.FromMilliseconds(420)) { EasingFunction = ease });
    }
}
