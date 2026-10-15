using System.IO;
using LibreHardwareMonitor.Hardware;
using Pulse.Models;
using Pulse.Services;
using Pulse.ViewModels;
using static Pulse.Tests.Snap;

namespace Pulse.Tests;

public class DashboardLayoutTests : PulseTest
{
    private const string PumpId = "/lpc/nct6798d/0/fan/1";
    private const string WaterInId = "/lpc/nct6798d/0/temperature/6";

    private static HardwareSnapshot Machine(bool withBoard = true) => Of(
        [
            Hardware("/intelcpu/0", "Intel Core i9-14900K", HardwareType.Cpu,
                Sensor("/intelcpu/0/temperature/0", "CPU Package", SensorType.Temperature, 72)),
            .. withBoard
                ? new[]
                {
                    Hardware("/lpc/nct6798d/0", "Nuvoton NCT6798D", HardwareType.SuperIO,
                        Sensor(PumpId, "Fan #2", SensorType.Fan, 2450),
                        Sensor(WaterInId, "Temperature #7", SensorType.Temperature, 31.5f)),
                }
                : [],
        ]);

    private static List<string> Kinds(IEnumerable<TileSettings> tiles) => tiles.Select(t => t.Kind).ToList();

    // ---- The layout rules ---------------------------------------------------------------------

    [Fact]
    public void The_default_layout_is_the_familiar_dashboard()
    {
        var tiles = DashboardLayout.Default();

        Assert.Equal(["Processor", "Graphics", "Memory", "Power", "Cores", "Cooling", "Fans", "Drives", "Network"], Kinds(tiles));
        // Four gauges in a row, then cores and cooling, then fans, drives and network: three full rows.
        Assert.Equal([12, 12, 12], Rows(tiles.Select(t => DashboardLayout.Span(t.SizeValue))));
    }

    private static List<int> Rows(IEnumerable<int> spans)
    {
        var rows = new List<int> { 0 };
        foreach (var span in spans)
        {
            if (rows[^1] + span > DashboardLayout.Columns) rows.Add(0);
            rows[^1] += span;
        }
        return rows;
    }

    [Fact]
    public void Loading_repairs_a_damaged_or_newer_layout()
    {
        var tiles = new List<TileSettings>
        {
            new() { Id = "graphics", Kind = "Graphics", Size = "Gigantic" },     // unknown size
            new() { Id = "weather", Kind = "Weather", Size = "Small" },         // a kind from a newer Pulse
            new() { Id = "graphics", Kind = "Graphics", Size = "Small" },       // duplicate
            new() { Id = "pinned-1", Kind = "Pinned", Size = "Small", Sensors = [] }, // empty pin
            new() { Id = "cores", Kind = "Cores", Size = "Small" },             // too small for the core strip
        };

        Assert.True(DashboardLayout.Normalize(tiles));

        Assert.Equal("graphics", tiles[0].Id);
        Assert.Equal("Small", tiles[0].Size);
        Assert.Equal("Wide", tiles.Single(t => t.Id == "cores").Size);
        Assert.DoesNotContain(tiles, t => t.Kind is "Weather" or "Pinned");
        Assert.Equal(9, tiles.Count);   // every built-in card is there, the missing ones at the end
        Assert.False(DashboardLayout.Normalize(tiles));
    }

    [Fact]
    public void An_unknown_tile_kind_in_the_file_does_not_lose_other_settings()
    {
        File.WriteAllText(Path.Combine(TempFolder, "settings.json"), """
            {
              "Sensors": { "Names": { "/intelcpu/0/temperature/0": "CPU" } },
              "Dashboard": { "Tiles": [ { "Id": "weather", "Kind": "Weather", "Size": "Huge" } ] }
            }
            """);

        var settings = NewSettings();

        Assert.Equal("CPU", settings.Current.Sensors.Names["/intelcpu/0/temperature/0"]);
        Assert.Equal(9, settings.Current.Dashboard.Tiles!.Count);
    }

    [Fact]
    public void Moving_puts_a_tile_before_another_or_at_the_end()
    {
        var tiles = DashboardLayout.Default();

        Assert.True(DashboardLayout.Move(tiles, "network", "processor"));
        Assert.Equal("network", tiles[0].Id);
        Assert.True(DashboardLayout.Move(tiles, "network", null));
        Assert.Equal("network", tiles[^1].Id);
        Assert.False(DashboardLayout.Move(tiles, "network", null));
    }

