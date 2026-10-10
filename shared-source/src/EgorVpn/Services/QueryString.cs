namespace EgorVpn.Services;

internal static class QueryString
{
    public static Dictionary<string, string> Parse(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(query)) return result;
        query = query.TrimStart('?');
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            var key = eq >= 0 ? part[..eq] : part;
            var value = eq >= 0 ? part[(eq + 1)..] : "";
            result[Uri.UnescapeDataString(key.Replace('+', ' '))] = Uri.UnescapeDataString(value.Replace('+', ' '));
        }
        return result;
    }

    public static string Get(this IReadOnlyDictionary<string, string> q, string key, string fallback = "")
        => q.TryGetValue(key, out var value) ? value : fallback;
}
