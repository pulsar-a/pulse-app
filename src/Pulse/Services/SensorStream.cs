using Pulse.Models;

namespace Pulse.Services;

/// <summary>
/// The hub every consumer of live sensor data subscribes to. Each published snapshot is appended to
/// an in-memory history (the last <see cref="Capacity"/> ticks for every sensor, about an hour at the
/// default 1 s interval) and then handed to subscribers.
///
/// Publishing happens on the polling thread; reads can come from any thread.
/// </summary>
public sealed class SensorStream
{
    /// <summary>Samples kept per sensor. 3600 × ~400 sensors × 4 bytes ≈ 6 MB.</summary>
    public const int Capacity = 3600;

    private readonly object _gate = new();
    private readonly DateTime[] _times = new DateTime[Capacity];
    private readonly Dictionary<string, float[]> _values = new();
    private int _head;
    private int _count;

    /// <summary>Raised on the polling thread after each snapshot is recorded.</summary>
    public event Action<HardwareSnapshot>? SnapshotPublished;

    public HardwareSnapshot? Latest { get; private set; }

    /// <summary>Number of samples recorded so far (up to <see cref="Capacity"/>).</summary>
    public int Count
    {
        get { lock (_gate) return _count; }
    }

    public void Publish(HardwareSnapshot snapshot)
    {
        lock (_gate)
        {
            // Every series gets a slot this tick; sensors missing from the snapshot record a gap.
            foreach (var series in _values.Values)
                series[_head] = float.NaN;

            foreach (var sensor in snapshot.Hardware.SelectMany(h => h.AllSensors))
            {
                if (!_values.TryGetValue(sensor.Id, out var series))
                {
                    series = new float[Capacity];
                    Array.Fill(series, float.NaN);
                    _values[sensor.Id] = series;
                }
                series[_head] = sensor.Value ?? float.NaN;
            }

            _times[_head] = snapshot.Timestamp;
            _head = (_head + 1) % Capacity;
            _count = Math.Min(_count + 1, Capacity);
            Latest = snapshot;
        }

        SnapshotPublished?.Invoke(snapshot);
    }

    /// <summary>
    /// The last <paramref name="count"/> samples of a sensor, oldest first. Gaps are NaN.
    /// Returns fewer samples while the history is still filling up, and an empty array for unknown sensors.
    /// </summary>
    public double[] Recent(string sensorId, int count)
    {
        lock (_gate)
        {
            if (!_values.TryGetValue(sensorId, out var series))
                return [];

            var n = Math.Min(count, _count);
            var result = new double[n];
            var start = (_head - n + Capacity) % Capacity;
            for (var i = 0; i < n; i++)
                result[i] = series[(start + i) % Capacity];
            return result;
        }
    }

    /// <summary>All recorded samples of a sensor at or after <paramref name="from"/>, oldest first, skipping gaps.</summary>
    public IReadOnlyList<(DateTime Time, float Value)> Since(string sensorId, DateTime from)
    {
        lock (_gate)
        {
            if (!_values.TryGetValue(sensorId, out var series))
                return [];

            var result = new List<(DateTime, float)>();
            var start = (_head - _count + Capacity) % Capacity;
            for (var i = 0; i < _count; i++)
            {
                var index = (start + i) % Capacity;
                if (_times[index] >= from && !float.IsNaN(series[index]))
                    result.Add((_times[index], series[index]));
            }
            return result;
        }
    }
}
