using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EgorVpn.Routing;

[JsonConverter(typeof(RuRoutingPreferencesJsonConverter))]
public sealed class RuRoutingPreferences
{
    public List<string> DisabledServiceIds { get; set; } = new();
    public List<DomainRule> CustomRules { get; set; } = new();

    public RuRoutingPreferences Copy() => new()
    {
        DisabledServiceIds = DisabledServiceIds == null ? new() : new(DisabledServiceIds),
        CustomRules = CustomRules == null ? new() : new(CustomRules)
    };

    public string? Validate()
    {
        if (DisabledServiceIds == null || CustomRules == null) return "Routing preferences must contain lists.";
        if (DisabledServiceIds.Count > 2000 || CustomRules.Count > 2000) return "Too many routing preferences.";
        foreach (var id in DisabledServiceIds)
            if (string.IsNullOrEmpty(id) || id.Length > 128 || id.Any(c => !(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')) || id[0] == '-')
                return "Invalid disabled service ID.";
        // Unknown disabled IDs are retained so a catalog refresh does not erase user intent.
        var seen = new HashSet<(DomainMatch Match, string Value)>();
        foreach (var rule in CustomRules)
        {
            if (rule == null || !Enum.IsDefined(rule.Match) || !Enum.IsDefined(rule.Route)) return "Invalid domain rule type.";
            if (!DomainNames.TryNormalize(rule.Value, out var name, out var error)) return error;
            if (name != rule.Value) return "Domain rule must contain a normalized hostname.";
            if (!seen.Add((rule.Match, name))) return "Duplicate domain rule. Choose one route for this address and match type.";
        }
        return null;
    }
}

// Shape corruption is isolated to routing preferences so unrelated settings survive.
// A deliberately invalid value triggers the caller's explicit FullVpn repair.
public sealed class RuRoutingPreferencesJsonConverter : JsonConverter<RuRoutingPreferences>
{
    public override bool HandleNull => true;
    public override RuRoutingPreferences Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("DisabledServiceIds", out var disabled) || disabled.ValueKind != JsonValueKind.Array ||
            !root.TryGetProperty("CustomRules", out var custom) || custom.ValueKind != JsonValueKind.Array ||
            disabled.GetArrayLength() > 2000 || custom.GetArrayLength() > 2000)
            return Invalid();
        var preferences = new RuRoutingPreferences();
        foreach (var id in disabled.EnumerateArray())
        {
            if (id.ValueKind != JsonValueKind.String) return Invalid();
            preferences.DisabledServiceIds.Add(id.GetString()!);
        }
        foreach (var item in custom.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) return Invalid();
            try
            {
                var rule = item.Deserialize<DomainRule>(options);
                if (rule == null) return Invalid();
                preferences.CustomRules.Add(rule);
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException or NotSupportedException) { return Invalid(); }
        }
        return preferences;
    }

    public override void Write(Utf8JsonWriter writer, RuRoutingPreferences value, JsonSerializerOptions options)
    {
        if (value == null) { writer.WriteNullValue(); return; }
        writer.WriteStartObject();
        writer.WritePropertyName("DisabledServiceIds");
        JsonSerializer.Serialize(writer, value.DisabledServiceIds, options);
        writer.WritePropertyName("CustomRules");
        JsonSerializer.Serialize(writer, value.CustomRules, options);
        writer.WriteEndObject();
    }
    private static RuRoutingPreferences Invalid() => new() { DisabledServiceIds = null!, CustomRules = null! };
}
