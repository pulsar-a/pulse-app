using System.IO;
using LibreHardwareMonitor.Hardware;
using Pulse.Models;
using Pulse.Services;
using Pulse.Services.History;
using static Pulse.Tests.Snap;

namespace Pulse.Tests;

public class PumpWatchTests : PulseTest
{
    // The Z790 Hero's sensor chip: CPU_FAN is "Fan #2", the pump connector (W_PUMP+) "Fan #6", a case fan on "Fan #1".
    private const string CpuFan = "/lpc/nct6798d/0/fan/1";
    private const string PumpHeader = "/lpc/nct6798d/0/fan/5";
    private const string CaseFan = "/lpc/nct6798d/0/fan/0";
    private static readonly DateTime T0 = new(2026, 10, 21, 23, 21, 0);
    private static readonly List<AlertRule> Rules = AlertDefaults.Create();

    internal static HardwareSnapshot Board(float? fan2, float fan6 = 0, float fan1 = 1200, bool withFan2 = true)
    {
        var sensors = new List<SensorReading> { Sensor(CaseFan, "Fan #1", SensorType.Fan, fan1) };
        if (withFan2)
            sensors.Add(Sensor(CpuFan, "Fan #2", SensorType.Fan, fan2));
        sensors.Add(Sensor(PumpHeader, "Fan #6", SensorType.Fan, fan6));
        return Of(Hardware("/motherboard", "ASUS ROG MAXIMUS Z790 HERO", HardwareType.Motherboard)
            .WithSub(Hardware("/lpc/nct6798d/0", "Nuvoton NCT6798D", HardwareType.SuperIO, [.. sensors])));
    }

    /// <summary>The old AIO: its pump on CPU_FAN, wobbling between 2,540 and 2,590 rpm.</summary>
    internal static HardwareSnapshot OldCooler(int second) => Board(2540 + second % 6 * 10);

    /// <summary>The new AIO: radiator fans on CPU_FAN (around 500 rpm), the pump on the pump connector.</summary>
    internal static HardwareSnapshot NewCooler(int second) => Board(480 + second % 5 * 10, fan6: 2100);

    /// <summary>One Pulse session: a fresh watch (as after a restart) fed one snapshot a second.</summary>
    private static PumpWatch Session(SensorCatalog catalog, int seconds, Func<int, HardwareSnapshot> at, DateTime? start = null,
        Func<string, double, DateTime, Task<PumpProfile?>>? history = null)
    {
        var watch = new PumpWatch(catalog) { History = history };
        Run(watch, seconds, at, start ?? T0);
        return watch;
    }

    private static void Run(PumpWatch watch, int seconds, Func<int, HardwareSnapshot> at, DateTime start)
    {
        for (var s = 0; s < seconds; s++)
            watch.Observe(at(s), Rules, start.AddSeconds(s));
    }

    private SensorCatalog CatalogWithPump(out SettingsService settings, string pump = CpuFan)
    {
        var catalog = NewCatalog(out settings);
        catalog.SetRole(SensorRoles.Pump, pump);
        return catalog;
    }

    [Fact]
    public void Learns_the_pumps_speed_range_and_the_fans_beside_it()
    {
        var catalog = CatalogWithPump(out var settings);
        var watch = Session(catalog, 60, OldCooler);

        var profile = settings.Current.Sensors.Pump!;
        Assert.Equal(CpuFan, profile.SensorId);
        // Medians of five readings, saved when they widen the range by more than 1%: the wobble doesn't count.
        Assert.Equal(2560, profile.Low);
        Assert.Equal(2560, profile.High);
        Assert.Equal([CaseFan], profile.Fans);
        Assert.Null(watch.Change);
        Assert.True(watch.Status.Settled);
    }

    [Fact]
    public void A_restart_with_the_same_pump_finds_nothing_changed()
    {
        var catalog = CatalogWithPump(out _);
        Session(catalog, 60, OldCooler);

        var watch = new PumpWatch(catalog);
        Run(watch, 5, OldCooler, T0.AddHours(1));
        Assert.False(watch.Status.Settled);

        Run(watch, 20, OldCooler, T0.AddHours(1).AddSeconds(5));
        Assert.True(watch.Status.Settled);
        Assert.Null(watch.Change);
    }

