using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows.Threading;
using Pulse.Services;
using Pulse.Services.Web;
using Pulse.ViewModels;

namespace Pulse.Tests;

public class WebViewTests : PulseTest
{
    // ---- Who gets in ----------------------------------------------------------------------------

    [Fact]
    public void Tokens_are_long_random_and_link_safe()
    {
        var tokens = Enumerable.Range(0, 50).Select(_ => WebAccess.NewToken()).ToList();

        Assert.All(tokens, t => Assert.Matches("^[A-Za-z0-9_-]{22}$", t));
        Assert.Equal(50, tokens.Distinct().Count());
    }

    [Fact]
    public void Only_the_exact_token_matches()
    {
        Assert.True(WebAccess.TokenMatches("abc123", "abc123"));
        Assert.False(WebAccess.TokenMatches("abc12", "abc123"));
        Assert.False(WebAccess.TokenMatches("ABC123", "abc123"));
        Assert.False(WebAccess.TokenMatches("", ""));
        Assert.False(WebAccess.TokenMatches(null, "abc123"));
        Assert.False(WebAccess.TokenMatches("abc123", null));   // no token yet: nobody gets in
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("192.168.1.20", true)]
    [InlineData("10.0.0.5", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("169.254.10.10", true)]
    [InlineData("fe80::1", true)]
    [InlineData("fd12:3456::1", true)]
    [InlineData("::ffff:192.168.1.20", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("100.64.0.1", false)]      // carrier-grade NAT / VPN overlays: not "the same network"
    [InlineData("2001:4860::8888", false)]
    [InlineData("::ffff:8.8.8.8", false)]
    public void Only_the_local_network_is_answered(string address, bool local) =>
        Assert.Equal(local, WebAccess.IsLocalNetwork(IPAddress.Parse(address)));

    [Fact]
    public void Links_put_the_token_in_the_address()
    {
        Assert.Equal("http://192.168.1.20:8085/?t=abc", WebAccess.Link(IPAddress.Parse("192.168.1.20"), 8085, "abc"));
        Assert.Equal("http://[fe80::1]:9000/?t=abc", WebAccess.Link(IPAddress.Parse("fe80::1"), 9000, "abc"));
    }

    private static readonly IPAddress Phone = IPAddress.Parse("192.168.1.50");
    private static readonly DateTime T0 = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Ten_wrong_links_in_a_minute_lock_an_address_out_for_five()
    {
        var lockout = new WebLockout();
        for (var i = 0; i < WebLockout.MaxFailures - 1; i++)
            Assert.False(lockout.Fail(Phone, T0.AddSeconds(i)));
        Assert.False(lockout.IsLocked(Phone, T0.AddSeconds(10)));

        Assert.True(lockout.Fail(Phone, T0.AddSeconds(10)));

        Assert.True(lockout.IsLocked(Phone, T0.AddMinutes(4)));
        Assert.False(lockout.IsLocked(Phone, T0.AddMinutes(6)));
        Assert.False(lockout.IsLocked(IPAddress.Parse("192.168.1.51"), T0.AddSeconds(11)));
    }

    [Fact]
    public void Slow_mistakes_and_a_right_link_reset_the_count()
    {
        var lockout = new WebLockout();
        for (var i = 0; i < 20; i++)
            lockout.Fail(Phone, T0.AddMinutes(i * 2));   // one every two minutes
        Assert.False(lockout.IsLocked(Phone, T0.AddMinutes(40)));

        for (var i = 0; i < 9; i++) lockout.Fail(Phone, T0);
        lockout.Succeed(Phone);
        Assert.False(lockout.Fail(Phone, T0));
    }

    [Fact]
    public void The_link_uses_the_real_network_not_virtual_switches()
    {
        AdapterAddress A(string name, string ip, bool gateway, NetworkInterfaceType type = NetworkInterfaceType.Ethernet) =>
            new(name, name, IPAddress.Parse(ip), gateway, type);

        Assert.Equal("192.168.1.20", WebNetwork.Pick(
        [
            A("vEthernet (WSL)", "172.22.48.1", false),
            A("vEthernet (Default Switch)", "172.17.0.1", true),
            A("Wi-Fi", "192.168.1.20", true, NetworkInterfaceType.Wireless80211),
            A("Ethernet 2", "169.254.3.3", false),
            A("Ethernet", "fe80::1", true),
        ])?.ToString());
        Assert.Equal("192.168.1.30", WebNetwork.Pick(
        [
            A("Wi-Fi", "192.168.1.20", true, NetworkInterfaceType.Wireless80211),
            A("Ethernet", "192.168.1.30", true),
        ])?.ToString());
        Assert.Null(WebNetwork.Pick([A("Ethernet", "8.8.8.8", true), A("Ethernet 2", "169.254.3.3", false)]));
    }

    [Fact]
    public void The_web_view_is_off_by_default()
    {
        var web = NewSettings().Current.Web;

        Assert.False(web.Enabled);
        Assert.Equal(8085, web.Port);
        Assert.Null(web.Token);
    }

    [Fact]
    public void Turning_it_on_makes_a_token_and_new_link_replaces_it()
    {
        var settings = NewSettings();
        using var host = new WebHost(settings, Server(settings, out _));

        host.Apply();
        Assert.Equal("Off. No port is open.", host.Status);
        Assert.Null(settings.Current.Web.Token);

        // Port 1 can't be opened by a test, whatever the rights: only the token matters here.
        settings.Update(s => { s.Web.Port = 1; s.Web.Enabled = true; });
        var first = settings.Current.Web.Token;
        Assert.NotNull(first);

        host.NewLink();
        Assert.NotEqual(first, settings.Current.Web.Token);

        settings.Update(s => s.Web.Enabled = false);
        Assert.False(host.IsRunning);
        Assert.Null(host.Link);
    }

    // ---- What the phone sees ---------------------------------------------------------------------

    private WebFeed Feed(SettingsService settings, IEnumerable<AlertItem>? alerts = null)
    {
        var catalog = new SensorCatalog(settings);
        var stream = new SensorStream();
        return new WebFeed(Dispatcher.CurrentDispatcher, stream, () => new DashboardViewModel(true, catalog, stream),
            () => alerts ?? [], _ => "Test PC");
    }

    [Fact]
    public void The_summary_carries_the_dashboards_cards()
    {
        var feed = Feed(NewSettings(), [new AlertItem(DateTime.Today.AddHours(9), "CPU too hot", "96 °C", AlertSeverity.Critical, false)]);
        feed.Enabled = true;

        feed.Publish(Snap.Fixture("non-admin.json"));

        var json = feed.Json!;
        Assert.Contains("\"machine\":\"Test PC\"", json);
        Assert.Contains("\"title\":\"Processor\"", json);
        Assert.Contains("\"title\":\"Graphics\"", json);
        Assert.Contains("\"title\":\"Memory\"", json);
        Assert.Contains("\"cooling\":{", json);
        Assert.Contains("\"title\":\"CPU too hot\"", json);
        Assert.Contains("\"severity\":\"Critical\"", json);
    }

    [Fact]
    public void Turning_it_off_forgets_the_summary()
    {
        var feed = Feed(NewSettings());
        feed.Enabled = true;
        feed.Publish(Snap.Fixture("non-admin.json"));

        feed.Enabled = false;

        Assert.Null(feed.Json);
    }

    // ---- The server, for real, on a localhost port ------------------------------------------------

    private WebServer Server(SettingsService settings, out WebFeed feed)
    {
        feed = Feed(settings);
        return new WebServer(feed, () => null, () => settings.Current.Web.Token);
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private sealed class Running : IDisposable
    {
        public required WebServer Server { get; init; }
        public required WebFeed Feed { get; init; }
        public required HttpClient Http { get; init; }
        public required string Token { get; init; }

        public HttpRequestMessage Get(string path, string? token = null, bool cookie = false)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, path);
            if (token is not null && cookie)
                request.Headers.Add("Cookie", $"{WebAccess.CookieName}={token}");
            else if (token is not null)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return request;
        }

        public void Dispose()
        {
            Http.Dispose();
            Server.Dispose();
        }
    }

    private Running Start(SettingsService? settings = null)
    {
        settings ??= NewSettings();
        settings.Update(s => s.Web.Token = WebAccess.NewToken());
        var server = Server(settings, out var feed);
        var port = FreePort();
        server.Start(port, "localhost");
        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        {
            BaseAddress = new Uri($"http://localhost:{port}/"),
            Timeout = TimeSpan.FromSeconds(10),
        };
        return new Running { Server = server, Feed = feed, Http = http, Token = settings.Current.Web.Token! };
    }

    [Fact]
    public async Task Without_the_token_nothing_is_served()
    {
        using var web = Start();

        foreach (var path in new[] { "/", "/app.js", "/api/summary", "/api/stream", "/api/history?sensor=x" })
        {
            var response = await web.Http.SendAsync(web.Get(path));
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        var wrong = await web.Http.SendAsync(web.Get("/", "not-the-token"));
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
    }

    [Fact]
    public async Task The_qr_link_sets_a_strict_cookie_and_drops_the_token_from_the_address()
    {
        using var web = Start();

        var response = await web.Http.SendAsync(web.Get($"/?t={web.Token}"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location?.OriginalString);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith($"{WebAccess.CookieName}={web.Token};", cookie);
        Assert.Contains("HttpOnly", cookie);
        Assert.Contains("SameSite=Strict", cookie);

        var page = await web.Http.SendAsync(web.Get("/", web.Token, cookie: true));
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("app.js", await page.Content.ReadAsStringAsync());
        Assert.Equal("no-referrer", page.Headers.GetValues("Referrer-Policy").Single());
        Assert.Contains("script-src 'self'", page.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Contains("frame-ancestors 'none'", page.Headers.GetValues("Content-Security-Policy").Single());

        var script = await web.Http.SendAsync(web.Get("/app.js", web.Token, cookie: true));
        Assert.Equal("text/javascript", script.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task The_api_serves_the_summary_and_is_read_only()
    {
        using var web = Start();
        web.Feed.Publish(Snap.Fixture("non-admin.json"));

        var summary = await web.Http.SendAsync(web.Get("/api/summary", web.Token));
        Assert.Equal(HttpStatusCode.OK, summary.StatusCode);
        Assert.Contains("\"title\":\"Processor\"", await summary.Content.ReadAsStringAsync());

        var post = new HttpRequestMessage(HttpMethod.Post, "/api/summary");
        post.Headers.Authorization = new AuthenticationHeaderValue("Bearer", web.Token);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await web.Http.SendAsync(post)).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await web.Http.SendAsync(web.Get("/api/../../etc", web.Token))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await web.Http.SendAsync(web.Get("/api/history?sensor=x&range=1y", web.Token))).StatusCode);
        Assert.Equal("[]", await (await web.Http.SendAsync(web.Get("/api/history?sensor=x&range=24h", web.Token))).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_stream_sends_the_summary_as_it_changes()
    {
        using var web = Start();
        web.Feed.Publish(Snap.Fixture("non-admin.json"));

        using var response = await web.Http.SendAsync(web.Get("/api/stream", web.Token), HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());

        var first = await reader.ReadLineAsync();
        Assert.StartsWith("data: {", first);
        Assert.Equal(1, web.Server.Streams);

        web.Feed.Publish(Snap.Fixture("non-admin.json"));
        await reader.ReadLineAsync();   // the blank line ending the first event
        Assert.StartsWith("data: {", await reader.ReadLineAsync());
    }

    [Fact]
    public async Task Guessing_locks_the_address_out_even_for_the_right_token()
    {
        using var web = Start();
        for (var i = 0; i < WebLockout.MaxFailures; i++)
            await web.Http.SendAsync(web.Get("/api/summary", $"guess-{i}"));

        var response = await web.Http.SendAsync(web.Get("/api/summary", web.Token));

        Assert.Equal((HttpStatusCode)429, response.StatusCode);
        Assert.Contains("locked out", File.ReadAllText(Log.FilePath));
    }

    [Fact]
    public async Task A_new_link_shuts_out_the_old_one_at_once()
    {
        var settings = NewSettings();
        using var web = Start(settings);
        var old = web.Token;

        settings.Update(s => s.Web.Token = WebAccess.NewToken());

        Assert.Equal(HttpStatusCode.Unauthorized, (await web.Http.SendAsync(web.Get("/api/summary", old, cookie: true))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await web.Http.SendAsync(web.Get("/api/summary", settings.Current.Web.Token))).StatusCode);
    }

    [Fact]
    public async Task Stopping_closes_the_port()
    {
        using var web = Start();
        var address = web.Http.BaseAddress!;

        web.Server.Stop();

        Assert.False(web.Server.IsRunning);
        using var client = new TcpClient();
        await Assert.ThrowsAnyAsync<SocketException>(() => client.ConnectAsync(address.Host == "localhost" ? "127.0.0.1" : address.Host, address.Port));
    }
}
