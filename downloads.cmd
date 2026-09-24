@echo off
rem Download counts per release, fresh from GitHub. Logic and Russian
rem text live in downloads.ps1: cmd must stay ASCII (BatchLineEndingTests).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0downloads.ps1"
echo.
pause
