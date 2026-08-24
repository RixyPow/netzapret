@echo off
rem Packs the distribution and publishes it as a GitHub release.
rem
rem   release.cmd 0.1.0
rem
rem Runs here rather than in CI on purpose. The archive needs winws2.exe, and
rem that comes from an installed Zapret 2 - a build server has no way to get
rem it. CI keeps doing what it is good at: building and running the tests on
rem every push.
rem
rem ASCII only on purpose: cmd.exe reads batch files in the OEM code page,
rem and UTF-8 Cyrillic here breaks apart into bogus commands.
setlocal

set "ROOT=%~dp0"
set "GH=C:\Program Files\GitHub CLI\gh.exe"
set "VERSION=%~1"

if "%VERSION%"=="" (
    echo Usage: release.cmd VERSION      for example: release.cmd 0.1.0
    echo.
    echo The version in Directory.Build.props must match, or the published
    echo archive will report a different one than the tag promises.
    exit /b 1
)

if not exist "%GH%" (
    echo GitHub CLI not found at %GH%
    exit /b 1
)

rem A dirty tree means the archive would contain something that is not in the
rem commit the tag points at, and afterwards there is no telling what shipped.
for /f "delims=" %%S in ('git -C "%ROOT%." status --porcelain') do (
    echo Uncommitted changes present. Commit or stash them first:
    git -C "%ROOT%." status --short
    exit /b 1
)

echo Building the distribution...
call "%ROOT%pack.cmd"
if %errorlevel% neq 0 (
    echo Packing failed.
    exit /b 1
)

rem Checked against the built file rather than trusted: the version lives in
rem Directory.Build.props, and forgetting to raise it there is the easy mistake.
for /f "delims=" %%V in ('powershell -NoProfile -Command "(Get-Item '%ROOT%dist\NetZapret\netzapret.exe').VersionInfo.FileVersion"') do set "BUILT=%%V"

echo Tag %VERSION%, built %BUILT%
echo %BUILT% | find "%VERSION%" >nul
if errorlevel 1 (
    echo.
    echo The built program reports %BUILT%, which does not match %VERSION%.
    echo Update ^<Version^> in Directory.Build.props and commit.
    exit /b 1
)

echo Publishing release v%VERSION%
"%GH%" release create "v%VERSION%" "%ROOT%dist\NetZapret.zip" ^
    --title "NetZapret %VERSION%" ^
    --notes-file "%ROOT%docs\release-notes.md"
if %errorlevel% neq 0 (
    echo Publishing failed.
    exit /b 1
)

echo.
echo Done.
exit /b 0
