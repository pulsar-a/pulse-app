using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Pulse.Controls;

namespace Pulse.Views;

/// <summary>
/// The launch heartbeat (see SplashView.xaml). It beats until <see cref="Dismiss"/>, at least once.
/// </summary>
public partial class SplashView : UserControl
{
    private const double Width0 = 480;
    private const double Baseline = 78;

    /// <summary>One heartbeat on the monitor, left to right: a P wave, the QRS spike, a T wave.</summary>
    public static readonly Point[] Beat =
    [
        new(0, Baseline), new(132, Baseline),
        new(144, 70), new(156, 66), new(168, 70), new(180, Baseline),           // P
        new(202, Baseline), new(210, 86), new(222, 10), new(236, 124),         // Q, R, S
        new(246, Baseline), new(274, Baseline),
        new(286, 70), new(300, 62), new(314, 64), new(326, 72), new(336, Baseline), // T
        new(Width0, Baseline),
    ];

    /// <summary>The R peak: where the rings burst and everything thumps.</summary>
    public static Point Spike => Beat.MaxBy(p => -p.Y);

    /// <summary>How long the head takes to cross the monitor, and how long a whole beat lasts.</summary>
    public static readonly TimeSpan Sweep = TimeSpan.FromSeconds(0.95);
    public static readonly TimeSpan Cycle = TimeSpan.FromSeconds(1.6);

    /// <summary>How far behind the head the line has faded out.</summary>
    private const double Trail = 300;

    /// <summary>At least this many beats are shown, even when the first readings arrive straight away.</summary>
    public const int MinimumBeats = 2;

    /// <summary>
    /// Where in a beat the splash leaves: just after the spike, so the farewell shockwave goes out with
    /// the heartbeat instead of cutting a sweep short.
    /// </summary>
    public static TimeSpan ExitPoint => TimeSpan.FromTicks((long)(Sweep.Ticks * TimeAt(Spike.X))) + TimeSpan.FromMilliseconds(320);

    /// <summary>
    /// How long to wait, <paramref name="shown"/> into the splash, before leaving: until the exit point of
    /// the next beat, and not before the <see cref="MinimumBeats"/>th beat. Never more than one beat.
    /// </summary>
    public static TimeSpan WaitBeforeLeaving(TimeSpan shown)
    {
        for (var beat = MinimumBeats - 1; ; beat++)
        {
            var exit = TimeSpan.FromTicks(Cycle.Ticks * beat) + ExitPoint;
            if (exit >= shown)
                return exit - shown;
        }
    }

    private Storyboard? _beat;
    private bool _dismissed;
    private readonly System.Diagnostics.Stopwatch _shown = new();
    private Action? _pendingDismiss;

    public SplashView()
    {
        InitializeComponent();
        BuildTitle();
    }

    /// <summary>When the head is at a given x on the monitor, as a fraction of the sweep.</summary>
    public static double TimeAt(double x) => x / Width0;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var brand = (Color)FindResource("Pulse.Brand.Color");
        var brandBrush = new SolidColorBrush(brand);

        var line = new PathGeometry([new PathFigure(Beat[0], [new PolyLineSegment(Beat.Skip(1), true)], false)]);
        line.Freeze();
        Trace.Data = TraceGlow.Data = line;
        Trace.Stroke = TraceGlow.Stroke = brandBrush;
        Ring1.Stroke = Ring2.Stroke = Farewell.Stroke = brandBrush;
        AuraCore.Color = Color.FromArgb(0x55, brand.R, brand.G, brand.B);
        AuraEdge.Color = Color.FromArgb(0x00, brand.R, brand.G, brand.B);
        HeadHalo.Fill = new RadialGradientBrush(Color.FromArgb(0xCC, brand.R, brand.G, brand.B), Color.FromArgb(0, brand.R, brand.G, brand.B));
        GridLines.Fill = GridBrush(brand);
        GridLines.OpacityMask = new RadialGradientBrush(Colors.Black, Colors.Transparent);

        var spike = Spike;
        Canvas.SetLeft(Ring1, spike.X - Ring1.Width / 2);
        Canvas.SetTop(Ring1, spike.Y - Ring1.Height / 2);
        Canvas.SetLeft(Ring2, spike.X - Ring2.Width / 2);
        Canvas.SetTop(Ring2, spike.Y - Ring2.Height / 2);

        if (!Motion.Enabled)
        {
            ShowStill();
            return;
        }

        // The line shows only behind the head, fading out over the trail: a gradient whose bright end
        // rides along with the head (one per line, each animated through its own line).
        Trace.OpacityMask = Reveal();
        TraceGlow.OpacityMask = Reveal();

