# 188. User diagnostics in ts3native.log ([UserDiag])

Goal: a player's `ts3native.log` (Discord "stuck saving", GitHub issue) answers the first triage questions without asking the player: which build and edition, which other mods and plugins, how big the world and the mod set are, and, for a save that hangs, which step it hangs in and what it waits for. Built in ts3native 0.36.53 + ts3fixes (`UserDiag.cs`), **not run in game yet** (queue `tools/harness/queues/verify-userdiag.json`, repro `userdiag-save`).

Logging only: no game behaviour changes. `[UserDiag] Enabled=1` by default (cost below); 0 = off. Players are told in `docs/PLAYERS.md` ("Reporting a problem") to close the game and send `Game\Bin\ts3native.log` before the next start rewrites it.

## 1. Session header (`diag:` lines)

| When | Line(s) | Source |
|---|---|---|
| DllMain (after `[SaveWrite]` / `[SaveFlush]` install) | `diag: ts3native 0.36.53 (built ...)`, `diag: edition Steam, TS3W.exe file version 1.67.2.x, install class Steam library` (or `not Steam ... - UNSUPPORTED`), `diag: cpu`, `ram`, `os`, `large address aware` | exe RT_VERSION resource from the loaded image (kernel32 only, no version.dll in DllMain), install class from the exe path (classes only, `UdInstallClass`), `CrSysInit` (docs/135) |
| worker, 15 s after start, background CPU/I/O priority | `diag: exe TS3W.exe <bytes>, sha256 <12 hex>..., TS3W.exe.orig present/absent, steam_api.dll present v<ver>` | `CrFileSha` |
| | `diag: ini (enabled sections, all keys): [Sec], k=v, ...` + `diag: ini N sections, M enabled` | every section with `Enabled>0`, its keys (scrubbed) |
| | `diag: Bin add-ons: <name> <KB> v<ver> (DXVK)` | `*.asi`, `*.orig` and the proxy DLL names in `Game\Bin` (d3d9, dinput8, dsound, version, winmm, wininet, dxgi, d3d11, winhttp, xinput1_3); `(DXVK)` = the file contains the bytes `DXVK` |
| | `diag: registry packs: <names> (N keys)` | subkeys of `HKLM\SOFTWARE\Sims` (32-bit view) [inferred: one key per installed pack; Steam's keys not checked] |
| | `diag: gpu <name>, driver <ver>, VRAM <MB> (primary display)`, `diag: display WxH @ Hz, N monitors` | `EnumDisplayDevicesW` + the adapter's display-class registry key (`DriverVersion`, `HardwareInformation.qwMemorySize`); no D3D/DXGI call in the game process. Hybrid laptops: an adapter with no display attached may be missing [inferred] |
| | `diag: Documents in the user profile / in OneDrive (synced ...) / outside, TS3 user folder found; free disk: game drive N GB, Documents drive N GB` | `UserSims3Dir` (apex.cpp) |
| | `diag: Mods\Resource.cfg <bytes>, hash <8 hex>, PackedFile: <patterns>` or `... missing: the game loads no mods` | patterns are relative paths |
| | `diag: Mods\Packages N .package files, M MB, S with scripts (S3SA), C other, ...; scan T ms`, `diag: script mods (S3SA packages): ...` (up to 100), `diag: packages (first 200 names): ...`, `diag: packages: K more names not listed` | DBPF 2 header + index per file (type `0x073FAA07`; index read capped at 2 MB), up to 5 folder levels |
| | `diag: saves N (+B .backup), F files, M MB in Saves, biggest save M MB`, `diag: header done in T ms` | folder walk |
| world load (from the ts3fixes line `world:`) | `diag: world load #N, uptime, ts3native, mods summary`, then `diag: memory at world load #N: ...` (§6) | memory worker |
| first world load | `diag: loaded modules outside Windows (Bin = game folder): <name> v<ver> (outside Bin)`, `diag: d3d9.dll from Bin (wrapper, e.g. DXVK) / Windows (system d3d9); N modules outside Windows and Bin (overlays, injectors)` | `K32EnumProcessModules` |
| every 10 min of play (after the first world load) | `diag: memory at <uptime> min: ...` (§6) | memory worker |
| exit (DLL_PROCESS_DETACH) | `diag: exit after N min, S saves, W world loads`, `diag: memory at exit: ...` | walk inline (the worker is gone by then) |

Not built: an ini diff against the release defaults (the log lists every key of every enabled section; compare with the release `ts3native.ini` by hand), the DXVK version number, NRaas module versions beyond their file names (`NRaas_X_V123.package`) and assembly MVIDs, CC split by kind (CAS / objects).

## 2. World size and ts3fixes settings (`ts3fixes:` lines)

ts3fixes cannot write to ts3native.log itself, so at every world load (`World.OnWorldLoadFinishedEventHandler`, Instantiator) `Fixes.DiagWorldLoad` sends text lines over a GetOption2 channel: top byte `0x87` (RouteOption2 bits 63 + 58 + 57 + 56, unnamed; `0x86` alone is the stuck-check PROBE mark, docs/163 §10: the game answers false, older ts3native builds pass it to the game, so ts3fixes stops at the first refused message), op in bits 48-55 (0 begin, 1 four ASCII bytes in bits 0-31, 2 end). ts3native logs each line as `ts3fixes: <text>` (non-printables `?`, 470 characters, 400 lines per session). Lines:

- `ts3fixes: ts3fixes 0.1.0 build <MVID 8 hex>, N settings on/valued, M off`, `ts3fixes: settings on: kX, kY=4, ...` (every public static non-const field of `Instantiator` by reflection = the tuning in effect), `ts3fixes: test switches (override the settings): name=on` (only when the agent set some).
- `ts3fixes: world: <World.GetWorldFileName()>, lots L (residential R, with a household O), households H with S Sims (active household A), Sims in the world <LotManager.Actors.Count>, objects <Queries.CountObjects<GameObject>()>`. Sources [verified: decompiled SimIFace `World.GetWorldFileName`, Sims3GameplaySystems `LotManager.AllLots` / `Actors`, `Household.sHouseholdList` / `NumMembers` / `ActiveHousehold`, `Lot.IsResidentialLot` / `Household`, `Queries.CountObjects<T>`; ts3fixes compiles against them]. MiniSimDescriptions are private (`sMiniSims`): not counted.
- `ts3fixes: script assemblies (N): <name> <version> <MVID 8 hex>, ...`: every loaded assembly except the game's ten.

Routing world stats (lot table, footprints) are not in the world line: no counter we already read gives them without new offsets; the per-save `footprint:` / routing lines of other features stay as they are.

## 3. Save trail (`save:` lines, needs `[SaveWrite] Enabled=1`, on in the release)

The old `save: result ...` summary is written only after the game's save returns (savewrite.cpp `SaveGameHook`), so a save that never returns left nothing (Discord report 2026-10-24, v0.1.5 = 0.36.37 + Apex `ApexStandDown=1`). Now:

| Line | Where |
|---|---|
| `save: start #N, normal save / travel save / traveling, uptime, save mode, flush mode (previous flush still running / done)` | first thing in `SaveGameHook` (before `FlushWait`) |
| `save: at start route job none / running (handle 0x..), queued Q, parked P, deferred footprints D, +0x918 free / tid T (sim / route worker), gates open G` | same; reads `g_async`, `g_jobs`, `g_discCount`, `rgOwnerTid`, `g_rgOffSimOpen` |
| `save: memory at start #N: ...` / `save: memory at end #N: ...` (§6) | memory worker, asked at the start / end line |
| `save: > <phase> (+ms since start)` / `save: < <phase> N ms` | phases in order: `prepare`, `info (save paths)` (`SaveInfoHook`), `write (package)`, `write: route gate (deferred footprints, running route job), header` (RT_SAVE entry, `SaveGateEnter`), `write: pre-save listeners`, `write: world save (lots, script state)`, `write: post-save listeners`, `write: package commit`, `backup (old save to .backup)`, `copy (new save into place)`, `final (marker file, folder flush)`, `after the game's save (our flush and log)` |
| `save: wait for <what> took N ms` (waits >= 1 s) | `UdWait` around the unbounded waits: the running route job in `RtWaitJob` and in `DiscDrain`, `+0x918 (deferred footprints)` (DiscDrain's `LockAcquireHook`), the backup worker's copy (`SaveJobPromote`, `SaveJobDiscard`), the previous save's flush worker (`FlushWait`) |
| `save: still running after N s: phase P for N s, waiting for W for N s; <route state>; backup worker copying, F files M MB` | watchdog thread per save, every 5 s until the save returns |
| `save: end #N result R after N ms`, then `save: folder now F files, M MB` | end of `SaveGameHook`; the folder is sized by the watchdog thread after the save |

The pre-/post-save listener phases come from two new call-site hooks in `Save_WritePackage 0xAAC110`: `0xAAC2F3` -> `0xAABB80` and `0xAAC34A` -> `0xAABBC0` [verified disassembly, Steam 1.67.2: both thiscall, one stack argument, `ret 4`, return `al`; a false result ends the write with `0xB` / `0xD`]. `0xAAC320` (World_Save) stays with `[FootprintIndex]` / Apex MemoryGuard; the world-save phase is the span between the two listener calls. With Apex owning `0xAB12AF` the write phase starts at the info step's end, as the old `write ~N (info span)`.

What the trail cannot show: a save stuck in managed code before `IC_LoadSaveManager_SaveGame` (Saver's `FunctionTask`, `OptionsModel.SaveGame`): there is no `save: start` at all then, which itself tells "never reached the native save". The trigger (manual vs NRaas Saver) is not known natively; travel flags are. A P4 clone-run spin at RT_SAVE (docs/25 §2 #6) has no wait marker (off in releases).

Paths: every log line carries file names only. `host` now logs `TS3W.exe` instead of the full exe path; the save backup / flush failure lines log the last path part; ini values and other path-derived strings go through `CrScrub` (docs/135 needles: `<TS3User>`, `<Bin>`, `<home>`, `<user>`, `<machine>`).

## 4. Cost

- DllMain: one ini read, the exe's version resource, CPUID / memory status: well under 1 ms [inferred, not timed].
- Header worker: one thread, 15 s after start, `THREAD_MODE_BACKGROUND_BEGIN`; reads the exe (SHA-256, ~15 MB), Bin add-ons, and per package 96 bytes + its index; ~10-60 s for 10k packages on an HDD [inferred], never on the sim thread.
- World load: ts3fixes ~100-400 GetOption2 calls (one per 4 characters) + one object count + one module list on the sim thread, once per load: ~1-5 ms [inferred].
- Save: ~17 log lines, one watchdog thread, two VirtualQuery walks on the memory worker (§6); the folder walk runs on the watchdog thread after the save.
- No per-frame or per-tick work. Selftest: `TestUserDiag` (selftest.cpp, userdiag-test.h).

## 5. Verify

`node tools/harness/queue.ts tools/harness/queues/verify-userdiag.json` (one Bridgeport load, one agent save to `UserDiag-Scratch`, ~10 min): `logRequire` over the header, world and save lines, `logForbid` on `FAIL` and on any `\Users\<name>` path. Needs `game-copy/ts3native.asi.userdiag` (0.36.53) and the main checkout's `build/ts3fixes.package` rebuilt from this commit.

## 6. Address space (`memory at` lines, ts3native 0.36.66)

For heavy-CC / mod reports (Error 12, crash on save; budget in docs/66). One line per sample (figures made up):

`save: memory at start #3: private 1851 MB, committed 2167 MB; limit 4 GB (large address aware, 64-bit Windows), free 1646 MB, largest free 1525 MB (2nd 80), session low 1301 MB; modules 310 MB, mapped files 120 MB, heaps and other private 2010 MB; walk 6100 us, 9000 regions`

| Field | Source |
|---|---|
| private | `K32GetProcessMemoryInfo` `PrivateUsage` (commit charge) |
| committed | sum of `MEM_COMMIT` regions of every type (images and mapped files too) |
| limit | highest user address + 1: 4 GB = LAA on 64-bit Windows, 3 GB = LAA with /3GB, 2 GB = no LAA |
| free, largest free, 2nd | `MEM_FREE` regions: sum, biggest, second biggest |
| session low | lowest largest free block of all samples this session |
| modules / mapped files / heaps and other private | reserved + committed by region type `MEM_IMAGE` / `MEM_MAPPED` / `MEM_PRIVATE` (heaps, GC heap, stacks, D3D, our pools) |
| walk, regions | cost of this sample |

When: world load, save start and end, every 10 min of play (nothing before the first world load), exit. A largest free block under 256 MB adds `diag: low address space: largest free block N MB (under 256) - risk of Error 12 / crash on save` (at every sample under it). The walk runs on a small userdiag memory worker (64 KB stack reserve, woken by the sim thread through an 8-slot queue; a full queue drops the sample), not on the sim thread and not on the header worker (its package scan can take a minute); at exit it runs inline. Cost [measured, selftest]: 5.5 ms over 5,045 regions (~1.1 us per region) on the worker. The game's region count is not known yet (the `regions` field will show it), so ~5-20 ms of worker time per sample [inferred]. Selftest `TestUserDiag`: top-2 logic, per-type sums, limit text, threshold and the exact line format.

Not built: an ini threshold (256 MB fixed), a per-heap split (heaps are one `MEM_PRIVATE` sum), the warning on the save watchdog lines.

**For the next release notes ("The log now shows"):** the game's memory use when a world loads, when you save (start and end), every 10 minutes of play and when you quit: how much of the game's 4 GB is in use, the biggest free block left, the lowest it got this session, and a warning line when the biggest free block falls under 256 MB (the point where Error 12 and crashes on save get likely). CHANGELOG has no unreleased section yet (v0.1.6 is the latest): copy this bullet there when the next release section opens.

## 7. Player memory lines (`[MemDiag] Light`, ts3native 0.36.79, default on)

Numbers only; no paths, only class and script assembly names. With `[MemDiag] Light=1` (shipped on) the address-space sample runs every 5 min of play (not 10) and each `memory at` line is followed by:

```
diag: mem #7 periodic priv=1839 wPriv=1780 wCom=2357 free=1430 big=1275? img=463? map=118 stkRes=112 stkCom=11.0? miRes=192 miCom=106 eaDir=203? eaDirN=150? rp=0.0 pl=0.0? sp=0.0? gcHeap=360? gcFree=150? gcN=420? rc=364? rcCold=9.0? rcBudget=200? rcN=107000? anim=10.1? rest=1126? t=1534
diag: memsus #7 periodic cc=104 ccIcon=0? ppc=6 etRef=1 gsc=5 mapv=40 wq=28 wpl=22 ogs=5 smh=0? gcCnt=420 rdMs=0.25 lag=180 t=1534
diag: memsus alive: seen=4120 trk=96 freed=31 alive=9 hook=1 ms=0.40 top=Ingredient:4,NRaasOverwatch/Thing:3,Plate:2
```

and once per load `diag: memsus types: n=10837 kinds=310 ms=12.50 top=Sim:265,...`. Fields: docs/191 module 1 (`wCom` commit, `priv` private, `big` largest free, `rc` resource cache, `gcHeap` Mono heap), module 2 (static-event handler counts) and "Player memory lines" (destroyed-but-alive check). Players send `Game\Bin\ts3native.log` as before; `node tools/memory-report.ts <log>` reads it (per load, over time, leak? vs cache, alive by type, mod types tied to the listed script packages).

## 8. Reading a player's log (`tools/player-log-digest.ts`)

`node tools/player-log-digest.ts <ts3native.log | ts3fixes-doctor zip> [...]` turns a player's log (or the zip `tools/player/ts3fixes-doctor.ps1` puts on their Desktop: report + scrubbed `ts3native.log`) into one screen. `--all` adds every script mod and exception, `--json` everything; the last line is `needs review: N issues`, exit 1 when there is one.

- **exe:** Steam 1.67 when the `diag: edition` line says Steam or the `diag: exe` sha256 is the Steam one (`10b2423e2368`, docs/15). An exe outside a Steam folder or with another hash still counts as Steam 1.67 code when all patch sites matched (no `SKIP`/`FAIL ... bytes differ` line, at least 10 `OK` lines): typically a moved or LAA-patched Steam exe, listed as an issue to confirm. Anything else prints `unsupported exe` and stops: the addresses do not apply (porting: docs/68 §7).
- **versions:** ts3native version and build date, the build names `docs/data/builds.json` has for that version, ts3fixes version and build id (MVID prefix), and whether the ts3fixes `ui tasks` field is there (v0.2.4+; without it the player runs an older ts3fixes).
- **machine and mods:** LAA, CPU, RAM, OS, GPUs, display; Bin add-ons, with a note for Apex Radiance (docs/187), the Performance Patch and Settings Setter (docs/68/69); package summary and script mods, with the `overlap` rows of `docs/data/compat-matrix.csv` that match a script mod by name prefix.
- **one line per session event:** start, previous session without a clean exit, world loads (world size and largest free block), saves (kind, result, seconds), exit; a missing exit line is said.
- **exceptions:** the first-chance `exception` lines grouped by code and address, placed in ts3native (offset from the `crash log` module range), TS3W.exe, or another module; `tools/crash-symbolize.ts` names them.
- **issues:** unsupported or changed exe, save results other than 0, saves over 60 s or with long waits, a largest free block under 300 MB (docs/66), a previous session that crashed, no LAA, no or old ts3fixes, exceptions inside ts3native, overlapping add-ons and mods, and every issue of the doctor report.

The self-check (`--self-check`) runs on made-up log lines in the 0.36.148 shapes and a zip built in memory; no player data.
