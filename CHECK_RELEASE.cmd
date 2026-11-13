@echo off
cd /d "%~dp0"
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0CHECK_RELEASE.ps1"
set "check_exit=%errorlevel%"
echo.
pause
exit /b %check_exit%
