using System.Diagnostics;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibreHardwareMonitor.Hardware;
using Pulse.Models;
using Pulse.Services;

namespace Pulse.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private const string PawnIoUrl = "https://pawnio.eu";

    private readonly Dispatcher _dispatcher;
    private HardwareSnapshot? _pending;
    private int _applyScheduled;

    public MainViewModel(HardwareService hardware, SettingsService settings, Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        var driverInstalled = HardwareService.IsDriverInstalled;

        Dashboard = new DashboardViewModel(driverInstalled, settings);
        Sensors = new SensorsViewModel();

        ShowElevationBanner = !HardwareService.IsElevated;
        ShowDriverBanner = !driverInstalled;

        hardware.SnapshotReady += OnSnapshotReady;
        hardware.Failed += ex => _dispatcher.BeginInvoke(() =>
        {
            ErrorText = $"Couldn't read sensors: {ex.Message}";
            IsLoading = false;
        });
    }

    public DashboardViewModel Dashboard { get; }
    public SensorsViewModel Sensors { get; }

    [ObservableProperty] public partial bool IsDashboardSelected { get; set; } = true;
    [ObservableProperty] public partial bool IsSensorsSelected { get; set; }

    partial void OnIsSensorsSelectedChanged(bool value) => Sensors.IsActive = value;

    [ObservableProperty] public partial string MachineName { get; set; } = "Pulse";
    [ObservableProperty] public partial string StatusText { get; set; } = "Detecting hardware…";
    [ObservableProperty] public partial bool IsLoading { get; set; } = true;
    [ObservableProperty] public partial string? ErrorText { get; set; }

    [ObservableProperty] public partial bool ShowElevationBanner { get; set; }
    [ObservableProperty] public partial bool ShowDriverBanner { get; set; }

    [RelayCommand]
    private void DismissDriverBanner() => ShowDriverBanner = false;

    [RelayCommand]
    private void DismissElevationBanner() => ShowElevationBanner = false;

    /// <summary>
    /// Opens the PawnIO site through Explorer so the browser starts unelevated,
    /// even though Pulse itself runs as administrator.
    /// </summary>
    [RelayCommand]
    private static void OpenPawnIo() => Process.Start("explorer.exe", PawnIoUrl);

    /// <summary>
    /// Called on the polling thread. Only the newest snapshot is applied; if the UI falls behind,
    /// older ones are dropped instead of queuing up.
    /// </summary>
    private void OnSnapshotReady(HardwareSnapshot snapshot)
    {
        Volatile.Write(ref _pending, snapshot);
        if (Interlocked.Exchange(ref _applyScheduled, 1) == 0)
            _dispatcher.BeginInvoke(ApplyPending, DispatcherPriority.Background);
    }

    private void ApplyPending()
    {
        Interlocked.Exchange(ref _applyScheduled, 0);
        if (Interlocked.Exchange(ref _pending, null) is not { } snapshot)
            return;

        var sw = Stopwatch.StartNew();
        var board = snapshot.OfType(HardwareType.Motherboard).FirstOrDefault();
        MachineName = board?.Name ?? Environment.MachineName;
        StatusText = $"Live · updated {snapshot.Timestamp:HH:mm:ss}";

        Dashboard.Apply(snapshot);
        Sensors.Apply(snapshot);
        IsLoading = false;

        if (sw.ElapsedMilliseconds > 30)
            Log.Write($"Applying a snapshot took {sw.ElapsedMilliseconds} ms on the UI thread");
    }
}
