using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EgorVpn.Routing;

[JsonConverter(typeof(RoutingModeJsonConverter))]
public enum RoutingMode { FullVpn = 0, RuMode = 1 }

// Consume only the mode value: an invalid mode must not discard unrelated settings.
public sealed class RoutingModeJsonConverter : JsonConverter<RoutingMode>
{
    public override RoutingMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return reader.GetString() == nameof(RoutingMode.RuMode) ? RoutingMode.RuMode : RoutingMode.FullVpn;
        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
            using (JsonDocument.ParseValue(ref reader)) { }
        return RoutingMode.FullVpn;
    }
    public override void Write(Utf8JsonWriter writer, RoutingMode value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value == RoutingMode.RuMode ? nameof(RoutingMode.RuMode) : nameof(RoutingMode.FullVpn));
}
