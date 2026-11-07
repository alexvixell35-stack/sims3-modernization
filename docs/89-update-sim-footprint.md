# 89. `UpdateSimFootprint 0x833560`: wait key `0x833593`

Written 2026-10-18. Static only; the game was not run (PLAN "Routing off-thread" item 5, docs/84 §7 item 4). Tools: Ghidra headless decompiles on a private copy of the docs/86 project (`game-copy/ghidra-usf`, copied from `ghidra-g0`, read-only runs), capstone 5 on `game-copy/bin/TS3W.exe` (Steam 1.67): a direct-call graph over `game-copy/analysis/functions.csv`, a raw `E8`/`E9` scan of all of `.text` for the callers (the function list misses the small object-event handlers, see §2), forward closures (5 hops from `0x833560`, 8 hops from the job roots `IC_Route_Plan 0x765160`, `Route_PlanLocked 0x82C6E0`, `RoutingJob_Execute 0x81B2A0`), a displacement scan for the world fields the function writes, and the absolute-pointer index for event registrations. Live numbers are re-read from existing runs in `probe-out/queue` (`*-native.json`, after minus before; 10 min = 600 s) and the sim-thread stack samples in `probe-out/profile/*-simstack.jsonl`. `native/` was read, not changed. Script side: `game-copy/decompiled` (grep). Scratch scripts stay in the session scratchpad.

**Built 2026-10-21 (offline, no game run): §10** (`[Discourage] DeferFootprint`, counters v74, queue `defer-footprint-ab.json`).

Tags as in AGENTS.md: **[verified]** = read in the disassembly, decompile, source or a run file (address or file given); **[inferred]** = reasoning, to confirm with the §7 probe or the A/B job.

## 1. Summary

