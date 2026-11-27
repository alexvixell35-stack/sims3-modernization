ExitDiag - logging-only diagnostics for the crash when quitting The Sims 3 to the desktop

What it does
  Writes what the game does while it shuts down (services, UI trigger hooks, windows), which modules load and unload,
  and a report if the game crashes. It changes nothing in the game: every hook logs and then runs the game's own code.
  It only hooks the Steam 1.67 TS3W.exe; on any other exe it still logs modules and crashes, without hooks.
  Works with or without ts3native and with Apex Radiance.

Install
  Copy zzzExitDiag.asi into  <The Sims 3>\Game\Bin  (the folder with TS3W.exe).

Use
  Play as usual and quit to the desktop. After the crash (or after a normal quit), double-click collect.cmd.
  It writes  Documents\Electronic Arts\The Sims 3\ExitDiag-<date>.zip  and opens its folder. Send that zip.

  The zip holds the ExitDiag logs plus the newest Apex Radiance crash/log files, ts3native.log / ts3native crash
  reports and EA xcpt files, if present. ExitDiag's own logs never contain your Windows user name (paths are written
  relative to Documents or the game folder); the Apex, ts3native and EA files are copied as they are.
  Mods\Packages is listed by file name and size only (no contents).

Remove
  Delete zzzExitDiag.asi from Game\Bin. Optionally delete Documents\Electronic Arts\The Sims 3\ExitDiag.
  (Logs older than 14 days are deleted automatically.)
