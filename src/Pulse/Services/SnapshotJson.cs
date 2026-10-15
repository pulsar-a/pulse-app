using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pulse.Models;

namespace Pulse.Services;

/// <summary>Reads and writes <see cref="HardwareSnapshot"/> as JSON, for test fixtures and diagnostics.</summary>
public static class SnapshotJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        // NaN and infinity do show up in LHM readings.
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static void Save(HardwareSnapshot snapshot, string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(snapshot, Options));

    public static HardwareSnapshot Load(string path) =>
        JsonSerializer.Deserialize<HardwareSnapshot>(File.ReadAllText(path), Options)
        ?? throw new InvalidDataException($"{path} doesn't contain a snapshot.");
}
