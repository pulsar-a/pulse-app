using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pulse.Services;

namespace Pulse.ViewModels;

/// <summary>
/// The Settings page. Each later roadmap step adds its own section here
/// (startup and tray, alerts, history, appearance).
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly SensorCatalog _catalog;
    private readonly SensorStream _stream;

    public SettingsViewModel(SettingsService settings, SensorCatalog catalog, bool driverInstalled, SensorStream stream)
    {
        _settings = settings;
        _catalog = catalog;
        _stream = stream;

        Version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "dev";
        AdminText = HardwareService.IsElevated ? "Running as administrator" : "Not running as administrator";
        DriverText = driverInstalled ? "PawnIO driver installed" : "PawnIO driver not installed";
        LogPath = Log.FilePath;
        SettingsPath = settings.FilePath;

        _catalog.NamesChanged += Refresh;
        Refresh();
    }

    // General

    public IReadOnlyList<IntervalChoice> IntervalChoices { get; } = GeneralSettings.UpdateIntervalChoices
        .Select(s => new IntervalChoice(s, s < 1 ? $"{s * 1000:0} ms" : s == 1 ? "1 second" : $"{s:0} seconds"))
        .ToList();

    public IntervalChoice SelectedInterval
    {
        get => IntervalChoices.FirstOrDefault(c => c.Seconds == _settings.Current.General.UpdateIntervalSeconds)
               ?? IntervalChoices.First(c => c.Seconds == 1);
        set
        {
            if (value is null || value.Seconds == _settings.Current.General.UpdateIntervalSeconds)
                return;
            _settings.Update(s => s.General.UpdateIntervalSeconds = value.Seconds);
            OnPropertyChanged();
        }
    }

    // Startup and tray

    public bool ShowTrayIcon
    {
        get => _settings.Current.Startup.ShowTrayIcon;
        set => UpdateStartup(s => s.ShowTrayIcon = value);
    }

    public bool StartMinimized
    {
        get => _settings.Current.Startup.StartMinimized;
        set => UpdateStartup(s => s.StartMinimized = value);
    }

    public bool CloseToTray
    {
        get => _settings.Current.Startup.CloseToTray;
        set { if (value) UpdateStartup(s => s.CloseToTray = true); }
    }

    public bool CloseExits
    {
        get => !_settings.Current.Startup.CloseToTray;
        set { if (value) UpdateStartup(s => s.CloseToTray = false); }
    }

    public ObservableCollection<TrayChoice> TrayChoices { get; } = [];

    public TrayChoice? SelectedTrayChoice
    {
        get => TrayChoices.FirstOrDefault(c => c.SensorId == _settings.Current.Startup.TraySensorId);
        set
        {
            if (value is not null && value.SensorId != _settings.Current.Startup.TraySensorId)
                UpdateStartup(s => s.TraySensorId = value.SensorId);
        }
    }

    /// <summary>Whether the start-with-Windows task exists. Read from Task Scheduler when the page opens.</summary>
    [ObservableProperty] public partial bool StartWithWindows { get; set; }

    /// <summary>Creating the task needs admin rights.</summary>
    public bool CanChangeStartWithWindows { get; } = HardwareService.IsElevated;

    public string StartWithWindowsHint => CanChangeStartWithWindows
        ? "Starts Pulse in the notification area when you sign in, without the admin prompt."
        : "Needs Pulse to run as administrator. Restart Pulse as administrator to change this.";

    private bool _loadingStartup;

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_loadingStartup || !CanChangeStartWithWindows)
            return;
        _ = ApplyStartWithWindowsAsync(value);
    }

    private async Task ApplyStartWithWindowsAsync(bool enable)
    {
        var ok = enable
            ? await StartupTask.EnableAsync(Environment.ProcessPath!)
            : await StartupTask.DisableAsync();
        if (!ok)
            await LoadStartWithWindowsAsync();
    }

    private async Task LoadStartWithWindowsAsync()
    {
        var enabled = await StartupTask.IsEnabledAsync();
        _loadingStartup = true;
        StartWithWindows = enabled;
        _loadingStartup = false;
    }

    private void UpdateStartup(Action<StartupSettings> change)
    {
        _settings.Update(s => change(s.Startup));
        OnPropertyChanged(nameof(ShowTrayIcon));
        OnPropertyChanged(nameof(StartMinimized));
        OnPropertyChanged(nameof(CloseToTray));
        OnPropertyChanged(nameof(CloseExits));
        OnPropertyChanged(nameof(SelectedTrayChoice));
    }

    private void RefreshTrayChoices()
    {
        var current = _settings.Current.Startup.TraySensorId;
        var choices = TrayText.Choices(_stream.Latest, _catalog.RoleSensor(SensorRoles.Pump)).ToList();
        // A sensor picked from All sensors ("Show in tray icon") isn't one of the common choices.
        if (current is not null && choices.All(c => c.SensorId != current))
            choices.Add(new TrayChoice(_catalog.Find(current)?.DisplayName ?? current, current));

        TrayChoices.Clear();
        foreach (var choice in choices)
            TrayChoices.Add(choice);
        OnPropertyChanged(nameof(SelectedTrayChoice));
    }

    // Alerts

    /// <summary>Set once the alert center exists; the "Send a test notification" button uses it.</summary>
    [ObservableProperty] public partial AlertCenter? Alerts { get; private set; }

    public void AttachAlerts(AlertCenter alerts) => Alerts = alerts;

    public ObservableCollection<AlertRuleViewModel> AlertRules { get; } = [];

    public bool PlaySoundForCritical
    {
        get => _settings.Current.Alerts.PlaySoundForCritical;
        set
        {
            _settings.Update(s => s.Alerts.PlaySoundForCritical = value);
            OnPropertyChanged();
        }
    }

    private void RefreshAlertRules()
    {
        AlertRules.Clear();
        foreach (var rule in _settings.Current.Alerts.Rules)
            AlertRules.Add(new AlertRuleViewModel(rule, _settings, _catalog, DeleteRule));
    }

    private void DeleteRule(AlertRuleViewModel rule)
    {
        _settings.Update(s => s.Alerts.Rules.Remove(rule.Rule));
        AlertRules.Remove(rule);
    }

    // Units

    public bool IsCelsius
    {
        get => _settings.Current.Units.Temperature == TemperatureUnit.Celsius;
        set { if (value) SetTemperatureUnit(TemperatureUnit.Celsius); }
    }

    public bool IsFahrenheit
    {
        get => _settings.Current.Units.Temperature == TemperatureUnit.Fahrenheit;
        set { if (value) SetTemperatureUnit(TemperatureUnit.Fahrenheit); }
    }

    private void SetTemperatureUnit(TemperatureUnit unit)
    {
        if (_settings.Current.Units.Temperature == unit)
            return;
        _settings.Update(s => s.Units.Temperature = unit);
        OnPropertyChanged(nameof(IsCelsius));
        OnPropertyChanged(nameof(IsFahrenheit));
    }

    // Sensors

    public ObservableCollection<RenamedSensorItem> RenamedSensors { get; } = [];

    [ObservableProperty] public partial bool HasRenamedSensors { get; set; }
    [ObservableProperty] public partial string PumpText { get; set; } = "";
    [ObservableProperty] public partial bool HasPumpChoice { get; set; }

    /// <summary>Re-reads names, roles, tray choices and the startup task; called when the page opens and after any rename.</summary>
    public void Refresh()
    {
        RefreshTrayChoices();
        RefreshAlertRules();
        _ = LoadStartWithWindowsAsync();

        RenamedSensors.Clear();
        foreach (var sensor in _catalog.RenamedSensors())
        {
            RenamedSensors.Add(new RenamedSensorItem(
                sensor.DisplayName,
                $"{sensor.OriginalName} · {sensor.HardwareName}",
                new RelayCommand(() => _catalog.ResetName(sensor.Id))));
        }
        HasRenamedSensors = RenamedSensors.Count > 0;

        var pump = _catalog.RoleSensor(SensorRoles.Pump);
        HasPumpChoice = pump is not null;
        PumpText = pump switch
        {
            null => "Not chosen yet. Pulse picks it automatically when only one motherboard header spins.",
            "" => "Cleared. Pick it in the CPU cooling card on the dashboard.",
            _ when _catalog.Find(pump) is { } info => $"{info.OriginalName} · {info.HardwareName}",
            _ => $"{pump} (not detected right now)",
        };
    }

    /// <summary>Forgets the pump choice so Pulse can detect it again.</summary>
    [RelayCommand]
    private void ResetPump() => _catalog.ForgetRole(SensorRoles.Pump);

    // About

    public string Version { get; }
    public string AdminText { get; }
    public string DriverText { get; }
    public string LogPath { get; }
    public string SettingsPath { get; }

    /// <summary>Shows the log in Explorer (which runs unelevated, unlike Pulse).</summary>
    [RelayCommand]
    private void OpenLogFolder() => Process.Start("explorer.exe", $"/select,\"{LogPath}\"");
}

public sealed record RenamedSensorItem(string Name, string Details, IRelayCommand ResetCommand);

public sealed record IntervalChoice(double Seconds, string Label)
{
    public override string ToString() => Label;
}
