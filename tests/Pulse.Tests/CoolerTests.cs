using System.IO;
using LibreHardwareMonitor.Hardware;
using Pulse.Models;
using Pulse.Services;
using Pulse.Services.Coolers;
using Pulse.ViewModels;
using static Pulse.Tests.Snap;

namespace Pulse.Tests;

/// <summary>
/// A simulated Commander controller that answers like the real one, in the layouts liquidctl documents:
/// replies start 00, the echoed command byte, 00; data replies then carry the type (2 bytes) and the payload.
/// It also records every command, to check what Pulse sends (wake, sleep) and in what order.
/// </summary>
internal sealed class FakeCommander(int reportLength = 96) : ICoolerLink
{
    public List<byte[]> Sent { get; } = [];
    public int[] Speeds { get; set; } = [2357, 918, 903, 0, 1104, 0, 0];
    public bool[] Connected { get; set; } = [true, true, true, false, true, false, false];
    public float?[] Temperatures { get; set; } = [31.4f, null];

    /// <summary>Overrides the data type in replies (the Commander ST's reported quirk).</summary>
    public (byte, byte)? TypeOverride { get; set; }

    /// <summary>Throws on the nth read, to check the cooler is still handed back.</summary>
    public int? FailOnRead { get; set; }

    public bool Disposed { get; private set; }

    private readonly Queue<byte[]> _replies = new();
    private byte _mode;
    private int _reads;

    public int ReportLength => reportLength;

    /// <summary>The commands sent, as their first bytes after 08 ("01 03 00 02" for wake...).</summary>
    public List<string> Commands => Sent.Select(p => Convert.ToHexString(p.AsSpan(1, p[1] switch
    {
        0x01 => 4, 0x02 => 2, 0x0D => 3, 0x08 => 3, 0x05 => 3, _ => 1,
    }))).ToList();

    public void Write(ReadOnlySpan<byte> report)
    {
        Assert.Equal(reportLength, report.Length);
        Assert.Equal(0x08, report[0]);
        var packet = report.ToArray();
        Sent.Add(packet);
        var command = packet[1];
        _replies.Enqueue(command switch
        {
            0x02 => Reply(command, [0x02, 0x00, 0x13]),           // firmware 2.0.19
            0x0D => Remember(packet[3], command),
            0x08 when packet[3] == 0x01 => Data(command),
            _ => Reply(command, []),
        });
    }

    private byte[] Remember(byte mode, byte command)
    {
        _mode = mode;
        return Reply(command, []);
    }

    private byte[] Data(byte command)
    {
        var (type, payload) = _mode switch
        {
            CorsairCommander.SpeedsMode => ((byte)0x06, Speeds.SelectMany(s => new[] { (byte)s, (byte)(s >> 8) }).Prepend((byte)Speeds.Length).ToArray()),
            CorsairCommander.TemperaturesMode => ((byte)0x10, Temperatures.SelectMany(t => t is { } v
                ? new byte[] { 0x00, (byte)(int)Math.Round(v * 10), (byte)((int)Math.Round(v * 10) >> 8) }
                : [0x01, 0x00, 0x00]).Prepend((byte)Temperatures.Length).ToArray()),
            CorsairCommander.ConnectedFansMode => ((byte)0x09, Connected.Select(c => c ? (byte)0x07 : (byte)0x01).Prepend((byte)Connected.Length).ToArray()),
            _ => ((byte)0xFF, Array.Empty<byte>()),
        };
        var typeBytes = TypeOverride ?? (type, (byte)0x00);
        return Reply(command, [typeBytes.Item1, typeBytes.Item2, .. payload]);
    }

    private byte[] Reply(byte command, byte[] body)
    {
        var reply = new byte[reportLength];
        reply[0] = 0x00;
        reply[1] = command;
        reply[2] = 0x00;
        body.CopyTo(reply, 3);
        return reply;
    }

    public byte[] Read()
    {
        if (++_reads == FailOnRead)
            throw new TimeoutException("simulated timeout");
        return _replies.Dequeue();
    }

    public void Dispose() => Disposed = true;
}

