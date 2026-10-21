using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pulse.Models;

namespace Pulse.ViewModels;

/// <summary>One bar in the per-core strip.</summary>
public partial class CoreTileViewModel : ObservableObject
{
    [ObservableProperty] public partial string? SensorId { get; set; }
    [ObservableProperty] public partial string Label { get; set; } = "";
    [ObservableProperty] public partial string ValueText { get; set; } = "";
    [ObservableProperty] public partial string ToolTip { get; set; } = "";
    [ObservableProperty] public partial double Fraction { get; set; }
    [ObservableProperty] public partial Brush Fill { get; set; } = Brushes.Transparent;
}

public partial class FanRowViewModel : ObservableObject
{
    [ObservableProperty] public partial string? SensorId { get; set; }
    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial string Source { get; set; } = "";
    [ObservableProperty] public partial string SpeedText { get; set; } = "";
    [ObservableProperty] public partial double Fraction { get; set; }
}

public partial class DriveRowViewModel : ObservableObject
{
    private string _volumesKey = "";

    /// <summary>The drive's temperature sensor, opened when the row is clicked.</summary>
    [ObservableProperty] public partial string? SensorId { get; set; }

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
    /// <summary>The headline reading (coolant temperature, else pump speed, else flow).</summary>
    [ObservableProperty] public partial string? SensorId { get; set; }
    public System.Collections.ObjectModel.ObservableCollection<StatRow> Rows { get; } = [];

    [ObservableProperty] public partial bool HasData { get; set; }

    /// <summary>Hot when the pump is slower than its alert limit.</summary>
    [ObservableProperty] public partial HeatLevel Heat { get; set; }
    [ObservableProperty] public partial string Source { get; set; } = "";
    [ObservableProperty] public partial string Headline { get; set; } = "—";
    [ObservableProperty] public partial string HeadlineCaption { get; set; } = "";
    [ObservableProperty] public partial string EmptyTitle { get; set; } = "";
    [ObservableProperty] public partial string EmptyText { get; set; } = "";
    [ObservableProperty] public partial IReadOnlyList<double>? History { get; set; }
    [ObservableProperty] public partial int HistoryCapacity { get; set; } = GaugeCardViewModel.HistoryLength;

    /// <summary>
    /// Every spinning motherboard fan header, plus "not on a header": the choices for which one is the pump.
    /// Motherboards like the Z790 Hero don't say which header the pump is on, so the user picks it here.
    /// </summary>
    public System.Collections.ObjectModel.ObservableCollection<PumpCandidateViewModel> Headers { get; } = [];

    /// <summary>The header picked as the pump; null until someone chooses.</summary>
    [ObservableProperty] public partial PumpCandidateViewModel? SelectedHeader { get; set; }

    [ObservableProperty] public partial bool ShowHeaderPicker { get; set; }

    /// <summary>Called with the chosen header's sensor id ("" for "not on a header") when the user picks one.</summary>
    public Action<string>? PumpChosen { get; set; }

    /// <summary>The pump reads unlike itself since Pulse started (see <see cref="Services.PumpWatch"/>): ask what changed.</summary>
    [ObservableProperty] public partial bool HasChange { get; set; }
    [ObservableProperty] public partial string ChangeText { get; set; } = "";

    /// <summary>"Use Fan #6": the header that started spinning since. Null when there's none to suggest.</summary>
    [ObservableProperty] public partial string? UseLabel { get; set; }

    /// <summary>"Fan #2 is the pump": it's the same header, with a different pump. Null when the pump is gone.</summary>
    [ObservableProperty] public partial string? KeepLabel { get; set; }

    /// <summary>Set by the dashboard: switch to the suggested header, or keep the current one and learn it again.</summary>
    public Action? UseSuggested { get; set; }
    public Action? KeepPump { get; set; }

    [RelayCommand]
    private void UseSuggestion() => UseSuggested?.Invoke();

    [RelayCommand]
    private void Keep() => KeepPump?.Invoke();

    /// <summary>Shows what <see cref="Services.PumpWatch"/> noticed, or hides it (null).</summary>
    public void SetChange(Services.PumpChange? change)
    {
        HasChange = change is not null;
        ChangeText = change is null ? "" : change.Suggestion is { } s ? $"{change.Description} {s}" : change.Description;
        UseLabel = change?.SuggestedName is { } suggested ? $"Use {suggested}" : null;
        KeepLabel = change?.Rpm is not null ? $"{change.Name} is the pump" : null;
    }

    private bool _syncing;

    partial void OnSelectedHeaderChanged(PumpCandidateViewModel? value)
    {
        if (!_syncing && value is not null)
            PumpChosen?.Invoke(value.Id);
    }

    /// <summary>Updates the choices and the selection from the readings without counting it as a choice.</summary>
    public void SyncHeaders(IReadOnlyList<(string Id, string Name, string Speed)> headers, string? selectedId)
    {
        _syncing = true;
        try
        {
            if (!Headers.Select(h => h.Id).SequenceEqual(headers.Select(h => h.Id)))
            {
                var existing = Headers.ToDictionary(h => h.Id);
                Headers.Clear();
                foreach (var header in headers)
                    Headers.Add(existing.GetValueOrDefault(header.Id) ?? new PumpCandidateViewModel { Id = header.Id });
            }
            for (var i = 0; i < headers.Count; i++)
            {
                Headers[i].Name = headers[i].Name;
                Headers[i].SpeedText = headers[i].Speed;
            }
            SelectedHeader = selectedId is null ? null : Headers.FirstOrDefault(h => h.Id == selectedId);
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>Recent samples of the headline reading from the sensor stream; NaN marks a gap.</summary>
    public void SetHistory(double[] values, int capacity)
    {
        HistoryCapacity = capacity;
        History = values;
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
    /// <summary>The adapter's download speed sensor.</summary>
    [ObservableProperty] public partial string? SensorId { get; set; }
    [ObservableProperty] public partial string Adapter { get; set; } = "No active adapter";
    [ObservableProperty] public partial string DownText { get; set; } = "—";
    [ObservableProperty] public partial string UpText { get; set; } = "—";
    [ObservableProperty] public partial string Totals { get; set; } = "";
    [ObservableProperty] public partial IReadOnlyList<double>? DownHistory { get; set; }
    [ObservableProperty] public partial IReadOnlyList<double>? UpHistory { get; set; }

    /// <summary>Shared scale so the download and upload lines are comparable.</summary>
    [ObservableProperty] public partial double Scale { get; set; } = 1;

    [ObservableProperty] public partial int HistoryCapacity { get; set; } = GaugeCardViewModel.HistoryLength;

    /// <summary>Recent download and upload rates from the sensor stream; NaN marks a gap.</summary>
    public void SetHistory(double[] down, double[] up, int capacity)
    {
        HistoryCapacity = capacity;
        DownHistory = down;
        UpHistory = up;
        var peak = down.Concat(up).Where(v => !double.IsNaN(v)).DefaultIfEmpty(0).Max();
        Scale = Math.Max(peak * 1.15, 1024);
    }
}
