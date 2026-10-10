namespace EgorVpn.Models;

public sealed class VpnNode
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Node";
    public string Country { get; set; } = "Other";
    public string CountryFlag { get; set; } = "🌐";
    public string RawUri { get; set; } = "";

    public string Protocol { get; set; } = "vless";
    public string UserId { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 443;
    public string Encryption { get; set; } = "none";
    public string Flow { get; set; } = "";

    public string Security { get; set; } = "tls";
    public string Transport { get; set; } = "raw";
    public string Sni { get; set; } = "";
    public string Fingerprint { get; set; } = "chrome";
    public string PublicKey { get; set; } = "";
    public string ShortId { get; set; } = "";
    public string SpiderX { get; set; } = "";
    public string Mldsa65Verify { get; set; } = "";
    public string Alpn { get; set; } = "";
    public bool AllowInsecure { get; set; }

    public string Path { get; set; } = "/";
    public string HostHeader { get; set; } = "";
    public string Mode { get; set; } = "auto";
    public string ServiceName { get; set; } = "";
    public string ExtraJson { get; set; } = "";

    public long? PingMs { get; set; }
    public string PingText => PingMs is null ? "—" : $"{PingMs} ms";
    public string Subtitle => $"{Protocol.ToUpperInvariant()} / {Transport.ToUpperInvariant()} / {Security.ToUpperInvariant()}";
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? $"{CountryFlag} {Country}" : Name;
}
