@echo off
rem Builds a self-contained distribution in dist\ and zips it.
rem
rem Self-contained on purpose: the .NET runtime travels inside, so whoever
rem downloads this needs nothing installed. Requiring an SDK - a 700 MB
rem developer tool - to run a traffic dispatcher was never reasonable.
rem
rem Not trimmed. Trimming would shave tens of megabytes, but the config
rem generator serialises through a reflection-based resolver, and trimming
rem removes exactly what reflection looks for. That failure appears at run
rem time on someone else's machine, which is the worst place for it.
rem
rem ASCII only on purpose: cmd.exe reads batch files in the OEM code page,
rem and UTF-8 Cyrillic here breaks apart into bogus commands.
setlocal enabledelayedexpansion

set "ROOT=%~dp0"
set "DIST=%ROOT%dist"
set "STAGE=%DIST%\NetZapret"

rem dist\ gets wiped below, and a copy running from there holds its own
rem executable. Never killing it: the thing running is quite possibly the live
rem setup carrying all of this machine's traffic, and taking it down without
rem asking to build an archive is not a trade anyone agreed to.
rem
rem The test is whether dist\ can actually be removed, not whether a process
rem named netzapret.exe exists anywhere. Those are different questions, and
rem asking the wrong one blocked a release while the running copy lived
rem somewhere else entirely - with dist\ empty. Windows will not let go of a
rem running program's own file, so a wipe that succeeds proves nothing is
rem running from here, and one that fails says exactly what is wrong.
echo Cleaning %DIST%
if exist "%DIST%" rd /s /q "%DIST%" 2>nul

if exist "%DIST%" (
    echo Could not clean %DIST% - something is holding files there, most
    echo likely a copy of NetZapret running from dist\NetZapret. Stop it
    echo from its menu, then run this again.
    exit /b 1
)

mkdir "%STAGE%" 2>nul

rem Single file so the folder stays readable. A plain self-contained publish
rem scatters 217 runtime assemblies next to the program, and whoever opens the
rem folder has to work out which of them to launch. One executable, a config
rem directory and the engines is a folder that explains itself.
echo Publishing self-contained...
"C:\Program Files\dotnet\dotnet.exe" publish "%ROOT%src\NetZapret.Cli\NetZapret.Cli.csproj" ^
    -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true -o "%STAGE%" --nologo -v quiet
if %errorlevel% neq 0 (
    echo Publish failed.
    exit /b 1
)

rem Debug symbols are of no use to anyone downloading this, and they clutter
rem a folder whose whole point is being obvious.
del /q "%STAGE%\*.pdb" >nul 2>&1

rem The distribution ships the base ruleset and the settings template, not a
rem settings file: that one holds the subscription link, which is a password.
echo Copying configuration
mkdir "%STAGE%\config" 2>nul
copy /y "%ROOT%config\rules.yaml" "%STAGE%\config\" >nul
copy /y "%ROOT%config\netzapret.example.json" "%STAGE%\config\" >nul

rem Addresses for names that have none of their own - the Roblox thumbnail
rem host among them. Without this the roblox-cdn list points at a name with no
rem A record and the previews stay broken, so the fix has to travel with the
rem list that depends on it.
copy /y "%ROOT%config\addresses.yaml" "%STAGE%\config\" >nul

rem Our own domain lists. Rules reference them by path, so leaving them out
rem gives rules that resolve to nothing - and the service view would show
rem Steam and Valheim as routed while nothing was routed at all.
mkdir "%STAGE%\config\lists" 2>nul
copy /y "%ROOT%config\lists\*.txt" "%STAGE%\config\lists\" >nul
copy /y "%ROOT%config\lists\README.md" "%STAGE%\config\lists\" >nul

rem A separate README for the archive. The repository one is written for
rem someone with the sources - it explains building and the module layout,
rem and tells the reader to run build.cmd, which does not exist here.
copy /y "%ROOT%docs\README.dist.md" "%STAGE%\README.md" >nul

rem Removal script. Kept out of the repository root deliberately: it stops
rem engines and strips a Defender exclusion for whatever folder it sits in,
rem and run from a working copy by mistake it would do all of that there.
copy /y "%ROOT%dist-template\uninstall.cmd" "%STAGE%\" >nul
copy /y "%ROOT%LICENSE" "%STAGE%\" >nul

rem No launcher script: the program opens the menu and asks for elevation
rem itself when started with no arguments. Two files side by side, of which
rem the correct one to double-click was the less obvious, is a choice nobody
rem should have to make.

rem Engines. Same selection as build.cmd, and for the same reasons - see
rem tools\README.md. Without them the archive is not self-contained at all,
rem which is the whole point of this script.
set "ENGINES=%STAGE%\engines"

set "SINGBOX_DIR="
for /f "delims=" %%F in ('dir /s /b "%ROOT%tools\sing-box.exe" 2^>nul') do set "SINGBOX_DIR=%%~dpF"

if not defined SINGBOX_DIR (
    echo.
    echo sing-box not found in tools\ - the archive would ship without VPN.
    echo Download it from https://github.com/SagerNet/sing-box/releases
    exit /b 1
)

