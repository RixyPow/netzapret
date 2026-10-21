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
rem named NetZapretOld.exe exists anywhere. Those are different questions, and
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
rem DebugType=none for the shipped build. The PE debug directory otherwise
rem names the pdb by its full path, and that path is the build machine's -
rem C:\Users\<name>\Projects\netzapret\... travelled to everyone who
rem downloaded. It also made the file impossible to reproduce elsewhere: a
rem different folder, a different binary, from identical sources.
rem
rem Nothing is lost. The pdb is deleted below anyway, so line numbers in stack
rem traces were never available to anyone downloading this.
rem The window is what ships, and it ships alone. It no longer needs the
rem console beside it: it builds the tunnel config itself and raises the
rem supervisor by restarting itself with --supervisor. Two executables in one
rem folder, of which the correct one to double-click was the less obvious, is
rem a choice nobody should have to make - and shipping both would have doubled
rem the archive, because each carries its own copy of the .NET runtime.
rem
rem What goes with the console: the command line. watch, test and rules have
rem no counterpart in the window yet; they remain developer tools, available
rem from a source build.
echo Publishing self-contained...
"C:\Program Files\dotnet\dotnet.exe" publish "%ROOT%gui\NetZapret.Gui\NetZapret.Gui.csproj" ^
    -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -p:DebugSymbols=false ^
    -o "%STAGE%" --nologo -v quiet
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

rem Our own address catalogue - the names that break by answer rather than by
rem channel, which the Zapret sets do not carry. Hand-edited, so it ships as
rem readable text beside the rules.
copy /y "%ROOT%config\catalog.yaml" "%STAGE%\config\" >nul

rem Snapshot of the Zapret catalogue entries that answered when it was taken
rem (nz catalog). Separate from catalog.yaml because that one is preserved
rem on update and this one must be replaced, or its addresses go stale.
copy /y "%ROOT%config\catalog.zapret.yaml" "%STAGE%\config\" >nul

rem Ready pin solutions - the "Direct" section of the Zapret GUI hosts catalogue
rem (Instagram, X, YouTube, Discord...). Replaced on update like the snapshot.
copy /y "%ROOT%config\pin-solutions.yaml" "%STAGE%\config\" >nul

rem Our domain and address lists - all of them now, not just the handful
rem Zapret has no entry for. Rules reference them by path, so leaving them out
rem gives rules that resolve to nothing: an absent list loads as empty, matches
rem nothing, and the route silently falls through to the default. For the
rem "Russian networks go direct" rule that means the whole domestic traffic
rem in the tunnel, which is why ipset-ru.txt travels here too.
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

rem No launcher script: one executable, and it asks for elevation itself
rem through its manifest.

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

rem The Telegram proxy (tg-ws-proxy-rs) for the "TG Proxy" tab. Required in the
rem archive, like sing-box: the tab ships either way, and without the proxy it
rem would be a switch that starts nothing.
set "TGWS_DIR="
for /f "delims=" %%F in ('dir /s /b "%ROOT%tools\tg-ws-proxy.exe" 2^>nul') do set "TGWS_DIR=%%~dpF"

if not defined TGWS_DIR (
    echo.
    echo tg-ws-proxy not found in tools\ - the archive would ship an empty TG Proxy tab.
    echo Download it from https://github.com/valnesfjord/tg-ws-proxy-rs/releases
    exit /b 1
)

echo Bundling tg-ws-proxy
robocopy "%TGWS_DIR%." "%ENGINES%\tg-ws-proxy" /E /R:2 /W:1 /NJH /NJS /NP /NDL /NFL >nul
if errorlevel 8 exit /b 1

