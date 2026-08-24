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

echo.
echo Done. Nothing is running now - start it from the menu.
exit /b 0
