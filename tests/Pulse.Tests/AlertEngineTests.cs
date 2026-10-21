using LibreHardwareMonitor.Hardware;
using Pulse.Models;
using Pulse.Services;
using static Pulse.Tests.Snap;

namespace Pulse.Tests;

public class AlertEngineTests : PulseTest
{
    private const string PumpId = "/lpc/nct6798d/0/fan/1";
    private static readonly DateTime T0 = new(2026, 10, 15, 12, 0, 0);

    private static HardwareSnapshot Machine(float? cpu = 60, float? pump = 2500, bool includePump = true, float? driveTemp = 45)
    {
        var board = Hardware("/lpc/nct6798d/0", "Nuvoton NCT6798D", HardwareType.SuperIO,
            includePump ? [Sensor(PumpId, "Fan #2", SensorType.Fan, pump)] : []);
        return Of(
            Hardware("/intelcpu/0", "CPU", HardwareType.Cpu,
                Sensor("/intelcpu/0/temperature/0", "CPU Package", SensorType.Temperature, cpu)),
            board,
            Hardware("/nvme/2", "CT4000T705SSD3", HardwareType.Storage,
                Sensor("/nvme/2/temperature/0", "Composite Temperature", SensorType.Temperature, driveTemp),
                Sensor("/nvme/2/temperature/1", "Warning Temperature", SensorType.Temperature, 86)));
    }

    private static List<AlertRule> Rules => AlertDefaults.Create();

    /// <summary>Feeds one snapshot per second and collects everything the engine raised.</summary>
    private static List<AlertEvent> Run(AlertEngine engine, IEnumerable<AlertRule> rules, params HardwareSnapshot[] seconds)
    {
        var events = new List<AlertEvent>();
        for (var i = 0; i < seconds.Length; i++)
            events.AddRange(engine.Evaluate(seconds[i], rules, PumpId, T0.AddSeconds(i)));
        return events;
    }

    [Fact]
    public void A_brief_spike_does_not_alert()
    {
        var events = Run(new AlertEngine(), Rules, Machine(cpu: 60), Machine(cpu: 99), Machine(cpu: 99), Machine(cpu: 60));
        Assert.Empty(events);
    }

    [Fact]
    public void A_sustained_breach_alerts_once_and_clears_when_it_ends()
    {
        var seconds = Enumerable.Repeat(Machine(cpu: 99), 12).Append(Machine(cpu: 70)).ToArray();
        var events = Run(new AlertEngine(), Rules, seconds);

        var raised = Assert.Single(events, e => e.Kind == AlertEventKind.Raised);
        Assert.Equal("builtin.cpu", raised.RuleId);
        Assert.Equal(T0.AddSeconds(10), raised.Time);
        Assert.True(raised.Notify);
        Assert.Contains("99.0 °C", raised.Message);
        Assert.Single(events, e => e.Kind == AlertEventKind.Cleared);
    }

    [Fact]
    public void Flapping_around_the_threshold_notifies_only_once_per_cooldown()
    {
        var seconds = new List<HardwareSnapshot>();
        for (var cycle = 0; cycle < 3; cycle++)
        {
            seconds.AddRange(Enumerable.Repeat(Machine(cpu: 99), 11));
            seconds.Add(Machine(cpu: 80));
        }
        var events = Run(new AlertEngine(), Rules, seconds.ToArray());

        var raised = events.Where(e => e.Kind == AlertEventKind.Raised).ToList();
        Assert.Equal(3, raised.Count);
        Assert.Equal([true, false, false], raised.Select(e => e.Notify));
    }

    [Fact]
    public void A_stopped_pump_is_critical_within_a_few_seconds()
    {
        var events = Run(new AlertEngine(), Rules, Machine(pump: 2500), Machine(pump: 0), Machine(pump: 0), Machine(pump: 0), Machine(pump: 0));

        var raised = Assert.Single(events);
        Assert.Equal("builtin.pump", raised.RuleId);
        Assert.Equal(AlertSeverity.Critical, raised.Severity);
        Assert.Equal(T0.AddSeconds(4), raised.Time);
    }

    [Fact]
    public void A_pump_reading_that_disappears_counts_as_stopped()
    {
        var events = Run(new AlertEngine(), Rules,
            Machine(pump: 2500), Machine(includePump: false), Machine(includePump: false), Machine(includePump: false), Machine(includePump: false));

        Assert.Single(events, e => e is { RuleId: "builtin.pump", Kind: AlertEventKind.Raised });
    }

    [Fact]
    public void A_pump_never_seen_does_not_alert()
    {
        // Without admin rights or the driver, the pump header never reports: that's not a pump failure.
        var events = Run(new AlertEngine(), Rules, Enumerable.Repeat(Machine(includePump: false), 10).ToArray());
        Assert.Empty(events);
    }

