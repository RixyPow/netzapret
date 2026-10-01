# Что входит в состав

NetZapret не работает сам по себе: он управляет двумя чужими движками. В готовый
архив они входят целиком, чтобы после распаковки ничего не приходилось доставлять.
Ниже — что именно, откуда и на каких условиях.

Ниже не юридическая консультация. Это сведения, собранные из файлов лицензий
и метаданных самих бинарников, чтобы было с чего начинать разбор.

| Что | Лицензия | Текст в комплекте |
| --- | --- | --- |
| sing-box | GPL v3 или новее | `engines/sing-box/LICENSE` |
| wintun.dll | проприетарная, © WireGuard LLC | см. wintun.net |
| Zapret 2 (winws2 и его библиотека lua) | MIT, © 2016–2024 bol-van | `engines/zapret/LICENSE.txt` |
| Zapret GUI (сценарии lua, списки, пресеты, каталог адресов) | MIT, © 2025–2026 censorliber; автор — loop-uh | `engines/zapret/LICENSE-ZapretGUI.txt` |
| zapret-discord-youtube (game filter, список ipset-all) | MIT, © 2024–2026 Flowseal, © 2016–2026 bol-van | `engines/zapret/LICENSE-Flowseal.txt` |
| cygwin1.dll | LGPL v3 | см. cygwin.com |
| WinDivert | LGPL v3 либо GPL v3 | см. reqrypt.org |
| ZXing.Net (чтение QR-кодов с ключами, с 01.10) | Apache-2.0, Michael Jahn | `LICENSE-ZXing.Net.txt` |

## Как это соотносится с лицензией самого NetZapret

NetZapret **запускает движки отдельными процессами** и общается с ними через
командную строку и HTTP-интерфейс Clash API. Он не линкуется с их кодом и не
включает его в себя.

GPLv3 прямо оговаривает такой случай: объединение независимых программ на одном
носителе — «простая агрегация», и она не распространяет условия GPL на остальное
содержимое носителя. Поэтому присутствие GPL-движков в архиве само по себе
не обязывает лицензировать NetZapret под GPL.

Из этого следуют три обязанности на каждый GPL/LGPL-файл в составе:

1. приложить текст лицензии;
2. обеспечить доступ к исходному коду — для неизменённых бинарников, взятых
   с публичного сервера, GPLv3 §6 разрешает сослаться на тот же сервер;
3. не убирать уведомления об авторстве и не изменять бинарники молча.

Ничего из этого мы не нарушаем: движки берутся как есть и не правятся.

## sing-box

