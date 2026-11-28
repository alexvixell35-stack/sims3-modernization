TS3 kit 1121.k4s: ts3native TEST BUILD for the retail game (disc / EA folder install, TS3W.exe 1.67), A/B test mode
=================================================================================================================

What is in this kit
- payload\            a TEST BUILD of ts3native for the retail 1.67 game, not the published release: the same asi
                      0.36.298-mkit2 and settings as kit k3 (core-mod matching for simler90 / ErrorTrap script methods,
                      the 0xD71EBD crash fix), and ts3fixes.package from our current code (with the fix for Buy <-> Build
                      mode switching leaving the game paused).
                      New in k4s: the check of our faster way to update autonomy's object lists when objects move (the
                      long pauses in your last log, "commodmapshadow") runs in arm A and is off in arm B. In arm A the
                      game keeps its own answer; our faster answer is only computed next to it and compared, so arm A
                      checks the fix on your save and arm B is the game exactly as it is.
- payload\diag\       the same asi and package, with read-only measurements switched on in its ini (PC check,
                      mod timing, task timing). See README-diag.txt.
- TS3Kit.bat          the tool: 1 diagnose (read-only report zip on your Desktop), 2 launch the game your usual
                      way and capture, 3 install (asks N = normal or D = diagnostic; backs up first),
                      4 uninstall (puts the backup back), 5 quit, 6 play one A/B test session, 7 choose the next arm.

A/B test mode (how to test this kit)
  1. Close the game. Double-click TS3Kit.bat, choose 3 (Install), pick your game folder. The last lines must say
     "Install check: OK".
  2. Every time you play: double-click TS3Kit.bat and choose 6. The kit sets the arm for this session BEFORE the game
     starts (it never changes anything while the game runs), starts the game your usual way and waits until you quit.
     Sessions take turns: the first one is arm A, the next arm B, then A again, and so on. Option 7 picks the next
     arm by hand if you need to.
       arm A = commodmapshadow ON (check only; game's own result kept)
       arm B = commodmapfast OFF (the game's own code)
     Everything else is the same in both arms.
  3. When a save has loaded, a game notification says which arm runs, for example
       "TS3 test kit: arm B - commodmapfast OFF (game's own code)"
     The same line is in the kit window and in Game\Bin\ts3native.log.
  4. Play a normal session in each arm (the same save, the things you usually do; 30 minutes or more is ideal).
     Quit the game normally. The kit window then writes a short summary of that session (minutes played, sim
     updates per second, how many pauses of 0.25 s and of 1 s or more, the longest pause, script errors, and which
     switches were on) and a zip on your Desktop.
  5. After at least one session of each arm, send us the newest zip (it holds the summaries of all sessions).
  To stop testing: option 4 (Uninstall) puts your earlier files back.
  If you start the game without the kit (Steam, the launcher, a shortcut), it runs the arm the kit set last and no
  summary is written for that session.

The installer only accepts the retail 1.67 exe (Game\Bin\TS3W.exe, PE timestamp 52D872DA, image size 0xE5B000).
It writes Game\Bin\ts3native.asi, Game\Bin\ts3native.ini and Documents\Electronic Arts\The Sims 3\Mods\Packages\
ts3fixes.package (it picks the user folder the game uses, also when Documents is in OneDrive), after a backup.
The A/B sessions only swap Mods\Packages\ts3fixes.package between the two arm copies in the kit (sha256 checked).
TS3W.exe and Options.ini are never changed. The diagnose option never changes anything; it masks your Windows user
name, computer name and home folder in the zip.

After an install, Game\Bin\ts3native.log says "host build: retail 1.67".

Source and issues: https://github.com/rodneypantonial/sims3-modernization
