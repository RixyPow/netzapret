@echo off
rem Short wrapper for command-line use: "nz doctor", "nz probe --sub ...".
rem The double-click entry point is NetZapret.cmd, which opens the menu.
rem
rem Runs from build\, not from the project's bin\: see build.cmd for why.
rem
rem ASCII only on purpose: cmd.exe reads batch files in the OEM code page,
rem and UTF-8 Cyrillic here breaks apart into bogus commands.
setlocal
set "NETZAPRET_EXE=%~dp0build\netzapret.exe"

if not exist "%NETZAPRET_EXE%" (
    echo Not deployed yet: %NETZAPRET_EXE%
    echo Run: "%~dp0build.cmd"
    exit /b 1
)

"%NETZAPRET_EXE%" %*
