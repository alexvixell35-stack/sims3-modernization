# 88. Destroy waits: key `0x2000003` (`ObjectService_DestroyById 0x939420`)

Written 2026-10-18. Static only; the game was not run (PLAN "Routing off-thread" item 3, docs/84 §6 step 5). Tools: Ghidra headless decompiles on a private copy of the docs/86 project (`game-copy/ghidra-ds`, copied from `ghidra-g0`, read-only runs), capstone 5 on `game-copy/bin/TS3W.exe` (Steam 1.67) with a direct-call graph over `game-copy/analysis/functions.csv` (40,260 functions; `E8`/`E9` targets at function entries, register-indirect calls counted per function), forward closures (destroy roots 7 hops, job roots 8 hops) and a reverse walk to roots. Live numbers are re-read from existing runs in `probe-out/queue` and `probe-out/keep` (`*-native.json`, after minus before; 10 min profiles = 600 s). `native/` was read, not changed. Script side: `game-copy/decompiled` (grep). Scratch scripts stay in the session scratchpad.

Tags as in AGENTS.md: **[verified]** = read in the disassembly, decompile, source or a run file (address or file given); **[inferred]** = reasoning, to confirm with the §7 probe or the A/B job.

## 1. Summary

- **What the key is [verified, `native/ts3native/rtsites.cpp`].** `0x2000003` = `0x02000000 | RT_DESTROY` (enum value 3), the key `RtEnter` passes to `RtWaitJob` at the `ObjectService_DestroyById 0x939420` site. It is our own closure **#4 "drain on destroy"** (docs/60 §2.2 row 4, S3b): with `[RouteGate] Enforce=1`, every non-worker destroy publishes `g_rgOffSimOpen` (no queued job starts while it is open) and then waits for a running job; the sim thread pumps the job's managed calls while it waits (`WaitAsyncDone`). It waits for **one** job at most: the published count stops `ServeAsync` from starting the next queued job [verified `ServeAsync` start loop]. The D-X apply (`DiscOnDestroy`, docs/84 §7.2) runs right after the wait and relies on it.
- **Why the closure exists, and what is real [verified paths, effect inferred].** docs/60 put the wait there for the object id hash: the job looks objects up with `ObjectService_FindById 0x939560` (no lock, no AddRef) at 15 sites in its direct closure (§3.2), and `DestroyById` erases the object from the hash and drops its references, so the last `Release` can free the object block (`Object_DeletingDtor 0x935DF0` -> `Object_Destruct 0x935860` -> each component's `+0x2C`) while the worker holds the raw pointer or is about to `lock inc` it. That **lifetime** dependency is real. The rest of the destroy is not a dependency: within 7 direct hops `DestroyById` and its owner callbacks touch **no routing state** (no footprint, lot table, lot quadtree, physics quadtree mutator, room graph or route handle function; §3.1), and the only data both sides touch is the hash itself and object memory (§3.3). The hash erase alone is safe for a concurrent reader as long as the erased object's memory stays valid: `ObjectIdHash_EraseNode 0x938D00` rewrites only the predecessor's `+0x10` (or the bucket head) and the count `+0x1028`, never the erased node's own `+0x10` [verified decompile].
- **So the wait is broader than the dependency.** What must wait is only the free (the last `Release` and the component destructors it runs), not the destroy. The events, the owner callback, the update-list removal, the destroyed flag and the hash erase can run while a job runs; script `Destroy()` already runs the owner callback (`0x798D40`) unwaited during jobs today, before it reaches `DestroyById` (§2).
- **Who destroys during jobs [verified callers, rates partly inferred].** 16 direct callers (§2). The hot ones are reached from the sim stages that run right after the script task stage that STARTs the jobs (docs/82 §8 order: script tasks `0xD81948` -> GC -> finalizers `0xD81A09`/`0xD81A30` -> `Animation_SACSPass 0xD81A70` -> ... -> `Locomotion_Update 0xD81C5C` -> `Animation_UpdateActors`): script `Simulator.DestroyObject` (`VisualEffect.Dispose`, `StateMachine.Destroy`, `FinalizerTask`, props: 223 C# call sites), `Animation_DrainDeferred 0x571A50` (animation props), effects (`IC_World_ObjectDetachAutoDestroyEffects` -> `0xC63E40`, locomotion effect list `0x84CE30`). Effects and state machines have no routing footprint and are never route targets [inferred]; the per-caller split needs probe P-G (§7).
- **Live size [verified run files].** P0 (`h6-m1-2026-10-17-bp-p0-async`): 603 destroys inside a job, every one waited, 5.24 s = **8.7 ms/s = 11.8%** of the 44.4 s sim wait. With design D on (`clean-night-2026-10-17-bp-env-on`): 681 waits, 5.28 s = 8.8 ms/s (9.2% of 57.3 s); `bp-s2-on` 621 / 5.12 s, `bp-s2-shadow` 631 / 5.14 s; IP 92-173 waits, 1.9-2.6 s (3.1-4.3 ms/s). Mean wait 7.8-8.8 ms in BP against a mean job of 5.1-6.1 ms; 14-28 ms in IP (mean job 10.6 ms) (§4). `vfRtDestroyOffSim` = 0 and `vfRtDestroyInInvoke` = 0 in all 16 async runs.
- **Recommendation: design G, a destroy graveyard (§5).** At `RT_DESTROY`, when a job runs and D-X has no queued record for that id: take one reference (`ObjectService_FindByIdAddRef 0x9395A0`), park the pointer, and let the original destroy run **without waiting**; release the parked references at the next IC point where no job runs (the same drain points as design D), with `g_rgOffSimOpen` published so no job starts during the release. Keep the wait for: D-X records queued for the id, a full graveyard, closing/teardown, and non-sim threads. **Gain cap 8.8 ms/s BP (5.2-5.3 s per 10 min), 3.1-4.3 ms/s IP; realistic 3-6 ms/s BP** (the sim reaches its next routing touch of the same iteration sooner and may wait there; gains do not add). **Effort ~1.5 days.** H2 (docs/86) does not cover it: the destroys run outside the `Locomotion_Update` window.

## 2. Callers, rate and stage

Every direct call of `0x939420` [verified caller index; roots from the reverse walk]:

| Caller | Reached from | What it destroys | Stage / rate class | Tag |
|---|---|---|---|---|
| host `ScriptHost_GetWorld` vtable `+0x17C` -> `+0xC` (no direct call; `IC_Simulator_DestroyObjectImpl 0x76D120`) | C# `Simulator.DestroyObject` (223 call sites): `VisualEffect.Dispose`, `StateMachine.Destroy` (every animation state machine), `FinalizerTask` (finalizer-thread disposals replayed on the sim), `ScriptObject.Destroy` (`World.RemoveObjectFromObjectManager` = `0x798D40` first, docs/84 §7.2), props (food, trays, held objects) | effects, state machines, temporary props, any object | script tasks, finalizers: **right after the job STARTs of the same tick** | callers verified (grep, IC body); host slot target not traced (docs/50 G) |
| `Animation_DrainDeferred 0x571A50` | `Animation_SACSPass`, `Animation_UpdateActors` | deferred animation props: `0x798D40` (owner callback, event `0x0C`) first, then the animation component hook `0x941BC0`, then `DestroyById` | SACS pass and actor update, per tick | verified decompile |
| `0x84CE30` (list at `this+0xA4`) | five callers `0x84EA00`, `0x84F2B0`, `0x8504D0`, `0x850E20`, `0x851280` (locomotion family) | objects with an Effect component on a locomotion list | locomotion, per route segment change [inferred] | verified decompile |
| `0xC63E40` | `IC_World_ObjectDetachAutoDestroyEffects 0x797B20` | objects with an Effect component (`g_CompBit_Effect`) | script IC | verified decompile |
| `0xB39770` | `IC_World_ObjectDesigner_SetObjectImpl 0x7875D0` | CAS/design preview object | rare (UI) | verified callers |
| `0xC70150` (owner list removal, with destroy flag) | `0xC71310`, `0xC71A90` (lot unload), and the owner callbacks below | lot objects | lot unload (no jobs: travel/teardown drain) | verified decompile |
| `LotObjects_LoadFromStream 0xC70320`, `WorldManager_Shutdown` callers `0xC63ED0`/`0xD680D0`, `ObjectService_DestroyAll 0x939510`, `0xD68320`/`0xD68540`, `0x5815F0`, `0xCDBD40`, `0xD27F50`, `0xD4C790`, `0xECBE70` | world/lot load and shutdown, build tools (`IC_UserToolUtils_*` at 5 hops), others | placeholders, all objects | load/shutdown (no jobs), rare | verified callers; roles of the last five not read |

**Rates.** Destroys during a job: 1.0-1.2/s in BP, 0.15-0.3/s in IP [verified, `vfRtDestroyInJob`]. Total destroys: ~35/s in an unseeded BP profile (`native-batch3-2026-10-16-bp-native-batch3`: `idcErases` 25,061 in 12 min; `IdcEraseHook` counts every `EraseNode` call whether the cache is on or off [verified `idcache.cpp`]) [verified run file; other build, other save state]. With jobs running 14% of the time (`asyncUs` 85.7 s / 600 s in `bp-env-on`), a uniform rate would put ~5/s inside jobs; the observed 1.1/s says destroys cluster outside job time, or the batch3 run churned more [inferred, open question 2]. No run records which caller a waiting destroy came from; P-G (§7) adds that.

**Managed reach of the destroy.** `DestroyById` reaches `GlobalHandlerChain 0x936C90` at hop 1 for events 5 and 6 (and 10 / `0x0C` in the owner callbacks), a native listener table with no handlers registered for these events (docs/50 G) [verified call, empty table inferred]. No `World_FireEvent`, remote call, `ScriptHost_InvokeDelegate` or runtime invoke within 7 direct hops [verified]. The managed reach is in the **free**: `Object_Destruct` calls each component's `+0x2C`; the script component releases its managed object through host vtable `+0x10C` (`ScriptComponent_ReleaseScriptObject 0x94EE30`, docs/50 H) [verified docs/50]. One `Lock_Acquire` is reached (hop 2, `0xAE00A0` on the free list lock `0x1068820`, a pool allocator for the lot's shell-object message; leaf) [verified].

## 3. What destroy touches, and what the job reads

### 3.1 Destroy, 7 hops

Roots: `DestroyById 0x939420`, its owner callbacks (world object manager `0xAA8FF0`, lot `0xACE060` -> `0xACD7B0`; both end in `0xC70150`), script `RemoveObjectFromObjectManager 0x798D40`, `Object_DeletingDtor 0x935DF0` and the footprint component's `+0x2C` `0x947A70`. 349 functions, 143 register-indirect calls [verified closure].

| Hop | Content | Writes | Tag |
|---|---|---|---|
| 1 | `ObjectId_Lookup 0x939100` (the id), `lock inc` of `obj+4`, event 5, owner `obj+0x30` (`0x68EC50`) vtable `+0x3C`, `ObjectService_RemoveFromUpdateList 0x938CA0` (swap-remove in `svc+0x11D4`, index in `obj+0x28` low 22 bits), `obj+0x28 |= 0x400000`, `ObjectIdHash_EraseNode 0x938D00`, event 6, two `lock dec` with the deleting destructor (slot 0, argument 1) when a count drops from 1 | update list, `obj+0x28`, predecessor `+0x10` or bucket head, `svc+0x1028`, `obj+4` | verified decompile |
| 1-2 (owner) | world manager: event 10, `0xC70150`; lot: render flags `lot+0x425`/`+0x334`, event 10, `0xACD7B0`, `0xC70150` | `0xC70150`: owner vector `+0x14/+0x18` (`0x5C0970`), two owner id maps (`0xC6FF80`, `0xC70030`: chain unlink and `EA_Free`), `obj+0x30 = 0` (`0x935760`), one `lock dec`; `0xACD7B0`: the lot-membership component (`0x11ECF08`) bits `+0x2C` (`0xC899F0`) and bytes `+0x30..+0x35` (`0xC89730`), the lot's per-cell id vectors at `lot+0x2D0` (`0xACCD00`, `memmove`), a handle registry unlink (`0xC0AD80`), the lot's buildable-shell and greenhouse objects under the lot critical section `lot+0xF0` (only when the destroyed object is one of them; posts message `0xA80B189`) | verified decompile |
| 2-7 | allocator, lot render pending lists (`LotRenderer_ErasePending 0xA8F0A0`), render message posts (`RenderMessageBuffer_Post 0x593E60`), lot lookups (`WorldManager_FindLotById 0xC60D30`, `Lot_RoomLookup 0xA8DF80`, `Lot_TileLookup 0xAB7ED0`: reads) | lot render and shell state | verified closure |

**Routing state: none.** The closure contains none of `LotRouting_RemoveObjectFootprints 0x635700`, `RemoveFootprintLocked 0x82A0C0`, `LockedLotUpdate 0x82B450`, `UpdateObjectFootprintCore 0x8321F0`, `RemoveDiscourageFootprint 0x834860`, `RemoveLot 0x829D50`, `UpdateSimFootprint 0x833560`, `Locomotion_Notify 0x947590`, `0x947160`, the quadtree mutators `0x6823E0`/`0x682080`/`0x6819B0`, `LotQuadTree_Insert 0x666C50`, `0x6675B0`, `LotTable_GetOrInsert 0x62EAB0`, `LotTable_Contains 0x628A90`, and no `+0x918` acquire [verified, direct calls]. This matches docs/84 §7.2 (destroy removes no routing footprints) from the other side. The component destructors (`+0x2C`, register-indirect) are not followed here; the footprint component's only releases its polygon objects (docs/84 §7.2) [verified]. Every destructor runs in the free, which design G keeps at a no-job point.

### 3.2 The job's object reads, 8 hops

Roots `IC_Route_Plan 0x765160`, `Route_PlanLocked 0x82C6E0`, `RoutingJob_Execute 0x81B2A0`: 1,104 functions [verified closure, direct calls]. Functions in it that call `FindById` (all on the worker during a job):

| Function (hop) | Called from (in the job) | Object | Pattern | Tag |
|---|---|---|---|---|
| `0x81A340` (1) | `RoutingJob_Execute` | ids from the routing options (ignore/goal lists) | lookup | verified call |
| `Route_PlanCommon 0x7606E0`, `0x750260`, `0x751FF0` (2-3) | the plan | the routing object (the Sim) and its components | lookup, `Object_GetComponent` | verified calls |
| `0x75D920` -> `0x74BE00`, `0x752480` (2-3) | `Route_StartPointsFromPosition` | start / goal objects | lookup, component, `RefPtr_Assign` | verified calls |
| `0x65BC20` (3) | `PathPlanner_PlanHierarchical` | portal objects | lookup | verified call |
| `0xB74A20` (4) | `Route_GoalStep` -> `0xB793F0` -> `0xB74D10` | goal footprint owners | lookup, component, transform `0x958EE0` | verified calls (docs/60 §1.4) |
| `0x636BF0` (6), `0x643010` / `0x6430F0` / `0x643180` (6, from `0x643E60` with `LotQuadTree_QuerySegment`), `0x627C40` (8, from `0x6351A0`, `0x634930`) | lot graph build, segment queries, portal edge cost | ids stored in footprints and portals (any object with a routing footprint) | **lookup, then `lock inc obj+4`, reads (`obj+0x18..+0x24` resource key for "StairsHelper"; lot-membership component `+0x18`/`+0x20`), `lock dec` with the deleting destructor on the last reference** | verified decompile `0x643010`, `0x627C40` |
| `Portal_LockState 0x64F2C0` (7) | `Portal_IsLocked` / `Portal_IsDiscouraged` | portal owner | lookup | verified call |

Plus the footprint records' own id resolution through slot `+0xA8` (`0x80C8C0`, docs/84 §7.2), not in this direct closure.

### 3.3 Overlap

The only functions both closures share are readers or generic helpers: `ObjectId_Lookup`, `FindById`, `ObjectService_Get`, `Object_GetComponent`, the allocator, `RefPtr_Assign`, lot lookups [verified intersection: 117 functions; the ones read are readers or allocator code]. None of the destroy's writes (§3.1) is read in the job's direct closure: `0xACCD00`, `0xC899F0`, `0xC89730`, `0xC89EE0`, `0xC0AD80`, `0x5C0970`, `0xC6FF80`, `0xC70030`, `0x938CA0`, `0x68EC50`, `0x935760`, `0x749660` (the lot-membership component getter) are absent [verified]. `0x627C40` reads the lot-membership component at `+0x18`/`+0x20`, not the `+0x2C..+0x35` the owner callback writes [verified both decompiles]. Readers through vtables are not excluded [inferred none].

**The three job-side hazards a destroy creates, and which survive design G:**

| Hazard | Today without the wait | With G | Tag |
|---|---|---|---|
| H-a: chain walk in `ObjectId_Lookup` while `EraseNode` unlinks | safe while the node's memory is valid (erase does not touch the node's `+0x10`; x86 stores are atomic and ordered) | safe: the parked reference keeps the memory | verified decompile, x86 order |
| H-b: free while the worker holds the pointer (between `FindById` and its `lock inc`, or a lookup without AddRef followed by component reads) | use after free, or a block reused by a new object | removed: the count cannot reach 0 while parked | verified pattern, race inferred |
| H-c: the worker's own `lock dec` is the last one (the sim dropped the hash and owner references meanwhile) | `Object_Destruct` on the worker: component destructors and the managed release run off the sim thread | removed: the parked reference is dropped on the sim at a no-job point | verified pattern |
| H-d: a lookup inside one plan finds the object, a later lookup of the same id misses | possible | still possible: the plan sees the destroy "between" two lookups; the planner treats a missing object as absent (returns 0 at each site) [inferred] | inferred |

