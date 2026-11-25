# 333. Our two background loops on unbounded worlds: cost now, scaling, off-thread value (2026-11-23)

Rodney (2026-11-23): "what's the total cost now and how large will that grow if we have unbounded worlds? If we do move it off
thread, will that help us later on unbounded worlds? We investigate first." Investigation only: no build, no game run, no queue.

Subject: the ts3fixes dead-reference sweep (`DeadRefs.cs`, sub-switch `deadrefcadence`) and the viewer push (`UnseenViewers.cs`
`ViewerPushLoop`, sub-switch `viewerpushdelta`), both default ON. Sources: docs/330 (branch `feature/ts3fixes-loop-cost-1122`,
§8.2 timing 2026-11-23, build `-1122d`), the per-slice counters in that lane's
`probe-out/profile/ts3fixes-loop-cost-time-1122-2026-11-23-{bp,ip-...-retry,sv}-loopcost-ab-ab-fixes.jsonl` (slices 2-3 = on, 1 and
4 = off, 300 s each), docs/324 (far-Sim model, branch `feature/far-sim-model-1122`), docs/331 (far-object model, branch
`feature/far-object-model-1123`), docs/332 (packages, branch `feature/unbounded-packages-1123`), docs/121 / 122 / 125 and
`docs/data/ic-safety.csv` for the native side. Ledger §587.

Tags: **[verified]** read in our source, the decompiled scripts, our native source or the IC safety table; **[measured]** the
2026-11-23 timing run (one 25-min load per world, ~10 min per arm); **[inferred]** arithmetic or reasoning on those, to confirm
(docs/51 §587).

## 0. Short answer

- **Now (ON):** 11.9 / 14.4 / 15.6 ms/s (BP / IP / SV) [measured]: sweep 8.2 / 11.0 / 11.4, viewer push 3.6 / 3.4 / 4.2. The
  sweep is one ~47-50 ms pass per sim hour, spread in ~26 slices; at the harness speed that is 0.18-0.23 passes/s. Four fifths of
  a pass is the object step (phase 10, 3.65 us per world object). Worst slice 10.4-13.2 ms; viewer wake max 0.9-1.0 ms
  [measured].
- **Scaling with today's code (a):** nearly everything is linear in the whole world. Per pass ~62 ms x content factor, viewer
  push linear in `LotManager.Actors`. **4x: ~56 / 63 / 71 ms/s, 10x: ~139 / 155 / 175 ms/s** [inferred]. The unsliced phase
  gathers (world snapshot plus type filters, ~6 ms today) grow into a ~60 ms atomic slice at 10x, i.e. a once-per-sim-hour hitch.
- **With parking (b):** the docs/331 parked lots keep their objects in the world, and the docs/324 T3 Sims keep theirs. So the
  models alone do **not** bound these loops. They are bounded only when the loops skip parked lots and model-owned Sims (or
  when streaming / T4 removes the objects). Then: **4x ~10 / 10 / 13, 10x ~17 / 18 / 21 ms/s** [inferred]. What remains
  linear is the description phases (all SimDescriptions persist).
- **Off-thread:** low value for both loops. The world object list (`Query_GetObjects` `0x749630`) and the destroyed test
  (`Simulator_GetTaskImpl` `0x76CA60`) are sim-thread-only (PROXY, unlocked reads) [verified ic-safety.csv]. The holders are
  managed lists that the sim thread mutates. A worker would still leave the snapshot, the description phases and the apply on
  the sim thread: ~2.2 ms/s now, ~9 at 4x, ~22 at 10x (SV), with a ~35 ms atomic snapshot at 10x [inferred]. Bounding the live
  set plus incremental (change-keyed) bookkeeping does better at every size, with no worker.
- **Order (revised by the second pass, §9):**
  1. M-1, a counters-only measurement build. Its queue is written; it is not built or run.
  2. LC-1: the one-pass partition, plus the exact per-object cuts found in §9.1.
  3. LC-4 (viewer push without Actors walks) and LC-5 (reverse index for the owner, handler and cooking holders) in parallel.
     The use lists keep a slim walk.
  4. LC-7: description phases with reverse lookups.
  5. LC-6: far-model hooks, after FO-4 / FS T3, plus one docs/331 eligibility rule.

  LC-3 (static census) is done (§7). The per-lot dirty set is dropped: cross-lot holders are common (§7.2).

## 1. Cost now (ON, 2026-11-23 timing, in-load ABBA) [measured unless marked]

### 1.1 Per loop and per phase (ms/s, on arm)

| | BP | IP | SV |
|---|---:|---:|---:|
| **ours total** | **11.87** | **14.41** | **15.56** |
| deadrefsweep | 8.23 | 11.04 | 11.41 |
| - p10 merged object step (use lists, owner, ObjectDisposed handlers, cooking) | 5.52 | 7.66 | 7.94 |
| - Sim group p4, p5, p7-p9, p13, p15 | 2.38 | 2.97 | 3.04 |
| - description group p1-p3, p6 | 0.32 | 0.29 | 0.35 |
| viewer push | 3.64 | 3.38 | 4.15 |
| off arm, for reference: ours / sweep / push | 26.96 / 17.70 / 9.26 | 29.88 / 22.48 / 7.39 | 37.06 / 27.53 / 9.53 |

