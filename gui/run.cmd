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

rem Copied as they are, never renamed. The apphost looks for its library by the
rem name baked in at build time, and the runtime config must be named after it
rem too. Renaming on copy made NetZapret.Gui.exe load the console's
rem netzapret.dll - Windows does not distinguish case - and run it with no
rem arguments. From the outside: asked for rights, then closed in silence.
rem
rem The whole folder, because the window needs its XAML resources and the same
rem libraries. They are built from the same sources as the console's, so
rem overwriting them changes nothing.
echo Deploying to %TARGET%
robocopy "%SOURCE%" "%TARGET%" /E /R:2 /W:1 /NJH /NJS /NP /NDL /NFL >nul
if errorlevel 8 (
    echo Could not copy - the window is probably still open. Close it.
    exit /b 1
)

echo Starting - Windows will ask for administrator rights.
start "" "%TARGET%\NetZapret.Gui.exe"


exit /b 0
