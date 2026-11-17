@echo off
rem Builds the RTX Remix bridge pair with the rewind mode (docs/270 section 12) from Apex Radiance's own bridge source.
rem Windows only: it wraps Apex's native\remix-bridge\build.cmd (MSVC). Needs git, VS 2022 C++ tools (x86 + x64), Python.
rem Everything goes to game-copy\rewind-bridge-1115 (git-ignored): apex-repo (clone at the release tag below, local branch
rem feature/bridge-rewind), venv (meson + ninja), and the pair in apex-repo\native\remix-bridge\out. Nothing is installed.
rem After a build: node tools\turbo-compat.ts install-copy   (the copy of the Apex release that the test arm uses)
setlocal
set TAG=v4.0.1
set HERE=%~dp0
set ROOT=%HERE%..\..\..\..\game-copy\rewind-bridge-1115
set REPO=%ROOT%\apex-repo
set RB=%REPO%\native\remix-bridge
if not exist "%ROOT%" mkdir "%ROOT%"
if not exist "%REPO%\.git" (
  git clone -q --depth 1 --branch %TAG% --filter=blob:limit=2m https://github.com/loinyx/Sims3-ApexRadiance.git "%REPO%" || exit /b 1
  git -C "%REPO%" checkout -q -b feature/bridge-rewind || exit /b 1
)
rem our patches for Apex's bridge: 0015 (rewind mode) and 0016 (device reference count, docs/270 section 29)
for %%p in ("%HERE%00*.patch") do if not exist "%RB%\patches\%%~nxp" (
  copy /y "%%~fp" "%RB%\patches\" >nul || exit /b 1
  rem a source tree fetched before the patch was added lacks it: fetch again
  if exist "%RB%\src" rmdir /s /q "%RB%\src"
)
if not exist "%ROOT%\venv\Scripts\meson.exe" (
  python -m venv "%ROOT%\venv" || exit /b 1
  "%ROOT%\venv\Scripts\python" -m pip install -q --disable-pip-version-check meson ninja || exit /b 1
)
set PATH=%ROOT%\venv\Scripts;%PATH%
call "%RB%\build.cmd" || exit /b 1
echo pair: %RB%\out\d3d9.dll and %RB%\out\.trex\NvRemixBridge.exe
