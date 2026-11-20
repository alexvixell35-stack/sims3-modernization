# 302. Many mods: where the cost grows with mod count, our gap, next week's plan (2026-11-20)

Status: static research and plan only. No build, no game run, nothing changed in code. Branch `docs/many-mods-perf-1120`.
Tags: [verified] = disassembly, decompiled code or our source, cited; [measured] = from a run, cited; [inferred] = reasoning,
to be confirmed; [community] = reported by others.

Question (Rodney 2026-11-20): next week's focus is game performance with MANY mods installed (CC 60-100 GB, many script
mods), like the SmellyMods STBL case (thousands of string tables walked per lookup, now an in-game merged index PR'd to
Apex). Find our gap and plan features, including (a) round-robin scheduling of script mods' sim-thread work and (b) a
cut-off for a mod that takes too much sim-thread time.

## 0. Short answer

- **The big many-mods cost today is script mods on the sim thread, not CC.** The many-mods tester's install (743 packages, 287 script
  mods, i5-7400 4c/4t) has the sim thread at 950-980 ms/s, ~122 script hitches per hour, sim p99 1.3-1.5 s, and a 284 s
  load iteration (world load + world-load-finished handlers) [measured, local tester diag results 11-19]. Our
  switches are not the cause (same in both AbHourly arms). Render is ~60 fps.
