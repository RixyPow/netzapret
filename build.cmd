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

echo Deploying to %TARGET%
robocopy "%SOURCE%" "%TARGET%" /E /NJH /NJS /NP /NDL /NFL >nul

rem robocopy returns 0-7 for success; 8 and above mean real failure.
if %errorlevel% geq 8 (
    echo.
    echo Could not update %TARGET% - the program is probably running.
    echo Stop it first:  nz stop
    exit /b 1
)

echo Done.
exit /b 0
