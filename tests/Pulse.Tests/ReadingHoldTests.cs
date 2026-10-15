using LibreHardwareMonitor.Hardware;
using Pulse.Models;
using Pulse.Services;
using static Pulse.Tests.Snap;

namespace Pulse.Tests;

/// <summary>Replays what a Corsair HX1000i shared with iCUE actually reports (captured with a probe).</summary>
public class ReadingHoldTests : PulseTest
{
    private static readonly DateTime T0 = new(2026, 10, 16, 0, 34, 32);

    private static HardwareSnapshot Psu(int second, float? rail12, float? rail5, float? rail3, float? total, float? output) =>
        new([Hardware("/psu/corsair/0", "Corsair HX1000i", HardwareType.Psu,
                Sensor("/psu/corsair/0/power/0", "+12V", SensorType.Power, rail12),
                Sensor("/psu/corsair/0/power/1", "+5V", SensorType.Power, rail5),
                Sensor("/psu/corsair/0/power/2", "+3.3V", SensorType.Power, rail3),
                Sensor("/psu/corsair/0/power/3", "Total watts", SensorType.Power, total),
                Sensor("/psu/corsair/0/power/4", "Total Output", SensorType.Power, output))],
            new Dictionary<int, IReadOnlyList<VolumeInfo>>(), T0.AddSeconds(second));

    private static float? Value(HardwareSnapshot snapshot, string name) =>
        snapshot.Hardware[0].Sensors.Single(s => s.Name == name).Value;

    [Fact]
    public void A_dropped_psu_reading_keeps_the_last_good_values()
    {
        var hold = new ReadingHold();
        hold.Apply(Psu(0, 248, 29.5f, 9.5f, 284, 287));

        // Everything lost; LHM still reports Total Output as 0.
        var dropped = hold.Apply(Psu(1, null, null, null, null, 0));

        Assert.Equal(284, Value(dropped, "Total watts"));
        Assert.Equal(287, Value(dropped, "Total Output"));
    }

    [Fact]
    public void A_partly_lost_psu_reading_is_not_trusted_at_all()
    {
        var hold = new ReadingHold();
        hold.Apply(Psu(0, 218, 31, 9.5f, 254, 258));

        // +12V lost, so LHM's Total Output (the sum of the rails) reads 39.5 W instead of ~270.
        var partial = hold.Apply(Psu(1, null, 30, 9.5f, 274, 39.5f));

        Assert.Equal(258, Value(partial, "Total Output"));
        Assert.Equal(218, Value(partial, "+12V"));
        Assert.Equal(254, Value(partial, "Total watts"));
    }

    [Fact]
    public void Good_readings_pass_through_and_become_the_new_fallback()
    {
        var hold = new ReadingHold();
        hold.Apply(Psu(0, 248, 29.5f, 9.5f, 284, 287));
        var fresh = hold.Apply(Psu(1, 206, 30, 9.5f, 246, 245.5f));
        var dropped = hold.Apply(Psu(2, null, null, null, null, 0));

        Assert.Equal(246, Value(fresh, "Total watts"));
        Assert.Equal(246, Value(dropped, "Total watts"));
    }

    [Fact]
    public void After_the_hold_time_a_lost_psu_shows_as_missing()
    {
        var hold = new ReadingHold();
        hold.Apply(Psu(0, 248, 29.5f, 9.5f, 284, 287));

        var later = hold.Apply(Psu(11, null, 30, 9.5f, 274, 39.5f));

        Assert.Null(Value(later, "Total Output"));
        Assert.Null(Value(later, "+5V"));
    }

    [Fact]
    public void Other_hardware_holds_only_the_sensor_that_dropped()
    {
        var hold = new ReadingHold();
        HardwareSnapshot Gpu(int second, float? hotSpot, float? core) => new(
            [Hardware("/gpu-nvidia/0", "RTX 4090", HardwareType.GpuNvidia,
                Sensor("/gpu-nvidia/0/temperature/0", "GPU Core", SensorType.Temperature, core),
                Sensor("/gpu-nvidia/0/temperature/2", "GPU Hot Spot", SensorType.Temperature, hotSpot))],
            new Dictionary<int, IReadOnlyList<VolumeInfo>>(), T0.AddSeconds(second));

        hold.Apply(Gpu(0, 70, 60));
        var next = hold.Apply(Gpu(1, null, 61));

        Assert.Equal(61, next.Hardware[0].Sensors[0].Value);
        Assert.Equal(70, next.Hardware[0].Sensors[1].Value);
    }

    [Fact]
    public void Sensors_that_never_read_are_left_alone()
    {
        var hold = new ReadingHold();
        hold.Apply(Psu(0, 248, 29.5f, null, 284, 287));
        var input = Psu(1, 250, 29.5f, null, 286, 290);
        var output = hold.Apply(input);

        // A rail this PSU never reports doesn't make every reading look partial.
        Assert.Equal(286, Value(output, "Total watts"));
        Assert.Null(Value(output, "+3.3V"));
        Assert.Same(input.Hardware[0], output.Hardware[0]);
    }
}
