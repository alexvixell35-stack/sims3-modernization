@echo off
rem Builds out\zzzExitDiag.asi (32-bit DLL, static CRT), runs the self-check and stages the kit (asi + collect.cmd + README.txt).
rem Optional argument: output folder without spaces (absolute, or relative to this folder). docs/67 §47.
rem The self-check also compares every hook site with a Steam 1.67 TS3W.exe at ..\..\game-copy\bin\TS3W.exe (or %EXITDIAG_EXE%).
setlocal
for /f "usebackq delims=" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSDIR=%%i"
if not defined VSDIR (echo Visual Studio with the C++ tools not found & exit /b 1)
call "%VSDIR%\VC\Auxiliary\Build\vcvars32.bat" >nul || exit /b 1
cd /d "%~dp0"
set "OUT=%~1"
if not defined OUT set "OUT=out"
if not exist %OUT% mkdir %OUT%
rem zzz: the ASI loader goes by name, so this loads after ts3native and zzApexRadiance and sees their patches (then SKIPs)
cl /nologo /LD /O2 /MT /W4 /WX /Fo%OUT%\ /Fe%OUT%\zzzExitDiag.asi exitdiag.cpp /link /DLL kernel32.lib advapi32.lib bcrypt.lib || exit /b 1
rem the test image sits away from 0x400000 like ts3native's selftest
cl /nologo /O2 /MT /W4 /WX /wd4505 /Fo%OUT%\ /Fe%OUT%\exitdiag-test.exe exitdiag-test.cpp /link /BASE:0x20000000 || exit /b 1
if not defined EXITDIAG_EXE set "EXITDIAG_EXE=%~dp0..\..\game-copy\bin\TS3W.exe"
%OUT%\exitdiag-test.exe "%EXITDIAG_EXE%" || exit /b 1
copy /y collect.cmd %OUT%\collect.cmd >nul
copy /y README.txt %OUT%\README.txt >nul
del %OUT%\*.obj %OUT%\*.exp %OUT%\*.lib 2>nul
echo built %OUT%\zzzExitDiag.asi
