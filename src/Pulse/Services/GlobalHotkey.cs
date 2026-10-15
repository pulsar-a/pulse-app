using System.Windows.Input;
using System.Windows.Interop;

namespace Pulse.Services;

/// <summary>
/// One system-wide keyboard shortcut (Settings → Mini window), heard through a hidden message-only window,
/// so it works while Pulse is in the tray and its main window has never been shown.
/// </summary>
public sealed class GlobalHotkey : IDisposable
{
    private const int Id = 0x5055;   // "PU"
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

    private readonly HwndSource _source;
    private bool _registered;

    public GlobalHotkey()
    {
        _source = new HwndSource(new HwndSourceParameters("PulseHotkey") { ParentWindow = new IntPtr(-3) });   // HWND_MESSAGE
        _source.AddHook(WndProc);
    }

    public event Action? Pressed;

    /// <summary>The shortcut now in use, or null when none (off, or another app already has it).</summary>
    public string? Current { get; private set; }

    /// <summary>Lets go of the shortcut for a moment (while a new one is recorded, so pressing it isn't swallowed).</summary>
    public void Pause()
    {
        if (!_registered) return;
        NativeWindows.UnregisterHotKey(_source.Handle, Id);
        _registered = false;
    }

    /// <summary>Switches to a shortcut like "Ctrl+Alt+P" (empty turns it off). False if Windows refused it.</summary>
    public bool Register(string? shortcut)
    {
        if (_registered)
        {
            NativeWindows.UnregisterHotKey(_source.Handle, Id);
            _registered = false;
            Current = null;
        }
        if (Parse(shortcut) is not { } parsed)
            return string.IsNullOrEmpty(shortcut);

        _registered = NativeWindows.RegisterHotKey(_source.Handle, Id, parsed.Modifiers | MOD_NOREPEAT,
            (uint)KeyInterop.VirtualKeyFromKey(parsed.Key));
        Current = _registered ? shortcut : null;
        Log.Write(_registered ? $"Shortcut {shortcut} registered" : $"Shortcut {shortcut} is taken by another app");
        return _registered;
    }

    /// <summary>
    /// Turns keys pressed while recording into a shortcut ("Ctrl+Alt+P"), or says why they can't be one.
    /// It needs Ctrl, Alt or Win: Shift alone with a key would take capital letters away from typing.
    /// </summary>
    public static (string? Shortcut, string? Problem) FromKeys(ModifierKeys modifiers, Key key)
    {
        if (IsModifier(key))
            return (null, null);
        if ((modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) == 0)
            return (null, "Add Ctrl, Alt or Win, so the shortcut doesn't take a key away from typing.");
        if (KeyInterop.VirtualKeyFromKey(key) == 0)
            return (null, "That key can't be used in a shortcut.");

        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(key.ToString());
        return (string.Join("+", parts), null);
    }

    public static bool IsModifier(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

    /// <summary>How a shortcut reads on screen: "Ctrl + Alt + P", "Ctrl + Shift + 1", "Off".</summary>
    public static string Display(string? shortcut) =>
        string.IsNullOrEmpty(shortcut)
            ? "Off"
            : string.Join(" + ", shortcut.Split('+').Select(DisplayKey));

    private static string DisplayKey(string part) => part switch
    {
        _ when part.Length == 2 && part[0] == 'D' && char.IsDigit(part[1]) => part[1..],
        _ when part.StartsWith("NumPad", StringComparison.Ordinal) => "Num " + part[6..],
        "Oem3" or "OemTilde" => "`",
        "OemMinus" => "-",
        "OemPlus" => "=",
        "OemComma" => ",",
        "OemPeriod" => ".",
        "Next" => "Page Down",
        "Prior" => "Page Up",
        "Return" => "Enter",
        "Back" => "Backspace",
        _ => part,
    };

    /// <summary>"Ctrl+Alt+P" → (MOD_CONTROL | MOD_ALT, P). Null for empty or unreadable text, or no modifier.</summary>
    public static (uint Modifiers, Key Key)? Parse(string? shortcut)
    {
        if (string.IsNullOrWhiteSpace(shortcut)) return null;
        uint modifiers = 0;
        Key? key = null;
        foreach (var part in shortcut.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= MOD_CONTROL; break;
                case "alt": modifiers |= MOD_ALT; break;
                case "shift": modifiers |= MOD_SHIFT; break;
                case "win": modifiers |= MOD_WIN; break;
                default:
                    if (key is not null || !Enum.TryParse<Key>(part, ignoreCase: true, out var k)) return null;
                    key = k;
                    break;
            }
        }
        // A bare key would steal that key from every app.
        return modifiers != 0 && key is { } found ? (modifiers, found) : null;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == Id)
        {
            handled = true;
            Pressed?.Invoke();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_registered) NativeWindows.UnregisterHotKey(_source.Handle, Id);
        _source.Dispose();
    }
}
