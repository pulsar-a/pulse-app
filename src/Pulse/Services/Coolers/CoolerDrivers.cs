using Pulse.Models;

namespace Pulse.Services.Coolers;

/// <summary>
/// One HID connection to a cooler: send a report, read the reply. Reports are given without the leading
/// report ID byte; the link adds and strips it. Behind an interface so the drivers can be tested against
/// recorded replies, without the hardware.
/// </summary>
public interface ICoolerLink : IDisposable
{
    /// <summary>Bytes per report, without the report ID (96 for Corsair's Commander family).</summary>
    int ReportLength { get; }

    void Write(ReadOnlySpan<byte> report);

    /// <summary>The next report from the device; throws <see cref="TimeoutException"/> if none comes.</summary>
    byte[] Read();
}

/// <summary>
/// Reads one cooler controller that LibreHardwareMonitor doesn't support. Read-only by design: drivers
/// never change speeds, curves or lighting.
/// </summary>
public interface ICoolerDriver : IDisposable
{
    /// <summary>Stable across runs, like an LHM identifier ("/corsair/commander-st/2052337E534B").</summary>
    string Id { get; }

    string Name { get; }

    /// <summary>Reads everything once. Throws on a device error; the caller retries later.</summary>
    HardwareReading Read();
}

/// <summary>Software that talks to coolers itself. While it runs, Pulse leaves the devices it knows alone (by default).</summary>
/// <param name="Name">As the user knows it ("iCUE").</param>
/// <param name="Processes">Process names without ".exe"; a trailing '*' matches any name that starts with it.</param>
public sealed record ConflictingApp(string Name, params string[] Processes)
{
    public bool Matches(string processName)
    {
        var name = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? processName[..^4] : processName;
        foreach (var pattern in Processes)
        {
            if (pattern.EndsWith('*')
                    ? name.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)
                    : name.Equals(pattern, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}

/// <summary>A cooler controller Pulse can read, matched by USB vendor and product ID.</summary>
/// <param name="Interface">The USB interface the protocol runs on (Corsair: 0).</param>
/// <param name="Conflicts">The vendor's own software, plus <see cref="KnownCoolers.SharedConflicts"/>.</param>
/// <param name="Create">Makes the driver for an open link and the device's serial number.</param>
/// <param name="IdFor">The driver's <see cref="ICoolerDriver.Id"/> for a serial number, known without opening the device.</param>
public sealed record KnownCooler(
    int VendorId,
    int ProductId,
    string Name,
    int Interface,
    IReadOnlyList<ConflictingApp> Conflicts,
    Func<ICoolerLink, string, ICoolerDriver> Create,
    Func<string, string> IdFor);

/// <summary>
/// Every cooler controller Pulse reads itself. To support another device, add its driver and a line here.
/// Devices LibreHardwareMonitor already reads (NZXT Kraken, Aqua Computer, Arctic and others) don't belong
/// here: they come through the normal sensor polling.
/// </summary>
public static class KnownCoolers
{
    public const int Corsair = 0x1B1C;

    /// <summary>Corsair's iCUE: the app, its background service (which runs even with the app closed) and its plugin host.</summary>
    public static readonly ConflictingApp Icue = new("iCUE", "iCUE*", "Corsair.Service*", "CorsairService*");

    /// <summary>Multi-vendor tools that also talk to cooler controllers over USB.</summary>
    public static readonly IReadOnlyList<ConflictingApp> SharedConflicts =
    [
        new("SignalRGB", "SignalRgb*"),
        new("OpenRGB", "OpenRGB"),
        new("FanControl", "FanControl"),
        new("HWiNFO", "HWiNFO64", "HWiNFO32", "HWiNFO"),
        new("AIDA64", "aida64"),
        new("Argus Monitor", "ArgusMonitor"),
        new("liquidctl", "liquidctl"),
        new("CoolerControl", "coolercontrold", "CoolerControl*"),
    ];

    private static readonly IReadOnlyList<ConflictingApp> CorsairConflicts = [Icue, .. SharedConflicts];

    public static readonly IReadOnlyList<KnownCooler> All =
    [
        Commander(0x0C1C, "Corsair Commander Core", hasPump: true),
        Commander(0x0C2A, "Corsair Commander Core XT", hasPump: false),
        Commander(0x0C32, "Corsair Commander ST", hasPump: true),
    ];

    private static KnownCooler Commander(int productId, string name, bool hasPump) =>
        new(Corsair, productId, name, 0, CorsairConflicts,
            (link, serial) => new CorsairCommander(link, name, productId, serial, hasPump),
            serial => CorsairCommander.IdFor(name, productId, serial));

    public static KnownCooler? Find(int vendorId, int productId) =>
        All.FirstOrDefault(c => c.VendorId == vendorId && c.ProductId == productId);

    /// <summary>The apps among <paramref name="processNames"/> that would conflict with this cooler, by name, each once.</summary>
    public static IReadOnlyList<string> ConflictsAmong(KnownCooler cooler, IEnumerable<string> processNames)
    {
        var found = new List<string>();
        foreach (var process in processNames)
        {
            foreach (var app in cooler.Conflicts)
            {
                if (!found.Contains(app.Name) && app.Matches(process))
                    found.Add(app.Name);
            }
        }
        return found;
    }
}
