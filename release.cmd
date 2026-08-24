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

rem The archive is built from the working copy, but the tag is placed on what
rem the server has. Unpushed commits would give a release whose contents and
rem whose tag describe different code - and nothing would say so.
git -C "%ROOT%." fetch --quiet origin
for /f "delims=" %%C in ('git -C "%ROOT%." rev-list --count "@{upstream}..HEAD" 2^>nul') do set "AHEAD=%%C"

if not "%AHEAD%"=="0" (
    echo Local commits are not pushed ^(%AHEAD%^). The tag would point at
    echo different code than the archive contains. Run: git push
    exit /b 1
)

rem Checked before building, not after. Packing takes a minute and produces
rem 55 MB; discovering at the end that the version was already published wastes
rem all of it, which is exactly how this was found.
"%GH%" release view "v%VERSION%" --repo RixyPow/netzapret >nul 2>&1
if not errorlevel 1 (
    echo Release v%VERSION% already exists.
    echo.
    echo A published release is not rebuilt in place - people may already have
    echo the file. Raise ^<Version^> in Directory.Build.props, commit, and use
    echo the new number.
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

rem From %ROOT%: gh works out which repository to publish to from the current
rem directory, and this script is normally started by its full path from
rem wherever the shell happened to be. Called from outside a working copy it
rem fails with "not a git repository" after the archive is already built.
echo Publishing release v%VERSION%
pushd "%ROOT%"

"%GH%" release create "v%VERSION%" "%ROOT%dist\NetZapret.zip" ^
    --title "NetZapret %VERSION%" ^
    --notes-file "%ROOT%docs\release-notes.md"

set "PUBLISHED=%errorlevel%"
popd

if not "%PUBLISHED%"=="0" (
    echo Publishing failed.
    exit /b 1
)

echo.
echo Done.
exit /b 0
