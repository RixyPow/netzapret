# Копия файлов Zapret GUI

Отсюда `build.cmd` и `pack.cmd` берут часть движка десинка — то, что делает
не наш winws2, а Zapret GUI ([loop-uh](https://git.zapret.moe/zapretdiscordyoutube/zapretgui)):

| Папка | Что это |
|---|---|
| `exe/` | `WinDivert.dll` и драйвер `Monkey64.sys` (WinDivert 2.2, переименованный) |
| `lists/` | списки доменов и адресов, на которые ссылаются пресеты |
| `lua/` | модули lua и файлы стратегий (`--lua-init`, `--lua-desync`) |
| `bin/` | образцы пакетов (`--blob`) |
| `windivert.filter/` | шаблоны фильтра перехвата (`--wf-raw-part`) |
| `system/` | каталог сервисов `hosts_catalog.sqlite3` |

Откуда и когда снята — в [`VERSION.txt`](VERSION.txt).

## Как обновлять

**Только по слову владельца.** До 22.10.2026 эти файлы копировались прямо
из установленного Zapret GUI при каждой сборке, и обновление Zapret GUI
молча меняло то, что уезжало в архив. Теперь так:

1. поставить новый Zapret GUI;
2. `nz zapret-gui` из рабочей копии — что изменилось бы: новые, убранные
   и изменённые файлы, у текстовых — сколько строк добавлено и убрано;
3. `nz zapret-gui take` — снять; дальше `git diff vendor/zapret-gui`,
   сборка и проверка вживую, коммит с версией Zapret GUI в сообщении.

Руками здесь ничего не правится: следующее снятие перепишет. Свои списки
и образцы — в `config/lists` и `config/blobs`, они едут с приставкой `nz-`.

## Чего здесь нет и почему

Что берётся, решает `ZapretGuiCopy` (`src/NetZapret.Zapret/ZapretGuiCopy.cs`).
Не берутся `winws2.exe` (свой, из `tools\`), `winws.exe` (прежний движок),
`stop.bat` (гасит движки за спиной надзора), `dnscrypt-proxy` (DNS-клиент
самого Zapret GUI), второй экземпляр драйвера `aaaaaaaaa1` — и `lists/user/`:
это свои списки человека в Zapret GUI, автосписок из его браузера.

Файлы лежат побайтно, как в установке (`-text` в `.gitattributes`).
Лицензии — в [`docs/THIRD-PARTY.md`](../../docs/THIRD-PARTY.md).
