@echo off
rem Sims 3 port probe launcher: runs Sims3PortProbe.ps1 next to this file with Windows PowerShell (no admin, read only).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Sims3PortProbe.ps1" %*
pause
