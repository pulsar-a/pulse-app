using System.Windows.Threading;
using System.Xml.Linq;
using LibreHardwareMonitor.Hardware;
using Pulse.Models;
using Pulse.Services;
using Pulse.ViewModels;
using static Pulse.Tests.Snap;

namespace Pulse.Tests;

internal sealed class FakeNotifier : INotifier
{
    public List<(string Title, string Message, AlertSeverity Severity, bool Sound)> Shown { get; } = [];

    public void Show(string title, string message, AlertSeverity severity, bool playSound) =>
        Shown.Add((title, message, severity, playSound));
}

public class AlertCenterTests : PulseTest
{
    private const string PumpId = "/lpc/nct6798d/0/fan/1";
    private static readonly DateTime T0 = new(2026, 10, 15, 12, 0, 0);

    private static HardwareSnapshot Pump(float rpm) => Of(
        Hardware("/lpc/nct6798d/0", "Nuvoton NCT6798D", HardwareType.SuperIO, Sensor(PumpId, "Fan #2", SensorType.Fan, rpm)));

    private AlertCenter NewCenter(out FakeNotifier notifier, out SettingsService settings)
    {
        var catalog = NewCatalog(out settings);
        catalog.SetRole(SensorRoles.Pump, PumpId);
        notifier = new FakeNotifier();
        return new AlertCenter(settings, catalog, new SensorStream(), notifier, Dispatcher.CurrentDispatcher);
    }

    [Fact]
    public void A_pump_failure_notifies_lists_the_alert_and_turns_critical()
    {
        var center = NewCenter(out var notifier, out _);
        center.Evaluate(Pump(2500), T0);
        for (var s = 1; s <= 4; s++)
            center.Evaluate(Pump(0), T0.AddSeconds(s));

        var shown = Assert.Single(notifier.Shown);
        Assert.Equal("Pump stopped", shown.Title);
        Assert.Equal(AlertSeverity.Critical, shown.Severity);
        Assert.True(shown.Sound);
        Assert.True(center.IsCriticalActive);
        Assert.Equal(1, center.UnreadCount);
        Assert.Equal("Pump stopped", Assert.Single(center.Items).Title);

        center.Evaluate(Pump(2500), T0.AddSeconds(5));
        Assert.False(center.IsCriticalActive);
        Assert.True(center.Items[0].IsCleared);
    }

    [Fact]
    public void The_alarm_sound_can_be_turned_off()
    {
        var center = NewCenter(out var notifier, out var settings);
        settings.Update(s => s.Alerts.PlaySoundForCritical = false);
        center.Evaluate(Pump(2500), T0);
        for (var s = 1; s <= 4; s++)
            center.Evaluate(Pump(0), T0.AddSeconds(s));

        Assert.False(Assert.Single(notifier.Shown).Sound);
    }

    [Fact]
    public void Opening_the_bell_marks_alerts_read_and_clear_empties_the_list()
    {
        var center = NewCenter(out _, out _);
        center.Evaluate(Pump(2500), T0);
        for (var s = 1; s <= 4; s++)
            center.Evaluate(Pump(0), T0.AddSeconds(s));

        center.MarkReadCommand.Execute(null);
        Assert.False(center.HasUnread);

        center.ClearCommand.Execute(null);
        Assert.Empty(center.Items);
        Assert.False(center.HasItems);
    }

    [Fact]
    public void Temperature_thresholds_are_edited_in_the_users_unit_but_stored_in_Celsius()
    {
        SensorFormat.TemperatureUnit = TemperatureUnit.Fahrenheit;
        var catalog = NewCatalog(out var settings);
        var cpuRule = settings.Current.Alerts.Rules.Single(r => r.Id == "builtin.cpu");
        var row = new AlertRuleViewModel(cpuRule, settings, catalog, _ => { });

        Assert.Equal(203, row.Threshold);
        Assert.Equal("°F", row.UnitText);

        row.Threshold = 194;
        Assert.Equal(90, cpuRule.Threshold, precision: 6);
    }

