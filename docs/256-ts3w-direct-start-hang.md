# 256. TS3W.exe direct start hangs, TS3.exe works (tester foulplay, static)

2026-11-10, static only (no game run). Tester setup: Steam 1.67, Windows 11, Apex Radiance 3.0.0 defaults, S3SS, the
Apex RTX Remix bridge `d3d9.dll`, Ultimate ASI Loader as `wininet.dll`, ts3native 0.36.240. Report: a direct
`TS3W.exe` start leaves a process with no window and no new mod logs (also without ts3native); the Steam launch option
`"game\bin\ts3" -%command%` works. Tester text and logs are data.

## 1. What TS3.exe is on Steam [verified]

- `Game\Bin\TS3.exe` (29 KB, sha256 `e490fe08e124`, PDB `GameRunner.pdb`) is a launcher stub. Imports: KERNEL32
  (`CreateProcessW`, `WaitForSingleObject`, `GetFileAttributesW`), USER32 `MessageBoxW`, ADVAPI32 registry calls,
  MSVCP80/MSVCR80. No `steam_api`, no `d3d9`, no `wininet`.
- Main `0x401240`: `0x401000` reads the install folder (HKLM `Software\Sims(Steam)\The Sims 3`, value `Install Dir`),
  appends `\Game\Bin\TS3W.exe` (falls back to `TS3.exe`), builds `"<path>" <own command line>` and calls
  `CreateProcessW(path, cmd, NULL, NULL, FALSE, 0, NULL, NULL, ...)`: environment and working directory are inherited,
  no flags. It waits only for `-waitforexit` or `-ead_gl_remove` (turned into `-uninstall:<exe>`). It skips the start
  for `/uninstall`, `/fulluninstall`, `/fulluninstallsilent`. With no install folder it shows "Unable to start. Please
  reinstall the base game." (`0x401070` reads `Locale` for the language).
- Unknown arguments are passed on unchanged. foulplay's option makes Steam run TS3.exe with
  `-"<...>\Game\Bin\Sims3Launcher.exe"` (Steam's default target for app 47890 is `Game\Bin\Sims3Launcher.exe`, from the
  local `appinfo.vdf`). TS3W then gets that junk argument. TS3W ignores it [inferred].
- TS3.exe does not call Steam and does not use `RestartAppIfNecessary`. TS3W.exe does not import `steam_api.dll` and has
  no SteamStub section (docs/32; ts3native diag "steam_api.dll absent"). So there is no "exit and let Steam relaunch"
  path on either exe.

**So TS3.exe adds nothing that a direct start lacks.** The only differences are the context the start comes from:
Steam as the ancestor (Steam environment such as `SteamAppId`, the overlay DLL injected), the parent process, and the
install path taken from the registry instead of the file that was double-clicked.

## 2. Do Apex or S3SS block process exit? [verified from source]

- Apex 3.0.0 (`apex_main.cpp:268-301`): `DllMain` checks the exe name (TS3W.exe or TS3.exe), pins the module, takes
  the per-PID `Local\ApexRadiance.<pid>` and `Local\S3SSApex.<pid>` mutexes, detours `Direct3DCreate9` and starts the
  init thread. `DLL_PROCESS_DETACH` does nothing. It cannot hang exit at DETACH.
- S3SS (`dllmain.cpp` at HEAD `5eb2c65`): `DllMain` hooks the allocator, checks the exe name, and starts `HookThread`
  (PeekMessage loop, `Sleep(10)`). DETACH joins the thread (`WaitForSingleObject` 5 s) only when `lpReserved == NULL`
  (FreeLibrary). At process exit it skips that. No exit hang.
- Apex and S3SS in TS3.exe: both accept that name because on the EA app TS3.exe is the game. On Steam, TS3.exe imports
  neither `wininet` nor `d3d9`, so the ASI loader is never loaded there and neither mod starts in it. Harmless.
