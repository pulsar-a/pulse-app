using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using LibreHardwareMonitor.Hardware;
using Pulse.Controls;
using Pulse.Models;
using Pulse.Services;

namespace Pulse.ViewModels;

/// <summary>One readout in the mini window.</summary>
public partial class MiniItemViewModel : ObservableObject
{
    public MiniItemViewModel(string item) => Item = item;

    /// <summary>A sensor id, or one of the automatic items (<see cref="MiniItems"/>).</summary>
    public string Item { get; }

    [ObservableProperty] public partial string Label { get; set; } = "";
    [ObservableProperty] public partial string ValueText { get; set; } = "—";
    [ObservableProperty] public partial Brush AccentBrush { get; set; } = Brushes.Gray;
}

/// <summary>
/// The mini window's readouts. The automatic ones (CPU, GPU, pump, power) are worked out from each
/// reading directly, so the mini window keeps working while the main window, and its dashboard,
/// aren't updating. Only updates while the mini window is open.
/// </summary>
public partial class MiniViewModel : ObservableObject
{
    private static readonly string[] CpuTemperatureNames = ["CPU Package", "Core Max", "Core Average", "Core (Tctl/Tdie)"];
    private static readonly Dictionary<Color, Brush> Brushes = new();

    private readonly SensorCatalog _catalog;
    private readonly SettingsService _settings;
    private readonly Dispatcher _dispatcher;
    private int _pending;

    public MiniViewModel(SensorCatalog catalog, SensorStream stream, SettingsService settings, Dispatcher dispatcher)
    {
        _catalog = catalog;
        _settings = settings;
        _dispatcher = dispatcher;
        ApplySettings();
        settings.Changed += ApplySettings;
        stream.SnapshotPublished += OnSnapshotPublished;
        if (stream.Latest is { } latest)
            Apply(latest);
    }

    public ObservableCollection<MiniItemViewModel> Items { get; } = [];

    [ObservableProperty] public partial bool IsHorizontal { get; private set; }
    [ObservableProperty] public partial double WindowOpacity { get; private set; } = 0.9;

    /// <summary>Set while the mini window is on screen; no work at all otherwise.</summary>
    public bool IsActive { get; set; }

    private void ApplySettings()
    {
        var mini = _settings.Current.Mini;
        IsHorizontal = mini.Horizontal;
        WindowOpacity = Math.Clamp(mini.Opacity, 0.4, 1);

        var wanted = mini.ItemsOrDefault;
        if (!Items.Select(i => i.Item).SequenceEqual(wanted))
        {
            Items.Clear();
            foreach (var item in wanted)
                Items.Add(new MiniItemViewModel(item));
        }
    }

    /// <summary>Polling thread: hand the newest reading to the UI, dropping any that pile up.</summary>
    private void OnSnapshotPublished(HardwareSnapshot snapshot)
    {
        if (!IsActive || Interlocked.Exchange(ref _pending, 1) == 1)
            return;
        _dispatcher.BeginInvoke(() =>
        {
            Interlocked.Exchange(ref _pending, 0);
            if (IsActive) Apply(snapshot);
        }, DispatcherPriority.Background);
    }

    public void Apply(HardwareSnapshot snapshot)
    {
        foreach (var row in Items)
        {
            var (sensor, _, label, accent) = Resolve(row.Item, snapshot);
            row.Label = label;
            row.ValueText = sensor is { HasValue: true, Value: { } v } ? Compact(sensor.Type, v) : "—";
            row.AccentBrush = BrushFor(accent);
        }
    }

