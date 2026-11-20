TS3 kit: ts3native retail 1.67 TEST BUILD + diagnostic tool
===========================================================

What this is
- A TEST build of ts3native for the retail / disc (non-Steam, non-EA-app) TS3W.exe 1.67 only
  (PE timestamp 52D872DA, SizeOfImage 0xE5B000, file version 0.2.0.209). It is ts3native 0.36.276-retail1-ko1:
  the GitHub pre-release "retail167-test-1118" plus ONE new fix (below), and the ts3fixes.package of the public
  experimental build v0.2.13-exp.20261118. Not a release. Not run in game by us yet.

What is new in kit 1119.2
- Install picks the user folder the game really uses: Documents\Electronic Arts\The Sims 3 (or Die / Les / Los /
  De Sims 3), never a folder named like a copy or backup ("Sims 3 Backup", "... - Copy", "... old", "... (2)").
  Kit 1119.1 could put ts3fixes.package into "Sims 3 Backup\Mods", so the game kept loading an older one. If
  several real-looking folders are left, it lists them (newest activity first) and asks which one.
- Install finds other ts3fixes*.package files anywhere under Mods and other ts3native*.asi files in Game\Bin
  (and Bin\scripts, Bin\plugins). The game loads them all, and mixed versions break things. It offers to move
  them into its backup folder (never deleted; uninstall puts them back).
- After the install it checks and prints OK or MISMATCH for: exactly one ts3native asi (ts3native.asi), its
  version equals the kit's, exactly one ts3fixes.package under Mods and it is the kit's.
- The diagnostic build (payload\diag) can bring its own asi; the install then writes it as ts3native.asi.