`engines/sing-box/sing-box.exe` — **изменённая сборка**, называющая себя
`1.14.1-extended-2.7.2`. Берётся готовым двоичным файлом из выпусков
[github.com/shtorm-7/sing-box-extended](https://github.com/shtorm-7/sing-box-extended).
Она, в свою очередь, основана на
[github.com/SagerNet/sing-box](https://github.com/SagerNet/sing-box)
© 2022 nekohasekai.

Отличается от исходной поддержкой транспорта `xhttp` и рядом протоколов,
которых нет в основной ветке: WireGuard, MASQUE, MTProto, Tailscale. Мы её
не собираем и не правим.

**GNU GPL v3 или новее.** Текст — в `engines/sing-box/LICENSE`, он едет вместе
с движком. Лицензия требует назвать источник именно изменённой сборки, а не
только исходный проект: исходный код этой сборки лежит в репозитории форка,
ссылка на который приведена выше.

## wintun.dll

`engines/sing-box/wintun.dll`, версия 0.14.1, © 2018–2021 WireGuard LLC —
драйвер туннельного адаптера, [wintun.net](https://www.wintun.net/).
Поставляется в составе релиза sing-box. Без него туннель не поднимается.

**Лицензия проприетарная, не GPL** — «Prebuilt Binaries License». Существенны
два пункта:

* распространять его отдельно нельзя, но разрешено «в составе другого
  программного обеспечения, использующего Software только через Permitted API».
  sing-box обращается к нему именно так, поэтому наш архив под это исключение
  подпадает;
* нельзя использовать названия WireGuard, WireGuard LLC и Wintun для
  продвижения продукта.

Запрета на коммерческое использование в лицензии нет. Изменять библиотеку
и извлекать из неё что-либо запрещено — мы этого и не делаем.

## Cygwin

`engines/zapret/exe/cygwin1.dll`, версия 3.4.10, © Cygwin Authors 1996–2023,
Red Hat.

**GNU LGPL v3.** Cygwin перелицензирован с GPL на LGPL в 2016 году, начиная
с версии 2.5.2. LGPL дополнительно требует, чтобы пользователь мог подменить
библиотеку своей сборкой; динамическая загрузка DLL это обеспечивает сама.

## WinDivert

`engines/zapret/exe/WinDivert.dll` и драйвер `engines/zapret/exe/Monkey64.sys` —
[reqrypt.org/windivert.html](https://reqrypt.org/windivert.html), исходники —
[github.com/basil00/WinDivert](https://github.com/basil00/WinDivert).

**LGPL v3 или GPL v3 на выбор**, есть и коммерческая лицензия. Метаданных
в файлах нет; версию следует уточнять по установке Zapret.

**О подписи драйвера.** `Monkey64.sys` — переименованный драйвер WinDivert,
подписанный **成都密思听科技有限公司**, сторонней организацией, не имеющей
отношения к проекту WinDivert. Переименование и чужая подпись — обычная
практика в этой области: Windows не загружает неподписанные драйверы ядра,
а подпись Microsoft получить трудно. Подпись действительна, система драйвер
принимает.

Знать об этом стоит потому, что распространяя сборку, вы ручаетесь за этот
файл. Если такое положение не устраивает — не включайте Zapret в архив
и оставьте его отдельной установкой.

Именно этот драйвер вызывает срабатывания антивирусов: на VirusTotal архив
даёт 3 из 66, и все три указывают на WinDivert. Kaspersky помечает его
`Not-a-virus:HEUR:RiskTool`, то есть прямо говорит, что это не вредонос.

## Zapret

`engines/zapret/exe/winws2.exe` — версия 1.0.3 — и шесть модулей его библиотеки
в `engines/zapret/lua/`: `zapret-lib.lua`, `zapret-antidpi.lua`,
`zapret-auto.lua`, `zapret-obfs.lua`, `zapret-pcap.lua`, `zapret-tests.lua`.
Оттуда же часть образцов пакетов в `bin/` и частей фильтра в `windivert.filter/`.

Всё остальное в `engines/zapret/` — не его, а Zapret GUI, о нём следующий
раздел. До 30.09 этот документ приписывал bol-van папку целиком, и это было
неверно.

**MIT License, Copyright (c) 2016–2024 bol-van.** «Zapret 2»
([github.com/bol-van/zapret2](https://github.com/bol-van/zapret2)) — форк проекта
[bol-van/zapret](https://github.com/bol-van/zapret), и условия наследуются
от него. Текст лежит в `engines/zapret/LICENSE.txt`.

MIT разрешает распространение в любом виде, включая состав архива, и требует
единственного: чтобы уведомление об авторстве и текст лицензии сопровождали
копии. Именно это мы и делаем.

Оговорка о происхождении текста: в установке Zapret файла лицензии нет ни
одного, у `winws2.exe` пусты поля версии, автора и копирайта. Текст взят
из первоисточника — [docs/LICENSE.txt](https://github.com/bol-van/zapret/blob/master/docs/LICENSE.txt)
в репозитории bol-van. Там он лежит не в корне, поэтому GitHub его
не распознаёт и показывает репозиторий как «без лицензии»; это особенность
размещения файла, а не отсутствие условий.

## Zapret GUI

Программа **Zapret GUI**, автор — **loop-uh**:
[wiki.zapret.moe](https://wiki.zapret.moe/), исходники —
[git.zapret.moe/zapretdiscordyoutube/zapretgui](https://git.zapret.moe/zapretdiscordyoutube/zapretgui).
Папка `engines/zapret/` в архиве — это копия её установки, и на ней NetZapret
стоит не меньше, чем на самом winws2. Из Zapret GUI взято:

* **сценарии десинка на lua** — пятнадцать модулей в `engines/zapret/lua/`:
  `combined-detector.lua`, `custom_diag.lua`, `custom_funcs.lua`,
  `domain-grouping.lua`, `fakemultidisorder.lua`, `fakemultisplit.lua`,
  `init_vars.lua`, `silent-drop-detector.lua`, `strategies.lua`,
  `strategy-lock-manager.lua`, `strategy-stats.lua`, `zapret-16kb.lua`,
  `zapret-multishake.lua`, `zapret-rst-flood.lua`, `zapret-wgobfs.lua` —
  и шесть файлов со стратегиями рядом с ними (`circular-config.txt`,
  `strategies-*-source.txt`). Наш пресет Universal V10 грузит пять из этих
  модулей: на одном winws2 от bol-van он бы не поднялся;
* **списки доменов и адресов** в `engines/zapret/lists/` — мы их не изменяем
  и не дополняем, только читаем. Один из них, `ipset-all.txt`, в установку
  пришёл от Flowseal — о нём следующий раздел;
* **образцы пакетов** в `engines/zapret/bin/` — те, которых нет у bol-van;
* **пресеты** в `presets/` — формат и сами наборы секций. Сверка с установкой
  30.09: четыре пресета совпадают с её пресетами дословно, семь отличаются
  на одну — двенадцать строк, а Universal V7–V10 в установке нет — они
  собраны уже здесь, но из тех же секций и в том же формате;
* **каталог адресов**. `config/catalog.zapret.yaml` — выборка из каталога
  установки (`system/hosts_catalog.sqlite3`): те записи «имя — адрес», что
  ответили при проверке в день сборки. Сам каталог `pack.cmd` тоже кладёт
  в архив, если он есть на машине сборщика. Это адреса чужих публичных
  DNS-сервисов (XBOX DNS, Comss DNS и других), то есть ответы, которые
  те раздают всем желающим; собрал их в каталог автор Zapret GUI;
* **сборка драйвера** — `Monkey64.sys`, о котором сказано в разделе WinDivert,
  едет в том виде, в каком лежит в установке Zapret GUI.

**MIT License, Copyright (c) 2025–2026 censorliber.** Так правообладатель назван
в самом файле лицензии —
[docs/LICENSE](https://git.zapret.moe/zapretdiscordyoutube/zapretgui/src/branch/main/docs/LICENSE)
в репозитории проекта; коммиты в репозитории подписаны loop-uh. Строка
приведена дословно, как того требует MIT, а текст целиком лежит
в `engines/zapret/LICENSE-ZapretGUI.txt` (в репозитории —
[docs/licenses/zapretgui-MIT.txt](licenses/zapretgui-MIT.txt)). В нём же сам
Zapret GUI называет, что взял у bol-van.

Оговорка о точности. Какие именно образцы пакетов и части фильтра WinDivert
в установке принадлежат bol-van, а какие добавлены Zapret GUI, пофайлово
не разобрано: сверены только модули lua (30.09, по каталогу `lua/`
репозитория zapret2). Лицензия у обоих одна — MIT, и оба уведомления едут
рядом с файлами.

### Zapret KVN

VPN-клиент той же команды —
[git.zapret.moe/zapretkvn/zapret-kvn](https://git.zapret.moe/zapretkvn/zapret-kvn),
GPL v3. Кода из него в NetZapret нет. Взят только **список из двенадцати
имён** для выключателя «Прятать VPN от российских приложений»
(`src/NetZapret.Core/Rules/VpnHiding.cs`): сервисы «узнать свой адрес»
и адреса, к которым ходят приложения Яндекса, VK, MAX и 2ГИС, — из правила
блокировки в шаблоне его конфига sing-box (`default.json`, сверка 01.10
по версии 0.8.4). Это перечень доменных имён, а не код; назван здесь,
чтобы не выдавать чужую находку за свою. Распоряжаемся мы им иначе:
KVN эти имена блокирует, мы пускаем напрямую.

## zapret-discord-youtube

Сборка **zapret-discord-youtube**, автор — **Flowseal**:
[github.com/Flowseal/zapret-discord-youtube](https://github.com/Flowseal/zapret-discord-youtube).
Отсюда взято три вещи:

* **game filter** — выключатель на вкладке «Десинк». У Flowseal это
  выключатель в `service.bat`: он открывает двум последним секциям каждого
  `.bat` порты 1024–65535. У нас те же две секции дописываются к любому
  пресету (`GameFilter` в коде). Отбор по `ipset-all` и `ipset-exclude`,
  обрезка на четвёртом пакете, рецепты — перевод секций его `general (ALT11).bat`
  на язык Zapret 2;
* **список адресов** `engines/zapret/lists/ipset-all.txt` — облака, хостинги
  и Cloudflare, по которым game filter отбирает трафик. Сверка 30.09: все
  33 048 записей нашего файла совпадают с `lists/ipset-all.txt.backup`
  в репозитории Flowseal. К нам он попадает из установки Zapret GUI;
* **три образца пакетов**, которыми эти секции пользуются:
  `bin/tls_clienthello_max_ru.bin`, `bin/stun2.bin` и `bin/ACTIVE_GAME_UDP.bin`.
  Сверка 30.09: побайтно те же файлы, что в `bin/` его репозитория. Тоже
  едут из установки Zapret GUI.

**MIT License, Copyright (c) 2016–2026 bol-van, Copyright (c) 2024–2026
Flowseal** — обе строки стоят в его
[LICENSE.txt](https://github.com/Flowseal/zapret-discord-youtube/blob/main/LICENSE.txt),
и приводятся как есть. Текст целиком — в `engines/zapret/LICENSE-Flowseal.txt`
(в репозитории — [docs/licenses/flowseal-MIT.txt](licenses/flowseal-MIT.txt)).

Исполняемых файлов из этой сборки в архиве нет: winws2 и WinDivert у нас свои,
описанные выше. Остальные образцы пакетов в `bin/` с его репозиторием
не сверялись.

## Сам NetZapret

Всё, что вне `engines/`, написано в рамках этого проекта и распространяется
под **MIT License** — текст в [LICENSE](../LICENSE) в корне репозитория.

Совместимость с составом сомнений не вызывает: MIT ничему не противоречит,
а движки остаются на своих условиях и запускаются отдельными процессами.