Source: `loopcost-report.ts` output in `probe-out/queue/chain-ts3fixes-loop-cost-time-1122-report.txt`, and docs/330 §8.2.

### 1.2 The pass: cost per sim hour against its spread ms/s

| | BP | IP | SV |
|---|---:|---:|---:|
| passes per real s (harness speed) | 0.177 | 0.222 | 0.230 |
| sim hour = real s | 5.7 | 4.5 | 4.3 |
| **ms per pass (= per sim hour)**, on / off | **46.6** / 101.0 | **49.8** / 101.9 | **49.6** / 120.3 |
| slices per pass, on / off | 25.1 / 54.9 | 27.0 / 54.8 | 26.6 / 63.4 |
| object walks per pass (event-gated, `kSimDestroyed`) | 0.78 | 0.88 | 0.83 |
| Sim-group runs per pass | 0.79 | 0.89 | 0.85 |
| description-group runs per pass | 0.35 | 0.32 | 0.35 |

- The pass is not an hourly spike. Its ~47-50 ms are spread at ~2 ms per slice, one slice per `Simulator.Sleep(0)`, and the
  ms/s is ms per pass x passes per second. At normal game speed (about 1 sim hour per 60 real s) the same pass would cost about
  0.8 ms/s [inferred: the pass is driven by sim time, `AddAlarmRepeating(1, Hours)`, DeadRefs.cs `StartDeadRefSweep`].
- **Pre-save pass** (`DeadRefSavePass`, called from `DisposeSpread.cs` before each save): relationship keys, aging states and
  alarm owners only. One sample: BP 581 us, nothing removed (`bp-deadrefs-check-cadence`, correctness run 2026-11-22)
  [measured, 1 sample]. No save happened in the timing run (`deadrefsweep_max_save_us` 0). It is linear in relationship keys,
  aging states and global alarm owners [verified source].

### 1.3 Hitch contribution

- Longest sweep slice (lifetime max over the load, the on arms included): 13.2 / 11.3 / 10.4 ms. BP and IP reached their max in
  an on slice; SV reached it in the warm-up slice, which ran with the switches on [measured, `deadrefsweep_max_slice_us`
  deltas]. The 2 ms budget is checked only between items, and each phase's **gather** (the snapshot, the type filters, the
  description lists) runs unsliced inside its first slice. The p5 gather (snapshot plus `Sim` filter, ~6 ms mean) is therefore
  the likely worst slice [inferred, I-587-3].
- Longest viewer-push wake: 1.02 / 0.90 / 0.87 ms [measured].
- Inside the IP >= 300 ms iterations: DeadRefSweepRun 2-3.4 ms, in off and on arms alike; ViewerPush never [measured, docs/330 §8.4].
  Both loops are a minor hitch source at 1x.

## 2. Per-item costs and what each phase grows with

On arm, ms divided by items or runs [measured]; the "grows with" column is from the source [verified DeadRefs.cs, UnseenViewers.cs].

| Phase | Items per run (BP / IP / SV) | Cost | Grows with |
|---|---|---|---|
| p10 object step | 10,915 / 10,784 / 11,452 objects | **3.65 us per object** (all three worlds) | every world object (`GetObjects<GameObject>`), x object-walk rate (one walk per pass with a `kSimDestroyed` event: grows with Sim churn) |
| world snapshot + type filters (gathers of p5, p6, p7, p8, p9) | over the same ~11k objects | ~2.6-2.8 ms per managed filter pass (~0.25 us per object), and ~6 ms for p5 (snapshot plus `Sim` filter) | world objects; 5 filter passes per Sim-group run |
| p4 household descriptions | 266 / 235 / 223 | 7.0 us | every SimDescription (`Household.EverySimDescription`) |
| p5, p13, p15 Sims | 97 / 66 / 81 | ~2 us per Sim (p13, p15); p5 is gather-dominated | instantiated Sims |
| p7 vehicles, p8 horseshoe, p9 darts | 61 / 37 / 26; 0; 0-7 | gather-dominated (the 2.6 ms filter) | objects of that type, but the filter cost follows all objects |
| p1 relationship keys | 220 / 193 / 204 | 4.1-5.8 us | relationship keys = descriptions with relationships; event-gated (disposals) plus a 4 h floor |
| p2, p3 aging states, alarm owners | 0 gone | 0.14-0.43 ms per run (a scan of all states / the alarm index) | descriptions, global alarm owners |
| viewer push, per wake | household <= 32, plus `LotManager.Actors` 1 walk in 4 | **~21 us + 0.74 us x Actors** per wake (fit over the three worlds; ~2.9 us per actor walked); 41-52 wakes/s | `LotManager.Actors` (every instantiated Sim); walks every wake while any NPC ride is in progress |

- Measured off-arm typed queries confirm the gather cost: `Objects<HorseshoeCourt>` returns 0 items and still costs ~3.1 ms per
  run (one native walk), and the managed `is` filter over the snapshot costs ~2.7 ms [measured, p8 off vs on]. A managed filter
  pass is about as dear as a native world walk, so the five filters per Sim-group run (~13 ms) are a cheap exact cut (LC-1)
  [inferred].
- Viewer fit: on-arm us/wake against Actors per walk, BP (91.9, 88.9), IP (65.4, 69.4), SV (81.4, 80.3). The slope is 0.74 us
  per actor per wake at 1 walk in 4 wakes, the intercept 21 us. The off arm (a walk every wake) has a slope of ~2.7 us per actor
  [measured points, inferred fit].

