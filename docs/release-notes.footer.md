

## Чем это собрано и как сверить

```
NetZapret.zip   {ZIP}
netzapret.exe   {EXE}
```

Пакет SDK .NET {SDK}, сборка Release, win-x64, self-contained, один файл.

Проверить скачанное:

```
Get-FileHash NetZapret.zip -Algorithm SHA256
```

Собрать самому и сравнить:

```
git checkout v{VERSION}
pack.cmd
```

Совпасть должен `netzapret.exe`. Архив — нет: в нём лежат winws2 и sing-box,
собранные не нами, и их версии у вас могут отличаться.
