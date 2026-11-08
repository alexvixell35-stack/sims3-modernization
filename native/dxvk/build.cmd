@echo off
rem native\dxvk\build.cmd: fetches DXVK (zlib license) at the pinned release, applies patches\*.patch and builds d3d9.dll
rem for x86 (TS3W in-process) and x64 (the RTX Remix bridge server's .trex\d3d9vk_x64.dll), release, with MSVC.
rem Needs git, VS 2022 C++ build tools (x86 + x64), meson + ninja (pip install --user meson ninja) and glslang
rem (KhronosGroup release zip) on PATH. Patches apply with --ignore-whitespace: the DXVK checkout and the patch files can
rem have either line ending (core.autocrlf).
rem DXVK_SRC=<short path> puts the source and build elsewhere (Windows path limits in submodule and build paths).
rem Output: out\x86\d3d9.dll and out\x64\d3d9.dll. The source goes to src\ (git-ignored); delete src\ to refetch.
rem DXVK's own releases are built with MinGW gcc; this MSVC build is for A/B runs against itself (patched vs unpatched).
setlocal
set UPSTREAM=https://github.com/doitsujin/dxvk.git
rem v3.1.1, the release in the player's Bin
set COMMIT=b1a1c99ab52b687cf950d62c88bc2fa316b41663
set HERE=%~dp0
if defined DXVK_SRC (set SRC=%DXVK_SRC%) else (set SRC=%HERE%src)
for /f "usebackq delims=" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -property installationPath`) do set VSDIR=%%i
if not defined VSDIR (echo VS 2022 build tools not found & exit /b 1)
where meson >nul 2>&1 || (echo meson not on PATH: pip install --user meson ninja, then add its Scripts folder to PATH & exit /b 1)
where glslang >nul 2>&1 || where glslangValidator >nul 2>&1 || (echo glslang not on PATH & exit /b 1)

if exist "%SRC%\.git" goto build
git -c core.longpaths=true clone -q --filter=blob:none --no-checkout %UPSTREAM% "%SRC%" || exit /b 1
git -C "%SRC%" -c core.longpaths=true checkout -q %COMMIT% || exit /b 1
git -C "%SRC%" -c core.longpaths=true submodule update -q --init --recursive --depth 1 || exit /b 1
for %%p in ("%HERE%patches\*.patch") do (
  git -C "%SRC%" apply --ignore-whitespace --whitespace=nowarn "%%~fp" || (echo patch %%~nxp did not apply & exit /b 1)
  echo applied %%~nxp
)

:build
for %%A in (x86 x64) do (
  echo == %%A
  call :one %%A || exit /b 1
  if not exist "%HERE%out\%%A" mkdir "%HERE%out\%%A"
  copy /y "%SRC%\_b_%%A\src\d3d9\d3d9.dll" "%HERE%out\%%A\" >nul || exit /b 1
)
echo built: %HERE%out\x86\d3d9.dll and %HERE%out\x64\d3d9.dll
exit /b 0

:one
setlocal
call "%VSDIR%\VC\Auxiliary\Build\vcvarsall.bat" %1 >nul || exit /b 1
cd /d "%SRC%" || exit /b 1
if not exist _b_%1\build.ninja (
  meson setup --buildtype release --backend ninja -Denable_dxgi=false -Denable_d3d8=false -Denable_d3d10=false -Denable_d3d11=false _b_%1 || exit /b 1
)
meson compile -C _b_%1 || exit /b 1
endlocal
exit /b 0
