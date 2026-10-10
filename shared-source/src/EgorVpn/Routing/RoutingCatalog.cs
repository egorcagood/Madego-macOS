using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace EgorVpn.Routing;

public sealed record RoutingService
{
    public string Id { get; }
    public string Name { get; }
    public IReadOnlyList<string> Aliases { get; }
    public string Category { get; }
    public bool EnabledInRuPreset { get; }
    public IReadOnlyList<DomainRule> Rules { get; }
    public RoutingService(string id, string name, IReadOnlyList<string> aliases, string category, bool enabledInRuPreset, IReadOnlyList<DomainRule> rules)
    {
        Id = id; Name = name; Category = category; EnabledInRuPreset = enabledInRuPreset;
        Aliases = System.Array.AsReadOnly(aliases.ToArray());
        Rules = System.Array.AsReadOnly(rules.ToArray());
    }
}

public sealed class RoutingCatalog
{
    private static readonly Lazy<RoutingCatalog> Embedded = new(ReadEmbedded);
    public IReadOnlyList<RoutingService> Services { get; }
    public IReadOnlyList<RoutingService> VpnOnlyServices { get; }
    public string Version { get; }
    public string? Warning { get; }
    public bool IsValid => Warning == null;
    private RoutingCatalog(string version, RoutingService[] services, RoutingService[] vpnOnly, string? warning = null)
    {
        Version = version; Services = System.Array.AsReadOnly(services); VpnOnlyServices = System.Array.AsReadOnly(vpnOnly); Warning = warning;
    }
    public static RoutingCatalog LoadEmbedded() => Embedded.Value;

    // All-or-nothing parsing: a damaged direct or protection seed disables DIRECT.
    public static RoutingCatalog Load(string directJson, string vpnOnlyJson)
    {
        try
        {
            var direct = Parse(directJson, DomainRoute.Direct);
            var proxy = Parse(vpnOnlyJson, DomainRoute.Proxy);
            if (direct.Version != proxy.Version) throw new InvalidDataException("Catalog versions differ.");
            return new(direct.Version, direct.Services, proxy.Services);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or ArgumentException or InvalidOperationException or OverflowException)
        {
            return Unavailable();
        }
    }

    public IReadOnlyList<RoutingService> Search(string query)
    {
        var text = SearchText(query);
        if (text.Length == 0) return Services;
        DomainNames.TryNormalize(query, out var domain, out _);
        return System.Array.AsReadOnly(Services.Where(service =>
            SearchText(service.Name).Contains(text, StringComparison.Ordinal) ||
            SearchText(service.Id).Contains(text, StringComparison.Ordinal) ||
            service.Aliases.Any(alias => SearchText(alias).Contains(text, StringComparison.Ordinal)) ||
            service.Rules.Any(rule => SearchText(rule.Value).Contains(text, StringComparison.Ordinal) ||
                                     (domain.Length > 0 && rule.MatchesNormalized(domain))))
            .ToArray());
    }

    private static string SearchText(string? input)
    {
        var text = (input ?? "").ToLowerInvariant().Replace('ё', 'е');
        var builder = new StringBuilder();
        // Spaces and hyphens have no search significance: “гос услуги” finds “Госуслуги”.
        foreach (var c in text) if (!char.IsWhiteSpace(c) && c != '-') builder.Append(c);
        return builder.ToString();
    }

    private static RoutingCatalog ReadEmbedded()
    {
        try
        {
            string Read(string filename)
            {
                using var stream = typeof(RoutingCatalog).Assembly.GetManifestResourceStream("EgorVpn.Routing.data." + filename);
                if (stream == null || stream.Length > 2_000_000) throw new InvalidDataException("Missing or oversized seed.");
                using var reader = new StreamReader(stream, Encoding.UTF8, true);
                return reader.ReadToEnd();
            }
            return Load(Read("services.ru.seed.json"), Read("vpn-only.seed.json"));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException) { return Unavailable(); }
    }

    private static RoutingCatalog Unavailable() => new("unavailable", System.Array.Empty<RoutingService>(), System.Array.Empty<RoutingService>(),
        "Routing catalog is unavailable or invalid. All public traffic stays through VPN.");

