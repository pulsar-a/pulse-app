using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibreHardwareMonitor.Hardware;
using Pulse.Controls;
using Pulse.Models;
using Pulse.Services;
using Pulse.Services.History;

namespace Pulse.ViewModels;

public sealed record DetailsRange(string Label, TimeSpan Span)
{
    public override string ToString() => Label;
}

/// <summary>Where "Pin to dashboard" can put a sensor: a new tile (null) or an existing pinned tile.</summary>
public sealed record PinChoice(string? TileId, string Label)
{
    public override string ToString() => Label;
}

public sealed record CompareChoice(string? SensorId, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Range statistics shown under the chart.</summary>
public sealed record DetailsStats(double Min, double Avg, double Max, double? FractionAbove, TimeSpan? TimeAbove);

/// <summary>
/// The sensor details panel: a large history chart for one sensor (and optionally a second to compare),
/// with range buttons, range statistics, and actions. Short ranges refresh with every reading from the
/// in-memory stream; long ranges re-read from disk every 30 seconds, off the UI thread.
/// </summary>
public partial class SensorDetailsViewModel : ObservableObject
{
    private const int MaxPoints = 600;
    private static readonly TimeSpan SlowRefresh = TimeSpan.FromSeconds(30);

    private readonly HistoryStore _store;
    private readonly SensorStream _stream;
    private readonly SensorCatalog _catalog;
    private readonly SettingsService _settings;
    private readonly Action<string> _createAlert;
    private readonly Dispatcher _dispatcher;
    private int _loadVersion;
    private DateTime _lastSlowLoad;
    private int _liveScheduled;

    public SensorDetailsViewModel(HistoryStore store, SensorStream stream, SensorCatalog catalog, SettingsService settings,
        Action<string> createAlert)
    {
        _store = store;
        _stream = stream;
        _catalog = catalog;
        _settings = settings;
        _createAlert = createAlert;
        _dispatcher = Dispatcher.CurrentDispatcher;
        SelectedRange = Ranges[1];
        _stream.SnapshotPublished += OnSnapshotPublished;
        _catalog.NamesChanged += () => { if (SensorId is not null) RefreshName(); };
        _settings.Changed += () => { if (IsOpen) RefreshPin(); };
    }

    /// <summary>The color of the dashboard card showing a sensor, so the chart matches it.</summary>
    public Func<string, Color?>? CardAccent { get; init; }

    /// <summary>The dashboard, for "Pin to dashboard"; null hides the button (tests, previews).</summary>
    public IPinBoard? PinBoard { get; init; }

    public bool CanPin => PinBoard is not null;

    [ObservableProperty] public partial bool IsPinned { get; private set; }

    /// <summary>A new tile, then every pinned tile the sensor could join.</summary>
    public ObservableCollection<PinChoice> PinChoices { get; } = [];

    private void RefreshPin()
    {
        if (PinBoard is null || SensorId is null) return;
        IsPinned = PinBoard.IsPinned(SensorId);
        PinChoices.Clear();
        PinChoices.Add(new PinChoice(null, "As a new tile"));
        foreach (var (id, title) in PinBoard.PinTargets())
            PinChoices.Add(new PinChoice(id, $"Add to “{title}”"));
    }

    [RelayCommand]
    private void PinTo(PinChoice? choice)
    {
        if (PinBoard is null || SensorId is null) return;
        PinBoard.Pin(SensorId, choice?.TileId);
        Status = "Pinned to the dashboard.";
    }

    [RelayCommand]
    private void Unpin()
    {
        if (PinBoard is null || SensorId is null) return;
        PinBoard.Unpin(SensorId);
        Status = "Removed from the dashboard.";
    }

    public IReadOnlyList<DetailsRange> Ranges { get; } =
    [
        new("5 min", TimeSpan.FromMinutes(5)),
        new("1 hour", TimeSpan.FromHours(1)),
        new("24 hours", TimeSpan.FromDays(1)),
        new("7 days", TimeSpan.FromDays(7)),
    ];

    [ObservableProperty] public partial bool IsOpen { get; set; }
    [ObservableProperty] public partial string? SensorId { get; private set; }
    [ObservableProperty] public partial string Name { get; private set; } = "";
    [ObservableProperty] public partial string Subtitle { get; private set; } = "";
    [ObservableProperty] public partial bool IsRenamed { get; private set; }
    [ObservableProperty] public partial string ValueText { get; private set; } = "—";
    [ObservableProperty] public partial Color Accent { get; private set; } = Palette.Cpu;
    [ObservableProperty] public partial Func<double, string> ValueFormatter { get; private set; } = v => v.ToString("0.#");

    [ObservableProperty] public partial DetailsRange SelectedRange { get; set; }
    [ObservableProperty] public partial IReadOnlyList<HistoryPoint>? Points { get; private set; }
    [ObservableProperty] public partial IReadOnlyList<HistoryPoint>? ComparePoints { get; private set; }
    [ObservableProperty] public partial DateTime From { get; private set; }
    [ObservableProperty] public partial DateTime To { get; private set; }

    [ObservableProperty] public partial string MinText { get; private set; } = "—";
    [ObservableProperty] public partial string AvgText { get; private set; } = "—";
    [ObservableProperty] public partial string MaxText { get; private set; } = "—";
    [ObservableProperty] public partial string? AboveText { get; private set; }

    public ObservableCollection<CompareChoice> CompareChoices { get; } = [];
    [ObservableProperty] public partial CompareChoice? SelectedCompare { get; set; }

    [ObservableProperty] public partial bool IsEditingName { get; set; }
    [ObservableProperty] public partial string EditName { get; set; } = "";
    [ObservableProperty] public partial string? Status { get; private set; }

    private SensorType _type;
    private double? _aboveThreshold;

    /// <summary>Opens the panel for a sensor (from the dashboard, All sensors, or Ctrl+K).</summary>
    public void Open(string sensorId)
    {
        SensorId = sensorId;
        var info = _catalog.Find(sensorId);
        _type = info?.Type ?? SensorType.Temperature;
        Accent = CardAccent?.Invoke(sensorId) ?? Palette.ForSensor(_type, info?.HardwareType);
        ValueFormatter = FormatterFor(_type);
        RefreshName();
        Status = null;
        IsEditingName = false;
        BuildCompareChoices();
        SelectedCompare = CompareChoices[0];
        UpdateLiveValue(_stream.Latest);
        RefreshPin();
        IsOpen = true;
        Log.Write($"Details opened for {sensorId}");
        _ = LoadAsync();
    }

    [RelayCommand]
    private void Close()
    {
        IsOpen = false;
        IsEditingName = false;
    }

    partial void OnSelectedRangeChanged(DetailsRange value) => _ = LoadAsync();
    partial void OnSelectedCompareChanged(CompareChoice? value) => _ = LoadAsync();

    [RelayCommand]
    private void SelectRange(DetailsRange range) => SelectedRange = range;

    private void RefreshName()
    {
        if (SensorId is not { } id) return;
        var info = _catalog.Find(id);
        Name = info?.DisplayName ?? id;
        IsRenamed = info?.IsRenamed ?? false;
        Subtitle = info is null ? "" : info.IsRenamed ? $"{info.OriginalName} · {info.HardwareName}" : info.HardwareName;
    }

    private void BuildCompareChoices()
    {
        CompareChoices.Clear();
        CompareChoices.Add(new CompareChoice(null, "Nothing"));
        if (_stream.Latest is not { } snapshot || SensorId is null)
            return;

        foreach (var (sensor, hardware) in WithOwners(snapshot.Hardware))
        {
            if (sensor.Type == _type && sensor.Id != SensorId && sensor.HasValue)
                CompareChoices.Add(new CompareChoice(sensor.Id, $"{_catalog.DisplayName(sensor)} · {hardware}"));
        }
    }

    // ---- Loading ------------------------------------------------------------------------------

    private async Task LoadAsync()
    {
        if (SensorId is not { } id || !IsOpen && Points is not null)
            return;

        var version = Interlocked.Increment(ref _loadVersion);
        var to = DateTime.Now;
        var from = to - SelectedRange.Span;
        var compareId = SelectedCompare?.SensorId;

        var points = await _store.QueryAsync(id, from, to, MaxPoints);
        var compare = compareId is null ? null : await _store.QueryAsync(compareId, from, to, MaxPoints);

        // A newer request (another range, another sensor) has started meanwhile: drop this one.
        if (version != _loadVersion)
            return;

        From = from;
        To = to;
        Points = points;
        ComparePoints = compare;
        _lastSlowLoad = DateTime.UtcNow;
        UpdateStats(points);
    }

    private void UpdateStats(IReadOnlyList<HistoryPoint> points)
    {
        if (_stream.Latest is { } snapshot && SensorId is { } id)
            _aboveThreshold = _type == SensorType.Temperature
                ? AlertEngine.AboveThresholdFor(id, snapshot, _settings.Current.Alerts.Rules, _catalog.RoleSensor(SensorRoles.Pump))
                : null;

        var stats = ComputeStats(points, _aboveThreshold);
        MinText = stats is null ? "—" : ValueFormatter(stats.Min);
        AvgText = stats is null ? "—" : ValueFormatter(stats.Avg);
        MaxText = stats is null ? "—" : ValueFormatter(stats.Max);
        AboveText = DescribeTimeAbove(stats, _aboveThreshold is { } limit ? ValueFormatter(limit) : null);
    }

    /// <summary>Min, average and max over the points; with a limit, how much of the range was spent above it.</summary>
    public static DetailsStats? ComputeStats(IReadOnlyList<HistoryPoint> points, double? limit)
    {
        if (points.Count == 0)
            return null;

        double? fraction = null;
        TimeSpan? time = null;
        if (limit is { } l)
        {
            fraction = points.Count(p => p.Avg > l) / (double)points.Count;
            time = (points[^1].Time - points[0].Time) * fraction.Value;
        }
        return new DetailsStats(points.Min(p => p.Min), points.Average(p => p.Avg), points.Max(p => p.Max), fraction, time);
    }

    /// <summary>"12 min above the 95° alert limit (8%)", or null when the sensor has no alert limit.</summary>
    public static string? DescribeTimeAbove(DetailsStats? stats, string? limitText)
    {
        if (stats?.TimeAbove is not { } time || limitText is null)
            return null;
        return stats.FractionAbove > 0
            ? $"{FormatDuration(time)} above the {limitText} alert limit ({Math.Max(stats.FractionAbove.Value * 100, 1):0}% of this range)"
            : $"Stayed below the {limitText} alert limit the whole time";
    }

    private static string FormatDuration(TimeSpan time) =>
        time.TotalHours >= 1 ? $"{(int)time.TotalHours} h {time.Minutes} min"
        : time.TotalMinutes >= 1 ? $"{(int)time.TotalMinutes} min"
        : $"{time.Seconds} s";

    // ---- Live updates -------------------------------------------------------------------------

    /// <summary>Polling thread: refresh the live value, and the chart for short ranges (or every 30 s for long ones).</summary>
    private void OnSnapshotPublished(HardwareSnapshot snapshot)
    {
        if (!IsOpen || Interlocked.Exchange(ref _liveScheduled, 1) == 1)
            return;
        _dispatcher.BeginInvoke(() =>
        {
            Interlocked.Exchange(ref _liveScheduled, 0);
            if (!IsOpen) return;
            UpdateLiveValue(snapshot);
            if (SelectedRange.Span <= TimeSpan.FromHours(1) || DateTime.UtcNow - _lastSlowLoad >= SlowRefresh)
                _ = LoadAsync();
        }, DispatcherPriority.Background);
    }

    private void UpdateLiveValue(HardwareSnapshot? snapshot)
    {
        var sensor = snapshot?.Hardware.SelectMany(h => h.AllSensors).FirstOrDefault(s => s.Id == SensorId);
        ValueText = sensor is { Value: { } v } ? SensorFormat.Format(_type, v) : "No reading";
    }

    // ---- Actions ------------------------------------------------------------------------------

    [RelayCommand]
    private void BeginRename()
    {
        EditName = Name;
        IsEditingName = true;
    }

    [RelayCommand]
    private void CommitRename()
    {
        if (!IsEditingName || SensorId is null) return;
        IsEditingName = false;
        if (EditName.Trim() != Name)
            _catalog.Rename(SensorId, EditName);
    }

    [RelayCommand]
    private void CancelRename() => IsEditingName = false;

    [RelayCommand]
    private void ResetName()
    {
        if (SensorId is not null)
            _catalog.ResetName(SensorId);
    }

    [RelayCommand]
    private void CreateAlert()
    {
        if (SensorId is null) return;
        IsOpen = false;
        _createAlert(SensorId);
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (SensorId is not { } id) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export sensor history",
            Filter = "CSV file (*.csv)|*.csv",
            FileName = $"{string.Concat(Name.Split(Path.GetInvalidFileNameChars()))} {DateTime.Now:yyyy-MM-dd HHmm}.csv",
        };
        if (dialog.ShowDialog() != true)
            return;

        var to = DateTime.Now;
        var points = await _store.QueryAsync(id, to - SelectedRange.Span, to, int.MaxValue);
        try
        {
            await File.WriteAllLinesAsync(dialog.FileName, ToCsv(points, _type), new System.Text.UTF8Encoding(true));
            Status = $"Exported {points.Count:N0} points.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"Couldn't export: {ex.Message}";
        }
    }

    /// <summary>One sensor's points as CSV: time, min, average, max (temperatures in the user's unit).</summary>
    public static IEnumerable<string> ToCsv(IReadOnlyList<HistoryPoint> points, SensorType type)
    {
        double Show(double v) => type == SensorType.Temperature ? SensorFormat.ToDisplayTemperature(v) : v;
        yield return "Time,Min,Average,Max";
        foreach (var p in points)
            yield return string.Join(",",
                p.Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                Show(p.Min).ToString("0.##", CultureInfo.InvariantCulture),
                Show(p.Avg).ToString("0.##", CultureInfo.InvariantCulture),
                Show(p.Max).ToString("0.##", CultureInfo.InvariantCulture));
    }

    // ---- Look ---------------------------------------------------------------------------------


    /// <summary>Axis and tooltip formatting: compact for temperatures and rates, the standard units otherwise.</summary>
    public static Func<double, string> FormatterFor(SensorType type) => type switch
    {
        SensorType.Temperature => v => SensorFormat.Degrees(v, "0.#"),
        SensorType.Throughput => v => SensorFormat.Rate((float)v),
        _ => v => SensorFormat.Format(type, (float)v),
    };

    private static IEnumerable<(SensorReading Sensor, string Hardware)> WithOwners(IEnumerable<HardwareReading> hardware)
    {
        foreach (var item in hardware)
        {
            foreach (var sensor in item.Sensors)
                yield return (sensor, item.Name);
            foreach (var sub in WithOwners(item.SubHardware))
                yield return sub;
        }
    }
}
