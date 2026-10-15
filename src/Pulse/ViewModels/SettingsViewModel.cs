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

    public SettingsViewModel(SettingsService settings, SensorCatalog catalog, bool driverInstalled)
    {
        _settings = settings;
        _catalog = catalog;

        Version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "dev";
        AdminText = HardwareService.IsElevated ? "Running as administrator" : "Not running as administrator";
        DriverText = driverInstalled ? "PawnIO driver installed" : "PawnIO driver not installed";
        LogPath = Log.FilePath;
        SettingsPath = settings.FilePath;

        _catalog.NamesChanged += Refresh;
        Refresh();
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

    /// <summary>Re-reads names and roles; called when the page opens and after any rename.</summary>
    public void Refresh()
    {
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
