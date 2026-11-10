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

## 7. Tester tool: one-hang evidence
`tools/diag/ts3-hang-diag.bat` (runs `ts3-hang-diag.ps1`; Windows-only on purpose: Toolhelp32, dbghelp, event log; Windows PowerShell 5.1, no admin). Run it while TS3W.exe is hung, or with `-Launch` (starts TS3W.exe directly, waits 90 s for a window, collects if none). Writes `ts3-hang-diag-<time>\` + `.zip` next to the tool (Desktop if that folder is read-only), `summary.txt` with findings first (§9): per-process facts and thread states, full loaded-module lists (WOW64 included: shows whether `d3d9.dll`, `WININET.dll`, `.asi` loaded), minidumps of TS3W.exe and NvRemixBridge.exe (stacks, TEBs and private memory since §8, no full memory), copied Remix/ts3native/S3SS/Apex logs with last-write times (stale = never ran), Bin listing, Application event log. Never kills anything unless `-KillAfter`. `-SelfCheck` tests it on a harmless child process. Not captured: managed Mono stacks, thread start addresses (the .NET API returns 0; read stacks from the dump), anything before the tester ran it.

## 8. The tester's two hang captures (2026-11-10)

Two zips from the §7 tool, both read offline (no debugger installed here; a small minidump parser in the session
scratchpad, not committed). Tester paths, PIDs and channel GUIDs are left out.

**What the two runs are [verified].** Both are direct starts by the tool's `-Launch`: the command line is the bare
`TS3W.exe` path with no arguments, and each report is written 90 s after the process start (the `-Launch` wait). Both
hung. Between them the tester updated Apex Radiance 3.0.0 to 3.1.0 and reinstalled the bridge files (`d3d9.dll`,
`.trex\`, `dxvk.conf`). Same result with both versions. Neither run is a TS3.exe start, so there is no working run to
compare against in these zips; the logs they copied (Apex, S3SS, ts3native, `bridge64.log`) are from an earlier
working Steam start that night.

**Where it stops [verified from the module lists, the same in both runs].**
- Loaded: the bridge `d3d9.dll`, the Ultimate ASI Loader `WININET.dll`, its system `WININET.dll`, `dinput`/`dinput8`
  (the bridge's input hooks), `ApexRadiance.asi` (with `D3DCOMPILER_47`, `cryptsp`, `windows.storage` after it), then
  `Sims3SettingsSetter.asi` last.
- Not loaded: `ts3native.asi`. The ASI loader loads the three in name order (Apex, S3SS, ts3native) [inferred:
  directory order], so the process stops between mapping S3SS and loading ts3native. That fits the report that the
  hang also happens without ts3native. **Not ours.**
- Handles in the dump: the loader's `Ultimate-ASI-Loader-HookIAT<pid>` mutex (the ASIs load from an import hook after
  the static DLLs are initialised, not from the loader's own `DllMain` [inferred from the name]), and Apex's
  per-PID `ApexRadiance.<pid>` and `S3SSApex.<pid>` mutexes, so Apex's `DllMain` ran to its `CreateThread`.
- No Apex or S3SS log line was written (both logs keep the earlier timestamp). Apex writes its first line in its init
  thread (`OpenLog`), S3SS writes "Hook thread started" from `HookThread`.
- 6 threads, all waiting: the main thread, Apex's init thread (its 64-bit stack holds `ApexRadiance.asi+0x7DD90` in
  3.0.0 and `+0x82CB0` in 3.1.0, the thread start [inferred]), three ntdll thread-pool workers (the parallel loader's
  [inferred]) and the input host thread. **No S3SS `HookThread`**: a thread created in `DllMain` exists at once even
  while it waits for the loader lock (Apex's does), so S3SS's `DllMain` has not reached its `CreateThread`
  (`dllmain.cpp:749` at `5eb2c65`) [inferred].

**Bridge suspect from §4/§6: refuted for the start-up [verified].** No `NvRemixBridge.exe` process exists in either
capture (the tool collects it when present). `bridge32.log` ends after the client's `DllMain` work (config, version,
DirectInput hooks, 5 shared-memory channels), and every channel object of that attach is open in the dump
(Module/Device channels, semaphores, events, `QueryResults`, the `Present` semaphore). The server is started only in
`Direct3DCreate9` (§2, upstream), which is never reached. The bridge has nothing to wait on at this point, and the
ASIs that load after it show its `DllMain` returned. (The bridge DETACH wait in §2 stays a possible cause for
processes that do not exit, a separate symptom.)

**Most likely hang site [inferred]: inside `Sims3SettingsSetter.asi`'s process attach**, before its hook thread starts:
CRT/static init, then `DllMain` -> `InitializeAllocatorHooks` (reads `S3SS.toml` through `SHGetFolderPathW` and the
localized folder name, `mi_option_set`, Detours on 15 `MSVCR80` allocator exports), the exe-name check, the hybrid-CPU
CPUID fix. That runs under the loader lock, and Apex's init thread and the loader workers wait behind it. Not proven:
the dumps hold no 32-bit stacks (below), the tester's S3SS file (2026-11-10 build) may be newer than our `5eb2c65`
source, and why a Steam start (TS3.exe) gets past the same code is not explained by anything in these captures.
Application event log: no TS3W or NvRemixBridge entries in either run.

**Tool gap, fixed [verified on a 32-bit dummy].** Windows PowerShell 5.1 is 64-bit, so `MiniDumpWriteDump` of the
32-bit game wrote only the 64-bit (WOW64 layer) stacks: no 32-bit TEB, stack or context. `ts3-hang-diag.ps1` now adds
`MiniDumpWithProcessThreadData` (0x100) and `MiniDumpWithPrivateReadWriteMemory` (0x200). A dump of a 32-bit
`cmd.exe` then has each thread's 32-bit TEB, stack range and WOW64 context (6.6 MB). Read it with WinDbg:
`.effmach x86`, `!wow64exts.sw`, `~*kb`.

**Owner and next steps.** Owner: S3SS's load (or S3SS together with something in the direct-start context)
[inferred]; not ts3native, not the bridge server, not EA code. Next: (1) the tester reruns `-Launch` with the updated
tool, so the main thread's 32-bit stack names the exact wait; (2) one direct start with `Sims3SettingsSetter.asi`
moved out of `Game\Bin` (a single swap to confirm the site the dumps point to, not a bisect); (3) his S3SS version
and whether `[patches.Mimalloc] enabled` is on in `S3SS.toml`. Workaround meanwhile: keep the Steam launch option.

## 9. Crash mode: `ts3-crash-diag.bat` (2026-11-10)

Same script (`tools/diag/ts3-hang-diag.ps1 -Crash`), for "the game goes back to the desktop at start". Built for a
second tester (EA app 1.69, Apex's RTX Remix bridge). Windows PowerShell 5.1, no admin, never changes game files.

**Steps for a player (Discord):**
1. Unzip `ts3-diag.zip` anywhere (Desktop is fine).
2. Double-click `ts3-crash-diag.bat`. When it says so, start The Sims 3 the normal way (EA app or Steam: Play). Wait
   until the game has crashed and the window says `Done. Send this file: ...`.
3. Send the `ts3-crash-diag-<time>.zip` from the same folder. For a Windows crash dump on top, run
   `ts3-crash-diag-dumps.bat` instead (Windows asks twice for admin: dumps on, then off again).

**What it does.** Waits up to 300 s (`-WaitSeconds`) for `TS3W.exe`/`TS3.exe` (EA 1.69 runs the game as `TS3.exe`
behind `Sims3Launcher.exe`; a direct `TS3.exe` start bounces to the EA app, docs/230, so crash mode never starts the
game itself on EA; `-Launch` uses `steam://rungameid/47890` on Steam). It records each game process start, exit time
and exit code (`0xC0000005` access violation, `0xC000007B` bad image = 64-bit DLL, ...), stops watching after 240 s of
normal running (`-WatchSeconds`), and dumps (32-bit stacks, §8) only when the game has no window or is not responding
for `-HangSeconds` (120). Then it collects, masks and zips (`summary.txt`: plain-language findings first):
- Install: registry `Install Dir` (`Sims(Steam)`, `Sims`, EA keys, both views), EA/Origin/Steam default paths, `-Bin`;
  edition, `TS3W.exe`/`TS3.exe` version, sha256 prefix, Large Address Aware flag.
