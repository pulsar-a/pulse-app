using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibreHardwareMonitor.Hardware;
using Pulse.Controls;
using Pulse.Models;
using Pulse.Services;
using Wpf.Ui.Controls;

namespace Pulse.ViewModels;

/// <summary>
/// Every sensor LHM reports, grouped by hardware and sensor type, with a search filter.
/// The groups are flattened into one list of headers and rows (<see cref="Items"/>) so the view
/// can virtualize it: only the rows on screen get UI elements, however many sensors there are.
/// </summary>
public partial class SensorsViewModel : ObservableObject
{
    private readonly Dictionary<string, SensorRowViewModel> _rows = new();
    private readonly List<HardwareGroupViewModel> _groups = [];
    private readonly HashSet<string> _expandedGroups = [];
    private readonly DispatcherTimer _filterDebounce;
    private readonly SensorCatalog _catalog;
    private readonly Action<string>? _showInTray;
    private readonly Action<string>? _createAlert;
    private readonly Action<string>? _openDetails;
    private HardwareSnapshot? _latest;
    private string _layoutKey = "";

    /// <param name="showInTray">Shows a sensor in the tray icon; null hides that menu item.</param>
    /// <param name="createAlert">Creates an alert rule for a sensor; null hides that menu item.</param>
    public SensorsViewModel(SensorCatalog catalog, Action<string>? showInTray = null, Action<string>? createAlert = null,
        Action<string>? openDetails = null)
    {
        _catalog = catalog;
        _openDetails = openDetails;
        _showInTray = showInTray;
        _createAlert = createAlert;
        _catalog.NamesChanged += OnNamesChanged;

        // Rebuild the list once typing pauses, not on every keystroke.
        _filterDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _filterDebounce.Tick += (_, _) =>
        {
            _filterDebounce.Stop();
            RefreshItems();
        };
    }

    /// <summary>Pins a sensor to the dashboard; null hides that menu item.</summary>
    public Action<string>? Pin { get; init; }

    /// <summary>Hardware headers, sensor-type headers and sensor rows, in display order.</summary>
    public BulkObservableCollection<object> Items { get; } = [];

    [ObservableProperty] public partial string Filter { get; set; } = "";
    [ObservableProperty] public partial string Summary { get; set; } = "";
    [ObservableProperty] public partial bool IsLoading { get; set; } = true;
    [ObservableProperty] public partial bool HasNoMatches { get; set; }

    /// <summary>
    /// Whether the page is on screen. While it's hidden, sensor rows aren't updated at all;
    /// when it comes back, the newest snapshot is applied straight away.
    /// </summary>
    [ObservableProperty] public partial bool IsActive { get; set; }

    partial void OnFilterChanged(string value)
    {
        _filterDebounce.Stop();
        _filterDebounce.Start();
    }

    /// <summary>A rename shows up in every row at once, and search re-runs against the new names.</summary>
    private void OnNamesChanged()
    {
        foreach (var row in _rows.Values)
            row.RefreshName();
        if (Filter.Trim().Length > 0)
            RefreshItems();
    }

    /// <summary>Applies a pending search right away instead of waiting for the typing pause.</summary>
    internal void FlushFilter()
    {
        _filterDebounce.Stop();
        RefreshItems();
    }

    partial void OnIsActiveChanged(bool value)
    {
        if (value && _latest is not null)
            UpdateRows(_latest);
    }

    private void ToggleGroup(HardwareGroupViewModel group)
    {
        group.IsExpanded = !group.IsExpanded;
        if (group.IsExpanded)
            _expandedGroups.Add(group.Key);
        else
            _expandedGroups.Remove(group.Key);
        RefreshItems();
    }

    public void Apply(HardwareSnapshot snapshot)
    {
        _latest = snapshot;

        // Rebuild the groups only when the set of sensors changes, which is rare; otherwise update in place.
        var layoutKey = string.Join("|", snapshot.Hardware.SelectMany(h => h.AllSensors).Select(s => s.Id));
        if (layoutKey != _layoutKey)
        {
            _layoutKey = layoutKey;
            Rebuild(Flatten(snapshot.Hardware.OrderBy(h => HardwareOrder(h.Type)), parent: null).ToList(), snapshot.Volumes);
            UpdateRows(snapshot);
            return;
        }

        if (IsActive)
            UpdateRows(snapshot);
    }

    private void UpdateRows(HardwareSnapshot snapshot)
    {
        foreach (var item in Flatten(snapshot.Hardware, parent: null).Select(h => h.Hardware))
        {
            foreach (var sensor in item.Sensors)
            {
                if (_rows.TryGetValue(sensor.Id, out var row))
                    row.Update(sensor);
            }
        }
    }

