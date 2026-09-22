@echo off
rem Short wrapper for the troubleshooting tool: "nz status", "nz where twitch.tv".
rem
rem Runs nz from build\, where build.cmd deploys it. nz needs no administrator
rem rights and finds the installation root on its own.
rem
rem ASCII only on purpose: cmd.exe reads batch files in the OEM code page,
rem and UTF-8 Cyrillic here breaks apart into bogus commands.
setlocal
set "NZ_EXE=%~dp0build\nz.exe"

if not exist "%NZ_EXE%" (
    echo Not deployed yet: %NZ_EXE%
    echo Run: "%~dp0build.cmd"
    exit /b 1
)

"%NZ_EXE%" %*
