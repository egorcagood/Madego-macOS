using System.Text.Json;
using EgorVpn.Services;
using EgorVpn.Routing;
using EgorVpn.Models;

var count = 0;
void Check(bool result, string name)
{
    if (!result) throw new Exception("FAIL " + name);
    Console.WriteLine("PASS " + name);
    count++;
}
JsonDocument Build(RoutingMode mode, RuRoutingPreferences? preferences = null, string? endpoint = "203.0.113.7", string provider = "Cloudflare") =>
    JsonDocument.Parse(SingBoxConfigBuilder.Build(endpoint, settings: new AppSettings { RoutingMode = mode, RuMode = preferences ?? new(), DnsProvider = provider }, fakeIpCachePath: Path.Combine(Path.GetTempPath(), "madego-ru-config-checks", "ru-fakeip-v1.db")));

bool DomainMatches(JsonElement rule, string? host)
{
    if (host is null) return false;
    return (rule.TryGetProperty("domain", out var exact) && exact.EnumerateArray().Any(v => v.GetString() == host)) ||
        (rule.TryGetProperty("domain_suffix", out var suffix) && suffix.EnumerateArray().Any(v => host == v.GetString() || host.EndsWith("." + v.GetString(), StringComparison.Ordinal)));
}
string Route(JsonElement config, string? host)
{
    foreach (var rule in config.GetProperty("route").GetProperty("rules").EnumerateArray())
        if (rule.GetProperty("action").GetString() == "route" && DomainMatches(rule, host)) return rule.GetProperty("outbound").GetString()!;
    return config.GetProperty("route").GetProperty("final").GetString()!;
}
string Dns(JsonElement config, string host, string query = "A")
{
    foreach (var rule in config.GetProperty("dns").GetProperty("rules").EnumerateArray())
    {
        if (!DomainMatches(rule, host)) continue;
        if (rule.TryGetProperty("query_type", out var types) && !types.EnumerateArray().Any(t => t.GetString() == query)) continue;
        return rule.GetProperty("server").GetString()!;
    }
    return config.GetProperty("dns").GetProperty("final").GetString()!;
}

