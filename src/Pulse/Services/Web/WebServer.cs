using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Pulse.Services.History;

namespace Pulse.Services.Web;

/// <summary>
/// The phone and web view: a small read-only web server inside Pulse, on the local network only.
///
/// Built on <see cref="HttpListener"/>, which runs on Windows' own HTTP service (http.sys) and ships with .NET,
/// so users need nothing beyond the Desktop runtime they already have (ASP.NET Core's Kestrel would need its own
/// runtime). Pulse runs as administrator, which binding to the network with http.sys requires.
///
/// Every request goes through the same checks, in order:
/// <list type="number">
/// <item>The caller must be on the local network (<see cref="WebAccess.IsLocalNetwork"/>).</item>
/// <item>An address that guessed wrong too often is locked out for a while (<see cref="WebLockout"/>).</item>
/// <item>The access token must match: from the QR code's link once (<c>/?t=…</c>, which sets a cookie and
/// redirects, so the token leaves the address bar), then from that cookie or an <c>Authorization: Bearer</c> header.</item>
/// <item>Only GET, only the routes below, and at most <see cref="MaxStreams"/> live streams.</item>
/// </list>
/// Routes: <c>/</c> (the page), <c>/app.js</c>, <c>/app.css</c>, <c>/api/summary</c>, <c>/api/history?sensor=…&amp;range=1h|24h|7d</c>,
/// <c>/api/stream</c> (Server-Sent Events, one summary per second).
/// </summary>
public sealed class WebServer : IDisposable
{
    public const int MaxStreams = 8;

    private static readonly Dictionary<string, (string Resource, string Type)> Files = new()
    {
        ["/"] = ("index.html", "text/html; charset=utf-8"),
        ["/app.js"] = ("app.js", "text/javascript; charset=utf-8"),
        ["/app.css"] = ("app.css", "text/css; charset=utf-8"),
    };

    private static readonly Dictionary<string, (TimeSpan Span, int Points)> Ranges = new()
    {
        ["1h"] = (TimeSpan.FromHours(1), 240),
        ["24h"] = (TimeSpan.FromHours(24), 288),
        ["7d"] = (TimeSpan.FromDays(7), 336),
    };

    private readonly WebFeed _feed;
    private readonly Func<HistoryStore?> _history;
    private readonly Func<string?> _token;
    private readonly WebLockout _lockout = new();
    private readonly HashSet<IPAddress> _seen = [];
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private int _streams;

    /// <param name="token">The current access token (read on every request, so "New link" takes effect at once).</param>
    public WebServer(WebFeed feed, Func<HistoryStore?> history, Func<string?> token)
    {
        _feed = feed;
        _history = history;
        _token = token;
    }

    public bool IsRunning => _listener?.IsListening == true;

    /// <summary>Phones and browsers watching the live stream right now.</summary>
    public int Streams => Volatile.Read(ref _streams);

    /// <summary>Raised (on a server thread) when a live stream opens or closes.</summary>
    public event Action? StreamsChanged;

    /// <summary>Starts listening. Throws <see cref="HttpListenerException"/> if the port is taken or not allowed.</summary>
    /// <param name="host">"+" for every address (the default; the local-network check does the filtering), "localhost" in tests.</param>
    public void Start(int port, string host = "+")
    {
        Stop();
        var listener = new HttpListener { IgnoreWriteExceptions = true };
        listener.Prefixes.Add($"http://{host}:{port}/");
        listener.Start();
        _listener = listener;
        _cts = new CancellationTokenSource();
        _feed.Enabled = true;
        _ = Task.Run(() => AcceptAsync(listener, _cts.Token));
        Log.Write($"Web view: listening on port {port}");
    }

    public void Stop()
    {
        if (_listener is null)
            return;
        _cts?.Cancel();
        try { _listener.Close(); }
        catch (ObjectDisposedException) { }
        _listener = null;
        _feed.Enabled = false;
        Log.Write("Web view: stopped, port closed");
    }

