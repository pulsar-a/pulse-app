using System.IO;
using Pulse.Services;
using Pulse.Services.Coolers;

namespace Pulse.Tests;

public class DiagnosticsTests : PulseTest
{
    private const string Token = "Zk3pQ9vX2mT7bR1cY8wL0a";

    private static readonly DiagnosticsFacts Facts = new("0.2.0", "Windows 11 24H2 (build 26200.1), x64", IsElevated: true,
        DriverInstalled: false, "graphics card (tier 2)", 1.5, "Dark", "de-DE", TimeSpan.FromSeconds(1));

    private static readonly Redaction Private = new(Token, "GAMING-RIG", "garip", @"C:\Users\garip");

    private static string SettingsJson => $$"""
        {
          "SchemaVersion": 3,
          "Sensors": { "Names": { "/lpc/nct6798d/0/fan/1": "Pump" } },
          "Web": { "Enabled": true, "Port": 8085, "Token": "{{Token}}" }
        }
        """;

    private static string Report(IReadOnlyList<string>? log = null, IReadOnlyList<CoolerStatus>? coolers = null) =>
        Diagnostics.Report(Facts, Snap.Fixture("non-admin.json"), coolers ?? [], log ?? ["12:00:00.000  Pulse started"], SettingsJson, Private);

    [Fact]
    public void The_report_says_how_pulse_runs_and_what_it_found()
    {
        var report = Report(coolers: [new CoolerStatus("k", "Corsair Commander ST", CoolerState.Paused, "Left alone while iCUE is running")]);

        Assert.Contains("Pulse          0.2.0", report);
        Assert.Contains("Administrator  yes", report);
        Assert.Contains("PawnIO driver  not installed", report);
        Assert.Contains("Display scale  150%", report);
        Assert.Contains("Culture        de-DE", report);
        // Every device with its identifier and how many sensors answered, sub-hardware indented.
        Assert.Contains("Cpu                Intel Core i9-14900K  /intelcpu/0", report);
        Assert.Matches(@"Motherboard .*\n  SuperIO|Motherboard .*\r\n  ", report);
        Assert.Matches(@"\(\d+ sensors, \d+ with readings\)", report);
        Assert.Contains("Corsair Commander ST: Paused, Left alone while iCUE is running", report);
        Assert.Contains("\"Pump\"", report);   // renames explain what the user sees
        Assert.Contains("Pulse started", report);
    }

    [Fact]
    public void Nothing_private_is_left_in_it()
    {
        var log = new[]
        {
            @"12:00:01.000  Settings saved to C:\Users\garip\AppData\Local\Pulse\settings.json",
            $"12:00:02.000  Web view link http://192.168.1.20:8085/?t={Token}",
            "12:00:03.000  Running on GAMING-RIG as Garip",
        };

        var report = Report(log);

        Assert.DoesNotContain(Token, report);
        Assert.DoesNotContain("garip", report, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("GAMING-RIG", report, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"%USERPROFILE%\AppData\Local\Pulse\settings.json", report);
        Assert.Contains("?t=(removed)", report);
        Assert.Contains("Running on <computer> as <user>", report);
        Assert.Contains("\"Token\": \"(removed)\"", report);
        // No readings: values aren't in the report, only which sensors exist.
        Assert.DoesNotContain("Value", report);
    }

    [Fact]
    public void Short_user_names_are_not_blanked_out_of_ordinary_words()
    {
        var text = Diagnostics.Redact(@"Fan #1 at C:\Users\al\x, all fine", new Redaction(null, null, "al", @"C:\Users\al"));

        Assert.Equal(@"Fan #1 at %USERPROFILE%\x, all fine", text);
    }

    [Fact]
    public void Settings_lose_only_the_token()
    {
        var clean = Diagnostics.SettingsWithoutSecrets(SettingsJson);

        Assert.Contains("\"Token\": \"(removed)\"", clean);
        Assert.Contains("\"Port\": 8085", clean);
        Assert.Contains("\"Pump\"", clean);
        Assert.Equal("(couldn't read the settings)", Diagnostics.SettingsWithoutSecrets("{ not json"));
        Assert.DoesNotContain("Token", Diagnostics.SettingsWithoutSecrets("""{ "Web": { "Enabled": false } }"""));
    }

    [Fact]
    public void The_log_tail_reads_a_file_still_being_written()
    {
        var path = Path.Combine(TempFolder, "busy.log");
        using var writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite));
        for (var i = 1; i <= 800; i++)
            writer.WriteLine($"line {i}");
        writer.Flush();

        var tail = Diagnostics.Tail(path, 500);

        Assert.Equal(500, tail.Count);
        Assert.Equal("line 301", tail[0]);
        Assert.Equal("line 800", tail[^1]);
        Assert.StartsWith("(couldn't read the log", Diagnostics.Tail(Path.Combine(TempFolder, "missing.log"), 10).Single());
    }

    [Fact]
    public void Before_the_first_reading_the_report_still_works()
    {
        var report = Diagnostics.Report(Facts, null, [], [], "{}", Private);

        Assert.Contains("No readings yet.", report);
        Assert.Contains("No supported USB cooler plugged in.", report);
    }

    [Fact]
    public void The_windows_version_names_windows_and_its_build()
    {
        Assert.Matches(@"^Windows 1[01].* \(build \d+(\.\d+)?\), (x64|arm64)", Diagnostics.WindowsVersion());
    }
}
