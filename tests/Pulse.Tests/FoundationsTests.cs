using System.IO;
using LibreHardwareMonitor.Hardware;
using Pulse.Models;
using Pulse.Services;
using Pulse.ViewModels;
using static Pulse.Tests.Snap;

namespace Pulse.Tests;

public class UnitTests : PulseTest
{
    [Fact]
    public void Fahrenheit_converts_values_and_symbols()
    {
        SensorFormat.TemperatureUnit = TemperatureUnit.Fahrenheit;

        Assert.Equal("113.0 °F", SensorFormat.Format(SensorType.Temperature, 45));
        Assert.Equal("212°", SensorFormat.Degrees(100));
        Assert.Equal("72 °F", SensorFormat.WholeTemperature(22.2));
    }

    [Fact]
    public void Temperature_differences_scale_without_the_32_degree_offset()
    {
        SensorFormat.TemperatureUnit = TemperatureUnit.Fahrenheit;
        Assert.Equal("9.0 °F", SensorFormat.TemperatureDelta(5));

        SensorFormat.TemperatureUnit = TemperatureUnit.Celsius;
        Assert.Equal("5.0 °C", SensorFormat.TemperatureDelta(5));
    }

    [Fact]
    public void Dashboard_shows_Fahrenheit_but_keeps_the_dial_scale()
    {
        SensorFormat.TemperatureUnit = TemperatureUnit.Fahrenheit;
        var dashboard = new DashboardViewModel(true, NewCatalog(out _), new SensorStream());

        dashboard.Apply(Of(Hardware("/intelcpu/0", "CPU", HardwareType.Cpu,
            Sensor("/intelcpu/0/temperature/0", "CPU Package", SensorType.Temperature, 72))));

        Assert.Equal("162°", dashboard.Cpu.GaugeText);
        Assert.Equal(0.72, dashboard.Cpu.Gauge, precision: 3);
    }
}

public class SettingsServiceTests : PulseTest
{
    [Fact]
    public void Version_1_pump_setting_moves_into_roles()
    {
        File.WriteAllText(Path.Combine(TempFolder, "settings.json"), """{ "PumpSensorId": "/lpc/nct6798d/0/fan/1" }""");

        var settings = NewSettings();
        settings.Flush();

        Assert.Equal("/lpc/nct6798d/0/fan/1", settings.Current.Sensors.Roles[SensorRoles.Pump]);
        Assert.Null(settings.Current.LegacyPumpSensorId);
        var saved = File.ReadAllText(settings.FilePath);
        Assert.Contains("\"Roles\"", saved);
        Assert.DoesNotContain("PumpSensorId", saved);
    }

    [Fact]
    public void Updates_notify_and_save_without_leaving_temporary_files()
    {
        var settings = NewSettings();
        var notified = 0;
        settings.Changed += () => notified++;

        settings.Update(s => s.Units.Temperature = TemperatureUnit.Fahrenheit);
        settings.Flush();

        Assert.Equal(1, notified);
        Assert.Contains("Fahrenheit", File.ReadAllText(settings.FilePath));
        Assert.False(File.Exists(settings.FilePath + ".tmp"));

        var reloaded = NewSettings();
        Assert.Equal(TemperatureUnit.Fahrenheit, reloaded.Current.Units.Temperature);
    }

    [Fact]
    public void A_corrupt_file_falls_back_to_defaults()
    {
        File.WriteAllText(Path.Combine(TempFolder, "settings.json"), "{ this is not json");

        var settings = NewSettings();

        Assert.Equal(TemperatureUnit.Celsius, settings.Current.Units.Temperature);
        Assert.Empty(settings.Current.Sensors.Names);
    }
}

public class SensorCatalogTests : PulseTest
{
    private static readonly HardwareSnapshot Board = Of(
        Hardware("/motherboard", "ASUS ROG MAXIMUS Z790 HERO", HardwareType.Motherboard).WithSub(
            Hardware("/lpc/nct6798d/0", "Nuvoton NCT6798D", HardwareType.SuperIO,
                Sensor("/lpc/nct6798d/0/fan/0", "Fan #1", SensorType.Fan, 1200),
                Sensor("/lpc/nct6798d/0/fan/1", "Fan #2", SensorType.Fan, 2538))));

