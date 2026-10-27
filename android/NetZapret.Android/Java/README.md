# Java-часть NetZapret для Android (WP3)

Всё, что трогает libbox, — здесь, в пакете `org.netzapret.android`. В C# нет
ни одного типа libbox (DESIGN.md: привязка gomobile валит мост сборщика мусора
Mono), C# зовёт шесть статических методов `NzBox` сырым JNI (`Host/NzBoxBridge.cs`).

| Файл | Что делает |
|---|---|
| `NzBox.java` | Договор DESIGN.md §4: `start`, `stop`, `status`, `command`, `checkConfig`, `version`; состояние, очередь запуска/остановки (`GATE`), общий журнал |
| `NzVpnService.java` | `VpnService` на переднем плане: порядок запуска root → ByeDPI → `Libbox.setup` → сервер команд → `startOrReloadService`, остановка наоборот, перезагрузка конфига, уведомление с «Остановить», Doze |
| `NzPlatform.java` | `io.nekohasekai.libbox.PlatformInterface`: `openTun` через `VpnService.Builder`, `protect`, `bindInterfaceControl`, интерфейсы, владелец соединения, уведомления движка |
| `NzNetworkMonitor.java` | Настоящая сеть телефона (не VPN) для Go и локального DNS |
| `NzLocalDns.java` | DNS типа `local` (bootstrap): `DnsResolver.rawQuery` на API 29+, `Network.getAllByName` ниже |
| `NzCommands.java` | Клиент сервера команд libbox: журнал, группы, режим — постоянной связью; выбор, замер, соединения — разовой |
| `NzByeDpi.java` | `libbyedpi.so` дочерним процессом: пароль SOCKS5 из `NZ_BYEDPI_AUTH`, готовность, перезапуски 0,5/1/2/5 с (порт на паузе держит сам надзор), свои 200 строк вывода; случайный свободный порт и пароль на подъём; порт за чужим слушателем — переезд |
| `NzRoot.java` | Команды root для nfqws2 (`request.root.start/stop`) |
| `NzTileService.java` | Плитка в шторке |
| `NzRequest.java`, `NzLibbox.java`, `NzLog.java`, `NzSecrets.java`, `NzProc.java`, `NzStrings.java`, `NzInterfaces.java` | Запрос, его файл и снимок сеанса, переписывание порта и пароля ByeDPI в конфиге и аргументах, `Libbox.setup` один раз, кольцо журнала, вычистка секретов, дочерние процессы на API 24+, итераторы для Go |

## Что нужно в манифесте (`Properties/AndroidManifest.xml`)

Всё ниже в манифесте WP4 уже есть (сверено 27.10); `TOGGLEABLE_TILE` — по желанию.

```xml
<uses-permission android:name="android.permission.INTERNET" />
<uses-permission android:name="android.permission.ACCESS_NETWORK_STATE" />
<!-- requestNetwork на Android 9–11 (NzNetworkMonitor) без него бросает SecurityException -->
<uses-permission android:name="android.permission.CHANGE_NETWORK_STATE" />
<uses-permission android:name="android.permission.FOREGROUND_SERVICE" />
<uses-permission android:name="android.permission.FOREGROUND_SERVICE_SYSTEM_EXEMPTED" />
<uses-permission android:name="android.permission.POST_NOTIFICATIONS" />
<!-- addAllowed/DisallowedApplication и пакеты владельца соединения: без него Android 11+
     не видит чужих пакетов, и exclude_package молча пропускался бы -->
<uses-permission android:name="android.permission.QUERY_ALL_PACKAGES" />

<application android:extractNativeLibs="true" …>
  <service
      android:name="org.netzapret.android.NzVpnService"
      android:permission="android.permission.BIND_VPN_SERVICE"
      android:exported="false"
      android:foregroundServiceType="systemExempted">
    <intent-filter>
      <action android:name="android.net.VpnService" />
    </intent-filter>
    <meta-data android:name="android.net.VpnService.SUPPORTS_ALWAYS_ON" android:value="true" />
  </service>

  <service
      android:name="org.netzapret.android.NzTileService"
      android:permission="android.permission.BIND_QUICK_SETTINGS_TILE"
      android:exported="true"
      android:icon="@drawable/ic_tile"
      android:label="NetZapret">
    <intent-filter>
      <action android:name="android.service.quicksettings.action.QS_TILE" />
    </intent-filter>
    <!-- по желанию: Android 13+ показывает плитку переключателем -->
    <meta-data android:name="android.service.quicksettings.TOGGLEABLE_TILE" android:value="true" />
  </service>
</application>
```

Служба и приложение — в одном процессе (без `android:process`): `NzBox`
и служба делят статическое состояние.

## Что нужно в `.csproj`

```xml
<AndroidLibrary Include="$(NzEngines)libbox-1.14.3-extended-2.7.3\libbox.aar" Bind="false" />
<AndroidJavaSource Include="Java\**\*.java" Bind="false" />
<AndroidNativeLibrary Include="$(NzEngines)byedpi-ba532298-nz\arm64-v8a\libbyedpi.so" Abi="arm64-v8a" />
<!-- если включено сжатие R8 (AndroidLinkTool=r8): -->
<ProguardConfiguration Include="Java\proguard.cfg" />
```

