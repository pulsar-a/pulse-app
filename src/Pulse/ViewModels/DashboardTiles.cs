using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibreHardwareMonitor.Hardware;
using Pulse.Controls;
using Pulse.Models;
using Pulse.Services;

namespace Pulse.ViewModels;

/// <summary>One choice in a tile's size menu, with how much of a row it takes.</summary>
public sealed record TileSizeOption(TileSize Size, string Label, string Hint, int Span, bool IsCurrent, IRelayCommand Choose)
{
    /// <summary>Width of the little preview bar in the menu, out of a 72 px track.</summary>
    public double PreviewWidth => 72.0 * Span / DashboardLayout.Columns;
}

/// <summary>
/// A place on the dashboard: which card it holds, how wide it is, and its edit-mode state.
/// The card itself (<see cref="Card"/>) is picked by type through the view's DataTemplates.
/// </summary>
public partial class DashboardTileViewModel : ObservableObject
{
    private readonly Action<DashboardTileViewModel, TileSize>? _resize;

    public DashboardTileViewModel(string id, TileKind kind, object card, Action<DashboardTileViewModel, TileSize>? resize = null)
    {
        Id = id;
        Kind = kind;
        Card = card;
        Sizes = DashboardLayout.AllowedSizes(kind);
        _resize = resize;
    }

    public string Id { get; }
    public TileKind Kind { get; }
    public object Card { get; }
    public IReadOnlyList<TileSize> Sizes { get; }

    public bool IsPinned => Kind == TileKind.Pinned;
    public bool CanResize => Sizes.Count > 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Span), nameof(SizeLabel), nameof(SizeOptions))]
    public partial TileSize Size { get; set; }

    /// <summary>The size menu: each size this card can take, the current one checked.</summary>
    public IReadOnlyList<TileSizeOption> SizeOptions => Sizes
        .Select(size => new TileSizeOption(size, SizeName(size), SizeHint(size), DashboardLayout.Span(size), size == Size,
            new RelayCommand(() => _resize?.Invoke(this, size))))
        .ToList();

    private static string SizeName(TileSize size) => size switch
    {
        TileSize.Small => "Small",
        TileSize.Medium => "Medium",
        TileSize.Large => "Large",
        TileSize.Wide => "Extra large",
        _ => "Full width",
    };

    private static string SizeHint(TileSize size) => size switch
    {
        TileSize.Small => "a quarter of a row",
        TileSize.Medium => "a third of a row",
        TileSize.Large => "half a row",
        TileSize.Wide => "two thirds of a row",
        _ => "the whole row",
    };

    /// <summary>Columns out of <see cref="DashboardLayout.Columns"/>.</summary>
    public int Span => DashboardLayout.Span(Size);

    public string SizeLabel => Size switch
    {
        TileSize.Small => "S",
        TileSize.Medium => "M",
        TileSize.Large => "L",
        TileSize.Wide => "XL",
        _ => "Full",
    };

    /// <summary>The name used for the tile in edit mode (the hidden-cards tray, tooltips).</summary>
    public string Title => Card switch
    {
        PinnedTileViewModel pinned => pinned.Title,
        _ => Kind switch
        {
            TileKind.Processor => "Processor",
            TileKind.Graphics => "Graphics",
            TileKind.Memory => "Memory",
            TileKind.Power => "System power",
            TileKind.Cores => "Cores",
            TileKind.Cooling => "CPU cooling",
            TileKind.Fans => "Fans",
            TileKind.Drives => "Drives",
            TileKind.Network => "Network",
            TileKind.Processes => "Top processes",
            _ => Kind.ToString(),
        },
    };

    /// <summary>
    /// The narrowest this card reads well at, in pixels. In a narrow window the tile takes more columns
    /// rather than get narrower (a ring gauge needs about 250 px, the drive list about 330).
    /// </summary>
    public double MinWidth => Card switch
    {
        PinnedTileViewModel pinned => pinned.IsGroup ? 300 : 220,
        _ => Kind switch
        {
            TileKind.Processor or TileKind.Graphics or TileKind.Memory or TileKind.Power => 250,
            TileKind.Cores => 360,
            TileKind.Cooling => 320,
            TileKind.Fans => 260,
            TileKind.Drives => 330,
            TileKind.Network => 280,
            TileKind.Processes => 300,
            _ => 240,
        },
    };

    /// <summary>True while this tile is being dragged; it fades so the drop position is visible.</summary>
    [ObservableProperty] public partial bool IsDragging { get; set; }

    /// <summary>A pinned tile's title and width needs change with its sensors.</summary>
    public void RefreshTitle()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(MinWidth));
    }
}

/// <summary>
/// Sensors the user pinned. One sensor shows large, with a sparkline; two or more show as a group of rows.
/// </summary>
public partial class PinnedTileViewModel : ObservableObject
{
    public PinnedTileViewModel(string id) => Id = id;

    public string Id { get; }
    public ObservableCollection<PinnedSensorViewModel> Sensors { get; } = [];

    /// <summary>A title the user gave the group, if any.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    public partial string? CustomTitle { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(Primary))]
    public partial bool IsGroup { get; private set; }

    /// <summary>The hottest of its sensors that have an alert limit.</summary>
    [ObservableProperty] public partial HeatLevel Heat { get; private set; }

    public void UpdateHeat(Func<PinnedSensorViewModel, HeatLevel> heat)
    {
        var level = HeatLevel.None;
        foreach (var sensor in Sensors)
        {
            sensor.Heat = heat(sensor);
            if (sensor.Heat > level) level = sensor.Heat;
        }
        Heat = level;
    }

