using LibreHardwareMonitor.Hardware;
using Pulse.Controls;
using Pulse.Models;
using Pulse.Services;
using Pulse.Services.History;
using Pulse.ViewModels;
using static Pulse.Tests.Snap;

namespace Pulse.Tests;

public class SensorDetailsTests : PulseTest
{
    private static readonly DateTime T0 = new(2026, 10, 15, 12, 0, 0);

    private static HistoryPoint Point(int minute, double avg, double spread = 0) =>
        new(T0.AddMinutes(minute), avg - spread, avg, avg + spread);

    // ---- Chart math ---------------------------------------------------------------------------

    [Theory]
    [InlineData(0.3, 0.5)]
    [InlineData(1.7, 2)]
    [InlineData(2.2, 2.5)]
    [InlineData(9.4, 10)]
    [InlineData(23, 25)]
    [InlineData(0, 1)]
    public void Nice_steps_are_1_2_2_5_or_5_times_a_power_of_ten(double rough, double expected) =>
        Assert.Equal(expected, ChartMath.NiceStep(rough), 9);

    [Fact]
    public void Nice_range_rounds_outward_to_round_numbers()
    {
        Assert.Equal((40, 80), ChartMath.NiceRange(41.3, 78.9));
        Assert.Equal([40.0, 50, 60, 70, 80], ChartMath.Ticks(40, 80, 5));
    }

    [Fact]
    public void A_flat_series_still_gets_a_range_around_it()
    {
        var (low, high) = ChartMath.NiceRange(50, 50);
        Assert.True(low < 50 && high > 50);
    }

    [Fact]
    public void Time_ticks_land_on_round_times()
    {
        var ticks = ChartMath.TimeTicks(T0.AddMinutes(3), T0.AddMinutes(63), 6).ToList();

        Assert.Equal(["12:10", "12:20", "12:30", "12:40", "12:50", "13:00"], ticks.Select(t => t.Label));
    }

    [Fact]
    public void Five_minutes_get_a_label_every_minute()
    {
        var ticks = ChartMath.TimeTicks(T0.AddSeconds(20), T0.AddMinutes(5).AddSeconds(20), 5).ToList();
        Assert.Equal(["12:01", "12:02", "12:03", "12:04", "12:05"], ticks.Select(t => t.Label));
    }

    [Fact]
    public void A_week_is_labelled_by_day()
    {
        var ticks = ChartMath.TimeTicks(T0, T0.AddDays(7), 7).ToList();

        Assert.Equal(7, ticks.Count);
        Assert.All(ticks, t => Assert.Equal(TimeSpan.Zero, t.Time.TimeOfDay));
        Assert.Equal("Fri 16", ticks[0].Label);
    }

    [Fact]
    public void Lines_break_where_pulse_was_not_running()
    {
        var points = Enumerable.Range(0, 10).Select(m => Point(m, 50))
            .Concat(Enumerable.Range(40, 10).Select(m => Point(m, 50)))
            .ToList();

        var runs = ChartMath.Runs(points);

        Assert.Equal(2, runs.Count);
        Assert.Equal(10, runs[0].Count);
        Assert.Equal(T0.AddMinutes(40), runs[1][0].Time);
    }

    [Fact]
    public void Evenly_spaced_points_are_one_run() =>
        Assert.Single(ChartMath.Runs(Enumerable.Range(0, 60).Select(m => Point(m, 50)).ToList()));

    // ---- Stats and export ---------------------------------------------------------------------

    [Fact]
    public void Stats_cover_the_whole_range_including_spikes()
    {
        var stats = SensorDetailsViewModel.ComputeStats([Point(0, 60, 5), Point(1, 70), Point(2, 80), Point(3, 90, 8)], limit: 75);

        Assert.NotNull(stats);
        Assert.Equal(55, stats.Min);
        Assert.Equal(75, stats.Avg);
        Assert.Equal(98, stats.Max);
        Assert.Equal(0.5, stats.FractionAbove);
        Assert.Equal(TimeSpan.FromSeconds(90), stats.TimeAbove);
    }

    [Fact]
    public void No_points_means_no_stats() =>
        Assert.Null(SensorDetailsViewModel.ComputeStats([], 80));

    [Fact]
    public void Without_an_alert_limit_there_is_no_time_above()
    {
        var stats = SensorDetailsViewModel.ComputeStats([Point(0, 60), Point(1, 70)], limit: null);
        Assert.Null(stats!.TimeAbove);
    }

    [Fact]
    public void Time_above_the_limit_reads_naturally()
    {
        var hot = SensorDetailsViewModel.ComputeStats([Point(0, 60), Point(10, 80), Point(20, 90), Point(30, 60)], 75);
        var cool = SensorDetailsViewModel.ComputeStats([Point(0, 60), Point(10, 70)], 75);

        Assert.Equal("15 min above the 75° alert limit (50% of this range)", SensorDetailsViewModel.DescribeTimeAbove(hot, "75°"));
        Assert.Equal("Stayed below the 75° alert limit the whole time", SensorDetailsViewModel.DescribeTimeAbove(cool, "75°"));
        Assert.Null(SensorDetailsViewModel.DescribeTimeAbove(cool, null));
    }

    [Fact]
    public void Csv_export_uses_the_chosen_temperature_unit()
    {
        HistoryPoint[] points = [new(T0, 40, 45.5, 50)];

        Assert.Equal(["Time,Min,Average,Max", "2026-10-15 12:00:00,40,45.5,50"],
            SensorDetailsViewModel.ToCsv(points, SensorType.Temperature));

        SensorFormat.TemperatureUnit = TemperatureUnit.Fahrenheit;
        Assert.Equal("2026-10-15 12:00:00,104,113.9,122", SensorDetailsViewModel.ToCsv(points, SensorType.Temperature).Last());
        // Other kinds are never converted.
        Assert.Equal("2026-10-15 12:00:00,40,45.5,50", SensorDetailsViewModel.ToCsv(points, SensorType.Fan).Last());
    }

