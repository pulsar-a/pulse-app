using System.Globalization;
using System.IO;
using LibreHardwareMonitor.Hardware;
using Pulse.Models;
using Pulse.Services;

namespace Pulse.Tests;

/// <summary>Builds small, readable snapshots for tests.</summary>
internal static class Snap
{
    public static SensorReading Sensor(string id, string name, SensorType type, float? value, float? max = null) =>
        new(id, name, type, 0, value, value, max ?? value);

    public static HardwareReading Hardware(string id, string name, HardwareType type, params SensorReading[] sensors) =>
        new(id, name, type, sensors, []);

    public static HardwareReading WithSub(this HardwareReading hardware, params HardwareReading[] sub) =>
        hardware with { SubHardware = sub };

    public static HardwareSnapshot Of(params HardwareReading[] hardware) =>
        new(hardware, new Dictionary<int, IReadOnlyList<VolumeInfo>>(), DateTime.Now);

    public static HardwareSnapshot WithVolumes(this HardwareSnapshot snapshot, int disk, params VolumeInfo[] volumes)
    {
        var all = snapshot.Volumes.ToDictionary(kv => kv.Key, kv => kv.Value);
        all[disk] = volumes;
        return snapshot with { Volumes = all };
    }

    /// <summary>Loads a snapshot captured with <c>Pulse.exe --dump-snapshot</c>.</summary>
    public static HardwareSnapshot Fixture(string name) =>
        SnapshotJson.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    public static bool FixtureExists(string name) =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
}

/// <summary>
/// Per-test sandbox: a temporary settings folder and log file, so tests never touch the real
/// %LOCALAPPDATA%\Pulse, plus invariant number formatting.
/// </summary>
public abstract class PulseTest : IDisposable
{
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;
    private readonly List<SettingsService> _settings = [];

    protected PulseTest()
    {
        TempFolder = Path.Combine(Path.GetTempPath(), "PulseTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(TempFolder);
        Log.FilePath = Path.Combine(TempFolder, "pulse.log");
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        SensorFormat.TemperatureUnit = TemperatureUnit.Celsius;
    }

    protected string TempFolder { get; }

    protected SettingsService NewSettings()
    {
        var settings = new SettingsService(TempFolder);
        _settings.Add(settings);
        settings.Load();
        return settings;
    }

    protected SensorCatalog NewCatalog(out SettingsService settings)
    {
        settings = NewSettings();
        return new SensorCatalog(settings);
    }

    public void Dispose()
    {
        CultureInfo.CurrentCulture = _culture;
        SensorFormat.TemperatureUnit = TemperatureUnit.Celsius;
        // Settings save on a timer; stop them before the folder goes away.
        foreach (var settings in _settings)
            settings.Dispose();
        try { Directory.Delete(TempFolder, recursive: true); }
        catch (IOException) { }
        GC.SuppressFinalize(this);
    }
}
