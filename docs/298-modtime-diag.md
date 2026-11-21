# 298. `[Diag] ModTime`: which script mods use the sim thread, the mod cost report (2026-11-20)

Status: on branch `feature/modtime-master-1120` (master basis 0.36.295-rc1119b, ts3native 0.36.295-mdt1), built + host
tests, smoke queue `modtime-master-smoke-1120` READY, NOT RUN. First built on `feature/modtime-diag-1120` (retail test
basis, kit 1120.1, smokes 1-2 in §7); this port brings only ModTime to master (no AbHourly, no kit, no PcCheck, §8).
docs/302 §3 #1 and the diag half of #5. Ledger §544.

## 1. Why

A many-mods tester (743 packages, 287 script mods incl. the full NRaas set, 4-thread i5) has the sim thread ~95% busy, ~122
script hitches per hour, idle Sims and a 284 s load iteration (docs/302 §0). Rodney 2026-11-20: no bisect, build the
diagnostic first. ModTime names the mods from inside the game, without the player removing anything, and gives players a
short "mod cost report".

## 2. What it does

`[Diag] ModTime=1` (default 0; needs `[UserDiag] Enabled=1` and a ts3fixes with `ModTime.cs`), `ModTimeHz` 10-200
(default 10 for players; harness queues set 50). Steam 1.67, retail 1.67 and EA 1.69 builds (the TLS slot, the GC range
ends and the Simulate loop site have rows in all three tables; `SimLoopAdd` checks the site's bytes and stands down on a
mismatch). Read-only: no Sim, save or setting changes, so no household rule applies.

- **Sampler** (`modtime.cpp`, one thread, above-normal priority so a saturated PC does not bias it): every 1/Hz s it suspends
  the sim thread, reads `eip` (`GetThreadContext`, which also makes the suspend take effect) and up to 48 Mono interpreter
  frames, resumes it. While it is suspended: no lock, heap or log; every read under SEH. Frame walk = autledger.cpp
  `AsLgFrames` (TEB TLS slot `[0x11F4160]` -> context, +8 current frame, frame[0] parent, frame[1] info, info +8
  `MonoMethod*`) [verified docs/04 F6a]; the sim thread's TEB is taken by the Simulate-iteration callback on that thread.
- **From the first script iteration** (new on master): sampling starts at the first Simulate iteration end the callback sees.
  Until ts3fixes opens the first sim hour (op 61 hi 1, world load finished) every sample goes to the **load block**
  (front end, world load, world-load-finished handlers); after a world quit samples go there again. The log line
  `modtime: world live: load / menu phase so far N samples over S real s` marks the switch. [inferred I-544-6: the front end
  runs Simulate iterations before the world-load iteration, so the 284 s load iteration of docs/302 L8 is sampled; the smoke
  checks N > 0.]
- **Names** (after the resume, cached per method): `MonoMethod` +8 class, +0x18 name; `MonoClass` +0 image, +0x34 / +0x38
  name / namespace [verified docs/104 §4]; `MonoImage` +0x18 assembly name, +0x1C module name [verified by smoke 1, I-544-1],
  +0x50 `#GUID` heap = MVID [verified docs/104 §4].
- **Weight** of a sample = the sim thread's CPU cycles since the previous sample (`QueryThreadCycleTime`). Waits weigh 0,
  so the shares are shares of sim-thread CPU, not of wall time (I-544-2).
- **Two owners per sample** (new on master):
  - *with game code* (inclusive, `MdtPick`): the innermost frame of a mod (not EA, not ours), else the innermost ts3fixes
    frame, else the innermost frame (game). No script frame = "engine". A mod's share includes the EA code it calls.
  - *own* (self): the innermost frame's assembly. Fixes the wrapper inflation of smoke 1 (Ts3Fixes 38.0% inclusive on BP,
    because EA work called through our patched methods counted as ours, docs/302 §5): the report shows both, and ts3fixes
    gets its own line plus its top 10 methods by own time (our own cost check).
- **gc/alloc**: `eip` inside `0xE49A00-0xE51380` (Steam; the same offsets around the EA / retail rows of `0xE4A050`,
  `0xE50380`) adds the sample to the inclusive owner's gc share [inferred I-544-3].
- **Busy share**: the sim thread's kernel + user time (`GetThreadTimes`) over wall time while a world is live = "your sim
  thread was busy X% of the time"; >= 90% = the simulation cannot keep up (I-544-7).
- **Hitch stacks** (live only): a Simulate iteration of 250 ms or more keeps its samples' top 3 owner: method and credits the
  inclusive owner with the most samples ("hitches topped" per mod). One `modtime: hitch` log line each (cap 2000), worst 20
  in the summary.
