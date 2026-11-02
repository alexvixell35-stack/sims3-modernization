# EA app edition on the EA laptop (measured 2026-11-02)

Facts from the EA laptop. Tags: [verified] = observed here, [inferred] = not confirmed.

## Install
- EA app edition, TS3.exe 1.69.47.024017 (`Version.tag` GameVersion).
- Bin: `C:\Program Files\EA Games\The Sims 3\Game\Bin`. Exe sha256 equals `game-copy/ea/TS3.exe`.
- PE timestamp 6707155C (2024-10-09), SizeOfImage 0xEB5000. Sections: .text .rdata .data .tls .rsrc .ooa.
- On disk the import table names only `Core/Activation.dll` (DRM wrapper). Real imports resolve after decryption at run time.

## Launch chain [verified]
- The EA app starts `Sims3Launcher.exe -test` (installerdata.xml `<runtime><launcher>`).
- A direct TS3.exe start, also with an EA launch session open, bounces to EALauncher.exe with `origin2://game/launch/?offerIds=sims3_dd,1002441,70384_CE,70384_Unrevised`. Only the launcher appears.
- The launcher is a .NET WinForms app (`Sims3Launcher.ini`: GameProcessName = TS3). Its Play button is owner-drawn, not reachable by UI Automation (only Pane controls).

## Harness launch [verified]
- Swap `TS3.exe` over `Sims3Launcher.exe` (backup `Sims3Launcher.exe.orig`, sha256 prefix 845199D3ED66), then start `EALauncher.exe <URI>`.
- The game then runs as process `Sims3Launcher.exe`: window "The Sims 3" after ~11 s, parent EADesktop.exe, command line `-test`.
- An EA app repair/update restores the launcher: re-swap.
- The swap skips the launcher's Play step, which writes the pack selection to `Documents\Electronic Arts\The Sims 3\LoadOption.ini` (`[productoption] selectedflags`); the game mounts only packs in that mask, so a swapped launch with `selectedflags = 00000000` is BaseGame only (docs/234). `ts3probe.py` EA launch sets it from the launcher's `HKCU\...\Launcher\Settings` `LastRunIProducts & LastRunEProducts` (`TS3_SELECTED_FLAGS` overrides; run the real launcher once if the keys are missing).
- Settings (user env): `TS3_BIN`, `TS3_EXE=Sims3Launcher.exe`, `TS3_LAUNCH=ea`. See `docs/TESTING.md`, `tools/harness/game-exe.ts`.

## ASI loader [verified]
- Ultimate ASI Loader v10.0.2 Win32 (`dinput8.dll` from Ultimate-ASI-Loader.zip, sha256 63b465dbc1bf9c5ab4e33c3ccad099d9edb702f2c51587ac8d4f196497396437) placed as `Bin\wininet.dll` loads.
- The game maps both `Bin\WININET.dll` and `system32\WININET.dll`.
- `ts3native.asi` 0.36.152 (CI artifact of commit b99f370) logs `host Sims3Launcher.exe base 0x00400000`, then `SKIP all: host is not TS3W.exe at 0x400000`.
- The `host` line now also prints PE timestamp and SizeOfImage (Steam 1.67 = 52DEC247 / 0xE5A000, EA app 1.69.47 = 6707155C / 0xEB5000).

## Windowed
- `Documents\Electronic Arts\The Sims 3\Options.ini`: `fullscreen = 0`, `resolution = 1600 900 0` (laptop screen 1920x1200). The EA launch cannot pass `-w`. Audio off too: `voicelevel`, `soundfxlevel`, `ambientlevel`, `musiclevel` = 0 and `soundfxmute`, `musicmute`, `ambientmute`, `voicemute` = 1, for unattended tests on the EA laptop (backup `Options.ini.bak-audio-2026-11-02`).

## Permissions
- The Bin folder is admin-only by default. One-time, elevated: `icacls "<Bin>" /grant "<user>:(OI)(CI)M" /T`.

## Open
- Mod-version popup ("unofficial game modification that does not match your current version", Quit/Continue) blocks start-up with the 1.67 Mods set (separate doc in progress).
- ts3native stays in SKIP until the 1.69 port (docs/68 section 7, `tools/sigmap.ts`, `tools/probe/dump-image.py`).
- Effect of the `-test` argument on the game: unknown.

