using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Pulse.Services.Processes;

/// <summary>What a program is called and looks like: "Google Chrome" and its icon, rather than chrome.exe.</summary>
/// <param name="Icon">A frozen 16 px icon, safe to use from any thread; null when the file can't be read.</param>
public sealed record AppInfo(string DisplayName, string? Path, ImageSource? Icon);

/// <summary>
/// Looks up each program's description and icon once, on the sampling thread, and keeps them. Reading a
/// file's version info and icon takes a few ms, so only a limited number of new programs are looked up per
/// sample; the rest follow on the next ones.
/// </summary>
internal sealed class AppInfoCache
{
    private const int LookupsPerSample = 24;
    private readonly ConcurrentDictionary<string, AppInfo> _known = new();

    public AppInfo? Find(string key) => _known.GetValueOrDefault(key);

    /// <summary>
    /// Looks up programs not seen before. The top few by CPU, GPU and memory go first (they're the ones on
    /// the dashboard tile), then the rest busiest first, so what's on screen gets its icon soonest.
    /// </summary>
    public void Resolve(IReadOnlyList<AppUsage> apps)
    {
        const int top = 5;
        var order = apps.OrderByDescending(a => a.Cpu).Take(top)
            .Concat(apps.OrderByDescending(a => a.Gpu ?? 0).Take(top))
            .Concat(apps.OrderByDescending(a => a.Memory).Take(top))
            .Concat(apps.OrderByDescending(a => a.Cpu + (a.Gpu ?? 0)));
        var lookups = 0;
        foreach (var app in order)
        {
            if (_known.ContainsKey(app.Key))
                continue;
            if (lookups++ >= LookupsPerSample)
                return;
            _known[app.Key] = Look(app);
        }
    }

    private static AppInfo Look(AppUsage app)
    {
        var fallback = Path.GetFileNameWithoutExtension(app.ImageName) is { Length: > 0 } stem ? stem : app.ImageName;
        string? path = null;
        foreach (var member in app.Members)
        {
            if ((path = NativeProcesses.ImagePath(member.Pid)) is not null)
                break;
        }
        if (path is null)
            return new AppInfo(fallback, null, null);

        string? description = null;
        try
        {
            description = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim();
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException)
        {
        }
        return new AppInfo(description is { Length: > 0 } ? description : fallback, path, Icon(path));
    }

    private static ImageSource? Icon(string path)
    {
        var info = new SHFILEINFO();
        if (SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), ShgfiIcon | ShgfiSmallIcon) == IntPtr.Zero
            || info.hIcon == IntPtr.Zero)
            return null;
        try
        {
            var image = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }

    private const uint ShgfiIcon = 0x100;
    private const uint ShgfiSmallIcon = 0x1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref SHFILEINFO info, uint size, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);
}
