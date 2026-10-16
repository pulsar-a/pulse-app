using System.Runtime.InteropServices;
using System.Text;

namespace Pulse.Services.Processes;

/// <summary>
/// Which processes have a window on the desktop, the way Task Manager decides what's an "app": visible,
/// not owned by another window, not a tool window, not hidden by Windows (cloaked: on another virtual
/// desktop, or a suspended Store app), and titled. The shell's own desktop and taskbar don't count.
/// One EnumWindows pass, well under a millisecond.
/// </summary>
internal static class DesktopWindows
{
    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW",
    };

    public static HashSet<int> Pids()
    {
        var pids = new HashSet<int>();
        var className = new StringBuilder(256);
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || GetWindow(hwnd, GwOwner) != IntPtr.Zero
                || ((long)GetWindowLongPtr(hwnd, GwlExStyle) & WsExToolWindow) != 0
                || GetWindowTextLength(hwnd) == 0 || IsCloaked(hwnd))
                return true;

            className.Clear();
            GetClassName(hwnd, className, className.Capacity);
            var cls = className.ToString();
            if (ShellClasses.Contains(cls))
                return true;

            GetWindowThreadProcessId(hwnd, out var pid);
            // A Store app's frame belongs to ApplicationFrameHost; the app itself owns the window inside it.
            if (cls == "ApplicationFrameWindow" && InnerAppPid(hwnd, (int)pid) is { } inner)
                pid = (uint)inner;
            pids.Add((int)pid);
            return true;
        }, IntPtr.Zero);
        return pids;
    }

    private static int? InnerAppPid(IntPtr frame, int framePid)
    {
        int? found = null;
        EnumChildWindows(frame, (child, _) =>
        {
            GetWindowThreadProcessId(child, out var pid);
            if ((int)pid == framePid)
                return true;
            found = (int)pid;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    private static bool IsCloaked(IntPtr hwnd) =>
        DwmGetWindowAttribute(hwnd, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0;

    private const int GwOwner = 4;
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x80;
    private const int DwmwaCloaked = 14;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
}
