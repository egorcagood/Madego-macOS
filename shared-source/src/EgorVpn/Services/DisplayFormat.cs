using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
namespace EgorVpn.Services;

public static class DisplayFormat
{
    public static string Bytes(long? bytes)
    {
        if (bytes is null) return "—";
        double value = Math.Max(0, bytes.Value);
        string[] units = ["Б", "КБ", "МБ", "ГБ", "ТБ"];
        var i = 0;
        while (value >= 1024 && i < units.Length - 1) { value /= 1024; i++; }
        return $"{value:0.#} {units[i]}";
    }
    public static string Rate(double? bytes) => bytes is null ? "—" : Bytes((long)Math.Max(0, bytes.Value)) + "/с";
    public static string NodeKey(string rawUri) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawUri)));
    public static string Endpoint(string host, int port) => host.Contains(':') ? $"[{host.Trim('[', ']')}]:{port}" : $"{host}:{port}";
    public static string CountryCode(string flag)
    {
        var runes = flag.EnumerateRunes().ToArray();
        if (runes.Length == 2 && runes.All(r => r.Value is >= 0x1F1E6 and <= 0x1F1FF))
            return new string(runes.Select(r => (char)('A' + r.Value - 0x1F1E6)).ToArray());
        return "ZZ";
    }
    public static string Redact(string text)
    {
        text = Regex.Replace(text, @"\x1B\[[0-?]*[ -/]*[@-~]", "");
        text = Regex.Replace(text, @"(?i)(?:https?|vless|vmess|trojan)://[^\s\""<>]+", "[ссылка скрыта]");
        text = Regex.Replace(text, @"(?i)\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b", "[ID скрыт]");
        return text.Length > 1600 ? text[..1600] + "…" : text;
    }
}
