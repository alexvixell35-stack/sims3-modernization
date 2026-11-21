@echo off
rem Builds out\monohost.exe (32-bit, static CRT). Its own image sits at 0x30000000 so TS3W.exe can map at 0x400000 (docs/119 §8).
rem Run: out\monohost.exe <repo>\game-copy\monohost\TS3W.exe <repo>\game-copy\assemblies
rem M1.2/M1.3: node tools/managed-callgraph.ts hostdata, then add: m13 <repo>\game-copy\monohost\hostdata.txt [lenient]
rem Selftest M1.1-M1.5, M2, M5 (exit code = failed checks): add selftest <repo>\game-copy\monohost\hostdata.txt
rem (copy the exe alone into its folder so the game folder's ddraw.dll and .asi files are not loaded; log: out\monohost.exe.log)
setlocal
for /f "usebackq delims=" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSDIR=%%i"
if not defined VSDIR (echo Visual Studio with the C++ tools not found & exit /b 1)
call "%VSDIR%\VC\Auxiliary\Build\vcvars32.bat" >nul || exit /b 1
cd /d "%~dp0"
if not exist out mkdir out
cl /nologo /O2 /MT /W4 /WX /Foout\ /Feout\monohost.exe monohost.cpp kernel32.lib /link /BASE:0x30000000 /FIXED /DYNAMICBASE:NO /LARGEADDRESSAWARE || exit /b 1
del out\*.obj 2>nul
echo built out\monohost.exe
rem Selftest (docs/119 §11): managed test assembly, compiled against the game's mscorlib/SimIFace copies (not shipped);
rem JitAccessors.cs (docs/207 §9.1) also needs the game's ScriptCore, Sims3GameplaySystems and UI copies in the same folder.
rem Usage: build.cmd [<assembly folder>], default ..\..\game-copy\assemblies (main checkout layout)
set "ASM=%~1"
if not defined ASM set "ASM=%~dp0..\..\game-copy\assemblies"
if not exist "%ASM%\SimIFace.dll" (echo no %ASM%\SimIFace.dll: MonoHostTest.dll not built & exit /b 0)
if not exist "%ASM%\Sims3GameplaySystems.dll" (echo no %ASM%\Sims3GameplaySystems.dll: MonoHostTest.dll not built & exit /b 0)
"%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe" -nologo -nostdlib -noconfig -target:library -optimize -out:out\MonoHostTest.dll "-r:%ASM%\mscorlib.dll" "-r:%ASM%\SimIFace.dll" "-r:%ASM%\ScriptCore.dll" "-r:%ASM%\Sims3GameplaySystems.dll" "-r:%ASM%\UI.dll" "-r:%ASM%\System.dll" "-r:%ASM%\System.Xml.dll" HostTest.cs JitCorpus.cs JitCorpusGates.cs JitAccessors.cs JitReplay.cs EqCorpus.cs ..\..\mods\ts3fixes\src\AutShadowCore.cs ..\..\mods\ts3fixes\src\AutShadowLists.cs || exit /b 1
echo built out\MonoHostTest.dll
