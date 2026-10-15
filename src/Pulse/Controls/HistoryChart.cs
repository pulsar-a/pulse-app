using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Pulse.Services.History;

namespace Pulse.Controls;

/// <summary>
/// The large chart in the sensor details panel: an average line over a soft min–max band, gridlines
/// with value and time labels, gaps where there's no data, an optional second series to compare,
/// and a hover crosshair with a tooltip.
/// </summary>
public sealed class HistoryChart : FrameworkElement
{
    private const double LeftAxis = 52;
    private const double BottomAxis = 22;
    private const double TopPad = 8;

    public static readonly DependencyProperty PointsProperty = Register<IReadOnlyList<HistoryPoint>?>(nameof(Points), null);
    public static readonly DependencyProperty ComparePointsProperty = Register<IReadOnlyList<HistoryPoint>?>(nameof(ComparePoints), null);
    public static readonly DependencyProperty FromProperty = Register(nameof(From), DateTime.MinValue);
    public static readonly DependencyProperty ToProperty = Register(nameof(To), DateTime.MinValue);
    public static readonly DependencyProperty ColorProperty = Register(nameof(Color), Colors.DeepSkyBlue);
    public static readonly DependencyProperty CompareColorProperty = Register(nameof(CompareColor), Colors.Orange);
    public static readonly DependencyProperty ValueFormatterProperty =
        Register<Func<double, string>>(nameof(ValueFormatter), v => v.ToString("0.#", CultureInfo.CurrentCulture));
    public static readonly DependencyProperty GridBrushProperty = Register<Brush>(nameof(GridBrush), Brushes.DimGray);
    public static readonly DependencyProperty LabelBrushProperty = Register<Brush>(nameof(LabelBrush), Brushes.Gray);
    public static readonly DependencyProperty TooltipBackgroundProperty = Register<Brush>(nameof(TooltipBackground), Brushes.Black);

    private Point? _hover;

    public IReadOnlyList<HistoryPoint>? Points { get => (IReadOnlyList<HistoryPoint>?)GetValue(PointsProperty); set => SetValue(PointsProperty, value); }
    public IReadOnlyList<HistoryPoint>? ComparePoints { get => (IReadOnlyList<HistoryPoint>?)GetValue(ComparePointsProperty); set => SetValue(ComparePointsProperty, value); }
    public DateTime From { get => (DateTime)GetValue(FromProperty); set => SetValue(FromProperty, value); }
    public DateTime To { get => (DateTime)GetValue(ToProperty); set => SetValue(ToProperty, value); }
    public Color Color { get => (Color)GetValue(ColorProperty); set => SetValue(ColorProperty, value); }
    public Color CompareColor { get => (Color)GetValue(CompareColorProperty); set => SetValue(CompareColorProperty, value); }

    /// <summary>Formats axis labels and the tooltip (it also converts units, e.g. °C to °F).</summary>
    public Func<double, string> ValueFormatter { get => (Func<double, string>)GetValue(ValueFormatterProperty); set => SetValue(ValueFormatterProperty, value); }

