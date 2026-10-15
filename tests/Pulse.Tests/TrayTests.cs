using System.Xml.Linq;
using LibreHardwareMonitor.Hardware;
using Pulse.Models;
using Pulse.Services;
using static Pulse.Tests.Snap;

namespace Pulse.Tests;

public class AppArgumentsTests
{
    [Fact]
    public void No_arguments_means_a_normal_start() =>
        Assert.Equal(new AppArguments(false, null), AppArguments.Parse([]));

    [Fact]
    public void Tray_flag_is_recognized_in_any_case() =>
        Assert.True(AppArguments.Parse(["--TRAY"]).StartInTray);

    [Fact]
    public void Dump_snapshot_takes_the_next_argument_as_its_path() =>
        Assert.Equal(@"C:\temp\a.json", AppArguments.Parse(["--dump-snapshot", @"C:\temp\a.json"]).DumpSnapshotPath);

    [Fact]
    public void Dump_snapshot_without_a_path_is_ignored() =>
        Assert.Null(AppArguments.Parse(["--dump-snapshot"]).DumpSnapshotPath);
}

public class StartupTaskTests
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    private static XDocument Task(string exe = @"C:\Apps\Pulse\Pulse.exe") =>
        XDocument.Parse(StartupTask.BuildTaskXml(exe, "S-1-5-21-1-2-3-1001"));

    [Fact]
    public void Starts_Pulse_in_the_tray_at_sign_in_with_admin_rights()
    {
        var task = Task();

        Assert.Equal("S-1-5-21-1-2-3-1001", task.Descendants(Ns + "LogonTrigger").Single().Element(Ns + "UserId")!.Value);
        Assert.Equal("HighestAvailable", task.Descendants(Ns + "RunLevel").Single().Value);
        Assert.Equal(@"C:\Apps\Pulse\Pulse.exe", task.Descendants(Ns + "Command").Single().Value);
        Assert.Equal("--tray", task.Descendants(Ns + "Arguments").Single().Value);
        Assert.Equal(@"C:\Apps\Pulse", task.Descendants(Ns + "WorkingDirectory").Single().Value);
    }

    [Fact]
    public void Overrides_Task_Scheduler_defaults_that_would_stop_or_slow_Pulse()
    {
        var settings = Task().Descendants(Ns + "Settings").Single();

        Assert.Equal("PT0S", settings.Element(Ns + "ExecutionTimeLimit")!.Value);
        Assert.Equal("false", settings.Element(Ns + "DisallowStartIfOnBatteries")!.Value);
        Assert.Equal("false", settings.Element(Ns + "StopIfGoingOnBatteries")!.Value);
        Assert.Equal("5", settings.Element(Ns + "Priority")!.Value);
    }

    [Fact]
    public void Special_characters_in_the_path_are_escaped()
    {
        var task = Task(@"C:\Tools & Stuff\<Pulse>\Pulse.exe");
        Assert.Equal(@"C:\Tools & Stuff\<Pulse>\Pulse.exe", task.Descendants(Ns + "Command").Single().Value);
    }
}

public class TrayTextTests : PulseTest
{
    [Theory]
    [InlineData(SensorType.Temperature, 72.4, "72")]
    [InlineData(SensorType.Load, 9.6, "10")]
    [InlineData(SensorType.Power, 146.2, "146")]
    [InlineData(SensorType.Power, 1210, "1.2k")]
    [InlineData(SensorType.Fan, 2538, "2.5k")]
    [InlineData(SensorType.Fan, 12000, "12k")]
    [InlineData(SensorType.Clock, 5680, "5.7")]
    public void Values_fit_in_two_or_three_characters(SensorType type, double value, string expected) =>
        Assert.Equal(expected, TrayText.Compact(type, value));

    [Fact]
    public void Temperatures_follow_the_unit_setting()
    {
        SensorFormat.TemperatureUnit = TemperatureUnit.Fahrenheit;
        Assert.Equal("162", TrayText.Compact(SensorType.Temperature, 72));
    }

    private static HardwareSnapshot Machine(float? cpuTemp) => Of(
        Hardware("/intelcpu/0", "CPU", HardwareType.Cpu,
            Sensor("/intelcpu/0/temperature/0", "CPU Package", SensorType.Temperature, cpuTemp),
            Sensor("/intelcpu/0/load/0", "CPU Total", SensorType.Load, 38)),
        Hardware("/gpu-nvidia/0", "GPU", HardwareType.GpuNvidia,
            Sensor("/gpu-nvidia/0/temperature/0", "GPU Core", SensorType.Temperature, 47),
            Sensor("/gpu-nvidia/0/load/0", "GPU Core", SensorType.Load, 26)),
        Hardware("/psu/corsair/0", "Corsair HX1000i", HardwareType.Psu,
            Sensor("/psu/corsair/0/power/0", "Total watts", SensorType.Power, 210)),
        Hardware("/lpc/nct6798d/0", "Nuvoton NCT6798D", HardwareType.SuperIO,
            Sensor("/lpc/nct6798d/0/fan/1", "Fan #2", SensorType.Fan, 2538)));

    [Fact]
    public void Automatic_shows_CPU_temperature_and_falls_back_to_load()
    {
        Assert.Equal("72", TrayText.Resolve(Machine(72), null, s => s.Name)!.Text);

        var withoutDriver = TrayText.Resolve(Machine(null), null, s => s.Name)!;
        Assert.Equal(SensorType.Load, withoutDriver.Type);
        Assert.Equal("38", withoutDriver.Text);
    }

    [Fact]
    public void A_chosen_sensor_wins_and_a_missing_one_falls_back_to_automatic()
    {
        Assert.Equal("47", TrayText.Resolve(Machine(72), "/gpu-nvidia/0/temperature/0", s => s.Name)!.Text);
        Assert.Equal("72", TrayText.Resolve(Machine(72), "/no/such/sensor", s => s.Name)!.Text);
    }

    [Fact]
    public void Tooltip_lists_the_headline_readings_within_the_Windows_limit()
    {
        var tooltip = TrayText.Tooltip(Machine(72), "/lpc/nct6798d/0/fan/1");

        Assert.Equal("Pulse\nCPU 72 °C · 38%\nGPU 47 °C · 26%\nPump 2,538 rpm\nPower 210 W", tooltip);
        Assert.True(tooltip.Length <= 127);
    }

    [Fact]
    public void Choices_offer_automatic_plus_what_this_PC_has()
    {
        var labels = TrayText.Choices(Machine(72), "/lpc/nct6798d/0/fan/1").Select(c => c.Label).ToList();

        Assert.Equal("Automatic (CPU temperature)", labels[0]);
        Assert.Contains("GPU temperature", labels);
        Assert.Contains("System power", labels);
        Assert.Contains("Pump speed", labels);
        Assert.DoesNotContain("GPU hot spot", labels);
    }
}

public class StartupSettingsTests : PulseTest
{
    [Fact]
    public void Defaults_keep_Pulse_in_the_tray_when_closed()
    {
        var startup = NewSettings().Current.Startup;

        Assert.True(startup.ShowTrayIcon);
        Assert.True(startup.CloseToTray);
        Assert.False(startup.StartMinimized);
        Assert.Null(startup.TraySensorId);
    }

    [Fact]
    public void Older_settings_files_get_the_startup_section()
    {
        System.IO.File.WriteAllText(System.IO.Path.Combine(TempFolder, "settings.json"),
            """{ "SchemaVersion": 2, "Startup": null }""");

        Assert.NotNull(NewSettings().Current.Startup);
    }
}
