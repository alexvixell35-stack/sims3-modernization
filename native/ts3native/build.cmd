@echo off
rem Builds out\ts3native.asi (32-bit DLL, static CRT) and runs the helper self-check.
rem Optional argument: another output folder without spaces (absolute, or relative to this folder), e.g. for parked builds.
setlocal
rem any Visual Studio 2019+ edition with the C++ x86/x64 tools (vswhere ships with the VS installer)
for /f "usebackq delims=" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSDIR=%%i"
if not defined VSDIR (echo Visual Studio with the C++ tools not found & exit /b 1)
call "%VSDIR%\VC\Auxiliary\Build\vcvars32.bat" >nul || exit /b 1
cd /d "%~dp0"
set "OUT=%~1"
if not defined OUT set "OUT=out"
if not exist %OUT% mkdir %OUT%
cl /nologo /LD /O2 /MT /W4 /WX /Fo%OUT%\ /Fe%OUT%\ts3native.asi ts3native.cpp /link /DLL /MAP:%OUT%\ts3native.map kernel32.lib || exit /b 1
rem selftest's own image sits elsewhere too: at 0x400000 it covers game addresses, and its "nothing matches the game's bytes"
rem checks failed once a probe build (/DTS3N_VCSITES5, docs/133 §6) grew the image
cl /nologo /O2 /MT /W4 /WX /wd4505 /Fo%OUT%\ /Fe%OUT%\selftest.exe selftest.cpp kernel32.lib /link /BASE:0x20000000 || exit /b 1
cl /nologo /O2 /MT /W4 /WX /wd4505 /Fo%OUT%\ /Fe%OUT%\decompress-test.exe decompress-test.cpp kernel32.lib || exit /b 1
rem waterjps-test maps game code at 0x61F000 and 0x66D000, so its own image sits elsewhere
cl /nologo /O2 /MT /W4 /WX /wd4505 /Fo%OUT%\ /Fe%OUT%\waterjps-test.exe waterjps-test.cpp kernel32.lib /link /BASE:0x20000000 || exit /b 1
rem savecompress-test maps game code at 0x4EB000
cl /nologo /O2 /MT /W4 /WX /wd4505 /Fo%OUT%\ /Fe%OUT%\savecompress-test.exe savecompress-test.cpp kernel32.lib /link /BASE:0x20000000 || exit /b 1
rem steerro-test maps the whole game image at 0x400000 (needs the exe; run by hand, docs/84 §3.6, §4.6); large address
rem aware since docs/137: its fake worlds are never freed and 3000-case runs pass 2 GB
cl /nologo /O2 /MT /W4 /WX /wd4505 /Fo%OUT%\ /Fe%OUT%\steerro-test.exe steerro-test.cpp kernel32.lib /link /BASE:0x20000000 /FIXED /DYNAMICBASE:NO /LARGEADDRESSAWARE || exit /b 1
%OUT%\selftest.exe || exit /b 1
copy /y ts3native.ini %OUT%\ts3native.ini >nul
del %OUT%\*.obj %OUT%\*.exp %OUT%\*.lib %OUT%\selftest.log 2>nul
echo built %OUT%\ts3native.asi
