using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Pulse.Services;

/// <summary>
/// Whether Pulse runs from its Microsoft Store (MSIX) package, and how to start it again from outside.
///
/// One build serves both: the GitHub installer runs it unpackaged, the Store installs the same app in a package.
/// Inside a package a few things differ:
/// <list type="bullet">
/// <item>The exe lives under WindowsApps, and a copy started straight from there has no package identity (so it
/// would see different settings). The package's execution alias, <c>pulse.exe</c> in the user's WindowsApps folder,
/// starts it properly: elevated relaunches and the startup task go through it.</item>
/// <item>Toasts use the package's identity instead of one Pulse registers itself.</item>
/// <item>The Store updates the app, so Pulse's own updater stays off.</item>
/// </list>
/// </summary>
public static class AppPackage
{
    /// <summary>The execution alias declared in the package manifest (packaging/msix/AppxManifest.xml).</summary>
    public const string Alias = "pulse.exe";

    public static bool IsPackaged { get; } = Detect();

    /// <summary>The path other programs (the startup task, an elevated relaunch) should start Pulse with.</summary>
    public static string? LaunchPath => IsPackaged
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", Alias)
        : Environment.ProcessPath;

    /// <summary>The package's full name ("12345Pulse_0.2.0.0_x64__abc…"), or null when unpackaged.</summary>
    public static string? FullName
    {
        get
        {
            var length = 0;
            if (GetCurrentPackageFullName(ref length, null) != ErrorInsufficientBuffer)
                return null;
            var name = new StringBuilder(length);
            return GetCurrentPackageFullName(ref length, name) == 0 ? name.ToString() : null;
        }
    }

    private static bool Detect()
    {
        try
        {
            var length = 0;
            return GetCurrentPackageFullName(ref length, null) != AppModelErrorNoPackage;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    private const int AppModelErrorNoPackage = 15700;
    private const int ErrorInsufficientBuffer = 122;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int length, StringBuilder? name);
}