    [Fact]
    public void Drives_use_their_own_warning_temperature()
    {
        var hotButUnderWarning = Enumerable.Repeat(Machine(driveTemp: 80), 40).ToArray();
        Assert.Empty(Run(new AlertEngine(), Rules, hotButUnderWarning));

        var overWarning = Enumerable.Repeat(Machine(driveTemp: 88), 40).ToArray();
        var raised = Assert.Single(Run(new AlertEngine(), Rules, overWarning));
        Assert.Equal("CT4000T705SSD3 is too hot", raised.Title);
    }

    [Fact]
    public void Turning_a_rule_off_clears_its_alert_quietly()
    {
        var engine = new AlertEngine();
        var rules = Rules;
        Run(engine, rules, Enumerable.Repeat(Machine(cpu: 99), 12).ToArray());
        Assert.True(engine.Firing.Count > 0);

        rules.Single(r => r.Id == "builtin.cpu").Enabled = false;
        var events = engine.Evaluate(Machine(cpu: 99), rules, PumpId, T0.AddSeconds(20));

        var cleared = Assert.Single(events);
        Assert.Equal(AlertEventKind.Cleared, cleared.Kind);
        Assert.False(cleared.Notify);
        Assert.Empty(engine.Firing);
    }

    [Fact]
    public void Critical_state_is_reported_while_the_pump_alert_fires()
    {
        var engine = new AlertEngine();
        Run(engine, Rules, Enumerable.Repeat(Machine(pump: 0), 5).ToArray());
        Assert.True(engine.IsCriticalFiring(Rules));
    }

    [Fact]
    public void Custom_rules_watch_any_sensor_in_either_direction()
    {
        var rule = new AlertRule
        {
            Name = "Pump slow",
            Target = AlertTarget.Sensor,
            SensorId = PumpId,
            Condition = AlertCondition.Below,
            Threshold = 2000,
            HoldSeconds = 2,
        };
        var events = Run(new AlertEngine(), [rule], Machine(pump: 1800), Machine(pump: 1800), Machine(pump: 1800));

        var raised = Assert.Single(events);
        Assert.Equal("Pump slow", raised.Title);
        Assert.Contains("1,800 RPM", raised.Message);
    }

    [Fact]
    public void Default_rules_are_added_to_older_settings()
    {
        var rules = new List<AlertRule> { new() { Id = "custom", Name = "Mine" } };
        Assert.True(AlertDefaults.AddMissing(rules));
        Assert.Equal(5, rules.Count);
        Assert.False(AlertDefaults.AddMissing(rules));
    }

    [Fact]
    public void Pump_alerts_wait_while_the_startup_check_runs()
    {
        var engine = new AlertEngine();
        var checking = new PumpStatus(Settled: false, Change: null);
        for (var s = 0; s < 10; s++)
            Assert.Empty(engine.Evaluate(Machine(pump: 0), Rules, PumpId, T0.AddSeconds(s), checking));
    }

    [Fact]
    public void Moving_the_pump_to_another_header_clears_what_fired_for_the_old_one()
    {
        var engine = new AlertEngine();
        Run(engine, Rules, Machine(pump: 0), Machine(pump: 0), Machine(pump: 0), Machine(pump: 0));
        Assert.True(engine.IsCriticalFiring(Rules));

        var events = engine.Evaluate(Machine(pump: 0), Rules, "/lpc/nct6798d/0/fan/5", T0.AddSeconds(4));

        var cleared = Assert.Single(events);
        Assert.Equal(AlertEventKind.Cleared, cleared.Kind);
        Assert.Equal("Pump stopped: pump header changed", cleared.Title);
        Assert.False(engine.IsCriticalFiring(Rules));
    }

    [Fact]
    public void A_softened_pump_alert_turns_critical_once_the_user_says_it_is_the_pump()
    {
        var engine = new AlertEngine();
        var changed = new PumpStatus(true, new PumpChange(PumpId, "Fan #2", 450, 2500, 2600));
        var events = new List<AlertEvent>();
        for (var s = 0; s < 5; s++)
            events.AddRange(engine.Evaluate(Machine(pump: 450), Rules, PumpId, T0.AddSeconds(s), changed));

        var soft = Assert.Single(events);
        Assert.Equal(AlertSeverity.Warning, soft.Severity);
        Assert.Equal("Fan #2 has read about 450 rpm since Pulse started. Before, it ran at 2,500–2,600 rpm. "
                     + "If you changed or re-plugged your cooler, choose the pump header in the Cooling card.", soft.Message);
        Assert.False(engine.IsCriticalFiring(Rules));

        events.Clear();
        for (var s = 5; s < 10; s++)
            events.AddRange(engine.Evaluate(Machine(pump: 450), Rules, PumpId, T0.AddSeconds(s), new PumpStatus(true, null)));

        var loud = Assert.Single(events);
        Assert.Equal("Pump stopped", loud.Title);
        Assert.Equal(AlertSeverity.Critical, loud.Severity);
        Assert.True(loud.Notify);
        Assert.True(engine.IsCriticalFiring(Rules));
    }
}
