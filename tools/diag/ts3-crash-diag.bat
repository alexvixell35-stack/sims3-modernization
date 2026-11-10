@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0ts3-hang-diag.ps1" -Crash %*
echo.
pause
