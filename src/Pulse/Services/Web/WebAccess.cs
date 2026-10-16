using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Pulse.Services.Web;

/// <summary>Settings → Phone and web view.</summary>
public sealed class WebSettings
{
    public const int DefaultPort = 8085;

    /// <summary>Off by default: no port is open until the user turns it on.</summary>
    public bool Enabled { get; set; }

    public int Port { get; set; } = DefaultPort;

    /// <summary>The secret in the link (and QR code). Made when the feature is first turned on; "New link" replaces it.</summary>
    public string? Token { get; set; }
}

/// <summary>Who may use the web view: pure rules, tested on their own.</summary>
public static class WebAccess
{
    public const string CookieName = "pulse_token";

    /// <summary>A new access token: 128 random bits, URL-safe (22 characters).</summary>
    public static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Compares in constant time, so response timing doesn't reveal how much of a guess was right.</summary>
    public static bool TokenMatches(string? candidate, string? token)
    {
        if (string.IsNullOrEmpty(candidate) || string.IsNullOrEmpty(token))
            return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(candidate), Encoding.UTF8.GetBytes(token));
    }

    /// <summary>
    /// The local network only: loopback, private IPv4 ranges, link-local, and IPv6 unique-local addresses.
    /// Anything else (a public address reaching an exposed PC, a VPN peer with a public range) is refused,
    /// whatever the firewall says.
    /// </summary>
    public static bool IsLocalNetwork(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address))
            return true;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 10
                   || (b[0] == 172 && b[1] is >= 16 and <= 31)
                   || (b[0] == 192 && b[1] == 168)
                   || (b[0] == 169 && b[1] == 254);
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = address.GetAddressBytes();
            return address.IsIPv6LinkLocal || (b[0] & 0xFE) == 0xFC;   // fe80::/10, fc00::/7
        }
        return false;
    }

    /// <summary>The address on the phone's side of the link: http://192.168.1.20:8085/?t=…</summary>
    public static string Link(IPAddress host, int port, string token)
    {
        var name = host.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{host}]" : host.ToString();
        return $"http://{name}:{port}/?t={token}";
    }
}

/// <summary>
/// Slows down guessing: after <see cref="MaxFailures"/> wrong tokens from one address within
/// <see cref="Window"/>, that address is refused for <see cref="LockTime"/>. Thread-safe; the clock is passed in.
/// </summary>
public sealed class WebLockout
{
    public const int MaxFailures = 10;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan LockTime = TimeSpan.FromMinutes(5);

    private readonly Dictionary<IPAddress, (int Count, DateTime First, DateTime? LockedUntil)> _entries = new();
    private readonly object _gate = new();

    public bool IsLocked(IPAddress address, DateTime now)
    {
        lock (_gate)
            return _entries.TryGetValue(address, out var e) && e.LockedUntil is { } until && now < until;
    }

    /// <returns>True if this failure locked the address out.</returns>
    public bool Fail(IPAddress address, DateTime now)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(address, out var e) || now - e.First > Window || e.LockedUntil is { } until && now >= until)
                e = (0, now, null);
            e.Count++;
            var locked = e.Count >= MaxFailures && e.LockedUntil is null;
            if (locked)
                e.LockedUntil = now + LockTime;
            _entries[address] = e;
            // Keep the table small: forget addresses long since quiet.
            if (_entries.Count > 256)
                foreach (var stale in _entries.Where(kv => now - kv.Value.First > LockTime + Window).Select(kv => kv.Key).ToList())
                    _entries.Remove(stale);
            return locked;
        }
    }

    public void Succeed(IPAddress address)
    {
        lock (_gate)
            _entries.Remove(address);
    }
}
