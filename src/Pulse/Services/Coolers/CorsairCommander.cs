using System.IO;
using LibreHardwareMonitor.Hardware;
using Pulse.Models;

namespace Pulse.Services.Coolers;

/// <summary>
/// The Corsair Commander family (Commander Core, Core XT and ST, the controllers of iCUE Elite coolers):
/// pump and fan speeds, the coolant temperature and temperature probes. Read-only.
///
/// The protocol, as documented by liquidctl (driver/commander_core.py; this is an independent implementation):
/// <list type="bullet">
/// <item>Every report starts with <c>08</c>, then the command. Replies start with <c>00</c>, then an echo of the command's first byte.</item>
/// <item>The device must be woken (<c>01 03 00 02</c>, "software mode") before a read, and put back to sleep
/// (<c>01 03 00 01</c>) after it, which returns it to its own fan and pump curves. Pulse wakes it only for the
/// few milliseconds of each read, always puts it back to sleep, and also sends a sleep when it first opens the
/// device, in case an earlier run was cut off mid-read.</item>
/// <item>A value is read by opening an endpoint (<c>0d 00 &lt;mode&gt;</c>), reading up to three chunks (<c>08 00 01/02/03</c>)
/// and closing it (<c>05 01 00</c>). The first chunk's bytes 3–4 name the data type; the payload starts at byte 5
/// (and at byte 3 of the other chunks). On the ST everything fits in the first chunk and the others repeat it.</item>
/// </list>
/// </summary>
public sealed class CorsairCommander : ICoolerDriver
{
    private static readonly byte[] Wake = [0x01, 0x03, 0x00, 0x02];
    private static readonly byte[] Sleep = [0x01, 0x03, 0x00, 0x01];
    private static readonly byte[] Firmware = [0x02, 0x13];
    private static readonly byte[] OpenEndpoint = [0x0D, 0x00];
    private static readonly byte[] CloseEndpoint = [0x05, 0x01, 0x00];
    private static readonly byte[][] ReadChunks = [[0x08, 0x00, 0x01], [0x08, 0x00, 0x02], [0x08, 0x00, 0x03]];

    public const byte SpeedsMode = 0x17;
    public const byte TemperaturesMode = 0x21;
    public const byte ConnectedFansMode = 0x1A;
    private static readonly (byte, byte) SpeedsType = (0x06, 0x00);
    private static readonly (byte, byte) TemperaturesType = (0x10, 0x00);
    private static readonly (byte, byte) ConnectedFansType = (0x09, 0x00);

    /// <summary>Which fans are plugged in rarely changes; check again this often.</summary>
    private static readonly TimeSpan ConnectedRefresh = TimeSpan.FromMinutes(1);

    private readonly ICoolerLink _link;
    private readonly bool _hasPump;
    private readonly Dictionary<string, (float Min, float Max)> _ranges = new();
    private bool[]? _connected;
    private DateTime _connectedAt;
    private bool _handedBack;
    private readonly HashSet<string> _quirksLogged = [];

    public CorsairCommander(ICoolerLink link, string name, int productId, string serial, bool hasPump)
    {
        _link = link;
        _hasPump = hasPump;
        Name = name;
        Id = IdFor(name, productId, serial);
    }

    /// <summary>"/corsair/commander-st/2052337E534B", or the product ID when the device has no serial number.</summary>
    public static string IdFor(string name, int productId, string serial) =>
        $"/corsair/{Slug(name)}/{(serial.Length > 0 ? serial : productId.ToString("x4"))}";

    public string Id { get; }
    public string Name { get; }

    /// <summary>Firmware version, read on the first successful read ("2.0.19").</summary>
    public string? FirmwareVersion { get; private set; }

    private static string Slug(string name) =>
        name.Replace("Corsair ", "", StringComparison.Ordinal).Replace(' ', '-').ToLowerInvariant();