## Coverage 2026-11-02 (first EA image)

First map of our Steam 1.67 hook sites onto the EA app 1.69.47 exe, from a decrypted memory image taken on the EA laptop. Times PHT. [verified] = tool output of this run, [inferred] = reading of it.

### Run
- 11:38:45 `EALauncher.exe origin2://game/launch/?offerIds=...` (swapped `Sims3Launcher.exe`, see Harness launch); process `Sims3Launcher.exe` pid 38716, window "The Sims 3" at 11:38:47, seen by the poll at 11:38:54. The 1.67 Mods set was in place, so the game sat at the mod-version popup; the code was already decrypted. [verified]
- 11:39:01 `python tools/probe/dump-image.py --out probe-out/ts3-ea169.bin --name Sims3Launcher.exe --wait 120`: `.text` entropy 6.57 on the first poll (no wait), 15,421,440 bytes = 0xEB5000, base 0x400000 (no ASLR move), PE timestamp 0x6707155C, 0 unreadable pages, 1 s. `--name` already accepts any process name (frida.attach by name), no script change. [verified]
- 11:39:12 game stopped (`Stop-Process -Force`), gone after 3 s. Whole game episode 27 s. [verified]
- 11:39:22 `node tools/sigmap.ts match --image probe-out/ts3-ea169.bin --name ea169`: 4 s, writes `docs/data/sigmap-ea169.json` (our signatures and mapped VAs only: committed). [verified]
- `tools/exe-addr-map.ts` / `port-run.ts` read a file-layout PE (section raw pointers), so the memory dump was first rewritten with a 12-line scratch script the way `Sims3PortProbe.ps1 WritePe` does: per section raw pointer = RVA and raw size = virtual size, ImageBase = dump base. Output `probe-out/ts3-ea169-file.exe` (EA code, gitignored). [verified]
- 11:40:05 `node tools/port-run.ts --exe probe-out/ts3-ea169-file.exe --ghidra-names`: 30 s, id `ts3-ea169-file-fec39efa`, outputs under `out/port/ts3-ea169-file-fec39efa/` (`map.csv`, `map.offsets.csv`, `map.vtables.csv`, `problems.csv`, `ghidra-names.csv`; EA-derived, never commit). [verified]

### Headline [verified]
- sigmap: 875 sites, 740 unique, 0 ambiguous, 135 none, 0 without signature. 74 features: 24 fully mapped, 50 partly, 0 with nothing mapped.
- exe-addr-map: 875 addresses: exact 573, masked 268, near 17, offset-changed 5, ambiguous 9, missing 3; refs 248 (0 conflicts); 1 site at the same address. The sigmap `none` sites are mostly found by the growing window: 80 masked, 40 exact, 15 ref, 6 near, 4 offset-changed, 3 ambiguous, 2 missing. exact+masked = 841 of 875 (96%); unresolved (ambiguous + missing) = 12.
- patch-bytes-check: 373 expectations at 371 addresses: ok 244, ported 120, mismatch 5 (at 4 addresses), unmapped 4; 2,701 probe-only expectations from `vcallsites*.h` skipped.
- addr-codemod gen (strict): 1,031 addresses, 206 without a usable row (173 of them `not in the map`: 117 `.data`, 50 `.rdata`, 6 `.text` of which 4 `vcallsites3.h` and 2 `replay-fake.h`).
- problems.csv: 284 rows (prio 1 patch sites 166, prio 2 hooks/code 19, prio 3 reads/data 99). `AI needed: 186 rows (see out/port/ts3-ea169-file-fec39efa/problems.csv)`.
- ghidra names: 3,827 ported, 263 `_guess`, 262 not placed of 4,352.
- Code moved as blocks, not uniformly: most common deltas +0x50 (185 sites), -0x1390 (51), +0x15F0 (49), +0x70 (33), +0xFC0 (28). [inferred: EA 1.69 is a rebuild of the same sources with small insertions, not a relink]

### sigmap per feature [verified]
Worst 10 by `none` count (sigmap `none` / sites; labels from map.csv, bare addresses where our sources have no label):