- **What the key is [verified].** `0x833593` is the return address of the `Lock_Acquire 0x4E16F0` call at `0x83358E` (`ecx = world+0x918`): a plain `+0x918` acquire that our `LockAcquireAsync` pump turns into a sim wait while a job holds the lock. It is not a gate site and not one of our closures.
- **What the function does [verified decompile and disassembly `0x833560..0x833675`].** `thiscall(world)(obj, enable, force, arg)`, `ret 0x10`. If `force` is 0 and `RoutingWorld_ShouldUpdateObjectFootprint 0x82A720 (obj)` says no (not a Sim and no owner object, or the world flags `+0x976/+0x978/+0x97E` say "skip"), it returns without the lock; `force` is used for nothing else. Then `+0x918`; if world `+0x974` is set, it looks the object id up in the lock-key map `world+0x8B8` (`0x82AD60`). **Not parked (the usual case):** it sets the lot planner's flag `P+0x10D` to 1 (`0x624620`, returns the old value), removes the object's footprints from its lot (`0x82CBF0` -> `RoutingWorld_UpdateLotLocked 0x82C120` -> `RoutingWorld_LockedLotUpdate 0x82B450` -> `LotRouting_RemoveObjectFootprints 0x635700`, `RoutingWorld_RemoveFootprintLocked 0x82A0C0`, and `LotRouting_RemovePortals 0x6399E0`), and if `enable` re-inserts them (`Object_GetRoutingLotId 0x8297C0`, `RoutingWorld_UpdateObjectFootprintCore 0x8321F0 (obj, 1, lot, 0, arg)`: new `"Routing/ObjectFootprint"` per level, quadtree insert); then restores `P+0x10D`, clears world `+0x994`, releases. **Parked:** it stores `enable` in the map node `+0x1C` and, if enabling with an empty node vector, runs `UpdateObjectFootprintCore` the same way. So the function is "make this object's routing footprints match its footprint component now": a remove-all plus an optional re-insert, the same work as the discourage Remove/Add of design D (docs/84 §2.1).
- **Callers [verified, §2].** Seven direct call sites in five functions, reached from: (1) `Locomotion_Notify 0x947590` (the footprint component's active bit `C+0x40` bit 0 changed) from `RigPose_Update 0x940430` in **both animation passes** (`Animation_SACSPass` via `Actor_PoseSubSteps`, and `Animation_UpdateActors`), from `IC_World_ObjectSetHiddenFlags`, from discourage Add/Remove (nested, lock already owned) and from build/buy tools; (2) the footprint component's **object event 0x21 handler** (parent changed: `0x947630` -> `0x947040`, registered by `0x949040`); (3) `IC_World_ObjectEnableFootprint` (`0x947160`); (4) `IC_World_ObjectResetFootprintState` (`0x947360`); (5) `TreeComponent_Create` (`0x948F30`, world/lot load).
- **The wait is a real dependency, not gate policy [verified paths, effect inferred].** Under `+0x918` the function writes the lot quadtree (insert, remove, deferred flush; `LotQuadTree_Insert 0x666C50`, `0x6675B0`, `LotQuadTree_FlushDeferred 0x666A40`), footprint objects and their refcounts, the lot table (`LotTable_GetOrInsert 0x62EAB0`, `LotRouting_QuadTreeBuild 0x62F8A0`) and portals; the route job reads and writes the same structures (all of these except `RemovePortals` are in the job's 8-hop closure, §3.2). A sim that ran it during a job would race exactly as docs/83 §4 describes (node arrays and footprints freed under the worker). What the job does **not** touch: the lock-key map `+0x8B8..+0x8C4`, world `+0x994`, `P+0x10D` and the footprint component (§3.2).
- **Managed reach: none [verified direct; vtables by inclusion].** No `World_FireEvent`, remote call, `ScriptHost_InvokeDelegate` or runtime invoke within 5 direct hops (268 functions). `0x833560` sits at hop 2 under `RoutingWorld_RemoveDiscourageFootprint 0x834860` (`Remove -> Locomotion_Notify -> 0x833560`), whose closure docs/84 §7.1 widened through all 93 routing-library vtables to 8 hops with no managed, remote or event entry, so the same holds for 6 hops below `0x833560`.
- **Live size [verified run files, §4].** P0 (`h6-m1 bp-p0-async`): 230 waits, 2.13 s, **3.5 ms/s = 4.8%** of the 44.4 s sim wait. With design D on it grows: `clean-night bp-env-on` 597 waits, 5.00 s (8.3 ms/s, 8.7%); `bp-s2-on` / `-shadow` 5.6-5.8 s (9.4-9.7 ms/s, 10.0-10.4%); `h6-m1 bp-env-on` 4.34 s; IP 3.76-4.74 s (6.3-7.9 ms/s, 7.5-9.4%). Mean wait 8.4-11.6 ms (BP) against a mean job of 5.1-7.3 ms. **Its sibling** `RoutingWorld_ObjectFootprintFromTool 0x8334A0` (key `0x8334CC`, the footprint component's event `0x0E` handler) adds 1.2-4.1 s per run (2.0-6.8 ms/s), and `RoutingWorld_UpdateObjectLot 0x82D280` (key `0x82D296`) 0.5-2.1 s: with `IC_RouteManager_RemovePrivateFootprint` (key `0x82A0F6`, 0.1-0.5 s) the per-object footprint writers together are **15 ms/s BP (15.7%) and 19 ms/s IP (22.6%)** in the clean `env-on` pair.
- **H2 (docs/86 §9) does not cover it, and likely makes it worse [verified order, effect inferred].** None of the callers is inside `Locomotion_Update 0x83CED0` (8 direct hops: no path to `0x947590`, `0x833560`, `0x8334A0` or `0x82D280`). The animation callers run in `Animation_SACSPass` (`0xD81A70`, before the window) and `Animation_UpdateActors` (`0xD81C73`, the very next call after the H2 site `0xD81C5C`; only register loads in between). A job H2 held during the pass is claimed when the window drops, so it starts just as `Animation_UpdateActors` begins, where these footprint updates wait for it.
- **Design G (docs/88) and the QL stubs [verified source, design].** G parks a destroyed object only when D-X has no queued record for its id, so footprint records kept in D's queue (below) keep their destroy-time apply under G with no change to G. The QL writer stubs (`steerro.cpp` W1-W6) take QL only on the worker; on the sim they count `steerSimTreeWrite`/`steerSimCacheWrite` while a job runs. Today the sim reaches those leaves only after it has waited out the job (it holds `+0x918`), so they count nothing from here; a deferred apply runs with no job and counts nothing either.
- **Recommendation: design U, defer the footprint update on the sim into design D's queue (§5).** A detour on `0x833560` that, on the sim thread while a job runs and the thread does not own `+0x918`, evaluates the original's lock-free early out at call time and records `(object id, enable, arg, object pointer, world)` in D's FIFO instead of waiting; D's drain applies it in order with the original (`force = 1`, the early out already passed) at the same IC points, when no job runs, and D-X applies the object's records at its destroy. Phase 2 adds `0x8334A0` (needs the caller's `+0x978` bracket replayed). **Gain cap** 7.2-9.7 ms/s BP (8.3 in clean `bp-env-on`) / 6.3-7.9 IP for `0x833593` (3.5 ms/s at P0), 10-16 ms/s BP / 11-15 IP with `0x8334A0`; **realistic 3-5 ms/s** (U alone), 5-8 with phase 2, since gains do not add [inferred]. **Effort ~1.5 days** (U), +0.5 day (phase 2).

## 2. Callers, rate and stage

Every direct call of `0x833560` (raw `E8` scan of `.text`; the function list `functions.csv` has no entries for the event handlers, so the plain caller index misses them) [verified]:

| Call site (ret) | Caller | Arguments (enable, force, arg) | Reached from | Stage / rate class | Tag |
|---|---|---|---|---|---|
| `0x94760C` (`0x947611`) | `Locomotion_Notify 0x947590 (C, active, arg)` | enable = active and no Sim ancestor (`0x946120` walks the transform parents); force = `RoutingWorld_ShouldUpdateSimFootprint 0x829A60 (obj, 1, arg)` | `RigPose_Update 0x940430` (`0x94059E`: active = "the track manager did not report a locomotion track" this update), `IC_World_ObjectSetHiddenFlags 0x78D360` (active = hidden flag bit 1 clear; also `...WithWorldFootprint 0x78D3E0`), discourage Add `0x834568` / Remove `0x8348BC` (lock owned: never waits), build/buy `BlueprintHelper` realize (`0xCA0400`, `0xCA5E80`, `0xCA6880` x4, from `IC_UserToolUtils_UserToolGenericImpl`), user-tool objects (`0xCFA970`, `0xCFB1F0`, `0xD013C0`, `0xD43CE0`, `0xD44070`, `0xD442B0`, `0xD44670`: `UserToolObject`/`UserToolWorldObject` vtables) | animation: `Animation_SACSPass 0xD81A70` -> `Actor_PoseSubSteps 0x942C50`, and `Animation_UpdateActors 0xD81C73`, per actor per update but returns at once unless the bit changes (docs/81 §2: per moving-state change, so per locomotion start/stop); script: script tasks `0xD81948` (the stage where jobs START); tools: build/buy | verified decompile and call order |
| `0x94707B`, `0x94709B` | `0x947040 (C)` | active, no Sim ancestor: (1, 0, 0); Sim ancestor: (0, 1, 0) | `0x947630` (cdecl, gets the footprint component), registered for **object event `0x21`** (parent changed) by `0x949040` (`ObjectEvents_Register 0x936BC0`, call `0x9490A0`) | every attach/detach of an object with an active footprint (docs/79 §1: attach `0x95A360`, detach `0x959FC0`, load link `0x95ABA0`; script `ParentToSlot`/`UnParent`, animation and SACS prop attach) | verified |
| `0x94732B` | `0x947160 (C, nameHash, enable)` | sets or clears bit 2 of each named footprint's flag word (`C+0x58[i]`, `C+0x68[i]`); calls only when a flag changed | `IC_World_ObjectEnableFootprint 0x78D520` (C# `World.ObjectEnableFootprint`: `GameObject.EnableFootprint/DisableFootprint`, route exit slots `RouteCurrentObject`, 145 files / 337 lines name `EnableFootprint`) | script tasks | verified decompile, grep |
| `0x947575` | `0x947360 (C)` | re-copies each footprint's flag word from its definition, calls when one changed | `IC_World_ObjectResetFootprintState 0x78D5B0` (5 files) | script, rare | verified |
| `0x948F6A`, `0x948F7D` | `0x948F30 (C)` | (bit 0 and no Sim ancestor, 0, 0) | `TreeComponent_Create 0x95EC10` (lot tree clusters, `Lot_Save`, lot load) | world/lot load: no jobs [inferred] | verified callers |

**Rates [verified run files; split inferred].** Waits: 0.4-1.0/s in BP (230-612 per 10 min), 0.2-0.4/s in IP. Total calls are not counted anywhere. 21 stack samples carry the key, but the sampler keeps 24 frames and our pump frames come first, so only one shows a caller: `0x947330` (`0x947160` -> `IC_World_ObjectEnableFootprint`, `clean-night bp-s2-on`) [verified sample]. The mean wait exceeds the mean job by 1.5-1.8x (§4), the same pattern as the destroys (docs/88 §4), which fits arrivals soon after a job starts, i.e. the script-stage callers [inferred, I-89-3]. Probe P-U (§7) gives the caller split.

**Sibling writers (same component, same lock pattern) [verified].** `0x949040` also registers event `0x0E` (transform published, docs/79 §2.2) -> `0x947660` -> `0x9470B0`: for an object whose footprint is active (`C+0x40` bit 0) and has a footprint with flags 6 (`0x946340`), and that is not a non-Sim steering object without a parent, it calls `RoutingWorld_ObjectFootprintFromTool 0x8334A0 (obj)` (key `0x8334CC`), which does the not-parked branch above with `enable = 1` and its own early out; around it `0x9470B0` sets world `+0x978` to 1 when `C+0x40` bit 1 is set and `+0x97E` and `0x945E40` hold, then clears `C+0x40` bit 1. Callers of `0x0E`: `RigPose_Update` root motion, script `SetPosition`/`SetTransform`, steering agents (`0x954580`), build tools (docs/79 §2.2). Events 9 and `0x0A` go to `0x946F40` -> `0x946160` and `0x946F70` -> `RoutingWorld_UpdateObjectLot 0x82D280` (key `0x82D296`) [verified registrations; handler bodies of `0x946160` not read beyond the call].

## 3. What it touches, and what the job reads

### 3.1 `0x833560`, 5 hops

268 functions, 233 register-indirect calls [verified closure]:

| Hop | Content | Writes | Tag |
|---|---|---|---|
| 0 | early out `0x82A720`; `+0x918`; lock-key map find `0x82AD60`; `P+0x10D` swap `0x624620` (x2); `0x82CBF0`; `Object_GetRoutingLotId 0x8297C0`; `UpdateObjectFootprintCore 0x8321F0` | world `+0x97F` (early out, unlocked, benign per docs/60), map node `+0x1C`, `P+0x10D`, world `+0x994` | verified |
| 1-4 | `RoutingWorld_UpdateLotLocked 0x82C120` (`+0x918` again), `LockedLotUpdate 0x82B450` (`+0x918` again; map hit: node `+0x1C = 0`; miss: `0x635700`, then `RemoveFootprintLocked 0x82A0C0` with a `FindById` + AddRef/Release of the object), `0xC62D40` + `LotRouting_RemovePortals 0x6399E0`; `UpdateObjectFootprintCore`: `+0x918` again, lot / footprint / transform / steering components, `0x948090` level offsets, per level `EA_AllocTagged(0x44, "Routing/ObjectFootprint")`, `0x80C780`, slot `+0x90` AddRef, slot `+0xB0` for Sims, insert `0x6376D0` or park `0x82DC70`, the private-footprint rebuild (`Footprint_SetLevelPolygons 0x80F6A0`) | footprint objects (new, refcounts), lock-key map, portals, lot quadtree via 3-5 | verified decompile (docs/84 §2.1 for `0x8321F0`) |
| 3-5 | `LotQuadTree_Insert 0x666C50`, `0x6675B0` (remove), `FlushDeferred 0x666A40` -> `ApplyInsert 0x665C50` / `ApplyRemove 0x666750`, `FootprintMapEntry_ctor 0x61ECF0`, entry pool `0x61ED80`, `LotTable_GetOrInsert 0x62EAB0`, `LotTable_Contains 0x628A90`, `LotRouting_QuadTreeBuild 0x62F8A0`, `LotTable_MarkDirty 0x631AA0`, `0x62D9F0`, footprint registry `0x80EDC0`, allocator, `WorldManager_RebuildLotIndex 0xC68680` (hop 5, lot index dirty only) | quadtree nodes, entries, `Q+0x48/+0x4C`, lot table, lot entry vectors; globals `0x11CF02C` (`QuadTreeBuild`), `0x11581DC` (entry pool), `0x1165D48` (registry), `0x1165B80/+0x1165B8C` (`0x807400`), `0x11CF428` (`0x667740`) | verified closure |

**Locks [verified sites].** `+0x918` first (`0x83358E`), then recursive `+0x918` (`0x832253`, `0x82C157`, `0x82B48C`, `0x82A0F1`), then leaves only: entry pool `0x11CEFA8` (`0x61ED8B`), footprint registry `0x11D9068` (`0x80EDDC`), `0x11D8F90` (`0x807455`), `wm+0x228` (`0xC6869C`), `0x931228` (object lock, hop 5). Nothing takes `+0x918` while holding a leaf. Same order as docs/84 §2.1.

**Managed / remote / event.** None within 5 direct hops [verified]; by inclusion in docs/84 §7.1's Remove closure, none within 6 hops through the 93 routing vtables either [verified there]. The callers' own reach does not matter for design U: only the body of `0x833560` is deferred, the caller continues as today.

### 3.2 The job

Job closure (`IC_Route_Plan`, `Route_PlanLocked`, `RoutingJob_Execute`; 8 direct hops): 1,104 functions; 219 of the 274 functions within 5 hops of `0x833560` (same walker, entry-only counting) are also in it [verified]. Shared writers include `LotQuadTree_Insert` (hop 6), `0x6675B0` (7), `FlushDeferred` (6), `LotRouting_RemoveObjectFootprints 0x635700` (6), `QuadTreeBuild` (6), `Footprint_SetLevelPolygons` (6), `LotTable_GetOrInsert` (3), `0x635440` (6), `0x62D9F0` (7), `LotTable_MarkDirty` (7), and the footprint component's polygon list readers `0x9342D0`/`0x9342E0` (7) [verified]. So the job reads and writes the quadtree, footprints and lot table that `0x833560` rewrites: **real dependency** (docs/83 §4: unsafe without exclusion).

Not in the job closure [verified, direct calls plus a displacement scan of every function for `+0x8B8`, `+0x8BC`, `+0x8C0`, `+0x8C4`, `+0x994`, `+0x10D`]: `0x833560` itself and every function of its hop 0-2 (`0x82A720`, `0x829A60`, `0x82AD60`, `0x82CBF0`, `0x82C120`, `0x82B450`, `0x82A0C0`, `0x8321F0`, `0x6376D0`, `0x82DC70`, `0x80C780`, `0x948090`, `0x946410`, `0x6399E0`); no job function addresses the lock-key map, world `+0x994` or `P+0x10D` (readers of `+0x10D`: `0x636130`, `LotRouting_AddPortal`, `0x637C30`, `RemovePortals`, all outside the job). The footprint component bits (`C+0x40`, the flag words `C+0x48/+0x58/+0x68`) are read through `0x946340`/`0x946410`, also outside the job. Readers through vtables are not excluded [inferred none].

### 3.3 The callers' unlocked writes

Before `0x833560`, its callers write the footprint component without any lock: `Locomotion_Notify` the bit `C+0x40` bit 0, `0x947160`/`0x947360` the flag words, `0x9470B0` bit 1 [verified]. These already race with nothing on the job side (§3.2) and are unchanged by any design below. `UpdateObjectFootprintCore` reads them under the lock at apply time, so a deferred apply builds from the newest component state [verified reads, consequence inferred].

## 4. Cost split

| Run (build) | `0x833593` waits | s | ms/s | Share of sim wait | Mean wait ms | Mean job ms | `0x8334CC` s | `0x82D296` s |
|---|---|---|---|---|---|---|---|---|
| `h6-m1 bp-p0-async` (0.11.4, P0) | 230 | 2.13 | 3.5 | 4.8% | 9.2 | 6.1 | 0.67 | 0.46 |
| `h6-m1 bp-env-on` (D on) | 373 | 4.34 | 7.2 | 9.1% | 11.6 | 7.3 | 1.77 | 1.35 |
| `clean-night bp-env-on` | 597 | 5.00 | 8.3 | 8.7% | 8.4 | 5.1 | 2.66 | 1.03 |
| `clean-night bp-s2-on` | 594 | 5.63 | 9.4 | 10.0% | 9.5 | 5.5 | 4.08 | 1.09 |
| `clean-night bp-s2-shadow` | 612 | 5.81 | 9.7 | 10.4% | 9.5 | 5.2 | 2.43 | 1.20 |
| `h6-m1 bp-d-async-3` / `-4` | 258 / 351 | 2.54 / 3.59 | 4.2 / 6.0 | 5.9% / 7.8% | 9.8 / 10.2 | 5.7 / 5.8 | 1.45 / 1.18 | 0.47 / 0.49 |
| `clean-night ip-env-on` | 255 | 4.74 | 7.9 | 9.4% | 18.6 | 10.6 | 4.09 | 2.05 |
| `h6-m1 ip-env-on` | 161 | 3.76 | 6.3 | 7.5% | 23.3 | 15.8 | 2.90 | 1.33 |
| `clean-night ip-ab-routeasync` (2026-10-17 / -18, half async) | 122 / 175 | 1.79 / 2.66 | 3.0 / 4.4 | 6.0% / 6.3% | 14.6 / 15.2 | 11.9 / 11.7 | 2.27 / 2.55 | 1.09 / 1.63 |

Share = key time over the sum of `blockUs_0..31` + `blockOtherUs` [verified]. The rise from P0 to the D runs is the same shift as gate 0 (docs/86 §2): D removed the discourage waits in the script stage, so jobs that the sim used to absorb there still run when the later footprint updates come [inferred]. `IC_RouteManager_RemovePrivateFootprint` -> `RemoveFootprintLocked` (key `0x82A0F6`) adds 0.1-0.5 s [verified]; it is a script IC and outside this study.

Cost of the body itself outside jobs: a lot-table lookup, a footprint remove per level and, when enabling, one allocation and insert per level; not measured [inferred: tens of us].

## 5. Design options

Rules as in docs/84/86/88: sync reads the sim needs now cannot move to the worker; managed reach stays on the sim; a lock-free change needs every job-side reader of the written data to be safe; gains do not add.

| # | Option | What | Risk | Effort | Gain cap [inferred] |
|---|---|---|---|---|---|
| **U** | **Deferred update at the next IC point, in D's queue (recommended)** | §5.1 | §5.2 | 1.5 d | 7.2-9.7 ms/s BP, 6.3-7.9 IP (3.5 at P0); realistic 3-5 |
| U2 | U plus `0x8334A0` (phase 2) | same record kind with the caller's `+0x978` bracket replayed | as U, plus the bracket | +0.5 d | +2.0-6.8 ms/s BP, +3.8-6.8 IP |
| W | **Worker-applied write queued in order** (like D's co-location idea) | the worker runs `0x833560` between jobs | the body reads the object model while the sim mutates it: object transform (`0x958EE0` via `UpdateObjectFootprintCore`), components, the parent chain (`0x946120`), lot membership (`0x8297C0`); same verdict as docs/84 §2.3. Not possible as it is | - | - |
| R | **Read-only / lock-free variant** | none: the function is a writer; its only lock-free part (the early out) already runs before the lock | - | - | 0 |
| P | **Gate policy change** (do not wait) | the key is a plain `+0x918` acquire, not a gate; running the body during a job races with the job's quadtree flush, inserts and removes (docs/83 §4: freed nodes and footprints) | not acceptable | - | - |
| H2 | **Covered by H2?** | no (§1): the callers are outside `Locomotion_Update`, and H2's held jobs start at `Animation_UpdateActors` | - | - | 0; may add waits |
| H2X | **Extend H2's window over `Animation_UpdateActors`** (a second call thunk at `0xD81C73`, same code as `locowin.cpp`) | no queued job starts during the animation pass either | only the waits on jobs that would start during that pass (none of the script-stage ones); holds every queued start ~1 more pass; throughput | 0.5 d | at most the `Animation_UpdateActors` share of the key (unknown until P-U) |

### 5.1 Design U

Hook `0x833560` (bytes `83 EC 10 80 7C 24 1C 00`: `sub esp,10h` and `cmp byte [esp+1Ch],0`, 8 bytes, whole instructions, no relative operand [verified]; not listed in docs/68). `thiscall(world; obj, enable, force, arg)`, `ret 0x10`.

1. **Pass through** (call the trampoline) unless all hold: `rgEnforce`, `[FootprintDefer] Enabled=1 Mode=1`, this is the sim (owner) thread, it does **not** own `+0x918` (`rgOwnerTid`; covers the nested calls from discourage Add/Remove, from D's own drains and any other caller already inside the lock), not inside a job callback (`g_inInvoke`), `g_async.state == A_RUNNING` (read after raising `g_rgOffSimOpen`, as D does), not `rtClosing`, world `+0x974` set, and D's queue has room.
2. **Early out at call time:** if `force == 0`, call the game's `0x82A720 (world; obj)` on this thread (as the original does; it may write `+0x97F`); if it returns 0, return (nothing else would have happened).
3. **Record** `{kind USF, idLo/idHi = obj+8/+0xC, enable, arg, obj pointer (D-X pointer check, no reference), world, tick}` at the tail of D's FIFO (`DiscRec` with a kind field) and return. Script and native callers get no value from `0x833560` (void) [verified].
4. **Apply** in D's drain (`DiscDrain`, same points: next D call with no job, `GetOptionHook` READY/START/CANCEL, `RT_SAVE`, travel/quit/teardown, max age 250 ms): `ObjectService_FindByIdAddRef 0x9395A0` (`ret 0xC`); if found and it is the recorded pointer, call the trampoline with `(obj, enable, force = 1, arg)` holding `+0x918` with no job running, then `RefPtr_Release 0x7460F0`. `force = 1` skips the early out that already passed at call time; `force` has no other use in the function [verified disassembly].
5. **Destroy (D-X):** `DiscOnDestroy` applies the id's queued USF records in order with the object still in the hash, like Remove records; design G then sees a queued record and keeps the wait (docs/88 §5.1 rule 2, unchanged).
6. **Gone at apply** (not found, or another object under the id): an `enable = 0` record runs D's dead-id sweep (`0x635700` with lot 0); an `enable = 1` record is dropped. Both counted.

Why the order holds: every record is "remove this object's footprints from its current lot, re-insert from the current component state if enabled", so a later record supersedes an earlier one for the same object, and records of one object stay in FIFO order with that object's discourage records [inferred from §1 body; I-89-5]. Lot changes are applied synchronously by `0x82D280` (event `0x0A`, not deferred in phase 1) before or after a queued USF record; either way the apply works on the current lot [inferred, I-89-6].

### 5.2 Risks

| Risk | Handling | Tag |
|---|---|---|
| Stale footprint while a job runs (a Sim that stopped is not yet an obstacle; a Sim that started walking still is) | same staleness the running job already sees; applied at the next no-job IC point, at most 250 ms later (D's max age); age counted (`usfAgeMaxUs`) | design; inferred effect |
| A plan queued after the call starts before the apply (`ServeAsync` starts the next queued job without waiting for the sim's drain) | that plan sees the old footprint, one job stale, as with D's discourage records (D confirmed 2026-10-17 with this behaviour) | verified `ServeAsync` start rule (docs/86 §1), effect inferred |
| The other sync footprint writers for the same object (`0x82D280`, `0x833680` from event 9, `RemovePrivateFootprint`, build tools) run between record and apply | each makes the footprints match the current state; the deferred record redoes it later from the then-current state; build tools run without jobs [inferred]. Counted: `usfSyncWriterSameId` (event 9/`0x0A` handlers for an id with queued records, shadow) | inferred, I-89-6 |
| Lock-key map (parked objects) changes between record and apply | the parked branch stores `enable` in the node and only inserts for an empty node, so it is order-safe for the same id; the map is sim-only (§3.2) | verified writers in the scanned set |
| `P+0x10D` and world `+0x994` | written only by the body under `+0x918`; no job reader (§3.2) | verified scan |
| Recursion: a drain apply of a discourage record reaches `Locomotion_Notify` -> `0x833560` | owner of `+0x918`: pass through (rule 1) | design |
| A destroy from inside a job callback or off the sim | as D-X today (counted, no apply possible) | verified counters (0 in all runs) |

## 6. Offline proof (steerro-test style)

1. **steerro-test section J (new):** map TS3W.exe as the other sections. Patch the real hook site at `0x833560` through the same install path as the build (refuse a changed byte), then drive the game's own callers that reach it: `Locomotion_Notify 0x947590` with a fake footprint component (bit flips 0->1->0), `0x947040` (event `0x21` handler) and `0x947160`, on a fake routing world whose `+0x974` is 1 and whose `0x82CBF0`/`0x8321F0` callees are stubbed to log `(obj, enable, arg)` (the quadtree work is not under test). Cases: no job (calls run at once, log equals the unhooked run); `A_RUNNING` (records, nothing logged until the drain, then the same log in the same order); nested call with `+0x918` owned (runs at once); `force = 0` with the early out saying no (no record); destroy with queued records (applied before the fake destroy returns, D-X order); id gone at apply (sweep for `enable = 0`, drop for `enable = 1`). Pass: logged sequences equal to the unhooked reference for 4 seeds x 5,000 random caller sequences, every record applied or swept, queue empty at the end.
2. **selftest:** `TestFootprintDefer`: pass-through rules, FIFO interleave with discourage records, max-age forced apply, overflow (apply with wait), D-X and dead-id sweep, counters; `TestDiscourageDX` and `TestDestroyGrave` (when built) unchanged.
3. **tools/emu spec** `usf_defer.py`: the stub's convention (`ret 0x10`, `ecx` = world, stack balanced on both paths, trampoline call with `force = 1`).
4. `node tools/hook-sites.ts --check` (new site `0x833560`, and `0x8334A0` in phase 2, into docs/68 first), `node tools/check-docs-code.ts`.

## 7. Probe P-U and live jobs

**P-U (count only, `[FootprintDefer] Mode=0`):** at `0x833560` entry on the sim: total calls and calls while `A_RUNNING` (`usfCalls`, `usfInJob`); caller key = the return address (top 32 like `blockKey`, with the time each keyed call then waited, `usfKey_*`/`usfKeyN_*`/`usfKeyUs_*`); for `0x947611` (`Locomotion_Notify`) also its own return address (`0x9405A3` RigPose, `0x78D3DA` hidden flags, the tool sites), read at the fixed frame offset of `Locomotion_Notify` [inferred offset; read in the emu spec]; a stage tag from the current `MonoScriptHost_Simulate` stage (`TaskObjects` / SACS / `Animation_UpdateActors` / other, from our existing stage hooks if present, else the second return address); the same three counters for `0x8334A0` and `0x82D280` (`ftInJob`, `uolInJob`). Tripwires in Mode 1: **T-U1** `usfApplyInJob` (an apply while `A_RUNNING`) = 0; **T-U2** `usfAgeMaxUs` under 300 ms; **T-U3** `usfGoneEnable0` = `usfDeadSweep` (every vanished disable swept).

Live, in `queues/clean-night*.json` only (clean machine), queue + D + D-X on, seeded:
1. **`bp-usf-probe`** (P-U, Mode 0, 10 min): caller and stage split of the 8.3 ms/s and of `0x8334CC`/`0x82D296`; decides U vs U2 vs H2X.
2. Read `0x833593` and `0x8334CC` in the halves of the already queued **`bp-g0w-ab`** / **`ip-g0w-ab`** (docs/86 §9): if H2 Mode 1 raises them, H2X joins U.
3. **`bp-usf-ab`** and **`ip-usf-ab`** (`--ab footprintdefer`, bundle rule): `0x833593` near 0 except pass-throughs, total sim wait and iterations/s against Mode 0, T-U1..T-U3, 0 crashes, stuck Sims logged only; any crash in `0x82xxxx`/`0x83xxxx`/`0x66xxxx` stops the run (crash-cause rule).
4. **Repro `usf-churn`** (new `repros.json` entry): spawned Sims at ultra speed with route jobs running, plus repeated sit/stand and pick-up/put-down interactions (parent changes `0x21` and route exit slots `EnableFootprint`) through `reflect_call`, and `World.ObjectSetHiddenFlags` toggles on a home-lot object; `pet-guard-cancel` with U on checks the D-X interleave.
Re-run P0 after (gains do not add).

## 8. Open questions

1. The caller and stage split (P-U, I-89-3): script ICs (event `0x21`, `EnableFootprint`, hidden flags) vs the two animation passes.
2. Does H2 Mode 1 raise `0x833593`/`0x8334CC` (I-89-4)?
3. `0x82D280` (event `0x0A`, lot change) and `0x833680` (event 9) as further deferral candidates: their return values (`0x946160` was not read past the calls) and whether they are order-sensitive with U records (I-89-6).
4. What `C+0x40` bit 0 means exactly for Sims (track manager "no locomotion track this update" from `RigPose_Update`, so walking Sims have their footprint off): the static reading fits docs/81 ("moving bit"), not confirmed live [inferred].
5. Whether the body cost outside jobs (remove + re-insert per level) is worth a cheaper "no change" skip: not measured.

## 9. Ledger

Rows `I-89-1` .. `I-89-8` in docs/51 §21; the build's rows `I-197-1` .. `I-197-7` in docs/51 §197.

## 10. Design U as built (2026-10-21, offline; no game run)

Built in `native/ts3native/discourage.cpp` on the 0.36.9 base (version set at merge), counters **v74** (docs/registry.md; renumbered from v75 at the 0.36.11 merge), selftest `TestFootprintDefer`. Ini `[Discourage] DeferFootprint=1` (default 0; the §5.1 name `[FootprintDefer]` was not used), installed only after design D's two detours; runtime switch `usfMode` (+0x3178, `--ab deferfootprint`).

### 10.1 What it does

Detour on `0x833560` (`83 EC 10 80 7C 24 1C 00`, refused if a byte differs; docs/68 row added). In order:

1. **Pass through** (the original with the caller's arguments) when: not the async owner (sim) thread, a P4 clone run, `obj` NULL, the thread owns `+0x918` (`rgOwnerTid`: nested calls from discourage Add/Remove, from D's drains and from the applies themselves), inside a job callback (`g_inInvoke`), during a D drain, world `+0x974` clear, `world+0x918` is not the learned routing lock, or `rtClosing` (`usfPass`).
2. **Order rule.** If the object (id + world) already has a record in D's queue, the call is queued whatever the mode and whether a job runs (`usfOrderDefer`); otherwise it is queued only with `usfMode = 1` while a job runs, else it runs now (today's call). Every object's records stay in FIFO order with its own discourage records (§5.1 "why the order holds").
3. **Early out at call time:** `force == 0` and `0x82A720 (world; obj)` (thiscall, `ret 4` [verified `0x82A720..0x82A778`]) says no: return, as the original does before its lock (`usfEarlyOut`).
4. **Parked object** (id in the lock-key map, read lock-free: the map is sim-only, §3.2) with nothing queued: the original with `force = 1` (it waits as today; `usfParkedPass`). The parked branch only stores the enable byte in the node and inserts into an empty node vector [verified `0x833639..0x833663`], so the supersede rule below does not hold for it.
5. **Duplicate:** the object's newest queued record is a U record with the same object pointer, enable byte and `arg`: dropped (`usfDup`). Applying the same call twice equals applying it once in both branches [verified body: remove-all plus re-insert from the current component state; parked: same byte, non-empty vector after the first].
6. **Queue full (256):** with no Add record queued, D's drain runs here (Remove and U records are managed-free: docs/84 §7.1, §1 above), then the call (`usfFullDrain`); with an Add queued (Add may fire `World_FireEvent 0x11`, IC points only) the call runs now, possibly out of order (`usfFullPass`, expected 0).
7. **Record** `{usf, id, enable byte, arg, object pointer (no reference), world, tick}` at the tail of D's FIFO (`usfDeferred`).

**Apply** (D's drain points: the next D call with no job, `GetOptionHook` START / READY / CANCEL, save, travel / quit; max age 250 ms; and D-X's destroy gate): `ObjectService_FindByIdAddRef 0x9395A0`, pointer check, then the original with `force = 1` holding `+0x918` with no job running, release (`usfApplied`; `usfApplyInJob` = tripwire T-U1, expected 0). **Collapse at the drain:** a U record whose object's next queued record is also a U record (same pointer, object not parked now) is skipped (`usfCollapsed`): the later one redoes the whole remove plus re-insert from the then-current state. A D record in between stops the collapse. **Destroy (D-X):** the object's U records are applied in order before the destroy (no collapse there; `usfDestroyApplied`), its Add records dropped, everything else kept; design G (`P5cDiscHas`) sees the queued record and keeps its wait, unchanged. **Gone at apply:** a disable record runs D's dead-id sweep (`0x635700`, lot 0, with `DeadSweep=1`; `usfGoneEnable0`, `usfSwept`, T-U3: equal), an enable record is dropped (`usfGoneEnable1`), a reused id is dropped (`usfIdMismatch`). A record from another world is dropped (`discDropped`, shared queue). Ages: `usfAgeMaxMs` (T-U2 < 300). A 60-s log line `footprint defer: ...` when installed.

**Not built (from §6/§7):** the P-U caller keys and stage tags (`usfKey_*`), the `usfSyncWriterSameId` shadow, steerro-test section J and the emu spec. `usfCalls` / `usfInJob` in the off slices of the A/B give the call and in-job rates. **Phase 2 (`0x8334A0`) not built:** it is not the same call (its caller `0x9470B0` brackets it with world `+0x978` and clears component `C+0x40` bit 1, §2), so not "trivially the same".

### 10.2 Readers between record and apply

The body writes, all under `+0x918` [verified `0x833580..0x83362B`]: the lock-key map node `+0x1C` (parked branch), `P+0x10D` (set, then restored), world `+0x994`, and through `0x82CBF0` / `0x8321F0` the object's routing footprints, the lot quadtree, lot table and portals (§3.1). U moves the whole body, so no reader can see a half-done update; a reader before the apply sees the whole pre-call state.

| Reader | What it sees | Tag |
|---|---|---|
| Route job on the worker (§3.2 closure) | the pre-call footprint, as if the call came after the job: consistent | verified closure, effect inferred |
| Sim-thread routing reads under `+0x918` or QL (sync plans, steering `0x82EDB0`/`0x85FD10`, gates, slot and obstruction checks, `LotTable_Contains`) | the pre-call footprint until the drain (at most to the next no-job IC point or 250 ms): consistent, stale; the same staleness design D has had since 0.12.4 (§5.2 rows 1-2) | inferred effect |
| The calling code right after the call | `Locomotion_Notify` returns (`0x947611`), `0x947040` returns (`0x947080`, `0x9470A0`), `0x947360` returns (`0x94757A`), `0x948F30` returns (`0x948F6F`); `0x947160` then calls `0x790D60` (string building and a channel message through `0x78F2B0`; no routing world access in its direct calls). `RigPose_Update` continues with component lookups at `0x9405A3` | verified disassembly (first instructions after each call; `0x78F2B0`'s virtual target inferred) |
| Same-object discourage Add / Remove | a D call with no job drains first (U records included), so order holds | verified code + selftest |
| Destroy, save, travel / quit | D-X applies the id's records; `RT_SAVE` and `RT_CLOSING` drain everything first | verified code + selftest |
| Other sync writers of the same object (`0x82D280`, `0x8334A0`, `RemovePrivateFootprint`) | each makes the footprints match the current state; the later apply redoes it from the then-current state (I-89-6) | inferred |
| Lock-key map ICs (park / unpark) | a parked object with nothing queued takes today's path; a queued record applies on whichever branch the object is in then | verified branch, effect inferred |
| Footprint component bits (`C+0x40`, flag words) | written by the callers before the call, as today; the apply reads them current (§3.3) | verified |
| `P+0x10D`, world `+0x994` | written only by the body; no job reader (§3.2) | verified scan |

No reader needs today's wait as a fallback beyond the parked case. Not proven: script-side reads of routing footprint data between record and apply (none found statically; an IC reading the footprint component sees the new state anyway) [inferred].

### 10.3 Offline checks

`build.cmd`: selftest ok. `TestFootprintDefer` (fake world, fake lock, fake original and early out, D-X fakes): no-job pass-through with the caller's `force`; in-job records in one FIFO with D's records, applied in order at the next drain with `force = 1` and the lock held (`pbQ`); early out leaves no record; drain on job end; exact repeat dropped; drain-time collapse (`qP`), stopped by a D record between (`Pap`); order rule with no job and in mode 0 (`Qap`); a D call with no job drains U records first (`pb`); pass-throughs (mode 0, `+0x918` owned, job callback, another world) and a nested call from inside an apply; parked object (today's call; queued records not collapsed); destroy while queued (`pP` before the destroy, Add dropped, the other object kept); gone at apply (disable swept with the dead id, enable dropped, reused id dropped); queue full with and without an Add; T-U1 0 and T-U2 < 300 ms. `hook-sites.ts --check`: `0x833560` / `0x82A720` in docs/68. `queue.ts --self-check` ok, `--dry-run` of the new queue.

### 10.4 Live jobs (queued, not run)

`tools/harness/queues/defer-footprint-ab.json` (asi `game-copy/ts3native.asi.deferfp`, copied by hand from the build; base = snap-p5c-ab.json settings + `Discourage.DeferFootprint=1`, HitchAttr on, 10 min, 2-min slices, `minCounters` 75): `bp-wr-deferfp` (off-first ABBA), `bp-wr-deferfp-onfirst` (`run.profile.abOnFirst`, new `profile-world.ts --ab-on-first`: on, off, off, on, for docs/136 §2's slot bias), `ip-wr-deferfp` (off-first). Read with `node tools/harness/wait-ab.ts <tag>`.

**Expected [inferred]:** key `0x833593` from ~7-9 ms/s BP (docs/116 §4.1; 7.2-9.7 in §4) and 6.3-7.9 IP down to ~0-1 ms/s in the on slices (left: parked objects, a full queue); the sim-thread keyed total down 3-5 ms/s BP, since part of the wait moves to the next sync acquire after the job (gains do not add, §5). Apply cost per record outside jobs: tens of µs, not measured.

## 11. Results: no gain, wait conserved (2026-10-22, clean off-first + daytime pair)

Data: `probe-out/profile/clean-night-routing-extra-2026-10-22-bp-deferfp-offfirst` (its on-first twin has no `-ab.json`), `defer-footprint-ab-2026-10-21-bp-wr-deferfp{,-onfirst}`, `-ip-wr-deferfp`. Measured unless marked.

**It defers [verified, counters].** Per 240 s on: `usfDeferred` 273-288 BP, applied 208-215 + collapsed 58-77 + destroy-applied 24-63, `usfAgeMaxMs` 31-94, `usfApplyInJob` 0; off: 1-9 (order rule only).

**The drain point holds nothing [verified].** `DiscDrain` waits for the job only when forced (queue full, a record older than `kDiscMaxAgeMs` 250, or an Add/Remove that cannot defer); `discDrainWaitUs` is 92-127 us per 240 s on both sides, and no usf record reached 250 ms. Not the cause.

**The key was never timed [verified].** `0x833593` is missing from every table: the warm-up slice is on, so UpdateSimFootprint did not wait until the 32 probe-P0 slots were full, and its off-half waits counted only in `blockOther` (off 247-309 vs on 78-129 per 240 s BP; the difference ~0.7/s matches the deferred rate). So the draft's "keyed wait +5.7 / +8.4 ms/s" leaves out the removed key's off time. Corrected with 7.9 ms/s for `0x833593` off (mean of the four BP slotro halves on the same base, 6.8-9.2) [inferred]: clean off-first +12.5 -> ~+4.6; daytime off-first +7.1 -> ~-0.8, on-first +4.2 -> ~-3.7.

**Where it went [verified per key, on minus off ms/s: clean / daytime off-first / on-first].** `0x8334CC` (inside the sibling `0x8334A0`, phase 2, not deferred [inferred from the address]) +3.3 / +2.1 / -2.4; `0x82D296` +2.2 / +1.6 / +3.1; destroy +2.4 / +0.9 / -1.7; gate 0 +2.1 / -0.8 / +1.7. The summed wait is conserved (docs/143 §8.14): the next taker in the tick waits out the same job hold. it/s -0.6% / +0.7% (draft pairs).

**Verdict: no gain by design while other takers follow in the tick.** Phase 2 (`0x8334A0`) alone would move the wait again. What gains: a shorter or lent job hold (P5b / P5c), H2 (docs/86), or all takers of the pass lock-free in one bundle (docs/137 §12). Re-measure with the key timed (rofix build seeds `0x833593`): `tools/harness/queues/rofix-ab.json` (`bp-deferfp-verify` day job with counter pass rules, then `bp/ip-deferfp-offfirst/onfirst`, asi `.rofix`). Ledger docs/51 §218.

## 12. Design U phase 2 as built (2026-10-22, offline; no game run)

Built in `native/ts3native/discourage.cpp` on 0.36.23 (TS3NATIVE_VERSION not bumped), counters **v94** (`fp2*`, docs/registry.md), selftest `TestFootprintDefer2`. Ini `[Discourage] DeferFootprint2=1` (default 0), installed only after design U (its order rule keeps a U call behind a queued phase 2 record); runtime switch `fp2Mode` (+0x3FB4, `--ab deferfootprint2`). It closes docs/152 gaps G2 and G3; G4 (`RemovePrivateFootprint`) is not deferred (§12.3).

### 12.1 Keys to functions [verified disassembly]

| Key | Function | Proof |
|---|---|---|
| `0x8334CC` | `RoutingWorld_ObjectFootprintFromTool 0x8334A0`, `thiscall(world; obj)`, `ret 4`, void | return of `call Lock_Acquire 0x4E16F0` at `0x8334C7`, the function's only `+0x918` acquire, after its early out `0x82A720` (`0x8334AC`) |
| `0x82D296` | `RoutingWorld_UpdateObjectLot 0x82D280`, `thiscall(world; obj)`, `ret 4`, void | return of `call 0x4E16F0` at `0x82D291` (no early out: the lock comes first) |
| `0x82A0F6` | `RoutingWorld_RemoveFootprintLocked 0x82A0C0`, `thiscall(world; obj, flag)`, `ret 8`, `al` = a footprint was removed | return of `call 0x4E16F0` at `0x82A0F1` |

Callers (direct `E8` scan of `.text`): `0x8334A0` from `0x94713D` (event `0x0E` handler path `0x9470B0`) and `0xB585A1` (build tool); `0x82D280` from `0x9462C9` and `0x946F99`; none reads `eax` after the call [verified]. `0x82A0C0` from the IC `0x754610` (`0x75465E`; `al` becomes the IC's result) and from `LockedLotUpdate` (`0x82B551`, under `+0x918`).

What they do [verified disassembly]: `0x8334A0` = early out `0x82A720(obj)`, lock, `+0x974`, lock-key map lookup (a parked object: nothing), else `P+0x10D` set, `UpdateObjectFootprintCore 0x8321F0 (obj, 1, lot, 1, 0)` (5th argument 1: the core runs `0x82A720` again and on yes the remove `0x82CBF0`; then `0x829A60(obj, 1, 0)` and the insert), `P+0x10D` restored, world `+0x994 = 0`. So it is not `0x833560` with enable 1: the core's 5th and 6th arguments differ and there is no remove before the core. `0x82D280` = lock, `+0x974`, `obj`, `0x82C120 (world; id, lot of obj)`, unlock. It only removes (§16 corrects "re-home"; the insert is `0x833680`).

The `+0x978` bracket: `0x9470B0` sets world `+0x978 = 1` around the call when `C+0x40` bit 1, `+0x97E` and `0x945E40` hold, then clears `C+0x40` bit 1; `0xB58557..0xB58574` brackets the build tool call the same way. Readers of `+0x978` on that path: only `0x82A720` (`0x82A738`) and `0x829A60` (`0x829A80`), and only when `+0x97E` and `+0x976` are both set (the world's batch mode); each then clears `+0x97F`. The other readers (`0x835B46`, `0x8359B3`, `0x83609D`) and writers (`0x82FBB2`, `0x8359B9`, `0x8360AD`) are not within 8 direct hops of `0x8334A0` or `0x82D280` (BFS over direct calls; indirect sites not followed) [verified direct].

### 12.2 What it does

Same skeleton as §10.1; differences only:

1. **Pass through** as U, plus FromTool while the batch mode is on (`+0x97E` and `+0x976`): there the bracket byte decides the early outs and the first clears `+0x97F` (`fp2Pass`).
2. **Order rule** as U: an object with any queued record (D, U or phase 2) queues the call in any mode; otherwise only with `fp2Mode = 1` while a job runs.
3. **FromTool early out at call time:** `0x82A720` says no: return (the original does nothing). UpdateObjectLot has none.
4. **Duplicate:** the object's newest queued record is the same kind, same pointer and (FromTool) the same `+0x978` byte: dropped (`fp2Dup`). Remove plus re-insert, or the re-home, twice from the same state equals once [inferred from the bodies].
5. **Queue full:** as U (drain unless an Add is queued, then the call: `fp2Forced`; with an Add queued the call runs now, out of order: `fp2FullPass`, expected 0).
6. **Record:** `{fp2 = 1 FromTool / 2 lot, id, pointer, world, tick, high = world +0x978 at the call}` (`fp2Deferred[kind]`).

**Apply** (D's drain points, D-X destroy): find by id + pointer check; FromTool: world `+0x978` set to the recorded byte, the original (it runs its own early out again: a call made now with the caller's bracket), `+0x978` restored; lot: the original, reading the lot at apply time. Gone or reused id: dropped (`fp2Dropped`). **Both rules lost removals: §16.** No drain-time collapse for phase 2 records, and a U record followed by a phase 2 record of the same object is not collapsed (the U collapse only looks at U records). Tripwire `fp2ApplyInJob` = 0; age `fp2AgeMaxMs`.

### 12.3 Readers that need the fresh state

| Reader | Handling | Tag |
|---|---|---|
| Same object's later U / FromTool / lot calls | queue behind (order rule) | verified code + selftest |
| Same object's D Add / Remove with no job | `DiscDrain` before the call (unchanged) | verified code + selftest |
| Private-footprint ICs: `RouteManager_RemovePrivateFootprint` (`0x754610` -> `0x82A0C0`: removes every footprint of the id from its lot via `0x635700` and frees the component's private footprint `C+0x38` via `0x947880(0)`) and `RouteManager_AddPrivateFootprint` (`0x75DCF0` -> `0x831BA0`) | call sites `0x75465E` / `0x75DE1A` retargeted: an object with queued records is flushed first (`DiscDrain(true)`, waits for a running job; `fp2Forced`), else the call as today. Without it a deferred FromTool could re-insert after the remove | sites and flush verified + selftest; reorder effect inferred |
| Destroy | D-X applies the object's U and phase 2 records first | verified code + selftest |
| Save, travel / quit, START / READY / CANCEL polls | D's drains (all records) | verified code |
| Route job on the worker | pre-call footprints, as if the call came after the job | inferred (as §10.2) |
| Sim-thread sync routing reads (sync plans, steering, gates, slot checks) | stale until the next drain (at most 250 ms or the next no-job IC point), as U and D | inferred |
| Event 9 `0x833680` and other unhooked writers of the same object | run now; the deferred record redoes from the then-current state (I-89-6) | inferred |

**`RemovePrivateFootprint` not deferred (G4):** its result is the IC's return (`0x754667`). Script pops it (`DynamicSimFootprint.set_Enabled`, the only game caller, `IL_014e: pop`) [verified IL], but it is order-sensitive with `AddPrivateFootprint` and the U / phase 2 records (it removes all footprints of the id), its key is 0.1-0.7 ms/s, and its entry already carries `[RouteSnap] Count`'s stub. It stays a residual; the flush above keeps it in order.

### 12.4 Offline checks

`build.cmd`: selftest ok. `TestFootprintDefer2`: no-job pass-through; in-job records in one FIFO with U and D, applied in order with the call-time `+0x978` replayed and restored; FromTool early out leaves no record; order rule in mode 0 and with no job; U before phase 2 not collapsed; duplicates (not across another bracket byte, kind or object); pass-throughs incl. batch mode; flush before the private-footprint ICs (also waiting for a running job; skipped with `+0x918` owned or no record of the object); destroy; gone / reused at apply; queue full with and without an Add; no lost update (`fp2Deferred = fp2Applied + fp2Dropped` after every drain); tripwires 0. `steerro-test` (prologues and call sites asserted on the image): seeds 1 and 2 ok; seed 3 fails in section K8 (planner assert, `g_k8Found` etc.) identically on the unchanged base build, so not this change. `hook-sites.ts --check`: new sites in docs/68; 7 older addresses (threadstack, monothread-m5, seeded wait keys) were already unlisted on base. `check-docs-code.ts` ok. Parked `game-copy/ts3native.asi.deferfp2` sha256 `f6dc77c42c464b944111170e99157414438e0270b7af8be775cd499395592745`.

### 12.5 Live jobs (queued, not run)

`tools/harness/queues/deferfp2.json`: `bp-deferfp2-verify` (day, 5 min, modes on, no A/B: deferred = applied + dropped by the end, `fp2ApplyInJob` / `fp2FullPass` 0, ages < 300 ms, forced flushes counted, stuck Sims not above the baseline, no crash), then night pairs BP + IP off-first / on-first with every B0 switch plus `deferfootprint2` toggled together (`--ab steerro,avoidro,slotenabledro,lotfdro,obstructedro,deferfootprint,destroypark,deferfootprint2`). **Expected [inferred, docs/152 §1]:** keys `0x833593`, `0x8334CC`, `0x82D296` ~0 in the on slices and the summed keyed wait about unchanged (gate 0 G-a and `0x83156D` remain in every pass): this arm tests the bundle model, not a gain on its own. Ledger docs/51 §238.

## 13. PlanDrain: drain before a sim-thread sync plan (2026-10-23, built 0.36.36 as `asi.0342`, offline; no game run)

**Why.** Repro `ip-boat-exit-defer-loop` (a Sim leaving a houseboat during crowd route jobs) shows repeated failing sync Replans of one Sim only with `DeferFootprint` on. The code gives a reason [verified discourage.cpp, ts3native.cpp]: there is **no timed drain**. `kDiscMaxAgeMs` (250) is read only inside `DiscPoll` (START / READY / CANCEL), and `DiscPoll` drains a fresh queue only when no job runs. A sim-thread sync plan (`PlanLockedBody` -> `PlanCached` -> `RunPlan`) waits for a running job, then plans with every deferred record still queued, so it reads the pre-call footprints of every deferred object. The "at most 250 ms" wording in §10.2 row 6 and §12.3 is too strong: it holds only while some task polls and the head record is old enough.

**What.** `[Discourage] PlanDrain=1` (needs `DeferFootprint=1`; ini default 0), runtime switch `sdMode` (`--ab plandrain`, `native_set plandrain`). In `PlanLockedBody`, after the original's early outs and before `G11PlanBegin`, `DiscDrainForPlan` (discourage.cpp) calls the existing `DiscDrain(true)`: `g_rgOffSimOpen++`, `WaitAsyncDone` (serves the job's managed calls), `+0x918` uncontended, every record applied in FIFO order by the game's own originals, release. Skipped (counted in `sdSkip[]`, today's defer-on behaviour) when: inside a job callback (`g_inInvoke`: the wait would wait for the job that waits on us), this thread already owns `+0x918` (no nested drain), the P5c lent lock is ours (the wait would hand it back and switch P5c off), a drain runs, or an Add record is queued (it may fire `World_FireEvent 0x11`, IC points only). Not counted at all: another thread (the worker's own plans see pre-call state by design, §10.2 row 1), a P4 clone run, switch off, empty queue.

**Why it cannot be worse than `DeferFootprint=0`:**
1. Same world at the plan [verified apply path; per-record equivalence = I-89-6]: every call made before the plan is applied in call order by the originals (U with `force = 1`, phase 2 with the caller's `+0x978` replayed).
2. No new wait [verified]: with a job running, `RunPlan` already waits for it (`HaNote 0x05000000`); the drain does the same wait a few us earlier (now under key `0x06000000`), then the plan finds no job. With defer off, the sim waits at every `UpdateSimFootprint` call during a job instead. With no job, `DiscDrain` does not wait.
3. No new lock order [verified]: the drain takes and releases `+0x918` before the plan's own acquire at `0x82C733`; never nested (skip rule own hold). Lock model `tools/model/p5-lock-model.ts`: `pd-base`, `pd-p5c` clean; mutation `mut-pd-nolentskip` (no lent-lock skip) is caught as a P5c hand-back.
4. No new managed reach [verified rule]: Add records block the drain; Remove, U and phase 2 are managed-free (docs/84 §7.1, §1).
5. Where it skips, behaviour is today's defer-on; `sdSkip[]` must be ~0 in the loop window for the equality to hold there.

**Side effects [verified code].** P10's `p10PlanUs` and plan-deadline sessions include the drain time (it runs inside `PlanLockedBody`). The goal step (gate 10, `0x75C2D0`) runs before `Route_PlanLocked` and still sees stale footprints [inferred effect]; a drain at its entry is a later step, only if the verify shows rejected goals rather than failed A*.

**Counters v107** (+0x4588..+0x45C3; after the v106 layout of merge eaef8a1b): `sdReady`, `sdMode`, `sdChecks` (owner sync plans with records pending), `sdDrains`, `sdRecs`, `sdJobRan` (the plan would have waited too), `sdUs` / `sdMaxUs`, `sdSkip[5]` (0 Add queued, 1 job callback, 2 `+0x918` owned, 3 P5c lent, 4 draining), `sdFail[2]` (failed plans: 0 after a drain, 1 after a skip).

**Hypotheses the counters separate (ledger I-89-13-1, I-89-13-2):**
- **H1, refill** [inferred]: in a crowd a job runs almost all the time and `0x833560` is called from both animation passes every tick, so the head record stays younger than 250 ms at most polls and each Replan sees a world one deferral window old. PlanDrain fixes H1: the loop stops in the on slices with `sdDrains` rising.
- **H2, lost update** [inferred]: the record that matters (the exit transition, the boat's re-home) is dropped or wrongly collapsed (`usfGoneEnable1`, `usfIdMismatch`, `usfCollapsed` across a parent change, `fp2Dropped`). A drain cannot fix H2: the loop persists with `sdDrains` rising and those counters move in the loop window. Flat counters with the loop unchanged = H3, the deferral is not the cause.

**Offline checks.** Selftest `TestPlanDrain` (10 cases through `PlanLockedHook` with a fake plan: job running and idle drains, Add / callback / own hold / P5c lent / draining skips, other thread / clone run / switch off / empty queue untouched, failed plans after drain and skip, a footprint call inside the plan passes through after the drained records). **Live verify (not run):** `ip-boat-exit-defer-loop` with `DeferFootprint=1`, `DeferFootprint2=1`, `PlanDrain=1`, `LogFallbackStack=16`, `--ab plandrain` ABBA in one load: fired = `sdDrains`, `sdRecs` > 0 on, flat off; worked = no >= 3 failing Replans of one Sim within 5 s after the boat exit in the on slices, `sdFail[0]` small next to `sdDrains`, hitch counts not higher on; safety = `usfApplyInJob`, `fp2ApplyInJob`, `fp2FullPass` 0, `p5cHandOver` unchanged, `sdSkip[0..3]` ~0.

## 14. DeferPrivateFp: the private footprint remove (2026-10-24, built 0.36.42 as `out-privfp`, offline; no game run)

**Why.** SV cause run 2026-10-24 (`docs/data/results/2026-10-24-sv-cause-run-analysis.md`, cause #3, release defaults with `DeferFootprint=1`, `DeferFootprint2=0`): sim-thread waits on `+0x918` behind our async route jobs at two footprint keys, 4 frames >= 300 ms, 1,297 ms. Over the whole 45-min HitchAttr record (`probe-out/profile/sv-cause-run-2026-10-24-sv-cause-run-2700-hitches.jsonl`, longest wait per frame) [measured]: `0x82A0F6` 62 frames, 4.1 s, max 257 ms, 3 frames >= 300 ms; `0x8334CC` (§12, phase 2 FromTool) 139 frames, 5.5 s, max 314 ms; `0x831BF4` (AddPrivateFootprint) 14 frames, 0.5 s, max 168 ms. The analysis's "RemovePrivateFootprint" is the hitch-attr-report name of key `0x82A0F6`.

### 14.1 Functions [verified disassembly unless marked]

| What | Address | Proof |
|---|---|---|
| wait key | `0x82A0F6` = return of `call Lock_Acquire 0x4E16F0` at `0x82A0F1` in `RoutingWorld_RemoveFootprintLocked 0x82A0C0` | `0x82A0E2..0x82A0F6` |
| body | `0x82A0C0 thiscall(world; obj, flag)`, `ret 8`, `al`: early out (`+0x974` clear or `obj` NULL: 0, no lock), lock, lot of the object `0x8297C0(obj)` (component, else `0x68EC50`), key `0x7CFA30(2, 0x19, id)` (cdecl, pure shifts and ors), world `+0x994 = 0`, `0x635700(P; lot, key)` removes that one footprint from the lot quadtree; if removed and `flag`: the routing component's private footprint `C+0x38` freed by `0x947880(0)`; unlock; `al` = removed | `0x82A0C0..0x82A183`, `0x7CFA30..0x7CFA6B`, `0x947880..0x9478B2` |
| caller (waits) | `IC_RouteManager_RemovePrivateFootprint 0x754610`: `FindById`, AddRef, routing world `0x8292D0`, `call 0x82A0C0` at `0x75465E` with `flag = 1`, release, `al` returned | `0x754610..0x754673` |
| caller (no wait) | `LockedLotUpdate` `0x82B551`, already under `+0x918` | §12.1 |
| script | `Route.RemovePrivateFootprint` <- `DynamicSimFootprint.set_Enabled(false)` <- `SimScriptAdaptor.OnAboutToStartFollowingRoute` (`ScriptAdaptor.PreRoute`, `FollowRouteAction.PerformRoute`): the Sim drops its standing footprint as its route starts | stacks of the `0x82A0F6` frames >= 100 ms (most name `OnAboutToStartFollowingRoute`, the rest `PerformRoute`) [measured] |
| result use | EA `DynamicSimFootprint.set_Enabled` `IL_014e: pop`; simler90's replaced `Sims3GameplaySystems` (UPDATE242) the same `IL_0149` / `IL_014e: pop`; ErrorTrap's own `ScriptCore` only wraps the IC; no other installed assembly names `RemovePrivateFootprint` (81 assemblies scanned) | ilspycmd IL [verified 2026-10-24 on this install] |
| add | `IC_RouteManager_AddPrivateFootprint 0x75DCF0` -> `0x831BA0` at `0x75DE1A`, key `0x831BF4`; ~0x640 bytes with string and lot lookups | entry `0x831BA0..0x831C9F` read, rest not; whole body audited in §15.2 |

### 14.2 What it does

`[Discourage] DeferPrivateFp=1` (default 0), runtime switch `pfMode` (+0x4740, `--ab deferprivatefp`), counters **v113** (`pf*`, registry `native-v113`). No new patch: phase 2's two call sites (`0x75465E`, `0x75DE1A`, now patched once for both features by `PfSitesInstall`) do the work. Needs phase 2 installed: its detours keep an object's later FromTool / UpdateObjectLot calls behind its queued remove (order rule). New `DeferFootprint2=2` installs phase 2 with `fp2Mode = 0` (order rule only), so the private remove can be A/B'd without phase 2's own deferral.

Remove site (`PfRemoveSite` -> `PfRemoveDefer`), same skeleton as phase 2:

1. **Pass through** (`pfPass`; today's call after a flush of the object's records): not the owner thread, P4 clone run, `obj` NULL, `+0x918` owned, job callback, draining, world not ready or not the learned lock, closing.
2. **Order rule:** the object has any queued record (D, U, phase 2, private): queue in any mode; else queue only with `pfMode = 1` while a job runs; else today's call.
3. **Duplicate:** the object's newest record is the same private remove (same pointer, flag): dropped (`pfDup`); the second remove would find nothing (`0x635700` no, the flag branch skipped).
4. **Queue full:** drain (no Add queued) then today's call; with an Add queued the call runs now, out of order (`pfFullPass`, expect 0).
5. **Record** `{pf, id, flag, object pointer (no reference), world, tick}`, answer **1** (`pfDeferred`).

**Apply** at D's drain points (next D call with no job, START / READY / CANCEL polls, save, travel / quit, D-X destroy) holding `+0x918` with no job: find by id + pointer check, then the original `0x82A0C0` with the recorded flag (`pfApplied`; tripwire `pfApplyInJob`). **Gone** (destroyed inside a job callback, left queued): with `DeadSweep=1` the private key `0x7CFA30(2, 0x19, id)` is removed from every lot (`0x635700` lot 0, world `+0x994 = 0` as the body does; `pfSwept`); the record never touches the object or its component (the component went with the object); with `DeadSweep=0` dropped. **Reused id:** dropped (`pfDropped`). A U record before a private record of the same object is not collapsed into it (the U collapse only looks at U records). Ledger: `pfDeferred = pfApplied + pfSwept + pfDropped` after every drain.

**Add site** (`PfAddSite`): not deferred (shortcut: the body is long and not audited). An object with a queued record is flushed first (`DiscDrain(true)`, waits for the job as today; `pfAddFlush`), so the add always follows the remove.

### 14.3 Safety

| Question | Answer | Tag |
|---|---|---|
| Can the body run during a job? | No: every apply is inside `DiscDrain` / `DiscOnDestroy` holding `+0x918` with no job (tripwire `pfApplyInJob`) | verified code + selftest |
| What does the route job see? | The private footprint present, as today (today the remove waits for the job's end) | verified (the record moves the call to after the job, where it ran anyway) |
| Order with the same object's footprint writes | One FIFO with D, U and phase 2 records; the order rule queues every later U / FromTool / lot / private call of the object; Add flushes first; D's non-deferred Add/Remove drain first | verified code + selftest |
| Freed memory | The record keeps the pointer only for the pointer check (never dereferenced); FindById + AddRef at apply; destroy applies first (D-X, `0x939420` gate); design G (`P5cDiscHas`, docs/88) sees the queued id and keeps its wait, unchanged; gone = sweep by id key, no object access | verified code + selftest |
| Script reads the result | Popped by every installed caller | verified IL |
| Sim-thread readers between record and apply | Sync plans, steering `0x85FE10` / S2, gates, slot checks see the Sim's standing footprint until the drain: stale for at most one drain window (no timed drain: §13 "Why"). The Sim's own route: route queries skip the routing object's own footprints [inferred, not checked for key type 0x19]. Other Sims route round a footprint that is empty. **Corrected in §15.4:** the private footprint is the turn helper of horses and boats and counts only for its owner's route search (walkable class 1, cost x35 or x200), never for other Sims or steering; the stale window costs only the owner's own sync replans | refuted in part (§15.4, verified disassembly) |
| `C+0x38` readers | Freed later than today; nothing needs it gone before the apply [inferred: the setter `0x947880` is the only writer found; readers not enumerated]. **§15.3:** the only reader is the private footprint rebuild in `UpdateObjectFootprintCore` (`0x832494`, sim thread, under `+0x918`, not in the job closure); the object's U / FromTool calls are queued behind the record | verified (displacement scan, §15.3) |
| Managed reach of an apply | none: `0x82A0C0` calls `0x8297C0`, `0x7CFA30`, `0x635700`, `0x935770`, `0x947880` (destructor `0x628E60` + free) | verified direct callees; `0x628E60` not followed |

PlanDrain (§13) is not used or extended (it crashed in game; no drain inside `PlanLockedBody`).

### 14.4 Offline checks

`build.cmd out-privfp`: selftest ok. `TestPrivateFpDefer` (phase 2 with `fp2Mode 0`): no-job call with its result; in-job record answered 1, the object's FromTool and U calls queued behind it, another object's FromTool runs now, nothing applied while the job runs, call order at the poll (`gXfP`); duplicate dropped, not across another record (`XpX`); mode 0 with and without a queued record; pass-throughs (own `+0x918`, job callback, another world); Add after a queued remove flushes (waits for the job) then adds, Add with nothing queued untouched; destroy while queued (applied first, other object kept); gone at apply (key sweep on lot 0 with the `0x7CFA30` key, `+0x994` cleared), reused id dropped, DeadSweep off drops; queue full with and without an Add; ledger and tripwires 0. `hook-sites.ts --check` ok (`0x7CFA30` row in docs/68). `p5-lock-model.ts --self-check` ok (no new lock order: the drain is D's).

### 14.5 Live jobs (drafts, not run)

`tools/harness/repros.json`: `sv-private-fp-loop` (SV, 30 spawned Sims + household go far and home, ini `DeferFootprint2=2`, `DeferPrivateFp=1`, ABBA on `deferprivatefp` only) and `sv-deferfp2-alone-loop` (same crowd, `DeferFootprint2=1`, `DeferPrivateFp=0`, ABBA on `deferfootprint2` only: phase 2 was never measured outside the lock-taker bundle). Read with `hitch-ab.ts` and `wait-ab.ts`.

**Expected [inferred].** Key `0x82A0F6` ~0 in the on slices (SV: ~4.1 s of sim wait and 2 freezes >= 300 ms per 45 min, ~0.5 s of cause #3's 1.3 s freeze time; phase 2 alone the other ~0.6 s through `0x8334CC`). **Risk:** §11's conservation: the next `+0x918` taker in the same tick (the Sim's first steering pass `0x85FE10`, `0x833593` of its own U calls now queued behind the record, gate 0) may wait out the same job, so the net gain can be ~0; the A/B decides. Other risks: `pfAddFlush` moves a wait to the Add when a Sim stops within one drain window; a stale standing footprint for one window in a crowd. Ledger docs/51 §317.

## 15. AddPrivateFootprint audit and the private footprint's readers (2026-10-24, static; no build, no game run)

Question from §14: can `IC_RouteManager_AddPrivateFootprint` (key `0x831BF4`) be deferred like the remove, and who reads the routing component's private polygon `C+0x38` and the footprint it inserts. **Verdict: not deferred** (§15.5). Two §14.3 claims are corrected (§15.4).

### 15.1 What the private footprint is [verified IL]

`Route.AddPrivateFootprint` has one game caller, `DynamicSimFootprint.set_Enabled` (`managed-callgraph.ts callers`). Add (`IL_00e8`, result popped at `IL_00ed`) and remove (`IL_0149` / `IL_014e: pop`) run only when `sim.IsLargeAnimal` or `boat.UsesTurnHelperFootprint()`: the private footprint is the **turn helper** (12 vertices from `UpdateTurnHelperFootprintShape`) of horses and other large animals and of boats. Every Sim's 4-vertex standing footprint is the discourage footprint (D), not this one. The IC's bool argument is `boat != null`. Add runs when `Enabled` goes true (the animal stops), remove when it goes false (`OnAboutToStartFollowingRoute`).

### 15.2 IC and body [verified disassembly]

| Step | What | Where |
|---|---|---|
| IC | `0x75DCF0`: `ScriptHost_GetWorld`, `ObjectService_FindById` + AddRef, `RoutingWorld_Get`; the script world's vtable `+0x78` marshals the managed `Vector3[]` into a temp buffer; the IC copies the given count into a 16-byte point vector; `call 0x831BA0(obj, &points, flag)` at `0x75DE1A`; `EA_Free` the vector; Release; `al` returned. No world, object service, object or routing world: 0, no call | `0x75DCF0..0x75DE83` |
| early out | `thiscall(world; obj, points*, flag)`, `ret 0xC`; 0 without the lock when `+0x974` is clear, `obj` NULL or fewer than 3 points | `0x831BAC..0x831BD8` |
| lock | `+0x918` acquire `0x831BEF`, key `0x831BF4` | |
| lot | world manager `[0x11ECBC4]` NULL: 0. Object's lot id `0xB03750(obj, 0, &out)`; lot id 0, or equal to `+0x48/+0x4C` of the lot `0xC88FD0` returns, with `flag` 0: 0. `WorldManager_FindLotByIdTyped 0xC62D40`; NULL with `flag` 0: 0 | `0x831BF9..0x831C8C` |
| polygons | temp world polygon (`0x6298B0`, tags "Routing/RoutingFootprintPolygon", "Routing/FootprintPoly"), x/z of every point; object transform `0x74B9B0` -> `0x958EE0` **read now**, inverse `0x82AAE0`; with a footprint component (`0x749680`): `EA_AllocTagged(0x24, "Routing/PrivateFootprintPoly")`, every point to object-local space by `0x82A610`, `0x947880(new)` stores it at `C+0x38` (old one destroyed and freed) | `0x831C8E..0x831F7E` |
| old footprint out | key `0x7CFA30(2, 0x19, id)`; `LotRouting_RemoveObjectFootprints 0x635700` on `world+0x95C` (lot id, or 0 without a lot) | `0x831F83..0x831FC1` |
| new footprint | `EA_AllocTagged(0x48, "Routing/PrivateFootprint")`, `Footprint_MidCtor 0x80EED0`, vtable `0xFEBBC0`; AddRef `+0x90`; key `+0xB4`; owner id `+0x40/+0x44` = object id; lot id `+0xC/+0x10`; `+0xD0` -> byte `+0x36` (lot lookup's out, 0 without a lot); `+0xB0` -> type byte `+0x35 = 0x19`; `+0xCC` -> class byte `+0x34 = 1`; steering component (`0x935770`, `0x953690 & 0xCF00`, `0x55DA60`) picks the multiplier `+0x1C` (`+0xC8`) = 35.0 (`[0x1166100]`) or 200.0 (`[0x1166104]`); `+0x4C(0)`, `+0x54`; `Footprint_SetLevelPolygons 0x80F6A0` with the **world** polygon | `0x831FC6..0x8320E8`; setters `0x620CD0` (`+0x1C`), `0x620CE0` (`+0x34`), `0x620CF0` (`+0x36`), `0x620D10` (`+0x35`) |
| insert | lock-key map (`world+0x8C4` non-zero and `RoutingWorld_LockKeyFind 0x82AD60` finds the lot in `+0x8B8`): parked in the node's list (`0x8295E0`, `0x82DC70`), node `+0x14 = 1`, `LockedLotUpdate` inserts it later; else `PathPlanner_AddFootprint 0x6376D0` (lot quadtree, map M, water grid: docs/84 §2, docs/100, docs/156) and world `+0x994 = 0` | `0x8320ED..0x8321A4` |
| end | Release `+0x94`, temp polygon `0x628E60`, unlock, 1 | `0x8321AB..0x8321D4` |

Writes: `C+0x38` (alloc + free), lot quadtree / map M / water grid through `0x6376D0`, the lock-key map node list, world `+0x994`, the new footprint object. The direct callees in the steps above fire no event and reach no managed code [verified direct; `0x6376D0` clean per docs/84 §7]. Same structure as the private footprint rebuild in `UpdateObjectFootprintCore` (`0x832646..0x83284D`).

### 15.3 Readers of `C+0x38` [verified displacement scan]

Scan of every function that loads the footprint component bit `[0x11E8FA8]` (incl. `Object_GetFootprintComponent 0x749680`), of the functions around its 6 call sites, and of the component's methods `0x945000..0x949000`, for `[reg+0x38]` on a non-stack register; each hit checked by hand:

| Function | Access | Thread |
|---|---|---|
| `UpdateObjectFootprintCore 0x8321F0` | read `0x832494`: with a transform component, rebuilds the key-0x19 footprint from `C+0x38` x the **current** transform (remove by key `0x635700`, new `0xFEBBC0` footprint, insert or park) | sim, under `+0x918`; not in the job closure (§3.2) |
| `0x947880` | setter (frees the old polygon); callers `0x82A15B` (remove), `0x831F7E` (add) | sim, under `+0x918` |
| `0x946FB0` | component ctor, `+0x38 = 0` | sim |
| `0x9478C0` | component dtor, frees `+0x38` | sim (object destroy) |

Other hits (`0x806784`, `0xB58092`, `0xB6DB28`, `0xB74D1D`, `0xD44C2A`, `0xABF287`, `0xAC5D12`) are other objects (footprint arrays, the object service's hash table). Not excluded: reads through a stored component pointer in a function that never loads the bit [inferred none]. The route worker never reads `C+0x38`.

### 15.4 Readers of the inserted footprint, and corrections to §14.3

The footprint class `0xFEBBC0` answers the route search's class and multiplier slots with `0x80F840` / `0x80F7D0` [verified disassembly, matches docs/98 §15.3]: class byte `+0x34` and multiplier `+0x1C` **only when the routing agent's object id equals the owner id `+0x40/+0x44`** and RouteOption `IgnorePrivateFootprints` is clear; otherwise class 0 and 1.0; with no agent 0 and 1.0. Class 1 is walkable (docs/98 §1).

| Reader | Sees the private footprint | Tag |
|---|---|---|
| Owner's route plans (sync on the sim thread, async on the worker) | yes: walkable, cost x35 (x200 with the steering flags) inside the turn-helper strips | verified |
| Other Sims' route plans | no (class 0, multiplier 1.0) | verified |
| Steering / avoidance (`0x82EDB0` visitor: the frame clears the option-object word, docs/84), S2 clone | no: no agent gives class 0 | verified (slot bodies; agent NULL per docs/84) |
| Other quadtree visitors (placement F-G, slot checks, LOS, `IC_Query_GetSimsOverlappingFootprint`) | not enumerated; those that use slot `+8` see class 0 without an agent | open |

So §14.3 is wrong on two points: the owner is the **only** Sim that sees its private footprint, and other Sims never route round it. Effect on `DeferPrivateFp=1`: during one drain window the moving animal's own sync replans pay x35 / x200 over its old turn-helper strips (walkable, so no "no path", only a costlier first leg); its async plans see the remove applied (START drains first). I-89-14-3 still holds (the late remove does not block the walker), now for this reason. `C+0x38` freed late: the only reader is the rebuild in §15.3, and the object's U / FromTool calls queue behind the record (order rule); an unhooked event-9 rebuild in the window re-inserts the turn helper at the new position, which the deferred remove then takes out by key: end state as today [inferred from the bodies].

### 15.5 Defer the Add? No

1. **Gain too small.** Key `0x831BF4` in the 45-min SV cause run: 14 frames, 0.5 s, max 168 ms, **0 frames >= 300 ms** [measured, §14]; only horses and boats call it. The hitch target (>= 300 ms) gets nothing, and §11's conservation applies on top.
2. **Not exact.** The body reads the object's transform and lot at apply time. Deferred, `C+0x38` would be the call-time world points in the apply-time object frame; if the animal turned or moved in the window (idle turns, slot snaps), every later rebuild by `0x8321F0` would place the turn helper rotated or shifted by that delta until the next Add [inferred from the body]. An exact deferral needs the call-time transform: a copy of the 0x640-byte body (both polygon classes, the `C+0x38` allocation) or a temporary transform swap on a live object. Neither is "clearly safe".
3. **Owner's next sync plan** in the window would miss the turn helper (the animal may turn on the spot); async plans would see it (START drain).

**If `pfAddFlush` shows up** (I-89-14-5, read from `sv-private-fp-loop`): the cheap option is an order-only Add (queue it only when the object already has a queued record, so the flush stops waiting for the job), accepting point 2 for the owner's own cost map. About 1-2 h + 1 game run. Not built. Ledger docs/51 §320.

## 16. LostRemovalFix: a queued UpdateObjectLot always reaches the lot map (2026-11-07, offline; no game run)

Player report 2026-11-07 (v0.2.8): oven and fridge plans fail with result 16, candidates 1/1/0, for household Sims too, forever. The earlier view that the object was lost to EA is wrong; the deferral lost EA's removal.

**Vanilla's chain [verified disassembly].** An object leaving a lot or the world fires object event `0xA`: owner vtable `+0x3C` (`0xACE060` lot at `0xACE07A`, `0xAA8FF0` world at `0xAA8FFC`) before `0xC70150` clears the owner, and `0xB6E19C` on a lot change. The handler registered at `0x94908F` is `0x946F70`; it calls `RoutingWorld_UpdateObjectLot 0x82D280` at `0x946F99`. `0x82D280` reads the lot with `0x8297C0` and calls `0x82C120`, then `0x82B450`, then `LotRouting_RemoveObjectFootprints 0x635700`, all under `+0x918`. It only removes: §12's "re-home" is wrong, the tail of `0x82C120` is portals (`0x6399E0`). Event 9 re-inserts through `0x946160`: `0x82D280` at `0x9462C9`, then `0x833680` at `0x9462D4`, or `0x833680` alone at `0x94630A`.

**Paths [verified disassembly, a also from logs].**

| Path | What happens | Evidence |
|---|---|---|
| a. Object gone | A job runs, `0x798D40` queues event `0xA`, kind 2 record queued; `DestroyById` parks under DestroyParkDx; the drain finds no object and drops the record | `b02e08a4` log: "drop: 16 ms ago, UpdateObjectLot, id B60B00F51D00F4C0, object gone" |
| b. Lot move during a job | `0x833680` is not deferred: it inserts on the new lot when the job ends, then the queued record reads the new lot and removes that fresh insert. The old lot keeps its copy. The same-lot sequence loses a live object's fresh footprint | static; `0xAB466E`, the other event 9 firer, not read |
| c. DestroyPark, no records | `DestroyById` fires owner `+0x3C` at `0x93947E` itself, after `P5cPark`, independent of DestroyParkDx; the record meets a gone object at the erase `0x9394A4` | static |
| d. DestroyParkDx=1 | Enabler of a: with `dxMode` 0 a queued record keeps the wait and D-X applies it while the object is in the hash (the owner is cleared: lot 0, all lots, removal works) | static, default on since `056fd225` |

DeadSweep covers Remove, usf disable and pf removes only, not kind 2 records [verified in code].

**Any object.** Paths a and c hit every object destroyed with a job running, not only a region's DynamicSlot. `stove-wall-repeat` on the diag2 build caught a `SocialJigOnePerson` in play: `Fridge_CleanOutBadFood` result 16, 1/1/0, blocker object `0x38B400F51D68B5A0` with no object, footprint #0, type 1, bounds on the fridge slot [measured]. Jigs are made and destroyed often. The fix sits in the shared kind 2 path, so it covers them; the repro has a `jig` target.

**Fix, `[Discourage] LostRemovalFix=1` (default on, 0 = the old rules; counters v172, switch `fp2lrMode` +0x5DE8, `--ab lostremoval`).**

1. `Fp2Hook` records the lot of the call (`0x8297C0`), as `0x82D280` would read it.
2. `DiscApplyOne` calls `0x82C120` with id and that lot, object alive or gone. That is vanilla's body: lock-key map branch, world lot to 0, portals, `world+0x994`. It takes no object. Only the private key removal at `0x82B551` needs one (`0x82A0C0` skips NULL at `0x82A0D8`), so a gone object's hit sweeps that key as the pf branch does. A reused id stays dropped.
3. `0x833680`'s two call sites go through `UofSite`: an object with such a record gets its records applied first, under the same job wait and `+0x918` the insert takes next. Nothing new waits.

**Limits.** Inside a job callback or with `+0x918` held the flush cannot run (`fp2lrOrderMiss`, expected 0): the late remove then takes the insert. A queued Add of the object stays queued past the flush, since an Add may fire `World_FireEvent 0x11` (also `fp2lrOrderMiss`).

**Counters.** `fp2lrApplied`, `fp2lrGone`, `fp2lrGoneHit` (footprints that would have stayed), `fp2lrMoved`, `fp2lrFlush`, `fp2lrFlushUs`, `fp2lrOrderMiss`, and `fp2lrLost`, fix off only: a dropped gone record with a map entry, or an apply on another lot than the call's. `diag: mem` lines carry `fpLost`, `fpLoad` and `fpSaved`, no walk, so a player's old save A/B shows them over time (queue `footprint-ghost-oldsave.json`).

**Offline.** Selftest `TestFp2LostRemoval`: paths a, b, c and the gate, each against a small lot-map model of vanilla's order, off arm reproduces the bug; order misses; the flush keeps other objects' records.

**Repro `footprint-ghost`** (built, not run): the worker holds a job at its start (`[Discourage] GhostTest=1`, ops 20-29 on the 0x87 channel, never in the shipped ini), the region's Cleanup falls inside it, then a plan to the slot. Stove, oven (same region, from 1 m), fridge, a jig on the fridge slot; arms by op 27 in one load, N = 10. Why a job is needed: `routediag-result16` fails on the v0.2.9 pair because its diag probes keep a job live at Cleanup, and passes on the master run that had none. Same code, same ini on this path [verified diff, log parks]; the hold makes both pairs fail alike.

## 17. GhostSweep: dead object footprints at world load (2026-11-07, offline; no game run)

A ghost is saved in the `.nhd` (`LotLevel_SaveFootprints` writes every entry, docs/25), re-added on load, and removed by EA's pass `0x639B40` at world-load end, which drops entries with no object, on a normal load. It survives only if a lot's routing load fails partway [verified disassembly; lot failure rate unmeasured]. `footprint-ghost-save`: the made ghost was gone after save and reload, and the census counted 0 ghost ids on all 4 loads, so GhostSweep has work only after such a failed lot load. `[Discourage] GhostSweep`: 0 off, 1 count only (code default when the key is missing), 2 remove (v0.2.10 ini).

**One pass, inside the load screen.** From the ts3fixes `world:` line (`OnWorldLoadFinished`, all lots loaded, docs/38), sim thread, no job, under `+0x918`. Never in play. A live census would need a per-tick node budget, a walk by bucket index, and the recheck at removal `FpgRemove` already has. Time logged (`fpgUs`).

**"No object" is not a ghost test.** A clean Baseline has 86,954 of 126,787 entries without an object [measured, diag2], mostly non-object ids such as `0000000002E31605` (class `0xFEBA98`) and `00001179038B0C05` (class `0xFEBF90`). The signature, all of it:

- Object-shaped key: low nibble 0 [observed on every object id so far, fails safe], high part set. `0x7CFA30` keys have bit 0 set [verified].
- Class `0xFEB848`, built by `0x80C780` in `UpdateObjectFootprintCore 0x8321F0`, which `AddDiscourageFootprint` reaches at `0x834810` [verified]; footprint id equals the key; type byte `+0x35` = 1 (the caught jig ghost: type 1 [measured]).
- FME Q set, id not parked, no object.

Whether this is tight is a measurement, not yet made: the class a made ghost has is [inferred] (`0x8321F0` also builds `0xFEBBC0`). The census logs each object-shaped key without an object that fails the signature, by class. Bar for mode 2 as first set: a made ghost is found, a clean Baseline counts ~0 or each hit is explained. v0.2.10 ships mode 2 by Rodney's call, ahead of that bar: no made ghost reached the census, so the removal has run only in selftest. If the two cannot be separated, the ini goes back to 1. A failed read keeps the entry. No save format change, no mod data in saves.

**Repros.** `footprint-ghost-save`: ghost with the fix off, save, reload count only, reload remove, save, reload. `footprint-ghost-census`: three clean Baselines. Queue `footprint-ghost.json`.

## 18. RemovalFirst: a route does not plan past a queued removal (2026-11-07)

Player report on v0.2.10 (0.36.168d, LostRemovalFix=1, census 0 ghosts): baking, household Sim Wayne at the stove; `BakingDish_CookInOven` plan from the stand spot failed once (result 16, 1/1/0, job 0 ms, 200 ms from START), EA's retry (60 candidates) walked him 22.9 m outside, the next click worked.

**The window [verified code].** Records are applied only by the owner thread, at START / READY / CANCEL polls (`DiscPoll`), when no job runs or the oldest record is 250 ms old. The worker takes the next queued job at once, with no drain and no poll in between. So a removal queued behind job A, followed by a START of job B before age 250 ms, gives B the old lot map. PlanDrain covers sync plans only and is off. Longest window with the sim ticking: 250 ms plus the rest of the running job; ageMax 21 s in that log matches its longest job, sim waits max 61 ms, so that one was not a ticking sim [inferred].

**Why it hits the Sim's own route [verified C#, inferred for the run].** `BakingDish_Prepare.Cleanup` -> `ImpassableRegion.Cleanup` -> `DynamicSlot.Destroy`, then `BakingDish_CookInOven.Run` plans to the same stove. During a job: `DestroyById` parks and a kind 2 record is queued (§16 path a). A dead region on the slot gives exactly this result (`footprint-ghost` arm 0: result 16, 1/1/0, for stove and oven) [measured]. No run logged the queue at the failed plan: the inferred step is that the record was queued there. Vanilla and the old wait at destroy have no such window.

**Fix, `[Discourage] RemovalFirst=1` (default on, 0 = off).** `AsyncStart`: a route not known unseen (`kdScope != 2`, not marked: household, seen NPC, unknown) with a removal queued (kind 2, plain Remove, pf; not usf, Add or kind 1, which refill every tick) parks as `J_PEND`; `FbRetry` holds it while the removal is queued, up to `kFbRetryTicks`, then queues the job on the normal path. This works with M1Retry off. The sim thread does not wait: the plan waits in its task as for any job, and the first no-job poll or the existing 250 ms drain ends it. Known unseen Sims keep today's behaviour. Log line `footprint removal first: mode, parked, capped, max ticks`; capped expected 0. No counter fields: the hotfix keeps its layout.

**Limit.** A refused START's sync plan (57 refused, 2206 started in that log) can still plan past a removal; `PlanDrain=1` would cover it (§13, no live verify yet).

**Offline.** Selftest in the M1Retry block: park, tick wait, job queued, record kinds, switch, tick cap, CANCEL, world closing, Plan without READY, scope 1 parks and scope 2 queues. **Live:** `footprint-window` on v0210d: fix off, 14 window fails in 14 held tries; fix on, 0 in 16; parked 42, capped 0.

**Repro `footprint-window`** (queue `footprint-window.json`, build v0210d). `footprint-ghost` cannot show this: it sleeps 30 ticks after Cleanup, so its plan comes after the drain. Here `Route.Plan` follows Cleanup with no sleep, on a route that already has its destination, and op 31 ends the hold once that START is queued or parked. Ops 30-34 exist only with `/DTS3N_RFTEST`: the release build has no extra op, and its hold loop calls a stub. N = 10 per arm, stove and oven.

**J_PEND audit [verified code, selftest].** M1Retry is off in the shipped ini, so RemovalFirst is what makes this path live. Household (scope 1) and unknown (scope 0) park, known unseen (scope 2) and marked routes do not. CANCEL: `DropJob` frees the slot. Plan without READY: the C# poll cap is 1000 ticks against 30 here, and the IC branch plans sync. World closing: READY gives up with M1Closing, sync plan. No READY at all (task gone, unload): the worker never sees `J_PEND`, `AsyncBusy` and waits ignore it, the slot stays until a START needs it (5 s old), a repeat of the handle is dropped or planned sync. A parked job holds no lock or reference. Scope after a give-up: the sync plan counts as unknown, so as household.