    public Brush GridBrush { get => (Brush)GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
    public Brush LabelBrush { get => (Brush)GetValue(LabelBrushProperty); set => SetValue(LabelBrushProperty, value); }
    public Brush TooltipBackground { get => (Brush)GetValue(TooltipBackgroundProperty); set => SetValue(TooltipBackgroundProperty, value); }

    private static DependencyProperty Register<T>(string name, T fallback) =>
        DependencyProperty.Register(name, typeof(T), typeof(HistoryChart),
            new FrameworkPropertyMetadata(fallback, FrameworkPropertyMetadataOptions.AffectsRender));

    protected override void OnMouseMove(MouseEventArgs e)
    {
        _hover = e.GetPosition(this);
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _hover = null;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        // Transparent fill so the whole area receives mouse moves for the crosshair.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));

        var plot = new Rect(LeftAxis, TopPad, Math.Max(0, ActualWidth - LeftAxis - 4), Math.Max(0, ActualHeight - TopPad - BottomAxis));
        if (plot.Width < 20 || plot.Height < 20 || To <= From)
            return;

        var series = Points ?? [];
        var compare = ComparePoints ?? [];
        var all = series.Concat(compare).ToList();
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        if (all.Count == 0)
        {
            DrawText(dc, "No data for this range yet", new Point(plot.Left + plot.Width / 2, plot.Top + plot.Height / 2), dpi, center: true, size: 13);
            return;
        }

        var (low, high) = ChartMath.NiceRange(all.Min(p => p.Min), all.Max(p => p.Max));
        double X(DateTime t) => plot.Left + (t - From).Ticks / (double)(To - From).Ticks * plot.Width;
        double Y(double v) => plot.Bottom - (v - low) / (high - low) * plot.Height;

        // Gridlines and value labels.
        var gridPen = new Pen(GridBrush, 1);
        foreach (var tick in ChartMath.Ticks(low, high, 5))
        {
            var y = Math.Round(Y(tick)) + 0.5;
            dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            DrawText(dc, ValueFormatter(tick), new Point(plot.Left - 8, y), dpi, rightAlign: true);
        }

        // Time labels.
        foreach (var (time, label) in ChartMath.TimeTicks(From, To, (int)(plot.Width / 80)))
            DrawText(dc, label, new Point(X(time), plot.Bottom + 12), dpi, center: true);

        dc.PushClip(new RectangleGeometry(plot));
        DrawSeries(dc, compare, CompareColor, X, Y, band: false);
        DrawSeries(dc, series, Color, X, Y, band: true);
        dc.Pop();

        DrawHover(dc, plot, series, compare, X, Y, dpi);
    }

    private static void DrawSeries(DrawingContext dc, IReadOnlyList<HistoryPoint> points, Color color,
        Func<DateTime, double> x, Func<double, double> y, bool band)
    {
        foreach (var run in ChartMath.Runs(points))
        {
            if (run.Count == 0) continue;

            if (band && run.Count > 1)
            {
                // Min–max band: along the max values, then back along the min values.
                var area = new StreamGeometry();
                using (var ctx = area.Open())
                {
                    ctx.BeginFigure(new Point(x(run[0].Time), y(run[0].Max)), true, true);
                    ctx.PolyLineTo(run.Skip(1).Select(p => new Point(x(p.Time), y(p.Max))).ToList(), false, true);
                    ctx.PolyLineTo(run.AsEnumerable().Reverse().Select(p => new Point(x(p.Time), y(p.Min))).ToList(), false, true);
                }
                area.Freeze();
                dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(0x38, color.R, color.G, color.B)), null, area);
            }

            var line = new StreamGeometry();
            using (var ctx = line.Open())
            {
                ctx.BeginFigure(new Point(x(run[0].Time), y(run[0].Avg)), false, false);
                ctx.PolyLineTo(run.Skip(1).Select(p => new Point(x(p.Time), y(p.Avg))).ToList(), true, true);
            }
            line.Freeze();
            var pen = new Pen(new SolidColorBrush(color), 1.8) { LineJoin = PenLineJoin.Round };
            dc.DrawGeometry(null, pen, line);
            if (run.Count == 1)
                dc.DrawEllipse(new SolidColorBrush(color), null, new Point(x(run[0].Time), y(run[0].Avg)), 2, 2);
        }
    }

    private void DrawHover(DrawingContext dc, Rect plot, IReadOnlyList<HistoryPoint> series, IReadOnlyList<HistoryPoint> compare,
        Func<DateTime, double> x, Func<double, double> y, double dpi)
    {
        if (_hover is not { } mouse || !plot.Contains(mouse) || series.Count == 0)
            return;

        var nearest = series.MinBy(p => Math.Abs(x(p.Time) - mouse.X))!;
        var px = x(nearest.Time);
        dc.DrawLine(new Pen(LabelBrush, 1) { DashStyle = DashStyles.Dash }, new Point(px, plot.Top), new Point(px, plot.Bottom));
        dc.DrawEllipse(new SolidColorBrush(Color), new Pen(Brushes.White, 1.5), new Point(px, y(nearest.Avg)), 4, 4);

        var lines = new List<string> { nearest.Time.ToString(ChartMath.TooltipTimeFormat(To - From), CultureInfo.CurrentCulture) };
        lines.Add(Math.Abs(nearest.Max - nearest.Min) < 0.005
            ? ValueFormatter(nearest.Avg)
            : $"{ValueFormatter(nearest.Avg)}  (min {ValueFormatter(nearest.Min)}, max {ValueFormatter(nearest.Max)})");
        if (compare.Count > 0)
        {
            var other = compare.MinBy(p => Math.Abs((p.Time - nearest.Time).Ticks))!;
            dc.DrawEllipse(new SolidColorBrush(CompareColor), new Pen(Brushes.White, 1.5), new Point(x(other.Time), y(other.Avg)), 4, 4);
            lines.Add($"Compared: {ValueFormatter(other.Avg)}");
        }

        var text = Text(string.Join("\n", lines), dpi, 12, Brushes.White);
        var box = new Rect(px + 12, plot.Top + 6, text.WidthIncludingTrailingWhitespace + 16, text.Height + 10);
        if (box.Right > plot.Right)
            box.X = px - 12 - box.Width;
        dc.DrawRoundedRectangle(TooltipBackground, null, box, 6, 6);
        dc.DrawText(text, new Point(box.X + 8, box.Y + 5));
    }

    private void DrawText(DrawingContext dc, string text, Point at, double dpi, bool rightAlign = false, bool center = false, double size = 11)
    {
        var formatted = Text(text, dpi, size, LabelBrush);
        var x = rightAlign ? at.X - formatted.Width : center ? at.X - formatted.Width / 2 : at.X;
        dc.DrawText(formatted, new Point(x, at.Y - formatted.Height / 2));
    }

    private static FormattedText Text(string text, double dpi, double size, Brush brush) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush, dpi);
}