Ресурсы: `drawable/ic_tile` — одноцветный значок; служба берёт его и для
уведомления (по имени, `getIdentifier`), без него — `android.R.drawable.ic_lock_lock`.

Код собирается и на уровне Java 8 (`javac -source 8`), и на 11; только API
платформы, без AndroidX; всё новее API 24 — под `Build.VERSION.SDK_INT`.

## Договор с C#

JNI-подписи (как в `NzBoxBridge`):

```
start        (Landroid/content/Context;Ljava/lang/String;)V
stop         (Landroid/content/Context;)V
status       ()Ljava/lang/String;
command      (Ljava/lang/String;Ljava/lang/String;)Ljava/lang/String;
checkConfig  (Ljava/lang/String;)Ljava/lang/String;
version      ()Ljava/lang/String;
```

- **Ни один метод не бросает.** Ошибка запуска — `status().state = "failed"`
  и `error`; ошибка команды — `{"error":"…"}`; `checkConfig` — непустая строка.
- `start` и `stop` не ждут: запуск идёт в очереди службы, итог — в `status()`.
  `start` при работающем движке перезагружает конфиг; тот же конфиг и тот же
  ByeDPI/root — ничего не трогает. **Разрешение на VPN** (`VpnService.prepare`)
  получает активность заранее: без него `start` сразу даёт `failed`.
- `command` блокирует до ~3 с (разовый клиент сервера команд) — звать не из
  потока окна. `checkConfig` — тоже не из него (собирает движок целиком).
- При смене состояния рассылается `org.netzapret.android.STATUS_CHANGED`
  (`setPackage` своего пакета) — можно слушать вместо частого опроса.

### `requestJson` (DESIGN.md §4)

```json
{ "configPath": "/data/user/0/<pkg>/files/runtime/singbox.json",
  "workingPath": "/data/user/0/<pkg>/files/runtime/libbox",
  "byedpi": { "args": ["-i","socks5://127.0.0.1","-p","41234"], "auth": "nz:секрет",
              "port": 41234, "workDir": "/…/files/runtime/byedpi" },
  "root": { "start": ["su","-c","sh …/nfqws2-start.sh"], "stop": ["su","-c","sh …/nfqws2-stop.sh"] },
  "notificationTitle": "NetZapret" }
```

- `configPath` обязателен и полный. `byedpi.port` обязателен: по нему служба
  находит в конфиге исходящий на ByeDPI (`type: socks`, сервер на петле,
  `server_port` равен ему). `byedpi.auth` — «имя:пароль»; передаётся ByeDPI
  переменной `NZ_BYEDPI_AUTH`, в argv его класть не надо. `byedpi.args` — без
  имени файла.
- **Порт и пароль ByeDPI из запроса служба не использует**: на каждый подъём
  (из приложения, с плитки, постоянным VPN, при смене ByeDPI в перезагрузке)
  она берёт свободный случайный порт 30000–60999 и новый пароль, переписывает
  ими `-p` в `args` и тот исходящий SOCKS в конфиге. Сохранённый запрос живёт
  между сеансами, и его порт мог занять чужой слушатель, пока VPN был выключен
  (разбор 27.10). Порт и пароль хозяина нужны только для поиска исходящего.
- Запрос сохраняется в `files/runtime/nz-request.json` **со снимком**: конфиг
  и папка `byedpi.workDir` копируются в `files/runtime/nz-session/<имя>/`, в
  сохранённом запросе пути (и пути списков в `args`, начинающиеся с `workDir`)
  указывают туда. Служба читает только снимок — хозяин может переписывать
  `singbox.json` и `runtime/byedpi` сразу после `start`, в том числе в попытке,
  которая потом оборвётся. Из снимка поднимаются постоянный VPN системы и плитка;
  ненужные снимки служба стирает после каждого запуска.
- `workingPath` — рабочая папка libbox (относительные пути конфига, `cache.db`);
  пусто — `files/runtime/libbox`. `command.sock` и снимок конфига
  `configuration.json` (с ключами — только во внутренней папке) libbox кладёт
  в `files/runtime/libbox`.

### `status()`

```json
{ "state": "stopped|starting|running|stopping|failed",
  "error": "текст или null", "startedAt": 1730000000000,
  "byedpi": { "running": true, "ready": true, "restarts": 0, "lastError": null,
              "port": 41234, "log": ["последние 20 строк"] } | null,
  "root": { "running": true, "lastError": null } | null,
  "log": ["чч:мм:сс [движок|nz|byedpi|root] строка", "… последние 100"] }
```

`ready` и `port`, `log` у ByeDPI — сверх DESIGN.md; `port` — порт этого
подъёма, не из запроса. После отзыва VPN системой (другой VPN, снято
разрешение) — `state: "stopped"` и `error` с причиной.

