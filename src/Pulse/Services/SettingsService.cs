using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pulse.Services;

public enum TemperatureUnit
{
    Celsius,
    Fahrenheit,
}

/// <summary>User choices that LHM can't detect, saved to %LOCALAPPDATA%\Pulse\settings.json.</summary>
public sealed class Settings
{
    public const int CurrentSchemaVersion = 3;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public GeneralSettings General { get; set; } = new();
    public StartupSettings Startup { get; set; } = new();
    public UnitSettings Units { get; set; } = new();
    public SensorSettings Sensors { get; set; } = new();
    public AlertSettings Alerts { get; set; } = new();
    public HistorySettings History { get; set; } = new();
    public DashboardSettings Dashboard { get; set; } = new();
    public MiniSettings Mini { get; set; } = new();
    public AppearanceSettings Appearance { get; set; } = new();
    public Pulse.Services.Processes.ProcessesSettings Processes { get; set; } = new();
    public Pulse.Services.Coolers.CoolerSettings Coolers { get; set; } = new();
    public Pulse.Services.Web.WebSettings Web { get; set; } = new();

    /// <summary>Version 1 stored the pump header here; <see cref="SettingsService.Load"/> moves it into roles.</summary>
    [JsonPropertyName("PumpSensorId")]
    public string? LegacyPumpSensorId { get; set; }
}

public sealed class GeneralSettings
{
    public static readonly double[] UpdateIntervalChoices = [0.5, 1, 2, 5];

    /// <summary>Seconds between sensor readings; one of <see cref="UpdateIntervalChoices"/>.</summary>
    public double UpdateIntervalSeconds { get; set; } = 1;

    [JsonIgnore]
    public TimeSpan UpdateInterval =>
        TimeSpan.FromSeconds(UpdateIntervalChoices.Contains(UpdateIntervalSeconds) ? UpdateIntervalSeconds : 1);
}

/// <summary>
/// Tray and startup behavior. "Start with Windows" isn't stored here: the scheduled task itself
/// is the source of truth (see <see cref="StartupTask"/>).
/// </summary>
public sealed class StartupSettings
{
    public bool ShowTrayIcon { get; set; } = true;

    /// <summary>
    /// Keep the icon next to the clock rather than in the ^ overflow. Windows treats every new build or
    /// copy of Pulse as a new app and hides it, so Pulse switches it back on at each start (see TrayPromotion).
    /// </summary>
    public bool KeepTrayIconVisible { get; set; } = true;

    /// <summary>Closing the window keeps Pulse running in the tray (when the tray icon is shown).</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>Start hidden in the tray, even when launched normally.</summary>
    public bool StartMinimized { get; set; }

    /// <summary>The sensor whose value the tray icon shows; null means automatic (CPU temperature, or load).</summary>
    public string? TraySensorId { get; set; }

    /// <summary>Whether the "Pulse is still running" notice has been shown after the first close to tray.</summary>
    public bool CloseNoticeShown { get; set; }
}

public sealed class HistorySettings
{
    public static readonly int[] RetentionChoices = [0, 1, 7, 30];

    /// <summary>Days of history kept on disk; 0 turns recording off.</summary>
    public int RetentionDays { get; set; } = 7;
}

public sealed class UnitSettings
{
    public TemperatureUnit Temperature { get; set; } = TemperatureUnit.Celsius;
}

public sealed class SensorSettings
{
    /// <summary>Custom display names: LHM sensor identifier → name.</summary>
    public Dictionary<string, string> Names { get; set; } = new();

    /// <summary>
    /// Roles the user (or Pulse) assigned to sensors, for example <see cref="SensorRoles.Pump"/>.
    /// A missing role means "never chosen"; an empty id means "the user cleared it".
    /// </summary>
    public Dictionary<string, string> Roles { get; set; } = new();
}

public static class SensorRoles
{
    public const string Pump = "Pump";
}