- `Game\Bin` and `.trex\`: each exe/dll/asi/ini/toml/conf/log with size, date, sha256 prefix, PE machine, version.
  Flags: 64-bit DLL/ASI in Bin; bridge `d3d9.dll`/`.trex` without `.trex\NvRemixBridge.exe`; other d3d9 hooks next to
  the bridge (by name: D3DShaderReplacer, WinterRoadsFix, ReShade, ...; or by a `d3d9.dll`/`Direct3DCreate9` string;
  Apex and ts3native excluded); ReShade; more than one ASI loader, or none; S3SS `ExpandedCrashLogs` on (§8);
  ts3native version (log line 1) older than the latest release; ts3native log `os: Windows 6.x` on Windows 10/11
  (compatibility mode).
- Environment: Windows build, GPU + driver, `vulkan-1.dll` in System32/SysWOW64, VC++ 2015-2022 x86/x64, free RAM,
  page file, `AppCompatFlags\Layers` (HKCU, HKLM, WOW6432Node) for the game, launcher, bridge and EA app exes (a
  `WIN7RTM`-style mode is a finding, `HIGHDPIAWARE` only a detail line), Defender detections (`Get-MpThreatDetection`,
  Defender events 1116/1117).
- Crashes: Application log 1000/1001/1002 for TS3W/TS3/NvRemixBridge/Sims3Launcher (24 h: faulting module, offset,
  exception), the newest 5 `Report.wer` (WER ReportArchive/ReportQueue, machine and user), `%LOCALAPPDATA%\CrashDumps`
  dumps of those exes from the last 24 h.
- Logs (5 MB tail cap): Bin `*.log`, `.trex` and `rtx-remix\logs` logs (bridge, Remix, DXVK), Bin toml/conf,
  `ts3native-crashes\crash-*.txt` (newest 3, Bin and Documents), S3SS log + toml, Apex log, toml and
  `ApexRadiance_Crash.txt`, `Options.ini`, `DeviceConfig.log`, `Sims3LauncherLogFile.log`, newest 3 `xcpt*` and
  `ScriptError*.xml`; EA app log: only lines about the game, never lines with token/auth/session/cookie or an `@`.
- Every text file and file name: Windows user name, PC name and e-mail addresses masked. `.dmp` files are not.

**Crash dumps (opt-in, admin).** WER LocalDumps exists only under HKLM, so `-EnableCrashDumps` asks for admin for that
one registry step (`LocalDumps\{TS3W,TS3,NvRemixBridge}.exe`: folder `%LOCALAPPDATA%\CrashDumps`, `CustomDumpFlags`
`0x1B24` = the tool's own dump flags, 3 dumps, marker value `ts3diag`). With `-Crash` the keys are removed at the end
(second admin prompt); alone they stay until `-DisableCrashDumps`. Keys without the marker are never changed.

**Self-check** (`ts3-hang-diag.ps1 -SelfCheck`, ~30 s, never starts the game): 24 regex cases (real lines: WER folder
names, ts3native log lines, a `Report.wer`, Application Error 1000 properties of another app), the parsers, a fixture
Bin/Documents with each bad combination above (all flagged, 3 of 4 crash reports copied, no user or PC name left in
any text file), a dump of a harmless child, and the watch loop on a renamed `cmd.exe` (exit code 3 read back; a
windowless process flagged as a hang). Not tested here: a real EA app start, the admin LocalDumps step.
