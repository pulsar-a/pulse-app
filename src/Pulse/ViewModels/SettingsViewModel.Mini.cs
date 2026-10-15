using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Pulse.Services;
using Pulse.Views;

namespace Pulse.ViewModels;

/// <summary>One readout in Settings → Mini window, with buttons to move or remove it.</summary>
public sealed record MiniItemRow(string Label, string Detail, IRelayCommand MoveUp, IRelayCommand MoveDown, IRelayCommand Remove);

/// <summary>Settings → Mini window.</summary>
public partial class SettingsViewModel
{
    private MiniController? _mini;
    private Action? _addMiniSensor;

    /// <summary>Hooks up the mini window once it exists (it's created after this page's view model).</summary>
    public void AttachMini(MiniController mini, Action addSensor)
    {
        _mini = mini;
        _addMiniSensor = addSensor;
        mini.StateChanged += RefreshMini;
        _settings.Changed += RefreshMini;
        RefreshMini();
    }

    public bool HasMini => _mini is not null;

    public bool MiniOpen
    {
        get => _mini?.IsOpen ?? false;
        set => _mini?.SetOpen(value);
    }

    public bool MiniStacked
    {
        get => !_settings.Current.Mini.Horizontal;
        set { if (value) _settings.Update(s => s.Mini.Horizontal = false); }
    }

    public bool MiniSideBySide
    {
        get => _settings.Current.Mini.Horizontal;
        set { if (value) _settings.Update(s => s.Mini.Horizontal = true); }
    }

    /// <summary>40 to 100.</summary>
    public double MiniOpacityPercent
    {
        get => Math.Round(_settings.Current.Mini.Opacity * 100);
        set
        {
            var opacity = Math.Clamp(Math.Round(value) / 100, 0.4, 1);
            if (Math.Abs(opacity - _settings.Current.Mini.Opacity) > 0.001)
                _settings.Update(s => s.Mini.Opacity = opacity);
        }
    }

    public bool MiniClickThrough
    {
        get => _settings.Current.Mini.ClickThrough;
        set => _settings.Update(s => s.Mini.ClickThrough = value);
    }

    public bool MiniHideWhenPulseActive
    {
        get => _settings.Current.Mini.HideWhenPulseActive;
        set => _settings.Update(s => s.Mini.HideWhenPulseActive = value);
    }

    // Shortcut: shown as "Ctrl + Alt + P"; "Set" records the next keys pressed, "Reset" goes back to the default.

    [ObservableProperty] public partial bool IsRecordingShortcut { get; private set; }

    /// <summary>What the shortcut box says: the shortcut, "Off", or the keys held so far while recording.</summary>
    [ObservableProperty] public partial string ShortcutText { get; private set; } = "";

    /// <summary>A hint under the box while recording, or why the last keys didn't work.</summary>
    [ObservableProperty] public partial string? ShortcutHint { get; private set; }

    public bool CanResetShortcut => _settings.Current.Mini.Hotkey != MiniSettings.DefaultHotkey;

    private const string RecordingHint = "Press the keys, for example Ctrl + Alt + P. Esc cancels; Backspace turns the shortcut off.";

    [RelayCommand]
    private void SetShortcut()
    {
        if (IsRecordingShortcut)
        {
            StopRecording();
            return;
        }
        IsRecordingShortcut = true;
        ShortcutText = "Press keys…";
        ShortcutHint = RecordingHint;
        _mini?.PauseShortcut(true);
    }

    [RelayCommand]
    private void ResetShortcut()
    {
        StopRecording();
        _settings.Update(s => s.Mini.Hotkey = MiniSettings.DefaultHotkey);
    }

    /// <summary>
    /// A key pressed while recording. Modifiers alone show what's held so far; a key with Ctrl, Alt or Win
    /// becomes the shortcut. Returns true when the key was used (so it doesn't reach the rest of the page).
    /// </summary>
    public bool RecordShortcutKey(ModifierKeys modifiers, Key key)
    {
        if (!IsRecordingShortcut) return false;

        if (key == Key.Escape && modifiers == ModifierKeys.None)
        {
            StopRecording();
            return true;
        }
        if (key is Key.Back or Key.Delete && modifiers == ModifierKeys.None)
        {
            StopRecording();
            _settings.Update(s => s.Mini.Hotkey = "");
            return true;
        }

        var (shortcut, problem) = GlobalHotkey.FromKeys(modifiers, key);
        if (shortcut is null)
        {
            // Only modifiers so far (or keys that can't be a shortcut): show what's held and keep listening.
            ShortcutText = modifiers == ModifierKeys.None
                ? "Press keys…"
                : GlobalHotkey.Display(string.Join("+", Held(modifiers))) + " + …";
            ShortcutHint = problem ?? RecordingHint;
            return true;
        }

        StopRecording();
        _settings.Update(s => s.Mini.Hotkey = shortcut);
        return true;
    }

