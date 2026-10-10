using System.Text.RegularExpressions;
using System.Globalization;
using System.Text;

namespace EgorVpn.Services;

internal static partial class CountryResolver
{
    private static readonly (string Country, string Flag, string[] Keys)[] Rules =
    [
        ("Netherlands", "🇳🇱", ["🇳🇱", "netherlands", "holland", "nl ", " nl", "амстердам", "нидерланд"]),
        ("Russia", "🇷🇺", ["🇷🇺", "russia", "russian", "ru ", " ru", "россия", "москва", "спб"]),
        ("Germany", "🇩🇪", ["🇩🇪", "germany", "de ", " de", "германия", "frankfurt"]),
        ("Finland", "🇫🇮", ["🇫🇮", "finland", "fi ", " fi", "финлянд"]),
        ("France", "🇫🇷", ["🇫🇷", "france", "fr ", " fr", "франц", "paris"]),
        ("United States", "🇺🇸", ["🇺🇸", "usa", "united states", " us ", "america", "сша"]),
        ("United Kingdom", "🇬🇧", ["🇬🇧", "united kingdom", " uk ", "britain", "england", "лондон"]),
        ("Poland", "🇵🇱", ["🇵🇱", "poland", " pl ", "польш"]),
        ("Sweden", "🇸🇪", ["🇸🇪", "sweden", " se ", "швец"]),
        ("Switzerland", "🇨🇭", ["🇨🇭", "switzerland", " ch ", "швейцар"]),
        ("Turkey", "🇹🇷", ["🇹🇷", "turkey", " tr ", "турц"]),
        ("Kazakhstan", "🇰🇿", ["🇰🇿", "kazakhstan", " kz ", "казах"]),
        ("Japan", "🇯🇵", ["🇯🇵", "japan", " jp ", "япон"]),
        ("Singapore", "🇸🇬", ["🇸🇬", "singapore", " sg ", "сингап"]),
    ];

    public static (string Country, string Flag) Resolve(string name)
    {
        var runes = (name ?? "").EnumerateRunes().ToArray();
        for (var i = 0; i + 1 < runes.Length; i++)
        {
            if (runes[i].Value is >= 0x1F1E6 and <= 0x1F1FF && runes[i+1].Value is >= 0x1F1E6 and <= 0x1F1FF)
            {
                var code = new string(new[] { (char)('A' + runes[i].Value - 0x1F1E6), (char)('A' + runes[i+1].Value - 0x1F1E6) });
                try
                {
                    var region = new RegionInfo(code);
                    return (region.EnglishName, runes[i].ToString() + runes[i+1].ToString());
                }
                catch (ArgumentException) { }
            }
        }
        var normalized = $" {Whitespace().Replace((name ?? "").ToLowerInvariant(), " ")} ";
        foreach (var rule in Rules)
        {
            if (rule.Keys.Any(k => normalized.Contains(k.ToLowerInvariant(), StringComparison.Ordinal)))
                return (rule.Country, rule.Flag);
        }
        return ("Other", "🌐");
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
