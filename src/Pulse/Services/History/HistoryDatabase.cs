using System.IO;
using Microsoft.Data.Sqlite;

namespace Pulse.Services.History;

/// <summary>A point in a history series: the lowest, average and highest value over its time span.</summary>
public sealed record HistoryPoint(DateTime Time, double Min, double Avg, double Max);

/// <summary>
/// The on-disk history: one row per sensor per minute, kept compact.
/// <list type="bullet">
/// <item>Values are stored as whole hundredths (72.35 → 7235), which SQLite packs into a few bytes.</item>
/// <item>A minute identical to the sensor's previous one isn't stored, except once an hour as a keyframe.
/// Reads carry the last value forward, but only inside periods Pulse was running (the <c>session</c> table)
/// and never past an explicit gap (a row with NULL values, written when a sensor stops reporting).</item>
/// </list>
/// Not thread-safe; the recorder owns one instance on its writer thread. Reads open their own connection.
/// </summary>
public sealed class HistoryDatabase : IDisposable
{
    public const int SchemaVersion = 1;
    private const double Scale = 100;

    /// <summary>Minutes between keyframes; also how far back a read looks for a value to carry forward.</summary>
    public const int KeyframeMinutes = 60;

    private readonly SqliteConnection _connection;
    private readonly Dictionary<string, long> _sensorIds = new();
    private readonly Dictionary<long, (long Min, long Avg, long Max, long Minute)> _lastStored = new();
    private readonly HashSet<long> _missing = [];
    private long? _sessionId;

    public string FilePath { get; }

    private HistoryDatabase(string path, SqliteConnection connection)
    {
        FilePath = path;
        _connection = connection;
    }