    /// <summary>The sensor behind an item, with a short label and its color.</summary>
    internal (SensorReading? Sensor, HardwareReading? Owner, string Label, Color Accent) Resolve(string item, HardwareSnapshot snapshot)
    {
        switch (item)
        {
            case MiniItems.Cpu:
            {
                var cpu = snapshot.OfType(HardwareType.Cpu).FirstOrDefault();
                var sensor = cpu?.First(SensorType.Temperature, CpuTemperatureNames) ?? cpu?.First(SensorType.Load, "CPU Total");
                return (sensor, cpu, "CPU", Palette.Cpu);
            }
            case MiniItems.Gpu:
            {
                var gpu = snapshot.OfType(HardwareType.GpuNvidia, HardwareType.GpuAmd).FirstOrDefault()
                          ?? snapshot.OfType(HardwareType.GpuIntel).FirstOrDefault();
                var sensor = gpu?.First(SensorType.Temperature, "GPU Core")
                             ?? gpu?.AllSensors.FirstOrDefault(s => s.Type == SensorType.Temperature && s.HasValue);
                return (sensor, gpu, "GPU", Palette.Gpu);
            }
            case MiniItems.Pump:
            {
                var pump = FindPump(snapshot);
                return (pump?.Sensor, pump?.Owner, "Pump", Palette.Network);
            }
            case MiniItems.Power:
            {
                var psu = snapshot.OfType(HardwareType.Psu).FirstOrDefault();
                var sensor = psu?.First(SensorType.Power, "Total watts", "Total Output");
                if (sensor is null)
                {
                    var cpu = snapshot.OfType(HardwareType.Cpu).FirstOrDefault();
                    return (cpu?.First(SensorType.Power, "CPU Package", "Package"), cpu, "CPU power", Palette.Power);
                }
                return (sensor, psu, "Power", Palette.Power);
            }
            default:
            {
                foreach (var (owner, sensor) in All(snapshot))
                {
                    if (sensor.Id == item)
                        return (sensor, owner, _catalog.DisplayName(sensor), Palette.ForSensor(sensor.Type, owner.Type));
                }
                var name = _settings.Current.Sensors.Names.GetValueOrDefault(item) ?? "Not detected";
                return (null, null, name, Palette.Memory);
            }
        }
    }

    /// <summary>The header picked as the pump, else a cooler's fan or a header named like a pump.</summary>
    private (SensorReading Sensor, HardwareReading Owner)? FindPump(HardwareSnapshot snapshot)
    {
        var picked = _catalog.RoleSensor(SensorRoles.Pump);
        foreach (var (owner, sensor) in All(snapshot))
        {
            if (sensor.Type == SensorType.Fan && picked is { Length: > 0 } && sensor.Id == picked)
                return (sensor, owner);
        }
        foreach (var (owner, sensor) in All(snapshot))
        {
            if (sensor.Type == SensorType.Fan && (owner.Type == HardwareType.Cooler
                                                  || sensor.Name.Contains("pump", StringComparison.OrdinalIgnoreCase)
                                                  || _catalog.DisplayName(sensor).Contains("pump", StringComparison.OrdinalIgnoreCase)))
                return (sensor, owner);
        }
        return null;
    }

    /// <summary>Short values for a small window: 71°, 2,450 rpm, 182 W, 23%.</summary>
    public static string Compact(SensorType type, float value) => type switch
    {
        SensorType.Temperature => SensorFormat.Degrees(value),
        SensorType.Fan => $"{value:N0} rpm",
        SensorType.Power => $"{value:0} W",
        SensorType.Load or SensorType.Control or SensorType.Level => $"{value:0}%",
        _ => SensorFormat.Format(type, value),
    };

    private static Brush BrushFor(Color color)
    {
        if (Brushes.TryGetValue(color, out var brush)) return brush;
        var created = new SolidColorBrush(color);
        created.Freeze();
        return Brushes[color] = created;
    }

    private static IEnumerable<(HardwareReading Owner, SensorReading Sensor)> All(HardwareSnapshot snapshot)
    {
        static IEnumerable<(HardwareReading, SensorReading)> Walk(HardwareReading h) =>
            h.Sensors.Select(s => (h, s)).Concat(h.SubHardware.SelectMany(Walk));
        return snapshot.Hardware.SelectMany(Walk);
    }
}