    [Fact]
    public void Only_sizes_that_suit_a_card_can_be_chosen()
    {
        Assert.Equal([TileSize.Small, TileSize.Medium, TileSize.Large], DashboardLayout.AllowedSizes(TileKind.Processor));
        Assert.Equal([TileSize.Large, TileSize.Wide, TileSize.Full], DashboardLayout.AllowedSizes(TileKind.Cores));

        var tiles = DashboardLayout.Default();
        Assert.False(DashboardLayout.Resize(tiles, "cores", TileSize.Small));
        Assert.True(DashboardLayout.Resize(tiles, "processor", TileSize.Large));
    }

    [Fact]
    public void Pinning_twice_does_not_duplicate_and_pinning_into_a_tile_makes_a_group()
    {
        var tiles = DashboardLayout.Default();

        var tile = DashboardLayout.Pin(tiles, PumpId);
        Assert.Equal(tile, DashboardLayout.Pin(tiles, PumpId));
        Assert.Single(tiles, t => t.Kind == "Pinned");

        DashboardLayout.Pin(tiles, WaterInId, tile);
        var group = tiles.Single(t => t.Id == tile);
        Assert.Equal([PumpId, WaterInId], group.Sensors);
        Assert.Equal("Medium", group.Size);   // grown to fit two rows
    }

    [Fact]
    public void Unpinning_the_last_sensor_removes_its_tile_and_built_in_cards_cannot_be_removed()
    {
        var tiles = DashboardLayout.Default();
        DashboardLayout.Pin(tiles, PumpId);

        Assert.True(DashboardLayout.Unpin(tiles, PumpId));
        Assert.DoesNotContain(tiles, t => t.Kind == "Pinned");
        Assert.False(DashboardLayout.Remove(tiles, "processor"));
    }

    // ---- The dashboard ------------------------------------------------------------------------

    [Fact]
    public void A_new_dashboard_shows_the_default_cards_and_saves_nothing()
    {
        var dashboard = new DashboardViewModel(true, NewCatalog(out var settings), new SensorStream());

        Assert.Equal(9, dashboard.Tiles.Count);
        Assert.Same(dashboard.Cpu, dashboard.Tiles[0].Card);
        Assert.Null(settings.Current.Dashboard.Tiles);
    }

    [Fact]
    public void Pump_and_water_in_pinned_next_to_the_cpu_card_survive_a_restart()
    {
        var stream = new SensorStream();
        stream.Publish(Machine());
        var catalog = NewCatalog(out var settings);
        catalog.Update(Machine());
        var dashboard = new DashboardViewModel(true, catalog, stream);

        dashboard.Pin(PumpId);
        var groupId = dashboard.PinTargets().Single().Id;
        dashboard.Pin(WaterInId, groupId);
        dashboard.MoveTile(groupId, "graphics");
        dashboard.RenameTile(groupId, "Loop");
        settings.Flush();

        // Restart: fresh settings from the same file, fresh dashboard.
        var reloaded = new SettingsService(TempFolder);
        reloaded.Load();
        var after = new DashboardViewModel(true, new SensorCatalog(reloaded), stream);

        Assert.Equal(["processor", groupId, "graphics"], after.Tiles.Take(3).Select(t => t.Id));
        var group = Assert.IsType<PinnedTileViewModel>(after.Tiles[1].Card);
        Assert.Equal("Loop", group.Title);
        Assert.Equal([PumpId, WaterInId], group.Sensors.Select(s => s.SensorId));
        reloaded.Dispose();
    }

    [Fact]
    public void Pinned_sensors_show_live_values()
    {
        var stream = new SensorStream();
        stream.Publish(Machine());
        var dashboard = new DashboardViewModel(true, NewCatalog(out _), stream);
        dashboard.Pin(PumpId);

        dashboard.Apply(Machine());

        var pinned = Assert.IsType<PinnedTileViewModel>(dashboard.Tiles[^1].Card);
        Assert.Equal("2,450 RPM", pinned.Primary!.ValueText);
        Assert.Equal("Fan #2", pinned.Title);
        Assert.Equal("Nuvoton NCT6798D", pinned.Primary.Hardware);
    }

