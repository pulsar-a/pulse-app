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
        Assert.True(dashboard.Cooling.ShowHeaderPicker);
        Assert.Equal("Not on a motherboard header", dashboard.Cooling.SelectedHeader!.Name);
    }

    [Fact]
    public void Several_spinning_headers_ask_the_user_to_pick()
    {
        var dashboard = NewDashboard(out var settings);
        dashboard.Apply(Of(Board(1200, 2538)));

        Assert.Null(Pump(settings));
        Assert.True(dashboard.Cooling.ShowHeaderPicker);
        Assert.Null(dashboard.Cooling.SelectedHeader);
        Assert.Equal(["Fan #1", "Fan #2", "Not on a motherboard header"], dashboard.Cooling.Headers.Select(h => h.Name));
    }

    [Fact]
    public void Every_spinning_header_stays_listed_after_the_pump_is_picked()
    {
        var dashboard = NewDashboard(out var settings);
        dashboard.Apply(Of(Board(1200, 2538)));

        dashboard.Cooling.SelectedHeader = dashboard.Cooling.Headers.Single(h => h.Name == "Fan #2");
        dashboard.Apply(Of(Board(1200, 2538)));

        Assert.Equal("/lpc/nct6798d/0/fan/1", Pump(settings));
        Assert.True(dashboard.Cooling.HasData);
        Assert.True(dashboard.Cooling.ShowHeaderPicker);
        Assert.Equal(["Fan #1", "Fan #2", "Not on a motherboard header"], dashboard.Cooling.Headers.Select(h => h.Name));
        Assert.Equal("Fan #2", dashboard.Cooling.SelectedHeader!.Name);
        Assert.Equal("2,538 rpm", dashboard.Cooling.SelectedHeader.SpeedText);
    }

    [Fact]
    public void Switching_the_pump_to_another_header_or_to_none()
    {
        var dashboard = NewDashboard(out var settings);
        dashboard.Apply(Of(Board(1200, 2538)));
        dashboard.Cooling.SelectedHeader = dashboard.Cooling.Headers.Single(h => h.Name == "Fan #2");
        dashboard.Apply(Of(Board(1200, 2538)));

        dashboard.Cooling.SelectedHeader = dashboard.Cooling.Headers.Single(h => h.Name == "Fan #1");
        dashboard.Apply(Of(Board(1200, 2538)));
        Assert.Equal("/lpc/nct6798d/0/fan/0", Pump(settings));
        Assert.Contains(dashboard.Fans, f => f.Name == "Fan #2");

        dashboard.Cooling.SelectedHeader = dashboard.Cooling.Headers.Single(h => h.Id == "");
        dashboard.Apply(Of(Board(1200, 2538)));
        Assert.Equal("", Pump(settings));
        Assert.False(dashboard.Cooling.HasData);
        Assert.Equal(2, dashboard.Fans.Count);
    }

    [Fact]
    public void Ticks_do_not_count_as_choosing_a_header()
    {
        var dashboard = NewDashboard(out var settings);
        for (var i = 0; i < 3; i++)
            dashboard.Apply(Of(Board(1200, 2538 + i)));

        Assert.Null(Pump(settings));
        Assert.Null(dashboard.Cooling.SelectedHeader);
    }

    [Fact]
    public void A_header_renamed_Pump_stays_pickable_while_another_header_is_the_pump()
    {
        var dashboard = NewDashboard(out var settings);
        settings.Current.Sensors.Names["/lpc/nct6798d/0/fan/1"] = "Fan";
        settings.Current.Sensors.Names["/lpc/nct6798d/0/fan/5"] = "Pump";
        settings.Current.Sensors.Roles[SensorRoles.Pump] = "/lpc/nct6798d/0/fan/1";
        var board = Of(Board(0, 500, 0, 0, 0, 2100));
        dashboard.Apply(board);

        // The pick is the pump; the header called "Pump" is a fan with a name, still in the list.
        Assert.Equal(["Fan", "Pump", "Not on a motherboard header"], dashboard.Cooling.Headers.Select(h => h.Name));
        Assert.Equal(["Fan"], dashboard.Cooling.Rows.Select(r => r.Label));
        Assert.Contains(dashboard.Fans, f => f.Name == "Pump");

        dashboard.Cooling.SelectedHeader = dashboard.Cooling.Headers.Single(h => h.Name == "Pump");
        dashboard.Apply(board);

        Assert.Equal("/lpc/nct6798d/0/fan/5", Pump(settings));
        Assert.Equal(["Fan", "Pump", "Not on a motherboard header"], dashboard.Cooling.Headers.Select(h => h.Name));
        Assert.Equal(["Pump"], dashboard.Cooling.Rows.Select(r => r.Label));
        Assert.Equal("2,100", dashboard.Cooling.Headline);
        Assert.Contains(dashboard.Fans, f => f.Name == "Fan");
        Assert.DoesNotContain(dashboard.Fans, f => f.Name == "Pump");
    }

    [Fact]
    public void A_header_named_as_the_pump_becomes_the_pump_until_another_is_picked()
    {
        var dashboard = NewDashboard(out var settings);
        settings.Current.Sensors.Names["/lpc/nct6798d/0/fan/5"] = "Pump";
        dashboard.Apply(Of(Board(1200, 500, 0, 0, 0, 2100)));

        Assert.Equal("/lpc/nct6798d/0/fan/5", Pump(settings));
        Assert.Equal("2,100", dashboard.Cooling.Headline);
        Assert.Equal("Pump", dashboard.Cooling.SelectedHeader!.Name);
    }

    [Fact]
    public void The_cooling_card_asks_when_the_cooling_changed()
    {
        var dashboard = NewDashboard(out var settings);
        var catalog = new SensorCatalog(settings);
        catalog.SetRole(SensorRoles.Pump, "/lpc/nct6798d/0/fan/1");
        var start = new DateTime(2026, 10, 21, 12, 0, 0);
        var rules = settings.Current.Alerts.Rules;
        var before = new PumpWatch(catalog);
        for (var s = 0; s < 60; s++)
            before.Observe(PumpWatchTests.OldCooler(s), rules, start.AddSeconds(s));
        var watch = new PumpWatch(catalog);
        for (var s = 0; s < 20; s++)
            watch.Observe(PumpWatchTests.NewCooler(s), rules, start.AddHours(3).AddSeconds(s));

        dashboard.PumpWatch = watch;
        dashboard.Apply(Of(PumpWatchTests.NewCooler(20).Hardware.ToArray()));

        Assert.True(dashboard.Cooling.HasChange);
        Assert.Equal("Fan #2 has read about 500 rpm since Pulse started. Before, it ran at about 2,560 rpm. "
                     + "Fan #6 started spinning at 2,100 rpm.", dashboard.Cooling.ChangeText);
        Assert.Equal("Use Fan #6", dashboard.Cooling.UseLabel);
        Assert.Equal("Fan #2 is the pump", dashboard.Cooling.KeepLabel);
        Assert.True(dashboard.Cooling.ShowHeaderPicker);
        Assert.Equal(HeatLevel.Warm, dashboard.Cooling.Heat);

        dashboard.Cooling.UseSuggestionCommand.Execute(null);
        dashboard.Apply(PumpWatchTests.NewCooler(21));

        Assert.Equal("/lpc/nct6798d/0/fan/5", Pump(settings));
        Assert.False(dashboard.Cooling.HasChange);
        Assert.Equal("2,100", dashboard.Cooling.Headline);
        Assert.Contains(dashboard.Fans, f => f.Name == "Fan #2");
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
    public void Cpu_load_card_shows_the_total_and_the_busiest_core()
    {
        var dashboard = NewDashboard(out _);
        dashboard.Apply(Of(Hardware("/intelcpu/0", "Intel Core i9-14900K", HardwareType.Cpu,
            Sensor("/intelcpu/0/load/0", "CPU Total", SensorType.Load, 12.4f),
            Sensor("/intelcpu/0/load/1", "CPU Core Max", SensorType.Load, 97),
            Sensor("/intelcpu/0/load/2", "CPU Core #1 Thread #1", SensorType.Load, 97),
            Sensor("/intelcpu/0/load/3", "CPU Core #1 Thread #2", SensorType.Load, 3),
            Sensor("/intelcpu/0/load/4", "CPU Core #2", SensorType.Load, 1))));

        Assert.Equal("12%", dashboard.CpuLoad.TotalText);
        Assert.Equal("97%", dashboard.CpuLoad.BusiestText);
        Assert.Equal("2 cores · 3 threads", dashboard.CpuLoad.Details);
        Assert.Equal("/intelcpu/0/load/0", dashboard.CpuLoad.SensorId);
    }

    /// <summary>The Z790 Hero's sensor chip and embedded controller, as the admin snapshot has them.</summary>
    private static HardwareReading BoardSensors() =>
        Hardware("/motherboard", "ASUS ROG MAXIMUS Z790 HERO", HardwareType.Motherboard).WithSub(
            Hardware("/lpc/nct6798d/0", "Nuvoton NCT6798D", HardwareType.SuperIO,
                Sensor("/lpc/nct6798d/0/temperature/0", "CPU Package", SensorType.Temperature, 47, max: 60),
                Sensor("/lpc/nct6798d/0/temperature/1", "CPU", SensorType.Temperature, 47.5f, max: 60),
                Sensor("/lpc/nct6798d/0/temperature/2", "Motherboard", SensorType.Temperature, 32, max: 33),
                Sensor("/lpc/nct6798d/0/temperature/8", "Temperature #8", SensorType.Temperature, 84, max: 84),
                Sensor("/lpc/nct6798d/0/temperature/13", "Chipset", SensorType.Temperature, 64, max: 65),
                Sensor("/lpc/nct6798d/0/temperature/16", "Temperature #16", SensorType.Temperature, 0, max: 0),
                Sensor("/lpc/nct6798d/0/voltage/0", "Vcore", SensorType.Voltage, 1.1f, max: 1.3f),
                Sensor("/lpc/nct6798d/0/voltage/1", "Voltage #2", SensorType.Voltage, 0.9f, max: 0.9f)),
            Hardware("/lpc/ec", "Embedded Controller", HardwareType.EmbeddedController,
                Sensor("/lpc/ec/temperature/0", "VRM", SensorType.Temperature, 44, max: 50),
                Sensor("/lpc/ec/temperature/1", "Water In", SensorType.Temperature, 30, max: 31)));

    [Fact]
    public void Motherboard_card_leads_with_the_chipset_and_lists_the_board_sensors()
    {
        var dashboard = NewDashboard(out _);
        dashboard.Apply(Of(BoardSensors()));

        var board = dashboard.Motherboard;
        Assert.True(board.HasData);
        Assert.Equal("ASUS ROG MAXIMUS Z790 HERO", board.Device);
        Assert.Equal("64°", board.Headline);
        Assert.Equal("Chipset", board.HeadlineCaption);
        Assert.Equal("/lpc/nct6798d/0/temperature/13", board.SensorId);
        // Temperatures, then voltages. Not the package (Processor card), coolant (cooling card), unnamed or never-read inputs.
        Assert.Equal(["CPU socket", "Motherboard", "Chipset", "VRM", "Vcore"], board.Rows.Select(r => r.Name));
        Assert.Equal("1.100 V", board.Rows.Single(r => r.Name == "Vcore").ValueText);
        Assert.Null(board.Rows.Single(r => r.Name == "Vcore").ValueBrush);
        Assert.Equal(HeatLevel.None, board.Heat);
    }

    [Fact]
    public void A_renamed_board_input_shows_on_the_motherboard_card()
    {
        var dashboard = NewDashboard(out var settings);
        settings.Update(s => s.Sensors.Names["/lpc/nct6798d/0/temperature/8"] = "M.2 heatsink");
        dashboard.Apply(Of(BoardSensors()));

        Assert.Contains("M.2 heatsink", dashboard.Motherboard.Rows.Select(r => r.Name));
        // 84 °C: warm, not yet hot.
        Assert.Equal(HeatLevel.Warm, dashboard.Motherboard.Heat);
    }

    [Fact]
    public void Motherboard_card_explains_missing_sensors()
    {
        var dashboard = NewDashboard(out _, driverInstalled: false);
        dashboard.Apply(Of(Board(1200)));

        Assert.False(dashboard.Motherboard.HasData);
        Assert.NotEmpty(dashboard.Motherboard.EmptyText);
        Assert.Null(dashboard.Motherboard.SensorId);
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
        Assert.Equal("Chipset", dashboard.Motherboard.HeadlineCaption);
    }
}
