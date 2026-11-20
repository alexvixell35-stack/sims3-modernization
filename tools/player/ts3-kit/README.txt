TS3 kit 1120.e6: ts3native TEST BUILD for the EA app (TS3.exe 1.69) + diagnostic tool
=====================================================================================

What is in this kit
- payload\            a TEST BUILD of ts3native for the EA app, not the published release: asi 0.36.297-eaooa1
                      (current code; it also accepts EA app exes with a slightly larger DRM part), the v0.3.0
                      EA settings plus the fix for the 0xD71EBD crash switched on ([HostMapIterFix]), and
                      ts3fixes.package from the same code.
- payload\diag\       the same asi and package, with read-only measurements switched on in its ini (PC check,
                      mod timing, task timing). See README-diag.txt.
- TS3Kit.bat          the tool: 1 diagnose (read-only report zip on your Desktop), 2 launch the game your usual
                      way and capture, 3 install (asks N = normal or D = diagnostic; backs up first),
                      4 uninstall (puts the backup back), 5 quit.

The installer only accepts the EA app 1.69 exe (Game\Bin\TS3.exe, PE timestamp 6707155C; image size 0xEB5000 or larger).
It writes Game\Bin\ts3native.asi, Game\Bin\ts3native.ini and Documents\Electronic Arts\The Sims 3\Mods\Packages\
ts3fixes.package (it picks the user folder the game uses, also when Documents is in OneDrive), after a backup.
TS3.exe and Options.ini are never changed. The diagnose option never changes anything; it masks your Windows user
name, computer name and home folder in the zip.

After an install, Game\Bin\ts3native.log starts with "host build: EA app 1.69.47".

Source and issues: https://github.com/rodneypantonial/sims3-modernization
