using System.Text.Json;
using System.Text.RegularExpressions;

namespace EgorVpn.Branding;

/// <summary>Build-time identity. Never load a profile from a subscription or network response.</summary>
public sealed record BrandProfile
{
    public int SchemaVersion { get; init; }
    public string ApplicationId { get; init; } = "";
    public string ProductName { get; init; } = "";
    public string ShortName { get; init; } = "";
    public string ExecutableName { get; init; } = "";
    public string InstallFolder { get; init; } = "";
    public string DataFolder { get; init; } = "";
    public string TunName { get; init; } = "";
    public string Publisher { get; init; } = "";
    public string Author { get; init; } = "";
    public bool ShowAuthor { get; init; }
    public string Telegram { get; init; } = "";
    public string TelegramLabel { get; init; } = "";
    public bool AuthorNotes { get; init; }
    public string Accent { get; init; } = "";
    public bool MigrateLegacyData { get; init; }
    public string Icon { get; init; } = "";
    public string Logo { get; init; } = "";
    public bool HasTelegram => !string.IsNullOrWhiteSpace(Telegram);
    public string AuthorLine => "Автор " + Author;

    public static BrandProfile FromJson(string json)
    {
        var profile = JsonSerializer.Deserialize<BrandProfile>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new ArgumentException("Пустой профиль бренда.");
        profile.Validate();
        return profile;
    }

    public void Validate()
    {
        if (SchemaVersion != 1) throw new ArgumentException("Неподдерживаемая версия профиля бренда.");
        foreach (var value in new[] { ApplicationId, ExecutableName, DataFolder, TunName })
            if (!IsSafeIdentifier(value)) throw new ArgumentException("Некорректный идентификатор бренда или папки.");
        foreach (var value in new[] { ProductName, ShortName, InstallFolder, Publisher, Author })
            if (string.IsNullOrWhiteSpace(value) || !Regex.IsMatch(value, @"^[\p{L}\p{N}][\p{L}\p{N} ._-]{1,47}$") || value.EndsWith('.') || value.EndsWith(' ') || Regex.IsMatch(value, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase))
                throw new ArgumentException("Некорректное имя в профиле бренда.");
        if (!Regex.IsMatch(Accent ?? "", @"^#[0-9A-Fa-f]{6}$")) throw new ArgumentException("Акцент должен быть в формате #RRGGBB.");
        if (HasTelegram && !IsAllowedTelegram(Telegram)) throw new ArgumentException("Допускается только HTTPS-ссылка на канал t.me.");
        if (TelegramLabel is null || TelegramLabel.Length > 48 || TelegramLabel.Any(char.IsControl))
            throw new ArgumentException("Некорректная подпись канала.");
        if (!Regex.IsMatch(Icon ?? "", @"^[A-Za-z0-9_-]+\.ico$") || !Regex.IsMatch(Logo ?? "", @"^[A-Za-z0-9_-]+\.png$"))
            throw new ArgumentException("Иконка и логотип должны быть локальными .ico/.png файлами профиля.");
        if (MigrateLegacyData && ApplicationId != "MadyarVPN") throw new ArgumentException("Миграция старого клиента разрешена только Madyar VPN.");
        if (ApplicationId == "MadyarVPN" && (ExecutableName != "MadyarVPN" || DataFolder != "MadyarVPN" || TunName != "MadyarVPN" || InstallFolder != "Madyar VPN"))
            throw new ArgumentException("Original Madyar identity is fixed. Use a separate applicationId for a partner build.");
        if (ApplicationId != "MadyarVPN")
        {
            string[] reserved = ["MadyarVPN", "EgorMadyarVPN", "FuseVPN", "FUSE"];
            if (reserved.Contains(DataFolder, StringComparer.OrdinalIgnoreCase) ||
                reserved.Contains(TunName, StringComparer.OrdinalIgnoreCase) ||
                reserved.Contains(ExecutableName, StringComparer.OrdinalIgnoreCase) ||
                new[] { "Madyar VPN", "Egor Madyar VPN" }.Contains(InstallFolder, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException("Партнёрская сборка должна иметь собственные папки и идентификаторы.");
        }
    }

    public static bool IsSafeIdentifier(string? value) => value is not null &&
        Regex.IsMatch(value, @"^[A-Za-z][A-Za-z0-9_-]{2,47}$") &&
        !Regex.IsMatch(value, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase);

    public static bool IsAllowedTelegram(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
        uri.Host.Equals("t.me", StringComparison.OrdinalIgnoreCase) && uri.IsDefaultPort &&
        uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
        Regex.IsMatch(uri.AbsolutePath, @"^/[A-Za-z][A-Za-z0-9_]{3,31}$");
}
