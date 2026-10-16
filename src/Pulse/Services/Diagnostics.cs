using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Pulse.Models;
using Pulse.Services.Coolers;

namespace Pulse.Services;

/// <summary>Facts about this copy of Pulse and the PC, for the diagnostics report.</summary>
public sealed record DiagnosticsFacts(
    string Version,
    string Windows,
    bool IsElevated,
    bool DriverInstalled,
    string Rendering,
    double DisplayScale,
    string Theme,
    string Culture,
    TimeSpan UpdateInterval);

/// <summary>What to take out of a report before anyone sees it.</summary>
/// <param name="Token">The web view's access token: anyone with it can watch this PC.</param>
public sealed record Redaction(string? Token, string? MachineName, string? UserName, string? UserProfile);

/// <summary>
/// Settings → About → Copy diagnostics: one text report that lets someone else debug Pulse on this PC (which
/// sensors were found, what the log says, how it's set up), without what's private: no web token, no user or
/// computer name, no profile path, and no readings or history (they say nothing a bug report needs).
/// The user sees the whole report before copying it.
/// </summary>
public static class Diagnostics
{
    public const int LogLines = 500;
    public const string Removed = "(removed)";

    public static string Report(DiagnosticsFacts facts, HardwareSnapshot? snapshot, IReadOnlyList<CoolerStatus> coolers,
        IReadOnlyList<string> log, string settingsJson, Redaction redaction)
    {
        var text = new StringBuilder();
        text.AppendLine("Pulse diagnostics");
        text.AppendLine("=================");
        Line(text, "Pulse", facts.Version);
        Line(text, "Windows", facts.Windows);
        Line(text, "Administrator", facts.IsElevated ? "yes" : "no");
        Line(text, "PawnIO driver", facts.DriverInstalled ? "installed" : "not installed");
        Line(text, "Rendering", facts.Rendering);
        Line(text, "Display scale", $"{facts.DisplayScale * 100:0}%");
        Line(text, "Theme", facts.Theme);
        Line(text, "Culture", facts.Culture);
        Line(text, "Update every", $"{facts.UpdateInterval.TotalSeconds:0.#} s");

        Section(text, "Hardware (sensor library)");
        if (snapshot is null)
            text.AppendLine("No readings yet.");
        else
        {
            foreach (var hardware in snapshot.Hardware)
                Hardware(text, hardware, 0);
            var drives = snapshot.Hardware.Count(h => h.Type == LibreHardwareMonitor.Hardware.HardwareType.Storage);
            text.AppendLine($"{snapshot.Hardware.Count} devices, {drives} drives, " +
                            $"{snapshot.Hardware.Sum(h => h.AllSensors.Count())} sensors");
        }

        Section(text, "USB coolers");
        if (coolers.Count == 0)
            text.AppendLine("No supported USB cooler plugged in.");
        foreach (var cooler in coolers)
            text.AppendLine($"{cooler.Name}: {cooler.State}, {cooler.Detail}");

        Section(text, "Settings (web token removed)");
        text.AppendLine(SettingsWithoutSecrets(settingsJson));

        Section(text, $"Log (last {log.Count} lines)");
        foreach (var line in log)
            text.AppendLine(line);

        return Redact(text.ToString(), redaction);
    }

    private static void Line(StringBuilder text, string label, string value) => text.AppendLine($"{label,-14} {value}");

    private static void Section(StringBuilder text, string title)
    {
        text.AppendLine();
        text.AppendLine(title);
        text.AppendLine(new string('-', title.Length));
    }

    /// <summary>One line per device: type, name, identifier, and how many of its sensors answered.</summary>
    private static void Hardware(StringBuilder text, HardwareReading hardware, int depth)
    {
        var withValues = hardware.Sensors.Count(s => s.HasValue);
        text.AppendLine($"{new string(' ', depth * 2)}{hardware.Type,-18} {hardware.Name}  {hardware.Id}  " +
                        $"({hardware.Sensors.Count} sensors, {withValues} with readings)");
        foreach (var sub in hardware.SubHardware)
            Hardware(text, sub, depth + 1);
    }

    /// <summary>The settings file with the web token taken out. Sensor names and rules stay: they explain what the user sees.</summary>
    public static string SettingsWithoutSecrets(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject root)
                return "(couldn't read the settings)";
            if (root["Web"] is JsonObject web && web.ContainsKey("Token"))
                web["Token"] = Removed;
            return root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        }
        catch (System.Text.Json.JsonException)
        {
            return "(couldn't read the settings)";
        }
    }

    /// <summary>
    /// Takes private details out of the whole report, wherever they turn up (the log has paths, for one): the web
    /// token, the profile folder (as %USERPROFILE%), then the user and computer names as whole words.
    /// </summary>
    public static string Redact(string text, Redaction r)
    {
        if (!string.IsNullOrEmpty(r.Token))
            text = text.Replace(r.Token, Removed, StringComparison.Ordinal);
        if (!string.IsNullOrEmpty(r.UserProfile))
            text = text.Replace(r.UserProfile.TrimEnd('\\'), "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        // Very short names would blank out ordinary words; they're rare, and the profile path is already gone.
        if (r.UserName is { Length: >= 3 } user)
            text = Regex.Replace(text, $@"\b{Regex.Escape(user)}\b", "<user>", RegexOptions.IgnoreCase);
        if (r.MachineName is { Length: >= 3 } machine)
            text = Regex.Replace(text, $@"\b{Regex.Escape(machine)}\b", "<computer>", RegexOptions.IgnoreCase);
        return text;
    }

    /// <summary>The last lines of a file another part of Pulse may be writing to right now.</summary>
    public static IReadOnlyList<string> Tail(string path, int count)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var lines = new Queue<string>(count + 1);
            while (reader.ReadLine() is { } line)
            {
                lines.Enqueue(line);
                if (lines.Count > count)
                    lines.Dequeue();
            }
            return lines.ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [$"(couldn't read the log: {ex.Message})"];
        }
    }

    /// <summary>"Windows 11 24H2 (build 26200.1234), x64", from the registry where Windows keeps its own name for itself.</summary>
    public static string WindowsVersion()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var build = key?.GetValue("CurrentBuild") as string ?? Environment.OSVersion.Version.Build.ToString();
            var ubr = key?.GetValue("UBR") is int u ? $".{u}" : "";
            var display = key?.GetValue("DisplayVersion") as string;
            // ProductName still says "Windows 10" on Windows 11; the build number tells them apart.
            var name = int.TryParse(build, out var b) && b >= 22000 ? "Windows 11" : "Windows 10";
            return $"{name}{(display is null ? "" : " " + display)} (build {build}{ubr}), " +
                   $"{System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()}";
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return Environment.OSVersion.VersionString;
        }
    }
}
