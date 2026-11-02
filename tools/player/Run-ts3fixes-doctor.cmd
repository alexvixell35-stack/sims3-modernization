@echo off
rem Double-click: finds old ts3fixes copies and stale caches, asks before moving them to a Desktop backup folder.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0ts3fixes-doctor.ps1" %*
pause