    private async Task AcceptAsync(HttpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;   // stopped
            }
            _ = Task.Run(() => HandleAsync(context, ct), ct);
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken ct)
    {
        var request = context.Request;
        var response = context.Response;
        try
        {
            SecurityHeaders(response);
            var remote = request.RemoteEndPoint.Address;
            var now = DateTime.UtcNow;

            if (!WebAccess.IsLocalNetwork(remote))
            {
                Log.Write($"Web view: refused {remote} (not on the local network)");
                await Text(response, 403, "Pulse only answers on the local network.");
                return;
            }
            if (_lockout.IsLocked(remote, now))
            {
                await Text(response, 429, "Too many wrong links. Try again in a few minutes.");
                return;
            }
            if (request.HttpMethod != "GET")
            {
                response.AddHeader("Allow", "GET");
                await Text(response, 405, "Pulse's web view is read-only.");
                return;
            }

            var path = request.Url?.AbsolutePath ?? "/";
            var token = _token();

            // The QR code's link: check the token once, remember it in a cookie, and drop it from the address.
            if (path == "/" && request.QueryString["t"] is { } fromLink)
            {
                if (!WebAccess.TokenMatches(fromLink, token))
                {
                    Failed(remote, now);
                    await Text(response, 401, "This link isn't valid any more. Scan the QR code in Pulse → Settings → Phone and web view.");
                    return;
                }
                Allowed(remote);
                response.AppendHeader("Set-Cookie",
                    $"{WebAccess.CookieName}={fromLink}; Path=/; Max-Age=31536000; HttpOnly; SameSite=Strict");
                response.Redirect("/");
                response.Close();
                return;
            }

            if (!WebAccess.TokenMatches(Presented(request), token))
            {
                Failed(remote, now);
                await Text(response, 401, "Open this page with the link or QR code from Pulse → Settings → Phone and web view.");
                return;
            }
            Allowed(remote);

            if (Files.TryGetValue(path, out var file))
                await Resource(response, file.Resource, file.Type);
            else if (path == "/api/summary")
                await Json(response, _feed.Json ?? "null");
            else if (path == "/api/history")
                await History(request, response);
            else if (path == "/api/stream")
                await Stream(response, ct);
            else
                await Text(response, 404, "Not found.");
        }
        catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The phone went away mid-answer.
        }
        finally
        {
            try { response.Close(); }
            catch (Exception ex) when (ex is ObjectDisposedException or HttpListenerException or InvalidOperationException) { }
        }
    }

    /// <summary>The token from the cookie, or from an Authorization: Bearer header (for scripts).</summary>
    private static string? Presented(HttpListenerRequest request)
    {
        if (request.Headers["Authorization"] is { } auth && auth.StartsWith("Bearer ", StringComparison.Ordinal))
            return auth[7..].Trim();
        return request.Cookies[WebAccess.CookieName]?.Value;
    }

    private void Failed(IPAddress remote, DateTime now)
    {
        if (_lockout.Fail(remote, now))
            Log.Write($"Web view: {remote} locked out for {WebLockout.LockTime.TotalMinutes:0} minutes after {WebLockout.MaxFailures} wrong links");
    }

    private void Allowed(IPAddress remote)
    {
        _lockout.Succeed(remote);
        lock (_seen)
        {
            if (_seen.Add(remote))
                Log.Write($"Web view: {remote} connected");
        }
    }

    private static void SecurityHeaders(HttpListenerResponse response)
    {
        response.AddHeader("Cache-Control", "no-store");
        response.AddHeader("X-Content-Type-Options", "nosniff");
        response.AddHeader("X-Frame-Options", "DENY");
        response.AddHeader("Referrer-Policy", "no-referrer");
        response.AddHeader("Content-Security-Policy",
            "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; " +
            "base-uri 'none'; form-action 'none'; frame-ancestors 'none'");
    }

    private async Task History(HttpListenerRequest request, HttpListenerResponse response)
    {
        var sensor = request.QueryString["sensor"];
        var range = request.QueryString["range"] ?? "1h";
        if (string.IsNullOrEmpty(sensor) || sensor.Length > 256 || !Ranges.TryGetValue(range, out var r))
        {
            await Text(response, 400, "Ask for ?sensor=<id>&range=1h|24h|7d.");
            return;
        }
        if (_history() is not { } store)
        {
            await Json(response, "[]");
            return;
        }
        var to = DateTime.Now;
        var points = await store.QueryAsync(sensor, to - r.Span, to, r.Points);
        var json = JsonSerializer.Serialize(points.Select(p => new
        {
            t = new DateTimeOffset(p.Time).ToUnixTimeMilliseconds(),
            min = Math.Round(p.Min, 2),
            avg = Math.Round(p.Avg, 2),
            max = Math.Round(p.Max, 2),
        }));
        await Json(response, json);
    }

    /// <summary>Server-Sent Events: the summary each time it changes (once a second), and a ping every 15 s to keep proxies quiet.</summary>
    private async Task Stream(HttpListenerResponse response, CancellationToken ct)
    {
        if (Interlocked.Increment(ref _streams) > MaxStreams)
        {
            Interlocked.Decrement(ref _streams);
            await Text(response, 429, "Too many open views.");
            return;
        }
        StreamsChanged?.Invoke();
        try
        {
            response.StatusCode = 200;
            response.ContentType = "text/event-stream";
            response.SendChunked = true;
            var output = response.OutputStream;
            if (_feed.Json is { } first)
                await Event(output, first, ct);

            while (!ct.IsCancellationRequested)
            {
                var changed = _feed.Changed;
                if (await Task.WhenAny(changed, Task.Delay(TimeSpan.FromSeconds(15), ct)) == changed && _feed.Json is { } json)
                    await Event(output, json, ct);
                else
                    await Write(output, ": ping\n\n", ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            Interlocked.Decrement(ref _streams);
            StreamsChanged?.Invoke();
        }
    }

    private static Task Event(Stream output, string json, CancellationToken ct) => Write(output, $"data: {json}\n\n", ct);

    private static async Task Write(Stream output, string text, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await output.WriteAsync(bytes, ct);
        await output.FlushAsync(ct);
    }

    private static async Task Resource(HttpListenerResponse response, string name, string type)
    {
        await using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Pulse.Web.{name}");
        if (stream is null)
        {
            await Text(response, 404, "Not found.");
            return;
        }
        response.StatusCode = 200;
        response.ContentType = type;
        response.ContentLength64 = stream.Length;
        await stream.CopyToAsync(response.OutputStream);
    }

    private static Task Json(HttpListenerResponse response, string json) => Body(response, 200, json, "application/json; charset=utf-8");

    private static Task Text(HttpListenerResponse response, int status, string text) => Body(response, status, text, "text/plain; charset=utf-8");

    private static async Task Body(HttpListenerResponse response, int status, string body, string type)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        response.StatusCode = status;
        response.ContentType = type;
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
    }

    public void Dispose() => Stop();
}
