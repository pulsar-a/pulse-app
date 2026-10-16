namespace Pulse.Services.Processes;

/// <summary>One process's share of the machine over the last sampling interval.</summary>
/// <param name="Cpu">Percent of the whole CPU (all cores), like Task Manager: 0 to 100.</param>
/// <param name="Gpu">Percent of its busiest GPU engine, like Task Manager; null when GPU counters aren't available.</param>
public sealed record ProcessUsage(int Pid, long CreateTime, string Name, int SessionId, double Cpu, double? Gpu, long Memory);

/// <summary>One reading of every process, published by <see cref="ProcessMonitor"/>.</summary>
/// <param name="Apps">The same processes grouped by program (see <see cref="ProcessApps"/>).</param>
/// <param name="HasGpu">False when Windows' GPU counters couldn't be read; the GPU columns are then hidden.</param>
/// <param name="IsBaseline">The first reading after sampling (re)starts: everything shows 0% until the next one.</param>
public sealed record ProcessList(
    IReadOnlyList<ProcessUsage> Processes,
    IReadOnlyList<AppUsage> Apps,
    bool HasGpu,
    bool IsBaseline,
    DateTime Timestamp);

/// <summary>
/// Turns cumulative CPU times into usage over the interval between two samples. A process is known by its
/// PID and start time together, so a PID that Windows hands to a new process never inherits the old
/// process's CPU time.
/// </summary>
public sealed class ProcessUsageTracker
{
    private Dictionary<(int Pid, long Created), long> _previous = new();
    private long? _previousTime;

    /// <summary>False until the first sample: the next one then only sets the baseline.</summary>
    public bool HasBaseline => _previousTime is not null;

    /// <summary>Forgets the last sample, after a pause: the next sample is a fresh baseline.</summary>
    public void Reset()
    {
        _previous.Clear();
        _previousTime = null;
    }

    /// <param name="now">The sample's time as FILETIME ticks (the same clock as process start times).</param>
    /// <param name="processors">Logical processors: a process using all of them is at 100%.</param>
    /// <returns>Every sampled process. On the first sample (no baseline yet) all CPU values are 0.</returns>
    public List<ProcessUsage> Update(IReadOnlyList<ProcessSample> samples, long now, int processors,
        IReadOnlyDictionary<int, double>? gpu = null)
    {
        var current = new Dictionary<(int, long), long>(samples.Count);
        var result = new List<ProcessUsage>(samples.Count);
        var wall = _previousTime is { } then ? now - then : 0;
        var capacity = (double)wall * Math.Max(1, processors);

        foreach (var sample in samples)
        {
            var key = (sample.Pid, sample.CreateTime);
            current[key] = sample.CpuTime;

            double cpu = 0;
            if (capacity > 0)
            {
                // Seen last time: what it used since. Started since: everything it has used is in this interval.
                // On the very first sample neither is known, so it stays at 0.
                long? used = _previous.TryGetValue(key, out var before) ? sample.CpuTime - before
                    : sample.CreateTime >= _previousTime ? sample.CpuTime
                    : null;
                if (used is > 0 and var u)
                    cpu = Math.Min(100, u / capacity * 100);
            }

            double? gpuShare = gpu is null ? null : gpu.GetValueOrDefault(sample.Pid);
            result.Add(new ProcessUsage(sample.Pid, sample.CreateTime, sample.Name, sample.SessionId, cpu, gpuShare, sample.Memory));
        }

        // Processes that exited simply aren't in the new sample, so their entries go with the old dictionary.
        _previous = current;
        _previousTime = now;
        return result;
    }
}
