# sing-box: выбор движка и особенности конфига

Проверено на sing-box 1.13.19 (`windows-amd64`), лежит в `tools/`.

## Один движок, не два

Первоначально предполагалась связка sing-box + Xray: подписка пользователя
показывала транспорт XHTTP, а его sing-box не поддерживает — это транспорт Xray.
Разбор реальной подписки показал, что вывод был поспешным:

| Протокол / транспорт | Серверов | sing-box | Xray |
| --- | --- | --- | --- |
| `hysteria2` | 7 | да | **нет** |
| `trojan` / tcp / reality | 4 | да | да |
| `vless` / tcp / reality | 3 | да | да |
| `vless` / **xhttp** / reality | 4 | **нет** | да |

sing-box покрывает 14 серверов из 18, Xray — 11, потому что не умеет Hysteria2.
Поэтому движок один — sing-box, а четыре XHTTP-сервера просто недоступны.
Если они когда-нибудь понадобятся, Xray добавляется вторым процессом с
SOCKS-инбаундом, к которому sing-box подключается через `socks`-outbound;
такой конфиг тоже проверен и работает.

## Особенности, на которые уходит время

**UTF-8 без BOM.** Go спотыкается о метку порядка байт: `invalid character 'ï'
looking for beginning of value`. Генератор обязан писать конфиг без BOM.

**`route.default_domain_resolver` обязателен** начиная с 1.12. Без него запуск
падает с требованием выставить `ENABLE_DEPRECATED_MISSING_DOMAIN_RESOLVER`.

**DNS-сервер по имени требует резолвера.** `"server": "dns.google"` не пройдёт
без явного `domain_resolver`; проще указывать адресом.

**Проверка без запуска и без прав администратора:**

```
tools\sing-box-1.13.19-windows-amd64\sing-box.exe check -c config.json
```

Возвращает 0 при успехе. Это позволяет отлаживать генератор конфига полностью
локально, не поднимая TUN.

## Проверенный скелет

Ниже то, что генератор должен собирать. Учётные данные подставные.

```json
{
  "log": { "level": "warn" },
  "dns": {
    "servers": [
      { "tag": "remote", "type": "https", "server": "8.8.8.8" },
      { "tag": "fake", "type": "fakeip", "inet4_range": "198.18.0.0/15", "inet6_range": "fc00::/18" }
    ],
    "rules": [{ "query_type": ["A", "AAAA"], "server": "fake" }],
    "independent_cache": true
  },
  "inbounds": [
    {
      "type": "tun",
      "tag": "tun-in",
      "address": ["172.19.0.1/30"],
      "auto_route": true,
      "strict_route": false,
      "stack": "gvisor"
    }
  ],
  "outbounds": [
    { "type": "direct", "tag": "direct" },
    {
      "type": "hysteria2",
      "tag": "hy2",
      "server": "example.com",
      "server_port": 4443,
      "password": "PLACEHOLDER",
      "tls": { "enabled": true, "server_name": "example.com" }
    },
    {
      "type": "vless",
      "tag": "vl-reality",
      "server": "example.com",
      "server_port": 443,
      "uuid": "PLACEHOLDER",
      "flow": "xtls-rprx-vision",
      "tls": {
        "enabled": true,
        "server_name": "example.com",
        "utls": { "enabled": true, "fingerprint": "chrome" },
        "reality": { "enabled": true, "public_key": "PLACEHOLDER", "short_id": "PLACEHOLDER" }
      }
    },
    { "type": "selector", "tag": "auto", "outbounds": ["hy2", "vl-reality"], "default": "hy2" }
  ],
  "route": {
    "rules": [
      { "action": "sniff" },
      { "protocol": "dns", "action": "hijack-dns" },
      { "ip_is_private": true, "outbound": "direct" },
      { "process_name": ["steam.exe"], "outbound": "direct" },
      { "domain_suffix": ["rutracker.org"], "outbound": "auto" }
    ],
    "final": "direct",
    "auto_detect_interface": true,
    "default_domain_resolver": { "server": "remote" }
  },
  "experimental": {
    "clash_api": { "external_controller": "127.0.0.1:9090" }
  }
}
```

Соответствие правилам NetZapret: `mode: proxy` → `outbound` с тегом сервера или
`auto`; `mode: direct` и `mode: desync` → `direct`. Различие между последними
двумя появится, когда `desync`-трафик потребуется уводить мимо TUN целиком.

## Формат ссылок в подписке

Тело подписки приходит в base64, внутри — по URI на строку. Наблюдённые формы:

```
hysteria2://<пароль>@<хост>:<порт>/?sni=<имя>#<название>
trojan://<пароль>@<хост>:<порт>?security=reality&sni=&fp=&pbk=&sid=&type=tcp#<название>
vless://<uuid>@<хост>:<порт>?security=reality&encryption=none&flow=&fp=&pbk=&sid=&sni=&type=tcp#<название>
vless://<uuid>@<хост>:<порт>?security=reality&encryption=none&type=xhttp&mode=&path=&extra=&fp=&pbk=&sid=&sni=#<название>
```

Ловушка для парсера: у `hysteria2` между портом и `?` стоит `/`, так что порт
нельзя брать до первого `?` — надо сперва отрезать путь.

Заголовки ответа несут данные для интерфейса: `subscription-userinfo`
(`upload`, `download`, `total`, `expire` — квота и срок), `profile-title`
(base64 с названием подписки), `profile-update-interval`.
