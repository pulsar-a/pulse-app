using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using LibreHardwareMonitor.Hardware;
using Pulse.Controls;
using Pulse.Models;
using Pulse.Services;
using Pulse.ViewModels;
using static Pulse.Tests.Snap;

namespace Pulse.Tests;

public class ResponsiveAndMiniTests : PulseTest
{
    // ---- Responsive dashboard -----------------------------------------------------------------

    /// <summary>The default dashboard as (span, minimum width) pairs.</summary>
    private static readonly (int Span, double MinWidth)[] DefaultTiles =
    [
        (3, 250), (3, 250), (3, 250), (3, 250),   // gauges
        (8, 360), (4, 320),                       // cores, cooling
        (4, 260), (4, 330), (4, 280),             // fans, drives, network
    ];

    private static List<int> TilesPerRow(double width) =>
        TileFlowPanel.Plan(DefaultTiles, width, 12).Select(r => r.Count).ToList();

    [Fact]
    public void A_wide_window_keeps_the_familiar_layout() =>
        Assert.Equal([4, 2, 3], TilesPerRow(1312));

    [Fact]
    public void Narrower_windows_reflow_gauges_three_two_then_one_per_row()
    {
        Assert.Equal(3, TilesPerRow(960)[0]);
        Assert.Equal(2, TilesPerRow(700)[0]);
        Assert.All(TilesPerRow(432), count => Assert.Equal(1, count));
    }

    [Fact]
    public void No_tile_is_ever_narrower_than_it_can_take()
    {
        for (var width = 432; width <= 1900; width += 7)
        {
            foreach (var row in TileFlowPanel.Plan(DefaultTiles, width, 12))
            {
                Assert.Equal(width, row.Sum(t => t.Width), 3);
                foreach (var (index, w) in row)
                    Assert.True(w >= DefaultTiles[index].MinWidth - 0.01 || width < DefaultTiles[index].MinWidth,
                        $"tile {index} is {w:0} px at a {width} px window");
            }
        }
    }

    [Theory]
    [InlineData(24, 900, 24)]   // fits in one row
    [InlineData(24, 500, 12)]   // two even rows
    [InlineData(24, 300, 8)]    // three rows
    [InlineData(6, 100, 3)]
    [InlineData(0, 500, 1)]
    public void The_core_strip_wraps_evenly_when_bars_get_thin(int cores, double width, int perRow) =>
        Assert.Equal(perRow, StripPanel.PerRow(cores, width, 30));

    // ---- Mini window: where it opens ----------------------------------------------------------

    private static readonly MonitorArea Laptop = new(@"\\.\DISPLAY1 0,0,1920x1080", new Rect(0, 0, 1920, 1040), true);
    private static readonly MonitorArea Desk = new(@"\\.\DISPLAY2 1920,0,2560x1440", new Rect(1920, 0, 2560, 1400), false);
    private static readonly Size Mini = new(200, 120);

    [Fact]
    public void First_time_it_opens_top_right_on_the_main_screen() =>
        Assert.Equal(new Point(1920 - 200 - 24, 24), MiniPlacement.Choose([Laptop, Desk], new MiniSettings(), Mini));

    [Fact]
    public void It_comes_back_where_it_was_on_that_monitor()
    {
        var settings = new MiniSettings { Monitor = Desk.Key };
        settings.Positions[Desk.Key] = new MiniPosition { X = 4000, Y = 300 };

        Assert.Equal(new Point(4000, 300), MiniPlacement.Choose([Laptop, Desk], settings, Mini));
    }

    [Fact]
    public void Undocked_from_that_monitor_it_goes_back_to_the_main_screen()
    {
        var settings = new MiniSettings { Monitor = Desk.Key };
        settings.Positions[Desk.Key] = new MiniPosition { X = 4000, Y = 300 };

        Assert.Equal(new Point(1696, 24), MiniPlacement.Choose([Laptop], settings, Mini));
    }

    [Fact]
    public void A_saved_spot_off_the_edge_is_pulled_back_on_screen()
    {
        var settings = new MiniSettings { Monitor = Laptop.Key };
        settings.Positions[Laptop.Key] = new MiniPosition { X = 1900, Y = 1030 };

        Assert.Equal(new Point(1720, 920), MiniPlacement.Choose([Laptop], settings, Mini));
    }

