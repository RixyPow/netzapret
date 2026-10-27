# NetZapret для Android

Ветка `android`, ночь 26–27.10.2026. **Собирается, на телефоне ещё не
запускалась.** Почему устроено так — `docs/android.md` (исследование)
и `android/DESIGN.md` (устройство и договор между частями).

## Что внутри

| | Чем | Root |
|---|---|---|
| Туннель (VPN, подписки, WARP, пины) | libbox — **тот же sing-box extended**, что у Windows (форк shtorm-7, тег `v1.14.3-extended-2.7.3`), собранный gomobile | не нужен |
| Десинк — аналог winws2 | **ByeDPI** (hufrea, MIT, `main@ba532298` + наша правка: пароль SOCKS5, 64 группы) отдельным процессом; пресет переводится в группы ByeDPI (`ByeDpiCommandLine`) | не нужен |
| Десинк без ByeDPI | фрагментация приветствия sing-box (`tls_record_fragment`) | не нужен |
| Десинк пакетами | **nfqws2** bol-van v1.0.6 с **теми же пресетами и lua**, что winws2 (`Nfqws2CommandLine`, `NfqueueFirewall`) — только если человек включил | нужен |
| Логика | общие библиотеки `src/` — те же, что у окна Windows | — |
| Интерфейс | Avalonia (`NetZapret.Mobile`), без единого вызова Android | — |

Без root работает всё, кроме nfqws2. Что из пресетов без root недоступно
(seqovl, tcp_ts, autottl, syndata, lua) и чем заменено — `byedpi.md` разбора
и предупреждения ByeDPI в журнале.

## Сборка

Один раз — инструменты (≈10 ГБ, без прав администратора, в
`%USERPROFILE%\.nz-android`):

```powershell
powershell -ExecutionPolicy Bypass -File android\toolchain.ps1
```

Дальше — **одна команда**:

```powershell
powershell -ExecutionPolicy Bypass -File android\build.ps1
```

Она собирает движки в `tools\android`, если их там нет (`engines.ps1`:
libbox из форка, ByeDPI с нашей правкой, nfqws2 из выпуска bol-van со
сверкой sha256), и APK. Пресеты, списки, lua, каталог и образцы пакетов
берутся прямо из `config\`, `presets\` и `vendor\zapret-gui\` — без копий:
**правка Windows-версии доходит до телефона этой же командой**. Пока ветка
не слита, сперва `git merge master` в `android`.

Итог — `android\NetZapret.Android\bin\Release\net10.0-android\android-arm64\io.github.rixypow.netzapret-Signed.apk`
(≈63 МБ, только arm64).

## Установка на телефон

USB-отладка на телефоне, кабель, затем:

```powershell
& "$env:USERPROFILE\.nz-android\android-sdk\platform-tools\adb.exe" install -r android\NetZapret.Android\bin\Release\net10.0-android\android-arm64\io.github.rixypow.netzapret-Signed.apk
```

Или скопировать APK на телефон и открыть (разрешить установку из этого
источника). **Подпись — отладочный ключ .NET** этой машины: обновить поверх
можно только сборкой с этой же машины. Для выпусков нужен свой ключ
подписи — он заводится один раз и навсегда.

## Что проверить на телефоне первым

1. Запуск: мастер, разрешение VPN, уведомление «Работает».
2. «Гибрид» с подпиской: сайты через VPN открываются, российские — напрямую.
3. Десинк без root (ByeDPI по пресету): YouTube, Discord, сайты из реестра;
   журнал в «Ещё → Журнал» — ByeDPI поднялся, предупреждения перевода.
4. «Проверка блокировок» на телефоне.
5. Выключить и включить Wi-Fi, сменить сеть на мобильную — VPN держится.
6. Плитка в шторке, always-on VPN в настройках Android.

## Окно просмотра на Windows

Тот же интерфейс в окне 412×915 с поддельным хозяином — смотреть разделы
без телефона:

```powershell
cd android
& "$env:USERPROFILE\.nz-android\dotnet\dotnet.exe" run --project NetZapret.Mobile.Preview
```

`--shots <папка>` рисует все разделы в PNG.

## Лицензии

libbox (sing-box) — GPL-3.0-or-later: он внутри приложения, поэтому APK
распространяется на условиях GPL-3.0 (исходники открыты и так). Наш код
остаётся MIT. ByeDPI — MIT, zapret2 — MIT, Avalonia — MIT.