using var full = Build(RoutingMode.FullVpn);
using var ru = Build(RoutingMode.RuMode);
var f = full.RootElement;
var r = ru.RootElement;
Check(r.TryGetProperty("experimental", out var exp) && exp.GetProperty("cache_file").GetProperty("store_fakeip").GetBoolean(), "RU virtual identities persist across core restarts");
Check(!exp.GetProperty("cache_file").GetProperty("store_dns").GetBoolean(), "ordinary DNS answers are not persisted as DIRECT authority");
Check(!f.GetProperty("dns").GetProperty("reverse_mapping").GetBoolean() && !r.GetProperty("dns").GetProperty("reverse_mapping").GetBoolean(), "real DNS answers never grant shared-IP DIRECT");
Check(Route(f, "esia.gosuslugi.ru") == "xray" && Dns(f, "esia.gosuslugi.ru") == "doh", "FullVPN does not apply Russian DIRECT policy");
Check(f.GetProperty("dns").GetProperty("servers").GetArrayLength() == 1 && f.GetProperty("dns").GetProperty("rules").GetArrayLength() == 0, "FullVPN has only VPN resolver");
Check(Route(r, "gosuslugi.ru") == "direct" && Route(r, "esia.gosuslugi.ru") == "direct", "RU root and subdomains DIRECT");
Check(Route(r, "notgosuslugi.ru") == "xray" && Route(r, "gosuslugi.ru.evil.example") == "xray", "suffix boundaries do not expand DIRECT");
Check(Dns(r, "gosuslugi.ru") == "direct-fakeip" && Dns(r, "gosuslugi.ru", "AAAA") == "direct-fakeip", "DIRECT A and AAAA use scoped domain evidence");
Check(Dns(r, "gosuslugi.ru", "HTTPS") == "directdns", "DIRECT non-address queries use physical resolver");
Check(Route(r, null) == "xray", "literal IPv4 IPv6 shared CDN IP ECH and cached real IP stay proxy without trusted domain");
Check(Route(r, "unknown.example") == "xray" && Dns(r, "unknown.example") == "doh", "unknown route and DNS stay VPN");
Check(Route(r, "youtube.com") == "xray" && Route(r, "discord.com") == "xray" && Dns(r, "youtube.com") == "doh" && Dns(r, "discord.com") == "doh", "protected services route and DNS stay VPN");
Check(!r.GetProperty("route").GetProperty("rules").EnumerateArray().Any(x => x.GetProperty("action").GetString() == "sniff"), "RU does not trust ECH outer SNI HTTP or QUIC sniffed names");
Check(f.GetProperty("route").GetProperty("rules").EnumerateArray().Any(x => x.GetProperty("action").GetString() == "sniff"), "FullVPN keeps original sniff pipeline");
Check(r.GetProperty("inbounds")[0].GetProperty("strict_route").GetBoolean() && r.GetProperty("inbounds")[0].GetProperty("dns_mode").GetString() == "hijack", "strict TUN routing and native DNS interception retained");
Check(r.GetProperty("route").GetProperty("auto_detect_interface").GetBoolean(), "DIRECT binds physical default interface through native mechanism");
var servers = r.GetProperty("dns").GetProperty("servers").EnumerateArray().ToArray();
Check(servers.Single(x => x.GetProperty("tag").GetString() == "doh").GetProperty("detour").GetString() == "xray", "VPN DNS HTTPS transport uses Xray");
var directDns = servers.Single(x => x.GetProperty("tag").GetString() == "directdns");
Check(directDns.GetProperty("type").GetString() == "udp" && directDns.GetProperty("server").GetString() == "1.1.1.1" && directDns.GetProperty("detour").GetString() == "direct", "ordinary DNS uses literal upstream and direct detour avoiding OS loop");
Check(r.GetProperty("dns").GetProperty("final").GetString() == "doh", "no common direct DNS fallback");
var routeRules = r.GetProperty("route").GetProperty("rules").EnumerateArray().ToArray();
var directIndex = Array.FindIndex(routeRules, x => x.GetProperty("action").GetString() == "route" && DomainMatches(x, "gosuslugi.ru"));
Check(directIndex > 0 && routeRules.Take(directIndex).Any(x => x.GetProperty("action").GetString() == "resolve" && DomainMatches(x, "gosuslugi.ru") && x.GetProperty("server").GetString() == "directdns"), "FakeIP DIRECT resolves actual destination before TUN pre-match flow");
Check(routeRules[0].GetProperty("ip_cidr")[0].GetString() == "203.0.113.7/32" && routeRules[1].GetProperty("process_name")[0].GetString() == "xray.exe", "minimal endpoint and Xray process loop guards remain first");
Check(routeRules.Count(x => x.TryGetProperty("ip_cidr", out _)) == 1 && !routeRules.Any(x => x.TryGetProperty("ip_is_private", out _) || x.TryGetProperty("geoip", out _) || x.TryGetProperty("geosite", out _) || x.TryGetProperty("domain_keyword", out _)), "no broad IP LAN GeoIP geosite or keyword DIRECT exemptions");

var prefs = new RuRoutingPreferences { CustomRules = new()
{
    new(DomainMatch.Suffix, "youtube.com", DomainRoute.Direct),
    new(DomainMatch.Suffix, "gosuslugi.ru", DomainRoute.Proxy),
    new(DomainMatch.Exact, "manual.example", DomainRoute.Direct),
    new(DomainMatch.Suffix, "family.example", DomainRoute.Direct),
    new(DomainMatch.Exact, "locked.family.example", DomainRoute.Proxy)
} };
using var conflicts = Build(RoutingMode.RuMode, prefs);
Check(Route(conflicts.RootElement, "youtube.com") == "xray" && Dns(conflicts.RootElement, "youtube.com") == "doh", "protected VPN service wins manual DIRECT conflict");
Check(Route(conflicts.RootElement, "esia.gosuslugi.ru") == "xray" && Dns(conflicts.RootElement, "esia.gosuslugi.ru") == "doh", "manual PROXY wins enabled service DIRECT conflict");
Check(Route(conflicts.RootElement, "manual.example") == "direct" && Route(conflicts.RootElement, "sub.manual.example") == "xray", "exact manual rule does not cover children");
Check(Route(conflicts.RootElement, "sub.family.example") == "direct" && Dns(conflicts.RootElement, "sub.family.example") == "direct-fakeip", "suffix manual rule covers children in both policies");
Check(Route(conflicts.RootElement, "locked.family.example") == "xray" && Dns(conflicts.RootElement, "locked.family.example") == "doh", "exact PROXY precedes broader manual DIRECT in route and DNS");
using var fullConflicts = Build(RoutingMode.FullVpn, prefs);
Check(Route(fullConflicts.RootElement, "manual.example") == "xray" && Dns(fullConflicts.RootElement, "manual.example") == "doh", "FullVPN suppresses saved manual DIRECT rules");
var disabled = new RuRoutingPreferences { DisabledServiceIds = RoutingCatalog.LoadEmbedded().Services.Select(s => s.Id).ToList() };
using var disabledConfig = Build(RoutingMode.RuMode, disabled);
Check(Route(disabledConfig.RootElement, "gosuslugi.ru") == "xray" && Dns(disabledConfig.RootElement, "gosuslugi.ru") == "doh", "disabled services contribute no direct route or DNS");
Check(disabledConfig.RootElement.GetProperty("dns").GetProperty("servers").GetArrayLength() == 2 &&
    !disabledConfig.RootElement.GetProperty("dns").GetProperty("rules").EnumerateArray().Any(x => x.GetProperty("server").GetString() != "doh"), "disabled exceptions keep virtual identity store dormant and select no DIRECT DNS");
