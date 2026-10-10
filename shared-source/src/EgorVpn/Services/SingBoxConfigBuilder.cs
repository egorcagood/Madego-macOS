using EgorVpn.Models;
using EgorVpn.Routing;
using System.Net;
using System.Text.Json;

namespace EgorVpn.Services;

public static class SingBoxConfigBuilder
{
    public static string Build(string? endpointIp, int socksPort = 10808, AppSettings? settings = null,
        RoutingPolicySnapshot? policy = null, string? fakeIpCachePath = null, bool retainFakeIpMappings = false)
    {
        settings ??= new AppSettings();
        if (settings.Validate() is { } error) throw new ArgumentException(error);
        policy ??= RoutingPolicySnapshot.Create(settings.RoutingMode, settings.RuMode);
        var ruMode = policy.Mode == RoutingMode.RuMode;
        var keepFakeIp = ruMode || retainFakeIpMappings;
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

        // In RU Mode only our current DNS FakeIP mapping can provide a DIRECT
        // hostname. Sniffing a real IP's ECH outer SNI would not identify the
        // actual target, and could turn an unknown/shared IP into DIRECT.
        if (!ruMode) rules.Add(new Dictionary<string, object?>
        {
            ["action"] = "sniff"
        });

        var dnsRules = new List<object>();
        if (ruMode)
        {
            AddDomainRules(rules, policy.ProxyRules, "route", "outbound", "xray");
            AddDomainRules(dnsRules, policy.ProxyRules, "route", "server", "doh");

            // TUN's pre-match flow path requires real destination addresses for
            // FakeIP DIRECT. Resolve the known name explicitly before routing;
            // the chosen transport bypasses FakeIP DNS classification.
            AddDomainRules(rules, policy.DirectRules, "resolve", "server", "directdns",
                new Dictionary<string, object?> { ["strategy"] = "prefer_ipv4" });
            AddDomainRules(rules, policy.DirectRules, "route", "outbound", "direct");
            AddDomainRules(dnsRules, policy.DirectRules, "route", "server", "direct-fakeip",
                new Dictionary<string, object?> { ["query_type"] = new[] { "A", "AAAA" } });
            AddDomainRules(dnsRules, policy.DirectRules, "route", "server", "directdns");
        }

        var dnsServers = new List<object>
        {
            new Dictionary<string, object?>
            {
                ["type"] = "https", ["tag"] = "doh", ["server"] = dns.Address,
                ["server_port"] = 443, ["path"] = "/dns-query",
                ["tls"] = new { enabled = true, server_name = dns.Name }, ["detour"] = "xray"
            }
        };
        var directOutbound = new Dictionary<string, object?> { ["type"] = "direct", ["tag"] = "direct" };
        if (ruMode && policy.DirectRules.Count > 0)
        {
            // Literal upstream + explicit detour avoids sending an OS local DNS
            // query back into the TUN DNS interception path. It is never fallback.
            dnsServers.Add(new Dictionary<string, object?>
            {
                ["type"] = "udp", ["tag"] = "directdns", ["server"] = dns.Address,
                ["server_port"] = 53, ["detour"] = "direct"
            });
            directOutbound["domain_resolver"] = "directdns";
        }
        if (keepFakeIp)
        {
            if (!directOutbound.ContainsKey("domain_resolver")) directOutbound["domain_resolver"] = "doh";
            // Dormant in FullVPN or with no enabled DIRECT exceptions: retained
            // virtual identities still receive the current domain policy.
            dnsServers.Add(new Dictionary<string, object?>
            {
                ["type"] = "fakeip", ["tag"] = "direct-fakeip",
                ["inet4_range"] = "198.18.0.0/15", ["inet6_range"] = "fc00::/18"
            });
        }

        var root = new Dictionary<string, object?>
        {
            ["log"] = new { level = settings.VerboseLogs ? "info" : "warn", timestamp = true, disabled = false },
            ["dns"] = new Dictionary<string, object?>
            {
                ["servers"] = dnsServers,
                ["rules"] = dnsRules,
                ["final"] = "doh",
                ["strategy"] = "prefer_ipv4",
                // Shared CDN addresses cannot establish a domain's DIRECT policy.
                ["reverse_mapping"] = false,
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
                directOutbound
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

        if (keepFakeIp)
        {
            fakeIpCachePath ??= Path.Combine(AppPaths.RuntimeDirectory, "ru-fakeip-v1.db");
            if (!Path.IsPathFullyQualified(fakeIpCachePath))
                throw new ArgumentException("FakeIP cache path must be absolute.", nameof(fakeIpCachePath));
            // Requires the audited 1.14.1-madego-ru1 core. It commits stable
            // virtual bindings and allocator metadata before a DNS response,
            // and fails instead of recycling addresses or resetting corruption.
            root["experimental"] = new Dictionary<string, object?>
            {
                ["cache_file"] = new Dictionary<string, object?>
                {
                    ["enabled"] = true,
                    ["path"] = fakeIpCachePath,
                    ["store_fakeip"] = true,
                    ["store_dns"] = false
                }
            };
        }

        return JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true });
    }

    private static void AddDomainRules(List<object> destination, IReadOnlyList<DomainRule> domains,
        string action, string targetKey, string target, IReadOnlyDictionary<string, object?>? options = null)
    {
        // Separate rules make Exact/Suffix alternatives explicit; they are not
        // combined as an AND and there is no substring/keyword DIRECT matching.
        foreach (var match in new[] { DomainMatch.Exact, DomainMatch.Suffix })
        {
            var values = domains.Where(rule => rule.Match == match).Select(rule => rule.Value).ToArray();
            if (values.Length == 0) continue;
            var rule = new Dictionary<string, object?>
            {
                [match == DomainMatch.Exact ? "domain" : "domain_suffix"] = values,
                ["action"] = action,
                [targetKey] = target
            };
            if (options is not null) foreach (var option in options) rule[option.Key] = option.Value;
            destination.Add(rule);
        }
    }
}
