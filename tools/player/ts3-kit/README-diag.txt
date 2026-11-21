TS3 diagnostic build (EA app 1.69, TEST BUILD): where does the time go?
=======================================================================

What it is
  The same ts3native TEST BUILD as payload\ (same fixes, same settings, including the fix for the
  0xD71EBD crash), plus diagnostics that only MEASURE. They change nothing in the game, your save, your
  mods or your settings.
  - PC check: CPU speed and limits, power plan, battery or plugged in, other programs' CPU use, memory,
    and which graphics card the game and the bridge are set to use
  - mod timing: which script mods use the game's simulation time
  - task timing: which game task or script is running during a long pause
  - CAS errors: when a mod calls one of our changed CAS methods in a way that fails (the "Unable to invoke
    an invalid target" errors in Create-a-Style), which mod and which method
  - adaptive diagnostics (kit 1121.a1): a cheap summary every minute (what you were doing: live, CAS,
    Buy/Build, loading, travel, saving, paused; long pauses; free memory; script errors; how busy the PC
    was). When something repeats, the build turns on a closer look by itself for a few minutes, or for
    your next game start, and writes what it found to adaptdiag-report.txt. Known problems are named
    (for example "known: ..., fixed in ..."). If a part of the install is missing, the game shows one
    notice asking you to make a report.
  Everything is written to ts3native.log and small text files in Game\Bin.

Install with the kit (close the game first)
  1. Extract the whole zip to a folder (for example your Desktop; not inside Program Files).
  2. Double-click TS3Kit.bat, choose 3 (Install), pick your game folder, answer D (diagnostic build).
     The kit first backs up your current ts3native.asi, ts3native.ini and ts3fixes.package, then writes
     the diagnostic ones. Your other mods and settings stay as they are.
     If TS3 Turbo Engine (Turbo.asi) is in Game\Bin, the kit moves it and its Turbo.txt into the same
     backup folder (not deleted): it takes over thread creation before our build, so one of our fixes
     switches itself off. The kit's backup folder has a kit.log line saying where it went. Uninstall
     (option 4) puts it back.
  3. The last lines must say "Install check: OK". If they say MISMATCH, send us a screenshot of that window.

Play
  Start the game the way you always do and play normally for 15-20 minutes, in a world, with your usual
  mods and settings. Nothing special to do: no settings to change, nothing to turn off.
  Then quit the game.

Send it back
  1. Double-click TS3Kit.bat again and choose 8 (Make report). It first shows how big the report will be
     and asks; then it takes a minute or two (it also saves the Windows "dxdiag" report) and writes one zip
     to your Desktop, named TS3Diag-<date>-<code>.zip. (Option 1, Diagnose, makes the same zip without
     asking first.)
  2. Send us that zip. Your Windows user name, computer name and home folder are masked inside it;
     nothing is uploaded by the kit. You can open the zip and look first.

Going back
  TS3Kit.bat, option 4 (Uninstall) puts your earlier files back from the backup.
