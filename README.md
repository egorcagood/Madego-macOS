# Madego для macOS

Отдельная нативная версия Madego. Это VPN-клиент для ваших подписок и конфигураций; собственные подписки, аккаунты и серверный сервис сюда не добавляются. Windows-версия развивается отдельно.

Сейчас **0.1.0-dev.1, локальная сборка**. Есть SwiftUI-интерфейс на русском и английском, импорт VLESS/base64/Xray JSON и HTTP-подписок, выбор и сохранение серверов, настройки Full VPN/RU Mode, Keychain и существующий PeerChat/Link. Профиль чата открывается отдельно с кнопкой «Назад». Передача сервера использует параметры одного выбранного узла; исходная подписка и её название не отправляются.

**VPN-туннель в этой сборке ещё не работает.** Нет Developer ID, notarization и приёмки Windows↔Mac через интернет. Connected не показывается по запуску helper. Full VPN/RU Mode пока сохраняют настройки политики, а не меняют системный трафик.

## Сборка

Apple Silicon, macOS 14+, Swift 6/CLT, Python 3.12+. Инструкция, проверенные SDK/hash и границы подписи — [docs/BUILD.md](docs/BUILD.md).

```sh
python3 scripts/bootstrap-toolchains.py
python3 scripts/verify.py
python3 scripts/build-local.py --dmg
```

Self-contained .NET broker, SQLite и Go Link включаются в `.app`; установка .NET пользователю не нужна. Local DMG подписан ad-hoc и не предназначен для выпуска готового VPN-релиза.

## Проверки и ограничения

Проверены portable routing/import/crypto/backup tests, native arm64 SQLite, Keychain, восстановление identity и отмена IPC. Два настоящих Mac helper-процесса локально обменялись знакомством, сообщениями и одним сервером с ACK после записи в SQLite. Это не подтверждает работу через NAT/CGNAT и не заменяет Windows↔Mac.

Все необходимые этапы и оставшаяся приёмка — [docs/ACCEPTANCE.md](docs/ACCEPTANCE.md). Исследование двух ядер — [docs/ENGINE-FEASIBILITY.md](docs/ENGINE-FEASIBILITY.md). Для подписанного direct release нужен NetworkExtension system provider и Apple Team. iOS сейчас не реализуется.

Чат включается явно; отключение прекращает сетевые соединения и сохранение новых сообщений, история остаётся. Identity защищена Keychain; база истории чата пока не зашифрована. Произвольные XHTTP extra-options нельзя передавать контакту до проверки безопасной schema. Лимит текущего Link helper — 64 контакта.

Исходники общей части сохранены с provenance и SHA-256. User profiles, subscription URLs, keys, базы и журналы исключены из Git и сборок. Лицензия нового порта/общей части — GPLv3, [отдельные notices](THIRD_PARTY_NOTICES.md).
