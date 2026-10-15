

[![Поддержать](https://img.shields.io/badge/%D0%9F%D0%BE%D0%B4%D0%B4%D0%B5%D1%80%D0%B6%D0%B0%D1%82%D1%8C-Tribute-26A5E4?style=for-the-badge&logo=telegram&logoColor=white)](https://t.me/tribute/app?startapp=g6vq)

## Чем это собрано и как сверить

```
NetZapret-{VERSION}.zip   {ZIP}
NetZapret.exe   {EXE}
```

Пакет SDK .NET {SDK}, сборка Release, win-x64, self-contained, один файл.

Проверить скачанное:

```
Get-FileHash NetZapret-{VERSION}.zip -Algorithm SHA256
```

Собрать самому и сравнить:

```
git checkout v{VERSION}
pack.cmd
```

Совпасть должен `NetZapret.exe`. Архив — нет: в нём лежат winws2 и sing-box,
собранные не нами, и их версии у вас могут отличаться.