    [Fact]
    public void Built_in_rules_cannot_be_deleted()
    {
        var catalog = NewCatalog(out var settings);
        var pumpRule = settings.Current.Alerts.Rules.Single(r => r.Id == "builtin.pump");
        var row = new AlertRuleViewModel(pumpRule, settings, catalog, _ => { });

        Assert.False(row.CanDelete);
        Assert.False(row.DeleteCommand.CanExecute(null));
        Assert.Equal("rpm", row.UnitText);
    }

    /// <summary>Pulse closed and started again: a new alert center over the same settings.</summary>
    private static AlertCenter Restart(SettingsService settings, FakeNotifier notifier) =>
        new(settings, new SensorCatalog(settings), new SensorStream(), notifier, Dispatcher.CurrentDispatcher);

    [Fact]
    public void A_changed_cooler_warns_once_instead_of_sounding_the_alarm()
    {
        var center = NewCenter(out var notifier, out var settings);
        for (var s = 0; s < 60; s++)
            center.Evaluate(PumpWatchTests.OldCooler(s), T0.AddSeconds(s));

        // The new AIO's radiator fans now sit on the old pump header, hovering around the 500 rpm limit.
        var restarted = Restart(settings, notifier);
        var start = T0.AddHours(3);
        for (var s = 0; s < 600; s++)
            restarted.Evaluate(PumpWatchTests.NewCooler(s), start.AddSeconds(s));

        var shown = Assert.Single(notifier.Shown);
        Assert.Equal("Pump reading changed", shown.Title);
        Assert.Equal(AlertSeverity.Warning, shown.Severity);
        Assert.Contains("Fan #6 started spinning at 2,100 rpm. If you moved the pump there, choose it in the Cooling card.", shown.Message);
        Assert.False(restarted.IsCriticalActive);
        Assert.Single(restarted.Items);

        // Choosing the suggested header settles it, quietly.
        restarted.Pump.Use(restarted.Pump.Change!.SuggestedId!);
        restarted.Evaluate(PumpWatchTests.NewCooler(600), start.AddSeconds(600));
        Assert.True(restarted.Items[0].IsCleared);
        Assert.Single(notifier.Shown);
        Assert.False(restarted.IsCriticalActive);
    }

    [Fact]
    public void A_pump_stopped_when_Pulse_starts_still_sounds_the_alarm_once_checked()
    {
        var center = NewCenter(out var notifier, out var settings);
        for (var s = 0; s < 60; s++)
            center.Evaluate(PumpWatchTests.OldCooler(s), T0.AddSeconds(s));

        var restarted = Restart(settings, notifier);
        for (var s = 0; s < 20; s++)
            restarted.Evaluate(PumpWatchTests.Board(0), T0.AddHours(3).AddSeconds(s));

        var shown = Assert.Single(notifier.Shown);
        Assert.Equal("Pump stopped", shown.Title);
        Assert.Equal(AlertSeverity.Critical, shown.Severity);
        Assert.True(restarted.IsCriticalActive);
    }
}

public class ToastXmlTests
{
    [Fact]
    public void Critical_toasts_stay_on_screen_with_an_alarm_and_an_open_button()
    {
        var toast = XDocument.Parse(Notifier.BuildToastXml("Pump stopped", "Pump: 0 RPM", AlertSeverity.Critical, playSound: true)).Root!;

        Assert.Equal("reminder", toast.Attribute("scenario")?.Value);
        Assert.Contains("Alarm", toast.Element("audio")!.Attribute("src")!.Value);
        Assert.Contains(toast.Descendants("action"), a => a.Attribute("content")!.Value == "Open Pulse");
    }

    [Fact]
    public void Warnings_are_ordinary_toasts_and_text_is_escaped()
    {
        var toast = XDocument.Parse(Notifier.BuildToastXml("Disk <C:> & co", "too hot", AlertSeverity.Warning, playSound: true)).Root!;

        Assert.Null(toast.Attribute("scenario"));
        Assert.Equal("Disk <C:> & co", toast.Descendants("text").First().Value);
    }

    [Fact]
    public void Silent_toasts_say_so()
    {
        var toast = XDocument.Parse(Notifier.BuildToastXml("t", "m", AlertSeverity.Critical, playSound: false)).Root!;
        Assert.Equal("true", toast.Element("audio")!.Attribute("silent")!.Value);
    }
}
