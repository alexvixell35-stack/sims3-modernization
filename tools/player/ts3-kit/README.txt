TS3 kit 1121.e7b: ts3native TEST BUILD for the EA app (TS3.exe 1.69) + diagnostic tool, A/B test mode
=====================================================================================================

What is in this kit
- payload\            a TEST BUILD of ts3native for the EA app, not the published release: asi 0.36.297-eaooa1
                      (the same asi and settings as kit e6 / e7: the v0.3.0 EA settings plus the fix for the 0xD71EBD
                      crash), and ts3fixes.package from our current code: the fix for Buy <-> Build mode switching
                      leaving the game paused, and the CAS error logging switched on (when a mod calls one of our
                      changed Create-a-Style methods in a way that fails, "Unable to invoke an invalid target", it
                      writes which mod and which method to Game\Bin\ts3native.log).
- payload\diag\       the same asi and package, with read-only measurements switched on in its ini (PC check,
                      mod timing, task timing). See README-diag.txt.
- TS3Kit.bat          the tool: 1 diagnose (read-only report zip on your Desktop), 2 launch the game your usual
                      way and capture, 3 install (asks N = normal or D = diagnostic; backs up first),
                      4 uninstall (puts the backup back), 5 quit, 6 play one A/B test session, 7 choose the next arm.

A/B test mode (how to test this kit)
  1. Close the game. Double-click TS3Kit.bat, choose 3 (Install), pick your game folder. The last lines must say
     "Install check: OK". TS3 Turbo Engine (Turbo.asi, Turbo.txt) is moved into the kit's backup folder in both
     arms (not deleted); option 4 (Uninstall) puts it back and checks it byte for byte.
  2. Every time you play: double-click TS3Kit.bat and choose 6. The kit sets the arm for this session BEFORE the game
     starts (it never changes anything while the game runs), starts the game your usual way and waits until you quit.
     Sessions take turns: the first one is arm A, the next arm B, then A again, and so on. Option 7 picks the next
     arm by hand if you need to.
       arm A = our mod ON (ts3native + ts3fixes, CAS error logging on)
       arm B = our mod OFF: ts3native.asi and ts3fixes.package are moved into the kit folder (ab\off) for that
               session, so the game runs without them; the next arm A session puts them back
     Arm B is the fair comparison: the same game, mods and save, without our mod.
  3. Arm A: when a save has loaded, a game notification says
       "TS3 test kit: arm A - our mod ON (ts3native + ts3fixes; CAS error logging on)"
     Arm B: our mod is off, so it cannot show a notification; the kit window says
       "TS3 test kit: arm B - our mod OFF (ts3native and ts3fixes not loaded)"
     Both lines are also in the kit folder's ab\kit.log.
  4. Play a normal session in each arm (the same save, the things you usually do, Create-a-Sim included; 30 minutes
     or more is ideal). Quit the game normally. The kit window then writes a short summary of that session
     (minutes played, script errors, and in arm A also sim updates per second, pauses of 0.25 s and of 1 s or more,
     the longest pause and which switches were on) and a zip on your Desktop.
  5. After at least one session of each arm, send us the newest zip (it holds the summaries of all sessions).
  To stop testing: option 4 (Uninstall) puts your earlier files back (also when the last session was arm B).
  If you start the game without the kit (EA app, a shortcut), it runs the arm the kit set last and no summary is
  written for that session.

The installer only accepts the EA app 1.69 exe (Game\Bin\TS3.exe, PE timestamp 6707155C; image size 0xEB5000 or larger).
It writes Game\Bin\ts3native.asi, Game\Bin\ts3native.ini and Documents\Electronic Arts\The Sims 3\Mods\Packages\
ts3fixes.package (it picks the user folder the game uses, also when Documents is in OneDrive), after a backup.
It moves TS3 Turbo Engine (Game\Bin\Turbo.asi and its Turbo.txt / Turbo.ini) into that backup folder, never deletes
it (it hooks thread creation before ts3native, so ts3native's thread stack fix switches off); uninstall puts it back.
TS3.exe and Options.ini are never changed. The diagnose option never changes anything; it masks your Windows user
name, computer name and home folder in the zip.

After an install, Game\Bin\ts3native.log starts with "host build: EA app 1.69.47".

Source and issues: https://github.com/rodneypantonial/sims3-modernization
