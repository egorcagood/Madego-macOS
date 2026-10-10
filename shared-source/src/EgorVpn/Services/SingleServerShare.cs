using EgorVpn.Models;

namespace EgorVpn.Services;

public static class SingleServerShare
{
    const int MaxLength = 32768;
    public static bool IsLocal(SubscriptionGroup group) => string.IsNullOrEmpty(group.Url);
    public static SubscriptionGroup Import(string input)
    {
        if (string.IsNullOrWhiteSpace(input) || input.Length > MaxLength)
            throw new ArgumentException("Вставьте полную конфигурацию одного сервера VLESS.");
        input = input.Trim();
        if (!input.StartsWith("vless://", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Нужна конфигурация одного сервера vless://, а не ссылка на подписку.");
        if (Uri.UnescapeDataString(input).IndexOf("vless://", 8, StringComparison.OrdinalIgnoreCase) >= 0)
            throw new ArgumentException("Конфигурация содержит ещё один сервер. Отправьте только выбранный.");
        if (input.Any(char.IsControl) || !input.StartsWith("vless://", StringComparison.OrdinalIgnoreCase) ||
            !VlessUriParser.TryParse(input, out var node, out _) || node is null ||
            !Guid.TryParse(node.UserId, out _) || node.Port is < 1 or > 65535)
            throw new ArgumentException("Нужна одна ссылка vless://. Ссылка на подписку и несколько серверов здесь не подходят.");
        if (IPAddress.TryParse(node.Host.Trim('[', ']'), out var ip)) node.Host = ip.ToString();
        // No subscription URL or provider request. The ordinary state cache owns this one node.
        return new SubscriptionGroup { Name = "Полученный сервер", Url = "", Nodes = new() { node }, UpdatedAt = DateTimeOffset.Now };
    }
    public static string ConnectionKey(VpnNode source)
    {
        var node = Import(Export(source)).Nodes.Single();
        var value = System.Text.Json.JsonSerializer.Serialize(new { UserId = Guid.Parse(node.UserId).ToString(),
            Host = node.Host.ToLowerInvariant(), node.Port, node.Protocol, node.Transport, node.Security,
            node.Encryption, node.Flow, node.Sni, node.Fingerprint, node.PublicKey, node.ShortId,
            node.SpiderX, node.Mldsa65Verify, node.Alpn, node.AllowInsecure, node.Path, node.HostHeader,
            node.Mode, node.ServiceName, node.ExtraJson });
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
    }
    public static string Export(VpnNode node)
    {
        if (node.RawUri.StartsWith("vless://", StringComparison.OrdinalIgnoreCase))
        { _ = Import(node.RawUri); return node.RawUri.Trim(); }
        if (node.Protocol != "vless") throw new ArgumentException("Можно передать сервер VLESS.");
        // JSON subscriptions have no original VLESS URI. Export the selected node's known parameters.
        var host = node.Host.Trim('[', ']');
        if (host.Contains(':')) host = "[" + host + "]";
        var fields = new List<KeyValuePair<string, string>>();
        void Add(string key, string value) { if (!string.IsNullOrEmpty(value)) fields.Add(new(key, value)); }
        Add("type", node.Transport switch { "raw" => "tcp", "websocket" => "ws", "mkcp" => "kcp", _ => node.Transport });
        Add("security", node.Security); Add("encryption", node.Encryption); Add("flow", node.Flow);
        Add("sni", node.Sni); Add("fp", node.Fingerprint); Add("pbk", node.PublicKey); Add("sid", node.ShortId);
        Add("spx", node.SpiderX); Add("pqv", node.Mldsa65Verify); Add("alpn", node.Alpn);
        Add("path", node.Path); Add("host", node.HostHeader); Add("mode", node.Mode);
        Add("serviceName", node.ServiceName); Add("extra", node.ExtraJson);
        if (node.AllowInsecure) Add("allowInsecure", "1");
        var uri = "vless://" + Uri.EscapeDataString(node.UserId) + "@" + host + ":" + node.Port + "?" +
            string.Join("&", fields.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value))) + "#" + Uri.EscapeDataString(node.Name);
        _ = Import(uri); return uri;
    }
}
