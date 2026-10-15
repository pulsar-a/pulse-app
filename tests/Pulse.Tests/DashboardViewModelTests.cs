using LibreHardwareMonitor.Hardware;
using Pulse.Models;
using Pulse.Services;
using Pulse.ViewModels;
using static Pulse.Tests.Snap;

namespace Pulse.Tests;

public class DashboardViewModelTests : PulseTest
{
    private static HardwareReading Cpu(float? package = 72, float? load = 38) => Hardware(
        "/intelcpu/0", "Intel Core i9-14900K", HardwareType.Cpu,
        Sensor("/intelcpu/0/temperature/0", "CPU Package", SensorType.Temperature, package),
        Sensor("/intelcpu/0/load/0", "CPU Total", SensorType.Load, load),
        Sensor("/intelcpu/0/power/0", "CPU Package", SensorType.Power, 102));

    /// <summary>The Z790 Hero's sensor chip: seven generic fan headers, only the given ones spinning.</summary>
    private static HardwareReading Board(params float[] fanRpm)
    {
        var fans = Enumerable.Range(0, 7)
            .Select(i => Sensor($"/lpc/nct6798d/0/fan/{i}", $"Fan #{i + 1}", SensorType.Fan, i < fanRpm.Length ? fanRpm[i] : 0))
            .ToArray();
        return Hardware("/motherboard", "ASUS ROG MAXIMUS Z790 HERO", HardwareType.Motherboard)
            .WithSub(Hardware("/lpc/nct6798d/0", "Nuvoton NCT6798D", HardwareType.SuperIO, fans));
    }

    private DashboardViewModel NewDashboard(out SettingsService settings, bool driverInstalled = true) =>
        new(driverInstalled, NewCatalog(out settings), new SensorStream());

    private static string? Pump(SettingsService settings) =>
        settings.Current.Sensors.Roles.GetValueOrDefault(SensorRoles.Pump);

    [Fact]
    public void Cpu_card_shows_package_temperature_when_available()
    {
        var dashboard = NewDashboard(out _);
        dashboard.Apply(Of(Cpu(package: 72)));

        Assert.Equal("72°", dashboard.Cpu.GaugeText);
        Assert.Equal("package", dashboard.Cpu.GaugeCaption);
        Assert.Equal(0.72, dashboard.Cpu.Gauge, precision: 3);
    }

    [Fact]
    public void Cpu_card_falls_back_to_load_without_temperatures()
    {
        var dashboard = NewDashboard(out _, driverInstalled: false);
        dashboard.Apply(Of(Cpu(package: null, load: 38)));

        Assert.Equal("38%", dashboard.Cpu.GaugeText);
        Assert.Equal("load", dashboard.Cpu.GaugeCaption);
    }

    [Fact]
    public void Gauge_tracks_session_min_and_max()
    {
        var dashboard = NewDashboard(out _);
        dashboard.Apply(Of(Cpu(package: 60)));
        dashboard.Apply(Of(Cpu(package: 80)));
        dashboard.Apply(Of(Cpu(package: 70)));

        Assert.Equal("60°", dashboard.Cpu.MinText);
        Assert.Equal("80°", dashboard.Cpu.MaxText);
    }

    [Fact]
    public void Memory_card_ignores_DIMM_limit_sensors()
    {
        HardwareReading Dimm(int slot, float temp) => Hardware(
            $"/memory/dimm/{slot}", $"Kingston - KF560C36-32 (#{slot})", HardwareType.Memory,
            Sensor($"/memory/dimm/{slot}/temperature/0", $"DIMM #{slot}", SensorType.Temperature, temp),
            Sensor($"/memory/dimm/{slot}/temperature/3", "Thermal Sensor High Limit", SensorType.Temperature, 55),
            Sensor($"/memory/dimm/{slot}/temperature/5", "Thermal Sensor Critical High Limit", SensorType.Temperature, 85));

        var ram = Hardware("/ram", "Total Memory", HardwareType.Memory,
            Sensor("/ram/load/0", "Memory", SensorType.Load, 50),
            Sensor("/ram/data/0", "Memory Used", SensorType.Data, 32),
            Sensor("/ram/data/1", "Memory Available", SensorType.Data, 31.8f));

        var dashboard = NewDashboard(out _);
        dashboard.Apply(Of(ram, Dimm(1, 46.25f), Dimm(3, 45)));

        var hottest = dashboard.Memory.Rows.Single(r => r.Label == "Hottest DIMM");
        Assert.Equal("46.3 °C", hottest.Value);
        Assert.Equal("2 × Kingston KF560C36-32", dashboard.Memory.Device);
    }

