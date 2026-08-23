@echo off
rem Wrapper so that "netzapret ..." works from the project root.
rem
rem ASCII only on purpose: cmd.exe reads batch files in the OEM code page,
rem and UTF-8 Cyrillic here breaks apart into bogus commands.
rem
rem The executable is resolved relative to this file, so the wrapper can also
rem be called by full path from any directory.
setlocal
set "NETZAPRET_EXE=%~dp0src\NetZapret.Cli\bin\Debug\net8.0-windows\netzapret.exe"

if not exist "%NETZAPRET_EXE%" (
    echo Build not found: %NETZAPRET_EXE%
    echo Run: dotnet build "%~dp0NetZapret.sln"
    exit /b 1
)

"%NETZAPRET_EXE%" %*
