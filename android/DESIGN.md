# NetZapret для Android — устройство и договор между частями

Рабочий документ ночи 27.10.2026. Почему так — в `docs/android.md`
(исследование 26.10) и в заметках разборов (скрэтчпад сессии, `notes/*.md`:
`libbox.md`, `config.md`, `byedpi.md`, `nfqws2.md`, `ui.md`,
`portability.md`). Здесь — только решения и интерфейсы, по которым части
пишутся параллельно.

## Требования владельца

1. **Без root.** Всё главное работает на обычном телефоне.
2. **Тот же движок** — sing-box extended (форк shtorm-7, тег
   `v1.14.3-extended-2.7.3`), libbox, собранный gomobile из него.
3. **Аналог winws2** — десинк. Без root это **ByeDPI** (hufrea, MIT,
   `main@ba532298` + наша правка `android/patches/byedpi-nz-auth.patch` —
   SOCKS5 с паролем). С root — **nfqws2** bol-van v1.0.6 с теми же
   пресетами, lua и списками.
4. **Правки Windows-версии доходят до телефона одной командой**:
   библиотеки `src/` общие (две цели: `net8.0-windows` и `net8.0`),
   пресеты, списки, lua, каталог и образцы пакетов берутся из тех же папок
   репозитория при сборке APK (`android/build.ps1`).

## Устройство

```
NetZapret.Android (net10.0-android, Avalonia.Android)  — хозяин
├─ C#: Application, MainActivity, распаковка поставки, крючки src/,
│      AndroidEngineHost (сборка конфига, ByeDPI, root), мост к Java
├─ Java (AndroidJavaSource): NzVpnService, NzPlatform, NzBox, NzByeDpi, NzRoot
│      — всё, что трогает libbox; libbox.aar подключён Bind="false"
├─ нативные файлы (lib/<abi>/): libbyedpi.so, libnfqws2.so — исполняемые
└─ ссылки: NetZapret.Mobile (UI, net10.0) и библиотеки src/ (net8.0)

NetZapret.Mobile (net10.0, Avalonia) — интерфейс телефона, без Android API
└─ разделы; всё платформенное — через IMobileHost

NetZapret.Mobile.Preview (net10.0, Avalonia.Desktop) — тот же интерфейс
   в окне 412×915 на Windows с поддельным хозяином: смотреть и снимать
   экраны без телефона.
```

**Почему libbox не привязан к C#.** Привязка собирается (проверено 27.10:
0 ошибок, 161 класс), но в VPNRouter (Avalonia 12, .NET 10, libbox — ближайший
образец) генератор привязок Mono.Android на ~80 прокси-классах gomobile
даёт падение моста сборщика мусора при запуске («asked if a class
System.Object is a bridge before we inited java.lang.Object»); там весь код
libbox ушёл в Java. Мы делаем так же сразу: в C# нет ни одного типа libbox,
обмен — строками JSON через `NzBox`.

**Почему ByeDPI отдельным процессом.** Глобальное состояние ByeDPI между
запусками не сбрасывается (ByeByeDPI запрещает второй запуск в процессе),
падение C уронило бы VPN, а его `signal()` подменяет обработчики всего
процесса. Файл `libbyedpi.so` — исполняемый PIE, лежит в `nativeLibraryDir`
(`extractNativeLibs="true"`), запускается до `establish()`.

## Режимы и маршруты на телефоне

| Маршрут правила | Без root | С root (nfqws2 поднят) |
|---|---|---|
| VPN | исходящий подписки / WARP | так же |
| Напрямую | `direct` | `direct` |
| Десинк | `resolve` + исходящий `socks` «desync» → ByeDPI 127.0.0.1:<порт>, логин/пароль на сессию; QUIC к этим именам — `reject` (браузер уходит на TCP). ByeDPI выключен в настройках — `tls_record_fragment`/`tls_fragment` на правиле | `direct`: пакеты ловит nfqws2 в iptables |
| Блок | `reject` | `reject` |

- Захват TUN — **полный** (все приложения), своё приложение —
  `exclude_package` (через него ходят ByeDPI и пробы «напрямую»).
  Решение «полного перехвата в Гибриде не будет» стоит на устройстве
  Windows и сюда не переносится (`docs/android.md`).
- Режим «Только десинк» на телефоне — тот же TUN без исходящих подписки.
- Пины — DNS-сервер `hosts` с `path` на `<files>/config/hosts`
  (`config.md` §3); `/etc/hosts` не трогается.
