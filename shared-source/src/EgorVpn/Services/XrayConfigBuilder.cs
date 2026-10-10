using System.Text.Json;
using EgorVpn.Models;

namespace EgorVpn.Services;

public static class XrayConfigBuilder
{
    public static string Build(VpnNode node, string? resolvedServerIp = null, int socksPort = 10808, bool verbose = false)
    {
        var serverAddress = string.IsNullOrWhiteSpace(resolvedServerIp) ? node.Host : resolvedServerIp;
        var stream = new Dictionary<string, object?>
        {
            ["method"] = node.Transport,
            ["security"] = node.Security
        };

        switch (node.Transport)
        {
            case "xhttp":
                var xhttp = new Dictionary<string, object?>
                {
                    ["path"] = node.Path,
                    ["mode"] = node.Mode
                };
                if (!string.IsNullOrWhiteSpace(node.HostHeader)) xhttp["host"] = node.HostHeader;
                MergeExtraJson(xhttp, node.ExtraJson);
                stream["xhttpSettings"] = xhttp;
                break;
            case "websocket":
                var ws = new Dictionary<string, object?>
                {
                    ["path"] = node.Path
                };
                if (!string.IsNullOrWhiteSpace(node.HostHeader))
                {
                    ws["host"] = node.HostHeader;
                    ws["headers"] = new Dictionary<string, string> { ["Host"] = node.HostHeader };
                }
                stream["wsSettings"] = ws;
                break;
            case "httpupgrade":
                var httpUpgrade = new Dictionary<string, object?>
                {
                    ["path"] = node.Path
                };
                if (!string.IsNullOrWhiteSpace(node.HostHeader)) httpUpgrade["host"] = node.HostHeader;
                stream["httpupgradeSettings"] = httpUpgrade;
                break;
            case "grpc":
                stream["grpcSettings"] = new Dictionary<string, object?>
                {
                    ["serviceName"] = node.ServiceName
                };
                break;
            case "raw":
                stream["rawSettings"] = new Dictionary<string, object?>();
                break;
        }

        if (node.Security.Equals("reality", StringComparison.OrdinalIgnoreCase))
        {
            stream["realitySettings"] = new Dictionary<string, object?>
            {
                ["serverName"] = string.IsNullOrWhiteSpace(node.Sni) ? node.Host : node.Sni,
                ["fingerprint"] = string.IsNullOrWhiteSpace(node.Fingerprint) ? "chrome" : node.Fingerprint,
                ["password"] = node.PublicKey,
                ["shortId"] = node.ShortId,
                ["spiderX"] = node.SpiderX,
                ["mldsa65Verify"] = EmptyToNull(node.Mldsa65Verify)
            };
        }
        else if (node.Security.Equals("tls", StringComparison.OrdinalIgnoreCase))
        {
            var tls = new Dictionary<string, object?>
            {
                ["serverName"] = string.IsNullOrWhiteSpace(node.Sni) ? node.Host : node.Sni,
                ["fingerprint"] = string.IsNullOrWhiteSpace(node.Fingerprint) ? "chrome" : node.Fingerprint,
                ["allowInsecure"] = node.AllowInsecure
            };
            if (!string.IsNullOrWhiteSpace(node.Alpn))
                tls["alpn"] = node.Alpn.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            stream["tlsSettings"] = tls;
        }

        var settings = new Dictionary<string, object?>
        {
            ["address"] = serverAddress,
            ["port"] = node.Port,
            ["id"] = node.UserId,
            ["encryption"] = string.IsNullOrWhiteSpace(node.Encryption) ? "none" : node.Encryption,
        };
        if (!string.IsNullOrWhiteSpace(node.Flow)) settings["flow"] = node.Flow;

        var root = new Dictionary<string, object?>
        {
            ["log"] = new { loglevel = verbose ? "info" : "warning", access = verbose ? "" : "none" },
            ["inbounds"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["tag"] = "local-socks",
                    ["listen"] = "127.0.0.1",
                    ["port"] = socksPort,
                    ["protocol"] = "socks",
                    ["settings"] = new { auth = "noauth", udp = true, ip = "127.0.0.1" }
                }
            },
            ["outbounds"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["tag"] = "proxy",
                    ["protocol"] = "vless",
                    ["settings"] = settings,
                    ["streamSettings"] = stream
                },
                new { tag = "direct", protocol = "freedom" },
                new { tag = "block", protocol = "blackhole" }
            }
        };

        return JsonSerializer.Serialize(root, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });
    }

    private static void MergeExtraJson(Dictionary<string, object?> target, string? extraJson)
    {
        if (string.IsNullOrWhiteSpace(extraJson)) return;
        try
        {
            using var document = JsonDocument.Parse(extraJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return;
            foreach (var property in document.RootElement.EnumerateObject())
                target[property.Name] = property.Value.Clone();
        }
        catch (JsonException)
        {
            // Subscription-provided `extra` is optional. Ignore malformed JSON here;
            // the core validator still guards the final generated configuration.
        }
    }

    private static object? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
