using LibreHardwareMonitor.Hardware;
using Pulse.Models;

namespace Pulse.Services;

/// <summary>
/// Bridges short read failures, so a dropped reading doesn't show up as a hole in charts and history.
///
/// Some devices lose a reading now and then. A Corsair PSU shares its USB link with iCUE: when both ask
/// at the same moment, the answer is lost and LHM reports no value. A sensor without a reading keeps its
/// last good value for up to <see cref="HoldTime"/>; after that it shows as missing, as before.
///
/// A PSU is all or nothing: when some rails fail, LHM's "Total Output" (the sum of the rails) is wrong too,
/// so if any of its sensors drops out, the whole PSU keeps its last good reading.
///
/// Runs on the polling thread only.
/// </summary>
public sealed class ReadingHold
{
    public static readonly TimeSpan HoldTime = TimeSpan.FromSeconds(10);

    private readonly Dictionary<string, (SensorReading Sensor, DateTime Time)> _lastGood = new();
    private readonly HashSet<string> _seen = new();
    private readonly HashSet<string> _reported = new();

    public HardwareSnapshot Apply(HardwareSnapshot snapshot) =>
        snapshot with { Hardware = snapshot.Hardware.Select(h => Apply(h, snapshot.Timestamp)).ToList() };

    private HardwareReading Apply(HardwareReading hardware, DateTime now)
    {
        var sub = hardware.SubHardware.Select(h => Apply(h, now)).ToList();
        IReadOnlyList<SensorReading> sensors = hardware.Sensors;

        if (hardware.Type == HardwareType.Psu && hardware.Sensors.Any(s => !s.HasValue && _seen.Contains(s.Id)))
        {
            // Part of the PSU's answer was lost, so none of it can be trusted: hold the last good reading,
            // or show nothing once that's too old.
            sensors = hardware.Sensors.Select(s => Held(s.Id, now) ?? s with { Value = null }).ToList();
            Report(hardware);
        }
        else
        {
            if (hardware.Sensors.Any(s => !s.HasValue && Held(s.Id, now) is not null))
            {
                sensors = hardware.Sensors.Select(s => s.HasValue ? s : Held(s.Id, now) ?? s).ToList();
                Report(hardware);
            }
            foreach (var sensor in hardware.Sensors)
            {
                if (!sensor.HasValue) continue;
                _lastGood[sensor.Id] = (sensor, now);
                _seen.Add(sensor.Id);
            }
        }

        return ReferenceEquals(sensors, hardware.Sensors) && sub.SequenceEqual(hardware.SubHardware)
            ? hardware
            : hardware with { Sensors = sensors, SubHardware = sub };
    }

    private SensorReading? Held(string sensorId, DateTime now) =>
        _lastGood.TryGetValue(sensorId, out var good) && now - good.Time <= HoldTime ? good.Sensor : null;

    /// <summary>Logs the first dropped reading per device, to explain flat stretches in a chart.</summary>
    private void Report(HardwareReading hardware)
    {
        if (!_reported.Add(hardware.Id))
            return;
        var hint = hardware.Type == HardwareType.Psu ? " Another app (iCUE, for one) may be reading the PSU at the same time." : "";
        Log.Write($"{hardware.Name} dropped a reading; holding the last values for up to {HoldTime.TotalSeconds:0} s.{hint}");
    }
}