    private static (string Version, RoutingService[] Services) Parse(string json, DomainRoute expectedRoute)
    {
        if (json == null || json.Length > 2_000_000) throw new InvalidDataException("Invalid catalog size.");
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || Integer(root, "schemaVersion") != 1 ||
            Text(root, "coverage") != "seed-not-exhaustive") throw new InvalidDataException("Invalid catalog schema.");
        var version = Text(root, "catalogVersion");
        if (version.Length > 128) throw new InvalidDataException("Invalid version.");
        var entries = Array(root, "services");
        if (entries.GetArrayLength() is < 1 or > 1000) throw new InvalidDataException("Invalid service count.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var services = new List<RoutingService>();
        foreach (var entry in entries.EnumerateArray())
        {
            var id = Text(entry, "id");
            if (id.Length > 128 || id[0] == '-' || id.Any(c => !(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')) || !ids.Add(id))
                throw new InvalidDataException("Invalid or duplicate service ID.");
            var name = Text(entry, "name");
            var category = String(entry, "category");
            var enabled = Boolean(entry, "enabledInRuPreset");
            _ = Boolean(entry, "endToEndVerified");
            if (Text(entry, "route") != (expectedRoute == DomainRoute.Direct ? "direct" : "proxy")) throw new InvalidDataException("Invalid service route.");
            var aliases = Array(entry, "aliases");
            if (aliases.GetArrayLength() > 100) throw new InvalidDataException("Too many aliases.");
            var aliasValues = aliases.EnumerateArray().Select(a => a.ValueKind == JsonValueKind.String ? a.GetString()! : throw new InvalidDataException("Invalid alias.")).ToArray();
            if (aliasValues.Any(a => a.Length > 512) || aliasValues.Distinct(StringComparer.Ordinal).Count() != aliasValues.Length) throw new InvalidDataException("Invalid or duplicate alias.");
            var rules = Array(entry, "rules");
            if (rules.GetArrayLength() is < 1 or > 2000) throw new InvalidDataException("Invalid rule count.");
            var ruleValues = new List<DomainRule>();
            foreach (var rule in rules.EnumerateArray())
            {
                var match = Text(rule, "match") switch { "domain" => DomainMatch.Exact, "domain_suffix" => DomainMatch.Suffix, _ => throw new InvalidDataException("Unsupported match.") };
                var value = Text(rule, "value");
                if (!DomainNames.TryNormalize(value, out var normalized, out _) || value != normalized) throw new InvalidDataException("Unsafe domain.");
                _ = Text(rule, "evidence");
                var sources = Array(rule, "sources");
                if (sources.GetArrayLength() is < 1 or > 100 || sources.EnumerateArray().Any(s => s.ValueKind != JsonValueKind.Object)) throw new InvalidDataException("Missing sources.");
                var item = new DomainRule(match, normalized, expectedRoute);
                if (ruleValues.Contains(item)) throw new InvalidDataException("Duplicate service rule.");
                ruleValues.Add(item);
            }
            services.Add(new(id, name, aliasValues, category, enabled, ruleValues));
        }
        return (version, services.ToArray());
    }
    private static JsonElement Property(JsonElement obj, string key) => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var value) ? value : throw new InvalidDataException("Missing " + key);
    private static string String(JsonElement obj, string key) { var v = Property(obj, key); if (v.ValueKind != JsonValueKind.String) throw new InvalidDataException("Invalid " + key); var s = v.GetString()!; if (s.Length > 4096) throw new InvalidDataException("Oversized " + key); return s; }
    private static string Text(JsonElement obj, string key) { var s = String(obj, key); if (string.IsNullOrWhiteSpace(s)) throw new InvalidDataException("Empty " + key); return s; }
    private static JsonElement Array(JsonElement obj, string key) { var v = Property(obj, key); if (v.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Invalid " + key); return v; }
    private static bool Boolean(JsonElement obj, string key) { var v = Property(obj, key); if (v.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new InvalidDataException("Invalid " + key); return v.GetBoolean(); }
    private static int Integer(JsonElement obj, string key) { var v = Property(obj, key); if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var number)) throw new InvalidDataException("Invalid " + key); return number; }
}
