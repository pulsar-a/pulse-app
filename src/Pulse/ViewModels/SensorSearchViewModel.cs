using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pulse.Services;

namespace Pulse.ViewModels;

/// <summary>One row in the Ctrl+K palette.</summary>
public sealed record SearchResult(SensorInfo Sensor, string ValueText)
{
    public string Name => Sensor.DisplayName;
    public string Detail => Sensor.IsRenamed
        ? $"{Sensor.OriginalName} · {Sensor.HardwareName} · {Sensor.Type}"
        : $"{Sensor.HardwareName} · {Sensor.Type}";
}

/// <summary>
/// The Ctrl+K sensor search: type part of a name (custom or original), a hardware name or a sensor kind,
/// pick a result with the arrow keys and Enter to open its details.
/// </summary>
public partial class SensorSearchViewModel : ObservableObject
{
    private const int MaxResults = 40;

    private readonly SensorCatalog _catalog;
    private readonly SensorStream _stream;
    private readonly Action<string> _openDetails;
    private IReadOnlyList<SensorInfo> _sensors = [];
    private Dictionary<string, float?> _values = new();

    public SensorSearchViewModel(SensorCatalog catalog, SensorStream stream, Action<string> openDetails)
    {
        _catalog = catalog;
        _stream = stream;
        _openDetails = openDetails;
    }

    [ObservableProperty] public partial bool IsOpen { get; private set; }
    [ObservableProperty] public partial string Query { get; set; } = "";
    [ObservableProperty] public partial SearchResult? SelectedResult { get; set; }
    [ObservableProperty] public partial bool HasNoResults { get; private set; }

    public ObservableCollection<SearchResult> Results { get; } = [];

    /// <summary>Opens the palette with an empty query, taking a fresh copy of the sensor list.</summary>
    public void Open()
    {
        _sensors = _catalog.AllSensors();
        _values = new Dictionary<string, float?>();
        foreach (var sensor in _stream.Latest?.Hardware.SelectMany(h => h.AllSensors) ?? [])
            _values[sensor.Id] = sensor.Value;
        Query = "";
        Refresh();
        IsOpen = true;
    }

    [RelayCommand]
    public void Close() => IsOpen = false;

    partial void OnQueryChanged(string value) => Refresh();

    private void Refresh()
    {
        Results.Clear();
        foreach (var sensor in Search(_sensors, Query, MaxResults))
        {
            var value = _values.GetValueOrDefault(sensor.Id);
            Results.Add(new SearchResult(sensor, value is { } v ? SensorFormat.Format(sensor.Type, v) : "—"));
        }
        SelectedResult = Results.FirstOrDefault();
        HasNoResults = Results.Count == 0;
    }

    /// <summary>Moves the highlight up or down, wrapping around.</summary>
    public void Move(int delta)
    {
        if (Results.Count == 0) return;
        var index = SelectedResult is null ? -1 : Results.IndexOf(SelectedResult);
        SelectedResult = Results[((index + delta) % Results.Count + Results.Count) % Results.Count];
    }

    [RelayCommand]
    public void Choose(SearchResult? result = null)
    {
        if ((result ?? SelectedResult) is not { } chosen) return;
        IsOpen = false;
        _openDetails(chosen.Sensor.Id);
    }

    /// <summary>
    /// Every word of the query must appear in the sensor's custom name, original name, hardware or kind.
    /// Names that start with the query rank first, then names that contain it, then the rest.
    /// </summary>
    public static IEnumerable<SensorInfo> Search(IReadOnlyList<SensorInfo> sensors, string query, int max)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
            return sensors.Take(max);

        var phrase = query.Trim();
        return sensors
            .Select((sensor, order) => (sensor, order, text: $"{sensor.DisplayName} {sensor.OriginalName} {sensor.HardwareName} {sensor.Type}"))
            .Where(x => words.All(w => x.text.Contains(w, StringComparison.CurrentCultureIgnoreCase)))
            .OrderBy(x => x.sensor.DisplayName.StartsWith(phrase, StringComparison.CurrentCultureIgnoreCase) ? 0
                : x.sensor.DisplayName.Contains(phrase, StringComparison.CurrentCultureIgnoreCase) ? 1 : 2)
            .ThenBy(x => x.order)
            .Take(max)
            .Select(x => x.sensor);
    }
}
