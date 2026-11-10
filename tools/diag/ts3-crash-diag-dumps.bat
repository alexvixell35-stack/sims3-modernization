@echo off
rem Same as ts3-crash-diag.bat, plus a Windows crash dump: asks for admin to turn WER LocalDumps on, and again to turn it off at the end.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0ts3-hang-diag.ps1" -Crash -EnableCrashDumps %*
echo.
pause