    public HardwareReading Read()
    {
        if (!_handedBack)
        {
            // If a previous run was killed between wake and sleep, the controller is still waiting for software.
            Send(Sleep);
            _handedBack = true;
        }

        Send(Wake);
        HardwareReading reading;
        try
        {
            if (FirmwareVersion is null)
                FirmwareVersion = ParseFirmware(Send(Firmware));
            if (_connected is null || DateTime.UtcNow - _connectedAt > ConnectedRefresh)
            {
                _connected = ParseConnectedFans(ReadData(ConnectedFansMode, ConnectedFansType, 1));
                _connectedAt = DateTime.UtcNow;
            }
            var speeds = ParseSpeeds(ReadData(SpeedsMode, SpeedsType, 2));
            var temperatures = ParseTemperatures(ReadData(TemperaturesMode, TemperaturesType, 3));
            reading = Build(speeds, temperatures, _connected);
        }
        catch
        {
            // Back to the cooler's own curves, whatever happened; the original error is the one that matters.
            TrySleep();
            throw;
        }
        Send(Sleep);
        return reading;
    }

    /// <summary>Sends sleep after a failure. It's written before its reply is awaited, so the cooler gets it even if that reply never comes.</summary>
    private void TrySleep()
    {
        try
        {
            Send(Sleep);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or CoolerProtocolException or ObjectDisposedException)
        {
            Log.Write($"{Name}: no reply to sleep after a failed read ({ex.Message})");
        }
    }

    // ---- Transport ------------------------------------------------------------------------------

