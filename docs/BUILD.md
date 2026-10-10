# Сборка Madego для Mac

Локальная линия 0.1.0-dev.1 — SwiftUI, Common Core и PeerChat/Link. VPN-туннель ещё не реализован. Для этой сборки не нужен Apple Developer, но ad-hoc подпись не подходит для публичного VPN-релиза.

## Окружение

Apple Silicon; Python 3.12+; Command Line Tools с Swift 6; .NET SDK 9.0.318; Go 1.27.1 для пересборки Link. SDK загружаются только с официальных сайтов, хеши закреплены в `scripts/bootstrap-toolchains.py`. Системные SDK не меняются.

```sh
python3 scripts/bootstrap-toolchains.py
python3 scripts/verify.py
python3 scripts/check-provider.py
python3 scripts/build-local.py --dmg
```

Если SDK уже установлен, передайте `MADEGO_DOTNET` и `MADEGO_GO`. Build products появляются в `artifacts/local-<UTC>/`; предыдущие сборки не удаляются. Manifest содержит SHA после подписи, architecture, minimum OS и фактические ограничения.

Minimum — macOS 14: .NET 9 поддерживает 14+, Go 1.27 требует 13+, System Extension для packet tunnel доступен с 10.15. Это согласованная граница зависимостей; чистая macOS 14 ещё не тестировалась. Intel/Universal 2 не собраны. На текущем Mac проверена macOS 27.0.1 arm64, Swift 6.3.3; это факт запуска, а не подтверждение поддержки каждой macOS.

Источники: [Microsoft .NET macOS](https://learn.microsoft.com/en-us/dotnet/core/install/macos), [Go minimum requirements](https://go.dev/wiki/MinimumRequirements), [Apple TN3134](https://developer.apple.com/documentation/technotes/tn3134-network-extension-provider-deployment).

## Данные

Настройки и зашифрованные подписки: `~/Library/Application Support/Madego`; cache: `~/Library/Caches/Madego`; logs: `~/Library/Logs/Madego`. Подписки защищены AES-GCM со случайными nonce и ключом из Keychain. Seed PeerID обёрнут тем же installation key с другим AAD. Потеря ключа закрывает доступ к существующим данным; новая identity автоматически не создаётся. Contacts/history/drafts SQLite остаются plaintext с закрытыми file modes. Перемещение приложения не переносит профиль.

Профиль можно восстановить из Windows password backup, подтверждая замену в UI. Preview работает даже при повреждённой identity или утрате Keychain. Только подтверждённое восстановление разрешает создать новый wrapping key; исходные файлы/restore checkpoint сохраняются. Подписки, зашифрованные утраченной старой key, этим backup не восстанавливаются и остаются нетронутыми; UI показывает отдельное состояние недоступного хранилища. История не входит в backup. Из исходников и DMG исключаются все пользовательские данные. Удаление `.app` сохраняет профиль; очистка Application Support удаляет локальные данные, поэтому сначала сохраните backup. Keychain item `dev.madego.macOS` хранится отдельно.

## Будущая подпись

Необходим Apple Team, зарегистрированные bundle IDs, Developer ID Application и provisioning profiles с `packet-tunnel-provider-systemextension`. Сейчас ни одного signing identity нет. `packaging/Host-Release.entitlements`, `Provider.entitlements` и `Provider-Info.plist` — подготовленные шаблоны; TeamIdentifierPrefix должен быть заменён реальным значением перед подписью.

Direct distribution использует `Contents/Library/SystemExtensions/<bundle-id>.systemextension`. Packet Tunnel app extension не подменяет этот путь. Host должен активировать provider через OSSystemExtensionRequest и настроить NETunnelProviderManager; macOS показывает свои подтверждения установки расширения и VPN-конфигурации. Эти механизмы ещё не подключены к local shell. Provider source сейчас отклоняет startTunnel, не меняя routes/DNS.

До включения релизной сборки: реализовать и проверить packet-flow bridge/обе engine libraries/защиту TCP+UDP сокетов и bootstrap DNS; immutable RU snapshot; durable FakeIP ledger; full config validation; проверку фактического трафика перед Connected; rollback; sleep/wake и изменение сети.

Подписывать внутри наружу: runtime/native SQLite/Go Link/engine libraries, broker с allow-jit, provider с его entitlements/profile, затем host. Для public release включить hardened runtime, Developer ID и timestamp, проверить `codesign --verify --deep --strict`. После успешной VPN-приёмки: `xcrun notarytool submit <dmg> --keychain-profile <profile> --wait`, затем `xcrun stapler staple <dmg>` и `spctl --assess`. Credentials/profile/password не добавлять в Git. Новый SHA считается после финальной подписи и stapling.

GitHub workflow пока выполняет local build и сохраняет артефакты. Публикация подписанного VPN-релиза должна быть отдельным проверенным этапом; неподписанный local DMG не объявляется готовым релизом.