| feature | none | missing sites |
|---|---|---|
| verify | 15/91 | 0x4B2D00, adoptVisuals, JQ_VTBL_RUNJOBS, stblRebuild, buildStep, ao, stCtor, shaderVs, VF_CCTOR_SLOW, imposterStep, HC_FN, 0xC6C290, 0xE85020, loadPump, fpsLoop |
| routesnap | 15/81 | footprint grid remove 0x62D9F0, RouteManager_AddDivingBoardPortals, _AddWaterSlidePortals, _AddPoolPortals, _SetOceanPortalOwner, _FootprintRemoveLockKey, _AddPortalLocationVariation, _RemovePortals, _AddPortal, World_LotClusterTreesImpl, World_DoorLockStateChangedImpl, World_LotSetEntranceByTargetPointImpl, ... |
| steerro | 14/66 | 0x6119F0, 0x619EF0, 0x61ED80, 0x620D60, 0x620E50, 0x624ED0, 0x6262E0, 0x626300, 0x7EAEA0, 0x807090, 0x83F5F0, STEER_LOCO, 0x9C1B00, AV_PT_POLY |
| satb | 12/149 | 0xD89BF0, 0xD89CD0, 0xE0A6D0, 0xE0A790, 0xE56EAE, 0xE56FEC, 0xE589F7, MD_TABLE, 0xE7CDC0, 0xE8DB40, 0xE99A50, 0xEA3140 |
| values | 7/60 | IC_Route_GetNumInterestPoints, Route_PlanCommon (Plan/Replan), Route_PlanCommon (point ICs), 0x791C00, 0x7920A0, World_FireLotLevelInfoChange (event 68), partition grid 0xC08C30 |
| pools | 7/12 | 0x61AC90, 0x61AD13, 0x61AE80, 0x61EB90, 0x667920, 0x667990, 0x66BB50 |
| ts3native | 6/44 | 0x676340, 0x67634A, 0x676354, 0x67635E, 0x74CFC0, 0xE4E168 |
| loadprof | 5/37 | RTData_Load (R), World_FireEvent 7 (load finished), 0x829410, 0x834DC0, WorldManager_LoadWorld from 0xAABE33 |
| locowin | 4/10 | Route_GetDestinationObstruction, Route_GetObstructionAtPoint, Route_InsertCustomPathAtIndex, LOCO_CALL |
| rtsites | 4/26 | Realm_RayCast, 0x829410, IC_GameUtils_TransitionToLoadWorldImpl, IC_GameUtils_TransitionToNotInWorldImpl |

Fully mapped (sigmap, 24): alloc, cellmemo, decompress, movedwait, optsinit, p10, plandeadline, rcguard, rctrim, replandedupe, savecompress, slotro, snap, stalelot, unseenfbc, wastar and 8 more (`perFeature` in the JSON). A sigmap `none` is a 16-64 byte fixed window that no longer exists verbatim; the exe-addr-map growing window still placed 130 of the 135 (see Headline), so the per-feature sigmap count is the pessimistic view. [inferred]

### Struct field shifts (`map.offsets.csv`) [verified]
5 `offset-changed` sites, 9 groups: `disp 0x0C -> 0x2C` (2 sites, 0x626280 0x626290, watchpt), `disp 0x64 -> 0x14` (0x626285), `disp 0x20 -> 0x08` (0xF20360, rtoro.cpp:292), `disp 0x11EDFA4 -> 0x1247CB4` (0xD819A4, memexp.cpp:554: an absolute data address the plain mask missed), `imm 4 -> 0x24`, `imm 8 -> 0x1C`, `imm 4 -> 0x14`, `imm 1 -> 0x80004001` (0x6119E2..0x611A05, steerro.cpp:377), `imm 0xCA5A68 -> 0xC21A68` (0x74B5F0, RouteManager_FootprintRemoveLockKey). The steerro and watchpt groups look like a changed footprint/child struct layout, not a moved field alone: re-derive those offsets from the 1.69 disassembly before porting steerro/watchpt. [inferred]

