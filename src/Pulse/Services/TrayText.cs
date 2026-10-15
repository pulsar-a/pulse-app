using LibreHardwareMonitor.Hardware;
using Pulse.Models;

namespace Pulse.Services;

/// <summary>One thing the tray icon can show: a sensor, or "automatic" (Id null).</summary>
public sealed record TrayChoice(string Label, string? SensorId);

/// <summary>What the tray icon draws: a short number and how to color it.</summary>
public sealed record TrayReading(string Text, SensorType Type, double Value, string Label);

/// <summary>Pure formatting for the tray icon, its tooltip and its menu. No UI types, so it's easy to test.</summary>
public static class TrayText
{
    /// <summary>
    /// The value a tray icon has room for: two or three characters.
    /// "72" for 72 °C (no degree sign), "38" for 38 %, "210" W, "2.5k" rpm, "5.7" GHz.
    /// </summary>
    public static string Compact(SensorType type, double value)
    {
        if (double.IsNaN(value))
            return "–";

        return type switch
        {
            SensorType.Temperature => $"{SensorFormat.ToDisplayTemperature(value):0}",
            SensorType.Clock => value >= 1000 ? $"{value / 1000:0.0}" : $"{value:0}",
            _ when value >= 9_950 => $"{value / 1000:0}k",
            _ when value >= 1_000 => $"{value / 1000:0.0}k",
            _ => $"{value:0}",
        };
    }

    /// <summary>
    /// The reading to show for <paramref name="sensorId"/>, or, for automatic, the CPU package temperature
    /// (falling back to CPU load when temperatures need the driver). Null if nothing is available.
    /// </summary>
    public static TrayReading? Resolve(HardwareSnapshot snapshot, string? sensorId, Func<SensorReading, string> displayName)
    {
        SensorReading? sensor = null;
        if (sensorId is not null)
            sensor = snapshot.Hardware.SelectMany(h => h.AllSensors).FirstOrDefault(s => s.Id == sensorId && s.HasValue);

        if (sensor is null)
        {
            var cpu = snapshot.OfType(HardwareType.Cpu).FirstOrDefault();
            sensor = cpu?.First(SensorType.Temperature, "CPU Package", "Core Max", "Core Average", "Core (Tctl/Tdie)")
                     ?? cpu?.First(SensorType.Load, "CPU Total");
        }

        return sensor is { Value: { } v }
            ? new TrayReading(Compact(sensor.Type, v), sensor.Type, v, displayName(sensor))
            : null;
    }

    /// <summary>
    /// The hover text: the headline readings, one per line. Windows cuts tray tooltips at 127 characters.
    /// </summary>
    public static string Tooltip(HardwareSnapshot snapshot, string? pumpSensorId)
    {
        var lines = new List<string> { "Pulse" };

        var cpu = snapshot.OfType(HardwareType.Cpu).FirstOrDefault();
        if (cpu is not null)
            lines.Add(Line("CPU", cpu.Value(SensorType.Temperature, "CPU Package", "Core Max"), cpu.Value(SensorType.Load, "CPU Total")));

        var gpu = snapshot.OfType(HardwareType.GpuNvidia, HardwareType.GpuAmd, HardwareType.GpuIntel).FirstOrDefault();
        if (gpu is not null)
            lines.Add(Line("GPU", gpu.Value(SensorType.Temperature, "GPU Core"), gpu.Value(SensorType.Load, "GPU Core")));

        if (pumpSensorId is { Length: > 0 }
            && snapshot.Hardware.SelectMany(h => h.AllSensors).FirstOrDefault(s => s.Id == pumpSensorId) is { Value: { } rpm })
            lines.Add($"Pump {rpm:N0} rpm");

        var power = snapshot.OfType(HardwareType.Psu).FirstOrDefault()?.Value(SensorType.Power, "Total watts", "Total Output");
        if (power is { } watts)
            lines.Add($"Power {watts:0} W");

        var text = string.Join("\n", lines);
        return text.Length <= 127 ? text : text[..127];

        static string Line(string name, float? temperature, float? load)
        {
            var parts = new List<string>();
            if (temperature is { } t)
                parts.Add(SensorFormat.WholeTemperature(t));
            if (load is { } l)
                parts.Add($"{l:0}%");
            return parts.Count > 0 ? $"{name} {string.Join(" · ", parts)}" : name;
        }
    }

    /// <summary>The tray choices available right now: automatic first, then the common readings this PC has.</summary>
    public static IReadOnlyList<TrayChoice> Choices(HardwareSnapshot? snapshot, string? pumpSensorId)
    {
        var choices = new List<TrayChoice> { new("Automatic (CPU temperature)", null) };
        if (snapshot is null)
            return choices;

        var cpu = snapshot.OfType(HardwareType.Cpu).FirstOrDefault();
        var gpu = snapshot.OfType(HardwareType.GpuNvidia, HardwareType.GpuAmd, HardwareType.GpuIntel).FirstOrDefault();
        var psu = snapshot.OfType(HardwareType.Psu).FirstOrDefault();

        Add("CPU load", cpu?.First(SensorType.Load, "CPU Total"));
        Add("CPU power", cpu?.First(SensorType.Power, "CPU Package", "Package"));
        Add("GPU temperature", gpu?.First(SensorType.Temperature, "GPU Core"));
        Add("GPU hot spot", gpu?.First(SensorType.Temperature, "GPU Hot Spot"));
        Add("GPU load", gpu?.First(SensorType.Load, "GPU Core"));
        Add("System power", psu?.First(SensorType.Power, "Total watts", "Total Output"));
        if (pumpSensorId is { Length: > 0 })
            Add("Pump speed", snapshot.Hardware.SelectMany(h => h.AllSensors).FirstOrDefault(s => s.Id == pumpSensorId));
        return choices;

        void Add(string label, SensorReading? sensor)
        {
            if (sensor is { HasValue: true })
                choices.Add(new TrayChoice(label, sensor.Id));
        }
    }
}