    // ---- Shortcut -----------------------------------------------------------------------------

    [Fact]
    public void Shortcuts_are_read_from_text()
    {
        Assert.Equal((0x2u | 0x1u, Key.P), GlobalHotkey.Parse("Ctrl+Alt+P"));
        Assert.Equal((0x2u | 0x4u | 0x1u, Key.P), GlobalHotkey.Parse("Ctrl + Shift + Alt + P"));
        Assert.Null(GlobalHotkey.Parse(""));
        Assert.Null(GlobalHotkey.Parse("P"));              // a bare key would steal it from every app
        Assert.Null(GlobalHotkey.Parse("Ctrl+Alt+Nope"));
        Assert.Null(GlobalHotkey.Parse("Ctrl+P+Q"));
    }

    [Theory]
    [InlineData(ModifierKeys.Control | ModifierKeys.Alt, Key.P, "Ctrl+Alt+P", "Ctrl + Alt + P")]
    [InlineData(ModifierKeys.Alt | ModifierKeys.Shift | ModifierKeys.Control, Key.D1, "Ctrl+Shift+Alt+D1", "Ctrl + Shift + Alt + 1")]
    [InlineData(ModifierKeys.Windows | ModifierKeys.Alt, Key.F9, "Alt+Win+F9", "Alt + Win + F9")]
    [InlineData(ModifierKeys.Control, Key.NumPad5, "Ctrl+NumPad5", "Ctrl + Num 5")]
    public void Recorded_keys_become_a_shortcut_that_reads_back(ModifierKeys modifiers, Key key, string stored, string shown)
    {
        var (shortcut, problem) = GlobalHotkey.FromKeys(modifiers, key);

        Assert.Null(problem);
        Assert.Equal(stored, shortcut);
        Assert.Equal(shown, GlobalHotkey.Display(shortcut));
        Assert.Equal(key, GlobalHotkey.Parse(shortcut)!.Value.Key);   // it registers as what was pressed
    }

    [Fact]
    public void Recording_waits_while_only_modifiers_are_held_and_refuses_shift_alone()
    {
        Assert.Equal((null, null), GlobalHotkey.FromKeys(ModifierKeys.Control, Key.LeftAlt));
        Assert.NotNull(GlobalHotkey.FromKeys(ModifierKeys.Shift, Key.P).Problem);
        Assert.NotNull(GlobalHotkey.FromKeys(ModifierKeys.None, Key.P).Problem);
        Assert.Equal("Off", GlobalHotkey.Display(""));
    }

    [Fact]
    public void Set_listens_for_the_next_keys_and_esc_or_backspace_end_it()
    {
        var catalog = NewCatalog(out var settings);
        var page = new SettingsViewModel(settings, catalog, true, new SensorStream());

        page.SetShortcutCommand.Execute(null);
        Assert.True(page.IsRecordingShortcut);
        Assert.True(page.RecordShortcutKey(ModifierKeys.Control, Key.LeftCtrl));
        Assert.Equal("Ctrl + …", page.ShortcutText);
        Assert.True(page.RecordShortcutKey(ModifierKeys.Control | ModifierKeys.Shift, Key.F10));
        Assert.False(page.IsRecordingShortcut);
        Assert.Equal("Ctrl+Shift+F10", settings.Current.Mini.Hotkey);
        Assert.False(page.RecordShortcutKey(ModifierKeys.Control, Key.K));   // not listening any more

        page.SetShortcutCommand.Execute(null);
        page.RecordShortcutKey(ModifierKeys.None, Key.Escape);
        Assert.Equal("Ctrl+Shift+F10", settings.Current.Mini.Hotkey);   // cancelled, unchanged

        page.SetShortcutCommand.Execute(null);
        page.RecordShortcutKey(ModifierKeys.None, Key.Back);
        Assert.Equal("", settings.Current.Mini.Hotkey);                 // off

        page.ResetShortcutCommand.Execute(null);
        Assert.Equal(MiniSettings.DefaultHotkey, settings.Current.Mini.Hotkey);
    }

    // ---- Mini window: what it shows -----------------------------------------------------------

    private const string PumpId = "/lpc/nct6798d/0/fan/1";