    private void Rebuild(
        List<(HardwareReading Hardware, string? Parent)> hardware,
        IReadOnlyDictionary<int, IReadOnlyList<VolumeInfo>> volumes)
    {
        _groups.Clear();
        _rows.Clear();

        foreach (var (item, parent) in hardware)
        {
            if (item.Sensors.Count == 0)
                continue;

            var types = item.Sensors
                .GroupBy(s => SensorFormat.TypeTitle(s.Type))
                .OrderBy(g => SensorFormat.TypeOrder(g.First().Type))
                .Select(g =>
                {
                    var rows = g.Select(s => _rows[s.Id] = new SensorRowViewModel(s.Id, s.Name, s.Type, _catalog, _showInTray, _createAlert, _openDetails, Pin)).ToList();
                    return new SensorTypeGroupViewModel(g.Key, rows);
                })
                .ToList();

            var group = new HardwareGroupViewModel(
                item.Id,
                item.Name,
                Subtitle(item, parent, volumes),
                HardwareIcon(item.Type),
                types,
                ToggleGroup);
            // Collapsed by default; groups the user opened stay open if the list is rebuilt.
            group.IsExpanded = _expandedGroups.Contains(group.Key);
            _groups.Add(group);
        }

        Summary = $"{_rows.Count} sensors on {_groups.Count} devices";
        IsLoading = false;
        RefreshItems();
    }

    /// <summary>Rebuilds the flat list from the groups, applying the search filter and collapsed groups.</summary>
    private void RefreshItems()
    {
        var filter = Filter.Trim();
        var searching = filter.Length > 0;
        var items = new List<object>(_rows.Count + _groups.Count * 4);

        foreach (var group in _groups)
        {
            var groupMatches = !searching
                               || group.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                               || group.Subtitle.Contains(filter, StringComparison.OrdinalIgnoreCase);

            var visibleTypes = group.Types
                .Select(type =>
                {
                    var typeMatches = groupMatches || type.Title.Contains(filter, StringComparison.OrdinalIgnoreCase);
                    var rows = typeMatches
                        ? type.Rows
                        : type.Rows.Where(r => r.Matches(filter)).ToList();
                    return (Type: type, Rows: rows);
                })
                .Where(t => t.Rows.Count > 0)
                .ToList();

            if (visibleTypes.Count == 0)
                continue;

            items.Add(group);

            // While searching, show matches even inside collapsed groups.
            if (!group.IsExpanded && !searching)
                continue;

            foreach (var (type, rows) in visibleTypes)
            {
                items.Add(type);
                items.AddRange(rows);
            }
        }

        Items.ReplaceAll(items);
        HasNoMatches = searching && items.Count == 0;
    }

    private static IEnumerable<(HardwareReading Hardware, string? Parent)> Flatten(
        IEnumerable<HardwareReading> hardware, string? parent)
    {
        foreach (var item in hardware)
        {
            yield return (item, parent);
            foreach (var sub in Flatten(item.SubHardware, item.Name))
                yield return sub;
        }
    }

    /// <summary>"Storage · C: 4TB Main, Z: Grub" for drives; "Sensor chip · ASUS ROG ..." for sub-hardware.</summary>
    private static string Subtitle(
        HardwareReading item, string? parent, IReadOnlyDictionary<int, IReadOnlyList<VolumeInfo>> volumes)
    {
        var title = HardwareTitle(item.Type);
        if (parent is not null)
            return $"{title} · {parent}";

        if (item.Type == HardwareType.Storage
            && int.TryParse(item.Id[(item.Id.LastIndexOf('/') + 1)..], out var disk)
            && volumes.TryGetValue(disk, out var list) && list.Count > 0)
        {
            return $"{title} · " + string.Join(", ", list.Select(v => $"{v.Letter}: {v.Label}".TrimEnd()));
        }

        return title;
    }

    /// <summary>Most-watched hardware first; the sort is stable, so devices of one type keep LHM's order.</summary>
    private static int HardwareOrder(HardwareType type) => type switch
    {
        HardwareType.Cpu => 0,
        HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => 1,
        HardwareType.Memory => 2,
        HardwareType.Motherboard => 3,
        HardwareType.Storage => 4,
        HardwareType.Psu => 5,
        HardwareType.Cooler => 6,
        HardwareType.Network => 7,
        _ => 8,
    };

    public static string HardwareTitle(HardwareType type) => type switch
    {
        HardwareType.Motherboard => "Motherboard",
        HardwareType.SuperIO => "Sensor chip",
        HardwareType.EmbeddedController => "Embedded controller",
        HardwareType.Cpu => "Processor",
        HardwareType.Memory => "Memory",
        HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => "Graphics",
        HardwareType.Storage => "Storage",
        HardwareType.Network => "Network",
        HardwareType.Cooler => "Cooler",
        HardwareType.Psu => "Power supply",
        HardwareType.Battery => "Battery",
        HardwareType.PowerMonitor => "Power monitor",
        _ => type.ToString(),
    };

    public static SymbolRegular HardwareIcon(HardwareType type) => type switch
    {
        HardwareType.Cpu => SymbolRegular.DeveloperBoard24,
        HardwareType.Memory => SymbolRegular.Ram20,
        HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => SymbolRegular.DesktopPulse24,
        HardwareType.Storage => SymbolRegular.Storage24,
        HardwareType.Network => SymbolRegular.Router24,
        HardwareType.Cooler => SymbolRegular.WeatherSqualls24,
        HardwareType.Psu => SymbolRegular.PlugConnected24,
        HardwareType.Battery => SymbolRegular.Battery524,
        HardwareType.PowerMonitor => SymbolRegular.Flash24,
        _ => SymbolRegular.Gauge24,
    };
}

