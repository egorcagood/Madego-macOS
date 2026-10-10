using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using EgorVpn.Models;

namespace EgorVpn.Services;

// UI-only normalization. Raw provider names and connection identities remain untouched.
public static partial class NodeLabelFormatter
{
    public static string ForNode(VpnNode node)
    {
        var flag = node.CountryFlag.TrimEnd('\uFE0F');
        var runes = flag.EnumerateRunes().ToArray();
        bool countryFlag = runes.Length == 2 && runes.All(r => r.Value is >= 0x1F1E6 and <= 0x1F1FF);
        var text = new StringBuilder();
        var elements = StringInfo.GetTextElementEnumerator(node.DisplayName);
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            if (countryFlag && element.TrimEnd('\uFE0F') == flag) continue;
            text.Append(element);
        }
        var label = Whitespace().Replace(text.ToString(), " ").Trim();
        if (label.Equals("AUTO", StringComparison.OrdinalIgnoreCase)) return "Сервер";
        if (label.Length > 0) return label;
        return !string.IsNullOrWhiteSpace(node.Country) && node.Country != "Other" ? node.Country : "Сервер";
    }
    public static string Distinguish(string label, int index) => label == "Сервер" ? $"Сервер {index}" : $"{index} · {label}";
    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