    [Fact]
    public void Axis_labels_follow_the_temperature_unit()
    {
        var format = SensorDetailsViewModel.FormatterFor(SensorType.Temperature);
        Assert.Equal("72.5°", format(72.5));

        SensorFormat.TemperatureUnit = TemperatureUnit.Fahrenheit;
        Assert.Equal("162.5°", format(72.5));
    }

    // ---- Alert limit shown in the stats -------------------------------------------------------

    private static HardwareSnapshot Machine() => Of(
        Hardware("/intelcpu/0", "CPU", HardwareType.Cpu,
            Sensor("/intelcpu/0/temperature/0", "CPU Package", SensorType.Temperature, 60),
            Sensor("/intelcpu/0/temperature/1", "Core #1", SensorType.Temperature, 58)),
        Hardware("/nvme/2", "CT4000T705SSD3", HardwareType.Storage,
            Sensor("/nvme/2/temperature/0", "Composite Temperature", SensorType.Temperature, 45)));

    [Fact]
    public void Built_in_alerts_give_the_limit_for_their_sensors()
    {
        var rules = AlertDefaults.Create();

        Assert.Equal(95, AlertEngine.AboveThresholdFor("/intelcpu/0/temperature/0", Machine(), rules, null));
        Assert.Equal(70, AlertEngine.AboveThresholdFor("/nvme/2/temperature/0", Machine(), rules, null));
        Assert.Null(AlertEngine.AboveThresholdFor("/intelcpu/0/temperature/1", Machine(), rules, null));
    }

    [Fact]
    public void A_disabled_alert_gives_no_limit()
    {
        var rules = AlertDefaults.Create();
        rules.Single(r => r.Id == "builtin.cpu").Enabled = false;

        Assert.Null(AlertEngine.AboveThresholdFor("/intelcpu/0/temperature/0", Machine(), rules, null));
    }

    // ---- Search -------------------------------------------------------------------------------

    private static readonly SensorInfo[] Sensors =
    [
        new("/intelcpu/0/temperature/0", "CPU Package", "CPU Package", SensorType.Temperature, "Intel Core i9-14900K"),
        new("/intelcpu/0/load/0", "CPU Total", "CPU Total", SensorType.Load, "Intel Core i9-14900K"),
        new("/lpc/nct6798d/0/fan/1", "Fan #2", "Pump", SensorType.Fan, "Nuvoton NCT6798D"),
        new("/gpu-nvidia/0/temperature/2", "GPU Hot Spot", "GPU Hot Spot", SensorType.Temperature, "NVIDIA GeForce RTX 4090"),
    ];

    private static List<string> Find(string query) =>
        SensorSearchViewModel.Search(Sensors, query, 40).Select(s => s.DisplayName).ToList();

    [Fact]
    public void Search_matches_custom_and_original_names()
    {
        Assert.Equal(["Pump"], Find("pump"));
        Assert.Equal(["Pump"], Find("fan #2"));
    }

    [Fact]
    public void Every_word_must_match_somewhere()
    {
        Assert.Equal(["CPU Package"], Find("14900 temperature"));
        Assert.Equal(["GPU Hot Spot"], Find("nvidia hot"));
        Assert.Empty(Find("nvidia fan"));
    }

    [Fact]
    public void Names_starting_with_the_query_rank_first() =>
        Assert.Equal(["Pump", "CPU Package", "CPU Total", "GPU Hot Spot"], Find("pu"));

    [Fact]
    public void An_empty_query_lists_everything_in_order() =>
        Assert.Equal(Sensors.Select(s => s.DisplayName), Find(" "));

    [Fact]
    public void The_catalog_lists_every_sensor_with_its_custom_name()
    {
        var catalog = NewCatalog(out _);
        catalog.Update(Machine());
        catalog.Rename("/nvme/2/temperature/0", "Boot drive");

        var all = catalog.AllSensors();

        Assert.Equal(3, all.Count);
        Assert.Contains(all, s => s.DisplayName == "Boot drive" && s.HardwareName == "CT4000T705SSD3");
    }

    // ---- Where details open from --------------------------------------------------------------

    [Fact]
    public void Dashboard_cards_know_which_sensor_they_show()
    {
        var dashboard = new DashboardViewModel(true, NewCatalog(out _), new SensorStream());
        dashboard.Apply(Of(
            Hardware("/intelcpu/0", "CPU", HardwareType.Cpu,
                Sensor("/intelcpu/0/temperature/0", "CPU Package", SensorType.Temperature, 60),
                Sensor("/intelcpu/0/load/0", "CPU Total", SensorType.Load, 20)),
            Hardware("/motherboard", "Board", HardwareType.Motherboard)
                .WithSub(Hardware("/lpc/nct6798d/0", "Nuvoton NCT6798D", HardwareType.SuperIO,
                    Sensor("/lpc/nct6798d/0/fan/0", "Fan #1", SensorType.Fan, 900),
                    Sensor("/lpc/nct6798d/0/fan/1", "Fan #2", SensorType.Fan, 2400)))));

        Assert.Equal("/intelcpu/0/temperature/0", dashboard.Cpu.SensorId);
        Assert.NotEmpty(dashboard.Fans);
        Assert.All(dashboard.Fans, fan => Assert.StartsWith("/lpc/nct6798d/0/fan/", fan.SensorId));
    }
}