public class CoolerTests : PulseTest
{
    private const string Wake = "01030002";
    private const string Sleep = "01030001";

    private static CorsairCommander St(ICoolerLink link) => new(link, "Corsair Commander ST", 0x0C32, "2052337E534B", hasPump: true);

    // ---- The protocol ---------------------------------------------------------------------------

    [Fact]
    public void Packets_are_08_then_the_command_padded_to_the_report()
    {
        var packet = CorsairCommander.Packet(64, [0x0D, 0x00], [0x17]);

        Assert.Equal(64, packet.Length);
        Assert.Equal([0x08, 0x0D, 0x00, 0x17], packet[..4]);
        Assert.All(packet[4..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void Replies_parse_as_liquidctl_documents_them()
    {
        // From liquidctl's tests: speeds (2357, 918, 903, 501, 1104, 1824, 104), temperatures (12.3, 45.6) and (none, 45.6).
        Assert.Equal([2357, 918, 903, 501, 1104, 1824, 104],
            CorsairCommander.ParseSpeeds(Convert.FromHexString("07350996038703F50150042007680000")));
        Assert.Equal([12.3f, 45.6f], CorsairCommander.ParseTemperatures(Convert.FromHexString("02007B0000C801")));
        Assert.Equal([null, 45.6f], CorsairCommander.ParseTemperatures(Convert.FromHexString("02010000 00C801".Replace(" ", ""))));
        Assert.Equal([false, true, false, false, false, false, true],
            CorsairCommander.ParseConnectedFans(Convert.FromHexString("0701070101010107")));
        Assert.Equal("1.2.33", CorsairCommander.ParseFirmware(Convert.FromHexString("000200010221")));
    }

    [Fact]
    public void A_reply_in_an_unexpected_format_is_rejected_not_misread()
    {
        Assert.Throws<CoolerProtocolException>(() => CorsairCommander.ParseSpeeds(Convert.FromHexString("40FFFF")));      // count 64
        Assert.Throws<CoolerProtocolException>(() => CorsairCommander.ParseSpeeds(Convert.FromHexString("0535")));        // too short
        Assert.Throws<CoolerProtocolException>(() => CorsairCommander.ParseTemperatures([]));
    }

    [Theory]
    [InlineData(96)]   // Commander Core, per liquidctl
    [InlineData(64)]   // Commander ST, as this PC's controller reports
    public void A_read_wakes_the_cooler_reads_it_and_hands_it_back(int reportLength)
    {
        var link = new FakeCommander(reportLength);
        using var driver = St(link);

        var reading = driver.Read();

        // A sleep first (in case an earlier run was cut off), then wake ... sleep.
        Assert.Equal(Sleep, link.Commands[0]);
        Assert.Equal(Wake, link.Commands[1]);
        Assert.Equal(Sleep, link.Commands[^1]);
        Assert.Equal("2.0.19", driver.FirmwareVersion);

        Assert.Equal(HardwareType.Cooler, reading.Type);
        Assert.Equal("/corsair/commander-st/2052337E534B", reading.Id);
        var sensors = reading.Sensors.ToDictionary(s => s.Name, s => s.Value);
        Assert.Equal(2357, sensors["Pump"]);
        Assert.Equal(918, sensors["Fan #1"]);
        Assert.Equal(1104, sensors["Fan #4"]);
        Assert.False(sensors.ContainsKey("Fan #3"));   // nothing plugged in
        Assert.Equal(31.4f, sensors["Coolant"]!.Value, 3);
        Assert.False(sensors.ContainsKey("Temperature #1"));   // probe not connected
    }

    /// <summary>
    /// Replays a capture from the real controller: every report Pulse sends must match what was sent then,
    /// byte for byte, and gets the reply the device gave.
    /// </summary>
    private sealed class Replay(string fixture) : ICoolerLink
    {
        private readonly Queue<(bool Sent, byte[] Bytes)> _lines = new(File.ReadAllLines(
                Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture))
            .Where(l => l.StartsWith('>') || l.StartsWith('<'))
            .Select(l => (l[0] == '>', Convert.FromHexString(l[2..].Split(' ')[0]))));

        public int ReportLength => 64;
        public bool Finished => _lines.Count == 0;

        public void Write(ReadOnlySpan<byte> report)
        {
            var (sent, expected) = _lines.Dequeue();
            Assert.True(sent, "Pulse sent a report where the device spoke");
            Assert.Equal(Convert.ToHexString(expected), Convert.ToHexString(report.TrimEnd((byte)0)));
        }

        public byte[] Read()
        {
            var (sent, reply) = _lines.Dequeue();
            Assert.False(sent, "Pulse waited for a reply where it should have sent");
            var padded = new byte[ReportLength];
            reply.CopyTo(padded, 0);
            return padded;
        }

        public void Dispose() { }
    }

    [Fact]
    public void A_read_of_the_real_commander_st_goes_exactly_as_captured()
    {
        var replay = new Replay("commander-st.txt");
        using var driver = St(replay);

        var reading = driver.Read();

        Assert.True(replay.Finished);
        Assert.Equal("2.0.19", driver.FirmwareVersion);
        Assert.Equal(["Pump", "Fan #1", "Fan #2", "Fan #3", "Fan #4", "Fan #5", "Fan #6", "Coolant"], reading.Sensors.Select(s => s.Name));
        var sensors = reading.Sensors.ToDictionary(s => s.Name, s => s.Value!.Value);
        Assert.Equal(2583, sensors["Pump"]);       // 0x0A17
        Assert.Equal(2014, sensors["Fan #1"]);     // 0x07DE
        Assert.Equal(1385, sensors["Fan #5"]);     // 0x0569
        Assert.Equal(1377, sensors["Fan #6"]);     // 0x0561
        Assert.Equal(41.6f, sensors["Coolant"], 3); // 0x01A0 tenths
    }

    [Fact]
    public void Later_reads_skip_the_extra_sleep_and_the_firmware()
    {
        var link = new FakeCommander();
        using var driver = St(link);
        driver.Read();
        link.Sent.Clear();

        driver.Read();

        Assert.Equal(Wake, link.Commands[0]);
        Assert.Equal(Sleep, link.Commands[^1]);
        Assert.DoesNotContain("0213", link.Commands);
    }

    [Fact]
    public void A_failed_read_still_hands_the_cooler_back()
    {
        var link = new FakeCommander { FailOnRead = 5 };
        using var driver = St(link);

        Assert.Throws<TimeoutException>(() => driver.Read());

        Assert.Equal(Sleep, link.Commands[^1]);
    }

    [Fact]
    public void An_unexpected_data_type_is_logged_and_the_payload_still_checked()
    {
        var link = new FakeCommander(64) { TypeOverride = (0x00, 0x00) };
        using var driver = St(link);

        var reading = driver.Read();

        Assert.Contains(reading.Sensors, s => s.Name == "Pump" && s.Value == 2357);
        Assert.Contains("data type", File.ReadAllText(Log.FilePath));
    }

    [Fact]
    public void A_commander_without_a_pump_reports_fans_and_probes()
    {
        var link = new FakeCommander { Speeds = [800, 900], Connected = [true, true], Temperatures = [28f] };
        using var xt = new CorsairCommander(link, "Corsair Commander Core XT", 0x0C2A, "", hasPump: false);

        var reading = xt.Read();

        Assert.Equal(["Fan #1", "Fan #2", "Temperature #1"], reading.Sensors.Select(s => s.Name));
        Assert.Equal("/corsair/commander-core-xt/0c2a", reading.Id);
    }

    [Fact]
    public void Every_supported_controller_is_registered_with_its_conflicts()
    {
        Assert.Equal("Corsair Commander ST", KnownCoolers.Find(0x1B1C, 0x0C32)?.Name);
        Assert.NotNull(KnownCoolers.Find(0x1B1C, 0x0C1C));
        Assert.NotNull(KnownCoolers.Find(0x1B1C, 0x0C2A));
        Assert.Null(KnownCoolers.Find(0x1B1C, 0x1C07));   // the Corsair PSU: LibreHardwareMonitor reads that one
        Assert.All(KnownCoolers.All, c => Assert.Contains(KnownCoolers.Icue, c.Conflicts));
        Assert.Equal("/corsair/commander-st/ABC", KnownCoolers.Find(0x1B1C, 0x0C32)!.IdFor("ABC"));
    }

    // ---- Sharing with other software --------------------------------------------------------------

    [Fact]
    public void Icue_its_service_and_other_cooler_tools_are_recognized()
    {
        var st = KnownCoolers.Find(0x1B1C, 0x0C32)!;

        Assert.Equal(["iCUE"], KnownCoolers.ConflictsAmong(st, ["explorer", "Corsair.Service", "iCUE.exe", "iCUEDevicePluginHost"]));
        Assert.Equal(["SignalRGB", "HWiNFO"], KnownCoolers.ConflictsAmong(st, ["SignalRgbLauncher", "HWiNFO64"]));
        Assert.Empty(KnownCoolers.ConflictsAmong(st, ["AsusFanControlService", "Pulse", "chrome", "FanControlHelperX"]));
    }

    private static readonly KnownCooler St_ = KnownCoolers.Find(0x1B1C, 0x0C32)!;
    private static readonly DateTime T0 = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void By_default_a_cooler_is_left_alone_while_other_software_runs()
    {
        var held = CoolerPolicy.Hold(St_, "k", CoolerAccess.Automatic, ["iCUE"], T0, T0);

        Assert.Equal(CoolerState.Paused, held!.State);
        Assert.Equal("Left alone while iCUE is running, so they don't fight over it", held.Detail);
        Assert.Contains("iCUE and SignalRGB are running",
            CoolerPolicy.Hold(St_, "k", CoolerAccess.Automatic, ["iCUE", "SignalRGB"], T0, T0)!.Detail);
    }

    [Fact]
    public void Reading_starts_again_a_little_after_the_other_app_quits()
    {
        Assert.Equal(CoolerState.Paused, CoolerPolicy.Hold(St_, "k", CoolerAccess.Automatic, [], T0, T0.AddSeconds(5))?.State);
        Assert.Null(CoolerPolicy.Hold(St_, "k", CoolerAccess.Automatic, [], T0, T0.AddSeconds(11)));
        Assert.Null(CoolerPolicy.Hold(St_, "k", CoolerAccess.Automatic, [], null, T0));
    }

    [Fact]
    public void Always_reads_regardless_and_off_never_does()
    {
        Assert.Null(CoolerPolicy.Hold(St_, "k", CoolerAccess.Always, ["iCUE"], T0, T0));
        Assert.Equal(CoolerState.Off, CoolerPolicy.Hold(St_, "k", CoolerAccess.Off, [], null, T0)!.State);
    }

    [Fact]
    public void A_failing_cooler_is_tried_again_less_and_less_often()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), CoolerPolicy.Backoff(1));
        Assert.Equal(TimeSpan.FromSeconds(15), CoolerPolicy.Backoff(3));
        Assert.Equal(TimeSpan.FromSeconds(60), CoolerPolicy.Backoff(40));
    }

    [Fact]
    public void Automatic_is_the_default()
    {
        Assert.Equal(CoolerAccess.Automatic, NewSettings().Current.Coolers.Access);
    }

    // ---- Into the snapshot, alerts and the cooling card -----------------------------------------

    private const string CoolerId = "/corsair/commander-st/2052337E534B";
    private const string PumpId = CoolerId + "/fan/0";

    private static HardwareReading Commander(float pump = 2400) =>
        Hardware(CoolerId, "Corsair Commander ST", HardwareType.Cooler,
            Sensor(PumpId, "Pump", SensorType.Fan, pump),
            Sensor(CoolerId + "/fan/1", "Fan #1", SensorType.Fan, 900),
            Sensor(CoolerId + "/fan/2", "Fan #2", SensorType.Fan, 880),
            Sensor(CoolerId + "/temperature/0", "Coolant", SensorType.Temperature, 31.4f));

    [Fact]
    public void Cooler_readings_join_the_snapshot_and_paused_ones_are_marked()
    {
        var board = Hardware("/lpc/nct6798d/0", "ASUS Z790", HardwareType.SuperIO);

        var reading = HardwareService.WithCoolers(Of(board), [Commander()]);
        var paused = HardwareService.WithCoolers(Of(board), [], [CoolerId]);

        Assert.Equal(2, reading.Hardware.Count);
        Assert.Empty(reading.Unread);
        Assert.Single(paused.Hardware);
        Assert.True(paused.IsUnread(PumpId));
        Assert.False(paused.IsUnread(CoolerId + "2/fan/0"));   // only below that hardware
        Assert.False(paused.IsUnread(CoolerId));
    }

    [Fact]
    public void A_pump_left_alone_for_icue_is_not_a_stopped_pump()
    {
        var engine = new AlertEngine();
        var rules = AlertDefaults.Create();
        var t = DateTime.Now;
        var events = new List<AlertEvent>();

        events.AddRange(engine.Evaluate(Of(Commander()), rules, PumpId, t));
        // iCUE starts: Pulse stops reading the cooler, and the pump disappears from the snapshots.
        for (var i = 1; i <= 15; i++)
            events.AddRange(engine.Evaluate(HardwareService.WithCoolers(Of(), [], [CoolerId]), rules, PumpId, t.AddSeconds(i)));

        Assert.DoesNotContain(events, e => e.RuleId == "builtin.pump");

        // The same pump vanishing without a reason still counts as stopped.
        for (var i = 16; i <= 30; i++)
            events.AddRange(engine.Evaluate(Of(), rules, PumpId, t.AddSeconds(i)));
        Assert.Contains(events, e => e is { RuleId: "builtin.pump", Kind: AlertEventKind.Raised });
    }

    private DashboardViewModel Dashboard(out SettingsService settings) =>
        new(true, NewCatalog(out settings), new SensorStream());

    [Fact]
    public void The_cooling_card_shows_the_coolers_pump_and_coolant_and_its_fans_go_to_fans()
    {
        var dashboard = Dashboard(out var settings);

        dashboard.Apply(Of(Commander()));

        Assert.True(dashboard.Cooling.HasData);
        Assert.Equal("Corsair Commander ST", dashboard.Cooling.Source);
        Assert.StartsWith("31.4", dashboard.Cooling.Headline);
        Assert.Contains(dashboard.Cooling.Rows, r => r.Label == "Pump" && r.Value.Contains("2,400"));
        Assert.DoesNotContain(dashboard.Cooling.Rows, r => r.Label.StartsWith("Fan"));
        Assert.Contains(dashboard.Fans, f => f.Name == "Fan #1" && f.Source == "Corsair Commander ST");
        // Its pump becomes the pump (for the alert, tray and mini window), and no header needs picking.
        Assert.Equal(PumpId, settings.Current.Sensors.Roles["Pump"]);
        Assert.False(dashboard.Cooling.ShowHeaderPicker);
    }

    [Fact]
    public void A_pump_header_the_user_picked_is_kept()
    {
        var dashboard = Dashboard(out var settings);
        settings.Update(s => s.Sensors.Roles["Pump"] = "/lpc/nct6798d/0/fan/1");

        dashboard.Apply(Of(Commander()));

        Assert.Equal("/lpc/nct6798d/0/fan/1", settings.Current.Sensors.Roles["Pump"]);
    }

    [Fact]
    public void A_paused_cooler_explains_itself_on_the_cooling_card()
    {
        var dashboard = Dashboard(out _);
        dashboard.CoolerStatuses =
        [
            new CoolerStatus("k", "Corsair Commander ST", CoolerState.Paused,
                "Left alone while iCUE is running, so they don't fight over it"),
        ];

        dashboard.Apply(Of());

        Assert.False(dashboard.Cooling.HasData);
        Assert.Equal("Corsair Commander ST isn't being read", dashboard.Cooling.EmptyTitle);
        Assert.Contains("iCUE is running", dashboard.Cooling.EmptyText);
        Assert.Contains("Always read", dashboard.Cooling.EmptyText);
    }
}
