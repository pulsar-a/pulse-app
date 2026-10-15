namespace Pulse.Services.History;

/// <summary>
/// Reads sensor history for charts and export. Recent data comes from the in-memory stream at full
/// resolution (the last hour or so, including the minute still being aggregated); older data comes from
/// the database. Results are reduced to at most the requested number of points, keeping each bucket's
/// lowest and highest value so spikes survive.
/// </summary>
public sealed class HistoryStore(SensorStream stream, string databasePath)
{
    public string DatabasePath { get; } = databasePath;

    /// <summary>Runs <see cref="Query"/> on a background thread.</summary>
    public Task<IReadOnlyList<HistoryPoint>> QueryAsync(string sensorId, DateTime from, DateTime to, int maxPoints) =>
        Task.Run(() => Query(sensorId, from, to, maxPoints));

    public IReadOnlyList<HistoryPoint> Query(string sensorId, DateTime from, DateTime to, int maxPoints)
    {
        var live = stream.Since(sensorId, from).Where(s => s.Time <= to).ToList();
        var liveStart = live.Count > 0 ? live[0].Time : DateTime.MaxValue;

        // The disk fills in whatever the live buffer doesn't reach back to.
        IEnumerable<HistoryPoint> disk = from < liveStart
            ? HistoryDatabase.ReadMinutes(DatabasePath, sensorId, from, to < liveStart ? to : liveStart)
                .Where(p => p.Time < liveStart)
            : [];

        var points = disk
            .Concat(live.Select(s => new HistoryPoint(s.Time, s.Value, s.Value, s.Value)))
            .ToList();
        return Downsample(points, from, to, maxPoints);
    }

    /// <summary>
    /// Splits the range into <paramref name="maxPoints"/> equal buckets and merges the points in each:
    /// lowest min, highest max, average of averages. Points are returned as they are if they already fit.
    /// </summary>
    public static IReadOnlyList<HistoryPoint> Downsample(IReadOnlyList<HistoryPoint> points, DateTime from, DateTime to, int maxPoints)
    {
        if (points.Count <= maxPoints || maxPoints <= 0 || to <= from)
            return points;

        var bucketTicks = (to - from).Ticks / (double)maxPoints;
        return points
            .GroupBy(p => Math.Clamp((int)((p.Time - from).Ticks / bucketTicks), 0, maxPoints - 1))
            .OrderBy(g => g.Key)
            .Select(g => new HistoryPoint(
                from.AddTicks((long)(g.Key * bucketTicks)),
                g.Min(p => p.Min),
                g.Average(p => p.Avg),
                g.Max(p => p.Max)))
            .ToList();
    }
}