What was new in kit 1119
- Fix: the game no longer resets ALL your options when it sees another graphics card. The game remembers the
  card in Options.ini ("lastdevice"); when the next start reports another one (a second GPU, a d3d9 wrapper
  added or removed, the Apex Radiance / RTX Remix bridge versus a direct TS3W.exe start), EA's code puts every
  option back to default: ageing and season lengths, pack toggles, clock, units, audio and graphics. With this
  build only the graphics options (quality levels, edge smoothing, draw distance, resolution...) are detected
  again for the new card; every other option keeps your value. Switch: ts3native.ini [EaCrashFixes]
  KeepOptionsOnGpuChange=1 (0 = the game's own full reset). When it happens, ts3native.log gets one line
  "options: device changed <old> -> <new>, kept N gameplay keys, graphics re-detected".
- Install (option 3) now also installs ts3fixes.package, so you no longer need to copy it by hand. If you put
  one in Mods\Packages yourself, the install backs it up first and uninstall puts it back.
- Launch and capture (option 2) starts the game the way you normally do (your shortcut / launcher).
- Diagnose (option 1) lists which ts3native features loaded or stood down on your game.
- A tool (TS3Kit.bat) that can diagnose your setup, start the game and capture what it writes, install the
  test build for you, and undo the install.

Start
1. Extract the whole zip to a folder (not inside Program Files).
2. Double-click TS3Kit.bat. Windows may ask "Windows protected your PC": More info > Run anyway.
   If the game is under Program Files, right-click TS3Kit.bat > Run as administrator for install / uninstall.

Menu
1. Diagnose (read-only)
   Changes nothing. Looks at Windows, graphics drivers, every Sims 3 install and user folder it can find,
   the settings files, mods list, logs (and which ts3native features stood down or loaded), crash files, Windows security settings (Controlled folder access,
   antivirus), OneDrive / moved Documents folders and more. Writes one file to your Desktop:
   TS3Diag-<date>-<random>.zip (report.txt, report.json and copies of settings and log files).
   Your Windows user name, home folder path and computer name are replaced by <user>, %USERPROFILE%, <pc>
   in everything inside the zip. Save files are only listed (name, size, date), never copied.
   Nothing is uploaded: you send us the zip yourself. Open report.txt first if you want to see what is in it.
2. Launch the game and capture (read-only)
   Notes every settings file, then starts the game THE WAY YOU NORMALLY DO: it lists your Desktop and Start Menu
   shortcuts of the game, Sims3Launcher.exe, Steam or the EA app, marks the one that looks like your usual one and
   asks you to pick (or you start the game yourself and the tool waits). Starting TS3W.exe directly is only the
   last choice: a direct start can make the game see another graphics device (for example without the Apex
   Radiance / RTX Remix bridge setup) and then the game resets ALL its options. The chosen way is in report.txt.
   It waits until you quit, then compares the settings files before and after and collects every log written
   meanwhile.
   Please: load a save, change one setting in the game options, save, quit normally.
   If the game hangs or crashed, press C in the tool's window to collect right away. A memory dump is only
   written if you answer Y; it stays next to the zip and is not put inside it.
3. Install the retail test build
   Checks that the exe is retail 1.67, shows what it will write, asks Y/N, makes a backup, then writes
     Game\Bin\ts3native.asi   (the test build)
     Game\Bin\ts3native.ini   (the test build's default settings; your old ini goes into the backup)
     Documents\Electronic Arts\The Sims 3\Mods\Packages\ts3fixes.package   (the script half of the fixes)
     Documents\Electronic Arts\The Sims 3\Mods\Resource.cfg   (only if you have none: the standard one, so the
                                                               game loads Mods\Packages; uninstall removes it again)
   If the kit has a payload\diag folder, it first asks: normal test build or diagnostic build (logs more for us).
   The user folder is Documents\Electronic Arts\The Sims 3 (copies and backups are skipped; if it is not clear
   which folder the game uses, it asks). Other ts3fixes packages under Mods and other ts3native*.asi files in
   Game\Bin are offered to be moved into the backup. At the end it prints "Install check: OK" or MISMATCH lines.
   and, only if you say yes, merges the recommended Sims3SettingsSetter settings into
     Documents\Electronic Arts\The Sims 3\S3SS\S3SS.toml   (your other S3SS settings are kept; the keys in the
     file below are set to its values). With the Sims 3 Performance Patch in Game\Bin, S3SS AdaptiveWait and
     OversizedThreadStackFix are set to off (the patch does the same job).
   It checks each written file by sha256. TS3W.exe and Options.ini are never changed.
   Backup folder: Documents\Electronic Arts\The Sims 3\TS3Kit-Backup\<date-time>\
   You still need an ASI loader in Game\Bin, as for any .asi mod. The kit does not install one.
4. Uninstall (restore)
   Puts back the files from the newest backup (your old ts3native.asi / .ini / ts3fixes.package); files the kit
   added are removed. Backups are kept.
   By hand: delete ts3native.asi and ts3native.ini from Game\Bin and Mods\Packages\ts3fixes.package (or copy
   your old ones back from the backup folder).

Please use a COPY of your game folder for testing, and back up Documents\Electronic Arts\The Sims 3\ first.
When the tool asks for the game folder, paste the path of that copy.

After a game start, Game\Bin\ts3native.log should contain:
  release: 0.36.276-retail1-ko1 retail
  host build: retail 1.67
and no "SKIP all" or "FAIL" lines. Steam and EA app players: this build is not for you.

Files
  TS3Kit.bat                         starts the tool
  tools\TS3Kit.ps1                   the tool (plain text, readable)
  payload\ts3native-retail167.asi    sha256 a2ab9cf226f318a6de7a7767382150acf6536b9adde44661fe9cf9c6b8fea199
  payload\ts3native.ini              sha256 86b7d521eea54ecea76673ec7395df56208b267b3860bbe8faf11ee957fc1f01
  payload\ts3fixes.package           sha256 5deb4c3d920d29c08c64bd47b9879e714c8ddf02c36436255924e5f367421842 (v0.2.13-exp.20261118)
  payload\S3SS-tunings.toml          sha256 706ad37b61382a28935740087cb3e07e844eadce485880b1b9c65ea5cd4569aa (LF line ends),
                                     the S3SS settings the installer can merge
  payload\diag\                      (only in some kits) diagnostic ts3fixes.package + ts3native.ini, and
                                     ts3native-retail167.asi when the diagnostic has its own (installed as
                                     Game\Bin\ts3native.asi)

Credits
- Sims3SettingsSetter (S3SS) is by sims3fiend: https://github.com/sims3fiend/Sims3SettingsSetter
- The S3SS settings in payload\S3SS-tunings.toml are the ones this project runs next to Apex Radiance
  (thanks to the Apex Radiance team for the shared test setup). S3SS and Apex Radiance are not included.
- ts3native: https://github.com/rodneypantonial/sims3-modernization (issue #555 for this retail test).