        _beat = BuildBeat();
        _beat.Begin(this, isControllable: true);
        _shown.Start();
        PlayTitle();

        // Asked to leave before it was even on screen: now it can count beats.
        if (_pendingDismiss is { } leave)
        {
            _pendingDismiss = null;
            Dismiss(leave);
        }
    }

    private static LinearGradientBrush Reveal()
    {
        var reveal = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = new Point(-Trail, 0), EndPoint = new Point(2, 0) };
        reveal.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
        reveal.GradientStops.Add(new GradientStop(Color.FromArgb(0x70, 0, 0, 0), 0.55));
        reveal.GradientStops.Add(new GradientStop(Colors.Black, Trail / (Trail + 2)));
        reveal.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
        reveal.Transform = new TranslateTransform();
        return reveal;
    }

    private static readonly PropertyPath RevealX = new("(UIElement.OpacityMask).(Brush.Transform).(TranslateTransform.X)");
    private static readonly PropertyPath ScaleX = new("(UIElement.RenderTransform).(ScaleTransform.ScaleX)");
    private static readonly PropertyPath ScaleY = new("(UIElement.RenderTransform).(ScaleTransform.ScaleY)");

    /// <summary>One beat, repeated: the head sweeps, the trail fades, and the spike sets everything off.</summary>
    private Storyboard BuildBeat()
    {
        var story = new Storyboard { Duration = Cycle, RepeatBehavior = RepeatBehavior.Forever };
        Timeline.SetDesiredFrameRate(story, 60);
        var hit = TimeSpan.FromTicks((long)(Sweep.Ticks * TimeAt(Spike.X)));

        // The bright end of the line moves with the head, then on past the edge so the tail fades away.
        var travel = Width0 + Trail;
        var trailTime = TimeSpan.FromTicks((long)(Sweep.Ticks * travel / Width0));
        foreach (var line in new[] { Trace, TraceGlow })
            Add(story, new DoubleAnimation(0, travel, trailTime), line, RevealX);

        // The head: x moves steadily; y follows the line's points at the moments it passes them.
        var x = new DoubleAnimation(-Head.Width / 2, Width0 - Head.Width / 2, Sweep);
        var y = new DoubleAnimationUsingKeyFrames();
        foreach (var p in Beat)
            y.KeyFrames.Add(new LinearDoubleKeyFrame(p.Y - Head.Height / 2, KeyTime.FromTimeSpan(TimeSpan.FromTicks((long)(Sweep.Ticks * TimeAt(p.X))))));
        Add(story, x, Head, new PropertyPath(Canvas.LeftProperty));
        Add(story, y, Head, new PropertyPath(Canvas.TopProperty));
        var headFade = new DoubleAnimationUsingKeyFrames();
        headFade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        headFade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(80))));
        headFade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(Sweep - TimeSpan.FromMilliseconds(120))));
        headFade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(Sweep)));
        Add(story, headFade, Head, new PropertyPath(OpacityProperty));

        // On the spike: two shockwave rings, a flare of the glow, and a thump of the monitor and title.
        Ripple(story, Ring1, hit, 0.25, 3.2, 0.85);
        Ripple(story, Ring2, hit + TimeSpan.FromMilliseconds(110), 0.25, 2.4, 0.6);
        Pulse(story, Aura, new PropertyPath(OpacityProperty), hit, 0.35, 0.95, TimeSpan.FromMilliseconds(90), TimeSpan.FromMilliseconds(650));
        foreach (var target in new FrameworkElement[] { Monitor, Title })
        {
            Pulse(story, target, ScaleX, hit, 1, 1.05, TimeSpan.FromMilliseconds(70), TimeSpan.FromMilliseconds(260));
            Pulse(story, target, ScaleY, hit, 1, 1.05, TimeSpan.FromMilliseconds(70), TimeSpan.FromMilliseconds(260));
        }
        return story;
    }

    private static void Ripple(Storyboard story, FrameworkElement ring, TimeSpan at, double from, double to, double opacity)
    {
        var duration = TimeSpan.FromMilliseconds(750);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Add(story, new DoubleAnimation(from, to, duration) { BeginTime = at, EasingFunction = ease }, ring, ScaleX);
        Add(story, new DoubleAnimation(from, to, duration) { BeginTime = at, EasingFunction = ease }, ring, ScaleY);
        Add(story, new DoubleAnimation(opacity, 0, duration) { BeginTime = at }, ring, new PropertyPath(OpacityProperty));
    }

    /// <summary>Up to a peak quickly, then back down slowly.</summary>
    private static void Pulse(Storyboard story, DependencyObject target, PropertyPath property, TimeSpan at,
        double rest, double peak, TimeSpan up, TimeSpan down)
    {
        var frames = new DoubleAnimationUsingKeyFrames();
        frames.KeyFrames.Add(new LinearDoubleKeyFrame(rest, KeyTime.FromTimeSpan(at)));
        frames.KeyFrames.Add(new EasingDoubleKeyFrame(peak, KeyTime.FromTimeSpan(at + up), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        frames.KeyFrames.Add(new EasingDoubleKeyFrame(rest, KeyTime.FromTimeSpan(at + up + down), new CubicEase { EasingMode = EasingMode.EaseInOut }));
        Add(story, frames, target, property);
    }

    private static void Add(Storyboard story, Timeline animation, DependencyObject target, PropertyPath property)
    {
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        story.Children.Add(animation);
    }

    // ---- Title --------------------------------------------------------------------------------

    private void BuildTitle()
    {
        foreach (var letter in "Pulse")
        {
            Title.Children.Add(new TextBlock
            {
                Text = letter.ToString(),
                FontSize = 40,
                Style = (Style)FindResource("Metric"),
                RenderTransform = new TranslateTransform(),
            });
        }
    }

    /// <summary>The letters rise into place one after another; then the status line fades in.</summary>
    private void PlayTitle()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        for (var i = 0; i < Title.Children.Count; i++)
        {
            var letter = (TextBlock)Title.Children[i];
            var at = TimeSpan.FromMilliseconds(180 + i * 70);
            letter.Opacity = 0;
            letter.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320)) { BeginTime = at });
            ((TranslateTransform)letter.RenderTransform).BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(420)) { BeginTime = at, EasingFunction = ease });
        }
        Status.Opacity = 0;
        Status.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(400)) { BeginTime = TimeSpan.FromMilliseconds(650) });
    }

    // ---- Still picture and goodbye -----------------------------------------------------------

    private void ShowStill()
    {
        Trace.OpacityMask = TraceGlow.OpacityMask = null;
        Head.Opacity = 0;
    }

    /// <summary>
    /// The dashboard is ready. The splash finishes the beat it's on (and shows at least
    /// <see cref="MinimumBeats"/>), then the monitor zooms past and fades while one last shockwave
    /// spreads across the window. <paramref name="leaving"/> runs as it starts to go, so the dashboard
    /// can rise in underneath. With Windows animations off it goes straight away.
    /// </summary>
    public void Dismiss(Action leaving)
    {
        if (_dismissed) return;
        if (Motion.Enabled && !_shown.IsRunning)
        {
            _pendingDismiss = leaving;   // not on screen yet: leave once it has beaten
            return;
        }

        var wait = Motion.Enabled ? WaitBeforeLeaving(_shown.Elapsed) : TimeSpan.Zero;
        _dismissed = true;
        if (wait <= TimeSpan.FromMilliseconds(15))
        {
            Leave(leaving);
            return;
        }
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = wait };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Leave(leaving);
        };
        timer.Start();
    }

    private void Leave(Action leaving)
    {
        void Finish()
        {
            _beat?.Stop(this);
            _beat?.Remove(this);
            Visibility = Visibility.Collapsed;
        }

        leaving();
        if (!Motion.Enabled || !IsLoaded)
        {
            Finish();
            return;
        }

        var duration = TimeSpan.FromMilliseconds(460);
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var zoom = (ScaleTransform)Root.RenderTransform;
        zoom.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 1.18, duration) { EasingFunction = ease });
        zoom.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 1.18, duration) { EasingFunction = ease });

        var wave = (ScaleTransform)Farewell.RenderTransform;
        var spread = new CubicEase { EasingMode = EasingMode.EaseOut };
        wave.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.3, 9, duration) { EasingFunction = spread });
        wave.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.3, 9, duration) { EasingFunction = spread });
        Farewell.BeginAnimation(OpacityProperty, new DoubleAnimation(0.8, 0, duration));

        var fade = new DoubleAnimation(1, 0, duration) { EasingFunction = ease };
        fade.Completed += (_, _) => Finish();
        BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>A faint oscilloscope grid, 24 px squares.</summary>
    private static DrawingBrush GridBrush(Color brand)
    {
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(0x22, brand.R, brand.G, brand.B)), 1);
        var lines = new GeometryGroup();
        lines.Children.Add(new LineGeometry(new Point(0, 0), new Point(24, 0)));
        lines.Children.Add(new LineGeometry(new Point(0, 0), new Point(0, 24)));
        var brush = new DrawingBrush(new GeometryDrawing(null, pen, lines))
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 24, 24),
            ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, 24, 24),
            ViewboxUnits = BrushMappingMode.Absolute,
        };
        brush.Freeze();
        return brush;
    }
}
