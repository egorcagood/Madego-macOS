// Derived from Windows 0.19.2 SubscriptionService; baseline remains immutable.
// Hash and extracted methods are recorded in build-metadata/core-provenance.json.
using System.Text;
using System.Text.Json;
using EgorVpn.Models;
using EgorVpn.Services;
namespace Madego.Core;
public static class SubscriptionParser
{
    private static bool Valid(VpnNode n) => Guid.TryParse(n.UserId, out _) && n.Port is >= 1 and <= 65535 && Uri.CheckHostName(n.Host.Trim('[', ']')) != UriHostNameType.Unknown;
    public static IReadOnlyList<VpnNode> Parse(string body)
    {
        if (body.Length > 8 * 1024 * 1024) throw new ArgumentException("Subscription exceeds 8 MiB.");
        var nodes = new List<VpnNode>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var text = DecodeMaybeBase64(body);

        // Classic XRAY_BASE64 / plain share-link subscription.
        foreach (var line in text.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith("vless://", StringComparison.OrdinalIgnoreCase)) continue;
            if (!seen.Add(line)) continue;
            if (VlessUriParser.TryParse(line, out var node, out _) && node is not null && Valid(node) && new Uri(line).Port != 0)
                nodes.Add(node);
        }

        if (nodes.Count > 0)
            return BoundLabels(nodes);