/// <summary>
/// Loads and saves <see cref="Settings"/>. Changes go through <see cref="Update"/>, which raises
/// <see cref="Changed"/> right away and writes the file shortly after (debounced, atomically).
/// </summary>
public sealed class SettingsService : IDisposable
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(500);

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string DefaultFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pulse");

    private readonly string _folder;
    private readonly object _gate = new();
    private readonly Timer _saveTimer;
    private bool _dirty;

    /// <param name="folder">Where settings.json lives; tests pass a temporary folder.</param>
    public SettingsService(string? folder = null)
    {
        _folder = folder ?? DefaultFolder;
        _saveTimer = new Timer(_ => Flush());
    }

    public string FilePath => Path.Combine(_folder, "settings.json");

    public Settings Current { get; private set; } = new();

    /// <summary>Raised on the calling thread after every <see cref="Update"/>.</summary>
    public event Action? Changed;

    public void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                Current = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Json) ?? new Settings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Write($"Couldn't read settings, using defaults: {ex.Message}");
            Current = new Settings();
        }

        if (Migrate(Current))
            ScheduleSave();
    }

    /// <summary>Applies a change, notifies listeners, and saves soon after.</summary>
    public void Update(Action<Settings> change)
    {
        lock (_gate)
            change(Current);
        Changed?.Invoke();
        ScheduleSave();
    }

    /// <summary>Writes pending changes now. Called on exit so nothing is lost.</summary>
    public void Flush()
    {
        string json;
        lock (_gate)
        {
            if (!_dirty)
                return;
            _dirty = false;
            json = JsonSerializer.Serialize(Current, Json);
        }

        try
        {
            Directory.CreateDirectory(_folder);
            // Write a temporary file and swap it in, so a crash mid-write never leaves a broken file.
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write($"Couldn't save settings: {ex.Message}");
        }
    }

    private void ScheduleSave()
    {
        lock (_gate)
            _dirty = true;
        _saveTimer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Upgrades older settings in place; returns true if anything changed.</summary>
    internal static bool Migrate(Settings settings)
    {
        var changed = false;
        settings.General ??= new GeneralSettings();
        settings.Startup ??= new StartupSettings();
        settings.Units ??= new UnitSettings();
        settings.Sensors ??= new SensorSettings();
        settings.Sensors.Names ??= new Dictionary<string, string>();
        settings.Sensors.Roles ??= new Dictionary<string, string>();
        settings.Alerts ??= new AlertSettings();
        settings.History ??= new HistorySettings();
        settings.Alerts.Rules ??= [];
        settings.Dashboard ??= new DashboardSettings();
        settings.Mini ??= new MiniSettings();
        settings.Appearance ??= new AppearanceSettings();
        settings.Appearance.Theme ??= AppearanceSettings.MatchWindows;
        settings.Mini.Positions ??= new Dictionary<string, MiniPosition>();
        settings.Mini.Hotkey ??= "";
        if (settings.Dashboard.Tiles is { } tiles && DashboardLayout.Normalize(tiles))
            changed = true;
        if (AlertDefaults.AddMissing(settings.Alerts.Rules))
            changed = true;

        if (settings.LegacyPumpSensorId is { } pump)
        {
            settings.Sensors.Roles.TryAdd(SensorRoles.Pump, pump);
            settings.LegacyPumpSensorId = null;
            changed = true;
        }

        // Version 3: "hide the mini window while Pulse is in front" became opt-in. Builds before it saved it
        // switched on, which made the header button seem to do nothing.
        if (settings.SchemaVersion < 3)
            settings.Mini.HideWhenPulseActive = false;

        if (settings.SchemaVersion < Settings.CurrentSchemaVersion)
        {
            settings.SchemaVersion = Settings.CurrentSchemaVersion;
            changed = true;
        }

        return changed;
    }

    public void Dispose()
    {
        _saveTimer.Dispose();
        Flush();
    }
}