- На телефоне **нет слушателей на 127.0.0.1 без пароля**: Clash API
  выключен (управление — сервер команд libbox на Unix-сокете в папке
  приложения), `health-in` и `local-in` не пишутся.

## Договоры

### 1. Сборка конфига — `src/NetZapret.Proxy/EngineConfigBuilder.cs` (часть WP1)

По `config.md` §5 (типы `SingBoxPlatform`, `EngineHost`, `SubscriptionSources`,
`AndroidEngine`, `EngineBuild`, `EngineConfigBuilder.BuildTunnelAsync`,
`BuildDnsOnly`, `TunnelOptions`, `Plan`) с одним дополнением — ByeDPI:

```csharp
public sealed record DesyncSocks(int Port, string Username, string Password);

public sealed record AndroidEngine
{
    // …поля из config.md §5.1…
    /// <summary>ByeDPI для маршрута «десинк» без root; null — фрагментация sing-box.</summary>
    public DesyncSocks? Socks { get; init; }
}
```

- Windows-вывод — **байт в байт прежний** (золотой тест, `config.md` §5.6).
- `TunnelConfig.BuildAsync` в окне становится тонкой обёрткой над
  `BuildTunnelAsync` (`WindowsEngineHost`).
- `SubscriptionBook` переезжает из `gui/` в `NetZapret.Subscriptions`
  (тот же класс, то же пространство имён меняется на `NetZapret.Subscriptions`).

### 2. Командная строка ByeDPI — `src/NetZapret.Zapret/ByeDpiCommandLine.cs` (WP2)

```csharp
public sealed record ByeDpiRequest
{
    public required string PresetPath { get; init; }        // presets/<имя>.txt
    public required string ZapretRoot { get; init; }        // engines/zapret (lists, bin)
    public required string WorkDirectory { get; init; }     // куда писать списки групп
    public required int Port { get; init; }
    public ByeDpiStrategy Strategy { get; init; } = ByeDpiStrategy.Preset;
    public string? CustomArguments { get; init; }           // своя строка человека (Strategy.Custom)
    public bool Md5Sig { get; init; }                       // ядро умеет TCP_MD5SIG
    public bool Udp { get; init; }
}
public enum ByeDpiStrategy { Preset, Default, Custom }
public sealed record ByeDpiPlan(
    IReadOnlyList<string> Arguments,                         // без имени файла
    IReadOnlyDictionary<string, string> Files,               // путь от WorkDirectory → содержимое
    IReadOnlyList<string> Warnings,                          // что не переводится без root
    IReadOnlyList<string> PassHosts);                        // имена секций pass → sing-box ведёт их напрямую
public static class ByeDpiCommandLine
{
    public static ByeDpiPlan Build(ByeDpiRequest request);
    public static string Normalize(IEnumerable<string> lines);   // список для -H (byedpi.md §3.5)
    public const string DefaultStrategy = "...";                 // byedpi.md §4.3
}
```

Перевод рецептов — `byedpi.md` §3.3–3.4 (склейка соседних секций с
проверкой пересечений, не больше 64 групп вместе с запасными, `-K t,h`
у групп со списком, `-N`, `-i socks5://127.0.0.1`).

### 3. nfqws2 и iptables — `src/NetZapret.Zapret/Nfqws2CommandLine.cs`, `NfqueueFirewall.cs` (WP2)

По `nfqws2.md` §3–4 (типы и тесты оттуда). Только сборка аргументов
и списков команд iptables — запуск через `su` делает хозяин.

### 4. Мост Java ↔ C# — `NzBox` (WP3 пишет Java, WP4 — обёртку C#)

Java-класс `org.netzapret.android.NzBox`, только статические методы
с простыми типами:

```java
public static void start(android.content.Context ctx, String requestJson);
public static void stop(android.content.Context ctx);
public static String status();                       // JSON, см. ниже
public static String command(String name, String argJson);  // JSON-ответ
public static String checkConfig(String configJson); // "" — годен, иначе текст ошибки
public static String version();                      // версия libbox
```

