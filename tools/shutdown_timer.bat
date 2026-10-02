@echo off
setlocal
set "SCRIPT=%~dp0shutdown_timer.ps1"

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%"

echo.
pause