echo Bundling sing-box
robocopy "%SINGBOX_DIR%." "%ENGINES%\sing-box" /E /R:2 /W:1 /NJH /NJS /NP /NDL /NFL >nul
if errorlevel 8 exit /b 1

set "ZAPRET="
if exist "C:\Zapret\Dev\presets\winws2" set "ZAPRET=C:\Zapret\Dev"
if not defined ZAPRET if exist "C:\Zapret\presets\winws2" set "ZAPRET=C:\Zapret"

if not defined ZAPRET (
    echo.
    echo Zapret not found - the archive would ship without desync.
    exit /b 1
)

echo Bundling Zapret from %ZAPRET%

rem Three files from exe\ are left out.
rem
rem   winws.exe    the previous engine. Our presets drive it through
rem                --lua-desync, which that binary has no notion of, so it
rem                cannot run any of them.
rem   aaaaaaaaa1   byte-for-byte identical to Monkey64.sys, and nothing refers
rem                to it: WinDivert.dll names Monkey64.sys and only that.
rem   stop.bat     belongs to the Zapret GUI. Running it kills the engines
rem                behind the supervisor's back, which then restarts them -
rem                a fight nobody wins.
for %%D in (exe lists lua bin windivert.filter) do (
    if exist "%ZAPRET%\%%D" (
        robocopy "%ZAPRET%\%%D" "%ENGINES%\zapret\%%D" /E /R:2 /W:1 /NJH /NJS /NP /NDL /NFL /XF winws.exe aaaaaaaaa1 stop.bat >nul
        if errorlevel 8 exit /b 1
    )
)

set PRESET_EXCLUDE="Universal.txt" "Universal V2.txt" "Universal V2.1.txt" "Universal V2.1 voice ALT.txt"
set PRESET_EXCLUDE=%PRESET_EXCLUDE% "Universal V3.txt" "Universal V3 AUTO.txt" "Universal V4.txt"
set PRESET_EXCLUDE=%PRESET_EXCLUDE% "Universal FULL.txt" "Universal LITE.txt" "Universal V5 beta.txt" "Preset.X.txt"

robocopy "%ZAPRET%\presets\winws2" "%ENGINES%\zapret\presets\winws2" /E /R:2 /W:1 /NJH /NJS /NP /NDL /NFL /XF %PRESET_EXCLUDE% >nul
if errorlevel 8 exit /b 1

rem The game-filter set lives in Zapret's own preset folder, which was not
rem copied at all - so those presets could never appear in a built copy no
rem matter what the program did with them. By name, not the whole folder:
rem it holds over a hundred files, almost all sweeps of one strategy.
for %%P in (
    "Default v1 (game filter).txt"
    "Default v2 (game filter).txt"
    "Default v3 (game filter).txt"
    "Default v4 (game filter).txt"
    "Default v5 (game filter).txt"
) do (
    if exist "%ZAPRET%\presets\winws2_builtin\%%~P" (
        robocopy "%ZAPRET%\presets\winws2_builtin" "%ENGINES%\zapret\presets\winws2_builtin" "%%~P" /R:2 /W:1 /NJH /NJS /NP /NDL /NFL >nul
        if errorlevel 8 exit /b 1
    )
)

rem Licences of what we redistribute, and the one obligation that actually
rem needs an action from us: Zapret is MIT, and MIT requires the notice to
rem travel with the copies. The installation ships no licence file at all,
rem so the text comes from upstream and is placed beside the engine.
copy /y "%ROOT%docs\THIRD-PARTY.md" "%STAGE%\" >nul 2>&1
copy /y "%ROOT%docs\licenses\zapret-MIT.txt" "%ENGINES%\zapret\LICENSE.txt" >nul 2>&1

rem Last line of defence before the archive exists. dist\ is wiped at the start,
rem so these files should never be here - but testing happens in the unpacked
rem folder, and config\netzapret.json holds a subscription link, which is a
rem password. A leaked release cannot be recalled, so this is checked rather
rem than assumed.
for %%F in ("%STAGE%\config\netzapret.json" "%STAGE%\config\rules.user.yaml") do (
    if exist "%%~F" (
        echo.
        echo Refusing to archive: %%~nxF is personal and must not ship.
        exit /b 1
    )
)

if exist "%STAGE%\runtime" (
    echo.
    echo Refusing to archive: runtime\ holds generated configs with server
    echo credentials. Remove it and pack again.
    exit /b 1
)

echo Archiving
powershell -NoProfile -Command "Compress-Archive -Path '%STAGE%' -DestinationPath '%DIST%\NetZapret.zip' -Force"
if %errorlevel% neq 0 (
    echo Archiving failed.
    exit /b 1
)

rem Size read through PowerShell: %%~z on a file created earlier in the same
rem script comes back empty, because cmd expanded the loop variable before
rem Compress-Archive had finished writing.
echo.
powershell -NoProfile -Command "$f = Get-Item '%DIST%\NetZapret.zip'; Write-Host ('Done: {0} ({1:N1} MB)' -f $f.FullName, ($f.Length/1MB))"
exit /b 0