- **Notice rule** (docs/302 §4.4, information only): a line naming mods (or ts3fixes) at >= 25% of sim CPU (with game code)
  for 2 sim hours in a row, with the longest run and peak share. Nothing acts on it.
- **Mod -> package**: one scan on a lowest-priority thread at the first summary: every `.package` under `<user>\Mods`
  (`UserSims3Dir`, then the other `Documents\Electronic Arts\*\Mods` folders), S3SA resources (type `0x073FAA07`; RefPack
  decoded with `RefpackRef`, XOR-decrypted with `MdtS3saDecrypt`), MVID = first `#GUID` entry (`MdtMvidOf`), matched to the
  loaded image's MVID. A game-named assembly found in Mods is labelled `core mod: <package>`. Package names are relative to
  Mods (no user paths). A package with an overlap row in docs/COMPATIBILITY.md (snapshot of `docs/data/compat-matrix.csv`,
  25 rows, 2026-11-05) gets "(docs/COMPATIBILITY.md has a row for this package)".

### Owner resolver for other lanes

`modtime.cpp` exposes the cached owner lookup so a later per-task-run seam (docs/302 §3 #3, `0xD7F000`) can attribute each
task run without its own name code:

```cpp
static void MdtTablesInit();                              // once, in the caller's Apply (ApplyModTime calls it too)
struct MdtOwner { int idx, kind; const char* name; const char* pkg; const u8* mvid; bool mvidOk; };
bool MdtOwnerOf(DWORD monoMethod, MdtOwner* out);         // any thread; false = no tables, method 0, table full
```

`idx` = owner row (stable for the session; 1 = unreadable method), `kind` = `MDT_GAME` / `MDT_OURS` / `MDT_MOD`, `name` =
assembly, `pkg` = Mods package once the scan matched ("" before). Hits are lock-free (hash of 16384 slots, rows published
after a barrier); a miss reads the names under `g_mdtCs` (8192 methods, 512 assemblies).

## 3. Files and the player report (next to ts3native.log, Game\Bin)

`modtime-summary.txt`, the mod cost report (docs/302 §5), top to bottom:

1. "Your sim thread was busy X% of the time while a world was live" (+ "cannot keep up" at >= 90%).
2. Top 10 script mods: own %, with game %, hitches topped, package, COMPATIBILITY.md note. No advice to remove anything.
3. The ts3fixes line (own, with game, hitches) and the notice-rule line.
4. Load block: real seconds covered, sim-thread CPU seconds, samples, top 5 mods by load-phase share (own / with game).
5. Details: samples, measured overhead, gc/alloc share, top 20 owners incl. engine / game / ours (own, with game, gc),
   top 10 methods of the top 5 mods, top 10 ts3fixes methods by own time, top 10 game methods, worst 20 hitches.

`modtime.csv`: per sim hour the top 30 owners (`session,sim_hour,rank,name,kind,package,pct_of_sim_cpu`, with game code).

Written by the sampler thread (never file work on the sim thread) at each sim hour (op 61 hi 1), world quit (hi 2) and every
10 real minutes. **One in-game notice per game session** once 2 sim hours are sampled: "Mod cost report: your sim thread was
busy X% of the time [(the simulation cannot keep up)]. Top script mods by share of sim CPU (own / with the game code they
call): A 12% / 31%, B ..., C .... Full list: modtime-summary.txt in the game's Bin folder." ts3fixes reads the text from
ts3native one bit per call (op 61 hi 3 ready?, hi 4 bit `lo`; < 480 bytes, once a session). The old start notice is gone
(the report is the one notice).

Counters v220 (`mdt*`, docs/registry.md): `mdtOn`, `mdtHz` (switch and rate read-back), `mdtSamples`, `mdtLoadSamples`,
`mdtOverheadUs`, `mdtAsm`, `mdtHitches`, `mdtBusyPct`.

## 4. Cost

Per sample: one suspend / context read / <= 48 frame reads / resume, plus `QueryThreadCycleTime` and `GetThreadTimes`.
Measured on the diag branch at 50 Hz: 73 us per sample, 0.33% of one core [measured, §7]. At the 10 Hz default that is
~0.07% of one core [inferred from the per-sample cost; job 2 of the smoke queue measures it].

## 5. Limits

- A core mod that replaces an EA assembly (same assembly name) counts as game; its package shows as `core mod: <package>`.
- Native work our ts3native hooks do under EA frames counts as game / engine.
- Samples of one iteration are assigned by the iteration counter at sample time: a sample right at an iteration end may go
  to the next one. Hitch stacks keep the first 256 samples of an iteration (25 s at 10 Hz).
- The load block has no hitch stacks (one long load iteration would dominate them); its top 5 is the answer for L8.
- EA 1.69: the rows are the generated table's (not read again for ModTime); the EA laptop smoke confirms names and samples.

### 5.1 Pass-through hooks label EA time as ours (2026-11-21, proposal, not built)

A sample with no mod frame whose innermost ts3fixes frame is a swapped EA method that only calls through
(`FestivalHiddenHook` on `GameObject.UpdateCommodityInteractionMap`, `EcHook` on `EventTracker.ProcessEvent`,
`TrafficCreateHook` on `GlobalFunctions.CreateObjectOutOfWorld`, `SpatialCacheHook` on `Queries.GetObjects`) is credited
to Ts3Fixes, although the time is EA's body (the stub, with the EA class word, reads as game) or what it calls [verified:
`SwapMethod` keeps the name and copies the class word; `MdtPick`]. A tester's report showed 101 s of hitches topped by
FestivalHiddenHook with ts3fixes own time at 0.6% (docs/856 §4). Proposed label: when the inclusive pick is an ours frame
and frame 0 (own) is game, report the owner as `game via ts3fixes hook (<hook method>)`, a row apart from `Ts3Fixes` in
the top owners, the hitch lines and the notice rule; Ts3Fixes keeps the samples whose frame 0 is ours. Cost: one compare
per sample. Needs a selftest case in modtime-test.h (kinds {GAME, OURS, GAME} -> via hook; {OURS, GAME} -> ours).

