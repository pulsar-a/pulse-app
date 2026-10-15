using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Pulse.Models;

namespace Pulse.ViewModels;

/// <summary>One bar in the per-core strip.</summary>
public partial class CoreTileViewModel : ObservableObject
{
    [ObservableProperty] public partial string Label { get; set; } = "";
    [ObservableProperty] public partial string ValueText { get; set; } = "";
    [ObservableProperty] public partial string ToolTip { get; set; } = "";
    [ObservableProperty] public partial double Fraction { get; set; }
    [ObservableProperty] public partial Brush Fill { get; set; } = Brushes.Transparent;
}

public partial class FanRowViewModel : ObservableObject
{
    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial string Source { get; set; } = "";
    [ObservableProperty] public partial string SpeedText { get; set; } = "";
    [ObservableProperty] public partial double Fraction { get; set; }
}

public partial class DriveRowViewModel : ObservableObject
{
    private string _volumesKey = "";

    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial IReadOnlyList<VolumeInfo> Volumes { get; set; } = [];
    [ObservableProperty] public partial bool HasVolumes { get; set; }
    [ObservableProperty] public partial string TemperatureText { get; set; } = "—";
    [ObservableProperty] public partial Brush TemperatureBrush { get; set; } = Brushes.Gray;
    [ObservableProperty] public partial double UsedFraction { get; set; }
    [ObservableProperty] public partial string Details { get; set; } = "";
    [ObservableProperty] public partial string Activity { get; set; } = "";

    /// <summary>Replaces the volume tags only when they actually change, so they don't re-render every tick.</summary>
    public void SetVolumes(IReadOnlyList<VolumeInfo> volumes)
    {
        var key = string.Join("|", volumes.Select(v => $"{v.Letter}{v.Label}"));
        if (key == _volumesKey)
            return;

        _volumesKey = key;
        Volumes = volumes;
        HasVolumes = volumes.Count > 0;
    }
}

public partial class PumpCandidateViewModel : ObservableObject
{
    public required string Id { get; init; }

    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial string SpeedText { get; set; } = "";
}

/// <summary>Pump, coolant and flow readings gathered from every source that reports them.</summary>
public partial class CoolingViewModel : ObservableObject
{
    private readonly Queue<double> _history = new();

    public System.Collections.ObjectModel.ObservableCollection<StatRow> Rows { get; } = [];

    [ObservableProperty] public partial bool HasData { get; set; }
    [ObservableProperty] public partial string Source { get; set; } = "";
    [ObservableProperty] public partial string Headline { get; set; } = "—";
    [ObservableProperty] public partial string HeadlineCaption { get; set; } = "";
    [ObservableProperty] public partial string EmptyTitle { get; set; } = "";
    [ObservableProperty] public partial string EmptyText { get; set; } = "";
    [ObservableProperty] public partial IReadOnlyList<double>? History { get; set; }

    /// <summary>Spinning motherboard fan headers the user can pick as the pump.</summary>
    public System.Collections.ObjectModel.ObservableCollection<PumpCandidateViewModel> Candidates { get; } = [];

    [ObservableProperty] public partial bool ShowPumpPicker { get; set; }

    /// <summary>True when the pump reading comes from a header the user picked, so they can undo it.</summary>
    [ObservableProperty] public partial bool CanChangePump { get; set; }

    public void PushHistory(double value)
    {
        _history.Enqueue(value);
        while (_history.Count > GaugeCardViewModel.HistoryLength)
            _history.Dequeue();
        History = _history.ToArray();
    }

    public void SetRows(IReadOnlyList<(string Label, string Value)> rows)
    {
        for (var i = 0; i < rows.Count; i++)
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
        while (Rows.Count > rows.Count)
            Rows.RemoveAt(Rows.Count - 1);
    }
}

public partial class NetworkViewModel : ObservableObject
{
    private readonly Queue<double> _down = new();
    private readonly Queue<double> _up = new();

    [ObservableProperty] public partial string Adapter { get; set; } = "No active adapter";
    [ObservableProperty] public partial string DownText { get; set; } = "—";
    [ObservableProperty] public partial string UpText { get; set; } = "—";
    [ObservableProperty] public partial string Totals { get; set; } = "";
    [ObservableProperty] public partial IReadOnlyList<double>? DownHistory { get; set; }
    [ObservableProperty] public partial IReadOnlyList<double>? UpHistory { get; set; }

    /// <summary>Shared scale so the download and upload lines are comparable.</summary>
    [ObservableProperty] public partial double Scale { get; set; } = 1;

    public void Push(double down, double up)
    {
        Enqueue(_down, down);
        Enqueue(_up, up);
        DownHistory = _down.ToArray();
        UpHistory = _up.ToArray();
        Scale = Math.Max(Math.Max(_down.Max(), _up.Max()) * 1.15, 1024);
    }

    public void Reset()
    {
        _down.Clear();
        _up.Clear();
        DownHistory = UpHistory = null;
    }

    private static void Enqueue(Queue<double> queue, double value)
    {
        queue.Enqueue(value);
        while (queue.Count > GaugeCardViewModel.HistoryLength)
            queue.Dequeue();
    }
}
