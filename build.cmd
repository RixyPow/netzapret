@echo off
rem Builds both parts and deploys them to build\.
rem
rem Both, because the window is the program now and the console is a developer
rem tool beside it. Two scripts for one working copy meant remembering which
rem of them to run after which change, and the answer was usually "both".
rem
rem Why the extra copy: running straight out of a project's bin\ folder makes
rem every rebuild fail while it is running, because MSBuild writes into the very
rem directory holding the loaded assemblies. Running from build\ instead means
rem "dotnet build" always succeeds, and only this script needs it stopped.
rem
rem ASCII only on purpose: cmd.exe reads batch files in the OEM code page,
rem and UTF-8 Cyrillic here breaks apart into bogus commands.
setlocal

set "ROOT=%~dp0"
set "CONSOLE=%ROOT%src\NetZapret.Cli\bin\Debug\net8.0-windows"
set "WINDOW=%ROOT%gui\NetZapret.Gui\bin\Debug\net8.0-windows"
set "TARGET=%ROOT%build"

set "DOTNET=C:\Program Files\dotnet\dotnet.exe"

echo Building the console...
"%DOTNET%" build "%ROOT%NetZapret.sln" -v quiet --nologo
if %errorlevel% neq 0 (
    echo Build failed.
    exit /b 1
)

rem The window lives in its own solution and is not part of NetZapret.sln:
rem it must not be able to break the console build or CI.
echo Building the window...
"%DOTNET%" build "%ROOT%gui\NetZapret.Gui.sln" -v quiet --nologo
if %errorlevel% neq 0 (
    echo Build failed.
    exit /b 1
)

rem ---------------------------------------------------------------------------
rem Stop what is running before copying over it.
rem
rem Through the program's own --stop, not by killing names. The supervisor is
rem the same executable as the window since 0.5.0, so "taskkill /im" would take
rem the window with it - and leave the state file behind, after which the next
rem start refuses, believing an instance is still up.
rem
rem From %ROOT%, not from wherever this was invoked: the state file lives at
rem runtime\supervisor.state.json relative to the working directory. Called from
rem elsewhere, --stop reports "not running" and leaves the engines holding the
rem very files we are about to overwrite.
rem ---------------------------------------------------------------------------
rem Every known name is tried, quietly, and none of them is trusted to tell us
rem whether it worked. Windows does not distinguish case, so "if exist
rem NetZapret.exe" also matches a leftover netzapret.exe from before the rename
rem - and the console, handed --stop, prints its help and returns an error that
rem means nothing here. Whether the stop actually mattered is answered by
rem robocopy below, which is the only honest source.
rem
rem From %ROOT%, not from wherever this was invoked: the state file lives at
rem runtime\supervisor.state.json relative to the working directory. Called from
rem elsewhere, --stop reports "not running" and leaves the engines holding the
rem very files we are about to overwrite.
echo Stopping the engines...

pushd "%ROOT%"

if exist "%TARGET%\NetZapret.exe" "%TARGET%\NetZapret.exe" --stop >nul 2>&1
if exist "%TARGET%\NetZapret.Gui.exe" "%TARGET%\NetZapret.Gui.exe" --stop >nul 2>&1
if exist "%TARGET%\netzapret.exe" "%TARGET%\netzapret.exe" stop >nul 2>&1

popd

rem Give the engines a moment to release WinDivert and the TUN adapter.
rem "ping" rather than "timeout": the latter fails outright when this script
rem runs with redirected input, which is how it runs from other tools.
ping -n 3 127.0.0.1 >nul 2>&1

rem The open window holds the deployed assemblies just as firmly as the
rem supervisor does, and --stop does not close it. Say so plainly, because
rem robocopy's failure alone does not point at the open window.
rem Both names, because a working copy updated across the rename still runs the
rem old one. tasklist filters by exact image name, so NetZapret.Gui.exe is not
rem covered by asking about NetZapret.exe - and the deploy then failed with
rem robocopy's "something is holding the files", which does not point at the
rem open window at all.
tasklist /fi "imagename eq NetZapret.exe" 2>nul | find /i "NetZapret.exe" >nul
if not errorlevel 1 goto :open

tasklist /fi "imagename eq NetZapret.Gui.exe" 2>nul | find /i "NetZapret.Gui.exe" >nul
if not errorlevel 1 goto :open

goto :deploy

:open
echo.
echo NetZapret is still running - most likely the window itself.
echo Close it, then run this again. If nothing is open, an elevated
echo instance is left over and needs an elevated shell to stop.
exit /b 1

:deploy

rem Files from before the rename go first, and it has to be before the copy,
rem not after. After the rename the old console and the new window are the same
rem names to the file system: netzapret.exe and NetZapret.exe differ only in
rem case, and Windows does not distinguish. Cleaning up afterwards deleted the
rem very file just deployed, and build\ came out with no window in it at all.
rem
rem NetZapret.Gui.exe is the unambiguous marker of an old layout, so the whole
rem cleanup runs only when one is actually there - once, and never again.
if exist "%TARGET%\NetZapret.Gui.exe" (
    echo Removing files from before the rename
    del /q "%TARGET%\NetZapret.Gui.*" >nul 2>&1
    del /q "%TARGET%\netzapret.exe" "%TARGET%\netzapret.dll" >nul 2>&1
    del /q "%TARGET%\netzapret.deps.json" "%TARGET%\netzapret.runtimeconfig.json" >nul 2>&1
    del /q "%TARGET%\netzapret.pdb" >nul 2>&1
)