    private static HardwareSnapshot Machine(bool psu = true) => Of(
        [
            Hardware("/intelcpu/0", "Intel Core i9-14900K", HardwareType.Cpu,
                Sensor("/intelcpu/0/temperature/0", "CPU Package", SensorType.Temperature, 71.4f),
                Sensor("/intelcpu/0/power/0", "CPU Package", SensorType.Power, 64.2f)),
            Hardware("/gpu-nvidia/0", "NVIDIA GeForce RTX 4090", HardwareType.GpuNvidia,
                Sensor("/gpu-nvidia/0/temperature/0", "GPU Core", SensorType.Temperature, 46)),
            Hardware("/lpc/nct6798d/0", "Nuvoton NCT6798D", HardwareType.SuperIO,
                Sensor(PumpId, "Fan #2", SensorType.Fan, 2450),
                Sensor("/lpc/nct6798d/0/fan/0", "Fan #1", SensorType.Fan, 980)),
            .. psu
                ? new[]
                {
                    Hardware("/psu/corsair/0", "Corsair HX1000i", HardwareType.Psu,
                        Sensor("/psu/corsair/0/power/3", "Total watts", SensorType.Power, 182)),
                }
                : [],
        ]);

    private MiniViewModel NewMini(out SettingsService settings)
    {
        var catalog = NewCatalog(out settings);
        catalog.Update(Machine());
        return new MiniViewModel(catalog, new SensorStream(), settings, Dispatcher.CurrentDispatcher);
    }

    [Fact]
    public void By_default_it_shows_cpu_gpu_pump_and_power()
    {
        var mini = NewMini(out var settings);
        settings.Current.Sensors.Roles[SensorRoles.Pump] = PumpId;

        mini.Apply(Machine());

        Assert.Equal(["CPU", "GPU", "Pump", "Power"], mini.Items.Select(i => i.Label));
        Assert.Equal(["71°", "46°", "2,450 rpm", "182 W"], mini.Items.Select(i => i.ValueText));
    }

    [Fact]
    public void Without_a_psu_power_falls_back_to_the_cpu_and_without_a_pump_it_says_so()
    {
        var mini = NewMini(out _);

        mini.Apply(Machine(psu: false));

        Assert.Equal("CPU power", mini.Items[3].Label);
        Assert.Equal("64 W", mini.Items[3].ValueText);
        Assert.Equal("—", mini.Items[2].ValueText);   // nothing picked as the pump, nothing named like one
    }

    [Fact]
    public void Chosen_sensors_show_with_their_names_in_the_users_unit()
    {
        var mini = NewMini(out var settings);
        settings.Current.Sensors.Names["/lpc/nct6798d/0/fan/0"] = "Rear fan";
        SensorFormat.TemperatureUnit = TemperatureUnit.Fahrenheit;
        SettingsViewModel.AddMiniItem(settings, "/lpc/nct6798d/0/fan/0");
        SettingsViewModel.AddMiniItem(settings, "/lpc/nct6798d/0/fan/0");   // added once

        mini.Apply(Machine());

        Assert.Equal(5, mini.Items.Count);
        Assert.Equal("Rear fan", mini.Items[^1].Label);
        Assert.Equal("980 rpm", mini.Items[^1].ValueText);
        Assert.Equal("161°", mini.Items[0].ValueText);
    }

    [Fact]
    public void Layout_and_opacity_follow_settings()
    {
        var mini = NewMini(out var settings);

        settings.Update(s =>
        {
            s.Mini.Horizontal = true;
            s.Mini.Opacity = 0.1;
        });

        Assert.True(mini.IsHorizontal);
        Assert.Equal(0.4, mini.WindowOpacity);   // never fully see-through
    }

    [Fact]
    public void Computed_values_stay_out_of_the_settings_file()
    {
        var settings = NewSettings();
        settings.Update(s =>
        {
            s.Mini.Items = [.. MiniSettings.AutoItems];
            s.Dashboard.Tiles = DashboardLayout.Default();
        });
        settings.Flush();

        var json = File.ReadAllText(settings.FilePath);
        Assert.DoesNotContain("ItemsOrDefault", json);
        Assert.DoesNotContain("KindValue", json);
        Assert.DoesNotContain("SizeValue", json);
    }
}