    /// <summary>
    /// Sends a command and returns its reply: the first report that starts with 00 and echoes the command.
    /// Anything else is skipped: a late reply to an earlier command that timed out, or a report the device sent
    /// on its own.
    /// </summary>
    private byte[] Send(byte[] command, ReadOnlySpan<byte> data = default)
    {
        _link.Write(Packet(_link.ReportLength, command, data));
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var reply = _link.Read();
            if (reply.Length >= 2 && reply[0] == 0x00 && reply[1] == command[0])
                return reply;
        }
        throw new CoolerProtocolException($"{Name}: no reply to {Hex(command)}");
    }

    /// <param name="bytesEach">Bytes per item after the leading count, to tell whether the first chunk holds it all.</param>
    private byte[] ReadData(byte mode, (byte, byte) expectedType, int bytesEach)
    {
        Send(OpenEndpoint, [mode]);
        try
        {
            var first = Send(ReadChunks[0]);
            if (first.Length < 6)
                throw new CoolerProtocolException($"{Name}: short reply for mode {mode:X2}");
            if ((first[3], first[4]) != expectedType)
            {
                // liquidctl's notes on the Commander ST say its type bytes can differ from the others'. The opened
                // endpoint says what the data is; the parsers' own checks catch anything that doesn't fit.
                Quirk($"mode {mode:X2} answered with data type {first[3]:X2} {first[4]:X2}, expected " +
                      $"{expectedType.Item1:X2} {expectedType.Item2:X2}: {Hex(first)}");
            }

            // Everything fits in the first chunk (it always has on the ST: 7 speeds are 15 bytes): skip the other
            // two, which halves the time the cooler spends waiting on software. Otherwise read on, as liquidctl does.
            var payload = first.AsSpan(5);
            if (1 + payload[0] * bytesEach <= payload.Length)
                return payload.ToArray();
            var more = Send(ReadChunks[1]);
            var final = Send(ReadChunks[2]);
            return [.. payload, .. more.AsSpan(3), .. final.AsSpan(3)];
        }
        finally
        {
            Send(CloseEndpoint);
        }
    }

    private void Quirk(string message)
    {
        if (_quirksLogged.Add(message[..Math.Min(40, message.Length)]))
            Log.Write($"{Name}: {message}");
    }

    // ---- Packets and replies (pure, tested against liquidctl's documented layouts) ---------------

    /// <summary>A report: 08, the command, its data, zero-padded to the report length.</summary>
    public static byte[] Packet(int reportLength, ReadOnlySpan<byte> command, ReadOnlySpan<byte> data = default)
    {
        var packet = new byte[reportLength];
        packet[0] = 0x08;
        command.CopyTo(packet.AsSpan(1));
        data.CopyTo(packet.AsSpan(1 + command.Length));
        return packet;
    }

    /// <summary>"major.minor.patch" from bytes 3–5 of the firmware reply.</summary>
    public static string ParseFirmware(byte[] reply) =>
        reply.Length >= 6 ? $"{reply[3]}.{reply[4]}.{reply[5]}" : throw new CoolerProtocolException("short firmware reply");

    /// <summary>Speeds in rpm: a count, then a little-endian u16 each.</summary>
    public static int[] ParseSpeeds(ReadOnlySpan<byte> payload)
    {
        var count = Count(payload, 2);
        var speeds = new int[count];
        for (var i = 0; i < count; i++)
            speeds[i] = payload[1 + i * 2] | payload[2 + i * 2] << 8;
        return speeds;
    }

    /// <summary>Temperatures in °C (null when the probe isn't connected): a count, then 3 bytes each: a status byte (00 = connected) and tenths of a degree.</summary>
    public static float?[] ParseTemperatures(ReadOnlySpan<byte> payload)
    {
        var count = Count(payload, 3);
        var temperatures = new float?[count];
        for (var i = 0; i < count; i++)
        {
            var at = 1 + i * 3;
            temperatures[i] = payload[at] == 0x00 ? (payload[at + 1] | payload[at + 2] << 8) / 10f : null;
        }
        return temperatures;
    }

    /// <summary>Which speed channels have something plugged in: a count, then 07 for connected.</summary>
    public static bool[] ParseConnectedFans(ReadOnlySpan<byte> payload)
    {
        var count = Count(payload, 1);
        var connected = new bool[count];
        for (var i = 0; i < count; i++)
            connected[i] = payload[1 + i] == 0x07;
        return connected;
    }

    /// <summary>The leading count, checked against what the payload can hold, so a reply in an unexpected format is rejected rather than misread.</summary>
    private static int Count(ReadOnlySpan<byte> payload, int bytesEach)
    {
        if (payload.Length < 1)
            throw new CoolerProtocolException("empty payload");
        var count = payload[0];
        if (count > 16 || 1 + count * bytesEach > payload.Length)
            throw new CoolerProtocolException($"implausible count {count} for {payload.Length} bytes");
        return count;
    }

    // ---- Into Pulse's model ----------------------------------------------------------------------

    /// <summary>
    /// Channel 0 is the pump on the Commander Core and ST (the XT has no pump), the rest are fans 1–6.
    /// Temperature 0 is the coolant on models with a pump; the others are probes.
    /// </summary>
    public HardwareReading Build(int[] speeds, float?[] temperatures, bool[]? connected)
    {
        var sensors = new List<SensorReading>();
        for (var i = 0; i < speeds.Length; i++)
        {
            var isPump = _hasPump && i == 0;
            // Unplugged fan headers read 0 forever; leave them out (the pump always shows: a stopped pump matters).
            if (!isPump && connected is not null && i < connected.Length && !connected[i])
                continue;
            var name = isPump ? "Pump" : $"Fan #{(_hasPump ? i : i + 1)}";
            sensors.Add(Sensor($"{Id}/fan/{i}", name, SensorType.Fan, i, speeds[i]));
        }
        for (var i = 0; i < temperatures.Length; i++)
        {
            if (temperatures[i] is not { } t)
                continue;
            var name = _hasPump && i == 0 ? "Coolant" : $"Temperature #{(_hasPump ? i : i + 1)}";
            sensors.Add(Sensor($"{Id}/temperature/{i}", name, SensorType.Temperature, i, t));
        }
        return new HardwareReading(Id, Name, HardwareType.Cooler, sensors, []);
    }

    private SensorReading Sensor(string id, string name, SensorType type, int index, float value)
    {
        var range = _ranges.TryGetValue(id, out var r) ? (Math.Min(r.Min, value), Math.Max(r.Max, value)) : (value, value);
        _ranges[id] = range;
        return new SensorReading(id, name, type, index, value, range.Item1, range.Item2);
    }

    private static string Hex(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(bytes[..Math.Min(bytes.Length, 24)]);

    public void Dispose() => _link.Dispose();
}

public sealed class CoolerProtocolException(string message) : Exception(message);
