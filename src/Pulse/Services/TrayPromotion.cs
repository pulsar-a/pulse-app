using Microsoft.Win32;

namespace Pulse.Services;

public enum PromotionResult
{
    /// <summary>Windows hasn't recorded this icon yet (it does so a moment after the icon first appears).</summary>
    NotFound,
    Unchanged,
    Changed,
    /// <summary>Not Windows 11's notification area settings (Windows 10 keeps them in an opaque blob).</summary>
    Unsupported,
}

/// <summary>
/// Keeps Pulse's tray icon next to the clock instead of in the ^ overflow.
///
/// Windows 11 hides every new tray icon by default, and it tells icons apart by the path of the program.
/// A new build in another folder, a published copy, or a framework change in the output path all look
/// like a new app, so the "always show" choice made for one copy doesn't carry to the next. There's no
/// API for this; Windows keeps the choice in HKCU\Control Panel\NotifyIconSettings (one key per icon,
/// with ExecutablePath and IsPromoted), the same value the taskbar settings page writes. Pulse sets it for
/// its own entry each time it starts, when "Always show it next to the clock" is on.
/// </summary>
public static class TrayPromotion
{
    public const string KeyPath = @"Control Panel\NotifyIconSettings";

    /// <param name="root">The user's registry hive; tests pass a scratch key.</param>
    public static PromotionResult Apply(RegistryKey root, string executablePath, bool show)
    {
        using var settings = root.OpenSubKey(KeyPath, writable: true);
        if (settings is null)
            return PromotionResult.Unsupported;

        var result = PromotionResult.NotFound;
        foreach (var name in settings.GetSubKeyNames())
        {
            using var icon = settings.OpenSubKey(name, writable: true);
            if (icon?.GetValue("ExecutablePath") is not string path
                || !string.Equals(path, executablePath, StringComparison.OrdinalIgnoreCase))
                continue;

            var wanted = show ? 1 : 0;
            if (icon.GetValue("IsPromoted") is int current && current == wanted)
            {
                if (result == PromotionResult.NotFound) result = PromotionResult.Unchanged;
                continue;
            }
            icon.SetValue("IsPromoted", wanted, RegistryValueKind.DWord);
            result = PromotionResult.Changed;
        }
        return result;
    }
}