    [Fact]
    public void A_pinned_sensor_that_disappeared_stays_explained_and_can_be_removed()
    {
        var stream = new SensorStream();
        var dashboard = new DashboardViewModel(true, NewCatalog(out var settings), stream);
        dashboard.Pin(PumpId);

        // Started without admin rights: the motherboard chip isn't readable.
        stream.Publish(Machine(withBoard: false));
        dashboard.Apply(Machine(withBoard: false));

        var tile = dashboard.Tiles[^1];
        var pinned = Assert.IsType<PinnedTileViewModel>(tile.Card);
        Assert.True(pinned.Primary!.IsMissing);
        Assert.Equal("Not detected right now", pinned.Primary.Hardware);

        dashboard.RemoveTileCommand.Execute(tile);
        Assert.DoesNotContain(dashboard.Tiles, t => t.IsPinned);
        Assert.DoesNotContain(settings.Current.Dashboard.Tiles!, t => t.Kind == "Pinned");
    }

    [Fact]
    public void Hidden_cards_wait_in_edit_mode_and_come_back_in_their_place()
    {
        var dashboard = new DashboardViewModel(true, NewCatalog(out _), new SensorStream());
        var memory = dashboard.Tiles[2];

        dashboard.HideTileCommand.Execute(memory);
        Assert.DoesNotContain(memory, dashboard.Tiles);
        Assert.Equal([memory], dashboard.HiddenTiles);
        Assert.True(dashboard.HasHiddenTiles);

        dashboard.ShowTileCommand.Execute(memory);
        Assert.Same(memory, dashboard.Tiles[2]);
        Assert.False(dashboard.HasHiddenTiles);
    }

    [Fact]
    public void Reset_restores_the_cards_and_keeps_pins()
    {
        var dashboard = new DashboardViewModel(true, NewCatalog(out var settings), new SensorStream());
        dashboard.Pin(PumpId);
        dashboard.MoveTile("network", "processor");
        dashboard.HideTileCommand.Execute(dashboard.Tiles.First(t => t.Id == "memory"));

        DashboardViewModel.ResetLayout(settings);

        Assert.Equal(["processor", "graphics", "memory", "power", "cores", "cooling", "fans", "drives", "network"],
            dashboard.Tiles.Take(9).Select(t => t.Id));
        Assert.True(dashboard.Tiles[^1].IsPinned);
    }

    [Fact]
    public void The_size_menu_offers_the_cards_sizes_with_the_current_one_checked()
    {
        var dashboard = new DashboardViewModel(true, NewCatalog(out _), new SensorStream());
        var cpu = dashboard.Tiles[0];

        var options = cpu.SizeOptions;

        Assert.Equal(["Small", "Medium", "Large"], options.Select(o => o.Label));
        Assert.Equal([true, false, false], options.Select(o => o.IsCurrent));
        Assert.Equal(18, options[0].PreviewWidth);   // a quarter of the 72 px preview track
    }

    [Fact]
    public void Choosing_a_size_applies_it_once_even_if_clicked_twice()
    {
        var dashboard = new DashboardViewModel(true, NewCatalog(out var settings), new SensorStream());
        var cpu = dashboard.Tiles[0];
        var large = cpu.SizeOptions.Single(o => o.Size == TileSize.Large);

        large.Choose.Execute(null);
        large.Choose.Execute(null);   // a quick second click on the same choice

        Assert.Equal(TileSize.Large, cpu.Size);
        Assert.Equal(6, cpu.Span);
        Assert.Equal("L", cpu.SizeLabel);
        Assert.Equal("Large", settings.Current.Dashboard.Tiles!.Single(t => t.Id == "processor").Size);
        Assert.True(cpu.SizeOptions.Single(o => o.Size == TileSize.Large).IsCurrent);
    }

    [Fact]
    public void Search_in_pin_mode_pins_instead_of_opening_details()
    {
        var catalog = NewCatalog(out _);
        catalog.Update(Machine());
        var opened = new List<string>();
        var pinned = new List<string>();
        var search = new SensorSearchViewModel(catalog, new SensorStream(), opened.Add);

        search.Open(pinned.Add, "Pin a sensor to the dashboard");
        search.Query = "fan #2";
        search.ChooseCommand.Execute(null);
        search.Open();
        search.Query = "package";
        search.ChooseCommand.Execute(null);

        Assert.Equal([PumpId], pinned);
        Assert.Equal(["/intelcpu/0/temperature/0"], opened);
    }
}