    public PinnedSensorViewModel? Primary => Sensors.FirstOrDefault();

    public string Title => !string.IsNullOrWhiteSpace(CustomTitle) ? CustomTitle!
        : IsGroup ? "Pinned sensors"
        : Primary?.Name ?? "Pinned sensor";

    /// <summary>Matches the sensors to the saved tile, keeping row view models that are still there.</summary>
    public void Configure(TileSettings tile)
    {
        CustomTitle = tile.Title;
        var ids = tile.Sensors ?? [];
        if (!Sensors.Select(s => s.SensorId).SequenceEqual(ids))
        {
            var existing = Sensors.ToDictionary(s => s.SensorId);
            Sensors.Clear();
            foreach (var id in ids)
                Sensors.Add(existing.GetValueOrDefault(id) ?? new PinnedSensorViewModel(id));
        }
        IsGroup = Sensors.Count > 1;
        OnPropertyChanged(nameof(Primary));
        OnPropertyChanged(nameof(Title));
    }

    public void Update(IReadOnlyDictionary<string, (SensorReading Sensor, string Hardware, HardwareType Type)> sensors,
        SensorCatalog catalog, SensorStream stream, int samples, Func<string, Color?> cardAccent)
    {
        foreach (var row in Sensors)
        {
            if (sensors.TryGetValue(row.SensorId, out var found))
                row.Update((found.Sensor, found.Hardware), catalog, stream, samples,
                    cardAccent(row.SensorId) ?? Palette.ForSensor(found.Sensor.Type, found.Type));
            else
                row.Update(null, catalog, stream, samples, Palette.Cpu);
        }
        OnPropertyChanged(nameof(Title));
    }
}

/// <summary>One pinned sensor: its value, session range and the last minute as a sparkline.</summary>
public partial class PinnedSensorViewModel : ObservableObject
{
    private Color? _accent;

    public PinnedSensorViewModel(string sensorId) => SensorId = sensorId;

    public string SensorId { get; }

    [ObservableProperty] public partial string Name { get; private set; } = "";
    [ObservableProperty] public partial string Hardware { get; private set; } = "";
    [ObservableProperty] public partial string ValueText { get; private set; } = "—";
    [ObservableProperty] public partial string? MinText { get; private set; }
    [ObservableProperty] public partial string? MaxText { get; private set; }
    [ObservableProperty] public partial bool IsMissing { get; private set; }
    [ObservableProperty] public partial HeatLevel Heat { get; set; }

    /// <summary>The latest reading, for the heat rules.</summary>
    public double? Value { get; private set; }
    [ObservableProperty] public partial IReadOnlyList<double>? History { get; private set; }
    [ObservableProperty] public partial int HistoryCapacity { get; private set; } = GaugeCardViewModel.HistoryLength;
    [ObservableProperty] public partial Color Accent { get; private set; } = Palette.Cpu;
    [ObservableProperty] public partial Brush AccentBrush { get; private set; } = Brushes.Transparent;
    [ObservableProperty] public partial Brush AccentSoftBrush { get; private set; } = Brushes.Transparent;

    public void Update((SensorReading Sensor, string Hardware)? found, SensorCatalog catalog, SensorStream stream, int samples, Color accent)
    {
        if (found is not { } f)
        {
            // Not in this snapshot: unplugged, or only readable as administrator. Keep the tile, say why.
            IsMissing = true;
            Value = null;
            if (Name.Length == 0)
                Name = catalog.Settings.Current.Sensors.Names.GetValueOrDefault(SensorId) ?? SensorId;
            Hardware = "Not detected right now";
            ValueText = "—";
            MinText = MaxText = null;
            History = [];
            return;
        }

        var sensor = f.Sensor;
        Value = sensor.HasValue ? sensor.Value : null;
        IsMissing = false;
        Name = catalog.DisplayName(sensor);
        Hardware = f.Hardware;
        ValueText = sensor.HasValue ? SensorFormat.Format(sensor.Type, sensor.Value) : "No reading";
        MinText = sensor.Min is { } min ? SensorFormat.Format(sensor.Type, min) : null;
        MaxText = sensor.Max is { } max ? SensorFormat.Format(sensor.Type, max) : null;
        HistoryCapacity = samples;
        History = stream.Recent(sensor.Id, samples);

        if (_accent != accent)
        {
            _accent = accent;
            Accent = accent;
            AccentBrush = Frozen(accent, 0xFF);
            AccentSoftBrush = Frozen(accent, 0x29);
        }
    }

    private static SolidColorBrush Frozen(Color color, byte alpha)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }
}

/// <summary>The per-core strip.</summary>
public partial class CoresCardViewModel : ObservableObject
{
    public ObservableCollection<CoreTileViewModel> Cores { get; } = [];
    [ObservableProperty] public partial string Title { get; set; } = "Cores";
    [ObservableProperty] public partial string Subtitle { get; set; } = "";
}

public partial class FansCardViewModel : ObservableObject
{
    public ObservableCollection<FanRowViewModel> Rows { get; } = [];
    [ObservableProperty] public partial bool HasFans { get; set; }
    [ObservableProperty] public partial string EmptyText { get; set; } = "";
}

public partial class DrivesCardViewModel : ObservableObject
{
    public ObservableCollection<DriveRowViewModel> Rows { get; } = [];
    [ObservableProperty] public partial bool HasDrives { get; set; }

    /// <summary>From the hottest drive.</summary>
    [ObservableProperty] public partial HeatLevel Heat { get; set; }
}
