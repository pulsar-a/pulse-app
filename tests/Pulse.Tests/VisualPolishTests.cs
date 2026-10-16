using System.IO;
using System.Windows.Media;
using System.Xml.Linq;
using LibreHardwareMonitor.Hardware;
using Pulse.Controls;
using Pulse.Models;
using Pulse.Services;
using Pulse.ViewModels;
using Wpf.Ui.Appearance;
using static Pulse.Tests.Snap;

namespace Pulse.Tests;

public class VisualPolishTests : PulseTest
{
    // ---- Splash -------------------------------------------------------------------------------

    [Fact]
    public void The_splash_beat_runs_left_to_right_with_one_spike()
    {
        var beat = Pulse.Views.SplashView.Beat;
        Assert.True(beat.Zip(beat.Skip(1)).All(p => p.Second.X >= p.First.X), "the head only moves forward");
        Assert.Equal(0, beat[0].X);
        Assert.Equal(480, beat[^1].X);
        Assert.Equal(222, Pulse.Views.SplashView.Spike.X);
    }

    [Fact]
    public void Each_beat_lasts_long_enough_for_the_tail_to_fade_before_the_next()
    {
        // The bright end travels past the right edge by the trail length (300 px) at the head's speed.
        var fadeOut = Pulse.Views.SplashView.Sweep.TotalSeconds * (480 + 300) / 480;
        Assert.True(Pulse.Views.SplashView.Cycle.TotalSeconds >= fadeOut,
            $"a beat lasts {Pulse.Views.SplashView.Cycle.TotalSeconds} s but the tail needs {fadeOut:0.00} s");
    }

    [Fact]
    public void Readings_that_come_quickly_still_get_two_beats_and_leave_after_a_spike()
    {
        var exit = Pulse.Views.SplashView.ExitPoint;
        var cycle = Pulse.Views.SplashView.Cycle;
        var spike = Pulse.Views.SplashView.Sweep * (Pulse.Views.SplashView.Spike.X / 480);

        Assert.True(exit > spike, "it leaves after the spike, not before");
        // Ready almost at once: it waits for the second beat's spike.
        Assert.Equal(cycle + exit - TimeSpan.FromMilliseconds(200), Pulse.Views.SplashView.WaitBeforeLeaving(TimeSpan.FromMilliseconds(200)));
        // Ready exactly then: no wait.
        Assert.Equal(TimeSpan.Zero, Pulse.Views.SplashView.WaitBeforeLeaving(cycle + exit));
    }

    [Fact]
    public void Slow_readings_never_wait_more_than_one_extra_beat()
    {
        var cycle = Pulse.Views.SplashView.Cycle;
        for (var ms = 2400; ms < 8000; ms += 37)
            Assert.True(Pulse.Views.SplashView.WaitBeforeLeaving(TimeSpan.FromMilliseconds(ms)) < cycle);
    }

    // ---- Windows animation setting ------------------------------------------------------------

    [Fact]
    public void With_animations_off_everything_is_instant()
    {
        try
        {
            Motion.Override(false);
            Assert.Equal(TimeSpan.Zero, Motion.Instrument(1, TimeSpan.FromMilliseconds(450)).Duration.TimeSpan);
            Assert.Equal(TimeSpan.Zero, Motion.Ui(0, 1, TimeSpan.FromMilliseconds(320)).Duration.TimeSpan);
            Assert.False(Motion.Current.AnimationsEnabled);

            Motion.Override(true);
            Assert.Equal(TimeSpan.FromMilliseconds(450), Motion.Instrument(1, TimeSpan.FromMilliseconds(450)).Duration.TimeSpan);
        }
        finally
        {
            Motion.Override(null);
        }
    }

    // ---- Heat ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(80, HeatLevel.None, HeatLevel.None)]
    [InlineData(85, HeatLevel.None, HeatLevel.Warm)]
    [InlineData(95, HeatLevel.Warm, HeatLevel.Hot)]
    [InlineData(94, HeatLevel.Hot, HeatLevel.Hot)]     // a degree under the limit: still hot (no flicker)
    [InlineData(92, HeatLevel.Hot, HeatLevel.Warm)]
    [InlineData(84, HeatLevel.Warm, HeatLevel.Warm)]
    [InlineData(82, HeatLevel.Warm, HeatLevel.None)]
    public void Heat_follows_the_alert_limit_with_a_little_hysteresis(double value, HeatLevel before, HeatLevel after) =>
        Assert.Equal(after, Heat.Above(value, 95, before));

    [Fact]
    public void A_slow_pump_is_hot()
    {
        Assert.Equal(HeatLevel.Hot, Heat.Below(300, 500, HeatLevel.None, margin: 50));
        Assert.Equal(HeatLevel.Hot, Heat.Below(520, 500, HeatLevel.Hot, margin: 50));
        Assert.Equal(HeatLevel.None, Heat.Below(2400, 500, HeatLevel.Hot, margin: 50));
        Assert.Equal(HeatLevel.None, Heat.Below(null, 500, HeatLevel.Hot, margin: 50));
    }

    private static HardwareReading Cpu(float package) => Hardware("/intelcpu/0", "CPU", HardwareType.Cpu,
        Sensor("/intelcpu/0/temperature/0", "CPU Package", SensorType.Temperature, package));

