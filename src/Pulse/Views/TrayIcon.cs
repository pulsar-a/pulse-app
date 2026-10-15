using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using LibreHardwareMonitor.Hardware;
using Pulse.Controls;
using Pulse.Models;
using Pulse.Services;

namespace Pulse.Views;

/// <summary>
/// The notification-area icon: a live number (CPU temperature by default), a tooltip with the headline
/// readings, and a menu. It listens to the sensor stream directly, so it keeps updating while the window
/// is hidden. The icon image is only redrawn when the number or its color changes.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly TaskbarIcon _icon;
    private readonly SettingsService _settings;
    private readonly SensorCatalog _catalog;
    private readonly SensorStream _stream;
    private readonly Dispatcher _dispatcher;
    private readonly Action _toggleWindow;
    private readonly Action _showWindow;
    private readonly Action _exit;

    private HardwareSnapshot? _pending;
    private int _updateScheduled;
    private string _renderedKey = "";
    private bool _alarm;
    private System.Drawing.Icon? _currentIcon;

    public TrayIcon(
        SettingsService settings,
        SensorCatalog catalog,
        SensorStream stream,
        Dispatcher dispatcher,
        Action toggleWindow,
        Action showWindow,
        Action exit)
    {
        _settings = settings;
        _catalog = catalog;
        _stream = stream;
        _dispatcher = dispatcher;
        _toggleWindow = toggleWindow;
        _showWindow = showWindow;
        _exit = exit;

        _icon = new TaskbarIcon
        {
            ToolTipText = "Pulse · starting…",
            IconSource = new BitmapImage(new Uri("pack://application:,,,/Resources/Pulse.ico")),
            LeftClickCommand = new RelayCommand(_toggleWindow),
            NoLeftClickDelay = true,
            ContextMenu = new ContextMenu(),
        };
        _icon.ContextMenu.Opened += (_, _) => BuildMenu();

        // Efficiency Mode (the library's default here) would throttle Pulse; a monitor shouldn't be throttled.
        _icon.ForceCreate(enablesEfficiencyMode: false);
        ApplyVisibility();

        _stream.SnapshotPublished += OnSnapshotPublished;
        _settings.Changed += OnSettingsChanged;
    }

    public bool IsVisible => _settings.Current.Startup.ShowTrayIcon;

    /// <summary>A critical alert is active: the icon turns red until it clears.</summary>
    public void SetAlarm(bool alarm)
    {
        if (_alarm == alarm)
            return;
        _alarm = alarm;
        _renderedKey = "";
        if (_stream.Latest is { } latest)
            Update(latest);
    }

    public void ShowNotification(string title, string message) =>
        _icon.ShowNotification(title, message);

    private string _settingsKey = "";

    private void OnSettingsChanged()
    {
        // Settings change often (every step of a slider); only the tray's own choices matter here.
        var s = _settings.Current;
        var key = $"{s.Startup.ShowTrayIcon}|{s.Startup.TraySensorId}|{s.Units.Temperature}|{string.Join(",", s.Sensors.Names)}|{string.Join(",", s.Sensors.Roles)}";
        if (key == _settingsKey) return;
        _settingsKey = key;

        ApplyVisibility();
        _renderedKey = "";   // unit or tray choice may have changed: redraw
        if (_stream.Latest is { } latest)
            Update(latest);
    }

    private void ApplyVisibility() =>
        _icon.Visibility = IsVisible ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Called on the polling thread; only the newest snapshot is applied.</summary>
    private void OnSnapshotPublished(HardwareSnapshot snapshot)
    {
        Volatile.Write(ref _pending, snapshot);
        if (Interlocked.Exchange(ref _updateScheduled, 1) == 0)
            _dispatcher.BeginInvoke(() =>
            {
                Interlocked.Exchange(ref _updateScheduled, 0);
                if (Interlocked.Exchange(ref _pending, null) is { } latest)
                    Update(latest);
            }, DispatcherPriority.Background);
    }

    private void Update(HardwareSnapshot snapshot)
    {
        if (!IsVisible)
            return;

        _icon.ToolTipText = TrayText.Tooltip(snapshot, _catalog.RoleSensor(SensorRoles.Pump));

        var reading = TrayText.Resolve(snapshot, _settings.Current.Startup.TraySensorId, _catalog.DisplayName);
        if (reading is null)
            return;

        var color = _alarm ? Colors.White : ColorFor(reading);
        var key = $"{reading.Text}|{color}|{_alarm}";
        if (key == _renderedKey)
            return;
        _renderedKey = key;

        var icon = ToIcon(Render(reading.Text, color, SmallIconSize(), _alarm));
        _icon.UpdateIcon(icon);
        _currentIcon?.Dispose();
        _currentIcon = icon;
    }

    /// <summary>
    /// WPF bitmap → GDI icon, with alpha. (The library's own converter doesn't accept rendered bitmaps.)
    /// The returned icon owns its handle; dispose it when it's replaced.
    /// </summary>
    private static System.Drawing.Icon ToIcon(BitmapSource source)
    {
        var width = source.PixelWidth;
        var height = source.PixelHeight;
        var pixels = new byte[width * height * 4];
        source.CopyPixels(pixels, width * 4, 0);

        using var bitmap = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        var data = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, width, height),
            System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
        bitmap.UnlockBits(data);

        var handle = bitmap.GetHicon();
        try
        {
            // FromHandle doesn't take ownership; the clone does, so the original handle can be freed now.
            using var borrowed = System.Drawing.Icon.FromHandle(handle);
            return (System.Drawing.Icon)borrowed.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    private static Color ColorFor(TrayReading reading) => reading.Type switch
    {
        SensorType.Temperature => Palette.Temperature(reading.Value).Color,
        SensorType.Load or SensorType.Control or SensorType.Level => Palette.Heat(reading.Value / 100).Color,
        _ => Color.FromRgb(0xF2, 0xF2, 0xF2),
    };

    /// <summary>Draws the number on a dark rounded square, at the exact pixel size Windows uses for tray icons.</summary>
    private static BitmapSource Render(string text, Color color, int size, bool alarm)
    {
        var visual = new DrawingVisual();
        TextOptions.SetTextFormattingMode(visual, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(visual, TextRenderingMode.Grayscale);

        using (var dc = visual.RenderOpen())
        {
            var radius = size / 5.0;
            var background = alarm ? Color.FromRgb(0xE5, 0x48, 0x4D) : Color.FromArgb(0xF0, 0x16, 0x18, 0x1E);
            dc.DrawRoundedRectangle(new SolidColorBrush(background), null,
                new Rect(0, 0, size, size), radius, radius);

            // Largest font that fits: two digits fill the square, three get a little smaller.
            var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Condensed);
            var brush = new SolidColorBrush(color);
            FormattedText formatted;
            var fontSize = size * 0.82;
            do
            {
                formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    typeface, fontSize, brush, 1.0);
                fontSize -= 0.5;
            } while (formatted.WidthIncludingTrailingWhitespace > size * 0.96 && fontSize > 6);

            var origin = new Point((size - formatted.WidthIncludingTrailingWhitespace) / 2, (size - formatted.Height) / 2);
            dc.DrawText(formatted, origin);
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    /// <summary>Tray icons are "small icons": 16 px at 100% scaling, 24 px at 150%, 32 px at 200%.</summary>
    private static int SmallIconSize() => Math.Max(16, GetSystemMetrics(49 /* SM_CXSMICON */));

    /// <summary>The mini window, for its menu entries; set by the app.</summary>
    public MiniController? Mini { get; set; }

    private void BuildMenu()
    {
        var menu = _icon.ContextMenu!;
        menu.Items.Clear();

        var open = new MenuItem { Header = "Open Pulse", FontWeight = FontWeights.SemiBold };
        open.Click += (_, _) => _showWindow();
        menu.Items.Add(open);

        if (Mini is { } mini)
        {
            var shortcut = mini.Shortcut is { } keys ? $"   {keys}" : "";
            var toggle = new MenuItem { Header = "Mini window" + shortcut, IsCheckable = true, IsChecked = mini.IsOpen };
            toggle.Click += (_, _) => mini.SetOpen(toggle.IsChecked);
            menu.Items.Add(toggle);

            // While clicks go through the mini window, its own menu can't be reached: this is the way out.
            if (mini.IsOpen && _settings.Current.Mini.ClickThrough)
            {
                var clickable = new MenuItem { Header = "Make the mini window clickable" };
                clickable.Click += (_, _) => _settings.Update(s => s.Mini.ClickThrough = false);
                menu.Items.Add(clickable);
            }
        }
        menu.Items.Add(new Separator());

        // What the icon shows: the common readings this PC has, plus whatever was picked from All sensors.
        var shows = new MenuItem { Header = "Tray icon shows" };
        var current = _settings.Current.Startup.TraySensorId;
        var choices = TrayText.Choices(_stream.Latest, _catalog.RoleSensor(SensorRoles.Pump)).ToList();
        if (current is not null && choices.All(c => c.SensorId != current) && _catalog.Find(current) is { } custom)
            choices.Add(new TrayChoice(custom.DisplayName, current));
        foreach (var choice in choices)
        {
            var item = new MenuItem { Header = choice.Label, IsCheckable = true, IsChecked = choice.SensorId == current };
            item.Click += (_, _) => _settings.Update(s => s.Startup.TraySensorId = choice.SensorId);
            shows.Items.Add(item);
        }
        menu.Items.Add(shows);

        var startup = new MenuItem
        {
            Header = "Start with Windows",
            IsCheckable = true,
            IsEnabled = HardwareService.IsElevated,
            ToolTip = HardwareService.IsElevated ? null : "Needs Pulse to run as administrator",
        };
        _ = RefreshStartupCheckAsync(startup);
        startup.Click += async (_, _) =>
        {
            var exe = Environment.ProcessPath!;
            var ok = startup.IsChecked ? await StartupTask.EnableAsync(exe) : await StartupTask.DisableAsync();
            if (!ok)
                await RefreshStartupCheckAsync(startup);
        };
        menu.Items.Add(startup);

        menu.Items.Add(new Separator());
        var exit = new MenuItem { Header = "Exit Pulse" };
        exit.Click += (_, _) => _exit();
        menu.Items.Add(exit);
    }

    private static async Task RefreshStartupCheckAsync(MenuItem item) =>
        item.IsChecked = await StartupTask.IsEnabledAsync();

    public void Dispose()
    {
        _stream.SnapshotPublished -= OnSnapshotPublished;
        _settings.Changed -= OnSettingsChanged;
        _icon.Dispose();
        _currentIcon?.Dispose();
    }
}