    [Fact]
    public void Names_fall_back_to_the_original_until_renamed()
    {
        var catalog = NewCatalog(out _);
        catalog.Update(Board);
        var changes = 0;
        catalog.NamesChanged += () => changes++;

        Assert.Equal("Fan #1", catalog.DisplayName("/lpc/nct6798d/0/fan/0", "Fan #1"));

        catalog.Rename("/lpc/nct6798d/0/fan/0", "  Front intake  ");
        Assert.Equal("Front intake", catalog.DisplayName("/lpc/nct6798d/0/fan/0", "Fan #1"));
        Assert.True(catalog.IsRenamed("/lpc/nct6798d/0/fan/0"));

        var renamed = Assert.Single(catalog.RenamedSensors());
        Assert.Equal("Fan #1", renamed.OriginalName);
        Assert.Equal("Nuvoton NCT6798D", renamed.HardwareName);

        catalog.ResetName("/lpc/nct6798d/0/fan/0");
        Assert.Equal("Fan #1", catalog.DisplayName("/lpc/nct6798d/0/fan/0", "Fan #1"));
        Assert.Equal(2, changes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Fan #1")]
    public void Renaming_to_blank_or_the_original_name_removes_the_custom_name(string name)
    {
        var catalog = NewCatalog(out var settings);
        catalog.Update(Board);
        catalog.Rename("/lpc/nct6798d/0/fan/0", "Front intake");

        catalog.Rename("/lpc/nct6798d/0/fan/0", name);

        Assert.Empty(settings.Current.Sensors.Names);
    }

    [Fact]
    public void Renamed_fans_and_pump_show_their_names_on_the_dashboard()
    {
        var catalog = NewCatalog(out _);
        catalog.Update(Board);
        catalog.SetRole(SensorRoles.Pump, "/lpc/nct6798d/0/fan/1");
        catalog.Rename("/lpc/nct6798d/0/fan/0", "Front intake");
        catalog.Rename("/lpc/nct6798d/0/fan/1", "D5 pump");

        var dashboard = new DashboardViewModel(true, catalog, new SensorStream());
        dashboard.Apply(Board);

        Assert.Equal("Front intake", Assert.Single(dashboard.Fans).Name);
        Assert.Contains(dashboard.Cooling.Rows, r => r.Label == "D5 pump");
    }

    [Fact]
    public void All_sensors_shows_new_names_and_search_matches_both_names()
    {
        var catalog = NewCatalog(out _);
        var sensors = new SensorsViewModel(catalog);
        sensors.Apply(Board);
        catalog.Update(Board);

        catalog.Rename("/lpc/nct6798d/0/fan/1", "D5 pump");

        sensors.Filter = "d5";
        sensors.FlushFilter();
        var row = Assert.Single(sensors.Items.OfType<SensorRowViewModel>());
        Assert.Equal("D5 pump", row.Name);
        Assert.True(row.IsRenamed);

        sensors.Filter = "Fan #2";
        sensors.FlushFilter();
        Assert.Single(sensors.Items.OfType<SensorRowViewModel>());
    }

    [Fact]
    public void Inline_rename_saves_on_commit_and_does_nothing_on_cancel()
    {
        var catalog = NewCatalog(out var settings);
        var sensors = new SensorsViewModel(catalog);
        sensors.Apply(Board);
        catalog.Update(Board);
        sensors.Filter = "Fan #1";
        sensors.FlushFilter();
        var row = Assert.Single(sensors.Items.OfType<SensorRowViewModel>());

        row.BeginRenameCommand.Execute(null);
        row.EditText = "Rear exhaust";
        row.CancelRenameCommand.Execute(null);
        Assert.Empty(settings.Current.Sensors.Names);

        row.BeginRenameCommand.Execute(null);
        row.EditText = "Rear exhaust";
        row.CommitRenameCommand.Execute(null);
        Assert.Equal("Rear exhaust", settings.Current.Sensors.Names["/lpc/nct6798d/0/fan/0"]);
        Assert.Equal("Rear exhaust", row.Name);
    }
}
