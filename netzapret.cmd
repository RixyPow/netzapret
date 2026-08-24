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

rem Two layouts, one launcher. In a downloaded distribution the program sits
rem right here; in a working copy it lives in build\, because running it from
rem the project's bin\ breaks every rebuild - see build.cmd.
set "NETZAPRET_EXE=%~dp0netzapret.exe"
if not exist "%NETZAPRET_EXE%" set "NETZAPRET_EXE=%~dp0build\netzapret.exe"

if not exist "%NETZAPRET_EXE%" (
    echo netzapret.exe not found next to this file or in build\.
    echo In a working copy, build it first: "%~dp0build.cmd"
    pause
    exit /b 1
)

rem UTF-8 in the console, otherwise Cyrillic in the menu turns into garbage.
chcp 65001 >nul

"%NETZAPRET_EXE%" menu