- **CC lookups are mostly covered.** Apex has the per-key resource lookup cache, missing-file memo, file-list cache and
  fast CAS sort; STBL is done (merged index PR to Apex, docs/297). What is left on the CC side is unmeasured startup
  cost (CAS part cache, catalog decode), thumbnail growth, and one unlocked package-list walk. These are engine
  items: we build them ourselves in our Apex fork (default off in Apex's config), prove them and PR them to Apex, the
  same way as the STBL index; first a diff of the newest Apex release against our late-October fork checkouts (§7 task 0).
- **Our gap is attribution and control of script-mod work.** We can now name the mods (`[Diag] ModTime`, docs/298, on a
  branch, not on master, sampling only while a world is live). We have no per-mod budget, nothing that times load-phase
  mod work, and several built spread fixes for NRaas (docs/145, 146, 147) that were never run.
- **Round-robin + cut-off verdict: feasible only at task-run granularity, and only partly.** EA already round-robins
  ThreadedObjects with a 5 ms budget checked between task runs [verified, docs/36 §1]; TaskObjects has no budget. A
  per-mod budget can skip a task's next resume (an extra `Sleep(0)`), never cut a run in the middle; inline alarm
  callbacks and EventTracker listeners are not tasks and cannot be split. It does not lower total work when the sim
  thread is saturated; it bounds how much one mod adds to a single sim iteration and lets EA and household work go
  first. The household rule makes "unknown scope = household", so most global mod managers (StoryProgression,
  Overwatch) cannot be deferred automatically. **Safest form:** measure and report per mod (no behaviour change), then
  defer only task runs whose target is a provably uncontrolled NPC or unoccupied lot, by at most 1-2 sim iterations
  with a hard ceiling, default off. Hard cut-off (skipping a mod) never automatic: only as an hourly A/B "what does
  mod X cost you" test the player opts into (§4.6).

## 1. Sources and what already exists

- Ours: docs/28 sim-thread map, docs/36 scheduler (stages, budgets), docs/39 resource system, docs/08 resource and
  catalog design, docs/17 heavy store content (M1-M8 plan, never run), docs/193 native leak scan, docs/539 compositor
  growth, docs/581 EA unused perf code (§2.1 script tuning cache), docs/145/146/147 spread fixes, docs/166 task
  attribution (Frida), docs/188 UserDiag, docs/294 AbHourly, docs/298 ModTime (branch `feature/modtime-diag-1120`),
  docs/297 STBL (branch `feature/compat-smellymods-stringmerge-1119`), docs/617 script hitch leads, docs/619 EventTracker,
  docs/COMPATIBILITY.md + `docs/data/compat-matrix.csv`, docs/69 S3PP vs ours, docs/23 #20 package lookup index.
- Apex (engine/memory owner; read from `game-copy/apex-src/docs/features/performance/*.md`, checkout of 2026-10-27, and the
  newer Apex tree under `game-copy/rewind-bridge-1115/apex-gi`): Faster Game File Lookups (`ResourceLookupCache`,
  FindProvider `0x4AFFC0`, on), Remember Missing Files (about 1/3 of lookups miss, on), Faster File Lists
  (`FileListCache`, on), Faster Sim Building (`FastCasSort`, on), Faster Object Lookups, Spread New Objects Over Frames
  (`SceneNodeBudget`), Faster Scripts, Fast Memory, Fast Cache/Texture Compression, Room to Save, Lot Streaming (off).
  Not re-checked against Apex main today: whether a newer Apex release added anything for catalog, thumbnails or CAS
  part cache startup.
- S3SS: stacks, mimalloc, WorldCache size, RefPack, lot streaming, `CreateFileRandomAccess` I/O hint; nothing in the
  resource or string code (Apex doc, docs/297 §3).
- S3PP: `PackageIndexWarmup` (pre-reads package indexes and DCCache into the OS file cache at the menu; no game memory),
  `StblBucketLayoutTuning`, `LocalizedStringCache`, `MonoTypeEHashProbe`, `MonoIsinstCache`, `SimulatorStageProfiler`
  (docs/69). Our stance: own fixes first, S3PP feature off (memory "prefer own fixes over S3PP").
- EA: ThreadedObjects 5 ms budget + round-robin cursor; AutonomyManager 1 search per tick, SimUpdate 2 Sims per tick,
  Services one per 30 ticks [verified, docs/04a §2]; `scriptCache.package` tuning cache that never stays on (§2.1 L7).

## 2. Cost surface: what scales with mod count

### 2.1 Load time

| # | Item | What scales | Where | Exists | Gap |
|---|---|---|---|---|---|
| L1 | DBPF open + index read | one `DatabasePackedFile` per package, index read per resource | vtable `0xFB2600`; one global `g_PackedFileLock 0xFB24F8` held across `ReadFile` + decode in `OpenRecord 0x4A94C0` [verified, docs/39 §0.2, §1.2]; plain `ReadFile`, no mapping [verified, docs/08 §0.2] | S3PP warmup (OS cache), S3SS I/O hint, offline merge tools | startup time per package never measured (docs/17 M1/M4 not run) |
| L2 | Resource.cfg walk, package list | sorted DB array, equal priority = first registered | cfg `0x596450`, parse `0x4A1BD0`, glob `0x4A2A70`, `RegisterDatabase 0x4B2D00` [verified, docs/08 §2.1, docs/39 §0.7] | none needed | `GetKeyList 0x4B1AE0` walks the array with no lock [inferred race, docs/39 §0.6]; check whether Apex `FileListCache` already serializes it |
| L3 | Resource lookup per key | ~440 package probes per cache miss with heavy CC [verified mechanism, docs/08 §2.2]; 0.67% of sim time, ~6.7 ms/s [measured, docs/39 §0.4] | `FindDatabase 0x4AFDA0` -> `GetPackageSource 0x4AFFC0`, `DBPF_IndexCompressed_Find 0x4AA1B0` | Apex lookup cache + missing-file memo | covered; docs/23 #20 "virtual CC merge" is superseded by Apex |
| L4 | STBL lookup | tables walked per lookup, ~29 KB layout per table | `GetString_A 0x59C5B0`, per-table Find `0x59AF50` [verified, docs/297 §5a] | merged index PR to Apex, SmellyMods offline, S3PP | done |
| L5 | CAS part cache + part lists | every CASP decoded at startup | `CASPartCacheService::Init 0x5AEDF0` -> `GetKeyList` type `0x034AEECB`, fills `CASPartCache.package` (`0x5AE170`) [verified, docs/08 §2.5] | Apex `FileListCache`, `FastCasSort`; ours `CasTrim`; cache clear in installer | startup time and MB per part never measured (docs/08 §9 Q4); EA bug `GetCachedPart 0x5A44A0` (types 53/54 pick a random part) [verified static, docs/550] unfixed |
| L6 | Build/buy catalog | every OBJD + 14 product types decoded at startup and kept: 576 B fixed per OBJD, 8 MB for 14,577 (CC 2.1 MB) + heap blocks | `Catalog::Init 0xC93000` -> `0xC92E20`, `0xC922C0` `GetKeyList` on `0x319E4F1D`; filter in memory `0xC90100` [verified; sizes inferred, docs/08 §2.5 H1] | Apex `FileListCache`, Nexus 242 (LAYO) | startup time unmeasured; lazy decode not viable (docs/23 #10) |
| L7 | Script assemblies (S3SA) | each mod assembly loaded, tunables found by reflection | `ScriptCore.CacheManager`, gate `CacheManager_IsCachingAllowed 0x73C6C0`; a signature miss clears the cache and leaves caching off for the session [verified code, docs/581 §2.1]; ours is 260 B (signature only), rewritten each start, so `FindTunables` reruns every boot [verified file, docs/581 §2.1] | none | **gap**: cost of `FindTunables` + `PersistStatic` scans grows with every S3SA mod; never measured; why the cache never turns on in a normal player install is not known [inferred: it is the same for players] |
| L8 | Mod startup work | `OnWorldLoadFinished` / Instantiator handlers of every mod, ErrorTrap's load-up scan, Overwatch/StoryProgression init | Many-mods tester: one 284 s Simulate iteration at load (S3PP: `ProcessTaskState` 280 s + ThreadedObjects 57.8 s) and ~64 GC iterations of ~1 s right after [measured, tester results round 2 §1]; ErrorTrap load-up 0.5-0.9 s tick [measured, docs/146] | `kErrorTrapSpread` built, not run (docs/146) | **gap**: ModTime samples only while a world is live (op 61 hour open, docs/298 §2), so load-phase mod work is not attributed |
| L9 | Mono JIT of mod code | our `[MonoJit]` code region (2 MB, `MaxMethods` 4096) is full in every game run since 11-08 [measured, docs/537 §20, §26] | `0xE643B0` transform | `MaxMethods`/`CodeKB` raised | with 287 mods the hot set is wider; region pressure unmeasured on a mod-heavy install [inferred] |

### 2.2 In game, sim thread

All managed work runs in `MonoScriptHost::Simulate 0xD81840` [verified, docs/28, docs/36 §1-2]:

| Stage | Address | Budget | Measured |
|---|---|---|---|
| ScriptTick | `0xD80BC0` | none | - |
| TaskObjects | `0xD7FC60` | none, every Task-type task each iteration | 72-83 ms/s |
| OneShotObjects | `0xD74800` | none | 0.1-0.2 ms/s |
| ThreadedObjects | `0xD7F8B0` | `5000 x max(1, [0x11CDDB0])` (5 ms at speed 1 if µs, unit inferred), round-robin cursor `host+0x1700`, checked between task runs | 607-640 ms/s, ~20 ms per call: tasks overrun the slice [measured, docs/36 §1 table, docs/28] |

| # | Item | What scales | Where | Exists | Gap |
|---|---|---|---|---|---|
| S1 | Mod tasks (Simulator objects) | one task per mod manager / per object a mod adds | `Simulator.AddObject` -> `ScriptProxy` -> `Simulator_AddObjectImpl`; per-task resume `0xD7F000` thiscall(host, &entry, bool, bool), entry `+8` = task state; yield save `0xE53620` [verified, docs/166 §1, docs/04a §1]; only cooperative yields (`Sleep` -> opcodes `0x1A6`-`0x1A8`, I-119-38) | none per mod | **gap**: no per-mod time, no per-mod budget |
| S2 | Alarms | due alarms per tick | `AlarmManager.Simulate` fires every due alarm in a `while` loop over `mTimerQueue`, no count or time cap; per lot, run first in `Lot.Simulate`; global alarms run by the world lot's task; plain callbacks run inline in that task, `YieldRequired` ones get an `AlarmOneShot` task; repeating alarms collapse missed repeats [verified, decompiled `AlarmManager.cs`, I-199-1, I-247-6]; NRaas `AlarmTask` starts a `FunctionTask`, so NRaas bodies run in their own tasks [I-184-15] | Overwatch chain, EA spread (docs/145, 147) built, not run | inline mod alarm callbacks are not separable from the lot task they run in |
| S3 | EventTracker listeners | listeners per event, mods add many | `SendEvent` -> `ProcessEvent` synchronous, no queue; listeners by id, by key, key 0; a throwing listener is removed; then the wish drain and `LifeEventManager` [verified, docs/619 §2, §5]; ~1.07% of managed samples [I-73-3] | none | small in vanilla; per-mod listener count unmeasured |
| S4 | Pie menu and autonomy `Test` | interactions mods add to every object/Sim at world load | per-object `GameObject.mInteractions` list, `GetAllInteractionsForActor` [verified, decompiled `GameObject.cs`]; each pair tested via `InteractionDefinition.Test` (pie: `InteractionObjectPair.cs`, autonomy: `Autonomy.cs`) [verified call sites]; cost per click and per autonomy search [inferred] | SmellyMods numbers say pie menu 949-1,200 ms -> 207 ms after the STBL merge [community, docs/297 §2], so strings were most of it | unmeasured after the STBL fix; MasterController-style mods add to every Sim |
| S5 | Buff/moodlet hooks, tuning lookups | per mod listener or tuning override | inferred via S3 and ITUN overrides (docs/128: 419 ITUN in a 171-package install) | none | inferred small; measure with ModTime |
| S6 | CAS with heavy CC | part lists, thumbnails, compositors | `CASThumbnails.package`, compositor registry at SimService `+0x104`, no release after `Uncache 0x5EE5A0` [inferred growth 14-52 MB/h, docs/539 §0]; one new 64 KB texture per catalog cell per populate, no native cache, freed only by the `UIImage` finalizer (`0x779430`) [verified, docs/193 §3.3] | Apex `FileListCache`, `FastCasSort`; ours `CasTrim` (`Mode=3` built, not run) | thumbnail dispose; CASThumbnails growth not analysed |
| S7 | Build/buy catalog paging | filter/sort over all products | in memory, no resource read per page [verified, docs/08 §2.5] | - | per-page cost with 20k+ products unmeasured |
| S8 | GC from mod heap | Mono heap 480-544 MB on the many-mods tester's install, 1-3 GC per hour of ~0.8-1.4 s, post-load burst [measured, tester results round 2 §1] | Boehm full cycles only (docs/290) | generational GC lane (docs/290), `[GC] IdleGate` | heap per mod unknown |

### 2.3 Memory and address space

| # | Item | Size | Exists | Gap |
|---|---|---|---|---|
| M1 | Package index memory | ~17.6 B per resource: 3.5 MB for 209,735 CC resources, ~18.5 MB with EA [verified layout, docs/08 §0.1, §3; docs/215 §0] | - | negligible, no work |
| M2 | STBL tables | ~29 KB per table, ~90 MB stock on 3,187 tables, up to ~290 MB [community + inferred, docs/17, docs/297 §2] | done (L4) | - |
| M3 | CAS part cache, catalog heap | catalog 8 MB fixed + heap; CASP per part unknown | - | measure (docs/08 §9 Q4) |
| M4 | Resource cache, read buffers | 200 MB `CacheBudget` + 10 MB overflow; 112-192 MB permanent EA heap from read buffers [verified ctor `0x732910`, docs/08 §2.5, docs/215 §4] | DXVK/bridge moves textures out; Apex `SceneNodeBudget`, Room to Save | fill level under heavy CC never measured |
| M5 | Duplicate resources | duplicate TGIs stay registered, first by priority wins [verified, docs/39 §0.7] | SmellyMods `--merge` (offline), `check-conflicts.ts` | no in-game report; memory small |
| M6 | Mono heap from mod assemblies and statics | part of the ~500 MB heap (S8) | gen GC lane | per-mod share unknown |

Error 12 is practically solved (memory "Error 12 practically solved"); the address-space items here are second rank.

## 3. Ranked features

Gain is for a many-mods player (install like the many-mods tester's). Effort is wall-clock for Claude with agents; game runs listed apart.

| Rank | Feature | Expected gain | Risk | Effort | Owner |
|---|---|---|---|---|---|
| 1 | **ModTime on master + player "mod cost report"** (§5): Steam/EA/retail, 10 Hz default, self vs via-mod split, load-phase sampling | indirect but decisive: names the mod behind ~95% sim load; players can act today | low (sampler, 0.33% of a core at 50 Hz [measured docs/298 §7], ~0.07% at 10 Hz [inferred]) | ~2 h agent + 1 smoke job | ours |
| 2 | **Run the built NRaas/EA spread fixes** (`kOverwatchChain`, `kErrorTrapSpread`, `kEaSpreadLots/Pets/Decay`, docs/145-147) on the mod set | removes known 0.5-0.9 s ticks (load-up, nightly 3 AM) [measured, docs/146; docs/196: Overwatch one-tick alarm 5.4 of 17.4 hitches >= 300 ms per 10 min on IP, chain-off builds] | low (built, default off, correctness repro needed) | ~30 min queue writing + 2 jobs | ours (+ upstream NRaas PR per docs/145) |
| 3 | **Exact per-task-run attribution** at the resume seam `0xD7F000` (+ `0xEA1090` invoke path), diag only: per owner ms/s, run-length histogram, runs per iteration | tells "many short runs" (deferrable) from "one long run" (not deferrable); prerequisite for 4 | medium (new native detour; Frida-proven only, ~300 ns per run [measured, docs/166]) | ~2-3 h agent + 1 smoke | ours |
| 4 | **Per-mod fair share for NPC-scoped mod task runs** (§4), default off | bounds one mod's share of a sim iteration; hitch p99 only, not ms/s; size depends on 3 [inferred] | medium-high (task order, household rule) | ~3 h agent + correctness + timing jobs | ours |
| 5 | **Mod startup attribution + spread** (L8): ModTime in the load phase, then per-mod spread of world-load-finished work like `kErrorTrapSpread` | The many-mods tester's 284 s load iteration is the biggest single number we have [measured]; share per mod unknown | low for diag, medium for spreads | ~1 h diag; spreads per mod later | ours |
| 6 | **CC startup cost in our Apex fork** (L1, L5, L6): load split first (docs/17 M1, M4: launch->menu, menu->live; `pkgs-N` synthetic), then per measured owner: CAS part cache build (`CASPartCacheService::Init 0x5AEDF0`, keep `CASPartCache.package` valid across starts), catalog decode (`Catalog::Init 0xC93000`: compact flags + key record, full product on demand, docs/08 H1), package index read off the single `g_PackedFileLock` (parallel index read at startup) | load time at 60-100 GB; size unknown until the split [inferred: startup decode grows per CASP/OBJD] | medium (engine code, cache staleness when CC changes) | ~1 h measure + ~2-3 h fork build per owner | ours in the Apex fork, default off in `ApexRadiance.toml`, proof, PR to Apex |
| 7 | **Script tuning cache that stays on** (L7, docs/581 §2.1) | skip `FindTunables` reflection scan each boot; grows with mod count; size unknown [inferred] | medium (stale tunables if the signature is wrong; core mods change `GameplayData`) | ~1 h static + 2 boots | ours (managed/Mono side) |
| 8 | **Pie-menu / autonomy Test cost after STBL** (S4): measure with MasterController + many interaction mods; memo only if big | pie lag is the most visible many-mods complaint [community]; after STBL fix unknown | low to measure; a Test memo is risky (Tests read live state) | ~1 h measure | ours |
| 9 | **Thumbnail dispose + CASThumbnails growth** (S6): dispose the old `UIImage` texture on repopulate (`GetThumbnailImage 0x782D00`, finalizer `0x779430`, docs/193 §3.3), bound `CASThumbnails.package` growth | memory in long CAS/Buy sessions with heavy CC [inferred] | low-medium | ~1 h static + ~2 h fork build | ours in the Apex fork (engine part) / ts3fixes (managed dispose), default off, proof, PR |
| 10 | **`GetKeyList` unlocked walk** (L2, `0x4B1AE0`): take the DB array lock (or Apex `FileListCache`'s, if it already has one) | crash risk while packages change (DLC/launcher, cache rebuild) [inferred] | low | ~30 min static + ~1 h fork build | ours in the Apex fork, default off, proof, PR |
| 11 | **Resource cache fill under heavy CC** (M4): measure `CacheBudget` fill and read-buffer heap with the CC set; tune only if full | address space / stutter on cache churn [inferred] | low to measure | ~1 h + 1 job | ours in the Apex fork if a change is needed |
| 12 | **`GetCachedPart` random-part EA bug** (L5, docs/550) | correctness, CC outfits | low | ~1 h | ours (EA bug, behind a switch) |
| 13 | In-game duplicate/conflict report (M5) | support help, no perf | low | ~1 h (extend `check-conflicts.ts` / compat-scan) | ours (tool), conflict tool to Apex per memory |

Engine-side items (6, 9, 10, 11) follow the STBL index route: we build them in our Apex fork, default off in Apex's
config, prove them (selftest + game job on the many-mods set), and PR them to Apex. Task 0 (§7) first diffs the newest
Apex release against our late-October fork checkouts so none of them duplicates a feature Apex added since.

Dropped or parked: package index memory (M1, negligible); our own lookup index (docs/23 #20, Apex covers it); catalog lazy
decode of the filter fields (not viable, docs/23 #10; item 6 keeps the flags resident); moving package indexes out of
process (no gain, docs/215).

## 4. Round-robin + cut-off design for script mods

### 4.1 What can be scheduled

- **Task runs** (TaskObjects, ThreadedObjects, OneShot): yes, at the resume boundary. Not resuming a task this iteration
  is the same as the task having called `Sleep(0)` once more [inferred from the cooperative model, docs/04a §2,
  I-119-38]. A run that has started cannot be cut: no preemption in the Mono 1.2.3 interpreter on the sim thread.
- **Alarm callbacks**: inline ones run inside the lot task (S2) and are not separable at the native level. A managed hook
  in `AlarmManager.Simulate` could hold one due mod alarm for the next tick (leave it due); repeating alarms already
  collapse missed repeats, so one tick of delay loses no fire but shifts its sim time [verified collapse, inferred
  effect]. NRaas alarms already start their own `FunctionTask`, so they fall under task runs.
- **EventTracker listeners**: no. Synchronous, callers expect the result in the same call (docs/619).
- **Pie menu / autonomy Test**: no. UI response and autonomy scoring must see the same state; deferring makes menus
  incomplete.
- **Save, load, world-load-finished, travel**: never.
- **EA and ours**: never deferred. Only assemblies that are neither EA's 11 game assemblies (`MdtGameName`) nor ours.
  Core mods that replace an EA assembly count as game (docs/298 §5) and are never deferred.

### 4.2 Attribution (cheap, at runtime)

- Per task entry, resolve the owner once at its first resume and cache it in a side table keyed by the entry pointer:
  task state `+8` -> `MonoMethod` -> class -> image -> MVID [verified offsets, docs/104 §4, docs/298 §2]. Unwrap
  `ScriptProxy.mTarget`, EA `OneShotFunctionTask` and NRaas `FunctionTask` `mFunction` delegates to the delegate's method
  [verified wrapper names, docs/166, I-184-15]; the owner is the delegate method's assembly.
- Per run: two `QueryPerformanceCounter` reads + one table lookup at the seam [inferred ~100-300 ns; Frida dummy
  ~300 ns per run, docs/166]. At 30 iterations/s and a few thousand runs/s this is well under 0.1% of a core
  [inferred, to be measured in feature 3].
- The sampler (ModTime) stays the reporting tool (it sees inline alarm and listener time too); the seam timer is the
  scheduling input (exact, per run).
- Target scope per task: the proxy target is a `Sim` -> household / selected / active-lot checks via the shared
  `HhExempt` (native) / `Fixes.HouseholdExempt` (managed) predicates; a `Lot` -> occupied by the active household or the
  camera lot = exempt; anything else (global managers, delegates, unknown) = **household** by the rule.

### 4.3 Scheduling policy

- Budget per Simulate iteration for deferrable mod runs: `B` = 6 ms (switch, 2-20) scaled like EA's slice by game speed.
- Deficit round-robin over owners: each owner with a deferrable runnable task gets quantum `B / active owners` per
  iteration, carries a bounded deficit (max 2 x quantum); an owner past its deficit has its next deferrable run held
  this iteration. The owner order rotates every iteration (round-robin cursor of our own, next to EA's `host+0x1700`).
- EA's own task order inside a stage is kept: we only hold runs, never reorder the rest.
- Hard ceiling: no task is held more than `MaxHold` = 2 iterations (switch, 1-10) and never more than 250 ms wall time;
  then it runs regardless. This keeps sim-time drift to a couple of ticks.
- Stand-down: off when S3PP's Simulator probes or S3MM hook the same seam (hook-site map, docs/68); off for the session
  if any held task throws in its next run more often than with the switch off (counter compare).

### 4.4 Cut-off policy

- **Soft (automatic, default off):** §4.3 holding, NPC-scoped runs only.
- **Notice (on with the report):** "Mod X used 41% of the simulation last hour" once per session at a threshold
  (e.g. >= 25% of sim CPU for 2 sim hours), with the package name. Pure information.
- **Hard (never automatic):** a player opt-in `[ModBudget] Test=<package>` runs an hourly A/B like docs/294: odd hours
  hold all of that mod's deferrable runs to the budget, even hours normal; the report says what the mod costs and what
  holding it gains. A permanent "pause mod X" is not offered: skipping a mod's tasks breaks its state (NRaas persists
  settings and per-Sim data), and removing the mod is the honest fix the report can point to.
- **Player whitelist:** `[ModBudget] Never=<package;...>` for mods that must stay exact (e.g. a mod they know is
  timing-sensitive).

### 4.5 Failure modes

- Mods that expect a same-tick follow-up from another task (ordering between two mods' tasks): held runs can reorder
  them across iterations [inferred]. Mitigation: hold per owner (all of one mod's runs together), not per task.
- NRaas StoryProgression timing: its managers are global (unknown scope = household), so they are never held under the
  rule; only the per-Sim tasks it creates for NPCs would be. Its own `AlarmManagerReference` stays untouched.
- ErrorTrap removes orphan OneShotFunctions and tracks every `Simulator` add/remove (I-184-13): holding is not a remove,
  so it should not trigger; check in correctness.
- Saturated PC: when every iteration is over budget, held mods fall behind in sim time; the ceiling stops unbounded lag
  but the mod's sim-time behaviour changes (later fires). That is why NPC-scoped only and default off.
- Long single runs (Overwatch 22-step tick, ErrorTrap load-up scan, StoryProgression passes): not helped. These need
  spreads like docs/145/146, per mod, found by ModTime hitch stacks.

### 4.6 Measurement

- Per owner ms/s (sampler shares x sim CPU; seam totals for task runs), runs per second, run-length p50/p99/max.
- Sim iteration p99 / p99.9 / max and counts >= 250 / 500 / 1000 ms (hitch metrics memory).
- Household responsiveness unchanged: household route-wait, idle %, interaction start latency for selected Sim (must
  equal off arm within noise).
- Held runs, max hold, ceiling hits, held-then-threw counts as counters (docs/registry.md slots via `reserve native`).

### 4.7 Verdict

Feasible at task granularity with one new native seam (feature 3) and a small policy (feature 4). Expected gain is
in hitch length for installs with many multi-task mods, not in total sim load; under the household rule much of the
mod load (global managers) is not deferrable. Do 1-3 first; build 4 only if feature 3 shows that held-able NPC-scoped
runs are a real share (rule of thumb: >= 20% of mod task time) on the many-mods set.

## 5. Tools: ModTime as the measuring stick, a mod cost report

- **ModTime gaps to close before it goes to players** (docs/298):
  - Not on master: branch `feature/modtime-diag-1120` is on the retail test basis. Port to master (Steam 1.67, retail
    1.67; EA 1.69 rows via the TS3A table).
  - **Owner rule inflates wrappers.** Smoke 1 gave Ts3Fixes 38.0% of sim CPU on BP [measured, docs/298 §7] because the
    innermost ts3fixes frame wins over EA frames below it, so EA work called through our patched methods counts as
    ours. Same for any mod that wraps EA methods. Add a self-time column (innermost frame's assembly) next to the
    inclusive one; the player report shows both, and our own self cost gets its own check.
  - Load phase not sampled: sample from first script iteration (not only op 61 hour open); a load block in the summary.
  - Default rate for players 10 Hz (cost ~0.07% of a core [inferred]); 50 Hz for our jobs.
- **Mod cost report** (player-facing, `modtime-summary.txt` + one in-game notice): top 10 mods by share of sim CPU
  (self and with EA code they call), their package, hitches they were on top of, load-phase top 5, and a plain line
  "your sim thread was busy X% of the time" (X >= 90% = the simulation cannot keep up). No advice to delete mods
  beyond naming them; link to COMPATIBILITY.md rows when a package is known.
- **Inventory per run**: `offload-override-scan.ts` + the docs/188 `script assemblies` line, written into the job
  folder so every many-mods result names the exact set.

## 6. Measurement plan and test set

### 6.1 What we have

- Test install script mods: NRaas ErrorTrap, NRaas Overwatch, simler90 gameplay core mod, Store Updates/Fixes
  (docs/COMPATIBILITY.md). Scanned only (not installed): MasterController (+ Cheats, Integration), Register, Careers,
  Moar Interactions, MonoPatcher, SmoothPatch, LagScope, ~20 others (`docs/data/compat-matrix.csv`, 49 of the MTS top
  100 downloaded under `compat-mods/`).
- CC: `tools/cc-gen.ts` synthetic CC (one package per item, own copy of every mesh/texture/thumbnail; kinds objects,
  cas, build, inv, makeup, presets), moved in/out by the queue key `ccSet`. Rodney's own install has 3,906 CC OBJDs,
  209,735 CC resources, ~13 GB of CC textures [measured, docs/08 §1].
- Missing: StoryProgression (+ Career, Relationship, Skill, Extra modules), Traffic, Traveler, Retuner, Woohooer, a
  60-100 GB CC set, a harness key for a whole Mods set (`mods-restore.ts` only knows our three packages).

### 6.2 Set to build

- **Script set "many-mods-s"**: full NRaas set (StoryProgression + 4 modules, Overwatch, ErrorTrap, Traffic, Register,
  MasterController + Cheats/Integration, Traveler, Retuner, Woohooer, Careers), plus the 49 downloaded
  top-100 mods minus known conflicts and minus S3MM (never install S3MM). Downloads from the official pages only, never
  re-hosted (installer rule); Rodney fetches what needs a login.
- **CC set "many-mods-cc"**: `cc-gen.ts --kind all` at 20,000 items (size per item from `--list-sources`; 60-100 GB is
  the target, first check free space with the gate) + Rodney's own CC as a second arm. One package per item (worst
  case for package count). Variant `pkgs-N` (2,000 tiny packages, docs/17 M4) to split package count from resource count.
- **Harness**: new queue key `modSet` (move a named Mods set in and out like `ccSet`, restore by manifest; cleanup
  enforced per memory "mandatory cleanup"), inventory written per job. Bin state apex (bridge-only tests).
- **Save**: Baseline-IP and Baseline-SV (baseline-saves.json) aged 3 sim days with the script set in, so
  StoryProgression has ramped; a copy per arm.

### 6.3 Jobs (each after a smoke, per measurement-smoke rule)

1. Load split (docs/17 M1): vanilla+ours vs +CC vs +script vs +both: launch->menu, menu->live, peak private bytes. 4
   arms x 1 load each, ~10 min per job.
2. ModTime 10 min live per arm (script, both): per-mod shares, load block, hitch stacks.
3. Seam attribution (feature 3) on "both": runs, run-length histogram, NPC-scoped share.
4. Spread fixes on/off (feature 2) on "both": nightly 3 AM window + load-up, hitch counts.
5. Only if 3 says so: `[ModBudget]` off/on (feature 4), 10 min A/B, household responsiveness checks.

## 7. Next week's tasks (week of 2026-11-23)

Times in PHT, wall-clock for Claude with agents; game jobs and nights listed apart. Order = do first to last.

| # | Task | Effort | Game runs | Depends |
|---|---|---|---|---|
| 0 | **Apex diff first:** newest Apex release (docs `features/**`, changelog, `features/` sources) vs our fork checkouts (`game-copy/apex-src` 2026-10-27, `game-copy/rewind-bridge-1115/apex-gi`); list every feature added since that touches resources, packages, CAS, catalog, thumbnails, caches or scripts; drop or rebase items 6, 9, 10, 11 of §3 that it covers; update the fork to that release | ~1 h agent | - | - |
| 1 | ModTime to master: Steam + retail, self vs inclusive column, load-phase sampling, 10 Hz player default, report text | ~2 h agent | 1 smoke (~15 min) | - |
| 2 | Harness `modSet` key + inventory per job; script set downloaded and installed into a set folder | ~1 h agent + Rodney downloads | - | - |
| 3 | CC set: `cc-gen.ts --kind all` 20k (free-space check first), `pkgs-2000` variant | ~1 h agent (gen time extra) | - | disk space |
| 4 | Age Baseline-IP/SV with the script set (3 sim days) | ~30 min agent | 2 jobs (~15 min each) | 2 |
| 5 | Load split + ModTime jobs (§6.3 1-2) | ~30 min queue | 6 jobs (~10-15 min each) | 1-4 |
| 6 | Spread fixes run on the set (feature 2): correctness repros + on/off | ~30 min queue | 2-4 jobs | 2, 4 |
| 7 | Script tuning cache static check (why the signature never matches; cost of `FindTunables` with 287-mod-like set) | ~1 h agent | 2 boots | 2 |
| 8 | Seam attribution (feature 3): `0xD7F000` / `0xEA1090` detour into the asi, diag only, counters reserved | ~2-3 h agent | 1 smoke + 1 job | 1 |
| 9 | Apex fork lane, small items (§3 10, 9): `GetKeyList` lock (unless `FileListCache` already has one); thumbnail dispose + CASThumbnails bound; each default off in `ApexRadiance.toml`, fork selftest, then PR to Apex | ~1.5 h static + ~3 h fork build | 1 smoke + 1 CAS/Buy job | 0, 3 |
| 9b | Apex fork lane, CC startup (§3 6): per owner the load split (task 5) names (CAS part cache, catalog decode, index read lock), default off, load-time A/B on the CC set, PR to Apex | ~2-3 h fork build per owner | 2 load jobs per owner | 0, 5 |
| 9c | Resource cache fill under heavy CC (§3 11): counters in the fork, one job; a change only if the cache is full | ~1 h | 1 job | 0, 3 |
| 10 | Pie-menu / autonomy Test cost with MasterController + interaction mods (ModTime hitch stacks around pie clicks) | ~1 h agent | 1 job | 1, 2 |
| 11 | Decision point: build `[ModBudget]` (feature 4) only if task 8 shows >= 20% NPC-scoped deferrable mod time | - | - | 8 |
| 12 | Offer the many-mods tester the master ModTime build (kit) once task 1 passes its smoke | ~30 min | his session | 1 |

## 8. Open questions

- How much of the many-mods tester's 950-980 ms/s is StoryProgression vs the other 280 mods (his single-mod test, or ModTime round 3).
- Whether `scriptCache.package` ever turns on in a normal player install (L7), and what `FindTunables` costs per mod.
- Whether newer Apex releases already cover catalog / CAS part cache startup or thumbnails (task 0 answers it before
  any fork build).
- Whether the ts3fixes 38% (inclusive) hides a real own cost; the self column in task 1 answers it.
