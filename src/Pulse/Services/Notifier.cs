using System.IO;
using System.Security;
using System.Windows;
using Microsoft.Win32;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace Pulse.Services;

/// <summary>
/// Windows toast notifications for alerts. An unpackaged app needs an identity for toasts; Pulse
/// registers one per user (HKCU\Software\Classes\AppUserModelId\Pulse.HardwareMonitor) with its name
/// and icon. Critical alerts stay on screen until dismissed and play an alarm sound. If toasts fail
/// (a locked-down machine, say), the tray balloon is used instead.
/// </summary>
/// <summary>Shows alert notifications. An interface so tests don't pop real toasts.</summary>
public interface INotifier
{
    void Show(string title, string message, AlertSeverity severity, bool playSound);
}

public sealed class Notifier : INotifier
{
    public const string AppUserModelId = "Pulse.HardwareMonitor";

    private readonly Action<string, string> _fallback;
    private bool _registered;

    /// <param name="fallback">Shows a tray balloon (title, message) when a toast can't be shown.</param>
    public Notifier(Action<string, string> fallback) => _fallback = fallback;

    /// <summary>Raised (on a background thread) when the user clicks a notification or its "Open Pulse" button.</summary>
    public event Action? OpenRequested;

    public void Show(string title, string message, AlertSeverity severity, bool playSound)
    {
        try
        {
            EnsureRegistered();
            var xml = new XmlDocument();
            xml.LoadXml(BuildToastXml(title, message, severity, playSound));
            var toast = new ToastNotification(xml) { Group = "alerts" };
            toast.Activated += (_, args) =>
            {
                if ((args as ToastActivatedEventArgs)?.Arguments != "dismiss")
                    OpenRequested?.Invoke();
            };
            ToastNotificationManager.CreateToastNotifier(AppUserModelId).Show(toast);
        }
        catch (Exception ex)
        {
            Log.Write($"Toast failed, using the tray instead: {ex.Message}");
            _fallback(title, message);
        }
    }

    /// <summary>The toast definition. Public for tests.</summary>
    public static string BuildToastXml(string title, string message, AlertSeverity severity, bool playSound)
    {
        var critical = severity == AlertSeverity.Critical;
        // "reminder" keeps the toast on screen until the user acts on it; it requires at least one button.
        var scenario = critical ? " scenario=\"reminder\"" : "";
        var audio = !playSound ? "<audio silent=\"true\"/>"
            : critical ? "<audio src=\"ms-winsoundevent:Notification.Looping.Alarm2\" loop=\"false\"/>"
            : "";
        return $"""
            <toast launch="open"{scenario}>
              <visual>
                <binding template="ToastGeneric">
                  <text>{SecurityElement.Escape(title)}</text>
                  <text>{SecurityElement.Escape(message)}</text>
                </binding>
              </visual>
              <actions>
                <action content="Open Pulse" arguments="open" activationType="foreground"/>
                <action content="Dismiss" arguments="dismiss" activationType="system"/>
              </actions>
              {audio}
            </toast>
            """;
    }

    /// <summary>Registers the toast identity (name and icon) for the current user. Cheap to call again.</summary>
    private void EnsureRegistered()
    {
        if (_registered)
            return;

        var iconPath = Path.Combine(SettingsService.DefaultFolder, "pulse-icon.png");
        if (!File.Exists(iconPath))
        {
            Directory.CreateDirectory(SettingsService.DefaultFolder);
            using var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Resources/Pulse-256.png"))?.Stream;
            if (resource is not null)
            {
                using var file = File.Create(iconPath);
                resource.CopyTo(file);
            }
        }

        using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{AppUserModelId}");
        key.SetValue("DisplayName", "Pulse");
        if (File.Exists(iconPath))
            key.SetValue("IconUri", iconPath);
        _registered = true;
    }
}
