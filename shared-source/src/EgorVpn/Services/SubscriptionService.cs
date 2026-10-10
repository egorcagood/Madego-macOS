using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using EgorVpn.Models;
using Microsoft.Win32;

namespace EgorVpn.Services;

public sealed class SubscriptionService
{
    private readonly string _deviceId = LoadOrCreateDeviceId();

    public string DeviceId => _deviceId;

    private static readonly ClientProfile[] Profiles =
    [
        new("MadyarVPN/0.4.1", AppPaths.ProductName),
        new("EgorMadyarVPN/0.2.2", "Legacy client compatibility"),
        new("Happ/4.3.0", "Happ compatibility"),
        new("v2rayN/7.0.0", "v2rayN compatibility"),
    ];

    public async Task<SubscriptionGroup> DownloadAsync(string url, string? preferredName = null, CancellationToken ct = default)
    {
        Exception? last = null;
        HwidLimitReachedException? hwidLimit = null;

        // Normal path: preserve the existing stable per-install HWID across the Madyar VPN rename.
        // This keeps already-added subscriptions from appearing as a new device.
        foreach (var profile in Profiles)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var group = await DownloadWithProfileAsync(url, preferredName, profile, _deviceId, ct);
                if (group.Nodes.Count > 0)
                    return group;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OperationCanceledException ex) { last = new TimeoutException("Истекло время загрузки подписки.", ex); }
            catch (HwidLimitReachedException ex)
            {
                hwidLimit = ex;
                // Same HWID will be rejected for the remaining UA profiles too.
                break;
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        // If the subscription is already registered in Happ on this Windows PC,
        // Remnawave may know the machine by Qt's machineUniqueId() (Windows MachineGuid).
        // Try that same physical-machine identity, plus Happ's no-hyphen desktop variant,
        // before telling the user that the provider-side device list must be reset.
        if (hwidLimit is not null)
        {
            foreach (var compatId in GetHappCompatibleDeviceIds())
            {
                if (compatId.Equals(_deviceId, StringComparison.OrdinalIgnoreCase)) continue;
                ct.ThrowIfCancellationRequested();
                try
                {
                    var happProfile = Profiles.First(p => p.Name == "Happ compatibility");
                    var group = await DownloadWithProfileAsync(url, preferredName, happProfile, compatId, ct);
                    if (group.Nodes.Count > 0)
                        return group;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OperationCanceledException ex) { last = new TimeoutException("Истекло время загрузки подписки.", ex); }
                catch (HwidLimitReachedException ex)
                {
                    hwidLimit = ex;
                }
                catch (Exception ex)
                {
                    last = ex;
                }
            }

            throw new HwidLimitReachedException(_deviceId, hwidLimit.ProviderAnnouncement, hwidLimit.SupportUrl, compatibilityTried: true);
        }

        throw new InvalidOperationException(
            "Сервер подписки не отдал распознаваемую конфигурацию. Попробованы основной и совместимые профили. " +
            $"Последняя ошибка: {last?.Message}", last);
    }

    private async Task<SubscriptionGroup> DownloadWithProfileAsync(
        string url,
        string? preferredName,
        ClientProfile profile,
        string deviceId,
        CancellationToken ct)
    {
        using var client = CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(profile.UserAgent);
        request.Headers.TryAddWithoutValidation("x-hwid", deviceId);
        request.Headers.TryAddWithoutValidation("x-device-os", "Windows");
        request.Headers.TryAddWithoutValidation("x-ver-os", Environment.OSVersion.Version.ToString());
        request.Headers.TryAddWithoutValidation("x-device-model", "Windows PC");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        request.Headers.ConnectionClose = true;

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);

        ThrowForHwidState(response, deviceId);
        response.EnsureSuccessStatusCode();

        var rawText = await response.Content.ReadAsStringAsync(ct);
        var groupName = string.IsNullOrWhiteSpace(preferredName)
            ? GuessName(response, url)
            : DecodeHeaderText(preferredName!);

        var group = new SubscriptionGroup
        {
            Name = groupName,
            Url = url,
            UpdatedAt = DateTimeOffset.Now,
        };

        if (response.Headers.TryGetValues("subscription-userinfo", out var values))
            ApplyUsage(values.FirstOrDefault(), group);

        var parsed = ParseNodes(rawText);
        foreach (var node in parsed)
            group.Nodes.Add(node);

