using System.Windows;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Pulse.Services;

/// <summary>Settings → Appearance.</summary>
public sealed class AppearanceSettings
{
    public const string MatchWindows = "System";
    public const string Dark = "Dark";
    public const string Light = "Light";

    /// <summary><see cref="MatchWindows"/> (default), <see cref="Dark"/> or <see cref="Light"/>.</summary>
    public string Theme { get; set; } = MatchWindows;
}

/// <summary>
/// Switches between the dark and light themes. Two layers change together: WPF-UI's own resources
/// (controls, text colors, Mica), and Pulse's tokens (Themes/Dark.xaml or Themes/Light.xaml). Every
/// view uses DynamicResource, so the switch is instant and nothing is rebuilt.
/// In "match Windows" mode, WPF-UI's watcher follows the Windows setting and this follows WPF-UI.
/// </summary>
public static class ThemeService
{
    private static Window? _watched;
    private static bool _hooked;

    /// <summary>The theme in use right now.</summary>
    public static ApplicationTheme Current { get; private set; } = ApplicationTheme.Dark;

    public static void Apply(string choice, Window? mainWindow = null)
    {
        if (!_hooked)
        {
            // Windows switched (in match-Windows mode): bring Pulse's tokens along.
            ApplicationThemeManager.Changed += (theme, _) => UseTokens(theme);
            _hooked = true;
        }

        if (_watched is not null && (choice != AppearanceSettings.MatchWindows || mainWindow != _watched))
        {
            SystemThemeWatcher.UnWatch(_watched);
            _watched = null;
        }

        var theme = Resolve(choice, ApplicationThemeManager.GetSystemTheme());
        ApplicationThemeManager.Apply(theme, WindowBackdropType.Mica, true);
        UseTokens(theme);

        if (choice == AppearanceSettings.MatchWindows && mainWindow is not null && _watched is null)
        {
            SystemThemeWatcher.Watch(mainWindow, WindowBackdropType.Mica, true);
            _watched = mainWindow;
        }
    }

    /// <summary>What a choice means given the Windows setting. High contrast themes count as dark or light by their background.</summary>
    public static ApplicationTheme Resolve(string choice, SystemTheme system) => choice switch
    {
        AppearanceSettings.Light => ApplicationTheme.Light,
        AppearanceSettings.Dark => ApplicationTheme.Dark,
        _ => system is SystemTheme.Light or SystemTheme.HCWhite or SystemTheme.Sunrise or SystemTheme.Flow
            ? ApplicationTheme.Light
            : ApplicationTheme.Dark,
    };

    /// <summary>Swaps Pulse's token dictionary in the app's resources for the one matching the theme.</summary>
    private static void UseTokens(ApplicationTheme theme)
    {
        if (Application.Current is not { } app)
            return;
        var name = theme == ApplicationTheme.Light ? "Light" : "Dark";
        Current = theme == ApplicationTheme.Light ? ApplicationTheme.Light : ApplicationTheme.Dark;

        var dictionaries = app.Resources.MergedDictionaries;
        for (var i = 0; i < dictionaries.Count; i++)
        {
            var source = dictionaries[i].Source?.OriginalString ?? "";
            if (!source.EndsWith("Themes/Dark.xaml", StringComparison.OrdinalIgnoreCase)
                && !source.EndsWith("Themes/Light.xaml", StringComparison.OrdinalIgnoreCase))
                continue;
            if (source.EndsWith($"Themes/{name}.xaml", StringComparison.OrdinalIgnoreCase))
                return;
            dictionaries[i] = new ResourceDictionary { Source = new Uri($"pack://application:,,,/Pulse;component/Themes/{name}.xaml") };
            Log.Write($"Theme: {name}");
            return;
        }
    }
}
