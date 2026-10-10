using EgorVpn.Models;
using System.Net;
using System.Text.Json;

namespace EgorVpn.Services;

public static class LegacySingBoxConfigBuilder
{
    public static string Build(string? endpointIp, int socksPort = 10808, AppSettings? settings = null)
    {
        settings ??= new AppSettings();
        if (settings.Validate() is { } error) throw new ArgumentException(error);
        var dns = settings.DnsProvider switch
        {
            "Google" => (Address: "8.8.8.8", Name: "dns.google"),
            "Quad9" => (Address: "9.9.9.9", Name: "dns.quad9.net"),
            _ => (Address: "1.1.1.1", Name: "cloudflare-dns.com")
        };
        var rules = new List<object>();

        if (IPAddress.TryParse(endpointIp, out var parsed))
        {
            var cidr = parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                ? $"{parsed}/32"
                : $"{parsed}/128";

            // Hard bypass for the currently selected VPN server. This prevents the
            // server connection itself from being captured by the TUN and looping.
            rules.Add(new Dictionary<string, object?>
            {
                ["ip_cidr"] = new[] { cidr },
                ["action"] = "route",
                ["outbound"] = "direct"
            });
        }

        // Process bypass is a second guard. If the endpoint changes or Xray opens
        // another socket, the Xray process still exits through the physical NIC.
        rules.Add(new Dictionary<string, object?>
        {
            ["process_name"] = new[] { "xray.exe" },
            ["action"] = "route",
            ["outbound"] = "direct"
        });

        rules.Add(new Dictionary<string, object?>
        {
            ["action"] = "sniff"
        });

        var root = new Dictionary<string, object?>
        {
            ["log"] = new { level = settings.VerboseLogs ? "info" : "warn", timestamp = true, disabled = false },
            ["dns"] = new Dictionary<string, object?>
            {
                ["servers"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "https",
                        ["tag"] = "doh",
                        ["server"] = dns.Address,
                        ["server_port"] = 443,
                        ["path"] = "/dns-query",
                        ["tls"] = new { enabled = true, server_name = dns.Name },
                        ["detour"] = "xray"
                    }
                },
                ["final"] = "doh",
                ["strategy"] = "prefer_ipv4",
                ["cache_capacity"] = 4096
            },
            ["inbounds"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "tun",
                    ["tag"] = "tun-in",
                    ["interface_name"] = AppPaths.TunInterfaceName,
                    ["address"] = new [] { "172.19.0.1/30", "fdfe:dcba:9876::1/126" },
                    ["mtu"] = settings.Mtu,
                    ["auto_route"] = true,
                    ["strict_route"] = true,
                    ["dns_mode"] = "hijack"
                }
            },
            ["outbounds"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "socks",
                    ["tag"] = "xray",
                    ["server"] = "127.0.0.1",
                    ["server_port"] = socksPort,
                    ["version"] = "5"
                },
                new Dictionary<string, object?>
                {
                    ["type"] = "direct",
                    ["tag"] = "direct"
                }
            },
            ["route"] = new Dictionary<string, object?>
            {
                ["rules"] = rules,
                ["final"] = "xray",
                // sing-box has a native Windows/macOS/Linux mechanism specifically
                // intended to keep direct outbound traffic on the real default NIC
                // when TUN changes the system route table.
                ["auto_detect_interface"] = true
            }
        };

        return JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true });
    }
}
