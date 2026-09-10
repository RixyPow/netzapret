@echo off
rem Starts the window from build\.
rem
rem Building and deploying moved to build.cmd in the repository root: it makes
rem both parts and brings the engines along. Two scripts that both built meant
rem remembering which one to run after which change, and the answer was usually
rem "both" - so one of them was always stale.
rem
rem ASCII only on purpose: cmd.exe reads batch files in the OEM code page,
rem and UTF-8 Cyrillic here breaks apart into bogus commands.
setlocal

set "ROOT=%~dp0.."
set "TARGET=%ROOT%\build"

if not exist "%TARGET%\NetZapret.exe" (
    echo Not built yet: %TARGET%\NetZapret.exe
    echo Run build.cmd in the repository root first - it builds both parts
    echo and bundles the engines.
    exit /b 1
)

rem The engines have to be there too. Missing them is the one thing the window
rem cannot work around, because starting them is its job.
if not exist "%TARGET%\engines" (
    echo Engines are missing: %TARGET%\engines
    echo Run build.cmd in the repository root - it bundles them.
    exit /b 1
)

echo Starting - Windows will ask for administrator rights.
start "" "%TARGET%\NetZapret.exe"

exit /b 0
