using System.Runtime.InteropServices;
using System.Windows;

namespace Pulse.Services;

/// <summary>The few Win32 window calls WPF doesn't offer: extended styles, monitors, topmost, hotkeys.</summary>
internal static class NativeWindows
{
    private const int GWL_EXSTYLE = -20;
    public const int WS_EX_TRANSPARENT = 0x20;
    public const int WS_EX_TOOLWINDOW = 0x80;
    public const int WS_EX_LAYERED = 0x80000;
    public const int WS_EX_NOACTIVATE = 0x8000000;

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    public static void SetExtendedStyle(IntPtr hwnd, int flags, bool on)
    {
        var style = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, on ? style | flags : style & ~flags);
    }

    /// <summary>Puts the window back on top without moving or activating it (games like to push in front).</summary>
    public static void KeepOnTop(IntPtr hwnd) => SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

    public static void MoveTo(IntPtr hwnd, int x, int y) => SetWindowPos(hwnd, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);

    /// <summary>The window's bounds in physical pixels.</summary>
    public static Rect Bounds(IntPtr hwnd) =>
        GetWindowRect(hwnd, out var r) ? new Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top) : Rect.Empty;

    /// <summary>Every monitor's work area (the screen minus the taskbar) in physical pixels.</summary>
    public static List<MonitorArea> Monitors()
    {
        var list = new List<MonitorArea>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            if (Info(monitor) is { } area) list.Add(area);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>The monitor most of the window is on.</summary>
    public static MonitorArea? MonitorOf(IntPtr hwnd) => Info(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST));

    private static MonitorArea? Info(IntPtr monitor)
    {
        var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        if (!GetMonitorInfo(monitor, ref info)) return null;
        var m = info.rcMonitor;
        var w = info.rcWork;
        // The device name plus the monitor's rectangle tells monitors (and arrangements) apart.
        var key = $"{info.szDevice} {m.Left},{m.Top},{m.Right - m.Left}x{m.Bottom - m.Top}";
        return new MonitorArea(key, new Rect(w.Left, w.Top, w.Right - w.Left, w.Bottom - w.Top), (info.dwFlags & 1) != 0);
    }

    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEX info);

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }
}