Если libbox не закрылся за 4 с, после остановки состояние — `"stopping"` с
`error` «движок ещё закрывается…», и `"stopped"` появится, только когда он
закроется: до этого Go держит свою копию TUN (VPN жив) и `command.sock`.
Новый запуск ждёт его до 30 с, затем — `"failed"` с объяснением.

### `command(name, argJson)`

| name | arg | ответ |
|---|---|---|
| `groups` | — | `[{tag,type,selected,selectable,items:[{tag,type,delay,time}]}]`; `delay` — мс, 0 — не мерили; `time` — секунды Unix |
| `select` | `{group,outbound}` | `{"ok":true}` |
| `urltest` | `{group}` или `{outbound}` | `{"ok":true}`; группа больше чем из одного сервера — `{"error":…}` (группа мерила бы всех разом, CLAUDE.md «Продавцов не бить залпом»). Результат — в `groups` |
| `connections` | `{state: active\|closed\|all, limit}` | `[{id,inbound,inboundType,ipVersion,network,source,destination,domain,protocol,rule,outbound,outboundType,chain:[…],createdAt,closedAt,upload,download,uid,packages:[…]}]`, новые первыми |
| `closeConnection` | `{id}` | `{"ok":true}` |
| `closeConnections` | — | `{"ok":true}` |
| `clashMode` | `{mode}` — сменить; без `mode` | `{"ok":true}` / `{mode,modes:[…]}` |
| `logs` | `{source: all\|engine\|nz\|byedpi\|root, limit}` | `["строка", …]`; работает и без движка |

## Поведение, которое стоит знать хозяину

- **ByeDPI и root не роняют VPN.** ByeDPI не ответил за 5 с — VPN всё равно
  поднимается, надзор перезапускает ByeDPI (как winws2 на Windows: туннель
  жив, «движок не работает»); причина — в `status().byedpi.lastError`.
  Команда root упала — `status().root.lastError`, VPN работает.
- **Порт ByeDPI за чужим слушателем — переезд.** ByeDPI вышел, а его порт
  слушает другая программа (bind не удался или порт заняли после падения) —
  надзор не перезапускает его там, а служба поднимает ByeDPI на новом порту и
  перезагружает конфиг (VPN переоткрывается). В паузе между перезапусками
  порт держит сам надзор: соединения сбрасываются, чужой встать не может.
  Четвёртый переезд за запуск — движки гасятся, `"failed"`.
- Своё приложение всегда мимо TUN: `openTun` дописывает свой пакет в
  `addDisallowedApplication`, а при `include_package` в конфиге свой пакет
  в список разрешённых не попадает. Через это идут ByeDPI и пробы «напрямую».
- Сервер команд libbox — на Unix-сокете `files/runtime/libbox/command.sock`,
  без слушателя на 127.0.0.1.
- Все строки наружу (журнал, ошибки, ответы) проходят через `NzSecrets.scrub`:
  UUID, ссылки серверов, токены в ссылках, поля `password`/`uuid`/… в JSON и
  пароль ByeDPI этой сессии заменяются на `<скрыто>`. Теги серверов в `groups`
  не трогаются — по ним выбирают.

## Не проверено

Проверено только без телефона (27.10): `javac -source 11` и `-source 8` против
`android.jar` (API 36) и `classes.jar` libbox — 0 ошибок; `d8 --min-api 24`
— без замечаний. Прежняя запись «Android lint (NewApi, minSdk 24) — 0 ошибок»
была неверна: lint на том же коде находит `Process.isAlive` (API 26) в
`NzRoot` — на Android 7 с root он ронял весь запуск. Исправлено; после правок
`lint --check NewApi,InlinedApi` (minSdk 24) — «No issues found».
На телефоне не запускалось ничего: ни VPN, ни ByeDPI, ни плитка, ни
сервер команд. В частности не проверены: порядок и тайминги `startForeground`
на Android 8–15, `systemExempted` на 14+, `excludeRoute` на 13+, локальный
DNS через `DnsResolver`, переподключение клиента команд, перезапуски ByeDPI
и убийство «призрачных» процессов Android 12+, `su` с окном Magisk; снимок
запроса и чистка снимков, переписывание конфига через org.json на конфиге
«Туннеля» (~1–2 МБ: время и память), удержание порта в паузе и переезд
ByeDPI, ожидание прежнего libbox и «stopping» до его закрытия.

Чего правки 27.10 не закрывают: окно между пробной привязкой порта и `bind`
самого ByeDPI (его разбор аргументов и списков) остаётся — чужой слушатель,
вставший ровно в него, получит соединения «десинка», пока ByeDPI не выйдет
и надзор не увидит занятый порт. Закрыла бы его метка из ByeDPI после
`listen()` (правка `byedpi-nz-auth.patch`) — готовность только по ней.
Команды root (`root.start/stop`) снимком не покрыты: сценарии и списки
nfqws2 хозяин пишет сам, и запуск с плитки берёт их как есть.