### Vtables (`map.vtables.csv`) [verified]
2,099 Steam vtables: 1,788 mapped (201 by neighbour delta, votes 0), 311 unmapped, 305 tie-breaks; 407 with a slot shift, 565 with a changed slot count. 27 named by our sources: 17 map with no slot shift and the same size (e.g. vt_Portal 0xFED048 -> 0x1038590, 0xFFE250 -> 0x10497C8 with 44 votes); 10 are shifted or resized: 0xFEB848 (221 -> 73 slots, shifts +25/+24/+30), 0xFEBF6C (63 -> 18, -26/-33), 0xFFC7FC (36 -> 53, +13), 0xFFE068 (36 -> 20, -14), 0x108B690 (79 -> 76, -3) and the 0xFEAE08/0xFEB5D4/0xFEB720/0xFEBBC0/0xFEBD10/0xFEBE38 family (slots 6-10 and 23 shifted, several `?`). Large slot-count changes with mixed shifts probably mean the run-of-pointers vtable finder merged or split adjacent vtables in one of the two exes rather than EA adding 100 virtuals; check these by hand in Ghidra before using any slot number. [inferred]

### Top 15 problems.csv rows (prio 1 first; mismatch, ambiguous, missing, then named rows the map lacks) [verified]

| prio | Steam address | label | kind | candidate | where |
|---|---|---|---|---|---|
| 1 | 0x00580420 | Matches(kGnGet) | mismatch (exact) | 0x0057FA30 (exact) | eafix.cpp:343 |
| 1 | 0x00626270 | child z setter 0x626270 (count only) | mismatch (exact) | 0x00627650 (exact) | watchpt.cpp:222 |
| 1 | 0x006FA2DB | s[1] | mismatch (masked) | 0x006FAEFB (masked) | crashfx.cpp:251 |
| 1 | 0x00D7E62A | kMxNotifySite[0] | mismatch (exact), 2 expectations | 0x00D7E9EA (exact) | gcroots.cpp:486 (+1) |
| 1 | 0x00629140 | PathNodes_Insert | ambiguous; unmapped | - | routephases.cpp:60 |
| 1 | 0x004E3D00 | (allocsites) | missing | hint 0x004E3EF0 | allocsites.cpp:36 |
| 1 | 0x00FED048 | vt_Portal | not in the map (data) | 0x01038590 via vtables.csv | lotfd.cpp:25 |
| 1 | 0x011608C8 | kHxIc[2] | ported (by-value); not in the map | - | locowin.cpp:102 |
| 1 | 0x011608D0 | kHxIc[3] | ported (by-value); not in the map | - | locowin.cpp:103 |
| 1 | 0x011608E0 | kSnIcDef[36] | ported (by-value); not in the map | - | routesnap.cpp:132 |
| 1 | 0x011608E8 | kSnIcDef[38] | ported (by-value); not in the map | - | routesnap.cpp:134 |
| 1 | 0x011608F0 | kSnIcDef[35] | ported (by-value); not in the map | - | routesnap.cpp:131 |
| 1 | 0x011608F8 | kSnIcDef[37] | ported (by-value); not in the map | - | routesnap.cpp:133 |
| 1 | 0x01160900 | kHxIc[0] | ported (by-value); not in the map | - | locowin.cpp:100 |
| 1 | 0x01160908 | kHxIc[1] | ported (by-value); not in the map | - | locowin.cpp:101 |

Prio 2 unresolved: ambiguous FootprintIface_GetTypeConst 0x620E50, FootprintIface_GetOwnerId 0x620E60, FootprintIface_GetTypeByte 0x624ED0, FootprintRefVec_ReleaseRange 0xAAC090 (all steerro), 0x7F3790 (p4clone), 0x8468C0 (avoidro), IC_Animation_SetMissingAnimationCallback 0xC0FD60 (memexp); missing Stub_Return10 0x70AE20 (hint 0x70BAF0, rtoro), AV_PT_POLY 0xBFB320 (hint 0xBF9BB0, avoidro). [verified]

