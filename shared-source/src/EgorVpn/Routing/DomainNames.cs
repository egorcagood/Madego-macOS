using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;

namespace EgorVpn.Routing;

public static class DomainNames
{
    private static readonly IdnMapping Idn = new() { UseStd3AsciiRules = true };
    private static readonly Lazy<PublicSuffixRules?> Suffixes = new(LoadSuffixes);

    public static string Normalize(string input)
    {
        if (!TryNormalize(input, out var normalized, out var error)) throw new ArgumentException(error, nameof(input));
        return normalized;
    }

    public static bool TryNormalize(string? input, out string normalized, out string? error)
    {
        normalized = "";
        error = null;
        try
        {
            if (string.IsNullOrWhiteSpace(input) || input.Length > 4096) return Fail("Enter a domain or an HTTP(S) URL.", out error);
            foreach (var c in input) if (char.IsControl(c)) return Fail("Control characters are not allowed.", out error);
            var host = input.Trim();
            if (host.Contains('\\')) return Fail("Backslashes are not allowed.", out error);
            if (host.Contains("://", StringComparison.Ordinal))
            {
                if (!Uri.TryCreate(host, UriKind.Absolute, out var url) ||
                    (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp) || !string.IsNullOrEmpty(url.UserInfo))
                    return Fail("Use an HTTP(S) URL without credentials.", out error);
                host = url.DnsSafeHost;
            }
            else if (host.IndexOfAny(new[] { '/', ':', '?', '#', '@', '*', '[', ']', '%' }) >= 0)
                return Fail("Enter a hostname without credentials, port, path or wildcard.", out error);
            // IDNA maps Unicode dot variants; strip exactly one absolute-DNS trailing dot.
            host = host.Replace('\u3002', '.').Replace('\uff0e', '.').Replace('\uff61', '.');
            if (host.EndsWith(".", StringComparison.Ordinal)) host = host[..^1];
            host = Idn.GetAscii(host).ToLowerInvariant();
            if (host.Length > 253 || IPAddress.TryParse(host, out _)) return Fail("IP addresses and oversized names are not domain rules.", out error);
            var labels = host.Split('.');
            if (labels.Length < 2) return Fail("A registrable domain is required.", out error);
            foreach (var label in labels)
            {
                if (label.Length is < 1 or > 63 || label[0] == '-' || label[^1] == '-') return Fail("Invalid DNS label.", out error);
                foreach (var c in label) if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')) return Fail("Invalid DNS label.", out error);
                if (label.StartsWith("xn--", StringComparison.Ordinal) && !string.Equals(Idn.GetAscii(Idn.GetUnicode(label)), label, StringComparison.Ordinal))
                    return Fail("Invalid IDNA label.", out error);
            }
            var suffixes = Suffixes.Value;
            if (suffixes == null) return Fail("Public suffix validation is unavailable.", out error);
            if (suffixes.IsPublicSuffix(host)) return Fail("Public suffixes and shared hosting namespaces cannot be domain rules.", out error);
            normalized = host;
            return true;
        }
        catch (ArgumentException) { return Fail("Invalid domain name.", out error); }
        catch (UriFormatException) { return Fail("Invalid URL.", out error); }
    }

    private static bool Fail(string message, out string? error) { error = message; return false; }

    private static PublicSuffixRules? LoadSuffixes()
    {
        try
        {
            using var stream = typeof(DomainNames).Assembly.GetManifestResourceStream("EgorVpn.Routing.data.public-suffix-list.txt");
            if (stream == null || stream.Length > 2_000_000) return null;
            using var reader = new StreamReader(stream, Encoding.UTF8, true);
            var rules = new PublicSuffixRules();
            var count = 0;
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                line = line.Trim();
                if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)) continue;
                if (++count > 50_000) return null;
                var exception = line.StartsWith('!');
                var wildcard = line.StartsWith("*.", StringComparison.Ordinal);
                var domain = Idn.GetAscii(line[(exception ? 1 : wildcard ? 2 : 0)..]).ToLowerInvariant();
                (exception ? rules.Exceptions : wildcard ? rules.Wildcards : rules.Exact).Add(domain);
            }
            return count > 1000 && rules.Exact.Contains("ru") && rules.Exact.Contains("com") ? rules : null;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException) { return null; }
    }

    private sealed class PublicSuffixRules
    {
        public HashSet<string> Exact { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Wildcards { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Exceptions { get; } = new(StringComparer.Ordinal);
        public bool IsPublicSuffix(string host)
        {
            if (Exceptions.Contains(host)) return false;
            if (Exact.Contains(host)) return true;
            var dot = host.IndexOf('.');
            return dot >= 0 && Wildcards.Contains(host[(dot + 1)..]);
        }
    }
}
