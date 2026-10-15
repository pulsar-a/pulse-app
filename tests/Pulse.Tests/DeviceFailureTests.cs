using System.IO;
using LibreHardwareMonitor.Hardware;
using Pulse.Models;
using Pulse.Services;

namespace Pulse.Tests;

/// <summary>A Corsair PSU on USB vanishing for a moment must not stop the other sensors.</summary>
public class DeviceFailureTests : PulseTest
{
    private static readonly DateTime T0 = new(2026, 10, 16, 1, 25, 59);

    // ---- One device failing doesn't stop the rest ---------------------------------------------

    [Fact]
    public void A_device_that_throws_is_skipped_and_the_others_still_read()
    {
        var cpu = new FakeHardware("/intelcpu/0", "CPU", HardwareType.Cpu, ("CPU Package", SensorType.Temperature, 71));
        var psu = new FakeHardware("/psu/corsair/0", "Corsair HX1000i", HardwareType.Psu, ("Total watts", SensorType.Power, 182))
        {
            Fails = new IOException("Operation failed early: A device which does not exist was specified."),
        };
        var failures = new List<string>();

        var snapshot = HardwareService.TakeSnapshot([cpu, psu], new Dictionary<int, IReadOnlyList<VolumeInfo>>(),
            failed: (h, _) => failures.Add(h.Name));

        Assert.Equal(["Corsair HX1000i"], failures);
        Assert.Equal(1, cpu.Updates);
        Assert.Equal(71, snapshot.Hardware.Single(h => h.Type == HardwareType.Cpu).Sensors[0].Value);
        // The failed device is in the snapshot without values, so ReadingHold can bridge the gap.
        Assert.Null(snapshot.Hardware.Single(h => h.Type == HardwareType.Psu).Sensors[0].Value);
    }

    // ---- Backing off and reopening ------------------------------------------------------------

    [Fact]
    public void A_failing_device_is_retried_after_a_growing_pause()
    {
        var health = new DeviceHealth();
        var error = new IOException("gone");

        health.Failed("psu", "PSU", error, T0);
        Assert.False(health.CanRead("psu", T0.AddSeconds(0.5)));
        Assert.True(health.CanRead("psu", T0.AddSeconds(1)));

        health.Failed("psu", "PSU", error, T0.AddSeconds(1));
        Assert.False(health.CanRead("psu", T0.AddSeconds(2.5)));
        Assert.True(health.CanRead("psu", T0.AddSeconds(3)));

        Assert.True(health.CanRead("cpu", T0));   // others aren't affected
    }

    [Fact]
    public void The_pause_never_grows_past_thirty_seconds()
    {
        var health = new DeviceHealth();
        var now = T0;
        for (var i = 0; i < 20; i++)
        {
            health.Failed("psu", "PSU", new IOException("gone"), now);
            now = now.AddMinutes(1);
        }

        var last = now.AddMinutes(-1);
        Assert.False(health.CanRead("psu", last.AddSeconds(29)));
        Assert.True(health.CanRead("psu", last.AddSeconds(30)));
    }

    [Fact]
    public void After_three_failures_in_a_row_it_is_reopened_at_most_every_thirty_seconds()
    {
        var health = new DeviceHealth();
        var error = new IOException("gone");

        Assert.Equal(FailureAction.Wait, health.Failed("psu", "PSU", error, T0));
        Assert.Equal(FailureAction.Wait, health.Failed("psu", "PSU", error, T0.AddSeconds(1)));
        Assert.Equal(FailureAction.Reopen, health.Failed("psu", "PSU", error, T0.AddSeconds(3)));
        Assert.Equal(FailureAction.Wait, health.Failed("psu", "PSU", error, T0.AddSeconds(7)));
        Assert.Equal(FailureAction.Reopen, health.Failed("psu", "PSU", error, T0.AddSeconds(33)));
    }

    [Fact]
    public void A_good_read_clears_the_record()
    {
        var health = new DeviceHealth();
        health.Failed("psu", "PSU", new IOException("gone"), T0);
        health.Failed("psu", "PSU", new IOException("gone"), T0.AddSeconds(1));

        health.Succeeded("psu", "PSU");

        Assert.True(health.CanRead("psu", T0.AddSeconds(1)));
        Assert.Equal(FailureAction.Wait, health.Failed("psu", "PSU", new IOException("gone"), T0.AddSeconds(2)));
    }

    // ---- Mini window settings -----------------------------------------------------------------

    [Fact]
    public void Hiding_the_mini_window_behind_pulse_is_off_after_upgrading()
    {
        File.WriteAllText(Path.Combine(TempFolder, "settings.json"),
            """{ "SchemaVersion": 2, "Mini": { "HideWhenPulseActive": true, "Opacity": 0.75 } }""");

        var settings = NewSettings();

        Assert.False(settings.Current.Mini.HideWhenPulseActive);
        Assert.Equal(0.75, settings.Current.Mini.Opacity);
        Assert.Equal(Settings.CurrentSchemaVersion, settings.Current.SchemaVersion);
    }

    [Fact]
    public void Once_upgraded_a_deliberate_choice_to_hide_is_kept()
    {
        File.WriteAllText(Path.Combine(TempFolder, "settings.json"),
            $$"""{ "SchemaVersion": {{Settings.CurrentSchemaVersion}}, "Mini": { "HideWhenPulseActive": true } }""");

        Assert.True(NewSettings().Current.Mini.HideWhenPulseActive);
    }

    /// <summary>Just enough of an LHM device to be read, or to fail reading.</summary>
    private sealed class FakeHardware : IHardware
    {
        private readonly FakeSensor[] _sensors;

        public FakeHardware(string id, string name, HardwareType type, params (string Name, SensorType Type, float Value)[] sensors)
        {
            Identifier = new Identifier(id.Trim('/').Split('/'));
            Name = name;
            HardwareType = type;
            _sensors = sensors.Select((s, i) => new FakeSensor(this, $"{id}/{s.Type.ToString().ToLowerInvariant()}/{i}", s.Name, s.Type, s.Value)).ToArray();
        }

        public Exception? Fails { get; init; }
        public int Updates { get; private set; }

        public void Update()
        {
            if (Fails is not null) throw Fails;
            Updates++;
        }

        public HardwareType HardwareType { get; }
        public Identifier Identifier { get; }
        public string Name { get; set; }
        public IHardware? Parent => null;
        public IDictionary<string, string> Properties { get; } = new Dictionary<string, string>();
        public ISensor[] Sensors => _sensors;
        public IHardware[] SubHardware => [];
        public string GetReport() => "";
        public void Accept(IVisitor visitor) { }
        public void Traverse(IVisitor visitor) { }
        public event SensorEventHandler? SensorAdded { add { } remove { } }
        public event SensorEventHandler? SensorRemoved { add { } remove { } }
    }

    private sealed class FakeSensor(IHardware hardware, string id, string name, SensorType type, float value) : ISensor
    {
        public IControl? Control => null;
        public IHardware Hardware => hardware;
        public Identifier Identifier { get; } = new(id.Trim('/').Split('/'));
        public int Index => 0;
        public bool IsDefaultHidden => false;
        public float? Max => value;
        public float? Min => value;
        public string Name { get; set; } = name;
        public IReadOnlyList<IParameter> Parameters => [];
        public SensorType SensorType => type;
        public float? Value => value;
        public IEnumerable<SensorValue> Values => [];
        public TimeSpan ValuesTimeWindow { get; set; }
        public void ClearValues() { }
        public void ResetMin() { }
        public void ResetMax() { }
        public void Accept(IVisitor visitor) { }
        public void Traverse(IVisitor visitor) { }
    }
}
