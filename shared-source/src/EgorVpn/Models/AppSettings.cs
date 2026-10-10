using EgorVpn.Routing;
namespace EgorVpn.Models;

public sealed class AppSettings
{
    public RoutingMode RoutingMode { get; set; } = RoutingMode.FullVpn;
    public RuRoutingPreferences RuMode { get; set; } = new();
    public bool CloseToTray { get; set; } = false;
    public string WindowCloseAction { get; set; } = "Ask";
    public bool StartWithWindows { get; set; } = true;
    public bool AutoConnectOnLaunch { get; set; } = false;
    public bool Animations { get; set; } = true;
    public bool VerboseLogs { get; set; } = false;
    public int RefreshMinutes { get; set; } = 60;
    public int CandidateCount { get; set; } = 4;
    public int TcpTimeoutMs { get; set; } = 2500;
    public int ConnectTimeoutSeconds { get; set; } = 35;
    public int Mtu { get; set; } = 1500;
    public string DnsProvider { get; set; } = "Cloudflare";
    public int ChatMessageFontSize { get; set; } = 14;
    public string ChatPalette { get; set; } = "Blue";
    public int ChatAppearanceRevision { get; set; }
    public bool ChatNotificationPreview { get; set; } = true;

    public AppSettings Copy()
    {
        var copy = (AppSettings)MemberwiseClone();
        copy.RuMode = RuMode?.Copy() ?? new();
        return copy;
    }
    public bool RepairRoutingPreferences()
    {
        if (Enum.IsDefined(RoutingMode) && RuMode is not null && RuMode.Validate() is null) return false;
        RoutingMode = RoutingMode.FullVpn;
        RuMode = new();
        return true;
    }
    public string? Validate()
    {
        if (RefreshMinutes != 0 && (RefreshMinutes is < 5 or > 1440)) return "Интервал обновления: от 5 до 1440 минут, либо 0 — вручную.";
        if (CandidateCount is < 1 or > 10) return "Количество резервных серверов: от 1 до 10.";
        if (TcpTimeoutMs is < 500 or > 10000) return "Тайм-аут TCP: от 500 до 10000 мс.";
        if (ConnectTimeoutSeconds is < 10 or > 120) return "Тайм-аут подключения: от 10 до 120 секунд.";
        if (Mtu is < 1280 or > 1500) return "MTU: от 1280 до 1500.";
        if (DnsProvider is not ("Cloudflare" or "Google" or "Quad9")) return "Выбери DNS из списка.";
        if (ChatMessageFontSize is < 12 or > 22) return "Размер сообщений: от 12 до 22.";
        if (ChatPalette is not ("Blue" or "Green" or "Plum" or "Graphite")) return "Выберите цвет чата из списка.";
        return null;
    }
}