rem The Zapret GUI files come from our own copy in vendor\zapret-gui - the
rem same as build.cmd, see the reasoning there. Taken only on the owner's word
rem with "nz zapret-gui take"; what is left out of it and why (winws.exe,
rem stop.bat, dnscrypt-proxy, the GUI's own user lists) is in ZapretGuiCopy.cs.
set "ZAPRET=%ROOT%vendor\zapret-gui"

if not exist "%ZAPRET%\lists" (
    echo.
    echo vendor\zapret-gui not found - the archive would ship without desync.
    exit /b 1
)

echo Bundling Zapret GUI files from vendor\zapret-gui

for %%D in (exe lists lua bin windivert.filter) do (
    robocopy "%ZAPRET%\%%D" "%ENGINES%\zapret\%%D" /E /R:2 /W:1 /NJH /NJS /NP /NDL /NFL >nul
    if errorlevel 8 exit /b 1
)

rem Our own winws2 from tools\ - same as build.cmd, see the reasoning there
rem (profile search cost, measured 03.10 and 07.10). The copy of the Zapret GUI
rem files carries no winws2, so without one in tools\ the archive would have no
rem desync engine at all. The native build needs no cygwin1.dll; bol-van's own
rem does, and then it travels from the same tools\ folder.
set "WINWS2="
for /f "delims=" %%F in ('dir /s /b "%ROOT%tools\winws2.exe" 2^>nul') do set "WINWS2=%%F"

if not defined WINWS2 (
    echo.
    echo winws2.exe not found under tools\ - the archive would ship without desync.
    exit /b 1
)

echo Bundling winws2 from %WINWS2%
copy /y "%WINWS2%" "%ENGINES%\zapret\exe\winws2.exe" >nul
if errorlevel 1 exit /b 1
for %%W in ("%WINWS2%") do if exist "%%~dpWcygwin1.dll" copy /y "%%~dpWcygwin1.dll" "%ENGINES%\zapret\exe\" >nul
rem Our own lists, delivered where the engine looks for them. Preset sections
rem name hostlists as "lists/<name>" relative to the Zapret root, and winws2
rem resolves them there - not next to our config, so a section pointing at
rem config\lists\proton.txt would load nothing and match nothing.
rem
rem Prefixed with nz- so they cannot collide with Zapret's own files: without
rem it our discord.txt would quietly replace theirs, and the preset would match
rem a different set of names than its author wrote.
for %%F in ("%ROOT%config\lists\*.txt") do (
    copy /y "%%F" "%ENGINES%\zapret\lists\nz-%%~nxF" >nul
)

rem Our own blobs - packet samples a preset names with --blob=. Same rule and
rem the same nz- prefix as the lists: winws2 looks for them in bin\ next to
rem the engine, and a sample of ours must never shadow one from Zapret GUI.
rem First ones (21.10): V3nilla's hellos from Flowseal 1.10.4, PR 17925.
for %%F in ("%ROOT%config\blobs\*.bin") do (
    copy /y "%%F" "%ENGINES%\zapret\bin\nz-%%~nxF" >nul
)

rem Presets are ours and come from presets\ in the repository. Nothing is taken
rem from the Zapret installation: it keeps two folders and a hundred and fifty
rem files, mostly sweeps of one strategy, so which to show had to be decided on
rem the user's behalf - and they changed underneath us on every Zapret update.
rem Lists still come from Zapret above, and presets refer to them relative to
rem the working directory, which stays the installation root when winws2 runs.
robocopy "%ROOT%presets" "%STAGE%\presets" *.txt /R:2 /W:1 /NJH /NJS /NP /NDL /NFL >nul
if errorlevel 8 exit /b 1

rem Themes: only the shipped ones and the format description. Named one
rem by one on purpose - themes\ in a working copy also holds the owner's and
rem artists' own themes (git ignores them), and a folder copy would ship them.
for %%T in (dark light grey tinted violet ocean coffee rose cream) do (
    robocopy "%ROOT%themes\%%T" "%STAGE%\themes\%%T" theme.json /R:2 /W:1 /NJH /NJS /NP /NDL /NFL >nul
    if errorlevel 8 exit /b 1
)
rem Shipped themes with a picture, by the owner's word: sloyka1 - art by an
rem acquaintance of the owner, with her permission (30.09); sloyka2 - the
rem earlier Sloyka, shipped by the owner's word 20.10; blissfield -
rem made for the project (01.10); fish - a looping video background, the
rem owner's file (08.10). Every other picture theme in a working copy is
rem somebody else's art and stays out.
for %%T in (sloyka1 sloyka2 blissfield fish) do (
    robocopy "%ROOT%themes\%%T" "%STAGE%\themes\%%T" theme.json background.jpg background.mp4 /R:2 /W:1 /NJH /NJS /NP /NDL /NFL >nul
    if errorlevel 8 exit /b 1
)
copy /y "%ROOT%themes\README.md" "%STAGE%\themes\" >nul

rem The service catalogue: seven sets of proxy addresses for services that
rem shut Russia out themselves. Without it that whole section is missing for
rem anyone who has no separate Zapret installation - and those are exactly
rem the people who cannot fall back on its own hosts editor either.
robocopy "%ZAPRET%\system" "%ENGINES%\zapret\system" /E /R:2 /W:1 /NJH /NJS /NP /NDL /NFL >nul
if errorlevel 8 exit /b 1

rem Licences of what we redistribute, and the one obligation that actually
rem needs an action from us: Zapret is MIT, and MIT requires the notice to
rem travel with the copies. The installation ships no licence file at all,
rem so the text comes from upstream and is placed beside the engine.
rem
rem Two notices, because the folder holds two works. winws2 and six lua
rem modules are bol-van's Zapret 2 - winws2 since 07.10 in loop-uh's native
rem build, a fork whose only licence file is bol-van's MIT. Everything else
rem in it - the other lua modules, lists, blobs, the address catalogue - comes
rem from Zapret GUI by
rem loop-uh (git.zapret.moe), also MIT. Until 30.09 only the first notice
rem shipped, and the archive credited all of it to bol-van.
rem
rem And a third: lists\ipset-all.txt is Flowseal's list from
rem zapret-discord-youtube, entry for entry, and our game filter is his
rem ALT11 sections carried over to Zapret 2. MIT as well.
rem
rem And WinDivert: exe\WinDivert.dll and its driver Monkey64.sys, LGPLv3 or
rem GPLv2 at our choice, and both want the licence text with the copy. Until
rem 22.10 the archive only pointed at reqrypt.org; the text is upstream's
rem LICENSE file (basil00/WinDivert), which carries LGPLv3, GPLv3 and GPLv2.
copy /y "%ROOT%docs\THIRD-PARTY.md" "%STAGE%\" >nul 2>&1
copy /y "%ROOT%docs\licenses\zapret-MIT.txt" "%ENGINES%\zapret\LICENSE.txt" >nul 2>&1
copy /y "%ROOT%docs\licenses\zapretgui-MIT.txt" "%ENGINES%\zapret\LICENSE-ZapretGUI.txt" >nul 2>&1
copy /y "%ROOT%docs\licenses\flowseal-MIT.txt" "%ENGINES%\zapret\LICENSE-Flowseal.txt" >nul 2>&1
copy /y "%ROOT%docs\licenses\windivert-LICENSE.txt" "%ENGINES%\zapret\LICENSE-WinDivert.txt" >nul 2>&1
rem ZXing.Net reads QR codes with keys (01.10). Apache-2.0: the licence text
rem must travel with the copy; the library sits beside NetZapret.exe.
copy /y "%ROOT%docs\licenses\zxing-net-Apache-2.0.txt" "%STAGE%\LICENSE-ZXing.Net.txt" >nul 2>&1
rem Manrope is built into NetZapret.exe as a resource (20.10). SIL OFL 1.1:
rem the font may be bundled with software as long as the licence text and
rem copyright notice travel with it.
copy /y "%ROOT%docs\licenses\manrope-OFL.txt" "%STAGE%\LICENSE-Manrope.txt" >nul 2>&1

rem Last line of defence before the archive exists. dist\ is wiped at the start,
rem so these files should never be here - but testing happens in the unpacked
rem folder, and config\netzapret.json holds a subscription link, which is a
rem password. A leaked release cannot be recalled, so this is checked rather
rem than assumed.
for %%F in ("%STAGE%\config\netzapret.json" "%STAGE%\config\subscriptions.json" "%STAGE%\config\rules.user.yaml") do (
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

rem Same reasoning, different contents. A block-check report is a snapshot of
rem one person's network: which sites they check, which ISP, what is pinned in
rem their hosts. Shipping one to everybody is a leak, not clutter.
if exist "%STAGE%\reports" (
    echo.
    echo Refusing to archive: reports\ holds block-check output describing
    echo this machine's network. Remove it and pack again.
    exit /b 1
)

rem The Zapret GUI's own user lists - its autohostlist learns from browsing.
rem Every release up to 1.0.0-pre.2 carried the owner's; the copy in
rem vendor\zapret-gui leaves them out, and this keeps it that way.
if exist "%STAGE%\engines\zapret\lists\user" (
    echo.
    echo Refusing to archive: engines\zapret\lists\user holds the Zapret GUI
    echo user lists of this machine. Remove them from vendor\zapret-gui.
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
