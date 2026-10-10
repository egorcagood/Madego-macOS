# Два VPN-ядра: проверка 2026-10-10

Exact source Xray v26.9.9, commit `52a412d9e2f5c2a5142b1b4e2ab3771dacb8b120`, официальный XTLS repository. Sing-box: frozen modified 1.14.1-madego-ru1, upstream `1ac1a339cb1223e9c70eae14c44411c75033c02d`. Go 1.27.1 arm64. Scratch probe в отдельной папке; baseline не менялась.

Проверено: оба ядра link в одном Go executable с `with_gvisor,with_utls,with_quic`; Xray `core.StartInstance("json",fixture)` → `Close()`; sing-box `libbox.CheckConfig` корректного fixture. Результат `DUAL_LINK_LIFECYCLE_PASS`. Config fixture использует только loopback SOCKS и blackhole; системные routes, DNS, TUN и пользовательские credentials не затрагивались. Линкер предупредил о повторном `-lobjc`; бинарник запустился. Эта проверка ещё не создаёт две работающие VPN-инстанции, c-shared библиотеку или NetworkExtension.

По точным API: Xray поддерживает library lifecycle и RegisterDialerController для защиты fd. libbox PlatformInterface.OpenTun возвращает fd; native Darwin Tun требует настоящий utun descriptor через GetsockoptString(UTUN_OPT_IFNAME). NEPacketTunnelFlow даёт публичные readPackets/writePackets, но не публичный utun fd. KVC-извлечение fd или raw utun вне NetworkExtension не выбрано.

Перспективный путь — свой bounded userspace packet adapter поверх публичного NEPacketTunnelFlow и sing-tun GVisorTun (WritePacket/NewEndpoint), либо подтверждённый официальный FD adapter. Exact GVisorTun требует дополнительный stack.LinkEndpoint; простого io.ReadWriter/socketpair недостаточно. Этот adapter, правильный physical-interface socket protection, packet health, DNS и continuity ещё не реализованы и не проверены. Поэтому перенос не объявляется VPN-ready.

Xray остаётся необходимым: sing-box-only теряет XHTTP и часть REALITY/encryption параметров текущей Windows-линии. Shared Core сохраняет параметры, а safe server exporter отклоняет opaque extra вместо тихого downgrade.

Дальнейшее доказательство: packet-flow bridge через C ABI в одном embedded Go runtime → exact candidate configs → физические TCP/UDP/DNS сокеты без route loop → provider активация с Team → traffic/DNS/IPv6/RU/FakeIP/rollback/sleep/Wi-Fi tests. Native signing blocker не доказывает, что архитектура неосуществима.
