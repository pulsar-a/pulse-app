using LibreHardwareMonitor.Hardware;
using Pulse.Models;

namespace Pulse.Services.History;

/// <summary>Who a sensor is, for the history database and CSV headers (independent of renames).</summary>
public sealed record SensorDescriptor(string Id, SensorType Type, string Name, string Hardware);

/// <summary>One sensor's minute: lowest, average and highest reading, from <see cref="Count"/> samples.</summary>
public sealed record MinuteSample(SensorDescriptor Sensor, double Min, double Avg, double Max, int Count);

/// <summary>Every sensor that had readings during one minute. <see cref="Minute"/> is the minute's start, in UTC.</summary>
public sealed record MinuteBatch(DateTime Minute, IReadOnlyList<MinuteSample> Samples);

/// <summary>
/// Folds per-second snapshots into one min/avg/max per sensor per minute. When a snapshot belongs to a
/// new minute, the previous minute is returned as a finished batch. Pure logic: no clock, no I/O.
/// </summary>
public sealed class MinuteAggregator
{
    private sealed class Accumulator(SensorDescriptor sensor)
    {
        public SensorDescriptor Sensor { get; set; } = sensor;
        public double Min = double.MaxValue;
        public double Max = double.MinValue;
        public double Sum;
        public int Count;
    }

    private readonly Dictionary<string, Accumulator> _current = new();
    private DateTime? _minute;

    /// <summary>Adds a snapshot; returns the previous minute's batch if this snapshot starts a new minute.</summary>
    public MinuteBatch? Add(HardwareSnapshot snapshot)
    {
        var minute = MinuteOf(snapshot.Timestamp);
        MinuteBatch? finished = null;
        if (_minute is { } current && minute != current)
            finished = Flush();
        _minute = minute;

        foreach (var (sensor, hardware) in WithOwners(snapshot.Hardware))
        {
            if (sensor.Value is not { } v || float.IsNaN(v) || float.IsInfinity(v))
                continue;

            if (!_current.TryGetValue(sensor.Id, out var acc))
                _current[sensor.Id] = acc = new Accumulator(new SensorDescriptor(sensor.Id, sensor.Type, sensor.Name, hardware));
            acc.Min = Math.Min(acc.Min, v);
            acc.Max = Math.Max(acc.Max, v);
            acc.Sum += v;
            acc.Count++;
        }

        return finished;
    }

    /// <summary>Returns the minute in progress (if any) and starts over. Used at shutdown.</summary>
    public MinuteBatch? Flush()
    {
        if (_minute is not { } minute || _current.Count == 0)
        {
            _current.Clear();
            return null;
        }

        var samples = _current.Values
            .Select(a => new MinuteSample(a.Sensor, a.Min, a.Sum / a.Count, a.Max, a.Count))
            .ToList();
        _current.Clear();
        return new MinuteBatch(minute, samples);
    }

    /// <summary>The start of the minute, in UTC.</summary>
    public static DateTime MinuteOf(DateTime time)
    {
        var utc = time.Kind == DateTimeKind.Utc ? time : time.ToUniversalTime();
        return new DateTime(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
    }

    private static IEnumerable<(SensorReading Sensor, string Hardware)> WithOwners(IEnumerable<HardwareReading> hardware)
    {
        foreach (var item in hardware)
        {
            foreach (var sensor in item.Sensors)
                yield return (sensor, item.Name);
            foreach (var sub in WithOwners(item.SubHardware))
                yield return sub;
        }
    }
}