## 3. Projection at 2x, 4x, 10x the world content

Model [inferred, I-587-1]: content factor k multiplies objects, Sims, descriptions and Actors. At k >= 2, Sim churn is assumed to
make the object walk, the Sim group and the description group run on every pass. Pass rate stays at the measured harness rate;
if a bigger world runs the sim slower, ms/s falls but ms per sim hour stays. At 1x the model gives 8.25 / 11.05 / 11.41 ms/s for
the sweep against the measured 8.23 / 11.04 / 11.41.

### 3.1 (a) Today's code

| | 1x (measured) | 2x | 4x | 10x |
|---|---:|---:|---:|---:|
| BP sweep / push / total ms/s | 8.2 / 3.6 / **11.9** | 22.0 / 6.4 / **28.4** | 44.0 / 11.9 / **56** | 110 / 28.5 / **139** |
| IP | 11.0 / 3.4 / **14.4** | 26.1 / 5.7 / **31.9** | 52.3 / 10.4 / **63** | 131 / 24.5 / **155** |
| SV | 11.4 / 4.2 / **15.6** | 28.6 / 7.3 / **35.9** | 57.2 / 13.5 / **71** | 143 / 32.1 / **175** |
| ms per pass (sim hour), SV | 49.6 | 124 | 249 | 621 |
| worst slice: unsliced p5 gather, SV [inferred] | ~6 (max 10-13) | ~12 | ~25 | ~60 |
| push with an NPC ride always in progress (walk every wake), SV | - | 25.5 | 49.9 | 123 |

On a 10x world, NPC horse rides are likely in progress most of the time, so the push degrades toward its walk-every-wake row
[inferred]. **Everything above is linear in the whole world.** Not one phase is bounded by what is near the player.

### 3.2 (b) With the far-object (docs/331) and far-Sim (docs/324) models

What the models change by themselves [verified in the design docs]:
- docs/331 §6.1: parking changes nothing in EA's state; parked objects stay in the world, so `GetObjects<GameObject>` and the
  object step still see them.
- docs/324 §5.2 and §7: a T3 Sim keeps its object and stays in `LotManager.Actors` (the doc lists our hourly sweeps as T3
  residual). Only T4 / streamed page-out removes Sim objects. T4 demotion goes through `Sim.Destroy`, which fires
  `kSimDestroyed` and so asks for one more object walk.
- SimDescriptions persist in every tier, so p1-p4 stay linear in the world population.

**(b0) models on, loops unchanged:** same as (a). The objects stay; T4 adds `kSimDestroyed` events.

**(b1) the loops skip parked lots and model-owned Sims, or streaming / T4 removes them:** near share of objects q = 0.3 at 1x
[inferred: household lot + L1 / L2 lots]. No existing run measured q (§9.4). It stays a bracket of 0.1-0.4 until M-1 runs. For
SV the near object part is q x 12.9 ms/s, i.e. 1.3-5.2 ms/s across the bracket. The near set stays constant in absolute size as the world grows. Viewer Actors =
household + T1 / T2 Sims (docs/324 §1.3 bands: 35 / 22 / 87% of NPCs inside 400 m).

| | 2x | 4x | 10x |
|---|---:|---:|---:|
| BP sweep / push / total ms/s | 5.3 / 1.8 / **7.2** | 7.7 / 1.8 / **9.6** | 15.0 / 1.8 / **16.8** |
| IP | 6.1 / 1.6 / **7.6** | 8.6 / 1.6 / **10.1** | 16.1 / 1.6 / **17.6** |
| SV | 6.6 / 3.8 / **10.4** | 9.3 / 3.8 / **13.1** | 17.5 / 3.8 / **21.3** |
| ms per pass, SV | 29 | 41 | 76 |

Bounded by the near set in (b1): the object step, the gathers and filters (only if they come from per-lot lists, LC-6), and the
viewer Actors walk. Still linear in the world: p4 household descriptions (7 us x every description, each Sim-group run), p1-p3
(relationship keys, aging states, alarm index; event-gated), the pre-save pass, and the native snapshot if it stays a world
query. At 10x, about 75% of the remaining sweep cost is the description phases, which LC-7 addresses.

**(c) change-keyed bookkeeping (no full walks; LC-5, LC-7):** each pass touches the holders of the Sims destroyed and the
descriptions disposed since the last pass, plus the parked lots that are due at promotion or save. That is ~1-3 ms/s at any
size, plus a 24 h full-walk floor spread over ~a day of passes, plus the push at ~1 ms/s (LC-4) [inferred, I-587-4].

## 4. Off-thread value per phase

What the reads are and whether a worker may make them:

