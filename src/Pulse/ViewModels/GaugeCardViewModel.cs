using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Wpf.Ui.Controls;

namespace Pulse.ViewModels;

/// <summary>A dashboard card with a ring gauge, a few stat rows and a history sparkline.</summary>
public partial class GaugeCardViewModel : ObservableObject
{
    public const int HistoryLength = 60;

    public GaugeCardViewModel(string title, SymbolRegular icon, Color accent)
    {
        Title = title;
        Icon = icon;
        Accent = accent;
        AccentBrush = Freeze(new SolidColorBrush(accent));
        AccentSoftBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x29, accent.R, accent.G, accent.B)));
    }

    public string Title { get; }
    public SymbolRegular Icon { get; }
    public Color Accent { get; }
    public Brush AccentBrush { get; }
    public Brush AccentSoftBrush { get; }

    public ObservableCollection<StatRow> Rows { get; } = [];

    /// <summary>The sensor the dial shows; clicking the card opens its details.</summary>
    [ObservableProperty] public partial string? SensorId { get; set; }
    [ObservableProperty] public partial string Device { get; set; } = "Not detected";
    [ObservableProperty] public partial double Gauge { get; set; }
    [ObservableProperty] public partial string GaugeText { get; set; } = "—";
    [ObservableProperty] public partial string GaugeCaption { get; set; } = "";
    [ObservableProperty] public partial IReadOnlyList<double>? History { get; set; }
    [ObservableProperty] public partial double HistoryMax { get; set; } = double.NaN;
    [ObservableProperty] public partial string HistoryLabel { get; set; } = "";

    /// <summary>Session low and high of the gauge reading, 0..1 (NaN until the first reading).</summary>
    [ObservableProperty] public partial double GaugeMin { get; set; } = double.NaN;
    [ObservableProperty] public partial double GaugeMax { get; set; } = double.NaN;
    [ObservableProperty] public partial string? MinText { get; set; }
    [ObservableProperty] public partial string? MaxText { get; set; }

    private string _metric = "";
    private double _min = double.NaN;
    private double _max = double.NaN;

    /// <summary>
    /// Sets the dial from a raw reading and tracks its session low and high.
    /// <paramref name="metric"/> names what the dial shows; switching metric (say, from load to
    /// temperature once the driver starts answering) resets the range.
    /// </summary>
    public void SetGauge(string metric, double? value, double fullScale, Func<double, string> format)
    {
        if (metric != _metric)
        {
            _metric = metric;
            _min = _max = double.NaN;
            MinText = MaxText = null;
            GaugeMin = GaugeMax = double.NaN;
        }

        if (value is not { } v || double.IsNaN(v))
        {
            Gauge = 0;
            GaugeText = "—";
            return;
        }

        _min = double.IsNaN(_min) ? v : Math.Min(_min, v);
        _max = double.IsNaN(_max) ? v : Math.Max(_max, v);

        Gauge = v / fullScale;
        GaugeText = format(v);
        GaugeMin = _min / fullScale;
        GaugeMax = _max / fullScale;
        MinText = format(_min);
        MaxText = format(_max);
    }

    /// <summary>Samples the sparkline spans (one minute at the current update interval).</summary>
    [ObservableProperty] public partial int HistoryCapacity { get; set; } = HistoryLength;

    /// <summary>Recent samples from the sensor stream, oldest first; NaN marks a gap.</summary>
    public void SetHistory(double[] values, int capacity)
    {
        HistoryCapacity = capacity;
        History = values;
    }

    /// <summary>Updates rows in place so the UI doesn't rebuild them every tick.</summary>
    public void SetRows(params (string Label, string Value)[] rows)
    {
        for (var i = 0; i < rows.Length; i++)
        {
            if (i < Rows.Count)
            {
                Rows[i].Label = rows[i].Label;
                Rows[i].Value = rows[i].Value;
            }
            else
            {
                Rows.Add(new StatRow { Label = rows[i].Label, Value = rows[i].Value });
            }
        }
        while (Rows.Count > rows.Length)
            Rows.RemoveAt(Rows.Count - 1);
    }

    private static T Freeze<T>(T freezable) where T : System.Windows.Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}

public partial class StatRow : ObservableObject
{
    [ObservableProperty] public partial string Label { get; set; } = "";
    [ObservableProperty] public partial string Value { get; set; } = "";
}
