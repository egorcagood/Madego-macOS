using Madyar.Localization;
namespace EgorVpn.Presentation;

// Presentation only. The existing connection lifecycle remains the source of Phase.
public sealed record ConnectionVisualState(
    string Status, string Title, string FeedbackTitle, string Detail,
    string Icon, string Color, string Background, bool IsVerified = false)
{
    public static ConnectionVisualState ForPhase(string phase, string accent) => phase switch
    {
        "Connected" => new(UiText.T("Подключено"), UiText.T("VPN подключён"), UiText.T("Соединение проверено"),
            UiText.T("Трафик направляется через выбранный VPN-сервер."), "Check", "#48C9A0", "#142A24", true),
        "Connecting" => new(UiText.T("Подключение"), UiText.T("Подключаем VPN"), UiText.T("Проверяем маршрут"),
            UiText.T("Подключение ещё не подтверждено. Попытку можно отменить."), "Clock", accent, "#172632"),
        "Stopping" => new(UiText.T("Отключение"), UiText.T("Отключаем VPN"), UiText.T("Завершаем соединение"),
            UiText.T("Дождись завершения отключения."), "Clock", "#A2B1C7", "#202630"),
        "Degraded" => new(UiText.T("Нет проверки"), UiText.T("Соединение требует проверки"), UiText.T("Проверка не прошла"),
            UiText.T("VPN ещё запущен. Можно отключить его или повторить подключение."), "Info", "#DEB16D", "#2D261C"),
        "Error" => new(UiText.T("Ошибка"), UiText.T("Ошибка соединения"), UiText.T("Состояние не подтверждено"),
            UiText.T("Повтори попытку или выбери другой сервер. Подробности — в диагностике."), "Close", "#E58A8A", "#2C2026"),
        _ => new(UiText.T("Отключено"), UiText.T("VPN отключён"), UiText.T("Обычное подключение"),
            UiText.T("Выбери сервер и подключись к VPN."), "Power", "#A2B1C7", "#202630")
    };
}
