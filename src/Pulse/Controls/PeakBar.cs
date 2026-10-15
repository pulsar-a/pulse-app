using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Pulse.Controls;

/// <summary>
/// A level bar with a VU-meter peak cap: the bar animates to <see cref="Value"/> (0..1) and a
/// thin cap marks the recent peak, holds there for a moment, then drops back onto the bar.
/// </summary>
public sealed class PeakBar : FrameworkElement
{
    // Defaults match the dark theme; Themes/Styles.xaml sets them from theme tokens.
    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(PeakBar),
        new FrameworkPropertyMetadata(Brushes.DimGray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CapBrushProperty = DependencyProperty.Register(
        nameof(CapBrush), typeof(Brush), typeof(PeakBar),
        new FrameworkPropertyMetadata(Brushes.WhiteSmoke, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public Brush CapBrush
    {
        get => (Brush)GetValue(CapBrushProperty);
        set => SetValue(CapBrushProperty, value);
    }

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(PeakBar), new PropertyMetadata(0.0, OnValueChanged));

    private static readonly DependencyProperty DisplayValueProperty = DependencyProperty.Register(
        "DisplayValue", typeof(double), typeof(PeakBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyProperty PeakProperty = DependencyProperty.Register(
        "Peak", typeof(double), typeof(PeakBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(PeakBar),
        new FrameworkPropertyMetadata(Brushes.DeepSkyBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty OrientationProperty = DependencyProperty.Register(
        nameof(Orientation), typeof(Orientation), typeof(PeakBar),
        new FrameworkPropertyMetadata(Orientation.Vertical, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(double), typeof(PeakBar),
        new FrameworkPropertyMetadata(6.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private readonly PeakHold _peak;

    public PeakBar() => _peak = new PeakHold(this, PeakProperty);

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public Brush Fill
    {
        get => (Brush)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public Orientation Orientation
    {
        get => (Orientation)GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

    public double CornerRadius
    {
        get => (double)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var bar = (PeakBar)d;
        var target = (double)e.NewValue;
        target = double.IsNaN(target) ? 0 : Math.Clamp(target, 0, 1);

        bar.BeginAnimation(DisplayValueProperty,
            Motion.Instrument(target, TimeSpan.FromMilliseconds(450), new CubicEase { EasingMode = EasingMode.EaseOut }));
        bar._peak.Update(target);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        var value = Math.Clamp((double)GetValue(DisplayValueProperty), 0, 1);
        var peak = Math.Clamp((double)GetValue(PeakProperty), 0, 1);
        var vertical = Orientation == Orientation.Vertical;
        var r = Math.Min(CornerRadius, Math.Min(w, h) / 2);

        dc.DrawRoundedRectangle(TrackBrush, null, new Rect(0, 0, w, h), r, r);

        var fill = vertical
            ? new Rect(0, h * (1 - value), w, h * value)
            : new Rect(0, 0, w * value, h);
        if (fill.Width > 0.5 && fill.Height > 0.5)
        {
            var fr = Math.Min(r, Math.Min(fill.Width, fill.Height) / 2);
            dc.DrawRoundedRectangle(Fill, null, fill, fr, fr);
        }

        // The cap shows only while it floats above the bar.
        if (peak - value > 0.015)
        {
            var cap = vertical
                ? new Rect(2, h * (1 - peak) - 1.5, w - 4, 3)
                : new Rect(w * peak - 1.5, -2, 3, h + 4);
            dc.DrawRoundedRectangle(CapBrush, null, cap, 1.5, 1.5);
        }
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        if (freezable.CanFreeze)
            freezable.Freeze();
        return freezable;
    }
}