        if (LooksLikeUnsupportedStub(rawText, group.Nodes))
            throw new InvalidOperationException($"Провайдер вернул заглушку «Приложение не поддерживается» для профиля {profile.Name}.");

        if (group.Nodes.Count == 0)
            throw new InvalidOperationException($"Профиль {profile.Name}: не найдено VLESS-узлов в ответе подписки.");

        return group;
    }

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromSeconds(20),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(10),
        };

        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(25)
        };
    }

    private static void ThrowForHwidState(HttpResponseMessage response, string deviceId)
    {
        if (HeaderTrue(response, "x-hwid-max-devices-reached") || HeaderTrue(response, "x-hwid-limit"))
        {
            var announce = ReadDecodedHeader(response, "announce");
            var supportUrl = ReadRawHeader(response, "support-url");
            throw new HwidLimitReachedException(deviceId, announce, supportUrl);
        }

        // We send x-hwid on every request. If the panel still says it is unsupported,
        // the current response rule probably rejected this client profile; the next
        // profile attempt may still work.
        if (HeaderTrue(response, "x-hwid-not-supported"))
            throw new InvalidOperationException("Панель требует HWID, но текущий профиль клиента не был принят.");
    }

    private static string? ReadDecodedHeader(HttpResponseMessage response, string name)
    {
        var raw = ReadRawHeader(response, name);
        return string.IsNullOrWhiteSpace(raw) ? null : DecodeHeaderText(raw);
    }

    private static string? ReadRawHeader(HttpResponseMessage response, string name)
    {
        return response.Headers.TryGetValues(name, out var values)
            ? values.FirstOrDefault()
            : null;
    }

    private static bool HeaderTrue(HttpResponseMessage response, string name)
    {
        return response.Headers.TryGetValues(name, out var values)
            && values.Any(v => v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1");
    }

    private static List<VpnNode> ParseNodes(string body)
    {
        var nodes = new List<VpnNode>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var text = DecodeMaybeBase64(body);

        // Classic XRAY_BASE64 / plain share-link subscription.
        foreach (var line in text.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith("vless://", StringComparison.OrdinalIgnoreCase)) continue;
            if (!seen.Add(line)) continue;
            if (VlessUriParser.TryParse(line, out var node, out _) && node is not null)
                nodes.Add(node);
        }

        if (nodes.Count > 0)
            return nodes;

        // Happ/Remnawave may return either one full XRAY_JSON config or an array
        // of full configs. Each config normally contains one VLESS outbound and a
        // human-readable `remarks` field.
        TryParseXrayJson(text, nodes, seen);
        return nodes;
    }

    private static void TryParseXrayJson(string text, List<VpnNode> nodes, HashSet<string> seen)
    {
        var trimmed = text.Trim().TrimStart('\uFEFF');
        if (!(trimmed.StartsWith('{') || trimmed.StartsWith('['))) return;

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in doc.RootElement.EnumerateArray())
                    ParseXrayConfig(item, nodes, seen);
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                ParseXrayConfig(doc.RootElement, nodes, seen);
            }
        }
        catch (JsonException)
        {
            // Not JSON; another compatibility profile can still be attempted.
        }
    }

    private static void ParseXrayConfig(JsonElement config, List<VpnNode> nodes, HashSet<string> seen)
    {
        if (config.ValueKind != JsonValueKind.Object) return;

        var remarks = GetString(config, "remarks");
        if (!config.TryGetProperty("outbounds", out var outbounds) || outbounds.ValueKind != JsonValueKind.Array)
            return;

        var vlessIndex = 0;
        foreach (var outbound in outbounds.EnumerateArray())
        {
            if (!GetString(outbound, "protocol").Equals("vless", StringComparison.OrdinalIgnoreCase))
                continue;

            vlessIndex++;
            var tag = GetString(outbound, "tag");
            var name = !string.IsNullOrWhiteSpace(remarks)
                ? remarks
                : (!string.IsNullOrWhiteSpace(tag) ? tag : $"VLESS {vlessIndex}");

            var node = ParseVlessOutbound(outbound, name);
            if (node is null) continue;

            var key = $"{node.UserId}@{node.Host}:{node.Port}/{node.Transport}/{node.Security}/{node.Sni}/{node.Path}";
            if (seen.Add(key))
                nodes.Add(node);
        }
    }

    private static VpnNode? ParseVlessOutbound(JsonElement outbound, string name)
    {
        if (!outbound.TryGetProperty("settings", out var settings) || settings.ValueKind != JsonValueKind.Object)
            return null;

        string host = "";
        int port = 443;
        string userId = "";
        string encryption = "none";
        string flow = "";

        // Standard Xray client config: settings.vnext[0].users[0]
        if (settings.TryGetProperty("vnext", out var vnext) && vnext.ValueKind == JsonValueKind.Array && vnext.GetArrayLength() > 0)
        {
            var server = vnext[0];
            host = GetString(server, "address");
            port = GetInt(server, "port", 443);

            if (server.TryGetProperty("users", out var users) && users.ValueKind == JsonValueKind.Array && users.GetArrayLength() > 0)
            {
                var user = users[0];
                userId = GetString(user, "id");
                encryption = GetString(user, "encryption", "none");
                flow = GetString(user, "flow");
            }
        }
        else
        {
            // Newer simplified shape accepted by current Xray builds.
            host = GetString(settings, "address");
            port = GetInt(settings, "port", 443);
            userId = GetString(settings, "id");
            encryption = GetString(settings, "encryption", "none");
            flow = GetString(settings, "flow");
        }

        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(userId))
            return null;

        var transport = "raw";
        var security = "none";
        var sni = host;
        var fingerprint = "chrome";
        var publicKey = "";
        var shortId = "";
        var spiderX = "";
        var mldsa65Verify = "";
        var alpn = "";
        var allowInsecure = false;
        var path = "/";
        var hostHeader = "";
        var mode = "auto";
        var serviceName = "";
        var extraJson = "";

        if (outbound.TryGetProperty("streamSettings", out var stream) && stream.ValueKind == JsonValueKind.Object)
        {
            var network = GetString(stream, "network", GetString(stream, "method", "tcp"));
            transport = NormalizeTransport(network);
            security = GetString(stream, "security", "none").ToLowerInvariant();

            if (security == "reality" && stream.TryGetProperty("realitySettings", out var reality) && reality.ValueKind == JsonValueKind.Object)
            {
                sni = GetString(reality, "serverName", host);
                fingerprint = GetString(reality, "fingerprint", "chrome");
                publicKey = GetString(reality, "publicKey", GetString(reality, "password"));
                shortId = GetString(reality, "shortId");
                spiderX = GetString(reality, "spiderX");
                mldsa65Verify = GetString(reality, "mldsa65Verify");
            }
            else if (security == "tls" && stream.TryGetProperty("tlsSettings", out var tls) && tls.ValueKind == JsonValueKind.Object)
            {
                sni = GetString(tls, "serverName", host);
                fingerprint = GetString(tls, "fingerprint", "chrome");
                allowInsecure = GetBool(tls, "allowInsecure");
                alpn = ReadStringArrayOrString(tls, "alpn");
            }

            switch (transport)
            {
                case "websocket":
                    if (stream.TryGetProperty("wsSettings", out var ws) && ws.ValueKind == JsonValueKind.Object)
                    {
                        path = GetString(ws, "path", "/");
                        hostHeader = GetString(ws, "host");
                        if (string.IsNullOrWhiteSpace(hostHeader) && ws.TryGetProperty("headers", out var headers) && headers.ValueKind == JsonValueKind.Object)
                            hostHeader = GetString(headers, "Host");
                    }
                    break;

                case "xhttp":
                    if (stream.TryGetProperty("xhttpSettings", out var xhttp) && xhttp.ValueKind == JsonValueKind.Object)
                    {
                        path = GetString(xhttp, "path", "/");
                        mode = GetString(xhttp, "mode", "auto");
                        hostHeader = GetString(xhttp, "host");
                        extraJson = GetUnknownObjectProperties(xhttp, ["path", "mode", "host"]);
                    }
                    break;

                case "httpupgrade":
                    if (stream.TryGetProperty("httpupgradeSettings", out var hu) && hu.ValueKind == JsonValueKind.Object)
                    {
                        path = GetString(hu, "path", "/");
                        hostHeader = GetString(hu, "host");
                    }
                    break;

                case "grpc":
                    if (stream.TryGetProperty("grpcSettings", out var grpc) && grpc.ValueKind == JsonValueKind.Object)
                        serviceName = GetString(grpc, "serviceName");
                    break;
            }
        }

        var (country, flag) = CountryResolver.Resolve(name);
        return new VpnNode
        {
            Name = name,
            Country = country,
            CountryFlag = flag,
            RawUri = outbound.GetRawText(),
            Protocol = "vless",
            UserId = userId,
            Host = host,
            Port = port,
            Encryption = string.IsNullOrWhiteSpace(encryption) ? "none" : encryption,
            Flow = flow,
            Security = security,
            Transport = transport,
            Sni = sni,
            Fingerprint = fingerprint,
            PublicKey = publicKey,
            ShortId = shortId,
            SpiderX = spiderX,
            Mldsa65Verify = mldsa65Verify,
            Alpn = alpn,
            AllowInsecure = allowInsecure,
            Path = string.IsNullOrWhiteSpace(path) ? "/" : path,
            HostHeader = hostHeader,
            Mode = string.IsNullOrWhiteSpace(mode) ? "auto" : mode,
            ServiceName = serviceName,
            ExtraJson = extraJson,
        };
    }

    private static string NormalizeTransport(string network)
    {
        return network.ToLowerInvariant() switch
        {
            "tcp" => "raw",
            "raw" => "raw",
            "ws" => "websocket",
            "websocket" => "websocket",
            "httpupgrade" => "httpupgrade",
            "splithttp" => "xhttp",
            "xhttp" => "xhttp",
            "grpc" => "grpc",
            "kcp" => "mkcp",
            "mkcp" => "mkcp",
            var other => other,
        };
    }

    private static bool LooksLikeUnsupportedStub(string body, IEnumerable<VpnNode> nodes)
    {
        if (body.Contains("Приложение не поддерживается", StringComparison.OrdinalIgnoreCase)
            || body.Contains("application is not supported", StringComparison.OrdinalIgnoreCase)
            || body.Contains("unsupported application", StringComparison.OrdinalIgnoreCase))
            return true;

        return nodes.Any(n =>
            n.Name.Contains("Приложение не поддерживается", StringComparison.OrdinalIgnoreCase)
            || ((n.Host == "0.0.0.0" || n.Host == "127.0.0.1") && n.Port <= 1));
    }

    private static string GuessName(HttpResponseMessage response, string url)
    {
        if (response.Headers.TryGetValues("profile-title", out var titles))
        {
            var v = titles.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(v)) return DecodeHeaderText(v);
        }
        return new Uri(url).Host;
    }

    private static string DecodeHeaderText(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return s;
        var value = s.Trim();

        try
        {
            if (value.StartsWith("base64:", StringComparison.OrdinalIgnoreCase))
            {
                var b64 = value[7..].Trim().Replace('-', '+').Replace('_', '/');
                b64 += new string('=', (4 - b64.Length % 4) % 4);
                return Encoding.UTF8.GetString(Convert.FromBase64String(b64));
            }
        }
        catch { }

        try { return Uri.UnescapeDataString(value.Replace("%20", " ")); }
        catch { return value; }
    }

    private static void ApplyUsage(string? header, SubscriptionGroup group)
    {
        if (string.IsNullOrWhiteSpace(header)) return;
        long upload = 0, download = 0;
        foreach (var part in header.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var p = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (p.Length != 2 || !long.TryParse(p[1], out var value)) continue;
            switch (p[0].ToLowerInvariant())
            {
                case "upload": upload = value; break;
                case "download": download = value; break;
                case "total": group.TrafficTotalBytes = value; break;
                case "expire":
                    if (value > 0) group.ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(value).ToLocalTime();
                    break;
            }
        }
        group.TrafficUsedBytes = upload + download;
    }

    private static string DecodeMaybeBase64(string input)
    {
        var trimmed = input.Trim().TrimStart('\uFEFF');
        if (trimmed.Contains("://", StringComparison.Ordinal) || trimmed.StartsWith('{') || trimmed.StartsWith('['))
            return trimmed;

        try
        {
            var compact = new string(trimmed.Where(c => !char.IsWhiteSpace(c)).ToArray())
                .Replace('-', '+').Replace('_', '/');
            compact += new string('=', (4 - compact.Length % 4) % 4);
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(compact));
            return decoded.Contains("://", StringComparison.Ordinal) || decoded.TrimStart().StartsWith('{') || decoded.TrimStart().StartsWith('[')
                ? decoded
                : trimmed;
        }
        catch { return trimmed; }
    }

    private static string LoadOrCreateDeviceId()
    {
        var dir = AppPaths.DataDirectory;
        var path = AppPaths.DeviceIdPath;
        try
        {
            Directory.CreateDirectory(dir);
            if (File.Exists(path))
            {
                var existing = File.ReadAllText(path).Trim();
                if (IsValidHwid(existing))
                    return existing;
            }

            var id = Guid.NewGuid().ToString("N");
            File.WriteAllText(path, id);
            return id;
        }
        catch
        {
            return Guid.NewGuid().ToString("N");
        }
    }

    private static IEnumerable<string> GetHappCompatibleDeviceIds()
    {
        var machineGuid = ReadWindowsMachineGuid();
        if (string.IsNullOrWhiteSpace(machineGuid)) yield break;

        var normalized = machineGuid.Trim();
        if (IsValidHwid(normalized))
            yield return normalized;

        var compact = normalized.Replace("-", "", StringComparison.Ordinal);
        if (!compact.Equals(normalized, StringComparison.Ordinal) && IsValidHwid(compact))
            yield return compact;
    }

    private static string? ReadWindowsMachineGuid()
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(@"SOFTWARE\Microsoft\Cryptography", writable: false);
            return key?.GetValue("MachineGuid") as string;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsValidHwid(string value)
    {
        return value.Length is >= 10 and <= 64
            && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '=' or '-');
    }

    private static string GetString(JsonElement obj, string name, string fallback = "")
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var value))
        {
            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? fallback,
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => fallback,
            };
        }
        return fallback;
    }

    private static int GetInt(JsonElement obj, string name, int fallback)
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var value))
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var i)) return i;
            if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out i)) return i;
        }
        return fallback;
    }

    private static bool GetBool(JsonElement obj, string name)
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var value))
        {
            if (value.ValueKind == JsonValueKind.True) return true;
            if (value.ValueKind == JsonValueKind.False) return false;
            if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var b)) return b;
        }
        return false;
    }

    private static string ReadStringArrayOrString(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var value)) return "";
        if (value.ValueKind == JsonValueKind.String) return value.GetString() ?? "";
        if (value.ValueKind == JsonValueKind.Array)
            return string.Join(',', value.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)));
        return "";
    }

    private static string GetUnknownObjectProperties(JsonElement obj, string[] known)
    {
        if (obj.ValueKind != JsonValueKind.Object) return "";
        var knownSet = new HashSet<string>(known, StringComparer.OrdinalIgnoreCase);
        var extra = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in obj.EnumerateObject())
        {
            if (!knownSet.Contains(property.Name))
                extra[property.Name] = property.Value.Clone();
        }
        return extra.Count == 0 ? "" : JsonSerializer.Serialize(extra);
    }

    private sealed record ClientProfile(string UserAgent, string Name);
}

