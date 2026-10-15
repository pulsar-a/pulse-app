using Microsoft.Win32;
using Pulse.Services;

namespace Pulse.Tests;

/// <summary>
/// Keeping the tray icon next to the clock. Runs against a scratch copy of the notification-area
/// settings under HKCU\Software\PulseTests, never the real ones.
/// </summary>
public sealed class TrayPromotionTests : IDisposable
{
    private const string Pulse = @"G:\code\Pulse\src\Pulse\bin\Debug\net10.0-windows10.0.19041.0\win-x64\Pulse.exe";
    private readonly string _rootPath = $@"Software\PulseTests\{Guid.NewGuid():N}";
    private readonly RegistryKey _root;

    public TrayPromotionTests() => _root = Registry.CurrentUser.CreateSubKey(_rootPath);

    private RegistryKey Icon(string id, string path, int? promoted = null)
    {
        var key = _root.CreateSubKey($@"{TrayPromotion.KeyPath}\{id}");
        key.SetValue("ExecutablePath", path);
        if (promoted is { } p) key.SetValue("IsPromoted", p, RegistryValueKind.DWord);
        return key;
    }

    private object? Promoted(string id) =>
        _root.OpenSubKey($@"{TrayPromotion.KeyPath}\{id}")!.GetValue("IsPromoted");

    [Fact]
    public void A_new_build_hidden_by_windows_is_brought_next_to_the_clock()
    {
        Icon("1", Pulse).Dispose();                                   // Windows' fresh record: no choice yet
        Icon("2", @"C:\Other\App.exe", promoted: 0).Dispose();

        Assert.Equal(PromotionResult.Changed, TrayPromotion.Apply(_root, Pulse.ToUpperInvariant(), show: true));
        Assert.Equal(1, Promoted("1"));
        Assert.Equal(0, Promoted("2"));                               // other apps are left alone
        Assert.Equal(PromotionResult.Unchanged, TrayPromotion.Apply(_root, Pulse, show: true));
    }

    [Fact]
    public void Turning_it_off_hands_the_icon_back_to_the_overflow()
    {
        Icon("1", Pulse, promoted: 1).Dispose();

        Assert.Equal(PromotionResult.Changed, TrayPromotion.Apply(_root, Pulse, show: false));
        Assert.Equal(0, Promoted("1"));
    }

    [Fact]
    public void Before_windows_records_the_icon_there_is_nothing_to_change_yet()
    {
        Icon("2", @"C:\Other\App.exe").Dispose();
        Assert.Equal(PromotionResult.NotFound, TrayPromotion.Apply(_root, Pulse, show: true));
    }

    [Fact]
    public void Without_windows_11_notification_settings_it_does_nothing() =>
        Assert.Equal(PromotionResult.Unsupported, TrayPromotion.Apply(_root, Pulse, show: true));

    public void Dispose()
    {
        _root.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(_rootPath, throwOnMissingSubKey: false);
        using var parent = Registry.CurrentUser.OpenSubKey(@"Software\PulseTests", writable: true);
        if (parent is { SubKeyCount: 0, ValueCount: 0 })
            Registry.CurrentUser.DeleteSubKey(@"Software\PulseTests", throwOnMissingSubKey: false);
    }
}
