@echo off
rem Double-click entry point: opens the interactive menu.
rem
rem ASCII only on purpose: cmd.exe reads batch files in the OEM code page,
rem and UTF-8 Cyrillic here breaks apart into bogus commands.
rem
rem Requests elevation up front: raising the TUN adapter and loading the
rem WinDivert driver both need administrator rights, and asking once here
rem beats failing halfway through with a confusing message.

net session >nul 2>&1
if %errorlevel% neq 0 (
    echo Requesting administrator rights...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs" >nul 2>&1
    exit /b
)

cd /d "%~dp0"

set "NETZAPRET_EXE=%~dp0src\NetZapret.Cli\bin\Debug\net8.0-windows\netzapret.exe"

if not exist "%NETZAPRET_EXE%" (
    echo Build not found: %NETZAPRET_EXE%
    echo Run: dotnet build "%~dp0NetZapret.sln"
    pause
    exit /b 1
)

rem UTF-8 in the console, otherwise Cyrillic in the menu turns into garbage.
chcp 65001 >nul

"%NETZAPRET_EXE%" menu
