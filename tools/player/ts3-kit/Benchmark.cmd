@echo off
rem Opt-in benchmark mode (docs/315): copies a save you pick, runs the game by itself for a fixed scripted scenario per A/B arm,
rem writes Game\Bin\adaptdiag-bench.txt (zipped by TS3Kit.bat option 8). The original save is never loaded or changed (hash-checked).
if not exist "%~dp0tools\TS3Bench.ps1" (
  echo Please extract the whole zip to a folder first, then start Benchmark.cmd from that folder.
  pause
  exit /b 1
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\TS3Bench.ps1" %*
pause
