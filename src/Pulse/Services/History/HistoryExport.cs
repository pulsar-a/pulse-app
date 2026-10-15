using System.Globalization;
using System.IO;
using System.Text;
using LibreHardwareMonitor.Hardware;

namespace Pulse.Services.History;

/// <summary>
/// Writes history as CSV for spreadsheets: one row per minute, one column per sensor (the minute's
/// average). Headers use the names shown in Pulse; temperatures follow the user's unit.
/// </summary>
public static class HistoryExport
{
    public static async Task<int> WriteCsvAsync(
        string databasePath, string outputPath, DateTime from, DateTime to,
        Func<SensorDescriptor, string> displayName, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var sensors = HistoryDatabase.ReadSensors(databasePath);
            var rows = new SortedDictionary<DateTime, double?[]>();
            for (var column = 0; column < sensors.Count; column++)
            {
                ct.ThrowIfCancellationRequested();
                foreach (var point in HistoryDatabase.ReadMinutes(databasePath, sensors[column].Id, from, to))
                {
                    if (!rows.TryGetValue(point.Time, out var row))
                        rows[point.Time] = row = new double?[sensors.Count];
                    row[column] = ToDisplay(sensors[column].Type, point.Avg);
                }
            }

            // Only sensors with data in the range get a column.
            var used = Enumerable.Range(0, sensors.Count).Where(c => rows.Values.Any(r => r[c] is not null)).ToList();

            using var writer = new StreamWriter(outputPath, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            writer.Write("Time");
            foreach (var c in used)
                writer.Write("," + Quote(Header(sensors[c], displayName(sensors[c]))));
            writer.WriteLine();

            foreach (var (time, row) in rows)
            {
                writer.Write(time.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
                foreach (var c in used)
                    writer.Write("," + (row[c] is { } v ? v.ToString("0.##", CultureInfo.InvariantCulture) : ""));
                writer.WriteLine();
            }
            return rows.Count;
        }, ct);
    }

    public static string Header(SensorDescriptor sensor, string name)
    {
        var unit = Unit(sensor.Type);
        return unit.Length > 0 ? $"{sensor.Hardware} / {name} ({unit})" : $"{sensor.Hardware} / {name}";
    }

    private static double ToDisplay(SensorType type, double value) =>
        type == SensorType.Temperature ? SensorFormat.ToDisplayTemperature(value) : value;

    private static string Unit(SensorType type) => type switch
    {
        SensorType.Temperature => SensorFormat.TemperatureSymbol,
        SensorType.Load or SensorType.Control or SensorType.Level or SensorType.Humidity => "%",
        SensorType.Power => "W",
        SensorType.Voltage => "V",
        SensorType.Current => "A",
        SensorType.Clock => "MHz",
        SensorType.Fan => "RPM",
        SensorType.Flow => "L/h",
        SensorType.Data => "GB",
        SensorType.SmallData => "MB",
        SensorType.Throughput => "B/s",
        SensorType.TimeSpan => "s",
        SensorType.Frequency => "Hz",
        _ => "",
    };

    private static string Quote(string field) =>
        field.IndexOfAny([',', '"', '\n']) >= 0 ? $"\"{field.Replace("\"", "\"\"")}\"" : field;
}
