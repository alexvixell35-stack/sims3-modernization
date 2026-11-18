@echo off
rem Builds the retail (disc / non-Steam) TS3W.exe 1.67 asi (docs/273): the Steam sources with /DTS3A_TABLE /DTS3A_RETAIL
rem (addresses from ts3addr-retail167.h, per-build constants via TS3A_PICK). Optional argument: output folder (default out-retail).
setlocal
for /f "usebackq delims=" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSDIR=%%i"
if not defined VSDIR (echo Visual Studio with the C++ tools not found & exit /b 1)
call "%VSDIR%\VC\Auxiliary\Build\vcvars32.bat" >nul || exit /b 1
cd /d "%~dp0"
set "OUT=%~1"
if not defined OUT set "OUT=out-retail"
if not exist %OUT% mkdir %OUT%
cl /nologo /LD /O2 /MT /W4 /WX /DTS3A_TABLE /DTS3A_RETAIL /Fo%OUT%\ /Fe%OUT%\ts3native-retail167.asi ts3native.cpp /link /DLL /MAP:%OUT%\ts3native-retail167.map kernel32.lib || exit /b 1