| Part | Reads | Off the sim thread? |
|---|---|---|
| world snapshot (`Queries.GetObjects<T>`) | `Query_GetObjects` `0x749630` -> host `0xD798F0`, walks the host per-class object maps `+0x1658` / `+0x1678` and allocates a managed array | **no**: ic-safety.csv verdict PROXY (sync), "unlocked read of sim-mutated state", allocates (needs GC thread registration, docs/30) [verified table; docs/121] |
| destroyed test (`HasBeenDestroyed` = `Simulator.GetProxy`) | `Simulator_GetTaskImpl` `0x76CA60` -> host vtable `+0x18` `0xD74240`, proxy hash map | **no**: PROXY (sync), unlocked [verified table; docs/122 IC table] |
| use lists, reference lists, owner component, ObjectDisposed invocation lists, cooking handlers (p10) | managed fields (`List<T>`, delegates) that the sim thread grows and shrinks | only from a managed worker inside a consistent window: the `[AutShadow]` Satb barrier window (docs/206 option 2b) or a sim-paused span. A free-running read races `List<T>` resizes [inferred] |
| relationship keys, aging states, alarm index (p1-p3), `Household.EverySimDescription` (p4) | managed `Dictionary` / `List` mutated by the sim thread | same as above; a concurrent `Dictionary` read during an insert can loop or throw [inferred] |
| re-check and apply (EA `Remove*`, `RelRemoveKey`, owner relink) | writes game state through EA methods | **sim thread only** (writes, events) |
| viewer push: household positions | `Sim.Position` -> native transform | the native side can already read a position from an object id: ts3native `unseen.cpp` resolves objects by id (`0x939560`) and reads the transform position (`0x959000`) [verified our source]. That runs on the sim thread at the consumers, so it needs no worker |
| viewer push: ride test | managed `Posture`, `CurrentInteraction` | managed worker only; not worth it, see LC-4 |

Remaining sim-thread cost with a worker doing every scan (sweep, SV) [inferred, I-587-2]: snapshot ~3-3.5 ms x k per pass,
description phases ~4.5 ms x k (if not moved) and apply ~0. That is **~2.2 ms/s at 1x, ~9 at 4x, ~22 at 10x**, with an atomic
snapshot of ~35 ms at 10x. Add the cost of the worker itself: a Satb window, GC registration, and a copy of the object list into
the worker, all new risk on the sim-thread hot path. Viewer push: worker saving < 3 ms/s at 1x; LC-4 removes the same walk on
the sim thread.

**Verdict: off-threading these loops is not worth building.**
- Today the full-walk loops cost 12-16 ms/s. The sim-thread residue of an off-thread version (snapshot, apply, the
  description phases) is still linear in the world.
- Once the loops follow the near set (b1) or the change (c), the total is 7-21 ms/s at 2x-10x with no worker, and it shrinks
  toward ~2-4 ms/s once the description phases are keyed by event. At that point a worker has nothing left worth moving.
