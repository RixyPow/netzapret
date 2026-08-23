@echo off
rem Short wrapper for command-line use: "nz doctor", "nz probe --sub ...".
rem The double-click entry point is NetZapret.cmd, which opens the menu.
rem
rem ASCII only on purpose: cmd.exe reads batch files in the OEM code page,
rem and UTF-8 Cyrillic here breaks apart into bogus commands.
setlocal
set "NETZAPRET_EXE=%~dp0src\NetZapret.Cli\bin\Debug\net8.0-windows\netzapret.exe"

if not exist "%NETZAPRET_EXE%" (
    echo Build not found: %NETZAPRET_EXE%
    echo Run: dotnet build "%~dp0NetZapret.sln"
    exit /b 1
)

"%NETZAPRET_EXE%" %*
