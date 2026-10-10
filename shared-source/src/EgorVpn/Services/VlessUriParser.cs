using EgorVpn.Models;

namespace EgorVpn.Services;

public static class VlessUriParser
{
    public static bool TryParse(string line, out VpnNode? node, out string? error)
    {
        node = null;
        error = null;
        try
        {
            line = line.Trim();
            if (!line.StartsWith("vless://", StringComparison.OrdinalIgnoreCase))
            {
                error = "This client supports VLESS nodes. Expected vless://";
                return false;
            }

            var uri = new Uri(line);
            var q = QueryString.Parse(uri.Query);
            var rawName = Uri.UnescapeDataString(uri.Fragment.TrimStart('#'));
            var (country, flag) = CountryResolver.Resolve(rawName);

            var transportRaw = q.Get("type", q.Get("net", "tcp")).ToLowerInvariant();
            var transport = transportRaw switch
            {
                "tcp" => "raw",
                "ws" => "websocket",
                "httpupgrade" => "httpupgrade",
                "splithttp" => "xhttp",
                "xhttp" => "xhttp",
                "grpc" => "grpc",
                "kcp" => "mkcp",
                _ => transportRaw
            };

            var security = q.Get("security", "none").ToLowerInvariant();
            var hostHeader = q.Get("host");
            var path = q.Get("path", "/");
            if (string.IsNullOrWhiteSpace(path)) path = "/";

            node = new VpnNode
            {
                RawUri = line,
                Protocol = "vless",
                UserId = Uri.UnescapeDataString(uri.UserInfo),
                Host = uri.Host,
                Port = uri.Port > 0 ? uri.Port : 443,
                Name = string.IsNullOrWhiteSpace(rawName) ? $"{flag} {country}" : rawName,
                Country = country,
                CountryFlag = flag,
                Encryption = q.Get("encryption", "none"),
                Flow = q.Get("flow"),
                Security = security,
                Transport = transport,
                Sni = q.Get("sni", q.Get("serverName", uri.Host)),
                Fingerprint = q.Get("fp", q.Get("fingerprint", "chrome")),
                PublicKey = q.Get("pbk", q.Get("publicKey", q.Get("password"))),
                ShortId = q.Get("sid", q.Get("shortId")),
                SpiderX = q.Get("spx", q.Get("spiderX")),
                Mldsa65Verify = q.Get("pqv", q.Get("mldsa65Verify")),
                Alpn = q.Get("alpn"),
                AllowInsecure = q.Get("allowInsecure", q.Get("insecure")) is "1" or "true",
                Path = path,
                HostHeader = hostHeader,
                Mode = q.Get("mode", "auto"),
                ServiceName = q.Get("serviceName", q.Get("service", path.TrimStart('/'))),
                ExtraJson = q.Get("extra"),
            };

            if (string.IsNullOrWhiteSpace(node.UserId) || string.IsNullOrWhiteSpace(node.Host))
            {
                error = "Missing VLESS id or host";
                node = null;
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