    [Fact]
    public void A_single_spinning_header_is_picked_as_the_pump()
    {
        var dashboard = NewDashboard(out var settings);
        dashboard.Apply(Of(Board(0, 2538)));

        Assert.Equal("/lpc/nct6798d/0/fan/1", Pump(settings));
        Assert.True(dashboard.Cooling.HasData);
        Assert.Equal("2,538", dashboard.Cooling.Headline);
        Assert.Contains(dashboard.Cooling.Rows, r => r.Label == "Pump");
        Assert.DoesNotContain(dashboard.Fans, f => f.Name == "Fan #2");
    }

    [Fact]
    public void Pump_is_not_auto_picked_after_the_user_cleared_it()
    {
        var dashboard = NewDashboard(out var settings);
        settings.Current.Sensors.Roles[SensorRoles.Pump] = "";
        dashboard.Apply(Of(Board(0, 2538)));

        Assert.Equal("", Pump(settings));
        Assert.False(dashboard.Cooling.HasData);
        Assert.True(dashboard.Cooling.ShowPumpPicker);
        Assert.Single(dashboard.Cooling.Candidates);
    }

    [Fact]
    public void Several_spinning_headers_ask_the_user_to_pick()
    {
        var dashboard = NewDashboard(out var settings);
        dashboard.Apply(Of(Board(1200, 2538)));

        Assert.Null(Pump(settings));
        Assert.True(dashboard.Cooling.ShowPumpPicker);
        Assert.Equal(2, dashboard.Cooling.Candidates.Count);
    }

    [Fact]
    public void Drives_show_their_volume_letters()
    {
        var nvme = Hardware("/nvme/2", "CT4000T705SSD3", HardwareType.Storage,
            Sensor("/nvme/2/temperature/0", "Composite Temperature", SensorType.Temperature, 57),
            Sensor("/nvme/2/load/0", "Used Space", SensorType.Load, 57),
            Sensor("/nvme/2/level/0", "Percentage Used", SensorType.Level, 1));
        var sata = Hardware("/ssd/0", "CT500MX500SSD1", HardwareType.Storage,
            Sensor("/ssd/0/temperature/0", "Temperature", SensorType.Temperature, 44),
            Sensor("/ssd/0/level/0", "Life", SensorType.Level, 93));

        var snapshot = Of(nvme, sata).WithVolumes(2, new VolumeInfo('C', "4TB Main"), new VolumeInfo('Z', "Grub"));
        var dashboard = NewDashboard(out _);
        dashboard.Apply(snapshot);

        var c = dashboard.Drives.Single(d => d.Name == "CT4000T705SSD3");
        Assert.Equal(['C', 'Z'], c.Volumes.Select(v => v.Letter));
        Assert.Equal("57% used · 99% health", c.Details);

        var noLetter = dashboard.Drives.Single(d => d.Name == "CT500MX500SSD1");
        Assert.False(noLetter.HasVolumes);
        Assert.Equal("93% health", noLetter.Details);
    }

    [Fact]
    public void Real_snapshot_without_admin_fills_the_dashboard()
    {
        var dashboard = NewDashboard(out _);
        dashboard.Apply(Fixture("non-admin.json"));

        Assert.False(dashboard.IsLoading);
        Assert.Contains("i9-14900K", dashboard.Cpu.Device);
        Assert.Equal("NVIDIA GeForce RTX 4090", dashboard.Gpu.Device);
        Assert.Equal("Corsair HX1000i", dashboard.Power.Device);
        Assert.Equal(24, dashboard.Cores.Count);
    }

    [Fact]
    public void Real_snapshot_with_admin_fills_the_dashboard()
    {
        // Captured with `make snapshot`; skipped until that fixture exists.
        if (!FixtureExists("admin.json"))
            return;

        var dashboard = NewDashboard(out _);
        dashboard.Apply(Fixture("admin.json"));

        Assert.Equal("package", dashboard.Cpu.GaugeCaption);
        Assert.Equal("Core temperatures", dashboard.CoresTitle);
        Assert.Equal(5, dashboard.Drives.Count);
    }
}
