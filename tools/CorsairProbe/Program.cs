using System.Diagnostics;
using System.IO;
using Pulse.Services.Coolers;

// Checks a USB cooler against Pulse's driver, outside Pulse.
//
//   CorsairProbe                 lists supported coolers and any cooler software running. Sends nothing.
//   CorsairProbe --read          one read: wake, firmware, fans, speeds, temperatures, sleep, every exchange in hex.
//   CorsairProbe --read --force  the same even with iCUE (or similar) running. It may briefly lose the cooler.
//   --out <file>                 also writes the exchanges to a file (for test fixtures).
//
// Close Pulse first: two readers on one controller is exactly the conflict this is about.

var read = args.Contains("--read");
var force = args.Contains("--force");
var outIndex = Array.IndexOf(args, "--out");
var outPath = outIndex >= 0 && outIndex + 1 < args.Length ? args[outIndex + 1] : null;

var found = HidCoolerLink.Scan();
if (found.Count == 0)
{
    Console.WriteLine("No supported USB cooler found. Supported:");
    foreach (var known in KnownCoolers.All)
        Console.WriteLine($"  {known.Name}  {known.VendorId:X4}:{known.ProductId:X4}");
    return 1;
}

var processes = Process.GetProcesses().Select(p => p.ProcessName).Distinct().ToList();
var blocked = false;
foreach (var device in found)
{
    var conflicts = KnownCoolers.ConflictsAmong(device.Cooler, processes);
    var pulse = processes.Any(p => p.Equals("Pulse", StringComparison.OrdinalIgnoreCase));
    Console.WriteLine($"{device.Cooler.Name}  {device.Cooler.VendorId:X4}:{device.Cooler.ProductId:X4}  serial '{device.Serial}'");
    Console.WriteLine($"  path: {device.Device.DevicePath}");
    Console.WriteLine($"  reports: {device.Device.GetMaxOutputReportLength()} out, {device.Device.GetMaxInputReportLength()} in (with report ID)");
    Console.WriteLine(conflicts.Count == 0 ? "  no cooler software running" : $"  running: {string.Join(", ", conflicts)}");
    if (pulse)
        Console.WriteLine("  Pulse is running: close it first, or the two will share the controller");
    blocked |= conflicts.Count > 0 || pulse;
}

if (!read)
{
    Console.WriteLine();
    Console.WriteLine("Nothing was sent. Add --read for one read.");
    return 0;
}
if (blocked && !force)
{
    Console.WriteLine();
    Console.WriteLine("Not reading: other software is using the cooler. Close it (iCUE: also stop its background service, " +
                      "Corsair.Service), or add --force to read anyway.");
    return 2;
}

var log = new List<string>();
foreach (var device in found)
{
    Console.WriteLine();
    Console.WriteLine($"Reading {device.Cooler.Name}...");
    var link = HidCoolerLink.TryOpen(device.Device);
    if (link is null)
    {
        Console.WriteLine("  Windows wouldn't open it.");
        continue;
    }

    using var driver = device.Cooler.Create(new RecordingLink(link, log), device.Serial);
    try
    {
        var sw = Stopwatch.StartNew();
        var reading = driver.Read();
        Console.WriteLine($"  done in {sw.ElapsedMilliseconds} ms, firmware {(driver as CorsairCommander)?.FirmwareVersion}");
        foreach (var sensor in reading.Sensors)
            Console.WriteLine($"  {sensor.Name,-16} {sensor.Value,8:0.#} {(sensor.Type == LibreHardwareMonitor.Hardware.SensorType.Fan ? "rpm" : "°C")}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  failed: {ex.GetType().Name}: {ex.Message}");
        Console.WriteLine("  The driver always sends sleep last, so the cooler is back on its own curves.");
    }
}

Console.WriteLine();
foreach (var line in log)
    Console.WriteLine(line);
if (outPath is not null)
{
    File.WriteAllLines(outPath, log);
    Console.WriteLine($"Exchanges written to {outPath}");
}
return 0;

/// <summary>Passes everything through and keeps a hex line per report, both ways.</summary>
internal sealed class RecordingLink(ICoolerLink inner, List<string> log) : ICoolerLink
{
    public int ReportLength => inner.ReportLength;

    public void Write(ReadOnlySpan<byte> report)
    {
        log.Add("> " + Trimmed(report));
        inner.Write(report);
    }

    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public byte[] Read()
    {
        var asked = _clock.Elapsed;
        var reply = inner.Read();
        log.Add($"< {Trimmed(reply)}  ({(_clock.Elapsed - asked).TotalMilliseconds:0} ms)");
        return reply;
    }

    /// <summary>Hex without the trailing zero padding.</summary>
    private static string Trimmed(ReadOnlySpan<byte> bytes)
    {
        var end = bytes.Length;
        while (end > 0 && bytes[end - 1] == 0) end--;
        return Convert.ToHexString(bytes[..Math.Max(end, Math.Min(bytes.Length, 2))]);
    }

    public void Dispose() => inner.Dispose();
}
