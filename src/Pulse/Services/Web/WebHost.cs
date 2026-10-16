using System.IO;
using System.Net;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QRCoder;

namespace Pulse.Services.Web;

/// <summary>
/// Follows Settings → Phone and web view: starts the server when it's turned on (making the access token the
/// first time), moves it when the port changes, closes the port when it's turned off, and says how it's going.
/// UI thread.
/// </summary>
public sealed class WebHost : IDisposable
{
    private readonly SettingsService _settings;
    private readonly WebServer _server;
    private int? _runningPort;
    private bool _applying;

    public WebHost(SettingsService settings, WebServer server)
    {
        _settings = settings;
        _server = server;
        settings.Changed += Apply;
    }

    public WebServer Server => _server;

    public bool IsRunning => _server.IsRunning;

    /// <summary>The link to open on the phone (with the token), or null while off or without a network.</summary>
    public string? Link { get; private set; }

    /// <summary>One line for Settings: what's happening, or what went wrong.</summary>
    public string Status { get; private set; } = "Off. No port is open.";

    /// <summary>Raised after every change of state.</summary>
    public event Action? Changed;

    public void Apply()
    {
        if (_applying) return;
        _applying = true;
        try
        {
            var web = _settings.Current.Web;
            if (web.Enabled && string.IsNullOrEmpty(web.Token))
                _settings.Update(s => s.Web.Token = WebAccess.NewToken());

            if (!web.Enabled)
            {
                _server.Stop();
                _runningPort = null;
                Link = null;
                Status = "Off. No port is open.";
            }
            else if (!_server.IsRunning || _runningPort != web.Port)
            {
                try
                {
                    _server.Start(web.Port);
                    _runningPort = web.Port;
                }
                catch (HttpListenerException ex)
                {
                    _runningPort = null;
                    Log.Write($"Web view: couldn't listen on port {web.Port}: {ex.Message} ({ex.ErrorCode})");
                    Status = ex.ErrorCode is 32 or 183
                        ? $"Port {web.Port} is already in use by another program. Pick another port."
                        : ex.ErrorCode == 5
                            ? "Windows didn't allow it: Pulse needs to run as administrator for this."
                            : $"Couldn't start: {ex.Message}";
                    Link = null;
                    Changed?.Invoke();
                    return;
                }
            }

            if (web.Enabled)
            {
                var address = WebNetwork.Pick(WebNetwork.Addresses());
                Link = address is null ? null : WebAccess.Link(address, web.Port, _settings.Current.Web.Token!);
                Status = address is null
                    ? "On, but this PC isn't connected to a local network."
                    : "On. Open the link or scan the code with a phone on the same network.";
            }
            Changed?.Invoke();
        }
        finally
        {
            _applying = false;
        }
    }

    /// <summary>A new token: the old link and every phone using it stop working at once.</summary>
    public void NewLink()
    {
        _settings.Update(s => s.Web.Token = WebAccess.NewToken());
        Log.Write("Web view: new link made; the old one no longer works");
    }

    /// <summary>The link as a QR code for the phone's camera.</summary>
    public static ImageSource QrCode(string link)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(link, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(8);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(png);
        image.EndInit();
        image.Freeze();
        return image;
    }

    public void Dispose()
    {
        _settings.Changed -= Apply;
        _server.Dispose();
    }
}
