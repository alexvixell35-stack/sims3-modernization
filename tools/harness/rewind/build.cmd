@echo off
rem Builds the rewind spike (x64 host) and its 32-bit toy target into %RO%\, then runs the toy selftest (docs/270).
rem   build.cmd [--no-test | <case>]   one selftest case: basic views files waits threads keepalive memory refusals parked writewatch rearm catch workerpark watchdog speed (speed: only when named)
setlocal
for /f "usebackq delims=" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSDIR=%%i"
if not defined VSDIR (echo Visual Studio with the C++ tools not found & exit /b 1)
cd /d "%~dp0"
set "RO=out" & if defined REWIND_OUT set "RO=%REWIND_OUT%"
if not exist "%RO%" mkdir "%RO%"
call "%VSDIR%\VC\Auxiliary\Build\vcvars32.bat" >nul || exit /b 1
cl /nologo /O1 /MT /W4 /WX /Fo%RO%\ /Fe%RO%\rewind-toy.exe rewind-toy.cpp kernel32.lib user32.lib advapi32.lib winmm.lib ws2_32.lib || exit /b 1
endlocal & setlocal
for /f "usebackq delims=" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSDIR=%%i"
cd /d "%~dp0"
set "RO=out" & if defined REWIND_OUT set "RO=%REWIND_OUT%"
call "%VSDIR%\VC\Auxiliary\Build\vcvars64.bat" >nul || exit /b 1
cl /nologo /O2 /MT /W4 /WX /EHsc /std:c++17 /Fo%RO%\ /Fe%RO%\rewind-spike.exe rewind-spike.cpp kernel32.lib user32.lib || exit /b 1
del %RO%\*.obj 2>nul
if "%1"=="--no-test" exit /b 0
%RO%\rewind-spike.exe selftest %RO%\rewind-toy.exe %1
