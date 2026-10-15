using LibreHardwareMonitor.Hardware;
using Pulse.Models;
using Pulse.Services;
using Pulse.ViewModels;
using static Pulse.Tests.Snap;

namespace Pulse.Tests;

public class PollScheduleTests
{
    private static readonly DateTime T0 = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Runs <paramref name="ticks"/> polling ticks of the schedule's interval and counts the reads.</summary>
    private static int Reads(PollSchedule schedule, HardwareType type, int ticks)
    {
        var reads = 0;
        for (var i = 0; i < ticks; i++)
        {
            // Real ticks drift a few milliseconds; the schedule must not skip a whole tick because of it.
            var now = T0 + schedule.TickInterval * i + TimeSpan.FromMilliseconds(i % 3 * 7);
            if (schedule.IsDue("hw", type, now))
            {
                schedule.MarkRead("hw", now);
                reads++;
            }
        }
        return reads;
    }

    [Fact]
    public void Most_hardware_is_read_every_tick() =>
        Assert.Equal(20, Reads(new PollSchedule(), HardwareType.Cpu, 20));

    [Fact]
    public void Drives_are_read_every_five_seconds() =>
        Assert.Equal(4, Reads(new PollSchedule(), HardwareType.Storage, 20));

    [Fact]
    public void Drives_follow_the_interval_when_it_is_slower_than_five_seconds()
    {
        var schedule = new PollSchedule { Interval = TimeSpan.FromSeconds(5) };
        Assert.Equal(10, Reads(schedule, HardwareType.Storage, 10));
    }

    [Fact]
    public void Idle_mode_slows_ticks_but_never_speeds_them_up()
    {
        var schedule = new PollSchedule { IsIdle = true };
        Assert.Equal(TimeSpan.FromSeconds(2), schedule.TickInterval);

        schedule.Interval = TimeSpan.FromSeconds(5);
        Assert.Equal(TimeSpan.FromSeconds(5), schedule.TickInterval);

        schedule.IsIdle = false;
        schedule.Interval = TimeSpan.FromSeconds(0.5);
        Assert.Equal(TimeSpan.FromSeconds(0.5), schedule.TickInterval);
    }
}

public class SensorStreamTests
{
    private static HardwareSnapshot Reading(float? value, DateTime? at = null, bool includeFan = true)
    {
        var sensors = new List<SensorReading> { Sensor("/cpu/temp", "CPU Package", SensorType.Temperature, value) };
        if (includeFan)
            sensors.Add(Sensor("/fan/0", "Fan #1", SensorType.Fan, 1200));
        return Of(Hardware("/cpu", "CPU", HardwareType.Cpu, sensors.ToArray())) with { Timestamp = at ?? DateTime.Now };
    }

    [Fact]
    public void Recent_returns_samples_oldest_first()
    {
        var stream = new SensorStream();
        foreach (var v in new float[] { 40, 41, 42, 43 })
            stream.Publish(Reading(v));

        Assert.Equal([42.0, 43.0], stream.Recent("/cpu/temp", 2));
        Assert.Equal([40.0, 41, 42, 43], stream.Recent("/cpu/temp", 100));
        Assert.Empty(stream.Recent("/no/such/sensor", 10));
    }

    [Fact]
    public void History_wraps_around_after_capacity()
    {
        var stream = new SensorStream();
        for (var i = 0; i < SensorStream.Capacity + 5; i++)
            stream.Publish(Reading(i));

        Assert.Equal(SensorStream.Capacity, stream.Count);
        var all = stream.Recent("/cpu/temp", SensorStream.Capacity);
        Assert.Equal(5, all[0]);
        Assert.Equal(SensorStream.Capacity + 4, all[^1]);
    }

    [Fact]
    public void Missing_readings_and_missing_sensors_become_gaps()
    {
        var stream = new SensorStream();
        stream.Publish(Reading(40));
        stream.Publish(Reading(null));
        stream.Publish(Reading(42, includeFan: false));

        Assert.True(double.IsNaN(stream.Recent("/cpu/temp", 3)[1]));
        Assert.True(double.IsNaN(stream.Recent("/fan/0", 3)[2]));
    }

    [Fact]
    public void Since_returns_timed_samples_and_skips_gaps()
    {
        var stream = new SensorStream();
        var t = new DateTime(2026, 10, 15, 12, 0, 0);
        stream.Publish(Reading(40, t));
        stream.Publish(Reading(null, t.AddSeconds(1)));
        stream.Publish(Reading(42, t.AddSeconds(2)));

        var samples = stream.Since("/cpu/temp", t.AddSeconds(1));
        Assert.Equal([(t.AddSeconds(2), 42f)], samples);
    }

    [Fact]
    public void Subscribers_hear_about_each_snapshot()
    {
        var stream = new SensorStream();
        var heard = 0;
        stream.SnapshotPublished += _ => heard++;

        stream.Publish(Reading(40));
        stream.Publish(Reading(41));

        Assert.Equal(2, heard);
        Assert.NotNull(stream.Latest);
    }
}

public class DashboardHistoryTests : PulseTest
{
    private static HardwareSnapshot Machine(float temp, float cpuPower, float gpuPower) => Of(
        Hardware("/intelcpu/0", "CPU", HardwareType.Cpu,
            Sensor("/intelcpu/0/temperature/0", "CPU Package", SensorType.Temperature, temp),
            Sensor("/intelcpu/0/power/0", "CPU Package", SensorType.Power, cpuPower)),
        Hardware("/gpu-nvidia/0", "GPU", HardwareType.GpuNvidia,
            Sensor("/gpu-nvidia/0/power/0", "GPU Package", SensorType.Power, gpuPower)));

    [Fact]
    public void Sparklines_come_from_the_stream_and_survive_a_pause()
    {
        var stream = new SensorStream();
        var dashboard = new DashboardViewModel(true, NewCatalog(out _), stream);

        // Three readings are recorded, but the dashboard (as if hidden) only applies the last one.
        stream.Publish(Machine(60, 100, 50));
        stream.Publish(Machine(65, 110, 60));
        var last = Machine(70, 120, 70);
        stream.Publish(last);
        dashboard.Apply(last);

        Assert.Equal([60.0, 65, 70], dashboard.Cpu.History);
    }

    [Fact]
    public void Without_a_PSU_the_power_sparkline_sums_CPU_and_GPU()
    {
        var stream = new SensorStream();
        var dashboard = new DashboardViewModel(true, NewCatalog(out _), stream);

        stream.Publish(Machine(60, 100, 50));
        var last = Machine(60, 110, 60);
        stream.Publish(last);
        dashboard.Apply(last);

        Assert.Equal([150.0, 170], dashboard.Power.History);
    }

    [Theory]
    [InlineData(1.0, 60)]
    [InlineData(0.5, 120)]
    [InlineData(5.0, 12)]
    public void Sparklines_always_span_about_a_minute(double intervalSeconds, int expectedSamples)
    {
        var stream = new SensorStream();
        var dashboard = new DashboardViewModel(true, NewCatalog(out _), stream)
        {
            SampleInterval = TimeSpan.FromSeconds(intervalSeconds),
        };
        var snapshot = Machine(60, 100, 50);
        stream.Publish(snapshot);
        dashboard.Apply(snapshot);

        Assert.Equal(expectedSamples, dashboard.Cpu.HistoryCapacity);
    }

    [Fact]
    public void An_unknown_interval_in_settings_falls_back_to_one_second()
    {
        var settings = new GeneralSettings { UpdateIntervalSeconds = 3 };
        Assert.Equal(TimeSpan.FromSeconds(1), settings.UpdateInterval);
    }
}
