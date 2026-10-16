using System.IO;
using HidSharp;

namespace Pulse.Services.Coolers;

/// <summary>
/// A cooler's HID interface through HidSharp (already shipped with LibreHardwareMonitor). Report sizes come
/// from the device itself rather than being assumed, since they differ between models.
/// </summary>
public sealed class HidCoolerLink : ICoolerLink
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(500);

    private readonly HidStream _stream;
    private readonly byte[] _input;

    private HidCoolerLink(HidStream stream, int outputLength, int inputLength)
    {
        _stream = stream;
        _stream.ReadTimeout = _stream.WriteTimeout = (int)Timeout.TotalMilliseconds;
        // HidSharp's lengths include the report ID byte.
        ReportLength = outputLength - 1;
        _input = new byte[inputLength];
    }

    public int ReportLength { get; }

    /// <summary>A cooler's HID device on the right USB interface, as found on this machine.</summary>
    public sealed record Found(KnownCooler Cooler, HidDevice Device, string Serial)
    {
        /// <summary>Stable across scans: the device path names the USB port and interface.</summary>
        public string Key => Device.DevicePath;
    }

    /// <summary>Every known cooler that's plugged in. Doesn't open or talk to anything.</summary>
    public static List<Found> Scan()
    {
        var found = new List<Found>();
        foreach (var device in DeviceList.Local.GetHidDevices())
        {
            if (KnownCoolers.Find(device.VendorID, device.ProductID) is not { } cooler
                || !device.DevicePath.Contains($"mi_{cooler.Interface:00}", StringComparison.OrdinalIgnoreCase))
                continue;
            found.Add(new Found(cooler, device, Serial(device)));
        }
        return found;
    }

    private static string Serial(HidDevice device)
    {
        try
        {
            return device.GetSerialNumber() ?? "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return "";
        }
    }

    /// <summary>Opens the device, shared: another app holding it open doesn't stop this, nor this it.</summary>
    public static HidCoolerLink? TryOpen(HidDevice device)
    {
        int output, input;
        try
        {
            output = device.GetMaxOutputReportLength();
            input = device.GetMaxInputReportLength();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
        if (output < 2 || input < 2 || !device.TryOpen(out var stream))
            return null;
        return new HidCoolerLink(stream, output, input);
    }

    public void Write(ReadOnlySpan<byte> report)
    {
        var buffer = new byte[ReportLength + 1];   // report ID 0, then the report
        report[..Math.Min(report.Length, ReportLength)].CopyTo(buffer.AsSpan(1));
        _stream.Write(buffer);
    }

    public byte[] Read()
    {
        var count = _stream.Read(_input, 0, _input.Length);
        return count <= 1 ? [] : _input.AsSpan(1, count - 1).ToArray();
    }

    public void Dispose() => _stream.Dispose();
}
