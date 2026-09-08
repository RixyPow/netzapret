@echo off
rem Builds the window and puts it next to the console build, then starts it.
rem
rem Next to it on purpose: the window asks netzapret.exe to start and stop the
rem supervisor, and looks for it in its own folder. Run from gui\bin it would
rem build fine and then refuse to start anything, for no visible reason.
rem
rem ASCII only on purpose: cmd.exe reads batch files in the OEM code page,
rem and UTF-8 Cyrillic here breaks apart into bogus commands.
setlocal

set "ROOT=%~dp0.."
set "TARGET=%ROOT%\build"
set "SOURCE=%~dp0NetZapret.Gui\bin\Debug\net8.0-windows"

if not exist "%TARGET%\netzapret.exe" (
    echo The console build is missing: %TARGET%\netzapret.exe
    echo Run build.cmd in the repository root first - the window drives it.
    exit /b 1
)

echo Building the window...
"C:\Program Files\dotnet\dotnet.exe" build "%~dp0NetZapret.Gui.sln" -v quiet --nologo
if %errorlevel% neq 0 (
    echo Build failed.
    exit /b 1
)

rem Only the window's own files. The libraries are already there from the
rem console build, and copying them over a running program fails anyway.
echo Deploying to %TARGET%
copy /y "%SOURCE%\NetZapret.exe" "%TARGET%\NetZapret.Gui.exe" >nul
if %errorlevel% neq 0 (
    echo Could not copy - the window is probably still open. Close it.
    exit /b 1
)

copy /y "%SOURCE%\NetZapret.dll" "%TARGET%\NetZapret.Gui.dll" >nul 2>&1
copy /y "%SOURCE%\NetZapret.runtimeconfig.json" "%TARGET%\NetZapret.Gui.runtimeconfig.json" >nul 2>&1
copy /y "%SOURCE%\NetZapret.deps.json" "%TARGET%\NetZapret.Gui.deps.json" >nul 2>&1

echo Starting - Windows will ask for administrator rights.
start "" "%TARGET%\NetZapret.Gui.exe"

exit /b 0
