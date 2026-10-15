using System.IO;
using System.Threading.Channels;
using Pulse.Models;

namespace Pulse.Services.History;

/// <summary>
/// Records the sensor stream to disk. Minutes are aggregated on the polling thread (cheap) and written by
/// a background task through a queue, so SQLite never blocks polling or the UI. Old history is pruned
/// hourly according to Settings → History. If the database fails, the recorder logs it and stops writing;
/// it never takes Pulse down.
/// </summary>
public sealed class HistoryRecorder : IDisposable
{
    private static readonly TimeSpan PruneEvery = TimeSpan.FromHours(1);

    private readonly SensorStream _stream;
    private readonly SettingsService _settings;
    private readonly MinuteAggregator _aggregator = new();
    private readonly Channel<Func<HistoryDatabase, Task>> _work =
        Channel.CreateUnbounded<Func<HistoryDatabase, Task>>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _writer;
    private readonly object _aggregateGate = new();

    public HistoryRecorder(SensorStream stream, SettingsService settings, string? databasePath = null)
    {
        _stream = stream;
        _settings = settings;
        DatabasePath = databasePath ?? Path.Combine(SettingsService.DefaultFolder, "history.db");
        _writer = Task.Run(WriterLoopAsync);
        _stream.SnapshotPublished += OnSnapshotPublished;
    }

    public string DatabasePath { get; }

    /// <summary>History retention in days; 0 means recording is off.</summary>
    private int RetentionDays => _settings.Current.History.RetentionDays;

    /// <summary>Database size on disk, including the write-ahead log.</summary>
    public long SizeBytes =>
        new[] { "", "-wal" }.Select(s => new FileInfo(DatabasePath + s)).Where(f => f.Exists).Sum(f => f.Length);

    private void OnSnapshotPublished(HardwareSnapshot snapshot)
    {
        if (RetentionDays <= 0)
            return;

        MinuteBatch? finished;
        lock (_aggregateGate)
            finished = _aggregator.Add(snapshot);
        if (finished is not null)
            Enqueue(db => { db.Write(finished); return Task.CompletedTask; });
    }

    /// <summary>Deletes all recorded history.</summary>
    public Task ClearAsync()
    {
        var done = new TaskCompletionSource();
        Enqueue(db =>
        {
            db.Clear();
            Log.Write("History cleared");
            done.SetResult();
            return Task.CompletedTask;
        });
        return done.Task;
    }

    private void Enqueue(Func<HistoryDatabase, Task> work) => _work.Writer.TryWrite(work);

    private async Task WriterLoopAsync()
    {
        HistoryDatabase? db = null;
        var nextPrune = DateTime.UtcNow.AddMinutes(1);
        try
        {
            db = HistoryDatabase.Open(DatabasePath);
            await foreach (var work in _work.Reader.ReadAllAsync())
            {
                try
                {
                    await work(db);
                    if (DateTime.UtcNow >= nextPrune && RetentionDays > 0)
                    {
                        var removed = db.Prune(DateTime.Now.AddDays(-RetentionDays));
                        if (removed > 0)
                            Log.Write($"History: pruned {removed} rows older than {RetentionDays} days");
                        nextPrune = DateTime.UtcNow + PruneEvery;
                    }
                }
                catch (Exception ex)
                {
                    // One failed write (disk full, file locked by a backup tool) shouldn't stop recording.
                    Log.Write($"History write failed: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write($"History recording stopped: {ex}");
        }
        finally
        {
            db?.Dispose();
        }
    }

    /// <summary>Writes the minute in progress and waits (briefly) for the queue to drain.</summary>
    public void Dispose()
    {
        _stream.SnapshotPublished -= OnSnapshotPublished;
        MinuteBatch? partial;
        lock (_aggregateGate)
            partial = _aggregator.Flush();
        if (partial is not null && RetentionDays > 0)
            Enqueue(db => { db.Write(partial); return Task.CompletedTask; });
        _work.Writer.TryComplete();
        _writer.Wait(TimeSpan.FromSeconds(5));
    }
}