/// <summary>Chart arithmetic, separate from drawing so it can be tested.</summary>
public static class ChartMath
{
    /// <summary>Pads the data range and rounds it outward to a "nice" step, so gridlines land on round numbers.</summary>
    public static (double Low, double High) NiceRange(double min, double max)
    {
        if (double.IsNaN(min) || double.IsNaN(max)) return (0, 1);
        if (max - min < 1e-9)
        {
            var pad = Math.Max(Math.Abs(max) * 0.05, 1);
            min -= pad;
            max += pad;
        }
        var step = NiceStep((max - min) / 4);
        return (Math.Floor(min / step) * step, Math.Ceiling(max / step) * step);
    }

    public static IEnumerable<double> Ticks(double low, double high, int approxCount)
    {
        var step = NiceStep((high - low) / Math.Max(1, approxCount - 1));
        for (var v = Math.Ceiling(low / step) * step; v <= high + step * 1e-6; v += step)
            yield return Math.Round(v, 10);
    }

    /// <summary>1, 2, 2.5 or 5 times a power of ten.</summary>
    public static double NiceStep(double rough)
    {
        if (rough <= 0 || double.IsNaN(rough)) return 1;
        var power = Math.Pow(10, Math.Floor(Math.Log10(rough)));
        var fraction = rough / power;
        var nice = fraction <= 1 ? 1 : fraction <= 2 ? 2 : fraction <= 2.5 ? 2.5 : fraction <= 5 ? 5 : 10;
        return nice * power;
    }

    /// <summary>Evenly spaced time labels at round times (every 1/5/15 minutes, hours, days...).</summary>
    public static IEnumerable<(DateTime Time, string Label)> TimeTicks(DateTime from, DateTime to, int maxLabels)
    {
        var span = to - from;
        TimeSpan[] steps =
        [
            TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10),
            TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30), TimeSpan.FromHours(1), TimeSpan.FromHours(2),
            TimeSpan.FromHours(3), TimeSpan.FromHours(6), TimeSpan.FromHours(12), TimeSpan.FromDays(1),
        ];
        var step = steps.FirstOrDefault(s => span.Ticks / s.Ticks <= Math.Max(2, maxLabels), TimeSpan.FromDays(2));
        var format = step >= TimeSpan.FromDays(1) ? "ddd d" : step >= TimeSpan.FromHours(6) && span > TimeSpan.FromDays(1) ? "ddd HH:mm" : "HH:mm";
        var first = new DateTime((from.Ticks + step.Ticks - 1) / step.Ticks * step.Ticks, from.Kind);
        for (var t = first; t <= to; t += step)
            yield return (t, t.ToString(format, CultureInfo.CurrentCulture));
    }

    public static string TooltipTimeFormat(TimeSpan span) =>
        span <= TimeSpan.FromHours(1) ? "HH:mm:ss" : span <= TimeSpan.FromDays(1) ? "HH:mm" : "ddd d MMM, HH:mm";

    /// <summary>
    /// Splits a series where the data has a hole (Pulse wasn't running, or the sensor was gone), so the
    /// line breaks instead of drawing a misleading straight segment across it. A hole is a gap more than
    /// five times the usual spacing between points.
    /// </summary>
    public static List<List<HistoryPoint>> Runs(IReadOnlyList<HistoryPoint> points)
    {
        var runs = new List<List<HistoryPoint>>();
        if (points.Count == 0) return runs;

        var gaps = points.Zip(points.Skip(1), (a, b) => (b.Time - a.Time).Ticks).Order().ToList();
        var typical = gaps.Count > 0 ? gaps[gaps.Count / 2] : 0;
        var current = new List<HistoryPoint> { points[0] };
        for (var i = 1; i < points.Count; i++)
        {
            if (typical > 0 && (points[i].Time - points[i - 1].Time).Ticks > typical * 5)
            {
                runs.Add(current);
                current = [];
            }
            current.Add(points[i]);
        }
        runs.Add(current);
        return runs;
    }
}
