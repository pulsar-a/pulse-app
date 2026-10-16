using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;
using System.Windows.Threading;
using Pulse.Models;
using Pulse.ViewModels;

namespace Pulse.Services.Web;

// What the phone gets: the desktop dashboard's cards, already formatted, in plain records.

public sealed record WebRow(string Label, string Value);

public sealed record WebGauge(
    string Title, string Device, string Value, string Caption, double Fraction, double Min, double Max, string Heat,
    string Accent, string? SensorId, IReadOnlyList<WebRow> Rows, string HistoryLabel, IReadOnlyList<double?> History);

public sealed record WebCooling(
    bool HasData, string Source, string Headline, string Caption, string Heat, string? SensorId, IReadOnlyList<WebRow> Rows,
    string EmptyTitle, string EmptyText, IReadOnlyList<double?> History);

public sealed record WebDrive(string Name, string Letters, string Temperature, double Used, string Details, string? SensorId);

public sealed record WebFan(string Name, string Source, string Speed, double Fraction, string? SensorId);

public sealed record WebNetworkCard(string Adapter, string Down, string Up, string Totals, string? SensorId);

public sealed record WebAlert(string Time, string Title, string Message, string Severity, bool Cleared);

public sealed record WebSummary(
    string Machine, string Updated, IReadOnlyList<WebGauge> Gauges, WebCooling Cooling, IReadOnlyList<WebFan> Fans,
    IReadOnlyList<WebDrive> Drives, WebNetworkCard Network, IReadOnlyList<WebAlert> Alerts)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Reads the cards of a dashboard that has just applied a snapshot. UI thread (the cards are view models).</summary>
    public static WebSummary From(DashboardViewModel dashboard, string machine, DateTime time, IEnumerable<AlertItem> alerts)
    {
        var cooling = dashboard.Cooling;
        return new WebSummary(
            machine,
            time.ToString("HH:mm:ss"),
            dashboard.Cards.Select(Gauge).ToList(),
            new WebCooling(cooling.HasData, cooling.Source, cooling.Headline, cooling.HeadlineCaption, cooling.Heat.ToString(),
                cooling.SensorId, Rows(cooling.Rows), cooling.EmptyTitle, cooling.EmptyText, Series(cooling.History)),
            dashboard.Fans.Select(f => new WebFan(f.Name, f.Source, f.SpeedText, Round(f.Fraction), f.SensorId)).ToList(),
            dashboard.Drives.Select(d => new WebDrive(d.Name, string.Join(" ", d.Volumes.Select(v => $"{v.Letter}:")), d.TemperatureText,
                Round(d.UsedFraction), d.Details, d.SensorId)).ToList(),
            new WebNetworkCard(dashboard.Network.Adapter, dashboard.Network.DownText, dashboard.Network.UpText,
                dashboard.Network.Totals, dashboard.Network.SensorId),
            alerts.Take(5).Select(a => new WebAlert(a.TimeText, a.Title, a.Message, a.Severity.ToString(), a.IsCleared)).ToList());
    }

    private static WebGauge Gauge(GaugeCardViewModel card) => new(
        card.Title, card.Device, card.GaugeText, card.GaugeCaption, Round(card.Gauge), Round(card.GaugeMin), Round(card.GaugeMax),
        card.Heat.ToString(), Hex(card.Accent), card.SensorId, Rows(card.Rows), card.HistoryLabel, Series(card.History));

    private static List<WebRow> Rows(IEnumerable<StatRow> rows) => rows.Select(r => new WebRow(r.Label, r.Value)).ToList();

    /// <summary>The last minute, with gaps as null and values rounded: smaller to send every second.</summary>
    private static List<double?> Series(IReadOnlyList<double>? values) =>
        values?.Select(v => double.IsNaN(v) ? (double?)null : Math.Round(v, 1)).ToList() ?? [];

    private static double Round(double value) => double.IsNaN(value) ? 0 : Math.Round(Math.Clamp(value, 0, 1), 4);

    private static string Hex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
}

/// <summary>
/// Keeps the web view's data ready. A dashboard of its own, never shown, applies the newest snapshot once a
/// second on the UI thread (where names, roles and settings may be read) and is turned into JSON there. The
/// web server's threads only ever read that finished string, so the phone shows exactly what the desktop
/// shows, computed by the same code, without touching UI state from another thread.
///
/// It works only while the web view is on: no clients, no cost.
/// </summary>
public sealed class WebFeed
{
    private static readonly TimeSpan MinimumSpacing = TimeSpan.FromMilliseconds(900);

    private readonly Dispatcher _dispatcher;
    private readonly Func<DashboardViewModel> _createDashboard;
    private readonly Func<IEnumerable<AlertItem>> _alerts;
    private readonly Func<HardwareSnapshot, string> _machine;
    private DashboardViewModel? _dashboard;
    private HardwareSnapshot? _pending;
    private int _scheduled;
    private DateTime _lastBuilt;
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _enabled;

    /// <param name="createDashboard">Makes the hidden dashboard, on first use (UI thread).</param>
    /// <param name="alerts">Recent alerts, newest first (UI thread).</param>
    /// <param name="machine">The machine's name for a snapshot (the motherboard, as in the window header).</param>
    public WebFeed(Dispatcher dispatcher, SensorStream stream, Func<DashboardViewModel> createDashboard,
        Func<IEnumerable<AlertItem>> alerts, Func<HardwareSnapshot, string> machine)
    {
        _dispatcher = dispatcher;
        _createDashboard = createDashboard;
        _alerts = alerts;
        _machine = machine;
        stream.SnapshotPublished += OnSnapshot;
    }

    /// <summary>The latest summary as JSON; null until the first reading after the web view was turned on.</summary>
    public string? Json { get; private set; }

    /// <summary>Turned on and off with the web server.</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            if (!value)
                Json = null;
        }
    }

    /// <summary>Completes when a new summary is ready. For the live stream.</summary>
    public Task Changed => Volatile.Read(ref _changed).Task;

    /// <summary>On the polling thread. Only the newest snapshot is applied, at most about once a second.</summary>
    private void OnSnapshot(HardwareSnapshot snapshot)
    {
        if (!_enabled)
            return;
        Volatile.Write(ref _pending, snapshot);
        if (Interlocked.Exchange(ref _scheduled, 1) == 0)
            _dispatcher.BeginInvoke(Build, DispatcherPriority.Background);
    }

    private void Build()
    {
        Interlocked.Exchange(ref _scheduled, 0);
        if (Interlocked.Exchange(ref _pending, null) is not { } snapshot || !_enabled)
            return;
        if (DateTime.UtcNow - _lastBuilt < MinimumSpacing)
            return;
        _lastBuilt = DateTime.UtcNow;
        Publish(snapshot);
    }

    /// <summary>Applies a snapshot and publishes the summary right away. UI thread; the tests call it directly.</summary>
    internal void Publish(HardwareSnapshot snapshot)
    {
        var dashboard = _dashboard ??= _createDashboard();
        dashboard.Apply(snapshot);
        Json = WebSummary.From(dashboard, _machine(snapshot), snapshot.Timestamp, _alerts()).ToJson();
        Interlocked.Exchange(ref _changed, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
    }
}