H-d is new behaviour versus the waited destroy, where the whole plan saw the object. It matches what a sync plan would see if the destroy had come first, except within one plan. P-G's shadow counter `dgJobMissParked` (§7) measures it.

## 4. Cost split

| Run (build) | Destroys in job | Wait s | ms/s | Share of `blockUs` | Mean wait ms | Mean job ms |
|---|---|---|---|---|---|---|
| `h6-m1 bp-p0-async` (0.11.4, P0) | 603 | 5.24 | 8.7 | 11.8% | 8.7 | 6.1 |
| `h6-m1 bp-env-on` (0.12.x, D on) | 356 | 4.02 | 6.7 | 8.4% | 11.3 | 7.3 |
| `clean-night bp-env-on` | 681 | 5.28 | 8.8 | 9.2% | 7.8 | 5.1 |
| `clean-night bp-s2-on` | 621 | 5.12 | 8.5 | 9.1% | 8.2 | 5.5 |
| `clean-night bp-s2-shadow` | 631 | 5.14 | 8.6 | 9.2% | 8.2 | 5.2 |
| `h6-m1 bp-d-async-3` / `-4` | 747 / 720 | 6.55 / 5.71 | 10.9 / 9.5 | 15.1% / 12.5% | 8.8 / 7.9 | 5.7 / 5.8 |
| `clean-night ip-env-on` | 173 | 2.25 | 3.8 | 4.5% | 13.0 | 10.6 |
| `h6-m1 ip-env-on` | 92 | 2.57 | 4.3 | 5.1% | 27.9 | 15.8 |
| `clean-night ip-ab-routeasync` (2026-10-17 / -18, half the load async) | 94 / 137 | 1.89 / 2.03 | 3.1 / 3.4 | 6.4% / 4.8% | 20.1 / 14.8 | 11.9 / 11.7 |

