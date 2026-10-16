using System.Text.Json.Serialization;
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
    [JsonIgnore]
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
    [JsonIgnore]
    public IEnumerable<SensorReading> AllSensors =>
        Sensors.Concat(SubHardware.SelectMany(h => h.AllSensors));

    public SensorReading? Find(SensorType type, string name) =>
        AllSensors.FirstOrDefault(s => s.Type == type && s.Name == name);

    /// <summary>The first of the named sensors that has a reading.</summary>
    public SensorReading? First(SensorType type, params string[] names)
    {
        foreach (var name in names)
        {
            if (Find(type, name) is { HasValue: true } s)
                return s;
        }
        return null;
    }

    public float? Value(SensorType type, params string[] names) => First(type, names)?.Value;
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

    /// <summary>
    /// Hardware Pulse deliberately didn't read this time (a USB cooler left alone while iCUE runs). Its sensors
    /// are missing on purpose: that says nothing about the device, so a pump in here hasn't stopped.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyCollection<string> Unread { get; init; } = [];

    public bool IsUnread(string sensorId)
    {
        foreach (var id in Unread)
        {
            if (sensorId.StartsWith(id, StringComparison.Ordinal) && sensorId.Length > id.Length && sensorId[id.Length] == '/')
                return true;
        }
        return false;
    }
}
