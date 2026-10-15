using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibreHardwareMonitor.Hardware;
using Pulse.Services;

namespace Pulse.ViewModels;

/// <summary>
/// One rule in Settings → Alerts. Edits go straight into the rule (it's the instance stored in settings)
/// through <see cref="SettingsService.Update"/>, so they're saved and take effect on the next reading.
/// Temperature thresholds are shown and edited in the user's unit but stored in °C.
/// </summary>
public partial class AlertRuleViewModel : ObservableObject
{
    private readonly AlertRule _rule;
    private readonly SettingsService _settings;
    private readonly SensorType _type;

    public AlertRuleViewModel(AlertRule rule, SettingsService settings, SensorCatalog catalog, Action<AlertRuleViewModel> delete)
    {
        _rule = rule;
        _settings = settings;
        _type = rule.Target switch
        {
            AlertTarget.Pump => SensorType.Fan,
            AlertTarget.Sensor => rule.SensorId is { } id ? catalog.Find(id)?.Type ?? SensorType.Temperature : SensorType.Temperature,
            _ => SensorType.Temperature,
        };
        SensorText = rule.Target switch
        {
            AlertTarget.Pump => "The header chosen as the CPU pump",
            AlertTarget.CpuPackage => "CPU package temperature",
            AlertTarget.GpuHotSpot => "GPU hot spot temperature",
            AlertTarget.DriveTemperature => "Each drive; drives that report their own warning temperature use it instead",
            _ => rule.SensorId is { } sid && catalog.Find(sid) is { } info ? $"{info.DisplayName} · {info.HardwareName}" : rule.SensorId ?? "",
        };
        DeleteCommand = new RelayCommand(() => delete(this), () => !rule.IsBuiltIn);
    }

    public AlertRule Rule => _rule;
    public string Name => _rule.Name;
    public string SensorText { get; }
    public bool IsCritical => _rule.Severity == AlertSeverity.Critical;
    public bool CanDelete => !_rule.IsBuiltIn;
    public IRelayCommand DeleteCommand { get; }

    public string ConditionText => _rule.Condition == AlertCondition.Above ? "Above" : "Below";

    public string UnitText => _type switch
    {
        SensorType.Temperature => SensorFormat.TemperatureSymbol,
        SensorType.Fan => "rpm",
        SensorType.Load or SensorType.Control or SensorType.Level => "%",
        SensorType.Power => "W",
        SensorType.Clock => "MHz",
        SensorType.Voltage => "V",
        SensorType.Flow => "L/h",
        _ => "",
    };

    public bool Enabled
    {
        get => _rule.Enabled;
        set
        {
            if (_rule.Enabled == value) return;
            _settings.Update(_ => _rule.Enabled = value);
            OnPropertyChanged();
        }
    }

    /// <summary>The threshold in the user's unit.</summary>
    public double Threshold
    {
        get => Math.Round(_type == SensorType.Temperature ? SensorFormat.ToDisplayTemperature(_rule.Threshold) : _rule.Threshold, 1);
        set
        {
            var stored = _type == SensorType.Temperature && SensorFormat.TemperatureUnit == TemperatureUnit.Fahrenheit
                ? (value - 32) * 5 / 9
                : value;
            if (Math.Abs(stored - _rule.Threshold) < 0.001) return;
            _settings.Update(_ => _rule.Threshold = stored);
            OnPropertyChanged();
        }
    }

    public double HoldSeconds
    {
        get => _rule.HoldSeconds;
        set
        {
            value = Math.Clamp(value, 0, 3600);
            if (Math.Abs(value - _rule.HoldSeconds) < 0.001) return;
            _settings.Update(_ => _rule.HoldSeconds = value);
            OnPropertyChanged();
        }
    }
}