echo Deploying to %TARGET%

rem /R and /W are not optional here. Robocopy defaults to one million retries
rem with a thirty second wait, so a single locked file hangs the script for
rem what is effectively forever. Observed exactly that when a stray instance
rem held the deployed assemblies. Two quick retries, then fail loudly.
robocopy "%CONSOLE%" "%TARGET%" /E /R:2 /W:1 /NJH /NJS /NP /NDL /NFL >nul
if %errorlevel% geq 8 goto :held

rem The window second, and never renamed on the way. The apphost looks for its
rem library by the name baked in at build time, and the runtime config must be
rem named after it too. Both parts share the same libraries, built from the same
rem sources, so overwriting them changes nothing.
robocopy "%WINDOW%" "%TARGET%" /E /R:2 /W:1 /NJH /NJS /NP /NDL /NFL >nul
if %errorlevel% geq 8 goto :held

rem ---------------------------------------------------------------------------
rem Engines, bundled next to the program so build\ runs on its own.
rem
rem Copied at build time rather than committed. Two reasons. The lists are
rem Zapret's data and change with it - a copy in git would be a stale fork of
rem someone else's work within weeks. And they are third-party binaries: keeping
rem them out of the repository keeps the licences out of it too, which matters
rem because cygwin1.dll is GPLv3 and WinDivert is LGPL/GPL.
rem
rem Only what the engine actually reads gets copied. The installation is 219 MB,
rem of which _internal is the GUI's Python runtime and logs\ is its history;
rem neither is any use to us.
rem ---------------------------------------------------------------------------

set "ENGINES=%TARGET%\engines"

rem The whole folder, not just the executable. sing-box ships with wintun.dll,
rem and without it the TUN adapter never comes up - a bundle carrying only the
rem .exe would build cleanly and then fail at runtime for no visible reason.
rem libcronet.dll and LICENSE travel with it for the same kind of reason.
rem
rem Found by search because the release unpacks into a versioned directory
rem (sing-box-1.13.19-windows-amd64), and pinning that name would break on
rem the next upgrade.
set "SINGBOX_DIR="
for /f "delims=" %%F in ('dir /s /b "%ROOT%tools\sing-box.exe" 2^>nul') do set "SINGBOX_DIR=%%~dpF"

if defined SINGBOX_DIR (
    echo Bundling sing-box
    robocopy "%SINGBOX_DIR%." "%ENGINES%\sing-box" /E /R:2 /W:1 /NJH /NJS /NP /NDL /NFL >nul
    if errorlevel 8 (
        echo Failed to bundle sing-box
        exit /b 1
    )
) else (
    echo sing-box not found in tools\ - VPN will be unavailable in this build.
)

rem xray is deliberately left out: it is not wired up yet, and its geoip and
rem geosite databases alone are 27 MB of dead weight.

set "ZAPRET="
if exist "C:\Zapret\Dev\lists" set "ZAPRET=C:\Zapret\Dev"
if not defined ZAPRET if exist "C:\Zapret\lists" set "ZAPRET=C:\Zapret"

if not defined ZAPRET (
    echo.
    echo Zapret not found - desync engine not bundled. Everything else works.
    goto :done
)

echo Bundling Zapret from %ZAPRET%

rem exe    - winws2 itself, WinDivert and the Cygwin runtime it links against
rem lists  - hostlists and ipsets the presets reference
rem lua    - desync recipes loaded by --lua-init and --lua-desync
rem bin    - blobs referenced by --blob=
rem windivert.filter - filter templates
for %%D in (exe lists lua bin windivert.filter) do (
    if exist "%ZAPRET%\%%D" (
        robocopy "%ZAPRET%\%%D" "%ENGINES%\zapret\%%D" /E /R:2 /W:1 /NJH /NJS /NP /NDL /NFL >nul
        if errorlevel 8 (
            echo Failed to bundle %%D
            exit /b 1
        )
    )
)

rem Presets are ours and come from presets\ in the repository. Nothing is taken
rem from the Zapret installation any more: it keeps two folders and a hundred
rem and fifty files, mostly sweeps of one strategy, so which of them to show
rem had to be decided on the user's behalf - and every such choice was
rem arguable. They also changed underneath us whenever Zapret updated.
rem
rem Lists still come from Zapret, above: a preset refers to them relative to
rem the working directory, which stays the installation root when winws2 runs.
if exist "%ROOT%presets" (
    robocopy "%ROOT%presets" "%TARGET%\presets" *.txt /R:2 /W:1 /NJH /NJS /NP /NDL /NFL >nul
    if errorlevel 8 (
        echo Failed to bundle presets
        exit /b 1
    )
)

rem Stale copies from a build made before the move would otherwise be found
rem first and quietly shadow the new folder.
if exist "%ENGINES%\zapret\presets" rd /s /q "%ENGINES%\zapret\presets"

:done
echo.
echo Done. Nothing is running now - start NetZapret.exe from build\.
exit /b 0

:held
echo.
echo Could not update %TARGET% - something is still holding the files.
echo The engines were stopped above, so look for strays:
echo   tasklist ^| findstr /i "netzapret sing-box winws2"
echo Stopping an elevated instance needs an elevated shell.
exit /b 1
