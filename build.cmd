@echo off
rem Builds the solution and deploys the result to build\.
rem
rem Why the extra copy: running the program straight out of the project's
rem bin\ folder makes every rebuild fail while it is running, because MSBuild
rem writes into the very directory holding the loaded assemblies. Running
rem from build\ instead means "dotnet build" always succeeds, and only this
rem script needs the program stopped.
rem
rem ASCII only on purpose: cmd.exe reads batch files in the OEM code page,
rem and UTF-8 Cyrillic here breaks apart into bogus commands.
setlocal

set "ROOT=%~dp0"
set "SOURCE=%ROOT%src\NetZapret.Cli\bin\Debug\net8.0-windows"
set "TARGET=%ROOT%build"

echo Building...
"C:\Program Files\dotnet\dotnet.exe" build "%ROOT%NetZapret.sln" -v quiet --nologo
if %errorlevel% neq 0 (
    echo Build failed.
    exit /b 1
)

rem Stop the engines before copying over them. Deploying used to fail against
rem a running instance, and the fix was to remember to stop it by hand - which
rem is exactly the kind of step that gets skipped. It was: a config once got
rem rebuilt by the previous binary because the deploy had quietly failed, and
rem that looked like a broken site rather than a skipped step.
rem
rem From %ROOT%, not from wherever this was invoked: the supervisor's state
rem file lives at runtime\supervisor.state.json relative to the working
rem directory. Called from elsewhere, "stop" reports "supervisor not running"
rem and then only kills the engines - leaving the supervisor alive to restart
rem them and to keep holding the very files we are about to overwrite.
set "STOPPED=1"

if exist "%TARGET%\netzapret.exe" (
    echo Stopping the engines...
    pushd "%ROOT%"
    "%TARGET%\netzapret.exe" stop
    if errorlevel 1 set "STOPPED=0"
    popd
)

rem Not fatal on its own. Killing an elevated supervisor from an ordinary
rem shell is denied, yet the deploy often still goes through - the engines
rem themselves do get killed, and the supervisor follows them out. Say what
rem happened and let robocopy below decide whether it actually mattered.
if "%STOPPED%"=="0" (
    echo Could not stop everything - an elevated instance needs an elevated shell.
    echo Continuing; the deploy will fail below if it really mattered.
)

rem Give the engines a moment to release WinDivert and the TUN adapter.
rem "ping" rather than "timeout": the latter fails outright when this script
rem runs with redirected input, which is how it runs from other tools.
ping -n 3 127.0.0.1 >nul 2>&1

rem The menu is netzapret.exe too, and "stop" does not close it - it holds the
rem deployed assemblies just as firmly as the supervisor does. Say so plainly,
rem because robocopy's failure alone does not point at the open window.
tasklist /fi "imagename eq netzapret.exe" 2>nul | find /i "netzapret.exe" >nul
if not errorlevel 1 (
    echo.
    echo NetZapret is still running - most likely the menu window.
    echo Close it, then run this again. If nothing is open, an elevated
    echo instance is left over and needs an elevated shell to stop.
    exit /b 1
)

echo Deploying to %TARGET%

rem /R and /W are not optional here. Robocopy defaults to one million retries
rem with a thirty second wait, so a single locked file hangs the script for
rem what is effectively forever. Observed exactly that when a stray instance
rem held the deployed assemblies. Two quick retries, then fail loudly.
robocopy "%SOURCE%" "%TARGET%" /E /R:2 /W:1 /NJH /NJS /NP /NDL /NFL >nul

rem robocopy returns 0-7 for success; 8 and above mean real failure.
if %errorlevel% geq 8 (
    echo.
    echo Could not update %TARGET% - something is still holding the files.
    echo The engines were stopped above, so look for strays:
    echo   tasklist ^| findstr /i "netzapret sing-box winws2"
    echo Stopping an elevated instance needs an elevated shell.
    exit /b 1
)

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
if exist "C:\Zapret\Dev\presets\winws2" set "ZAPRET=C:\Zapret\Dev"
if not defined ZAPRET if exist "C:\Zapret\presets\winws2" set "ZAPRET=C:\Zapret"

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

rem Only presets\winws2. winws1 is the previous engine, and winws2_builtin is
rem never read by us either - the program looks in winws2 alone, so those 107
rem files were dead weight in the bundle.
rem
rem The pre-V5 Universal line is left out: those are superseded by V5 and V6,
rem and a menu offering nine near-identical entries makes the choice harder,
rem not richer. This only affects what ships - the installation keeps all of
rem them, so anything can be brought back by copying the file across.
if exist "%ZAPRET%\presets\winws2" (
    robocopy "%ZAPRET%\presets\winws2" "%ENGINES%\zapret\presets\winws2" /E /R:2 /W:1 /NJH /NJS /NP /NDL /NFL ^
        /XF "Universal.txt" "Universal V2.txt" "Universal V2.1.txt" "Universal V2.1 voice ALT.txt" ^
            "Universal V3.txt" "Universal V3 AUTO.txt" "Universal V4.txt" >nul
    if errorlevel 8 (
        echo Failed to bundle presets
        exit /b 1
    )
)

rem Stale copies from an earlier build would otherwise survive: robocopy without
rem /PURGE only adds. Dropping a preset from the list above has to actually drop
rem it, or the exclusion is decorative.
for %%F in ("Universal.txt" "Universal V2.txt" "Universal V2.1.txt" "Universal V2.1 voice ALT.txt" ^
            "Universal V3.txt" "Universal V3 AUTO.txt" "Universal V4.txt") do (
    if exist "%ENGINES%\zapret\presets\winws2\%%~F" del /q "%ENGINES%\zapret\presets\winws2\%%~F"
)

if exist "%ENGINES%\zapret\presets\winws2_builtin" rd /s /q "%ENGINES%\zapret\presets\winws2_builtin"

:done
echo.
echo Done. Nothing is running now - start it from the menu.
exit /b 0