- The bridge client (upstream dxvk-remix `bridge/src/client/d3d9_bootstrap.cpp`) does real work in its own `DllMain`:
  `RemixAttach` reads the config, opens `bridge32.log`, adds WndProc and DirectInput detours and creates the message
  channels and the named Present semaphore. At `DLL_PROCESS_DETACH`, `RemixDetach` sends `Terminate` to
  `NvRemixBridge.exe` and waits for an Ack with `commandTimeout`, under the loader lock. The server starts later, in
  `Direct3DCreate9` (`InitServer`, SYN/ACK wait `startupTimeout`). The Apex port's changes to these paths are not
  public here [inferred: same as upstream].

## 3. Can a leftover TS3W.exe block the next start? [verified for TS3W, Apex, S3SS]

- TS3W has no cross-process single-instance lock. Its named `CreateMutexA` sites:
  - `0x4FA2AD`: `SingleMgrMutex%08x` with `GetCurrentProcessId`, per process.
  - `0x747A3F` and `0x88488F`: `CreateMutexA(owner)` on the launcher GUID `0245C82B-EB81-4cb7-93B9-63FD94736297`, then
    `WaitForSingleObject(0)` and `CloseHandle`. This is a non-blocking check for a running launcher before the
    `S3L_NPM_*` launcher messages.
  - `0x4E16C9`: a generic named wrapper. Its callers were not checked.
  - No `FindWindow` import.
- Apex's mutexes are per PID. S3SS has no named objects.
- That leftovers do not block is consistent with the report: the TS3.exe route works while 3 dead TS3W.exe stay alive.

## 4. Combination or single mod? [inferred]

No Apex/S3SS interop runs before their logs open (Apex detects S3SS in its init thread, after `OpenLog`; S3SS writes
"Hook thread started" first). "No new log from any mod" therefore points to a stop before the ASI loader starts the
ASIs. That is either the loader stage (static imports: the bridge `d3d9.dll` `DllMain`, which is listed before
`WININET.dll` in TS3W's imports) or a process that never really started. The bridge is the only add-on with
cross-process waits at startup and at DETACH. Not proven: there is no dump or `bridge32.log` from a hung start.

## 5. Other leads from the working run [verified]

- S3SS "`[StartupWarningDialogFix::warningCallbackWrapper] Pattern matched on unknown version at 0x00ecac30`" is
  benign. That address has no per-version entry, so `Resolve()` always uses the pattern scan and prints "unknown
  version". The pattern matches exactly once in TS3W 1.67 (`sub esp,0x44` at `0xECAC30`).
- The exit-time AV `C0000005 at 0x6FC7178F` is in ts3native (module range `0x6FC10000-0x70677000` is `g_selfLo/Hi`).
  RVA `0x6178F` = `MdEaCores+0x7F` (build 0.36.240, map in the launcherfix worktree, asi sha `3eef9b8c4643` = the
  shipped package). That is the exit memory diag walking the EA heap core ring (`eax 0x011D4BC8` = the second EA
  allocator instance, `ecx` = heap `0x0F6CA988 + 0x448` = the ring head). The read fails on a core already released at
  exit (Apex FastMemory queues big-block releases to a helper). It happens inside `MdEaCores`' `__try`: the VEH logs it
  first-chance, the SEH guard returns false, and the next line logs `eaCom=-`. It is harmless and not the dead
  processes. Fix (cosmetic): skip the core-ring walk in the exit sample, or don't log first-chance AVs from guarded
  memdiag reads.

## 6. Verdict

- The "TS3.exe is required" belief is wrong as stated. TS3.exe only re-runs TS3W.exe with the same environment
  [verified]. What differs is the Steam launch context, or the hang is not deterministic.
- The owner is most likely the bridge (Apex package), not EA, Steam, or S3SS [inferred]. Next checks: a `bridge32.log`
  from a hung direct start, and a Task Manager "Create dump file" of the dead TS3W.exe (main thread stack).
- Workaround: start through Steam (his launch option, or Steam's default). End leftover TS3W.exe and NvRemixBridge.exe
  processes before a new start.
- Questions for the tester: see the report in the session (direct start with only Apex, only S3SS, without the
  bridge; Steam running or not; `bridge32.log`; dump).
