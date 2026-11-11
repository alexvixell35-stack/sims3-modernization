@echo off
rem Builds out\ts3wrap.exe (64-bit, static CRT): the host window of [Wrapper] (docs/530). Copy it next to ts3native.asi.
setlocal
for /f "usebackq delims=" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSDIR=%%i"
if not defined VSDIR (echo Visual Studio with the C++ tools not found & exit /b 1)
call "%VSDIR%\VC\Auxiliary\Build\vcvars64.bat" >nul || exit /b 1
cd /d "%~dp0"
if not exist out mkdir out
cl /nologo /O2 /MT /W4 /WX /DUNICODE /D_UNICODE /Foout\ /Feout\ts3wrap.exe ts3wrap.cpp /link /SUBSYSTEM:WINDOWS kernel32.lib user32.lib gdi32.lib || exit /b 1
