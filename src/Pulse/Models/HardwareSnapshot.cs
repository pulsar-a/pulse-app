using LibreHardwareMonitor.Hardware;

namespace Pulse.Models;

/// <summary>An immutable copy of one sensor's state, safe to hand to the UI thread.</summary>
public sealed record SensorReading(
    string Id,
    string Name,
    SensorType Type,
    int Index,
    float? Value,
    float? Min,
    float? Max)
{
    public bool HasValue => Value is { } v && !float.IsNaN(v);
}

/// <summary>An immutable copy of one hardware component and its sub-hardware.</summary>
public sealed record HardwareReading(
    string Id,
    string Name,
    HardwareType Type,
    IReadOnlyList<SensorReading> Sensors,
    IReadOnlyList<HardwareReading> SubHardware)
{
    /// <summary>This hardware's sensors followed by those of all sub-hardware.</summary>
    public IEnumerable<SensorReading> AllSensors =>
        Sensors.Concat(SubHardware.SelectMany(h => h.AllSensors));

    public SensorReading? Find(SensorType type, string name) =>
        AllSensors.FirstOrDefault(s => s.Type == type && s.Name == name);

    public float? Value(SensorType type, params string[] names)
    {
        foreach (var name in names)
        {
            if (Find(type, name) is { HasValue: true } s)
                return s.Value;
        }
        return null;
    }
}

/// <summary>A lettered volume (partition) on a physical disk, as Explorer shows it.</summary>
public sealed record VolumeInfo(char Letter, string Label);

/// <summary>Everything read from the hardware in one polling tick.</summary>
/// <param name="Volumes">Lettered volumes keyed by Windows disk number.</param>
public sealed record HardwareSnapshot(
    IReadOnlyList<HardwareReading> Hardware,
    IReadOnlyDictionary<int, IReadOnlyList<VolumeInfo>> Volumes,
    DateTime Timestamp)
{
    public IEnumerable<HardwareReading> OfType(params HardwareType[] types) =>
        Hardware.Where(h => types.Contains(h.Type));
}
