namespace EgorVpn.Models;

public sealed record SubscriptionRequest(string? Name, IReadOnlyList<string> Urls)
{
    public static SubscriptionRequest Parse(string? name, string input, bool single = false)
    {
        var lines = input.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        if (lines.Length == 0) throw new ArgumentException("Вставь ссылку на подписку.");
        if (single && lines.Length != 1) throw new ArgumentException("При редактировании нужна одна ссылка.");
        if (lines.Length > 20) throw new ArgumentException("Добавляй не больше 20 подписок за один раз.");
        if (lines.Any(s => !Uri.TryCreate(s, UriKind.Absolute, out var uri) ||
                           uri.Scheme is not ("https" or "http") || string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo)))
            throw new ArgumentException("Нужны ссылки http:// или https://, каждая с новой строки. Не вставляй кавычки или пояснения.");
        var title = name?.Trim();
        if (title?.Length > 100) throw new ArgumentException("Название не должно быть длиннее 100 символов.");
        return new(string.IsNullOrWhiteSpace(title) ? null : title, lines);
    }
}
