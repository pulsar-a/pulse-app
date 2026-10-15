using LibreHardwareMonitor.Hardware;
using Pulse.Models;

namespace Pulse.Services;

/// <summary>What the catalog knows about one sensor.</summary>
public sealed record SensorInfo(string Id, string OriginalName, string DisplayName, SensorType Type, string HardwareName,
    HardwareType? HardwareType = null)
{
    public bool IsRenamed => DisplayName != OriginalName;
}

/// <summary>
/// The single source of sensor names and roles. Everything that shows a sensor name asks here,
/// so a rename ("Fan #2" → "Pump") shows up everywhere at once.
/// Lives on the UI thread; <see cref="Update"/> is called with each snapshot.
/// </summary>
public sealed class SensorCatalog
{
    private readonly SettingsService _settings;
    private Dictionary<string, (SensorReading Sensor, string HardwareName, HardwareType HardwareType)> _index = new();

    public SensorCatalog(SettingsService settings) => _settings = settings;

    /// <summary>The settings names and roles live in; the dashboard keeps its layout there too.</summary>
    public SettingsService Settings => _settings;

    /// <summary>Raised after a rename, a reset, or a role change.</summary>
    public event Action? NamesChanged;

    private Dictionary<string, string> Names => _settings.Current.Sensors.Names;
    private Dictionary<string, string> Roles => _settings.Current.Sensors.Roles;

    /// <summary>Indexes the sensors in a snapshot so names and owners can be looked up by id.</summary>
    public void Update(HardwareSnapshot snapshot)
    {
        var index = new Dictionary<string, (SensorReading, string, HardwareType)>();
        foreach (var hardware in snapshot.Hardware)
            Add(index, hardware);
        _index = index;
    }

    private static void Add(Dictionary<string, (SensorReading, string, HardwareType)> index, HardwareReading hardware)
    {
        foreach (var sensor in hardware.Sensors)
            index[sensor.Id] = (sensor, hardware.Name, hardware.Type);
        foreach (var sub in hardware.SubHardware)
            Add(index, sub);
    }

    /// <summary>The custom name if there is one, otherwise <paramref name="originalName"/>.</summary>
    public string DisplayName(string sensorId, string originalName) =>
        Names.TryGetValue(sensorId, out var name) && !string.IsNullOrWhiteSpace(name) ? name : originalName;

    public string DisplayName(SensorReading sensor) => DisplayName(sensor.Id, sensor.Name);

    public bool IsRenamed(string sensorId) => Names.ContainsKey(sensorId);

    public SensorInfo? Find(string sensorId) =>
        _index.TryGetValue(sensorId, out var entry)
            ? new SensorInfo(sensorId, entry.Sensor.Name, DisplayName(entry.Sensor), entry.Sensor.Type, entry.HardwareName, entry.HardwareType)
            : null;

    /// <summary>Every sensor in the latest snapshot, in hardware order.</summary>
    public IReadOnlyList<SensorInfo> AllSensors() =>
        _index.Select(kv => new SensorInfo(kv.Key, kv.Value.Sensor.Name, DisplayName(kv.Value.Sensor), kv.Value.Sensor.Type,
            kv.Value.HardwareName, kv.Value.HardwareType)).ToList();

    /// <summary>Renamed sensors, including ones not present right now (shown with their id).</summary>
    public IReadOnlyList<SensorInfo> RenamedSensors() =>
        Names.Select(kv => Find(kv.Key) ?? new SensorInfo(kv.Key, kv.Key, kv.Value, SensorType.Temperature, "Not detected"))
            .OrderBy(s => s.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    public void Rename(string sensorId, string name)
    {
        name = name.Trim();
        var original = _index.TryGetValue(sensorId, out var entry) ? entry.Sensor.Name : null;
        _settings.Update(s =>
        {
            if (name.Length == 0 || name == original)
                s.Sensors.Names.Remove(sensorId);
            else
                s.Sensors.Names[sensorId] = name;
        });
        Log.Write($"Sensor {sensorId} renamed to \"{name}\"");
        NamesChanged?.Invoke();
    }

    public void ResetName(string sensorId)
    {
        _settings.Update(s => s.Sensors.Names.Remove(sensorId));
        NamesChanged?.Invoke();
    }

    /// <summary>
    /// The sensor holding <paramref name="role"/>: null if never chosen, "" if the user cleared it.
    /// </summary>
    public string? RoleSensor(string role) => Roles.TryGetValue(role, out var id) ? id : null;

    public void SetRole(string role, string sensorId)
    {
        _settings.Update(s => s.Sensors.Roles[role] = sensorId);
        Log.Write($"Role {role} set to {sensorId}");
        NamesChanged?.Invoke();
    }

    /// <summary>Marks the role as deliberately unassigned, so Pulse won't pick one automatically.</summary>
    public void ClearRole(string role)
    {
        _settings.Update(s => s.Sensors.Roles[role] = "");
        Log.Write($"Role {role} cleared");
        NamesChanged?.Invoke();
    }

    /// <summary>Forgets the role entirely, so Pulse may pick one automatically again.</summary>
    public void ForgetRole(string role)
    {
        _settings.Update(s => s.Sensors.Roles.Remove(role));
        NamesChanged?.Invoke();
    }
}
