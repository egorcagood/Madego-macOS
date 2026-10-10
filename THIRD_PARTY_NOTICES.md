# Лицензии и происхождение

Новый macOS-порт и общая часть: GPL-3.0-only по разрешению владельца исходников. Текст GPLv3 — LICENSE. База Windows 0.19.2 сохранена отдельно; shared-source содержит только используемые файлы, каждый SHA-256 в build-metadata/shared-source.json. Старые Windows releases сюда не входят.

- Managed PeerChat и Link protocol/crypto не переписаны; исходники взяты из Windows 0.19.2. BouncyCastle.Cryptography 2.7.0: MIT; Microsoft.Data.Sqlite 9.0.20: MIT; SQLitePCLRaw 2.1.12: Apache-2.0; SQLite engine: public domain. Отдельные тексты в shared-source/src/EgorVpn/assets/licenses.
- Go Link uses libp2p 0.50.0 and kad-dht 0.42.2. Exact module graph is go.mod/go.sum; individual dependency licenses in shared-source/src/Madyar.Link/LICENSES. No centralized relay is promised. Source rebuild is available in scripts/build-local.py.
- Public suffix list: MPL-2.0, license in shared-source/src/EgorVpn/Routing/data. Domain seeds: their original notice and provenance preserved next to files.
- Self-contained .NET runtime is redistributed under its bundled Microsoft MIT/third-party notices (LICENSE.txt, ThirdPartyNotices.txt in Core). Native SwiftUI/AppKit/Security frameworks are part of macOS; no third-party UI assets/fonts are included in this local candidate.
- Xray v26.9.9 (MPL-2.0) and sing-box 1.14.1-madego-ru1 (GPL-3.0-or-later) are research inputs, not bundled VPN engines in this candidate. Modified sing-box corresponding source remains in the immutable local baseline. A future engine distribution must include its exact source/build inputs and all notices before upload.

Generated app/DMG excludes user profiles, subscriptions, identity, message databases, keychain data and runtime logs. Local development signing is ad-hoc, not Developer ID and not notarization.