`requestJson` (пишет C#):

```json
{
  "configPath": "/data/user/0/<pkg>/files/runtime/singbox.json",
  "workingPath": "/data/user/0/<pkg>/files/runtime/libbox",
  "byedpi": { "args": ["-i","socks5://127.0.0.1","-p","41234", "..."],
              "auth": "nz:секрет", "port": 41234, "workDir": "/…/files/runtime/byedpi" } | null,
  "root":   { "start": ["su","-c","…"], "stop": ["su","-c","…"] } | null,
  "notificationTitle": "NetZapret"
}
```

`status()`:

```json
{ "state": "stopped|starting|running|stopping|failed",
  "error": "текст или null", "startedAt": 1730000000000,
  "byedpi": { "running": true, "restarts": 0, "lastError": null } | null,
  "root": { "running": true } | null,
  "log": ["последние строки журнала движка", "..."] }
```

`command(name, arg)`: `"groups"` → `[{tag,type,selected,items:[{tag,type,delay}]}]`;
`"select"` `{group,outbound}`; `"urltest"` `{group}` (только группе из одного
сервера или серверу — `libbox.md`: группа меряет всех разом); `"connections"`
→ список; `"clashMode"` `{mode}`; `"logs"` → строки. Ответ-ошибка:
`{"error":"…"}`.

Секреты (ссылки подписок, ключи, пароль ByeDPI) **не пишутся ни в журнал,
ни в status**.

### 5. Интерфейс ↔ хозяин — `android/NetZapret.Mobile/IMobileHost.cs` (WP5 пишет, WP4 реализует)

```csharp
public interface IMobileHost
{
    string FilesDirectory { get; }
    EngineStatus Status { get; }                 // из NzBox.status
    event Action? StatusChanged;
    Task<string?> StartAsync(CancellationToken ct);   // null — поднято, иначе причина
    Task StopAsync();
    Task<string> CommandAsync(string name, string argJson);
    PlatformFacts Facts();                        // root, частный DNS, батарея, другой VPN, ABI, версия Android
    IReadOnlyList<InstalledApp> InstalledApps();  // пакет, имя, системное ли
    Task ShareFileAsync(string path, string mime);
    Task OpenUrlAsync(string url);
    Task<string?> ReadClipboardAsync();
    Task<string?> ScanQrAsync();                  // null — не поддержано
}
public sealed record EngineStatus(string State, string? Error, DateTimeOffset? StartedAt,
    bool DesyncRunning, int DesyncRestarts, IReadOnlyList<string> Log);
public sealed record PlatformFacts(bool Root, string? PrivateDns, bool BatteryOptimized,
    bool OtherVpnActive, bool AlwaysOn, string Abi, int Sdk, string AppVersion);
public sealed record InstalledApp(string Package, string Label, bool System);
```

## Кто чем владеет (параллельная работа)

| Часть | Файлы | Где собирать |
|---|---|---|
| WP1 конфиг | `src/NetZapret.Proxy/**`, `src/NetZapret.Subscriptions/SubscriptionBook.cs` (переезд), `gui/NetZapret.Gui/TunnelConfig.cs`, `WindowsEngineHost.cs`, ссылки на `SubscriptionBook` в `gui/`, тесты `tests/NetZapret.Core.Tests/EngineConfigBuilder*` | своя рабочая копия `netzapret-wp1` |
| WP2 десинк | `src/NetZapret.Zapret/ByeDpi*.cs`, `Nfqws2*.cs`, `NfqueueFirewall.cs`, тесты к ним | своя рабочая копия `netzapret-wp2` |
| WP3 Java | `android/NetZapret.Android/Java/**` | проверка `javac` с `android.jar` и классами libbox |
| WP4 хозяин | `android/NetZapret.Android/**` кроме `Java/`, `android/Directory.Build.props`, `android/build.ps1`, `android/engines.ps1` | `android/`, свой `dotnet` 10 |
| WP5 интерфейс | `android/NetZapret.Mobile/**`, `android/NetZapret.Mobile.Preview/**` | `android/`, свой `dotnet` 10 |

После параллельной части — слияние WP1 и WP2 в `android`, сборка APK
целиком, правки, проверка.

## Сборка

- Инструменты (без прав администратора) — `C:\Users\rogfa\.nz-android`:
  `dotnet` 10.0.403 с рабочей нагрузкой android 36.1.118, JDK 17, Go 1.26.10,
  Android SDK (platform 36, build-tools 36.0.0, NDK 28.2.13676358), gomobile
  v0.1.13 SagerNet.
- Движки — `tools\android\` (вне git, как `tools\*` у Windows):
  `libbox-1.14.3-extended-2.7.3\libbox.aar` (sha256 `1370fc55…37adae`),
  `byedpi-ba532298-nz\<abi>\libbyedpi.so`, `zapret2-v1.0.6\<abi>\libnfqws2.so`
  (sha256 arm64 `2f0d6316…eec3c` — как в выпуске bol-van).
  Собирает и кладёт их `android\engines.ps1`.
- `android\build.ps1` — одна команда: движки (если нет), поставка из
  `config/`, `presets/`, `vendor/zapret-gui/`, сборка APK.
