using System.Diagnostics;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Pulse.Services.Web;

/// <summary>An adapter's IPv4 address, as far as picking the one a phone can reach is concerned.</summary>
public sealed record AdapterAddress(string Adapter, string Description, IPAddress Address, bool HasGateway, NetworkInterfaceType Type);

/// <summary>The PC's address on the local network, Windows' network category, and the firewall rule.</summary>
public static class WebNetwork
{
    public const string FirewallRuleName = "Pulse web view";

    private static readonly string[] VirtualWords =
        ["vEthernet", "Hyper-V", "VirtualBox", "VMware", "WSL", "Docker", "Loopback", "TAP-", "Tailscale", "ZeroTier", "WireGuard"];

    /// <summary>
    /// The address to put in the link: a private IPv4 address on a real adapter, preferring one with a gateway
    /// (the network the phone is on), then wired over Wi-Fi. Virtual switches (Hyper-V, WSL, Docker) and VPNs
    /// come last: a phone can't reach those.
    /// </summary>
    public static IPAddress? Pick(IEnumerable<AdapterAddress> addresses) =>
        addresses
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && WebAccess.IsLocalNetwork(a.Address)
                        && !IPAddress.IsLoopback(a.Address) && !IsLinkLocal(a.Address))
            .OrderBy(a => IsVirtual(a) ? 1 : 0)
            .ThenBy(a => a.HasGateway ? 0 : 1)
            .ThenBy(a => a.Type == NetworkInterfaceType.Ethernet ? 0 : 1)
            .Select(a => a.Address)
            .FirstOrDefault();

    private static bool IsLinkLocal(IPAddress address) => address.GetAddressBytes() is [169, 254, ..];

    private static bool IsVirtual(AdapterAddress a) =>
        VirtualWords.Any(w => a.Adapter.Contains(w, StringComparison.OrdinalIgnoreCase)
                              || a.Description.Contains(w, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<AdapterAddress> Addresses()
    {
        var result = new List<AdapterAddress>();
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;
                var properties = adapter.GetIPProperties();
                var gateway = properties.GatewayAddresses.Any(g => !g.Address.Equals(IPAddress.Any));
                foreach (var unicast in properties.UnicastAddresses)
                    result.Add(new AdapterAddress(adapter.Name, adapter.Description, unicast.Address, gateway, adapter.NetworkInterfaceType));
            }
        }
        catch (NetworkInformationException ex)
        {
            Log.Write($"Couldn't list network adapters: {ex.Message}");
        }
        return result;
    }

    /// <summary>
    /// True when every connected network is Public in Windows: the private-networks-only firewall rule then
    /// doesn't apply and phones can't connect. Null when Windows won't say.
    /// </summary>
    public static bool? OnlyPublicNetworks()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\StandardCimv2", "SELECT NetworkCategory FROM MSFT_NetConnectionProfile");
            var categories = searcher.Get().Cast<ManagementBaseObject>().Select(p => Convert.ToInt32(p["NetworkCategory"])).ToList();
            return categories.Count == 0 ? null : categories.All(c => c == 0);
        }
        catch (Exception ex) when (ex is ManagementException or InvalidCastException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Whether Pulse's inbound rule exists (checking changes nothing).</summary>
    public static bool HasFirewallRule() => Netsh($"advfirewall firewall show rule name=\"{FirewallRuleName}\"") == 0;

    /// <summary>
    /// Adds (or replaces) an inbound rule for the port, on private networks only. Run only when the user asks:
    /// it changes Windows Firewall. The web server runs inside Windows' HTTP service, so the rule is by port.
    /// </summary>
    public static bool AllowThroughFirewall(int port)
    {
        Netsh($"advfirewall firewall delete rule name=\"{FirewallRuleName}\"");
        var ok = Netsh($"advfirewall firewall add rule name=\"{FirewallRuleName}\" dir=in action=allow protocol=TCP " +
                       $"localport={port} profile=private") == 0;
        Log.Write(ok ? $"Firewall: allowed TCP {port} on private networks" : "Firewall: couldn't add the rule");
        return ok;
    }

    /// <summary>Takes the rule away again (when the user turns the web view off and asks to close the port).</summary>
    public static bool RemoveFirewallRule()
    {
        var ok = Netsh($"advfirewall firewall delete rule name=\"{FirewallRuleName}\"") == 0;
        if (ok) Log.Write("Firewall: rule removed");
        return ok;
    }

    private static int Netsh(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("netsh.exe", arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process is null) return -1;
            process.StandardOutput.ReadToEnd();
            return process.WaitForExit(10_000) ? process.ExitCode : -1;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return -1;
        }
    }
}
