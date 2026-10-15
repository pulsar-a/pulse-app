using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pulse.Models;
using Pulse.Services;

namespace Pulse.ViewModels;

/// <summary>An entry in the alerts list under the bell.</summary>
public sealed record AlertItem(DateTime Time, string Title, string Message, AlertSeverity Severity, bool IsCleared)
{
    public string TimeText => Time.ToString(Time.Date == DateTime.Today ? "HH:mm:ss" : "d MMM HH:mm");
}

/// <summary>
/// Runs the alert engine on every reading (even while the window is hidden), keeps the recent-alerts
/// list for the bell in the header, sends notifications, and reports whether a critical alert is active
/// (the tray icon turns red). Lives on the UI thread.
/// </summary>
public partial class AlertCenter : ObservableObject
{
    private const int MaxItems = 50;

    private readonly AlertEngine _engine = new();
    private readonly SettingsService _settings;
    private readonly SensorCatalog _catalog;
    private readonly INotifier _notifier;
    private readonly Dispatcher _dispatcher;
    private HardwareSnapshot? _pending;
    private int _scheduled;

    public AlertCenter(SettingsService settings, SensorCatalog catalog, SensorStream stream, INotifier notifier, Dispatcher dispatcher)
    {
        _settings = settings;
        _catalog = catalog;
        _notifier = notifier;
        _dispatcher = dispatcher;
        stream.SnapshotPublished += OnSnapshotPublished;
    }

    public ObservableCollection<AlertItem> Items { get; } = [];

    [ObservableProperty] public partial int UnreadCount { get; set; }
    [ObservableProperty] public partial bool HasUnread { get; set; }
    [ObservableProperty] public partial bool HasItems { get; set; }

    /// <summary>True while any critical alert (the pump) is firing.</summary>
    [ObservableProperty] public partial bool IsCriticalActive { get; set; }

    partial void OnUnreadCountChanged(int value) => HasUnread = value > 0;

    /// <summary>Called on the polling thread; evaluation happens on the UI thread with the newest snapshot.</summary>
    private void OnSnapshotPublished(HardwareSnapshot snapshot)
    {
        Volatile.Write(ref _pending, snapshot);
        if (Interlocked.Exchange(ref _scheduled, 1) == 0)
            _dispatcher.BeginInvoke(() =>
            {
                Interlocked.Exchange(ref _scheduled, 0);
                if (Interlocked.Exchange(ref _pending, null) is { } latest)
                    Evaluate(latest, DateTime.Now);
            }, DispatcherPriority.Background);
    }

    internal void Evaluate(HardwareSnapshot snapshot, DateTime now)
    {
        var rules = _settings.Current.Alerts.Rules;
        foreach (var e in _engine.Evaluate(snapshot, rules, _catalog.RoleSensor(SensorRoles.Pump), now))
            Handle(e);
        IsCriticalActive = _engine.IsCriticalFiring(rules);
    }

    private void Handle(AlertEvent e)
    {
        var cleared = e.Kind == AlertEventKind.Cleared;
        Log.Write($"Alert {(cleared ? "cleared" : "raised")}: {e.Title} — {e.Message}{(e.Notify || cleared ? "" : " (quiet: cooldown)")}");

        Items.Insert(0, new AlertItem(e.Time, e.Title, e.Message, e.Severity, cleared));
        while (Items.Count > MaxItems)
            Items.RemoveAt(Items.Count - 1);
        HasItems = true;

        if (cleared)
            return;
        UnreadCount++;
        if (e.Notify)
            _notifier.Show(e.Title, e.Message, e.Severity,
                playSound: e.Severity == AlertSeverity.Critical ? _settings.Current.Alerts.PlaySoundForCritical : true);
    }

    /// <summary>The bell was opened: everything in the list has been seen.</summary>
    [RelayCommand]
    private void MarkRead() => UnreadCount = 0;

    [RelayCommand]
    private void Clear()
    {
        Items.Clear();
        UnreadCount = 0;
        HasItems = false;
    }

    /// <summary>Settings → Alerts → "Send a test notification".</summary>
    [RelayCommand]
    private void SendTest() =>
        _notifier.Show("Test alert from Pulse", "Alerts are working. This is what a critical alert looks like.",
            AlertSeverity.Critical, _settings.Current.Alerts.PlaySoundForCritical);
}