    [Fact]
    public void A_new_cooler_whose_fans_took_the_old_pump_header_is_a_change_not_a_failure()
    {
        var catalog = CatalogWithPump(out _);
        Session(catalog, 60, OldCooler);

        var watch = Session(catalog, 20, NewCooler, T0.AddHours(3));

        var change = Assert.IsType<PumpChange>(watch.Change);
        Assert.Equal(CpuFan, change.SensorId);
        Assert.True(change.IsSpinning);
        Assert.Equal(PumpHeader, change.SuggestedId);
        Assert.Equal("Fan #2 has read about 500 rpm since Pulse started. Before, it ran at about 2,560 rpm.", change.Description);
        Assert.Equal("Fan #6 started spinning at 2,100 rpm.", change.Suggestion);
    }

    [Fact]
    public void A_new_pump_on_the_same_header_that_runs_slower_is_a_change_without_a_suggestion()
    {
        var catalog = CatalogWithPump(out _);
        Session(catalog, 60, OldCooler);

        var watch = Session(catalog, 20, _ => Board(1200), T0.AddHours(3));

        var change = Assert.IsType<PumpChange>(watch.Change);
        Assert.Null(change.SuggestedId);
        Assert.Contains("If you changed or re-plugged your cooler, choose the pump header", change.AlertNote);
    }

    [Fact]
    public void A_pump_that_stopped_while_the_PC_was_off_is_still_a_stopped_pump()
    {
        var catalog = CatalogWithPump(out _);
        Session(catalog, 60, OldCooler);

        var watch = Session(catalog, 20, _ => Board(0), T0.AddHours(3));

        Assert.True(watch.Status.Settled);
        Assert.Null(watch.Change);
    }

    [Fact]
    public void A_stopped_pump_header_with_a_new_header_spinning_looks_moved()
    {
        var catalog = CatalogWithPump(out _);
        Session(catalog, 60, OldCooler);

        var watch = Session(catalog, 20, _ => Board(0, fan6: 2100), T0.AddHours(3));

        var change = Assert.IsType<PumpChange>(watch.Change);
        Assert.False(change.IsSpinning);
        Assert.Equal(PumpHeader, change.SuggestedId);
    }

    [Fact]
    public void A_pump_that_is_gone_is_a_change_once_it_had_time_to_turn_up()
    {
        var catalog = CatalogWithPump(out _);
        Session(catalog, 60, OldCooler);

        var watch = new PumpWatch(catalog);
        Run(watch, 20, _ => Board(null, fan6: 2100, withFan2: false), T0.AddHours(3));
        // USB coolers turn up a few seconds in, so "missing" waits longer than the usual check.
        Assert.False(watch.Status.Settled);

        Run(watch, 20, _ => Board(null, fan6: 2100, withFan2: false), T0.AddHours(3).AddSeconds(20));
        var change = Assert.IsType<PumpChange>(watch.Change);
        Assert.Null(change.Rpm);
        Assert.Equal("Fan #6", change.SuggestedName);
        Assert.StartsWith("The pump hasn't reported since Pulse started.", change.Description);
    }

    [Fact]
    public void A_pump_that_turns_up_late_is_checked_like_any_other()
    {
        var catalog = CatalogWithPump(out _);
        Session(catalog, 60, OldCooler);

        var watch = Session(catalog, 30, s => s < 12 ? Board(null, withFan2: false) : OldCooler(s), T0.AddHours(3));

        Assert.True(watch.Status.Settled);
        Assert.Null(watch.Change);
    }

    [Fact]
    public void Using_the_suggested_header_makes_it_the_pump_and_learns_it()
    {
        var catalog = CatalogWithPump(out var settings);
        Session(catalog, 60, OldCooler);
        var watch = Session(catalog, 20, NewCooler, T0.AddHours(3));

        watch.Use(watch.Change!.SuggestedId!);
        Assert.Null(watch.Change);
        Run(watch, 10, NewCooler, T0.AddHours(3).AddSeconds(20));

        Assert.Equal(PumpHeader, catalog.RoleSensor(SensorRoles.Pump));
        Assert.Equal(PumpHeader, settings.Current.Sensors.Pump!.SensorId);
        Assert.Equal(2100, settings.Current.Sensors.Pump.High);
        Assert.Null(watch.Change);
    }