All runs: `vfRtDestroyInJob` = waits (every destroy inside a job waits), `vfRtDestroyOffSim` = 0, `vfRtDestroyInInvoke` = 0, `g11EndDestroy` 1-11 [verified]. The mean wait exceeds the mean job by 1.3-2.6x: a destroy lands in long jobs more often than in short ones (length-biased sampling), and many destroys come in the stages right after the tick's START, i.e. near the start of a job [inferred, stage order verified docs/82 §8].

Our stub's own cost outside jobs: a few compares and two interlocked operations per destroy (~35/s), negligible [inferred].

## 5. Design options

Rules as in docs/84/86: sync reads the sim needs now cannot move to the worker; managed reach stays on the sim; a lock-free change needs every job-side reader of the written data to be safe; gains do not add.

| # | Option | What | Risk | Effort | Gain cap [inferred] |
|---|---|---|---|---|---|
| Q | **Queue the destroy to the worker in order** (like D's co-location idea) | the worker runs `DestroyById` between jobs | the free reaches managed code (script component release) and the owner callbacks post render and lot messages; the sim and script expect the object gone at once (`ObjectGuid` invalid, `FindById` null). Not possible | - | not applicable |
| DF | **Defer the whole destroy on the sim to the next IC point with no job** (D's pattern) | record the id, return as if destroyed, run the original later | script reads the object after `Destroy()` (it is still in the hash: `FindById` finds it, `IsValid` true, events 5/6 late, update list still ticks it); event order against later creates; a create that reuses the slot or id; D-X ordering with Remove records. High | 2-3 d | 8.8 ms/s, same as G |
| S | **Split destroy: now the non-routing part, routing removal later** | as asked | destroy has no routing part (§3.1); the part that must wait is the free, which is what G defers | - | = G |
| L | **Lock-free skip when the object has no routing presence** | skip the wait when the object has no footprint component (and is no portal / route target) | the job's lookups are not limited to footprint owners (§3.2: route targets, ignore lists, portal owners, the routing Sim); presence cannot be decided from the object alone, and a wrong guess is a use after free. Effects and state machines would pass a class test, but a class allow-list is brittle | 1 d | the effect/state-machine share of 8.8 ms/s (unknown until P-G) |
| P | **Gate policy change: no wait at all** | drop closure #4 | H-b and H-c (use after free, destructors on the worker). Not acceptable | 0.2 d | 8.8 ms/s |
| H2 | **Covered by docs/86 H2?** | - | no: H2 opens its window only around `Locomotion_Update`; the destroys come from script tasks, finalizers, `Animation_SACSPass` and `Animation_UpdateActors` (§2, docs/82 §8 order), and H2's held jobs meet them later (docs/86 §5 note) | - | 0 |
| **G** | **Destroy graveyard (recommended)**: park one reference, destroy now, release when no job runs | §5.1 | §5.2 | 1.5 d | 8.8 ms/s BP, 3.1-4.3 ms/s IP; realistic 3-6 ms/s BP |

### 5.1 Design G

In `RtEnter` `RT_DESTROY` (existing stub, no new hook site; `thiscall(svc; idLo, idHi)`, `ret 8`, id at `slot[1]`/`slot[2]` [verified D-X]):

1. **Flush first** when this is the sim thread, the graveyard is not empty and no job runs (below).
2. **Park** when all hold: `rgEnforce`, `[DestroyGrave] Enabled=1 Mode=1`, this is the owner (sim) thread and not inside a job callback (`g_inInvoke`), `g_async.state == A_RUNNING` (read after `g_rgOffSimOpen` is raised, as today), not `rtClosing`, D-X has **no** queued record for this id in the current world (else keep today's wait and `DiscOnDestroy`), the graveyard has room (cap 512). Then `ObjectService_FindByIdAddRef 0x9395A0 (svc; idLo, idHi, &obj)` (`ret 0xC` [verified D-X]); if found, store `obj` and the job sequence number in the graveyard and skip `RtWaitJob`. The original then runs as today: events, owner, update list, flag, erase, its two `Release`s; the count stays at least 1 (ours).
3. Otherwise (no job, not sim, closing, D-X record, full): today's path unchanged (wait, `DiscOnDestroy`).

**Flush** (sim thread, IC points only): raise `g_rgOffSimOpen`, read `g_async.state`; if a job runs, drop the count and leave the graveyard for the next point; else release every parked object with `RefPtr_Release 0x7460F0` (the game's own `lock dec` and deleting destructor [verified D-X use]) in park order, then drop the count. Flush points: the `RT_DESTROY` stub entry (above), `GetOptionHook` READY/START polls, the D drain points (`DiscPoll` callers), and, waiting for the job first, `RT_SAVE`, `RT_CLOSING`, `RT_TEARDOWN` (travel, quit, world init/teardown) and graveyard overflow. **Never** at `LockAcquireHook`/`RgEnter` gate entries: the free reaches managed code (script component release), and a plain acquire can sit inside a native caller mid-update (docs/84 §7.1 argument). Because every `DestroyById` entry with no job flushes first, `ObjectService_DestroyAll` and the shutdown destroys empty the graveyard before any object service teardown [inferred: jobs are refused while closing, so the first shutdown destroy flushes].

Why the flush may run Object_Destruct there: an extra holder delaying the free is a vanilla state (the discourage ICs, `0x643010`, `0x627C40` and `0x84CE30` all hold a temporary reference across calls that may destroy) [verified patterns], and the deferred `Release` runs on the same thread with no job running, the conditions the free has today after the wait.

### 5.2 Risks

| Risk | Handling | Tag |
|---|---|---|
| Dangling pointer on the worker (H-b) | the parked reference keeps the block until a no-job flush; a job that starts after the destroy cannot find the object (it left the hash before the job started, since starts are blocked during the destroy) | design; start rule verified |
| Object reuse after free | the block is not freed while parked, so no new object can occupy it during the job. Ids: a parked object is out of the hash, so `ObjectService_NewUniqueId` could hand out its id again (64-bit random, negligible), and stream loads (`ReserveId`) run only with no job (docs/84 §7.2); the graveyard keys by pointer, not id | verified id code (docs/84), probability inferred |
| Refcounts | one `FindByIdAddRef` and one `RefPtr_Release` per park, both the game's own atomic code; the worker's own pairs can no longer reach 0 (H-c) | verified functions |
| Visible lag of the free | component destructors (render node, physics shape `Realm_RemoveShape`, effect stop, managed release) run at the next IC point, usually the same tick [inferred]; a destroyed object's physics shape can stay in the quadtree for the rest of the running job, so the job may route around it (stale by one job, as D) | inferred; T-G3 measures age |
| H-d (object vanishes between two lookups of one plan) | counted in shadow (`dgJobMissParked`); the lookup sites return "not found" paths [inferred] | inferred |
| D-X order | ids with queued discourage records keep today's wait and apply, so D-X's guarantee is unchanged | design |
| Memory | up to 512 parked objects for at most one job plus one poll interval | inferred |
| A destroy from inside a job callback (`g_inInvoke`) or off the sim | unchanged (count, no park): 0 in every run | verified counters |

## 6. Offline proof (steerro-test style)

1. **steerro-test section I (new):** map TS3W.exe as the other sections. Build a fake object service in our memory (1,033 bucket heads, the miss sentinel at `+0x1024` and its pointer at `+0x1034`, count `+0x1028`, an empty update vector `+0x11D4/+0x11D8`) and fake objects (vtable whose slot 0 is our deleting destructor: counts the thread, poisons the block with `0xDD`, frees it; `obj+0x30` owner NULL; `obj+0x28` index 0; ids at `+8/+0xC`; a canary at `+0x18`). The game's global handler table for events 5/6 is empty in the mapped image (no init ran) [inferred, assert it]. A "worker" thread loops the game's own `ObjectService_FindById 0x939560` over a set of ids, then `lock inc`, reads the canary, `lock dec` (the `0x643010` pattern) and records any poisoned read and any destructor call on its thread. The main thread sets `g_async.state = A_RUNNING`, runs the game's `DestroyById 0x939420` through our `RT_DESTROY` stub for half the objects, ends the "job", runs a flush. Pass: graveyard on: 0 poisoned reads, 0 worker destructors, destructor count = destroyed count only after the flush, every count 0 at the end, hash count `+0x1028` right; graveyard off (wait removed, the unsafe option P): poisoned reads or worker destructors > 0 over the run as the negative control. Seeds 1, 7, 99, 2026 and the default.
2. **selftest:** `TestDestroyGrave`: park rules (no job, D-X record queued, closing, full, in-invoke, off-sim), flush refused while `A_RUNNING`, flush order, overflow waits, counters; `TestRouteEnforce` and `TestDiscourageDX` unchanged.
3. **tools/emu spec** `destroy_grave.py`: the stub path with a stub `0x9395A0`/`0x7460F0` checking the conventions (`ret 0xC`, `ecx` = service).
4. `node tools/hook-sites.ts --check` (no new site: `0x939420`, `0x9395A0` and `0x7460F0` are called, not hooked), `node tools/check-docs-code.ts`.

## 7. Probe P-G and live jobs

**P-G (count only, same build, `[DestroyGrave] Mode=0` = shadow):** at each `RT_DESTROY` during a job, keep today's wait, and record (a) the caller key (return address `slot[0]`, top 32 like `blockKey`), (b) the object's component bits (footprint `0x11E8FA8`, effect, animation, script; `obj+0x2C` mask [verified `Object_Destruct` reads it]), (c) whether D-X had records. In shadow also park the reference (it is harmless while the wait still runs) and count `dgJobMissParked`: worker `ObjectId_Lookup` misses for an id that is parked (through the existing `[IdCache]` lookup detour, worker thread only).

Counters (reserve in docs/registry.md): `dgParked`, `dgFlushed`, `dgPendingMax`, `dgAgeMaxUs`/`dgAgeSumUs` (park to release), `dgFlushRefused` (job running at a flush point), `dgKeptWaitDisc`, `dgKeptWaitFull`, `dgKeptWaitClosing`, `dgJobMissParked`, `dgKey_0..31`/`dgKeyN_0..31`, `dgKindFootprint`/`Effect`/`Anim`/`Script`. Tripwires: **T-G1** `dgCountLow` (a parked object whose count `obj+4` is below 1 at flush, read before our release) = 0; **T-G2** `dgFlushInJob` = 0; **T-G3** `dgAgeMaxUs` under 100 ms (one job plus one poll).

Live, in `queues/clean-night*.json` only (clean machine), queue + D + D-X on, seeded:
1. **`bp-dg-shadow`** (Mode 0, 10 min): caller and kind split of the 8.8 ms/s; `dgJobMissParked`; pass = game alive, 0 watchdogs, T-G2 = 0.
2. **`bp-dg-ab`** and **`ip-dg-ab`** (in-load `--ab destroygrave`, bundle rule): Mode 1 halves `vfRtDestroyWaitUs` near 0 except kept waits, total `blockUs` and iterations/s against Mode 0, T-G1..T-G3, 0 crashes, stuck Sims logged only; any crash in `0x939xxx`, `0x935xxx` or `0x64xxxx` stops the run (crash-cause rule).
3. **`bp-pet-guard-dx-dg`**: the `pet-guard-cancel` repro with G on: `dgKeptWaitDisc` > 0 and D-X's T-D5 still holds.
4. Repro for churn: `repros.json` `rt-s2b-sweep` already runs an object create/destroy churn twice (both Mode slices see destroys and quadtree writes) while Sims route, and `alloc-dupfree` churns objects, effects and Sims [verified repros.json]; use `rt-s2b-sweep` with `--ab destroygrave` as the targeted repro, no new agent action needed.
Re-run P0 after (gains do not add).

## 8. Open questions

1. The caller and object-kind split of the 8.8 ms/s (P-G, I-88-4); if effects and state machines carry almost all of it, option L for those two classes becomes a cheaper fallback.
2. Why destroys land in jobs at ~1.1/s against ~35/s total (I-88-5): clustering by stage, or a different churn in the batch3 run.
3. H-d frequency and its effect on plans (I-88-6).
4. The host slot behind `IC_Simulator_DestroyObjectImpl` (`ScriptHost_GetWorld` vtable `+0x17C` -> `+0xC`) is assumed to end in `DestroyById` (docs/50) and does not do more first [inferred, I-88-7].
5. Component destructors (`+0x2C`) of other classes than the footprint component, run at flush time: no job runs then, so their reach does not matter for safety, only for the flush points' managed-safety (IC points) [inferred].

## 9. Ledger

Rows `I-88-1` .. `I-88-8` in docs/51 §19; the standalone build (§10) in docs/51 §202.

## 10. Design G built standalone (`[Discourage] DestroyPark`, counters v76, 2026-10-21)

Built offline on the 0.36.9 base, off by default (`DestroyPark=0`), not run in game. History: the first standalone build (`[DestroyGrave]`, `destroypark.cpp`, ts3native 0.30.0, counters v51, later reserved as v52) is commit `14a25ef` on an unmerged branch; P5c (0.35.0, docs/94 §22.11) then built the same graveyard in `p5c.cpp`, but only for destroys inside a `FreeLock` window. This build revives the standalone variant on P5c's graveyard instead of a second copy: one admission test more in `P5cPark`, no new file, no new hook site.

**What it does.** `RtEnter` `RT_DESTROY` (`0x939420` stub, `rtsites.cpp`) already calls `P5cPark(idLo, idHi)` before D2 and the job wait. `P5cPark` now admits a destroy when either a P5c window is open (unchanged) or **`dpReady` and `dpMode == 1`, an async job is running (`g_async.state == A_RUNNING`) and the world is not closing**. Admitted destroys follow P5c's rules unchanged: kept wait (today's path) for a non-owner thread or a job callback (`p5cKeptWait[2]`), a queued D-X record for the id in this world (`[0]`; design U's records share D's queue, so they keep the wait too once U merges), a full graveyard of 512 (`[1]`), an id not in the hash (`[3]`); otherwise `ObjectService_FindByIdAddRef 0x9395A0` parks one reference and the destroy runs at once, with no `RtWaitJob`. Install: `InstallDiscourage` reads `[Discourage] DestroyPark` right after its `rgEnforce` test, before `Enabled`, so it needs `[RouteGate] Enforce=1` (the wait it removes exists only then) and neither design D, P5c nor RouteSnap.

**Release points** (0.36.19, crash I, docs/67 §11: the `RT_DESTROY` entry releases only while the world closes, since `DestroyById` has native callers mid-update such as `0x84CE30`; the rest as written here) (P5c's `P5cFlush`, unchanged except the thread rule below): every `RT_DESTROY`, `RT_SAVE`, `RT_CLOSING` and `RT_TEARDOWN` stub entry after its own wait, and both `GetOptionHook` paths (`Route.Plan` START / READY / CANCEL polls, beside `DiscPoll`). Each raises `g_rgOffSimOpen` first and refuses while a job runs (`p5cFlushRefused`), so the release runs with no job and none can start: the state the free has today after the wait. Save: `RT_SAVE` waits for the job, then flushes. Travel / quit: `RT_CLOSING` drains D, sets `rtClosing` (no START, no new park), waits for the job, then flushes. World teardown during a job keeps the graveyard (flush refused) for the next point; `ObjectService_DestroyAll` and the shutdown destroys pass `RT_DESTROY`, which flushes first.

**Thread rule tightened (P5c too).** `P5cFlush` released on any non-worker thread outside a job callback; it now releases only on `g_async.owner` (the thread that STARTs jobs, the sim) outside a job callback. The free runs `Object_Destruct`, the component destructors and the managed release of the script component (§2), which belong on the sim. Every flush point of a live run is on the sim already (`vfRtDestroyOffSim` 0 in all runs), so this changes nothing measured; a non-sim save or destroy now leaves the graveyard for the next sim point.

### 10.1 Safety argument

| Claim | Why | Tag |
|---|---|---|
| The job never reads freed object memory (H-b) | The job's object reads (§3.2: `FindById` raw pointers, the `0x643010` / `0x627C40` `lock inc` pattern, component reads) all happen while `A_RUNNING`. A destroy in that time parks one reference before `DestroyById` runs, so the destroy's two `Release`s cannot reach 0; the block stays until a flush, and every flush refuses while `A_RUNNING` (read after `g_rgOffSimOpen` is raised, the Dekker pair with `ServeAsync`'s start). A job that starts later finds the id gone from the hash (the erase ran before it could start: `g_rgOffSimOpen` is held across the destroy) | verified code path; offline proof below |
| No destructor runs on the worker (H-c) | The worker's own `lock dec` can no longer be the last one: our reference is dropped only on the owner thread at a flush | verified code path; offline proof below |
| Refcount semantics | One `FindByIdAddRef` (`lock inc obj+4`) per park and one release `0x7460F0` (`lock dec`, deleting destructor at 0) per flush entry, both the game's own code; an extra holder delaying a free is a vanilla state (§5.1) | verified functions (docs/84 §7.2) |
| Double release is impossible | T-G1: a parked object whose count is already below 1 at the flush is counted (`p5cCountLow`) and leaked, not released | design, selftest |
| Release order | Park order (FIFO), on one thread; a release that destroys a child re-enters the stub, whose nested flush finds the list already taken | design, selftest |
| D-X order kept | An id with a queued discourage record keeps today's wait, and `DiscOnDestroy` applies its records under it; a parked id has no record at park time and none can appear before `DiscOnDestroy` (same thread) | design, selftest |
| What stays unsafe: H-d | A plan can see an object in one lookup and miss it in a later one (unchanged from §3.3; the planner's miss paths return "absent") | inferred, I-88-6 |
| P4 / RouteSnap with the standalone park | A parked destroy skips D2's clone-run wait as in a P5c window: the clone run reads none of `DestroyById`'s writes (I-94-372) and `g_nrN[2]` still counts the destroy, so a P4 answer that saw it is refused | same argument as P5c (docs/94 §22.11) |

### 10.2 Counters (v76, `docs/registry.md`; built as v77 at 0x3174, renumbered at the 0.36.11 merge)

`dpReady` (+0x31DC), `dpMode` (+0x31E0, switch, `--ab destroypark`), `dpParked` (+0x31E4, parks outside a P5c window), `dpKeptWait` (+0x31E8, admitted outside a window but waited). The graveyard counters are shared with P5c: `p5cParked` (all parks), `p5cKeptWait[4]`, `p5cFlushed`, `p5cFlushRefused`, `p5cPendMax`, `p5cAgeMaxUs` (T-G3), `p5cCountLow` (T-G1). At the 0.36.11 merge it became v76 after v74 (design U) and v75 (M1Retry): 0x31DC-0x31EB, `destroypark` at +0x31E0 in `profile-world.ts counterSwitches`; `[AvoidRO]` moved to v77.

### 10.3 Tests (2026-10-21, offline)

| Test | What | Result |
|---|---|---|
| selftest `TestDestroyPark` | fakes with the game's count at `+4` through `RtEnter(7)`: a job holding A by raw pointer and B by its own reference across both destroys (no wait, no free, the job's release of B not the last); flush refused in the job, from another thread and inside a job callback, then A, B freed in park order on the owner; release order E, C, D at the next no-job destroy entry; not installed / switch off / closing / no job refuse; kept waits off the owner thread, in a callback, D-X record (one of another world does not keep), not in the hash; 512 parks fill the graveyard, the 513th keeps the wait, one flush releases all 512; T-G1; save entry (`RT_SAVE`) and travel / quit (`RT_CLOSING`) release, teardown in a job keeps | ok |
| `TestP5cIn` (P5c, unchanged) | with the owner-only flush | ok |
| steerro-test section DG | the game's own `DestroyById 0x939420`, `FindById 0x939560`, `FindByIdAddRef 0x9395A0` (through `P5cPark`, no P5c window) and release `0x7460F0` on a fake object service; a job thread holds raw pointers (half also a reference) across the owner's destroys | seeds 1 / 2 x 3000: 27,309 / 26,959 objects parked, each freed once at the flush, 0 freed reads, 0 job-thread destructors, hash back to 0. Negative control (park off, no wait, 750 cases each): 3,518 / 3,443 freed reads, 3,411 / 3,467 job-thread destructors |
| `build.cmd` | all selftests | `selftest ok` |

### 10.4 Expected gain and live jobs

Destroy key `0x2000003`: 8.7-8.8 ms/s BP and 3.1-4.3 ms/s IP before RouteSnap (§4), ~7-11 ms/s BP on the docs/136 base. With P5c on in the base, destroys inside a `FreeLock` window already park; the standalone park takes the rest. **Expected [inferred]: the key near 0 in the on slices except kept waits; net 3-6 ms/s BP, 1-3 ms/s IP less keyed sim wait** (gains do not add: part may reappear at the sim's next routing touch of the same tick, docs/87 A10 lesson). Run-level it/s cannot resolve this on a noisy night (docs/136 §2); the mechanism read is `wait-ab.ts`.

Queue `tools/harness/queues/destroy-park-ab.json` (not run; clean night only): `bp-wr-destroypark` (off-first ABBA), `bp-wr-destroypark-onfirst` (`abOnFirst`: on, off, off, on; pairs against the slot-1 bias of docs/136 §2), `ip-wr-destroypark`; base = `snap-p5c-ab.json` settings + `Discourage.DestroyPark=1`, 10 min, 2-min slices, `--ab destroypark`. asi: the build's `out/ts3native.asi` copied to `game-copy/ts3native.asi.destroypark`. Pass: no crash, `dpParked` > 0, T-G1 `p5cCountLow` 0, T-G3 `p5cAgeMaxUs` < 100 ms, `vfRtDestroyOffSim` 0; a crash in `0x939xxx`, `0x935xxx` or `0x64xxxx` stops the queue. Not built: probe P-G's caller / kind split and `dgJobMissParked` (§7).

**Effort:** this build ~1 h (code 15 min, tests 30 min, docs and queue 15 min); the queue ~40 min of game time plus reading.

## 11. The kept D-X waits: `[Discourage] DestroyParkDx` (2026-11-04, counters v904, run)

**What is left [measured, `combo-local-2026-11-04-combo-rtotail-r2`, save "1111 kit5", 10 min, DestroyPark on, the eleven read-only pieces + `[FootstepRO]` + `[RouteToObjRO] Tail`].** The destroy key `0x2000003` is still 8-11 ms/s on this base, one of the three largest sim waits. `dpParked` 10,627, `dpKeptWait` 881. Of the kept ones, 863 are `p5cKeptWait[0]` (a queued discourage record for the id, §10.1 "D-X order kept") and 18 are `[3]` (id not in the hash). So about 7.5% of the destroys during jobs pay the whole wait, ~6 ms each. In the same run D-X 1 applied `usfDestroyApplied` 300 design-U records and `discDestroyApplied` 2 Remove records at destroys; the gone-object sweep ran 3 times (`discDeadSweep`).

**Change (opt-in).** With `DestroyParkDx=1` (needs `DestroyPark=1`; runtime switch `dxMode` +0x5BCC, `--ab destroyparkdx`) a queued record no longer keeps the wait:
1. `P5cPark` parks the reference as for any other destroy and notes the record count (`dxParked`, `dxLeft`).
2. `RtEnter` `RT_DESTROY` skips `DiscOnDestroy` for that destroy, because D-X 1 takes `+0x918` (the job wait).
3. The records stay queued in their order. At the next drain the id is gone, so `DiscApplyOne`'s gone-object rules apply: Remove, usf disable and pf removes are swept by id (`0x635700` on all lots, then `world+0x994 = 0`); Add, usf enable and fp2 records are dropped; a reused id is dropped and counted (`discIdMismatch`).
4. Order of release: the drain runs before any flush of the parked reference. `DiscPoll` comes before `P5cFlush` at the START / READY polls, and save and closing drain first. So the object stays allocated until its records are handled, though nothing dereferences `r.obj` (pointer compare only).

**What changes against §10.1 [design].** "D-X order kept" no longer holds for these destroys. The difference from D-X 1:
- A Remove is swept by id instead of run by the original `0x834860` while the object is still in the hash. The lock-key map entry of the dead id stays (`discDeadKeyMap`, the existing `shortcut: not erased`).
- A design-U enable or an fp2 update of a destroyed object is dropped instead of applied just before the destroy. Destroy does not remove routing footprints (§D-X), so the swept Remove (or the game's own leak, when no Remove came) decides what stays, as for the destroys DestroyPark parks today.
- The footprints of the destroyed object stay in the lot quadtrees until the next drain, instead of going at the destroy. The running job already saw them (the records were deferred); the next job starts after the START poll's drain.

**Pass rules for the A/B.** No crash. `discIdMismatch` 0. `discDeadKeyMap`, `usfGoneEnable0/1`, `pfSwept` and `fp2Dropped` per arm, read beside `dxLeft`. Stuck Sims and route failures not above the off arm. Key `0x2000003` on vs off.

**Tests.** selftest `TestDestroyPark` case 8: with `dxMode 0` a queued record keeps the wait; with `dxMode 1` the destroy parks through `RtEnter(7)` without a wait, skips D-X 1, leaves the record queued, and a destroy without a record parks as before; the flush releases both.

**Runs (2026-11-04, dx-local, save "1111 kit5", 10 min each, in-load A/B `--ab destroyparkdx`; the eleven pieces, footstep cache, DestroyPark, Tail and SiteC on both arms).** Per arm, on vs off, about 240 s each:

| | dx-a | dx-b |
|---|---|---|
| destroy #4 `0x2000003`, ms/s | 0.4 vs 13.9 | 0.9 vs 11.7 |
| idle check site C `0x755990`, ms/s | 18.2 vs 14.3 | 17.7 vs 14.1 |
| goal validation `0x75A64C`, ms/s | 8.7 vs 6.7 | 7.9 vs 5.4 |
| all keyed sim waits, ms/s | 41.7 vs 45.3 | 33.5 vs 35.6 |
| D-X drain wait (`discDrainWaitUs`), ms/s | 3.5 vs 2.1 | 2.3 vs 1.1 |
| arm sum (`arm-ms.ts`), ms/s | 80.2 vs 88.8 (-10%) | 75.9 vs 74.4 (+2%) |
| parked with records (`dxParked`) / kept waits (`p5cKeptWait[0]`) | 802 / 0 vs 0 / 374 | 925 / 0 vs 0 / 318 |
| dead-id sweeps / sweeps that removed a footprint | 236 / 105 vs 0 / 0 | 182 / 62 vs 0 / 0 |
| fp2 updates dropped (gone object), slots 0 / 1 | 135 / 826 vs 1 / 15 | 78 / 933 vs 0 / 20 |

Pass rules: no crash; `discIdMismatch` 0, `discDeadKeyMap` 0, `p5cCountLow` 0; stuck Sims 2 and 2 (both arms pooled), inside the base rounds' 1-15. The destroy wait goes, but most of it moves to the next takers (site C, goal validation) and the drain: the keyed total falls only 2-4 ms/s, and the arm sums are inside the noise. With the footstep cache and the route-to-object tail (job new3, all three on vs all off): all keyed sim waits 31.3 vs 47.0 ms/s (-33%), arm sum 78.5 vs 91.8 (-15%), stuck Sims 5. The biggest wait left is site C (`0x755990`, 19.5 ms/s on): docs/195 §4 C2.

**Tripwire T-D5 with DestroyParkDx on.** A usf disable of a gone object is swept too, so T-D5 becomes `discDeadSweep = discDropRemove + usfGoneEnable0`. A private footprint remove of a gone object bumps `pfSwept`, never `discDeadSweep`, so it stays out of the formula. It held in every arm (236 = 72 + 164, 182 = 86 + 96, 213 = 59 + 154). The `pet-guard-cancel` repro checks the plain form; it runs with DestroyParkDx off.
