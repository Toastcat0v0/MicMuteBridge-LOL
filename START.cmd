@echo off
cd /d "%~dp0"
REM Always rebuild from THIS folder's source to avoid launching an outdated EXE.
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0BUILD.ps1"
if errorlevel 1 (
  echo Build failed. See Errors\BUILD_ERROR.log in this folder.
  pause
  exit /b 1
)
start "" "%~dp0MicMuteBridge.exe"
