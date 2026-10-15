using System.IO;
using System.Text.Json;

namespace Pulse.Services;

/// <summary>User choices that LHM can't detect, saved to %LOCALAPPDATA%\Pulse\settings.json.</summary>
public sealed class Settings
{
    /// <summary>LHM identifier of the fan header the CPU pump is plugged into, e.g. "/lpc/nct6798d/0/fan/1".</summary>
    public string? PumpSensorId { get; set; }
}

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pulse");

    private static string FilePath => Path.Combine(Folder, "settings.json");

    public Settings Current { get; private set; } = new();

    public void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                Current = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Write($"Couldn't read settings, using defaults: {ex.Message}");
            Current = new Settings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write($"Couldn't save settings: {ex.Message}");
        }
    }
}