### Reading [inferred]
- The 4 mismatch addresses map `exact` as code but the expected bytes differ: the patch targets an instruction inside a function that 1.69 recompiled (kGnGet, the watchpt child z setter, crashfx `s[1]`, the Mono notify site). Each needs the 1.69 bytes read at the candidate before any write.
- The 44 `ported (by-value)` rows are IC definition tables (`kHxIc`, `kSnIcDef`) in `.data`: patch-bytes-check found the dwords by value in the 1.69 exe, the map has no row for them because no matched window reads them. A port takes the by-value address; `gen` needs a `ref` row or a by-value rule for them.
- Data addresses no matched window reads (`.data` 117, `.rdata` 50) carry most of the 186 `AI needed` rows; the docs/68 §7 shortcut "no string/IAT-name anchors" is now the main gap.
- Next: hand-verify the 12 unresolved code sites and the 4 mismatches in a Ghidra project of `probe-out/ts3-ea169-file.exe` with `ghidra-names.csv` applied; then decide whether the steerro/watchpt struct changes make those features a 1.69 rewrite rather than a port.

## Laptop setup 2026-11-02 (affinity, DXVK, graphics, power)

- CPU topology [verified, `GetLogicalProcessorInformationEx` RelationProcessorCore]: Intel Core Ultra 7 155U, 12 cores / 14 logical. P-cores (EfficiencyClass 1, SMT) = logical CPUs 0-1 and 10-11; E-cores (class 0) = 2-9; LP E-cores (class 0) = 12-13. The P-cores are not 0-3, so a plain "first 8 CPUs" mask would take one P-core and six E-cores.
- Hybrid-CPU start-up failure (docs/24, 2026-11-02 rows): the known Alder Lake fault is the DRM start-up check misreading E-cores, symptom no window / crash `0xc0000094`; every community fix (IntelFix, Task Manager, S3SS) is a few seconds of narrowed affinity at start, and EA's 2025-01-13 patch fixed the EA app build. The three EA app 1.69.47 runs today reached the window with all 14 CPUs (Harness launch, Coverage), so this build has no core-count crash at start. [inferred: 3 runs; a hybrid-scheduling effect on sim speed or stutter is still open]
- Pin profile [verified self-check, not yet run against the game]: `tools/harness/pin.ts` `MACHINES['ea-laptop'] = '0-5,10-11'` (both P-cores + E-cores 2-5), heavy apps on 6-9,12-13; user env `TS3_MACHINE=ea-laptop` set (`TS3_GAME_CPUS` overrides it). Pinning happens only while the harness runs a job (5 s loop on the `Sims3Launcher` process, priority High); an EA app start without the harness runs on all 14 CPUs. An always-on affinity (IFEO or a launcher wrapper) is not needed for the start-up fault (see above); add one only if the first smoke shows hybrid-scheduling trouble before the harness attaches. [inferred until the first smoke: read back `(Get-Process Sims3Launcher).ProcessorAffinity` = 0xC3F]
- DXVK [verified install]: 3.1.1 x32 `d3d9.dll` (tar.gz sha256 40565B4A724AADC4433FA4E010B4B23916D9B1F1BAEEE64E17186DB94F54E608, d3d9.dll sha256 265888C31CA78DFFA290C39CB7E50BFB02762590E41927906E46FB32F01497FA) in `Bin`, from `%USERPROFILE%\Downloads\ts3-fixes\dxvk\`. DXVK's built-in Sims 3 profile matches `\TS3(W)?\.exe$` only (config.cpp), so `Bin\dxvk.conf` spells it out: `d3d9.customVendorId = 10de`, `d3d9.customDeviceId = 1080`, `d3d9.customDeviceDesc = Geforce GTX 580`, plus the main machine's `d3d9.maxFrameRate = 60`, `dxvk.hud = fps,memory`, `d3d9.samplerAnisotropy = 16`. No `dxvk.hideIntegratedGraphics` (the Intel Xe iGPU, driver 32.0.101.8826, is the only GPU). `wininet.dll` (ASI loader) stays. Not yet launched with DXVK: first smoke checks `DeviceConfig.log` says GTX 580 and `Sims3Launcher_d3d9.log` has no error. [inferred]
- Graphics (`Options.ini`, backup `Options.ini.bak-gfx-2026-11-02`) [verified]: changed `lightingquality 1 -> 0`, `visualeffects 1 -> 0`; already low/medium and kept: `texturequality = 1`, `simquality = 1`, `treequality = 1`, `terrainquality = 1`, `edgesmoothing = 0`, `generalreflections = 0`, `advancedrendering = 0`, `drawdistance = 1` (unchanged per Rodney), `resolution = 1600 900 0`, `fullscreen = 0`, audio lines as before. If it stutters: lower `resolution`, not `drawdistance`.
- Power [verified]: `powercfg /overlaysetactive overlay_scheme_max` rc 0; `powercfg /getactiveoverlayscheme` is refused on this build (`Invalid Parameters`), read back from `HKLM\SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes\ActiveOverlayAcPowerScheme = ded574b5-45a0-4f42-8737-46345c09c238` (Best performance), base scheme Balanced. No PERFBOOSTMODE / PROCTHROTTLEMIN change.

## Harness smoke 2026-11-02
First `queue.ts` job on the EA laptop: `tools/harness/queues/ea-smoke.json` (ea-sv-smoke: CI asi b99f370 pinned, release sections Enabled=0, Baseline-SV-Spring, 5 min soak, scriptErrorsMax 0), `TS3_BIN`/`TS3_EXE=Sims3Launcher.exe`/`TS3_LAUNCH=ea`, screen locked. Times PHT. [verified]
- Run 4 (12:09:47-12:16:26) is the clean row (Mods stable: simler90 EAapp core, no ErrorTrap): launch, agent after 22 s, "Mod Scripts Found" accepted, world loaded 14 s, live mode, build check ok (asi c035abe5a3a6, log ok), `PASS: soak 5 min, watchdog quiet` (clock day 15 17:39 -> day 17 01:36 at Triple, 1.82 -> 1.85 GB committed), Bin asi+ini restored. Row verdict **FAIL** only on `1 new script errors (max 0)`: `Relationship.GetInternal` duplicate key under `Career.CheckBoss` <- NRaas `ResetSimTask` <- Overwatch StuckCheck reset (ts3fixes `OwResetHook` wraps it; stuckcheck_reset 0->3 in 5 min). Same stack on the Steam machine 2026-10-12 (`probe-out/20261012-172543/events.jsonl`): not an EA-edition issue; NRaas frames keep it out of known-script-errors.json. The mod-version popup did not appear (1.69 core mod), as docs/231 §3 expects. The EA laptop has only `Installed="BaseGame"` (no EPs).
- Gaps fixed on the way: run 1 (12:00) `cycle.ts --restart` copies `build/ts3-nointro.package`, which nothing builds on a fresh clone (`node tools/harness/nointro.ts` once); run 2 (12:03) `spawn('python')` hit the Microsoft Store alias `WindowsApps\python.exe` (exit 9009: Node skips pyenv's `python.bat` shim) - the pyenv `versions\3.13.5-win32` folder is now first in the User PATH; run 3 (12:05) watchdog `status` RPC `unable to find module 'TS3W.exe'` - `agent.js status()` now takes `Process.enumerateModules()[0]`, `watchdog.ts` uses `GAME_EXE`; `game-exe.ts` default paths had unescaped backslashes (Steam default `BIN` was broken when `TS3_BIN` is unset).
- Run 3 also loaded with a half-swapped Mods set (another agent swapped the core mod 2.6 s after launch); its row is FAIL for the RPC gap only.
- EA app relaunch (2026-11-02, 13:07 crash -> 13:09 EA Desktop restarted `Sims3Launcher.exe -test` on its own): `queue.ts` now calls `killEaRelaunch`
  (`game-exe.ts`) in the crash path, right after `row.crashed` is set: with `TS3_LAUNCH=ea` it polls `Win32_Process` for 150 s and kills any new
  `GAME_EXE` whose command line has `-test` (log `EA app relaunch killed`); the harness launches nothing in that window, so any such process is EA's. Steam: no-op.
- `LoadSave` (ts3agent, 2026-11-02): a bare save name (`Baseline-SV-Spring`) passed `GetSaveFileDetails` and ended in "A serious error has occurred while
  loading"; the handler now appends `.sims3` when it is missing (package `3eb7bb6fc994`, checked with a live reload, docs/232 second run).
- Left as is: `node()` dumps the game at every soak end (`kill dump (job deadline)`, 1.9 GB in `probe-out/dumps/`), pre-existing; `focus-game.ts` still looks for TS3W (harmless here: the window is never focused); `ai-usage.ts` has no usage file on this laptop (ignored). Not run: timing (laptop gives none), ErrorTrap-based script-error capture (Overwatch wrote the report).
