@echo off
rem Double-click: menu with a read-only diagnostic, launch-and-capture, install and uninstall of the ts3native retail test build.
rem ExecutionPolicy Bypass applies to this one script run only; no system setting is changed.
if not exist "%~dp0tools\TS3Kit.ps1" (
  echo Please extract the whole zip to a folder first, then start TS3Kit.bat from that folder.
  pause
  exit /b 1
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\TS3Kit.ps1" %*
pause