- For unbounded worlds the better path is region / lot-keyed and incremental bookkeeping. Off-threading would only move a
  cost that the bookkeeping removes. This fits the offload goal (docs/23 #17): move what is left after the work stops scaling
  with the world, and nothing of this is left.

## 5. Open facts (answered in §9; what is still open)

1. The 3.65 us split is partly answered statically (§9.1). Per-part numbers need M-1.
2. Worst slice: the timer placement is verified (§9.2). Whether the 10-13 ms max is the snapshot gather, a GC step or a big
   relationship item needs M-1.
3. Holder writers, cross-lot holders and ride entries are answered (§7).
4. Near-object share q has no existing data (§9.4). It is a bracket until M-1 runs.
5. Event completeness for LC-5 / LC-7: the send sites are verified, and an exception can skip them (§9.5). How often holders
   meet a gone Sim with no event is measured by M-1.
6. Scale proof still waits for WS-0 (docs/332).

## 6. Packages (docs/332 format; agent minutes + game runs; revised 2026-11-23 second pass)

| ID | Goal | Inputs | Outputs | Done check | Dep | Kind | Est | HH |
|---|---|---|---|---|---|---|---|---|
| M-1 | Counters-only measurement build (§8): per-part us of the object step, holder occupancy, gather and longest slice per phase, snapshot native vs copy, object and lot share per distance band, gone-without-event counts, ride candidates | `-1122d` loop-cost build, §8 | `ts3fixes.package.loopcost-measure-1123` (sha bf881a6b687f, tuning = `-1122d`; branch `feature/loopcost-measure-1123` daf7ed277), queue `loopcost-measure-1123` (**built, queued after the IP BAAB, not run**) | selftest; smoke reads every counter; BP / IP / SV rows fill §9 tables | - | build + game-run | 45 + ~55 min game (smoke + 3 x 10 min, not timing) | R |
| LC-1 | Exact cuts, one build: (a) one partition pass of the snapshot (`Sim`, `Fish`, vehicles, horseshoe, darts; sliced every 8 items) instead of 5 `DrOfType` filters; (b) one `ObjectDisposed` read and one invocation list per object for the handler and cooking bodies (re-read only when the handler body changed the field); (c) the `deadrefcooking` switch and `PatchCounters` read once per slice, not per object | DeadRefs.cs, §9.1 | ts3fixes build on the loop-cost lane | selftest (partition = 5 filters; shared list = two reads on synthetic delegates); docs/330 correctness rows PASS; next timing: Sim-group gathers ~14 -> ~4-6 ms per run, p10 below 3.65 us per object | docs/330 merged; M-1 advisable first (it sizes (b) and (c)) | build + game-run | 40 + correctness + folds into next timing | R (same items) |
| LC-4 | Viewer push without Actors walks: a ride set kept by hooks on `Mount.Run`, `Dismount.Run`, `BeingDismounted.Run` and `RidingPosture.StartRiding` (§7.4), seeded by one Actors walk at load, with a 10 s Actors walk as floor for mod paths; ts3native reads household positions by id at its consumers (`0x939560` + `0x959000`, as `unseen.cpp`), managed side sends ids on membership change | §7.4, `UnseenViewers.cs`, ts3native unseen*.cpp | ts3fixes + ts3native builds, new sub-switch (default off until measured) | selftest; shadow compare: hook ride set = walk-every-wake set on 3 worlds; `utViewerBad` 0; push ms/s 3.4-4.2 -> <= ~1.5 | - | build + game-run | 60 + 3 correctness + 3 timing | H (household positions read at every consumer; unknown = household) |
| LC-5 | Reverse index Sim -> objects for the owner component (`SetStealActors`, `SetRewardGiver`, import), Relationship `ObjectDisposed` handlers (`GameObjectRelationship` ctor) and cooking handlers (`AddObjectListeners` / `ClearObjectListeners`), seeded by one walk at load; on `kSimDestroyed` / `kSimDescriptionDisposed` only the indexed objects are stepped. Use lists keep a slim walk (three list reads, gone test against the set of Sims destroyed since the last pass, no reflection, no component lookup); the 24 h full walk (`kDeadRefObjHours`) stays as the floor | §7.1-7.2, §9.5, M-1 | **built 2026-11-23 (docs/336, NOT RUN):** `ts3fixes.package.lc5-reverse-index-1123`, sub-switch `deadrefdelta` (default off) + test-only `deadrefdeltaverify`, queues `lc5-delta-correct-1123` / `lc5-delta-time-1123`; build 2 (`lc5-reverse-index-1123b`): hybrid index (Sim-keyed owner, description-keyed handlers, unkeyed cooking) + sub-switch `deadrefslimuse` (use pass over objects with a use list only; ~17 unhooked EA writers wait for the 24 h walk), leak checks (docs/336 §4) | the eight docs/330 deadref repros PASS in delta mode; shadow compare: delta finds = full-walk finds over 3 x 10 min; M-1 `gone_without_event` ~0; p10 -> slim walk only | M-1 | build + game-run | 60 + 8 correctness + 3 timing | R (same removals) |
| LC-7 | Description phases with reverse lookups: coworkers (`AddCoworker` / `RemoveCoworker` / `ClearCoworkers` + ~6 direct sites), `FormerBoss` (`SetBoss`), chess challenger (1 writer), pregnancy dad (4 sites) indexed by the referenced description, seeded at load; p4 visits only descriptions that reference a disposed one; p1-p3 stay event-gated; full scan on the 4 h floor (public persisted fields, mods bypass the hooks) | §7.3 | **built 2026-11-23 (docs/336 §10, ledger §601, NOT RUN):** `ts3fixes.package.lc7-desc-reverse-1123`, sub-switch `deadrefdescrev` (default off), slots 1744-1757, queues `lc7-desc-correct-1124` / `lc7-desc-time-1124`. M-1: p4 = 166-201 ms per 10 min (0.28-0.34 ms/s at 1x, ~7-10 us x every description, on ~85% of passes); p1-p3 unchanged | deadref repros PASS; p1-p4 ms/s ~0 between events | M-1 (gone-without-event for descriptions) | build + game-run | 45 + 2 correctness | R |
| LC-6 | **LC-6a built 2026-11-23 (docs/336 §9, not run): per-lot lists for the typed gathers (`deadrefperlot`, default OFF) and the 17 direct use-list writers pinned (slim use exact); the rest below waits for FO-4.** Far-model hooks: skip parked lots and model-owned Sims in the slim use-list walk and take per-lot lists for live lots; owner / handler / cooking need nothing more (LC-5 is lot-agnostic). Needs one docs/331 §6.1 rule: **a lot parks only when no use, reference or routing list on it holds any Sim** (today: no EA-owned Sim). Then a parked lot's lists cannot pick up a Sim that is destroyed later (a T4 demotion) | docs/331 FO-4, docs/324 FS T3, LC-5 | ts3fixes build + docs/331 edit | repro: T4 demotion of a Sim that was in a list on a since-parked lot; `parkedHousehold` 0 | FO-4, FS T3, LC-5 | build + game-run | 45 + 2 correctness | H (household lots never park; unknown scope = live) |

**ON criterion for every LC package (Rodney, 2026-11-23).** Each package is built with its switch off. It is flipped ON in the
same lane once all three hold:
1. Correctness passes: its repros and the docs/330 deadref rows, with the household exemption intact.
2. Timing shows no regression: our ms/s and hitch p99 / p99.9 / max are no worse than off beyond noise.
3. The static scaling case of this doc shows a gain at 4x and 10x.

A small or zero gain at 1x is accepted. The scaling cases:

| Package | 4x gain | 10x gain | Basis |
|---|---|---|---|
| LC-1 | ~5-7 ms/s | ~12-17 | §3, §9.1 |
| LC-4 | ~10-12 | ~30 (~120 with rides in progress) | §3.1 |
| LC-5 | ~25-30 | ~65-80 | p10 to a slim walk |
| LC-7 | ~5-6 | ~13-15 | p1-p4 linear part |
| LC-6 | the rest down to the near set | | §3.2 (b1) |

M-1 is counters only and has no switch.

**Order:**
1. M-1: built and queued; it runs after the IP BAAB.
2. LC-1.
3. LC-4 and LC-5 in parallel. LC-4 has no dependency and could go first if game time is short.
4. LC-7.
5. LC-6 once the far models have their owner flags.

The former LC-2 (measurement) is now M-1, and the former LC-3 (census) is done in §7. Game time: M-1 ~55 min, then ~3 h of
correctness and timing spread over the loop-cost queues. Nothing needs a large world before WS-0.

**Projection with the revised design** [inferred, I-587-9], SV:

| | 1x | 4x | 10x |
|---|---:|---:|---:|
| (a) today | 15.6 | ~71 | ~175 |
| LC-1 + LC-4 + LC-5 + LC-7, models off | ~6-7 | ~20-25 | ~45-55 |
| the same with LC-6 and parking / T4 | ~5-6 | ~6-8 | ~7-10 |

Notes on the rows:
- Models off: what remains is the slim use-list walk (~1-1.5 us per object, still linear) and the snapshot. The push is ~1 ms/s.
- With LC-6: the slim walk covers only near lots, events and the description index.
- The worst slice stays the snapshot gather until LC-6 replaces it with per-lot lists (LC-6a, docs/336 §9: built, typed gathers ~3-7 ms at 10x [inferred]; the snapshot remains on full-walk passes). At 10x without LC-6 it is still ~35 ms
  (native call plus copy).

## 7. Static census (former LC-3, done 2026-11-23)

Paths are relative to `game-copy/decompiled` (local only; nothing quoted); GO = `Sims3GameplaySystems/Sims3.Gameplay.Abstracts/GameObject.cs`.

### 7.1 Writers of the p10 holder fields [verified]

| Holder (DeadRefs.cs body) | Choke points | Bypasses | Index by hooks? |
|---|---|---|---|
| use list `mActorsUsingMe` (`UseListBody`) | `AddToUseList` GO:6516, `RemoveFromUseList` GO:6522, `ClearUseList` GO:6532; ~325 / ~381 / ~11 call lines | **27 direct `ActorsUsingMe.Add/Remove/Clear`** in EA scripts (grep count; e.g. picnic posture, serving containers, band instruments, lemonade stand), plus reset GO:4012, init GO:6137, validate GO:6273; persisted, restored at load without a writer | **no** |
| reference list `mReferenceList` | `AddToReferenceList` GO:6476, `RemoveFromReferenceList` GO:6481 (main caller `InteractionInstance`) | `ClearReferenceList` GO:3953, reset GO:4028, validate GO:6261 | yes, but redundant with the slim walk |
| routing reference list | GO:2826 / GO:2835 (callers: Sim routing component, surface utils, skatable terrain, `InteractionInstance`) | reset GO:4038, post-load prune GO:6149 | yes |
| owner component owner / thief (`OwnerBody`) | `ObjectOwnerComponent` ctor and `ObjectStolen`, reached only through `SetStealActors` GO:3684 (7 callers) and `SetRewardGiver` GO:3710 (1 caller); `ImportContent` at load | none | **yes** (we already hook `SetRewardGiver`, `EaFixes.cs:113`) |
| Relationship `ObjectDisposed` handlers (`HandlerBody`) | subscribed only in the `GameObjectRelationship` ctor (`Sims3.Gameplay.Socializing/GameObjectRelationship.cs:77`); EA never unsubscribes (the cause of the stale handlers) | none | **yes**, one ctor |
| cooking handlers (`CookingBody`) | `CookingProcess.AddObjectListeners` / `ClearObjectListeners` | 6 sites clear `ObjectClickedOn` without unsubscribing (microwave, pot, frying pan, baking dish, food tray, microwave have): they make the stale entries | yes |
| ours | `RemoveFromUseList` in 8 of our files, `AddToUseList` in 2, `AddToReferenceList` in 1, owner fields in DeadRefs.cs, `ObjectDisposed -=` in `CookClickListener.cs`; we already hook `AddToUseList` (`Batch582bLogic.cs:533`) | - | our own writes call EA's methods, so the index hooks see them |

### 7.2 Cross-lot holders [verified at the write sites]

An object on lot X often holds a Sim that never stood on X:
- **reward giver:** any relationship Sim, with the object in the actor's mailbox;
- **theft:** the owner is any relationship Sim, with the object in the thief's inventory;
- **reference list:** a queued interaction references its target wherever the actor stands;
- **routing reference list:** set when the route starts;
- **car and bird relationships:** the handler is on a vehicle or pet that moves between lots;
- **inventory objects** keep their owner component while they travel [inferred].

**So a per-lot dirty set (the destroyed Sim's last lot) would miss refs; it is dropped.** The reverse index is keyed by Sim and
does not care about lots.

### 7.3 Phase 4 fields [verified]

| Field | Writers |
|---|---|
| coworkers | `Occupation.AddCoworker` / `RemoveCoworker` / `ClearCoworkers`, ~6 direct sites (school, academic career, career clone, firefighter clone), public field |
| `FormerBoss` | `Career.SetBoss`, `Occupation.ImportContent`, public field |
| chess challenger | 1 writer (`LogicSkill`) |
| pregnancy dad | 4 sites in `Pregnancy`; `PetPregnancy.mDad` (2 sites) not covered by p4 today |

A reverse index is feasible. Because the fields are public and persisted, the 4 h full scan stays as the floor.

### 7.4 Rides [verified]

- `Sim.mPosture` is written outside the setter (`Sim.cs:46385`) only by the Sim ctor (Standing) and `Dispose` (null).
- `BeingRiddenPosture` is created only in `RidingPosture.StartRiding` (`RidingPosture.cs:454`), which is called from
  `AnimateAndStartRiding`. Its callers are `Mount`, `Debug_Mount` and a rabbit-hole exit.
- One `Run` per interaction with no EA subclasses: `Mount.Run` `Sim.cs:40470`, `Dismount.Run` `Sim.cs:40636`,
  `BeingDismounted.Run` `Sim.cs:40777`. Other places create instances without changing that entry: borrow-horse situation,
  terrain, riding postures.
- A ride restored from a save may not pass the setter [inferred], so LC-4 seeds its set with one Actors walk at load.

## 8. M-1: counters-only measurement build (BUILT 2026-11-23, QUEUED, NOT RUN)

Base: the loop-cost lane build `-1122d` (branch `feature/loopcost-measure-1123` off `feature/ts3fixes-loop-cost-1122`,
commit daf7ed277, `mods/ts3fixes/src/LoopMeasure.cs` plus hook lines in DeadRefs.cs and UnseenViewers.cs). It only adds
counters: no switch, default or behaviour change, and the object step runs the same holders in the same order. Slots 1590-1657
(registry `fixes-1590`), agent names `lcm_*`. Build `ts3fixes.package.loopcost-measure-1123` (sha bf881a6b687f, tuning digest
1467b8d4c707 = `-1122d`), copied to `game-copy/ts3fixes.package.loopmeasure1`, agent `game-copy/ts3agent.package.loopmeasure1`.
Selftest: `tools/selftest/loopcost` adds band edges and 1-in-64 sampling. selftest-all in that worktree is 309 PASS / 1 FAIL;
the FAIL is `mts-helper/cli/chrome.ts`, a port race already fixed on master (240de422d) and not in the lane base.

Caveats:
- Viewer points are the household Sims only (no camera target), so bands err toward farther.
- On band passes (1 in 6) the position and lot reads land inside p10 ms.
- `loopcost-report.ts` has no `lcm` columns. Read the `lcm_*` counters from the job's `ab-fixes` / activity output.

| Group | Counters | Cost when on |
|---|---|---|
| object step parts | on 1 in 64 objects: us in the destroyed test, use-list scan, owner body, handler body, cooking body, and the rest of the step (dispatch); samples taken | 6 clock reads per sampled object, ~0.1 ms/s |
| holder occupancy (last walk) | objects with any non-empty list, with an owner component, with `ObjectDisposed` subscribers; delegates in those lists; Relationship and cooking targets among them | counts only |
| gathers and slices | gather us per phase (time before the first item); longest slice per phase; snapshot split into the `GetObjects` call and the copy into the list | 2 clock reads per phase |
| object and lot bands | every 6th snapshot, inside the sliced p10 loop: object position against the nearest household Sim and the camera target into < 50, 50-150, 150-200, 200-400, 400-1000, >= 1000 m and no position; lots with objects per band | ~11k position reads per 6 passes, sliced; ~0.4 ms/s |
| event completeness | `kSimDestroyed` and `kSimDescriptionDisposed` seen; gone Sims found in holders that were not destroyed since the last pass (`gone_without_event`); the same for gone descriptions in p1-p4 | a set lookup per find (finds are rare) |
| rides | ride candidates per Actors walk, household rides | counts only |

Queue `tools/harness/queues/loopcost-measure-1123.json` (in the measure branch): BP smoke 3 min (stops the queue on fail), then
BP / IP / SV 10 min natural play, seed 12345, release defaults, Bin state apex. Chain `probe-out/queue/chain-loopcost-measure-1123.sh`,
started by `probe-out/loopmeasure-wait-1123.sh`. That waiter runs FIFO after `loopcost-baab-wait-1123.sh` (the IP BAAB timing)
and needs no chain, queue or job listed. It was armed 11:36 PHT 2026-11-23. Not timing: no quiet machine needed, but the tool
cost is light enough that the per-part numbers are good to ~10%. Game time ~55 min.

## 9. Second pass answers (2026-11-23, static + existing data)

### 9.1 What the 3.65 us per object is made of

Per object, `DrObjectStep` (DeadRefs.cs:352) makes these calls in the timing run (cadence ON, `deadrefcooking` and
`deadrefconvo` read back ON in its counters) [verified source]:

| Part | Managed calls | icalls | Allocations |
|---|---|---|---|
| slice loop + `Action<object>` delegate + clock test (`RunSliceEvery`, EaSpreadCore.cs:184) | ~3 | clock 1 per 8 objects | - |
| `PatchCounters()` (Fixes.cs:1592), 4 try blocks | 1-3 | - | - |
| `HasBeenDestroyed` = `ObjectId` + `Simulator.GetProxy` | 2-3 | **1** (`Simulator_GetTaskImpl` `0x76CA60`) | - |
| `UseListGone`: 3 list getters, 2-3 enumerators | ~9-12 | 1 per listed Sim | - |
| `OwnerBody`: `ObjectOwnerComponent` -> `GetComponent<T>` (`Type.GetTypeFromHandle` + lookup) | ~3 | ~1 | - |
| `HandlerBody`: `FieldInfo.GetValue` on `ObjectDisposed` | ~2 | **1** (`MonoField.GetValueInternal`) | `GetInvocationList` array if subscribed |
| `ShortcutOn("deadrefcooking")` (string-keyed switch cache) | ~3 | - | - |
| `CookingBody`: second `FieldInfo.GetValue`, second `GetInvocationList` | ~2 | **1** | a second array if subscribed |

That is about 25-30 managed calls and ~3-4 icalls per object. Owned objects add 2 reflection reads, and each listed Sim adds one
destroyed test. The m1122 managed samples (old separate phases, 57 sweep samples over the three worlds) agree [measured,
small n]:

| Frames in the samples | Samples |
|---|---:|
| world queries (`Query_GetObjects`) | 13 |
| per-item clock | 7 |
| dispatch / self frames of the step | ~17 |
| list copies (gone with cadence) | 6 |
| `ObjectId` / `GetProxy` / `GetTaskImpl` | 4 |
| `GetInvocationList` | 4 |
| `MonoField.GetValue` | 2 |
| `GetComponent` | 1 |
| description phases, rest | ~3 |

Reading of the samples: at 3.65 us the interpreter dispatch of ~25-30 managed calls is the largest part (~50-60%). The
destroyed icall is ~15-25%. Reflection and invocation lists are ~15-25% [inferred, I-587-6]. The second reflection read, the
second invocation list, the per-object switch lookup and `PatchCounters` are exact cuts (LC-1 b, c), worth ~15-25% of p10
(~1-2 ms/s) [inferred]. M-1 gives the real split.

### 9.2 Worst slice

- Placement [verified, DeadRefs.cs:295-314]: the phase watch restarts, then the phase's gather runs, then the first slice; the
  slice time recorded at `DeadRefSliceDone` includes the gather. So each phase's first slice = gather + at least 8 items. The
  snapshot (`Objects<GameObject>` = `Queries.GetObjects` + a managed copy into a `List<object>`, DeadRefs.cs:383) and the type
  filters are never sliced.
- `GetObjects` cost against object count [measured]: an empty typed query (`HorseshoeCourt`, off arm) costs 3.09 / 2.75 / 2.83
  ms per run for 10.9k / 10.8k / 11.5k objects, i.e. **0.25-0.28 us per world object**. The native side walks the host
  per-class maps whatever the result [verified docs/121]. The managed `is` filter over the snapshot costs 0.23-0.25 us per
  object (p8 on). The p5 gather (snapshot + copy + `Sim` filter) has a mean of 6.0-6.5 ms per run.
- So the first p5 slice is >= ~6 ms by construction [verified placement + measured mean]. The 10-13 ms max fits a slow
  snapshot: a fresh 11k array, plus a list that grows to 16k entries by repeated copies, can trigger a GC step inside the slice
  [inferred, I-587-3]. M-1's gather and longest-slice counters settle it.
- At 10x this is a ~60 ms atomic slice (a ~35 ms snapshot + filters). LC-1 removes the filters; LC-6 per-lot lists remove the
  world snapshot.

### 9.3 Holder writers

See §7: owner, handler, cooking and phase-4 fields have choke points; use lists do not; cross-lot holders are common
[verified]. This changes LC-5: the reverse index covers owner / handler / cooking, the use lists keep a slim walk, and the per-lot
dirty set is dropped.

### 9.4 Near-object share

- No existing run counted objects by distance [verified, checked sources]:
  - the docs/329 NonSimSplit dumps give time per band and lot counts only (BP 83 lots);
  - docs/322 bands count Sims;
  - the ErrorTrap inventory pass gives a world total only (SV 11,317 objects).
- Time shares are not object shares: far lot rows hold most lot alarm time on BP, and the household lot row is large.
- q stays a bracket of 0.1-0.4. M-1's band group measures it.

### 9.5 Other findings that change the design

- **Events [verified]:**
  - `kSimDestroyed` is sent only in `Sim.Destroy` (`Sim.cs:54808`).
  - `kSimDescriptionDisposed` is sent only at the end of `SimDescription.Dispose` (`SimDescription.cs:4040`).
  - An exception in the steps before either send skips it [inferred].
  - `EventTracker.SendEvent` drops events while its instance is null (`EventTracker.cs:61-63`) [verified].
  - `Sim.Dispose` without `Destroy` (world unload, travel) sends no `kSimDestroyed` [inferred]. The sweep stands down there
    anyway, and the first pass after a load runs every group.
  - A reset does not destroy the Sim and clears the three lists itself (`DoReset`, GO:3991).
  - Consequence: LC-5 and LC-7 keep the 4 h idle floor and the 24 h full walk (`kDeadRefObjHours` = 24, DeadRefs.cs:90). M-1
    counts how often holders meet a gone Sim without an event.
- **docs/330 note:** `deadrefcooking` and `deadrefconvo` were ON in the timing run (read back true in the counters) although
  the DeadRefs.cs header calls them default off. The measured costs include phases 14 / 15.
- **docs/331 rule** (for LC-6): a lot parks only when no list on it holds any Sim. Today the rule says "no EA-owned Sim", which
  lets a T3 Sim in a list become T4 (destroyed) while the lot is parked.
- **Order:** LC-4 has no open fact left and can run before LC-5. LC-5 waits for M-1's event-completeness numbers.
