TS3 diagnostic build (retail 1.67): hourly ON / OFF check of the autonomy fixes
===============================================================================

What it is
  The same retail 1.67 test build as the kit (with the "keep options on GPU change" fix), plus a diagnostic.
  While you play, every sim hour it switches our routing / autonomy / off-screen-Sim fixes OFF and back ON,
  and measures both, including hitches (sim pauses) per hour.
  A notice in the game says which one starts: "Diagnostic: autonomy fixes ON (hour 14)" or "OFF".
  It does not change your save. Sims you control keep the same protection as in the normal build.

Install with the kit (recommended; close the game first)
  1. Extract the whole zip to a folder (not inside Program Files).
  2. Double-click TS3Kit.bat, choose 3 (Install), pick your game folder, answer D (diagnostic build).
     The kit writes Game\Bin\ts3native.asi (the diagnostic asi), Game\Bin\ts3native.ini and
     Documents\Electronic Arts\The Sims 3\Mods\Packages\ts3fixes.package, after a backup.
     It skips folders such as "Sims 3 Backup" and offers to move older ts3fixes packages and extra
     ts3native asi files out of the way (into its backup folder, not deleted).
  3. The last lines must say "Install check: OK". If they say MISMATCH, send us a screenshot of that window.

Install by hand (only if the kit does not work for you)
  1. Game\Bin\: delete ts3native.asi and any other ts3native*.asi (for example ts3native-retail167.asi).
     Copy payload\diag\ts3native-retail167.asi in and RENAME it to ts3native.asi.
     Copy payload\diag\ts3native.ini in (replace the old one).
     Afterwards there must be exactly ONE ts3native*.asi in Game\Bin: ts3native.asi.
  2. Documents\Electronic Arts\The Sims 3\Mods\Packages\ (the folder the game uses, NOT "Sims 3 Backup"):
     replace ts3fixes.package with payload\diag\ts3fixes.package. Remove every other ts3fixes*.package
     anywhere under Mods (move it out, for example to your Desktop).
  All three files belong together. To go back: kit option 4 (Uninstall).

Play
  Play as you normally do for at least 8 sim hours in one go (more is better), at your usual speed.
  The first, partial hour after loading is not counted. If you notice Sims standing around or a
  bad hitch, note the sim hour and whether the last notice said ON or OFF.

Send back (all three are in Game\Bin after you quit to the main menu or exit)
  - ts3native.log          (send it before you start the game again: it is rewritten each start)
  - ts3native-abdiag.csv
  - abdiag-summary.txt
  And one line: did it feel different in ON hours and OFF hours?

Stop the diagnostic
  In Game\Bin\ts3native.ini set AbHourly=0 under [Diag] (last lines), or kit option 4 (Uninstall).
