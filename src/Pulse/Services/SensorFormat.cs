using LibreHardwareMonitor.Hardware;

namespace Pulse.Services;

public static class SensorFormat
{
    public const string Empty = "—";

    public static string Format(SensorType type, float? value)
    {
        if (value is not { } v || float.IsNaN(v))
            return Empty;

        return type switch
        {
            SensorType.Voltage => $"{v:0.000} V",
            SensorType.Current => $"{v:0.00} A",
            SensorType.Power => $"{v:0.0} W",
            SensorType.Clock => Clock(v),
            SensorType.Temperature => $"{v:0.0} °C",
            SensorType.Load => $"{v:0.0} %",
            SensorType.Frequency => $"{v:0} Hz",
            SensorType.Fan => $"{v:N0} RPM",
            SensorType.Flow => $"{v:0.0} L/h",
            SensorType.Control => $"{v:0} %",
            SensorType.Level => $"{v:0.0} %",
            SensorType.Factor => $"{v:0.000}",
            SensorType.Data => $"{v:0.0} GB",
            SensorType.SmallData => $"{v:N0} MB",
            SensorType.Throughput => Rate(v),
            SensorType.TimeSpan => Duration(v),
            SensorType.Timing => $"{v:0.000} ns",
            SensorType.Energy => $"{v:N0} mWh",
            SensorType.Noise => $"{v:0} dBA",
            SensorType.Conductivity => $"{v:0.0} µS/cm",
            SensorType.Humidity => $"{v:0} %",
            _ => $"{v:0.##}",
        };
    }

    /// <summary>Clock in MHz, shown as GHz once it reaches 1000.</summary>
    public static string Clock(float mhz) => mhz >= 1000 ? $"{mhz / 1000:0.00} GHz" : $"{mhz:0} MHz";

    /// <summary>Bytes per second as a human-readable rate.</summary>
    public static string Rate(float bytesPerSecond)
    {
        string[] units = ["B/s", "KB/s", "MB/s", "GB/s"];
        double value = bytesPerSecond;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.0} {units[unit]}";
    }

    public static string Duration(float seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays}d {t.Hours}h";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
        return $"{t.Minutes}m {t.Seconds}s";
    }

    public static string TypeTitle(SensorType type) => type switch
    {
        SensorType.Temperature => "Temperatures",
        SensorType.Load => "Load",
        SensorType.Clock => "Clocks",
        SensorType.Power => "Power",
        SensorType.Voltage => "Voltages",
        SensorType.Current => "Currents",
        SensorType.Fan => "Fans",
        SensorType.Control => "Fan control",
        SensorType.Flow => "Flow",
        SensorType.Level => "Levels",
        SensorType.Data or SensorType.SmallData => "Data",
        SensorType.Throughput => "Throughput",
        SensorType.TimeSpan => "Time",
        SensorType.Timing => "Timings",
        SensorType.Energy => "Energy",
        SensorType.Frequency => "Frequencies",
        SensorType.Factor => "Factors",
        SensorType.Noise => "Noise",
        SensorType.Conductivity => "Conductivity",
        SensorType.Humidity => "Humidity",
        _ => type.ToString(),
    };

    /// <summary>Display order of sensor groups on the Sensors page.</summary>
    public static int TypeOrder(SensorType type) => type switch
    {
        SensorType.Temperature => 0,
        SensorType.Load => 1,
        SensorType.Clock => 2,
        SensorType.Power => 3,
        SensorType.Voltage => 4,
        SensorType.Current => 5,
        SensorType.Fan => 6,
        SensorType.Control => 7,
        SensorType.Flow => 8,
        SensorType.Level => 9,
        SensorType.Data or SensorType.SmallData => 10,
        SensorType.Throughput => 11,
        _ => 20,
    };
}
