using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Pulse.Controls;

/// <summary>
/// A 270° dial gauge. From the inside out it draws:
/// tick marks, the track with a solid value arc and a knob at its tip, a VU-style peak marker,
/// and a thin band showing the range between <see cref="RangeMin"/> and <see cref="RangeMax"/>.
/// All values are 0..1; <see cref="Value"/> animates smoothly between updates.
/// </summary>
public sealed class RingGauge : FrameworkElement
{
    private const double StartAngle = 135;
    private const double SweepAngle = 270;
    private const double BandThickness = 3;
    private const double BandGap = 5;
    private const int TickCount = 10;

    private static readonly Brush TrackBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF)));
    private static readonly Pen MinorTick = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF))), 1.2));
    private static readonly Pen MajorTick = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0x48, 0xFF, 0xFF, 0xFF))), 1.6));
    private static readonly Brush KnobFill = Frozen(new SolidColorBrush(Color.FromRgb(0xF7, 0xF7, 0xF7)));

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(RingGauge), new PropertyMetadata(0.0, OnValueChanged));

    private static readonly DependencyProperty DisplayValueProperty = DependencyProperty.Register(
        "DisplayValue", typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyProperty PeakProperty = DependencyProperty.Register(
        "Peak", typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RangeMinProperty = DependencyProperty.Register(
        nameof(RangeMin), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RangeMaxProperty = DependencyProperty.Register(
        nameof(RangeMax), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(
        nameof(Color), typeof(Color), typeof(RingGauge),
        new FrameworkPropertyMetadata(Colors.DeepSkyBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(10.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private readonly PeakHold _peak;

    public RingGauge() => _peak = new PeakHold(this, PeakProperty);

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>Lowest value seen (0..1), drawn as the start of the outer band. NaN hides the band.</summary>
    public double RangeMin
    {
        get => (double)GetValue(RangeMinProperty);
        set => SetValue(RangeMinProperty, value);
    }

    /// <summary>Highest value seen (0..1), drawn as the end of the outer band.</summary>
    public double RangeMax
    {
        get => (double)GetValue(RangeMaxProperty);
        set => SetValue(RangeMaxProperty, value);
    }

    public Color Color
    {
        get => (Color)GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    public double Thickness
    {
        get => (double)GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var gauge = (RingGauge)d;
        var target = (double)e.NewValue;
        target = double.IsNaN(target) ? 0 : Math.Clamp(target, 0, 1);

        gauge.BeginAnimation(DisplayValueProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(700))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
        gauge._peak.Update(target);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;

        var t = Thickness;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var outer = size / 2 - 1;
        var radius = outer - BandThickness - BandGap - t / 2;
        var value = (double)GetValue(DisplayValueProperty);
        var peak = Math.Clamp((double)GetValue(PeakProperty), 0, 1);

        // Dial ticks just inside the track; 0, 50 and 100% are a little longer and brighter.
        for (var i = 0; i <= TickCount; i++)
        {
            var major = i % 5 == 0;
            var angle = StartAngle + SweepAngle * i / TickCount;
            var from = radius - t / 2 - 4;
            var to = from - (major ? 7 : 4);
            dc.DrawLine(major ? MajorTick : MinorTick, PointOn(center, from, angle), PointOn(center, to, angle));
        }

        dc.DrawGeometry(null, RoundPen(TrackBrush, t), Arc(center, radius, StartAngle, SweepAngle));

        // Session range: an opaque, muted shade of the accent so it never competes with the value arc.
        var min = RangeMin;
        var max = RangeMax;
        if (!double.IsNaN(min) && !double.IsNaN(max))
        {
            min = Math.Clamp(min, 0, 1);
            max = Math.Clamp(Math.Max(max, min + 0.004), 0, 1);
            var bandRadius = radius + t / 2 + BandGap + BandThickness / 2;
            var band = RoundPen(Frozen(new SolidColorBrush(Mix(Color, Color.FromRgb(0x1C, 0x1C, 0x22), 0.5))), BandThickness);
            dc.DrawGeometry(null, band, Arc(center, bandRadius, StartAngle + SweepAngle * min, SweepAngle * (max - min)));
        }

        // Peak marker: a bright notch across the track, visible only while it sits above the value.
        if (peak - value > 0.01)
        {
            var angle = StartAngle + SweepAngle * peak;
            var notch = RoundPen(Frozen(new SolidColorBrush(Mix(Color, Colors.White, 0.55))), 3);
            dc.DrawLine(notch, PointOn(center, radius - t / 2 - 1, angle), PointOn(center, radius + t / 2 + 1, angle));
        }

        if (value <= 0.002)
            return;

        // Solid value arc, deepening from a darker shade at the start to the full accent at the tip.
        var sweep = SweepAngle * value;
        var startPoint = PointOn(center, radius, StartAngle);
        var tip = PointOn(center, radius, StartAngle + sweep);
        var gradient = Frozen(new LinearGradientBrush(Mix(Color, Colors.Black, 0.35), Color, startPoint, tip)
        {
            MappingMode = BrushMappingMode.Absolute,
        });
        dc.DrawGeometry(null, RoundPen(gradient, t), Arc(center, radius, StartAngle, sweep));

        // A crisp knob at the tip marks the exact reading.
        dc.DrawEllipse(KnobFill, Frozen(new Pen(Frozen(new SolidColorBrush(Color)), 2)), tip, t / 2 + 1, t / 2 + 1);
    }

    private static Pen RoundPen(Brush brush, double thickness) => Frozen(new Pen(brush, thickness)
    {
        StartLineCap = PenLineCap.Round,
        EndLineCap = PenLineCap.Round,
    });

    private static Geometry Arc(Point center, double radius, double startDeg, double sweepDeg)
    {
        sweepDeg = Math.Min(sweepDeg, 359.99);
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(PointOn(center, radius, startDeg), isFilled: false, isClosed: false);
            ctx.ArcTo(PointOn(center, radius, startDeg + sweepDeg), new Size(radius, radius), 0,
                sweepDeg > 180, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    private static Point PointOn(Point center, double radius, double degrees)
    {
        var rad = degrees * Math.PI / 180;
        return new Point(center.X + radius * Math.Cos(rad), center.Y + radius * Math.Sin(rad));
    }

    private static Color Mix(Color a, Color b, double amount) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * amount),
        (byte)Math.Round(a.G + (b.G - a.G) * amount),
        (byte)Math.Round(a.B + (b.B - a.B) * amount));

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
