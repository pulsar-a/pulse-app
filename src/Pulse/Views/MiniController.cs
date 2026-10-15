using System.Windows;
using System.Windows.Threading;
using Pulse.Services;
using Pulse.ViewModels;

namespace Pulse.Views;

/// <summary>
/// Opens and closes the mini window (header button, tray menu, global shortcut), puts it back where it was
/// on that monitor, hides it while Pulse's own window is in front (if wanted), and keeps it on top.
/// </summary>
public sealed class MiniController : IDisposable
{
    private static readonly TimeSpan TopmostInterval = TimeSpan.FromSeconds(5);

    private readonly SettingsService _settings;
    private readonly MiniViewModel _viewModel;
    private readonly Action<bool> _setActive;
    private readonly Action _openSettings;
    private readonly GlobalHotkey _hotkey = new();
    private readonly DispatcherTimer _topmost;
    private MiniWindow? _window;
    private bool _wantOpen;
    private bool _pulseInFront;
    private bool _placed;
    private string _hotkeySetting = "";
    private bool? _clickThrough;
    private bool? _shown;

    /// <param name="setActive">Tells the rest of Pulse whether the mini window is showing (keeps readings coming at full speed).</param>
    /// <param name="openSettings">Opens Settings → Mini window.</param>
    public MiniController(SettingsService settings, MiniViewModel viewModel, Action<bool> setActive, Action openSettings)
    {
        _settings = settings;
        _viewModel = viewModel;
        _setActive = setActive;
        _openSettings = openSettings;
        _topmost = new DispatcherTimer { Interval = TopmostInterval };
        _topmost.Tick += (_, _) => { if (_window is { Handle: var h } && h != IntPtr.Zero) NativeWindows.KeepOnTop(h); };

        _hotkey.Pressed += Toggle;
        ApplySettings();
        settings.Changed += ApplySettings;

        if (settings.Current.Mini.Open)
            SetOpen(true);
    }

    /// <summary>What the user asked for: open or closed (it may still be hidden while Pulse is in front).</summary>
    public bool IsOpen => _wantOpen;

    /// <summary>The shortcut in use, or null when it's off or another app has it.</summary>
    public string? Shortcut => _hotkey.Current;

    public event Action? StateChanged;

    /// <summary>While a new shortcut is being recorded, the current one stops working so it can be pressed.</summary>
    public void PauseShortcut(bool paused)
    {
        if (paused)
        {
            _hotkey.Pause();
        }
        else
        {
            _hotkey.Register(_settings.Current.Mini.Hotkey);
            StateChanged?.Invoke();
        }
    }

    public void Toggle() => SetOpen(!_wantOpen);

    public void SetOpen(bool open)
    {
        _wantOpen = open;
        // Opened on purpose (often with the button in Pulse's own window): show it now, even if it's set to
        // hide while Pulse is in front. That resumes the next time Pulse's window comes to the front.
        if (open)
            _pulseInFront = false;
        if (_settings.Current.Mini.Open != open)
            _settings.Update(s => s.Mini.Open = open);
        Refresh();
        StateChanged?.Invoke();
    }

    /// <summary>Pulse's main window came to the front or went to the back.</summary>
    public void SetPulseInFront(bool inFront)
    {
        _pulseInFront = inFront;
        Refresh();
    }

    private void ApplySettings()
    {
        var mini = _settings.Current.Mini;
        if (_window is not null && _clickThrough != mini.ClickThrough)
        {
            _clickThrough = mini.ClickThrough;
            _window.ApplyClickThrough(mini.ClickThrough);
        }
        if (mini.Hotkey != _hotkeySetting)
        {
            _hotkeySetting = mini.Hotkey;
            _hotkey.Register(mini.Hotkey);
            StateChanged?.Invoke();
        }
        Refresh();
    }

    private void Refresh()
    {
        var show = _wantOpen && !(_pulseInFront && _settings.Current.Mini.HideWhenPulseActive);
        if (show)
        {
            if (_window is null)
            {
                _window = new MiniWindow(_viewModel, _settings);
                _clickThrough = _settings.Current.Mini.ClickThrough;
                _window.Moved += Remember;
                _window.SettingsRequested += _openSettings;
                _window.CloseRequested += () => SetOpen(false);
            }
            _viewModel.IsActive = true;
            if (!_window.IsVisible)
            {
                _window.Show();
                if (!_placed) Place();
                _topmost.Start();
            }
        }
        else if (_window is { IsVisible: true })
        {
            _window.Hide();
            _topmost.Stop();
            _viewModel.IsActive = false;
        }
        else
        {
            _viewModel.IsActive = false;
        }
        if (_shown != show)
        {
            _shown = show;
            _setActive(show);
        }
    }

    /// <summary>Back to its spot on the monitor it was last on, or the top-right corner of the main screen.</summary>
    private void Place()
    {
        if (_window is null) return;
        _window.UpdateLayout();
        var bounds = NativeWindows.Bounds(_window.Handle);
        var spot = MiniPlacement.Choose(NativeWindows.Monitors(), _settings.Current.Mini, bounds.Size);
        NativeWindows.MoveTo(_window.Handle, (int)spot.X, (int)spot.Y);
        _placed = true;
    }

    /// <summary>Saves where it was dragged, for the monitor it's on now (in physical pixels).</summary>
    private void Remember()
    {
        if (_window is null || NativeWindows.MonitorOf(_window.Handle) is not { } monitor) return;
        var bounds = NativeWindows.Bounds(_window.Handle);
        _settings.Update(s =>
        {
            s.Mini.Positions[monitor.Key] = new MiniPosition { X = bounds.X, Y = bounds.Y };
            s.Mini.Monitor = monitor.Key;
        });
    }

    public void Dispose()
    {
        _topmost.Stop();
        _hotkey.Dispose();
        _window?.Close();
    }
}