        // Happ/Remnawave may return either one full XRAY_JSON config or an array
        // of full configs. Each config normally contains one VLESS outbound and a
        // human-readable `remarks` field.
        TryParseXrayJson(text, nodes, seen);
        return BoundLabels(nodes);
    }

    static IReadOnlyList<VpnNode> BoundLabels(List<VpnNode> nodes) {
        foreach(var node in nodes) {var name=new string(node.Name.Where(c=>!char.IsControl(c)).Take(160).ToArray()).Trim();
            if(name.Length>0&&char.IsHighSurrogate(name[^1]))name=name[..^1];node.Name=string.IsNullOrEmpty(name)?"VLESS server":name;}
        return nodes;
    }

    private static void TryParseXrayJson(string text, List<VpnNode> nodes, HashSet<string> seen)
    {
        var trimmed = text.Trim().TrimStart('\uFEFF');
        if (!(trimmed.StartsWith('{') || trimmed.StartsWith('['))) return;

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in doc.RootElement.EnumerateArray())
                    ParseXrayConfig(item, nodes, seen);
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                ParseXrayConfig(doc.RootElement, nodes, seen);
            }
        }
        catch (JsonException)
        {
            // Not JSON; another compatibility profile can still be attempted.
        }
    }

    private static void ParseXrayConfig(JsonElement config, List<VpnNode> nodes, HashSet<string> seen)
    {
        if (config.ValueKind != JsonValueKind.Object) return;

        var remarks = GetString(config, "remarks");
        if (!config.TryGetProperty("outbounds", out var outbounds) || outbounds.ValueKind != JsonValueKind.Array)
            return;

        var vlessIndex = 0;
        foreach (var outbound in outbounds.EnumerateArray())
        {
            if (!GetString(outbound, "protocol").Equals("vless", StringComparison.OrdinalIgnoreCase))
                continue;

            vlessIndex++;
            var tag = GetString(outbound, "tag");
            var name = !string.IsNullOrWhiteSpace(remarks)
                ? remarks
                : (!string.IsNullOrWhiteSpace(tag) ? tag : $"VLESS {vlessIndex}");

            var node = ParseVlessOutbound(outbound, name);
            if (node is null || !Valid(node)) continue;

            var key = JsonSerializer.Serialize(new { node.UserId, node.Host, node.Port, node.Encryption, node.Flow, node.Transport, node.Security, node.Sni, node.Fingerprint, node.PublicKey, node.ShortId, node.SpiderX, node.Mldsa65Verify, node.Alpn, node.AllowInsecure, node.Path, node.HostHeader, node.Mode, node.ServiceName, node.ExtraJson });
            if (seen.Add(key))
                nodes.Add(node);
        }
    }

    private static VpnNode? ParseVlessOutbound(JsonElement outbound, string name)
    {
        if (!outbound.TryGetProperty("settings", out var settings) || settings.ValueKind != JsonValueKind.Object)
            return null;

        string host = "";
        int port = 443;
        string userId = "";
        string encryption = "none";
        string flow = "";

        // Standard Xray client config: settings.vnext[0].users[0]
        if (settings.TryGetProperty("vnext", out var vnext) && vnext.ValueKind == JsonValueKind.Array && vnext.GetArrayLength() > 0)
        {
            var server = vnext[0];
            if (server.ValueKind != JsonValueKind.Object) return null;
            host = GetString(server, "address");
            port = GetInt(server, "port", 443);

            if (server.TryGetProperty("users", out var users) && users.ValueKind == JsonValueKind.Array && users.GetArrayLength() > 0)
            {
                var user = users[0];
                if (user.ValueKind != JsonValueKind.Object) return null;
                userId = GetString(user, "id");
                encryption = GetString(user, "encryption", "none");
                flow = GetString(user, "flow");
            }
        }
        else
        {
            // Newer simplified shape accepted by current Xray builds.
            host = GetString(settings, "address");
            port = GetInt(settings, "port", 443);
            userId = GetString(settings, "id");
            encryption = GetString(settings, "encryption", "none");
            flow = GetString(settings, "flow");
        }

        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(userId))
            return null;

        var transport = "raw";
        var security = "none";
        var sni = host;
        var fingerprint = "chrome";
        var publicKey = "";
        var shortId = "";
        var spiderX = "";
        var mldsa65Verify = "";
        var alpn = "";
        var allowInsecure = false;
        var path = "/";
        var hostHeader = "";
        var mode = "auto";
        var serviceName = "";
        var extraJson = "";

        if (outbound.TryGetProperty("streamSettings", out var stream) && stream.ValueKind == JsonValueKind.Object)
        {
            var network = GetString(stream, "network", GetString(stream, "method", "tcp"));
            transport = NormalizeTransport(network);
            security = GetString(stream, "security", "none").ToLowerInvariant();

            if (security == "reality" && stream.TryGetProperty("realitySettings", out var reality) && reality.ValueKind == JsonValueKind.Object)
            {
                sni = GetString(reality, "serverName", host);
                fingerprint = GetString(reality, "fingerprint", "chrome");
                publicKey = GetString(reality, "publicKey", GetString(reality, "password"));
                shortId = GetString(reality, "shortId");
                spiderX = GetString(reality, "spiderX");
                mldsa65Verify = GetString(reality, "mldsa65Verify");
            }
            else if (security == "tls" && stream.TryGetProperty("tlsSettings", out var tls) && tls.ValueKind == JsonValueKind.Object)
            {
                sni = GetString(tls, "serverName", host);
                fingerprint = GetString(tls, "fingerprint", "chrome");
                allowInsecure = GetBool(tls, "allowInsecure");
                alpn = ReadStringArrayOrString(tls, "alpn");
            }

            switch (transport)
            {
                case "websocket":
                    if (stream.TryGetProperty("wsSettings", out var ws) && ws.ValueKind == JsonValueKind.Object)
                    {
                        path = GetString(ws, "path", "/");
                        hostHeader = GetString(ws, "host");
                        if (string.IsNullOrWhiteSpace(hostHeader) && ws.TryGetProperty("headers", out var headers) && headers.ValueKind == JsonValueKind.Object)
                            hostHeader = GetString(headers, "Host");
                    }
                    break;

                case "xhttp":
                    if (stream.TryGetProperty("xhttpSettings", out var xhttp) && xhttp.ValueKind == JsonValueKind.Object)
                    {
                        path = GetString(xhttp, "path", "/");
                        mode = GetString(xhttp, "mode", "auto");
                        hostHeader = GetString(xhttp, "host");
                        extraJson = GetUnknownObjectProperties(xhttp, ["path", "mode", "host"]);
                    }
                    break;

                case "httpupgrade":
                    if (stream.TryGetProperty("httpupgradeSettings", out var hu) && hu.ValueKind == JsonValueKind.Object)
                    {
                        path = GetString(hu, "path", "/");
                        hostHeader = GetString(hu, "host");
                    }
                    break;

                case "grpc":
                    if (stream.TryGetProperty("grpcSettings", out var grpc) && grpc.ValueKind == JsonValueKind.Object)
                        serviceName = GetString(grpc, "serviceName");
                    break;
            }
        }

        var (country, flag) = CountryResolver.Resolve(name);
        return new VpnNode
        {
            Name = name,
            Country = country,
            CountryFlag = flag,
            RawUri = outbound.GetRawText(),
            Protocol = "vless",
            UserId = userId,
            Host = host,
            Port = port,
            Encryption = string.IsNullOrWhiteSpace(encryption) ? "none" : encryption,
            Flow = flow,
            Security = security,
            Transport = transport,
            Sni = sni,
            Fingerprint = fingerprint,
            PublicKey = publicKey,
            ShortId = shortId,
            SpiderX = spiderX,
            Mldsa65Verify = mldsa65Verify,
            Alpn = alpn,
            AllowInsecure = allowInsecure,
            Path = string.IsNullOrWhiteSpace(path) ? "/" : path,
            HostHeader = hostHeader,
            Mode = string.IsNullOrWhiteSpace(mode) ? "auto" : mode,
            ServiceName = serviceName,
            ExtraJson = extraJson,
        };
    }

    private static string NormalizeTransport(string network)
    {
        return network.ToLowerInvariant() switch
        {
            "tcp" => "raw",
            "raw" => "raw",
            "ws" => "websocket",
            "websocket" => "websocket",
            "httpupgrade" => "httpupgrade",
            "splithttp" => "xhttp",
            "xhttp" => "xhttp",
            "grpc" => "grpc",
            "kcp" => "mkcp",
            "mkcp" => "mkcp",
            var other => other,
        };
    }

    private static string DecodeMaybeBase64(string input)
    {
        var trimmed = input.Trim().TrimStart('\uFEFF');
        if (trimmed.Contains("://", StringComparison.Ordinal) || trimmed.StartsWith('{') || trimmed.StartsWith('['))
            return trimmed;

        try
        {
            var compact = new string(trimmed.Where(c => !char.IsWhiteSpace(c)).ToArray())
                .Replace('-', '+').Replace('_', '/');
            compact += new string('=', (4 - compact.Length % 4) % 4);
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(compact));
            return decoded.Contains("://", StringComparison.Ordinal) || decoded.TrimStart().StartsWith('{') || decoded.TrimStart().StartsWith('[')
                ? decoded
                : trimmed;
        }
        catch { return trimmed; }
    }

    private static string GetString(JsonElement obj, string name, string fallback = "")
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var value))
        {
            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? fallback,
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => fallback,
            };
        }
        return fallback;
    }

    private static int GetInt(JsonElement obj, string name, int fallback)
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var value))
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var i)) return i;
            if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out i)) return i;
            return 0; // An explicitly invalid port must not fall back to 443.
        }
        return fallback;
    }

    private static bool GetBool(JsonElement obj, string name)
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var value))
        {
            if (value.ValueKind == JsonValueKind.True) return true;
            if (value.ValueKind == JsonValueKind.False) return false;
            if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var b)) return b;
        }
        return false;
    }

    private static string ReadStringArrayOrString(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var value)) return "";
        if (value.ValueKind == JsonValueKind.String) return value.GetString() ?? "";
        if (value.ValueKind == JsonValueKind.Array)
            return string.Join(',', value.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)));
        return "";
    }

    private static string GetUnknownObjectProperties(JsonElement obj, string[] known)
    {
        if (obj.ValueKind != JsonValueKind.Object) return "";
        var knownSet = new HashSet<string>(known, StringComparer.OrdinalIgnoreCase);
        var extra = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in obj.EnumerateObject())
        {
            if (!knownSet.Contains(property.Name))
                extra[property.Name] = property.Value.Clone();
        }
        return extra.Count == 0 ? "" : JsonSerializer.Serialize(extra);
    }

}