public partial class HardwareGroupViewModel : ObservableObject
{
    public HardwareGroupViewModel(
        string key,
        string name,
        string subtitle,
        SymbolRegular icon,
        IReadOnlyList<SensorTypeGroupViewModel> types,
        Action<HardwareGroupViewModel> toggle)
    {
        Key = key;
        Name = name;
        Subtitle = subtitle;
        Icon = icon;
        Types = types;
        SensorCount = types.Sum(t => t.Rows.Count);
        // The command lives on the group itself rather than taking it as a parameter: the list
        // recycles row controls, and a recycled header could otherwise hand it a sensor row.
        ToggleCommand = new RelayCommand(() => toggle(this));
    }

    /// <summary>LHM hardware identifier; stable across list rebuilds.</summary>
    public string Key { get; }
    public string Name { get; }
    public string Subtitle { get; }
    public SymbolRegular Icon { get; }
    public IReadOnlyList<SensorTypeGroupViewModel> Types { get; }
    public int SensorCount { get; }
    public IRelayCommand ToggleCommand { get; }

    [ObservableProperty] public partial bool IsExpanded { get; set; }
}

public sealed class SensorTypeGroupViewModel(string title, IReadOnlyList<SensorRowViewModel> rows)
{
    public string Title { get; } = title;
    public IReadOnlyList<SensorRowViewModel> Rows { get; } = rows;
}

public partial class SensorRowViewModel : ObservableObject
{
    private readonly SensorCatalog _catalog;
    private readonly Action<string>? _showInTray;
    private readonly Action<string>? _openDetailsAction;
    private readonly Action<string>? _pin;
    private readonly Action<string>? _createAlert;

    public SensorRowViewModel(string id, string originalName, SensorType type, SensorCatalog catalog, Action<string>? showInTray = null, Action<string>? createAlert = null,
        Action<string>? openDetails = null, Action<string>? pin = null)
    {
        _pin = pin;
        _showInTray = showInTray;
        _openDetailsAction = openDetails;
        _createAlert = createAlert;
        Id = id;
        OriginalName = originalName;
        Type = type;
        _catalog = catalog;
        RefreshName();
    }

    public string Id { get; }
    public string OriginalName { get; }
    public SensorType Type { get; }

    /// <summary>What the row shows: the user's name if they renamed it, otherwise the original.</summary>
    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial bool IsRenamed { get; set; }

    [ObservableProperty] public partial string ValueText { get; set; } = SensorFormat.Empty;
    [ObservableProperty] public partial string MinText { get; set; } = SensorFormat.Empty;
    [ObservableProperty] public partial string MaxText { get; set; } = SensorFormat.Empty;

    /// <summary>Heat color for temperatures; null means "use the normal text color".</summary>
    [ObservableProperty] public partial Brush? ValueBrush { get; set; }

    [ObservableProperty] public partial bool IsEditing { get; set; }
    [ObservableProperty] public partial string EditText { get; set; } = "";

    public bool Matches(string filter) =>
        Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || OriginalName.Contains(filter, StringComparison.OrdinalIgnoreCase);

    public void RefreshName()
    {
        Name = _catalog.DisplayName(Id, OriginalName);
        IsRenamed = _catalog.IsRenamed(Id);
    }

    public void Update(SensorReading reading)
    {
        ValueText = SensorFormat.Format(Type, reading.Value);
        MinText = SensorFormat.Format(Type, reading.Min);
        MaxText = SensorFormat.Format(Type, reading.Max);

        // Color temperatures by how hot they are; distances to TjMax count down, so leave those neutral.
        ValueBrush = Type == SensorType.Temperature && reading.HasValue && !OriginalName.Contains("Distance")
            ? Palette.Temperature(reading.Value!.Value)
            : null;
    }

    [RelayCommand]
    private void BeginRename()
    {
        EditText = Name;
        IsEditing = true;
    }

    [RelayCommand]
    private void CommitRename()
    {
        if (!IsEditing)
            return;
        IsEditing = false;
        if (EditText.Trim() != Name)
            _catalog.Rename(Id, EditText);
    }

    [RelayCommand]
    private void CancelRename() => IsEditing = false;

    [RelayCommand]
    private void ResetName() => _catalog.ResetName(Id);

    public bool CanShowInTray => _showInTray is not null;

    [RelayCommand]
    private void ShowInTray() => _showInTray?.Invoke(Id);

    public bool CanCreateAlert => _createAlert is not null;

    [RelayCommand]
    private void CreateAlert() => _createAlert?.Invoke(Id);

    [RelayCommand]
    private void OpenDetails() => _openDetailsAction?.Invoke(Id);

    public bool CanPin => _pin is not null;

    [RelayCommand]
    private void PinToDashboard() => _pin?.Invoke(Id);
}
