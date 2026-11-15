@echo off
rem Builds the rewind spike (x64 host) and its 32-bit toy target into out\, then runs the toy selftest (docs/270).
rem   build.cmd [--no-test | <case>]   one selftest case: basic views files waits threads memory refusals parked writewatch rearm catch workerpark watchdog speed (speed: only when named)
setlocal
for /f "usebackq delims=" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSDIR=%%i"
if not defined VSDIR (echo Visual Studio with the C++ tools not found & exit /b 1)
cd /d "%~dp0"
if not exist out mkdir out
call "%VSDIR%\VC\Auxiliary\Build\vcvars32.bat" >nul || exit /b 1
cl /nologo /O1 /MT /W4 /WX /Foout\ /Feout\rewind-toy.exe rewind-toy.cpp kernel32.lib user32.lib advapi32.lib winmm.lib || exit /b 1
endlocal & setlocal
for /f "usebackq delims=" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSDIR=%%i"
cd /d "%~dp0"
call "%VSDIR%\VC\Auxiliary\Build\vcvars64.bat" >nul || exit /b 1
cl /nologo /O2 /MT /W4 /WX /EHsc /std:c++17 /Foout\ /Feout\rewind-spike.exe rewind-spike.cpp kernel32.lib user32.lib || exit /b 1
del out\*.obj 2>nul
if "%1"=="--no-test" exit /b 0
out\rewind-spike.exe selftest out\rewind-toy.exe %1
