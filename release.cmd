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
for /f "delims=" %%V in ('powershell -NoProfile -Command "(Get-Item '%ROOT%dist\NetZapret\NetZapret.exe').VersionInfo.FileVersion"') do set "BUILT=%%V"

echo Tag %VERSION%, built %BUILT%
echo %BUILT% | find "%VERSION%" >nul
if errorlevel 1 (
    echo.
    echo The built program reports %BUILT%, which does not match %VERSION%.
    echo Update ^<Version^> in Directory.Build.props and commit.
    exit /b 1
)

rem The version goes into the file name, not only the release title. A downloaded
rem NetZapret.zip says nothing about what is inside until it is unpacked, and
rem people keep several of them in Downloads (user feedback on 0.8.1, 25.09).
rem The in-app updater takes any .zip attached to the release, so the name is
rem free to change. pack.cmd keeps producing NetZapret.zip; only the published
rem copy is renamed.
set "ZIP=%ROOT%dist\NetZapret-%VERSION%.zip"
move /y "%ROOT%dist\NetZapret.zip" "%ZIP%" >nul
if not exist "%ZIP%" (
    echo Could not name the archive %ZIP%
    exit /b 1
)

rem Checksums go into the notes. Telling people to build it themselves and
rem compare is empty advice while there is nothing to compare against: the
rem archive is unsigned, SmartScreen calls it suspicious, and the only honest
rem answer we can give is a number they can check.
rem
rem The SDK version is printed with them, because it changes the IL: the same
rem sources under a different SDK give a different file, and without knowing
rem which one built this, a mismatch says nothing.
rem The wording lives in docs\release-notes.footer.md, not here. This file is
rem read by cmd.exe in the OEM code page, and Cyrillic written into it breaks
rem apart into bogus commands - which is exactly what happened when the text
rem was inlined, and it took the release down with it. The template is UTF-8
rem and holds every Russian word; the line below only substitutes numbers.
set "NOTES=%ROOT%dist\notes.md"
copy /y "%ROOT%docs\release-notes.md" "%NOTES%" >nul

powershell -NoProfile -ExecutionPolicy Bypass -Command "$f=[IO.File]::ReadAllText('%ROOT%docs\release-notes.footer.md',[Text.Encoding]::UTF8); $f=$f.Replace('{ZIP}',(Get-FileHash '%ZIP%' -Algorithm SHA256).Hash).Replace('{EXE}',(Get-FileHash '%ROOT%dist\NetZapret\NetZapret.exe' -Algorithm SHA256).Hash).Replace('{SDK}',(& 'C:\Program Files\dotnet\dotnet.exe' --version)).Replace('{VERSION}','%VERSION%'); [IO.File]::AppendAllText('%NOTES%',$f,(New-Object Text.UTF8Encoding($false)))"

if not exist "%NOTES%" (
    echo Could not prepare the notes.
    exit /b 1
)

rem From %ROOT%: gh works out which repository to publish to from the current
rem directory, and this script is normally started by its full path from
rem wherever the shell happened to be. Called from outside a working copy it
rem fails with "not a git repository" after the archive is already built.
echo Publishing release v%VERSION%
pushd "%ROOT%"

"%GH%" release create "v%VERSION%" "%ZIP%" ^
    --title "NetZapret %VERSION%" ^
    --notes-file "%NOTES%"

set "PUBLISHED=%errorlevel%"
popd

if not "%PUBLISHED%"=="0" (
    echo Publishing failed.
    exit /b 1
)

rem The unpacked copy goes away with the release that produced it.
rem
rem dist\NetZapret\NetZapret.exe and build\NetZapret.exe look identical and sit
rem two folders apart, but only build\ is kept current - build.cmd rewrites it,
rem while this script never returns to dist\. Within a day of a release the
rem unpacked copy is stale, and both of us have already tested against the wrong
rem one: 16 Sep the two were 22 hours apart with nothing on screen saying so.
rem
rem Removed only after publishing succeeded. Before that it is still the thing
rem being released, and a failed upload that also deleted the build would mean
rem packing 55 MB again.
rem
rem The zip stays: it is not mistakable for a program to run, and the checksum
rem in the notes is only checkable while the file it describes is here.
echo Removing the unpacked copy from dist\
rd /s /q "%ROOT%dist\NetZapret" 2>nul
del /q "%NOTES%" >nul 2>&1

rem Not a failure of the release - it is published either way. But saying so
rem matters: a folder left behind is exactly the stale copy this removes, and
rem silence would leave it looking current.
if exist "%ROOT%dist\NetZapret" (
    echo.
    echo Note: dist\NetZapret could not be removed - something is holding files
    echo there, most likely a copy running from it. The release is published;
    echo delete the folder by hand so it is not mistaken for a current build.
)

echo.
echo Done.
exit /b 0
