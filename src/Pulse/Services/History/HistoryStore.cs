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

        // Buckets are fixed to the clock (every 6 s, every minute...), not counted from `from`. The chart
        // re-queries as time moves on; with fixed buckets only the newest point changes, instead of every
        // bucket regrouping each second and the whole line shimmering.
        var bucket = BucketSize(to - from, maxPoints).Ticks;
        return points
            .GroupBy(p => p.Time.Ticks / bucket)
            .OrderBy(g => g.Key)
            .Select(g => new HistoryPoint(
                new DateTime(g.Key * bucket, g.First().Time.Kind),
                g.Min(p => p.Min),
                g.Average(p => p.Avg),
                g.Max(p => p.Max)))
            .ToList();
    }

    private static readonly TimeSpan[] BucketSizes =
    [
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(6),
        TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(30), TimeSpan.FromHours(1), TimeSpan.FromHours(2),
        TimeSpan.FromHours(3), TimeSpan.FromHours(6), TimeSpan.FromHours(12), TimeSpan.FromDays(1),
    ];

    /// <summary>The smallest round bucket that fits <paramref name="span"/> into at most <paramref name="maxPoints"/> buckets.</summary>
    public static TimeSpan BucketSize(TimeSpan span, int maxPoints)
    {
        var smallest = span.Ticks / (double)maxPoints;
        foreach (var size in BucketSizes)
        {
            if (size.Ticks >= smallest)
                return size;
        }
        return TimeSpan.FromTicks((long)Math.Ceiling(smallest));
    }
}