    private static IEnumerable<string> Held(ModifierKeys modifiers)
    {
        if (modifiers.HasFlag(ModifierKeys.Control)) yield return "Ctrl";
        if (modifiers.HasFlag(ModifierKeys.Shift)) yield return "Shift";
        if (modifiers.HasFlag(ModifierKeys.Alt)) yield return "Alt";
        if (modifiers.HasFlag(ModifierKeys.Windows)) yield return "Win";
    }

    private void StopRecording()
    {
        if (!IsRecordingShortcut) return;
        IsRecordingShortcut = false;
        ShortcutHint = null;
        _mini?.PauseShortcut(false);
        RefreshMini();
    }

    /// <summary>Set when Windows refused the shortcut because another app already uses it.</summary>
    [ObservableProperty] public partial string? HotkeyProblem { get; private set; }

    public ObservableCollection<MiniItemRow> MiniRows { get; } = [];

    [RelayCommand]
    private void AddMiniSensor() => _addMiniSensor?.Invoke();

    [RelayCommand]
    private void ResetMiniItems() => _settings.Update(s => s.Mini.Items = null);

    /// <summary>Adds a sensor to the end of the mini window (once).</summary>
    public static void AddMiniItem(SettingsService settings, string sensorId) => EditMiniItems(settings, items =>
    {
        if (!items.Contains(sensorId)) items.Add(sensorId);
    });

    private static void EditMiniItems(SettingsService settings, Action<List<string>> edit) => settings.Update(s =>
    {
        var items = s.Mini.ItemsOrDefault.ToList();
        edit(items);
        s.Mini.Items = items;
    });

    private void RefreshMini()
    {
        if (_mini is null) return;
        var items = _settings.Current.Mini.ItemsOrDefault;
        var rows = items.Select((item, i) => new MiniItemRow(
            MiniItems.IsAuto(item) ? MiniItems.Describe(item) : _catalog.Find(item)?.DisplayName ?? _settings.Current.Sensors.Names.GetValueOrDefault(item) ?? item,
            MiniItems.IsAuto(item) ? "Automatic: follows this PC's hardware" : _catalog.Find(item)?.HardwareName ?? "Not detected right now",
            new RelayCommand(() => EditMiniItems(_settings, list => Swap(list, i, i - 1)), () => i > 0),
            new RelayCommand(() => EditMiniItems(_settings, list => Swap(list, i, i + 1)), () => i < items.Count - 1),
            new RelayCommand(() => EditMiniItems(_settings, list => list.RemoveAt(i))))).ToList();

        if (!MiniRows.Select(r => (r.Label, r.Detail)).SequenceEqual(rows.Select(r => (r.Label, r.Detail))))
        {
            MiniRows.Clear();
            foreach (var row in rows) MiniRows.Add(row);
        }

        var hotkey = _settings.Current.Mini.Hotkey;
        HotkeyProblem = hotkey.Length > 0 && _mini.Shortcut is null && !IsRecordingShortcut
            ? $"Another app is already using {GlobalHotkey.Display(hotkey)}. Set a different one."
            : null;
        if (!IsRecordingShortcut)
            ShortcutText = GlobalHotkey.Display(hotkey);

        OnPropertyChanged(nameof(MiniOpen));
        OnPropertyChanged(nameof(MiniStacked));
        OnPropertyChanged(nameof(MiniSideBySide));
        OnPropertyChanged(nameof(MiniOpacityPercent));
        OnPropertyChanged(nameof(MiniClickThrough));
        OnPropertyChanged(nameof(MiniHideWhenPulseActive));
        OnPropertyChanged(nameof(CanResetShortcut));
        OnPropertyChanged(nameof(HasMini));
    }

    private static void Swap(List<string> list, int a, int b)
    {
        if (a < 0 || b < 0 || a >= list.Count || b >= list.Count) return;
        (list[a], list[b]) = (list[b], list[a]);
    }
}