public sealed class HwidLimitReachedException : InvalidOperationException
{
    public string DeviceId { get; }
    public string? ProviderAnnouncement { get; }
    public string? SupportUrl { get; }

    public HwidLimitReachedException(string deviceId, string? providerAnnouncement, string? supportUrl, bool compatibilityTried = false)
        : base(BuildMessage(deviceId, providerAnnouncement, supportUrl, compatibilityTried))
    {
        DeviceId = deviceId;
        ProviderAnnouncement = providerAnnouncement;
        SupportUrl = supportUrl;
    }

    private static string BuildMessage(string deviceId, string? providerAnnouncement, string? supportUrl, bool compatibilityTried)
    {
        var lines = new List<string>
        {
            "У этой подписки закончился лимит привязанных устройств.",
            "",
            "Это ограничение на стороне провайдера, а не ошибка VPN. Другое приложение может считаться дополнительным устройством.",
            compatibilityTried
                ? "Я также попробовал совместимый HWID этого Windows-ПК (в формате Happ), но сервер всё равно сообщил о лимите."
                : "",
            "Удалите старое устройство в кабинете/боте провайдера либо попросите поддержку сбросить HWID, затем добавьте подписку снова.",
            "",
            $"HWID {AppPaths.ProductName}: {deviceId}"
        };

        if (!string.IsNullOrWhiteSpace(providerAnnouncement))
        {
            lines.Add("");
            lines.Add("Сообщение провайдера: " + providerAnnouncement.Trim());
        }

        if (!string.IsNullOrWhiteSpace(supportUrl))
        {
            lines.Add("");
            lines.Add("Поддержка: " + supportUrl.Trim());
        }

        return string.Join(Environment.NewLine, lines);
    }
}

