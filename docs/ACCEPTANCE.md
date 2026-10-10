# Приёмка macOS — фактическое состояние

2026-10-10. Local build/tests не равны clean-Mac acceptance. Full goal не выполнен, пока не закрыта вся исходная матрица.

| № | Критерий | Состояние |
|---|---|---|
|1|Установка на чистом Mac|DMG собран; clean Mac не проверен|
|2|Запуск|Native .app запускается на текущем arm64 Mac|
|3|Импорт config/subscription|Source/UI реализованы; fixture tests; реальная provider acceptance ещё не подтверждена|
|4|Список серверов|Реализован; state fixture tests; native empty-state осмотрен|
|5|Выбор сервера|Реализован и сохраняется; fixture restart test|
|6|Реальный VPN connect|Не реализован packet-flow engine adapter; Team unavailable|
|7|Трафик через выбранный VPN|Не проверен/не доступен|
|8|Disconnect туннеля|Native engine отсутствует; настоящая teardown acceptance pending|
|9|Настройки после рестарта|Mac state tests проходят|
|10|Identity create/restore|Keychain envelope/native SQLite/PeerID restart tests проходят|
|11|Существующий Windows peer|Не проверен: нужен Windows PC|
|12|Переписка|Native integration реализована; два real Mac helper processes локально прошли|
|13|Windows отправляет сервер Mac|Exact wire-preserving local Mac fixture прошёл; Windows live pending|
|14|Mac подключается к нему|Блокируется VPN adapter/signing|
|15|Mac отправляет Windows сервер|Safe exporter+local ACK tests; native Windows live pending|
|16|Нет утечки ключей/подписки|Protection/IPC/export tests и release allowlist; independent review required before public upload; opaque extra sharing отклоняется|
|17|Windows не сломан|3957 baseline hashes unchanged; portable Windows source tests run on Mac; native Windows rerun pending|
|18|Готовая release build|Только ad-hoc local .app/DMG; signed release отсутствует|
|19|README и сборка|Подготовлены и local build выполнен|
|20|Список ограничений|В этом документе, README, BUILD, engine evidence|

Оставшиеся native network tests: FullVpn/RuMode/DNS/IPv6, YouTube/Discord UDP, protected PROXY priority, FakeIP durability and bad-cache recovery, rollback after cancellation/failed save/failed health, sleep/wake, Wi-Fi change, hotspot, two Internet networks, NAT/CGNAT, VPN ON/OFF, fresh Mac and actual Windows peer.

Профиль/backup: native UI nickname/invite/export/preview/restore/blocks ещё требуют ручной приёмки; portable implementations и Mac protector проверены отдельно. Не выводить helper Ready как контакт Online или VPN Connected.