    [Fact]
    public void Cards_take_their_limits_from_the_users_alert_rules()
    {
        var dashboard = new DashboardViewModel(true, NewCatalog(out var settings), new SensorStream());

        dashboard.Apply(Of(Cpu(96)));
        Assert.Equal(HeatLevel.Hot, dashboard.Cpu.Heat);

        settings.Update(s => s.Alerts.Rules.Single(r => r.Id == Heat.CpuRule).Threshold = 105);
        dashboard.Apply(Of(Cpu(96)));
        Assert.Equal(HeatLevel.Warm, dashboard.Cpu.Heat);
    }

    [Fact]
    public void A_drive_card_warms_with_its_hottest_drive()
    {
        var dashboard = new DashboardViewModel(true, NewCatalog(out _), new SensorStream());

        dashboard.Apply(Of(
            Hardware("/nvme/2", "A", HardwareType.Storage, Sensor("/nvme/2/temperature/0", "Temperature", SensorType.Temperature, 44)),
            Hardware("/nvme/3", "B", HardwareType.Storage, Sensor("/nvme/3/temperature/0", "Temperature", SensorType.Temperature, 63))));

        Assert.Equal(HeatLevel.Warm, dashboard.DrivesCard.Heat);
    }

    // ---- Themes -------------------------------------------------------------------------------

    [Theory]
    [InlineData(AppearanceSettings.Dark, SystemTheme.Light, ApplicationTheme.Dark)]
    [InlineData(AppearanceSettings.Light, SystemTheme.Dark, ApplicationTheme.Light)]
    [InlineData(AppearanceSettings.MatchWindows, SystemTheme.Light, ApplicationTheme.Light)]
    [InlineData(AppearanceSettings.MatchWindows, SystemTheme.Dark, ApplicationTheme.Dark)]
    [InlineData(AppearanceSettings.MatchWindows, SystemTheme.HCWhite, ApplicationTheme.Light)]
    public void The_theme_choice_resolves_against_windows(string choice, SystemTheme system, ApplicationTheme expected) =>
        Assert.Equal(expected, ThemeService.Resolve(choice, system));

    [Fact]
    public void Matching_windows_is_the_default() =>
        Assert.Equal(AppearanceSettings.MatchWindows, NewSettings().Current.Appearance.Theme);

    private static Dictionary<string, Color> Tokens(string theme)
    {
        var x = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");
        var doc = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Themes", $"{theme}.xaml"));
        var colors = new Dictionary<string, Color>();
        foreach (var element in doc.Root!.Elements())
        {
            var key = (string?)element.Attribute(x + "Key");
            var text = (string?)element.Attribute("Color") ?? (element.Name.LocalName == "Color" ? element.Value : null);
            if (key is null || text is null || text.StartsWith('{')) continue;
            colors[key] = (Color)ColorConverter.ConvertFromString(text);
        }
        return colors;
    }

    [Fact]
    public void Both_themes_define_the_same_tokens()
    {
        var x = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");
        IEnumerable<string> Keys(string theme) => XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Themes", $"{theme}.xaml"))
            .Root!.Elements().Select(e => (string?)e.Attribute(x + "Key")).OfType<string>().Order();

        Assert.Equal(Keys("Dark"), Keys("Light"));
    }

    /// <summary>Mica's base color: the window behind the translucent cards.</summary>
    private static readonly Dictionary<string, Color> Backdrop = new()
    {
        ["Dark"] = Color.FromRgb(0x20, 0x20, 0x20),
        ["Light"] = Color.FromRgb(0xF3, 0xF3, 0xF3),
    };

    /// <summary>WPF-UI's own text colors, which Pulse uses for primary and secondary text.</summary>
    private static readonly Dictionary<string, Color[]> WpfUiText = new()
    {
        ["Dark"] = [Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), Color.FromArgb(0xC5, 0xFF, 0xFF, 0xFF)],
        ["Light"] = [Color.FromArgb(0xE4, 0x00, 0x00, 0x00), Color.FromArgb(0x9E, 0x00, 0x00, 0x00)],
    };

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void Text_on_cards_reads_at_least_4_5_to_1(string theme)
    {
        var tokens = Tokens(theme);
        var card = Over(tokens["Pulse.CardBackground"], Backdrop[theme]);

        var texts = WpfUiText[theme].Select((c, i) => (i == 0 ? "primary" : "secondary", c))
            .Concat([("caption", tokens["Pulse.Text.Caption"]), ("warning", tokens["Pulse.Warning"]),
                     ("info", tokens["Pulse.Info"]), ("error", tokens["Pulse.Danger.Text"])]);
        foreach (var (name, color) in texts)
        {
            var ratio = Contrast(Over(color, card), card);
            Assert.True(ratio >= 4.5, $"{theme} {name} text is {ratio:0.00}:1 on a card");
        }
    }

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void Heat_borders_stand_out_from_the_card(string theme)
    {
        var tokens = Tokens(theme);
        var card = Over(tokens["Pulse.CardBackground"], Backdrop[theme]);
        foreach (var key in new[] { "Pulse.Heat.Warm", "Pulse.Heat.Hot" })
            Assert.True(Contrast(tokens[key], card) >= 3, $"{theme} {key} border is {Contrast(tokens[key], card):0.00}:1");
    }

    private static Color Over(Color top, Color bottom)
    {
        var a = top.A / 255.0;
        byte Mix(byte t, byte b) => (byte)Math.Round(t * a + b * (1 - a));
        return Color.FromRgb(Mix(top.R, bottom.R), Mix(top.G, bottom.G), Mix(top.B, bottom.B));
    }

    private static double Contrast(Color a, Color b)
    {
        static double Luminance(Color c)
        {
            static double Channel(byte v)
            {
                var s = v / 255.0;
                return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        }
        var (l1, l2) = (Luminance(a), Luminance(b));
        return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
    }
}
