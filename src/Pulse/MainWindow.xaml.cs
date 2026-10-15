using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
        viewModel.Search.PropertyChanged += OnSearchPropertyChanged;
        WatchDetails();

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

    /// <summary>Opening the bell marks its alerts as read.</summary>
    private void BellButton_Checked(object sender, RoutedEventArgs e) =>
        _viewModel.Alerts?.MarkReadCommand.Execute(null);

    /// <summary>The popup closes itself on an outside click; keep the bell button in step.</summary>
    private void BellPopup_Closed(object? sender, EventArgs e) => BellButton.IsChecked = false;

    private void AlertSettings_Click(object sender, RoutedEventArgs e)
    {
        BellButton.IsChecked = false;
        _viewModel.OpenAlertSettingsCommand.Execute(null);
    }

    // ---- Narrow windows ------------------------------------------------------------------------

    /// <summary>
    /// Fits the chrome to the window width: under 940 px the header buttons move below the title, under
    /// 640 px the page buttons drop their labels and the side margins shrink. The details panel and the
    /// search never get wider than the window. (The dashboard tiles reflow on their own.)
    /// </summary>
    private void Window_SizeChanged(object sender, SizeChangedEventArgs e) => FitToWidth(ActualWidth);

    private void FitToWidth(double width)
    {
        var stacked = width < 940;
        Grid.SetRow(HeaderActions, stacked ? 1 : 0);
        Grid.SetColumn(HeaderActions, stacked ? 0 : 1);
        HeaderActions.HorizontalAlignment = stacked ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        HeaderActions.Margin = stacked ? new Thickness(0, 10, 0, 0) : new Thickness(0);

        var narrow = width < 640;
        DashboardLabel.Visibility = SensorsLabel.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        DashboardIcon.Margin = SensorsIcon.Margin = narrow ? new Thickness(0) : new Thickness(0, 0, 6, 0);
        var side = narrow ? 14 : 30;
        Header.Margin = new Thickness(side, 2, side, 10);
        Banners.Margin = new Thickness(side, 0, side, 4);

        DetailsPanel.Width = Math.Min(540, width);
        SearchBoxCard.Width = Math.Min(620, Math.Max(200, width - 24));
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
            case nameof(MainViewModel.Details):
                WatchDetails();
                return;
            case nameof(MainViewModel.IsLoading) when !_viewModel.IsLoading:
                var remaining = MinimumSplash - _sinceLaunch.Elapsed;
                if (remaining > TimeSpan.Zero)
                    await Task.Delay(remaining);
                DismissSplash();
                return;
        }
    }

    // ---- Sensor details panel ---------------------------------------------------------------------

    private SensorDetailsViewModel? _watchedDetails;

    private void WatchDetails()
    {
        if (_watchedDetails is not null)
            _watchedDetails.PropertyChanged -= OnDetailsPropertyChanged;
        _watchedDetails = _viewModel.Details;
        if (_watchedDetails is not null)
            _watchedDetails.PropertyChanged += OnDetailsPropertyChanged;
    }

    private void OnDetailsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SensorDetailsViewModel.IsOpen))
            AnimateDetails(_watchedDetails?.IsOpen == true);
    }

    /// <summary>Slides the panel in from the right while the page behind dims; the reverse on close.</summary>
    private void AnimateDetails(bool open)
    {
        var slide = (TranslateTransform)DetailsPanel.RenderTransform;
        var duration = TimeSpan.FromMilliseconds(open ? 320 : 220);
        var ease = new CubicEase { EasingMode = open ? EasingMode.EaseOut : EasingMode.EaseIn };

        if (open)
        {
            BellButton.IsChecked = false;
            DetailsLayer.Visibility = Visibility.Visible;
        }

        var fade = new DoubleAnimation(open ? 1 : 0, duration) { EasingFunction = ease };
        var move = new DoubleAnimation(open ? 0 : DetailsPanel.Width, duration) { EasingFunction = ease };
        if (open && DetailsScrim.Opacity >= 1 && slide.X == 0)
        {
            // Opening while already open (another sensor from the search): just a quick fade of the content.
            DetailsPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0.4, 1, TimeSpan.FromMilliseconds(200)));
            return;
        }
        if (open)
        {
            DetailsScrim.Opacity = 0;
            slide.X = DetailsPanel.Width;
        }
        else
        {
            move.Completed += (_, _) =>
            {
                if (_watchedDetails?.IsOpen != true)
                    DetailsLayer.Visibility = Visibility.Collapsed;
            };
        }
        DetailsScrim.BeginAnimation(OpacityProperty, fade);
        slide.BeginAnimation(TranslateTransform.XProperty, move);
    }

    private void DetailsScrim_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        _viewModel.Details?.CloseCommand.Execute(null);

    // ---- Ctrl+K search ---------------------------------------------------------------------------

    private void SearchButton_Click(object sender, RoutedEventArgs e) => OpenSearch();

    private void OpenSearch()
    {
        BellButton.IsChecked = false;
        _viewModel.Search.Open();
    }

    private void OnSearchPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SensorSearchViewModel.IsOpen))
            return;

        if (!_viewModel.Search.IsOpen)
        {
            SearchLayer.Visibility = Visibility.Collapsed;
            return;
        }

        SearchLayer.Visibility = Visibility.Visible;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(180);
        var scale = (ScaleTransform)SearchBoxCard.RenderTransform;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, duration) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1, duration) { EasingFunction = ease });
        SearchLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
        Dispatcher.BeginInvoke(() =>
        {
            SearchBox.Focus();
            Keyboard.Focus(SearchBox);
        }, DispatcherPriority.Input);
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var search = _viewModel.Search;
        switch (e.Key)
        {
            case Key.Down:
                search.Move(1);
                break;
            case Key.Up:
                search.Move(-1);
                break;
            case Key.PageDown:
                search.Move(8);
                break;
            case Key.PageUp:
                search.Move(-8);
                break;
            case Key.Enter:
                search.ChooseCommand.Execute(null);
                break;
            default:
                return;
        }
        if (search.SelectedResult is { } selected)
            SearchResults.ScrollIntoView(selected);
        e.Handled = true;
    }

    private void SearchResult_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: SearchResult result })
            _viewModel.Search.ChooseCommand.Execute(result);
    }

    private void SearchScrim_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _viewModel.Search.Close();

    /// <summary>
    /// Ctrl+K opens the search; Esc closes the topmost overlay. This handles the bubbling event, so a
    /// control that uses Esc itself (an open dropdown, the rename box) gets it first.
    /// </summary>
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control)
        {
            OpenSearch();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (_viewModel.Search.IsOpen)
                _viewModel.Search.Close();
            else if (_viewModel.Details is { IsOpen: true } details)
                details.CloseCommand.Execute(null);
            else if (_viewModel.Dashboard.IsEditing)
                _viewModel.Dashboard.EndEditCommand.Execute(null);
            else
                return;
            e.Handled = true;
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