    [Fact]
    public void Confirming_the_same_header_learns_the_new_pump_from_now()
    {
        var catalog = CatalogWithPump(out var settings);
        Session(catalog, 60, OldCooler);
        var watch = Session(catalog, 20, _ => Board(1200), T0.AddHours(3));

        watch.Confirm();
        Run(watch, 10, _ => Board(1200), T0.AddHours(3).AddSeconds(20));

        Assert.Null(watch.Change);
        Assert.Equal(CpuFan, catalog.RoleSensor(SensorRoles.Pump));
        Assert.Equal(1200, settings.Current.Sensors.Pump!.Low);
        Assert.Equal(1200, settings.Current.Sensors.Pump.High);

        // And the next start takes 1,200 rpm as normal.
        Assert.Null(Session(catalog, 20, _ => Board(1200), T0.AddHours(5)).Change);
    }

    [Fact]
    public void Readings_back_to_normal_clear_the_change()
    {
        var catalog = CatalogWithPump(out _);
        Session(catalog, 60, OldCooler);

        var watch = Session(catalog, 30, s => s < 15 ? Board(800) : OldCooler(s), T0.AddHours(3));

        Assert.Null(watch.Change);
    }

    [Fact]
    public void A_pump_watched_before_profiles_existed_is_compared_with_its_history()
    {
        var catalog = CatalogWithPump(out var settings);
        var fromHistory = new PumpProfile { SensorId = CpuFan, Low = 2510, High = 2590, Fans = [CaseFan] };

        var watch = Session(catalog, 20, NewCooler, history: (_, _, _) => Task.FromResult<PumpProfile?>(fromHistory));

        Assert.Equal(2510, settings.Current.Sensors.Pump!.Low);
        Assert.Equal(PumpHeader, Assert.IsType<PumpChange>(watch.Change).SuggestedId);
    }

    [Fact]
    public void The_history_profile_is_the_middle_of_the_healthy_minutes_and_the_fans_then()
    {
        var path = Path.Combine(TempFolder, "history.db");
        var pump = new SensorDescriptor(CpuFan, SensorType.Fan, "Fan #2", "Nuvoton NCT6798D");
        var caseFan = new SensorDescriptor(CaseFan, SensorType.Fan, "Fan #1", "Nuvoton NCT6798D");
        var pumpHeader = new SensorDescriptor(PumpHeader, SensorType.Fan, "Fan #6", "Nuvoton NCT6798D");
        var gpuFan = new SensorDescriptor("/gpu-nvidia/0/fan/1", SensorType.Fan, "GPU Fan 1", "NVIDIA GeForce RTX 4090");
        var start = T0.AddDays(-1);
        using (var db = HistoryDatabase.Open(path))
        {
            // Two hours of the old cooler, then a few minutes of the new one.
            for (var m = 0; m < 120; m++)
                db.Write(Minute(start.AddMinutes(m), (pump, 2500 + m % 10 * 10), (caseFan, 1200), (pumpHeader, 0), (gpuFan, 1500)));
            for (var m = 120; m < 125; m++)
                db.Write(Minute(start.AddMinutes(m), (pump, 600), (caseFan, 1200), (pumpHeader, 2100), (gpuFan, 1500)));
        }

        var profile = PumpWatch.FromHistory(path, CpuFan, 500, T0, days: 7)!;

        Assert.Equal(2500, profile.Low);
        Assert.Equal(2580, profile.High);
        Assert.Equal([CaseFan], profile.Fans);
        Assert.Null(PumpWatch.FromHistory(Path.Combine(TempFolder, "none.db"), CpuFan, 500, T0, days: 7));
    }

    private static MinuteBatch Minute(DateTime minuteLocal, params (SensorDescriptor Sensor, double Value)[] values) =>
        new(MinuteAggregator.MinuteOf(minuteLocal), values.Select(v => new MinuteSample(v.Sensor, v.Value, v.Value, v.Value, 60)).ToList());
}