    /// <summary>
    /// Opens (or creates) the database. A corrupt or unreadable file is moved aside as
    /// history.corrupt-*.db and a fresh one started, so a bad file never stops Pulse.
    /// </summary>
    public static HistoryDatabase Open(string path)
    {
        try
        {
            return OpenCore(path);
        }
        catch (SqliteException ex)
        {
            var aside = Path.Combine(Path.GetDirectoryName(path)!, $"history.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}.db");
            Log.Write($"History database unusable ({ex.Message}); moved to {aside} and starting a new one");
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                if (File.Exists(path + suffix))
                    File.Move(path + suffix, aside + suffix, overwrite: true);
            }
            return OpenCore(path);
        }
    }

    private static HistoryDatabase OpenCore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var connection = new SqliteConnection(ConnectionString(path, readOnly: false));
        connection.Open();
        try
        {
            Execute(connection, """
                PRAGMA auto_vacuum = INCREMENTAL;
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;
                CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS sensor (
                    id INTEGER PRIMARY KEY,
                    key TEXT NOT NULL UNIQUE,
                    type INTEGER NOT NULL,
                    name TEXT NOT NULL,
                    hardware TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS minute (
                    sensor_id INTEGER NOT NULL,
                    ts INTEGER NOT NULL,
                    min INTEGER, avg INTEGER, max INTEGER,
                    PRIMARY KEY (sensor_id, ts)) WITHOUT ROWID;
                CREATE TABLE IF NOT EXISTS session (id INTEGER PRIMARY KEY, start_ts INTEGER NOT NULL, end_ts INTEGER NOT NULL);
                INSERT OR IGNORE INTO meta (key, value) VALUES ('schema', '1');
                """);
            using var check = connection.CreateCommand();
            check.CommandText = "PRAGMA quick_check";
            if (check.ExecuteScalar() as string != "ok")
                throw new SqliteException("quick_check failed", 11);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
        return new HistoryDatabase(path, connection);
    }

    /// <summary>Writes one minute for every sensor in the batch, in a single transaction.</summary>
    public int Write(MinuteBatch batch)
    {
        var ts = ToMinutes(batch.Minute);
        var written = 0;
        using var transaction = _connection.BeginTransaction();

        using var insert = _connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT OR REPLACE INTO minute (sensor_id, ts, min, avg, max) VALUES ($s, $t, $min, $avg, $max)";
        var pSensor = insert.Parameters.Add("$s", SqliteType.Integer);
        var pTs = insert.Parameters.Add("$t", SqliteType.Integer);
        var pMin = insert.Parameters.Add("$min", SqliteType.Integer);
        var pAvg = insert.Parameters.Add("$avg", SqliteType.Integer);
        var pMax = insert.Parameters.Add("$max", SqliteType.Integer);
        pTs.Value = ts;

        var present = new HashSet<long>();
        foreach (var sample in batch.Samples)
        {
            var id = SensorId(sample.Sensor, transaction);
            present.Add(id);
            var row = (Min: ToStored(sample.Min), Avg: ToStored(sample.Avg), Max: ToStored(sample.Max));

            // Unchanged since the last stored minute, and that minute isn't too old: skip it.
            if (_lastStored.TryGetValue(id, out var last) && !_missing.Contains(id)
                && last.Min == row.Min && last.Avg == row.Avg && last.Max == row.Max
                && ts - last.Minute < KeyframeMinutes)
                continue;

            pSensor.Value = id;
            pMin.Value = row.Min;
            pAvg.Value = row.Avg;
            pMax.Value = row.Max;
            insert.ExecuteNonQuery();
            _lastStored[id] = (row.Min, row.Avg, row.Max, ts);
            _missing.Remove(id);
            written++;
        }

        // Sensors that stopped reporting get one gap row, so reads don't carry their old value forward.
        foreach (var id in _lastStored.Keys.Where(id => !present.Contains(id) && !_missing.Contains(id)).ToList())
        {
            pSensor.Value = id;
            pMin.Value = pAvg.Value = pMax.Value = DBNull.Value;
            insert.ExecuteNonQuery();
            _missing.Add(id);
            written++;
        }

        ExtendSession(ts, transaction);
        transaction.Commit();
        return written;
    }

    /// <summary>Records that Pulse was running through minute <paramref name="ts"/>.</summary>
    private void ExtendSession(long ts, SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        if (_sessionId is { } id)
        {
            command.CommandText = "UPDATE session SET end_ts = $t WHERE id = $id";
            command.Parameters.AddWithValue("$id", id);
        }
        else
        {
            command.CommandText = "INSERT INTO session (start_ts, end_ts) VALUES ($t, $t) RETURNING id";
        }
        command.Parameters.AddWithValue("$t", ts);
        var result = command.ExecuteScalar();
        _sessionId ??= (long)result!;
    }

    /// <summary>Deletes history older than <paramref name="olderThan"/> and returns freed pages to the disk.</summary>
    public int Prune(DateTime olderThan)
    {
        var cutoff = ToMinutes(MinuteAggregator.MinuteOf(olderThan));
        var deleted = Execute(_connection, "DELETE FROM minute WHERE ts < $t", ("$t", cutoff));
        Execute(_connection, "DELETE FROM session WHERE end_ts < $t", ("$t", cutoff));
        Execute(_connection, "PRAGMA incremental_vacuum");
        return deleted;
    }

    /// <summary>Removes all history (Settings → History → Delete history).</summary>
    public void Clear()
    {
        Execute(_connection, "DELETE FROM minute; DELETE FROM session; PRAGMA incremental_vacuum;");
        _lastStored.Clear();
        _missing.Clear();
        _sessionId = null;
    }

    private long SensorId(SensorDescriptor sensor, SqliteTransaction transaction)
    {
        if (_sensorIds.TryGetValue(sensor.Id, out var id))
            return id;

        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO sensor (key, type, name, hardware) VALUES ($k, $type, $name, $hw)
            ON CONFLICT (key) DO UPDATE SET name = excluded.name, hardware = excluded.hardware
            RETURNING id
            """;
        command.Parameters.AddWithValue("$k", sensor.Id);
        command.Parameters.AddWithValue("$type", (int)sensor.Type);
        command.Parameters.AddWithValue("$name", sensor.Name);
        command.Parameters.AddWithValue("$hw", sensor.Hardware);
        id = (long)command.ExecuteScalar()!;
        _sensorIds[sensor.Id] = id;
        return id;
    }

    // ---- Reading -------------------------------------------------------------------------------

    /// <summary>
    /// One point per minute between <paramref name="from"/> and <paramref name="to"/> where the value is
    /// known: stored minutes, plus skipped (unchanged) minutes filled in from the previous row, but only
    /// while Pulse was running and never across a gap row.
    /// </summary>
    public static IReadOnlyList<HistoryPoint> ReadMinutes(string path, string sensorKey, DateTime from, DateTime to)
    {
        if (!File.Exists(path))
            return [];

        using var connection = new SqliteConnection(ConnectionString(path, readOnly: true));
        connection.Open();
        var start = ToMinutes(MinuteAggregator.MinuteOf(from));
        var end = ToMinutes(MinuteAggregator.MinuteOf(to));

        var sessions = new List<(long Start, long End)>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT start_ts, end_ts FROM session WHERE end_ts >= $a AND start_ts <= $b ORDER BY start_ts";
            command.Parameters.AddWithValue("$a", start);
            command.Parameters.AddWithValue("$b", end);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                sessions.Add((reader.GetInt64(0), reader.GetInt64(1)));
        }

        var rows = new SortedList<long, (double Min, double Avg, double Max)?>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT m.ts, m.min, m.avg, m.max FROM minute m JOIN sensor s ON s.id = m.sensor_id
                WHERE s.key = $k AND m.ts BETWEEN $a AND $b ORDER BY m.ts
                """;
            command.Parameters.AddWithValue("$k", sensorKey);
            command.Parameters.AddWithValue("$a", start - KeyframeMinutes);
            command.Parameters.AddWithValue("$b", end);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows[reader.GetInt64(0)] = reader.IsDBNull(2)
                    ? null
                    : (reader.GetInt64(1) / Scale, reader.GetInt64(2) / Scale, reader.GetInt64(3) / Scale);
            }
        }

        var points = new List<HistoryPoint>();
        foreach (var (sessionStart, sessionEnd) in sessions)
        {
            (double Min, double Avg, double Max)? carried = null;
            var carriedAt = long.MinValue;
            // The value in force when the requested window starts: the last row before it, from this session
            // only (a new session writes every sensor in its first minute, so nothing carries across sessions).
            var first = Math.Max(start, sessionStart);
            foreach (var (ts, value) in rows)
            {
                if (ts >= first) break;
                if (ts >= sessionStart) { carried = value; carriedAt = ts; }
            }

            for (var ts = first; ts <= Math.Min(end, sessionEnd); ts++)
            {
                if (rows.TryGetValue(ts, out var value))
                {
                    carried = value;
                    carriedAt = ts;
                }
                if (carried is { } v && ts - carriedAt < KeyframeMinutes)
                    points.Add(new HistoryPoint(FromMinutes(ts), v.Min, v.Avg, v.Max));
            }
        }
        return points;
    }

    /// <summary>Every sensor that has history: key, type, name and hardware.</summary>
    public static IReadOnlyList<SensorDescriptor> ReadSensors(string path)
    {
        if (!File.Exists(path))
            return [];
        using var connection = new SqliteConnection(ConnectionString(path, readOnly: true));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT key, type, name, hardware FROM sensor ORDER BY hardware, name";
        using var reader = command.ExecuteReader();
        var result = new List<SensorDescriptor>();
        while (reader.Read())
            result.Add(new SensorDescriptor(reader.GetString(0), (LibreHardwareMonitor.Hardware.SensorType)reader.GetInt32(1),
                reader.GetString(2), reader.GetString(3)));
        return result;
    }

    // ---- Helpers -------------------------------------------------------------------------------

    private static string ConnectionString(string path, bool readOnly) => new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
        Pooling = !readOnly,
    }.ToString();

    private static long ToStored(double value) => (long)Math.Round(value * Scale);

    private static long ToMinutes(DateTime utcMinute) => utcMinute.Ticks / TimeSpan.TicksPerMinute;

    private static DateTime FromMinutes(long minutes) =>
        new DateTime(minutes * TimeSpan.TicksPerMinute, DateTimeKind.Utc).ToLocalTime();

    private static int Execute(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _connection.Dispose();
        SqliteConnection.ClearPool(_connection);
    }
}