using var fullDormant = JsonDocument.Parse(SingBoxConfigBuilder.Build("203.0.113.7", fakeIpCachePath: Path.Combine(Path.GetTempPath(), "madego-ru-config-checks", "ru-fakeip-v1.db"), retainFakeIpMappings: true));
Check(fullDormant.RootElement.GetProperty("dns").GetProperty("servers").GetArrayLength() == 2 && fullDormant.RootElement.GetProperty("dns").GetProperty("rules").GetArrayLength() == 0, "FullVPN may retain old virtual identity without assigning new synthetic DNS");
Check(Route(fullDormant.RootElement, "gosuslugi.ru") == "xray" && Dns(fullDormant.RootElement, "gosuslugi.ru") == "doh", "retained previous DIRECT identity receives current FullVPN proxy policy");
var dormantDirect = fullDormant.RootElement.GetProperty("outbounds").EnumerateArray().Single(x => x.GetProperty("tag").GetString() == "direct");
Check(dormantDirect.TryGetProperty("domain_resolver", out var dormantResolver) && dormantResolver.GetString() == "doh", "multiple-server dormant profile gives service DIRECT outbound explicit VPN resolver");
Check(!f.TryGetProperty("experimental", out _), "new FullVPN and invalid-cache fallback need no FakeIP cache");
using var v6 = Build(RoutingMode.RuMode, endpoint: "2001:db8::7");
Check(v6.RootElement.GetProperty("route").GetProperty("rules")[0].GetProperty("ip_cidr")[0].GetString() == "2001:db8::7/128", "IPv6 endpoint bypass limited to one address");
using var absentEndpoint = Build(RoutingMode.RuMode, endpoint: "server.example");
Check(absentEndpoint.RootElement.GetProperty("route").GetProperty("rules").EnumerateArray().All(x => !x.TryGetProperty("ip_cidr", out _)), "hostname endpoint does not invent an IP exemption");
using var google = Build(RoutingMode.RuMode, provider: "Google");
Check(google.RootElement.GetProperty("dns").GetProperty("servers")[1].GetProperty("server").GetString() == "8.8.8.8", "selected DNS provider also supplies ordinary DIRECT resolver");
var immutable = RoutingPolicySnapshot.Create(RoutingMode.RuMode, prefs);
prefs.CustomRules.Clear();
using var captured = JsonDocument.Parse(SingBoxConfigBuilder.Build("203.0.113.7", settings: new(), policy: immutable));
Check(Route(captured.RootElement, "manual.example") == "direct", "builder consumes captured immutable snapshot instead of later settings edits");

if (args.Length == 2 && args[0] == "--output")
{
    Directory.CreateDirectory(args[1]);
    foreach (var pair in new[] { ("full-v4", full), ("ru-v4", ru), ("ru-v6", v6), ("ru-conflicts", conflicts), ("ru-disabled", disabledConfig), ("ru-google", google), ("full-custom", fullConflicts), ("full-retained", fullDormant) })
        File.WriteAllText(Path.Combine(args[1], pair.Item1 + ".json"), pair.Item2.RootElement.GetRawText());
}
Console.WriteLine($"Routing config checks: {count}/{count}");