## 6. Tests

- Native selftest `TestModTime` (modtime-test.h): MVID from a metadata root incl. a truncated one, S3SA decrypt round trip,
  kinds and the pick, ordering, hitch list, notice rule steps, package base name + COMPATIBILITY.md row match, notice text
  (busy line, cannot keep up, own / with game, cap), hitch owner mode, the owner resolver on fake MonoMethod / MonoClass /
  MonoImage structs (cache, same image = one row, ours, unreadable, method 0), op 61 hi 3 / 4 bit reads.
  Optional `TS3_S3SA_FIXTURES` = real packages (each must give >= 1 MVID).
- Queue `tools/harness/queues/modtime-master-smoke-1120.json` (READY, NOT RUN; correctness, Bin state apex): job 1 BP 10 min
  at 50 Hz (version, switch and rate read back in the log and `@mdtOn` / `@mdtHz`, load-phase samples > 0, summary, package
  scan, 0 script errors); job 2 BP 20 min at 10 Hz (two sim-hour summaries, notice text built and shown, the 10 Hz
  overhead number from the summary).

## 7. Results

Diag branch (feature/modtime-diag-1120, retail test basis, owner rule inclusive only, live sampling only, 50 Hz):

- Smoke 1 (0.36.276-modtime1, BP-Spring 15 min, Steam, Apex 4.0.10 + bridge): real assemblies named [verified]: Ts3Fixes
  38.0%, engine 32.9%, Sims3GameplaySystems 9.0%, ScriptCore 8.0%, mscorlib 5.4%, StudentUnionLibrary 2.4%, NRaasOverwatch
  0.8%, NRaasErrorTrap 0.7%; gc/alloc 10.0%; overhead 73 us per sample, 0.33% of one core at 50 Hz. The package scan found
  0 MVIDs (S3SA bodies are XOR-encrypted): fixed with `MdtS3saDecrypt`.
- Smoke 2 (0.36.276-modtime2): PASS in 18.1 min; every mod assembly maps to its package [verified] (NRaas, StudentUnion
  Overrides, ts3fixes, LazyDuchess.SmoothPatch); game-named assemblies found in Mods = core mods, labelled since modtime3.

Master port (0.36.295-mdt1): not run yet.

## 8. Port to master: what came, what stayed

Came: `modtime.cpp` (+ own / with-game owners, load block, busy share, hitches per mod, notice rule line, COMPATIBILITY.md
note, report notice text, owner resolver, counters v220, EA build without the stand-down), `modtime-test.h`, `ModTime.cs`
(report notice in place of the start notice), the `[Diag]` ini keys (`ModTimeHz` default 10), Instantiator / selftest /
userdiag wiring (op 61), `tools/ini-sections.json` `Diag`, agent.js counter names, `fixes-require.ts` startup counters
`mdtOn` / `mdtHz` (harness change).
Stayed on the diag branch: `[Diag] AbHourly` (docs/294), the tester kit, `KeepOptionsOnGpuChange` (its own lane keep-options-gpu-1119), and
`[PcCheck]` (docs/299): it is self-contained but ships `Enabled=1 Notify=1` (a player notice on by default) and has its
own doc, ts3fixes file and op 62, so it needs its own default decision; ModTime no longer prints its PC block.
