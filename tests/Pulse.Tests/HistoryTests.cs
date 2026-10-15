using System.IO;
using LibreHardwareMonitor.Hardware;
using Microsoft.Data.Sqlite;
using Pulse.Models;
using Pulse.Services;
using Pulse.Services.History;
using static Pulse.Tests.Snap;

namespace Pulse.Tests;

public class HistoryTests : PulseTest
{
    private static readonly DateTime T0 = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Local);
    private static readonly SensorDescriptor Cpu = new("/cpu/temp", SensorType.Temperature, "CPU Package", "Intel Core i9-14900K");
    private static readonly SensorDescriptor Fan = new("/fan/0", SensorType.Fan, "Fan #1", "Nuvoton NCT6798D");

    private string DbPath => Path.Combine(TempFolder, "history.db");

    private static HardwareSnapshot Reading(DateTime at, float? cpu, float? fan = 1200) =>
        Of(Hardware("/cpu", "Intel Core i9-14900K", HardwareType.Cpu,
               Sensor("/cpu/temp", "CPU Package", SensorType.Temperature, cpu)),
           Hardware("/lpc", "Nuvoton NCT6798D", HardwareType.SuperIO,
               fan is null ? [] : [Sensor("/fan/0", "Fan #1", SensorType.Fan, fan)]))
        with { Timestamp = at };

    private static MinuteBatch Minute(DateTime minuteLocal, params (SensorDescriptor Sensor, double Value)[] values) =>
        new(MinuteAggregator.MinuteOf(minuteLocal), values.Select(v => new MinuteSample(v.Sensor, v.Value, v.Value, v.Value, 60)).ToList());

    private long RowCount()
    {
        using var connection = new SqliteConnection($"Data Source={DbPath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM minute";
        return (long)command.ExecuteScalar()!;
    }

    // ---- Aggregation ----------------------------------------------------------------------------

    [Fact]
    public void Aggregator_summarizes_each_minute_and_hands_it_over_when_the_next_begins()
    {
        var aggregator = new MinuteAggregator();
        Assert.Null(aggregator.Add(Reading(T0.AddSeconds(5), 60)));
        Assert.Null(aggregator.Add(Reading(T0.AddSeconds(30), 70)));
        Assert.Null(aggregator.Add(Reading(T0.AddSeconds(55), float.NaN)));

        var batch = aggregator.Add(Reading(T0.AddMinutes(1), 80));

        Assert.NotNull(batch);
        Assert.Equal(MinuteAggregator.MinuteOf(T0), batch.Minute);
        var cpu = batch.Samples.Single(s => s.Sensor.Id == "/cpu/temp");
        Assert.Equal((60.0, 65.0, 70.0, 2), (cpu.Min, cpu.Avg, cpu.Max, cpu.Count));
        Assert.Equal("Intel Core i9-14900K", cpu.Sensor.Hardware);

        var partial = aggregator.Flush();
        Assert.Equal(80, partial!.Samples.Single(s => s.Sensor.Id == "/cpu/temp").Avg);
    }

    // ---- Database -------------------------------------------------------------------------------

    [Fact]
    public void Unchanged_minutes_are_skipped_on_disk_but_read_back_in_full()
    {
        using (var db = HistoryDatabase.Open(DbPath))
        {
            for (var m = 0; m < 10; m++)
                db.Write(Minute(T0.AddMinutes(m), (Cpu, m < 5 ? 60 : 62.5), (Fan, 1200)));
        }

        // CPU changed once (2 rows), the fan never did (1 row).
        Assert.Equal(3, RowCount());

        var cpu = HistoryDatabase.ReadMinutes(DbPath, Cpu.Id, T0, T0.AddMinutes(9));
        Assert.Equal(10, cpu.Count);
        Assert.Equal(60, cpu[4].Avg);
        Assert.Equal(62.5, cpu[5].Avg);
        Assert.Equal(10, HistoryDatabase.ReadMinutes(DbPath, Fan.Id, T0, T0.AddMinutes(9)).Count);
    }

    [Fact]
    public void Unchanged_sensors_still_get_an_hourly_keyframe()
    {
        using (var db = HistoryDatabase.Open(DbPath))
        {
            for (var m = 0; m < 130; m++)
                db.Write(Minute(T0.AddMinutes(m), (Fan, 1200)));
        }
        Assert.Equal(3, RowCount());   // minutes 0, 60 and 120
        Assert.Equal(130, HistoryDatabase.ReadMinutes(DbPath, Fan.Id, T0, T0.AddMinutes(129)).Count);
    }

    [Fact]
    public void A_sensor_that_stops_reporting_leaves_a_gap_instead_of_its_last_value()
    {
        using (var db = HistoryDatabase.Open(DbPath))
        {
            db.Write(Minute(T0, (Cpu, 60), (Fan, 1200)));
            db.Write(Minute(T0.AddMinutes(1), (Cpu, 60), (Fan, 1200)));
            db.Write(Minute(T0.AddMinutes(2), (Cpu, 60)));
            db.Write(Minute(T0.AddMinutes(3), (Cpu, 60)));
        }

        var fan = HistoryDatabase.ReadMinutes(DbPath, Fan.Id, T0, T0.AddMinutes(3));
        Assert.Equal([T0, T0.AddMinutes(1)], fan.Select(p => p.Time));
    }

    [Fact]
    public void Nothing_is_carried_across_time_Pulse_was_not_running()
    {
        using (var db = HistoryDatabase.Open(DbPath))
            db.Write(Minute(T0, (Cpu, 60)));
        // Pulse restarts 10 minutes later: a new session, with the same reading.
        using (var db = HistoryDatabase.Open(DbPath))
            db.Write(Minute(T0.AddMinutes(10), (Cpu, 60)));

        var cpu = HistoryDatabase.ReadMinutes(DbPath, Cpu.Id, T0, T0.AddMinutes(10));
        Assert.Equal([T0, T0.AddMinutes(10)], cpu.Select(p => p.Time));
    }

    [Fact]
    public void Pruning_removes_old_minutes()
    {
        using (var db = HistoryDatabase.Open(DbPath))
        {
            db.Write(Minute(T0.AddDays(-10), (Cpu, 50)));
            db.Write(Minute(T0, (Cpu, 60)));
            Assert.Equal(1, db.Prune(T0.AddDays(-7)));
        }
        Assert.Single(HistoryDatabase.ReadMinutes(DbPath, Cpu.Id, T0.AddDays(-30), T0));
    }

    [Fact]
    public void A_corrupt_file_is_moved_aside_and_a_new_database_started()
    {
        File.WriteAllText(DbPath, "this is not a database, just text that happens to be long enough to look like one");

        using (var db = HistoryDatabase.Open(DbPath))
            db.Write(Minute(T0, (Cpu, 60)));

        Assert.Single(Directory.GetFiles(TempFolder, "history.corrupt-*.db"));
        Assert.Single(HistoryDatabase.ReadMinutes(DbPath, Cpu.Id, T0, T0));
    }

    // ---- Queries and export -----------------------------------------------------------------------

    [Fact]
    public void Queries_use_disk_for_older_data_and_the_live_stream_for_recent_data()
    {
        using (var db = HistoryDatabase.Open(DbPath))
        {
            for (var m = 0; m < 5; m++)
                db.Write(Minute(T0.AddMinutes(m), (Cpu, 50 + m)));
        }
        var stream = new SensorStream();
        for (var s = 0; s < 30; s++)
            stream.Publish(Reading(T0.AddMinutes(5).AddSeconds(s * 2), 70));

        var points = new HistoryStore(stream, DbPath).Query(Cpu.Id, T0, T0.AddMinutes(6), maxPoints: 1000);

        Assert.Equal(5 + 30, points.Count);
        Assert.Equal(50, points[0].Avg);
        Assert.Equal(70, points[^1].Avg);
    }

    [Fact]
    public void Downsampling_keeps_spikes_in_each_bucket()
    {
        var points = Enumerable.Range(0, 100)
            .Select(i => new HistoryPoint(T0.AddSeconds(i), 50, i == 42 ? 95 : 50, i == 42 ? 95 : 50))
            .ToList();

        var reduced = HistoryStore.Downsample(points, T0, T0.AddSeconds(100), maxPoints: 10);

        Assert.Equal(10, reduced.Count);
        Assert.Equal(95, reduced[4].Max);
        Assert.Equal(54.5, reduced[4].Avg, precision: 6);
        Assert.Equal(50, reduced[0].Max);
    }

    [Fact]
    public void Downsampled_buckets_stay_put_as_the_window_slides()
    {
        // A noisy hour at one reading a second, queried twice, three seconds apart, as the live chart does.
        var rng = new Random(7);
        var points = Enumerable.Range(0, 3605)
            .Select(i => rng.Next(100, 600))
            .Select((v, i) => new HistoryPoint(T0.AddSeconds(i), v, v, v))
            .ToList();

        var to1 = T0.AddSeconds(3601.4);
        var to2 = to1.AddSeconds(3);
        var first = HistoryStore.Downsample(points.Where(p => p.Time >= to1.AddHours(-1) && p.Time <= to1).ToList(), to1.AddHours(-1), to1, 600);
        var second = HistoryStore.Downsample(points.Where(p => p.Time >= to2.AddHours(-1) && p.Time <= to2).ToList(), to2.AddHours(-1), to2, 600);

        // Every full bucket the two queries share is identical; only the edges differ.
        var shared = first.Skip(1).SkipLast(1).Where(a => second.Any(b => b.Time == a.Time)).ToList();
        Assert.True(shared.Count > 590);
        Assert.All(shared, a => Assert.Equal(a, second.Single(b => b.Time == a.Time)));
        Assert.All(first, p => Assert.Equal(0, p.Time.Ticks % TimeSpan.FromSeconds(6).Ticks));
    }

    [Theory]
    [InlineData(300, 1)]          // 5 minutes
    [InlineData(3600, 6)]         // an hour
    [InlineData(86400, 300)]      // a day: 144 s would do, rounded up to 5 minutes
    [InlineData(604800, 1200)]    // a week: ~17 min, rounded up to 20 minutes
    public void Bucket_sizes_are_round(int spanSeconds, int bucketSeconds) =>
        Assert.Equal(TimeSpan.FromSeconds(bucketSeconds), HistoryStore.BucketSize(TimeSpan.FromSeconds(spanSeconds), 600));

    [Fact]
    public void Csv_export_has_a_column_per_sensor_with_units_in_the_users_choice()
    {
        SensorFormat.TemperatureUnit = TemperatureUnit.Fahrenheit;
        using (var db = HistoryDatabase.Open(DbPath))
        {
            db.Write(Minute(T0, (Cpu, 50), (Fan, 1200)));
            db.Write(Minute(T0.AddMinutes(1), (Cpu, 100), (Fan, 1250)));
        }
        var csv = Path.Combine(TempFolder, "out.csv");

        var rows = HistoryExport.WriteCsvAsync(DbPath, csv, T0, T0.AddMinutes(1), s => s.Name).Result;

        Assert.Equal(2, rows);
        var lines = File.ReadAllLines(csv);
        Assert.Equal("Time,Intel Core i9-14900K / CPU Package (°F),Nuvoton NCT6798D / Fan #1 (RPM)", lines[0]);
        Assert.Equal($"{T0:yyyy-MM-dd HH:mm},122,1200", lines[1]);
        Assert.Equal($"{T0.AddMinutes(1):yyyy-MM-dd HH:mm},212,1250", lines[2]);
    }

    // ---- Recorder ---------------------------------------------------------------------------------

    [Fact]
    public void The_recorder_writes_finished_minutes_and_the_partial_one_on_exit()
    {
        var stream = new SensorStream();
        var recorder = new HistoryRecorder(stream, NewSettings(), DbPath);
        for (var s = 0; s < 150; s += 10)
            stream.Publish(Reading(T0.AddSeconds(s), 60 + s / 60));
        recorder.Dispose();

        var cpu = HistoryDatabase.ReadMinutes(DbPath, Cpu.Id, T0, T0.AddMinutes(2));
        Assert.Equal([60.0, 61, 62], cpu.Select(p => p.Avg));
    }

    [Fact]
    public void Recording_can_be_turned_off()
    {
        var settings = NewSettings();
        settings.Update(s => s.History.RetentionDays = 0);
        var stream = new SensorStream();
        var recorder = new HistoryRecorder(stream, settings, DbPath);
        for (var s = 0; s < 150; s += 10)
            stream.Publish(Reading(T0.AddSeconds(s), 60));
        recorder.Dispose();

        Assert.Empty(HistoryDatabase.ReadMinutes(DbPath, Cpu.Id, T0, T0.AddMinutes(2)));
    }
}
