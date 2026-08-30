@echo off
rem Removes everything NetZapret leaves behind on this machine.
rem
rem The program itself installs nothing: it runs from the folder it was
rem unpacked into. But three things do outlive that folder, and deleting it
rem alone leaves them behind - which is exactly the kind of leftover people
rem discover months later and cannot attribute to anything.
rem
rem   the autostart task     survives in Task Scheduler and fails silently
rem   the WinDivert driver   stays registered as a system service
rem   the Defender exclusion stays, quietly unprotecting a folder that is gone
rem
rem ASCII only on purpose: cmd.exe reads batch files in the OEM code page,
rem and UTF-8 Cyrillic here breaks apart into bogus commands.
setlocal

set "ROOT=%~dp0"

echo.
echo   NetZapret - removal
echo   ============================================================
echo.
echo   This will remove:
echo     - the autostart task
echo     - the WinDivert driver service
echo     - the Defender exclusion for this folder
echo.
echo   Your settings in config\ are NOT touched. Delete this folder
echo   afterwards if you want them gone too.
echo.

net session >nul 2>&1
if errorlevel 1 (
    echo   Administrator rights are required: the autostart task and the
    echo   driver service are system-wide. Right-click this file and choose
    echo   "Run as administrator".
    echo.
    pause
    exit /b 1
)

set /p CONFIRM="  Continue? [y/N]: "
if /i not "%CONFIRM%"=="y" (
    echo   Cancelled. Nothing was changed.
    exit /b 0
)

echo.
echo   Stopping the engines...

rem Through the program's own stop, so the supervisor takes its children with
rem it. Killing the processes by name would leave the state file behind and
rem the next start would refuse, believing an instance is still running.
if exist "%ROOT%netzapret.exe" "%ROOT%netzapret.exe" stop >nul 2>&1

taskkill /f /im winws2.exe >nul 2>&1
taskkill /f /im sing-box.exe >nul 2>&1

echo   Removing the autostart task...
schtasks /delete /tn "NetZapret" /f >nul 2>&1

rem The driver is registered under the name WinDivert regardless of what the
rem file on disk is called - this build ships it as Monkey64.sys.
echo   Removing the driver service...
sc stop WinDivert >nul 2>&1
sc delete WinDivert >nul 2>&1
sc stop Monkey64 >nul 2>&1
sc delete Monkey64 >nul 2>&1

echo   Removing the Defender exclusion...
powershell -NoProfile -Command "Remove-MpPreference -ExclusionPath '%ROOT:~0,-1%' -ErrorAction SilentlyContinue" >nul 2>&1

rem The TUN adapter disappears with sing-box; its routes go with it. Nothing
rem to remove here, and trying would touch adapters we did not create.

echo.
echo   Done.
echo.
echo   What is left:
echo     - this folder, including your settings in config\
echo     - entries this program never wrote: the hosts file is edited by
echo       Zapret GUI too, and we do not clean up after it
echo.
echo   Delete the folder to finish.
echo.
pause
