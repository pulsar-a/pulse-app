using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Pulse.Controls;
using Pulse.Services;
using Pulse.ViewModels;
using Pulse.Views;
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

    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        viewModel.PropertyChanged += OnViewModelPropertyChanged;

        _splashTimeout = new DispatcherTimer { Interval = MaximumSplash };
        _splashTimeout.Tick += (_, _) => DismissSplash();
        _splashTimeout.Start();

        DashboardPage.IsVisibleChanged += (_, e) => { if (e.NewValue is true) PlayEntrance(DashboardPage); };
        SensorsPage.IsVisibleChanged += (_, e) => { if (e.NewValue is true) PlayEntrance(SensorsPage); };
        SettingsPage.IsVisibleChanged += (_, e) => { if (e.NewValue is true) PlayEntrance(SettingsPage); };

        // Idle mode: nobody can see a minimized or hidden window, so stop updating it.
        StateChanged += (_, _) => UpdateActivity();
        IsVisibleChanged += (_, _) => UpdateActivity();

        ContentRendered += (_, _) =>
        {
            var sinceStart = DateTime.Now - Process.GetCurrentProcess().StartTime;
            Log.Write($"First frame {sinceStart.TotalMilliseconds:0} ms after the process started");
        };
    }

    private void UpdateActivity() =>
        _viewModel.IsWindowActive = IsVisible && WindowState != WindowState.Minimized;

    private async void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            // Pages other than the dashboard are built the first time they're opened.
            case nameof(MainViewModel.IsSensorsSelected) when _viewModel.IsSensorsSelected:
                EnsurePage(SensorsPage, () => new SensorsView { DataContext = _viewModel.Sensors });
                return;
            case nameof(MainViewModel.IsSettingsSelected) when _viewModel.IsSettingsSelected:
                EnsurePage(SettingsPage, () => new SettingsView { DataContext = _viewModel.Settings });
                return;
            // StatusText changes once per applied reading: pulse the live dot to show fresh data.
            case nameof(MainViewModel.StatusText):
                PulseLiveDot();
                return;
            case nameof(MainViewModel.IsLoading) when !_viewModel.IsLoading:
                var remaining = MinimumSplash - _sinceLaunch.Elapsed;
                if (remaining > TimeSpan.Zero)
                    await Task.Delay(remaining);
                DismissSplash();
                return;
        }
    }

    /// <summary>
    /// One ripple of the green dot next to "Live" per reading. It used to loop forever, which kept the
    /// whole window redrawing 20+ times a second even when nothing else changed.
    /// </summary>
    private void PulseLiveDot()
    {
        var duration = TimeSpan.FromMilliseconds(650);
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        var scale = (ScaleTransform)LiveHalo.RenderTransform;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, From(0.6, Motion.Instrument(1.8, duration, ease)));
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, From(0.6, Motion.Instrument(1.8, duration, ease)));
        LiveHalo.BeginAnimation(OpacityProperty, From(0.55, Motion.Instrument(0, duration, ease)));

        static DoubleAnimation From(double from, DoubleAnimation animation)
        {
            animation.From = from;
            return animation;
        }
    }

    private static void EnsurePage(ContentControl host, Func<FrameworkElement> create)
    {
        if (host.Content is not null)
            return;
        var sw = Stopwatch.StartNew();
        host.Content = create();
        Log.Write($"Built {host.Name} in {sw.ElapsedMilliseconds} ms");
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
