namespace Pulse.Services;

public enum AlertCondition
{
    Above,
    Below,
}

public enum AlertSeverity
{
    Warning,
    Critical,
}

/// <summary>What a rule watches.</summary>
public enum AlertTarget
{
    /// <summary>One sensor, by LHM identifier (<see cref="AlertRule.SensorId"/>).</summary>
    Sensor,

    /// <summary>Whatever header the user picked as the pump (<see cref="SensorRoles.Pump"/>).</summary>
    Pump,

    /// <summary>CPU package temperature (or the hottest core, when there's no package sensor).</summary>
    CpuPackage,

    /// <summary>GPU hot spot temperature.</summary>
    GpuHotSpot,

    /// <summary>Every drive, each against its own "Warning Temperature" (or the rule's threshold).</summary>
    DriveTemperature,
}

/// <summary>
/// An alert rule: notify when a reading stays above or below a threshold for a while.
/// Thresholds are in the sensor's own units (°C for temperatures, rpm for fans); the UI converts.
/// </summary>
public sealed class AlertRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public bool Enabled { get; set; } = true;
    public AlertTarget Target { get; set; }
    public string? SensorId { get; set; }

    /// <summary>Name shown in notifications and Settings; built-in rules use their default name.</summary>
    public string Name { get; set; } = "";

    public AlertCondition Condition { get; set; } = AlertCondition.Above;
    public double Threshold { get; set; }

    /// <summary>How long the condition must hold before alerting, so a brief spike doesn't notify.</summary>
    public double HoldSeconds { get; set; } = 10;

    /// <summary>After notifying, stay quiet this long even if the condition flaps.</summary>
    public double CooldownMinutes { get; set; } = 10;

    public AlertSeverity Severity { get; set; } = AlertSeverity.Warning;

    /// <summary>
    /// Treat a reading that disappears (after having been seen) as a breach. On for the pump:
    /// a pump that stops reporting is as alarming as one reading 0 rpm.
    /// </summary>
    public bool AlertWhenMissing { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsBuiltIn => Id.StartsWith(BuiltInPrefix, StringComparison.Ordinal);

    public const string BuiltInPrefix = "builtin.";

    public AlertRule Clone() => (AlertRule)MemberwiseClone();
}

public sealed class AlertSettings
{
    public List<AlertRule> Rules { get; set; } = AlertDefaults.Create();

    public bool PlaySoundForCritical { get; set; } = true;
}

/// <summary>The rules every install starts with. Users can edit or turn them off, but not delete them.</summary>
public static class AlertDefaults
{
    public static List<AlertRule> Create() =>
    [
        new()
        {
            Id = AlertRule.BuiltInPrefix + "pump",
            Name = "Pump stopped",
            Target = AlertTarget.Pump,
            Condition = AlertCondition.Below,
            Threshold = 500,
            HoldSeconds = 3,
            CooldownMinutes = 5,
            Severity = AlertSeverity.Critical,
            AlertWhenMissing = true,
        },
        new()
        {
            Id = AlertRule.BuiltInPrefix + "cpu",
            Name = "CPU too hot",
            Target = AlertTarget.CpuPackage,
            Condition = AlertCondition.Above,
            Threshold = 95,
            HoldSeconds = 10,
        },
        new()
        {
            Id = AlertRule.BuiltInPrefix + "gpu-hotspot",
            Name = "GPU hot spot too hot",
            Target = AlertTarget.GpuHotSpot,
            Condition = AlertCondition.Above,
            Threshold = 100,
            HoldSeconds = 10,
        },
        new()
        {
            Id = AlertRule.BuiltInPrefix + "drives",
            Name = "Drive too hot",
            Target = AlertTarget.DriveTemperature,
            Condition = AlertCondition.Above,
            // Used only for drives that don't report their own warning temperature.
            Threshold = 70,
            HoldSeconds = 30,
        },
    ];

    /// <summary>Adds any built-in rule missing from <paramref name="rules"/> (new ones from an update, say).</summary>
    public static bool AddMissing(List<AlertRule> rules)
    {
        var added = false;
        foreach (var rule in Create())
        {
            if (rules.All(r => r.Id != rule.Id))
            {
                rules.Add(rule);
                added = true;
            }
        }
        return added;
    }
}
