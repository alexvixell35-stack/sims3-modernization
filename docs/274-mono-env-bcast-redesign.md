# 274. Env score and broadcaster offload: why apply never fires, and what can save them

Status 2026-11-16: static analysis + existing run data only (no game run, no build). Scope: docs/129 ranks 1-2 on the `[AutShadow]` worker, i.e. the environment score (`autoffenv` + `autoffenvapply`, `autoffunseen`; parked in docs/208 §13) and the broadcaster candidate set (`autoffbcast` / `autoffbcastapply`; 11-06 correctness PASS, never timed). Code read at master `3d737d658`; `mods/ts3fixes/src/AutOffload.cs` is unchanged since build `ecbc9c76`, the build of the 11-16 runs [verified: `git log ecbc9c76..master -- AutOffload.cs` is empty].

Tags: **[verified]** = code, decompiled call graph (`tools/managed-callgraph.ts`, `game-copy/analysis/callgraph`) or a counter read from `probe-out/queue/*.jsonl` / `probe-out/profile/*-fixes.jsonl`; **[measured]** = run counters as written; **[inferred]** = reasoning, the stop rule or a dry probe confirms it.

## Summary

| Question | Answer | Tag |
|---|---|---|
| Why does env apply never fire under `autoffunseen`? | EA only runs `CalculateSimEnvironmentScore` for high-LOD Sims, and high LOD is close to "seen or household". The unseen-only pick can only pick Sims that just dropped to low LOD, whose env updates have stopped. Unseen share of env calls: BP 0.14-0.24%, IP 0.00% | verified (EA rule, our pick code) + measured (share) |
| Is it a bug, a freshness window or the pickup point? | None of these. The work is not there: 101 answers kept, 0 picked up, 0 refused by scope in `bp-offu-apply` | measured |
| What unseen env work exists? | BP ~0.03 ms/s of 18.3, IP 0, SV not sampled (expected ~0 by the same rule) | measured BP/IP, inferred SV |
| Does bcast have the same scope problem? | No: 65% (BP) / 72% (IP) / 11% (SV) of the pulses that still run are unseen. Its apply fails for three other reasons: (1) the answer lives 15 ticks, the next pulse comes ~37.5 ticks later; (2) 72% of bcast items abort `simstale`; (3) the batch rate caps coverage at ~20% of unseen pulses | verified code + measured counters |
| Hidden defects | Mode R A2 is not 0 (bcast 8%, env 11%) although §3.2 says it must be; Mode 2 runs skip the cut reference after warm-in, so apply runs keep unchecked answers; the 11-06 PASS rows carry no `autoff_*` counter | verified + measured |
| Best way to save the work | Drop the worker for both. **R1** exact per-lot / per-room list memo for env (all Sims, ~2-4 ms/s BP and IP); **R2** sim-thread Sim position table for unseen broadcasters (~3 ms/s BP and IP, ~0.5 SV). Both start as dry probes that change nothing | inferred |

## 1. Evidence used

| Run (build) | World, length | Switches | Counters read here |
|---|---|---|---|
| `bp-offu-dry` (mono-offload-correctness-1116, 2026-11-16 07:24 UTC / 15:24 PHT, `ecbc9c76`) | BP, 10 min | autshadow, autoffenv, autoffbcast, autoffdry, autoffunseen (no apply, so the pick is not scoped) | env items 3923, A0 1162, A2 139, A4 2265, A5 357; dry pickups 473, dry_eq 470; bcast items 17917, A0 4144, A2 366, A4 12854, A5 553, dry 3862, dry_eq 3828, dry_order 3818 [measured] |
| `bp-offu-apply` (same queue, 07:36 UTC / 15:36 PHT) | BP, 10 min | + autoffenvapply, autoffbcastapply (scoped pick) | env items 267, A4 153, A5 13, ready 101, pick_scoped 972, pick_overdue 3434, applied 0, apply_stale 0, scope_* 0, dry 0, apply_none 9298, asked_expired 3, never_asked 5; bcast items 6449, A0 32, A4 4130, A5 75, ready 2244, pick_scoped 4091, applied 4, dry 4, dry_eq 4, apply_none 127399 [measured] |
| `bp-offu-smoke` (mono-offload-correctness-1115-mm) | BP, 4 min | as apply | env ready 164, applied 0, pick_scoped 1121; bcast ready 1354, applied 4, apply_stale 4 [measured] |
| offpickup1114 (docs/208 §12 table) | BP, IP | pre-scope pick | 396 of 399 BP pickups household (153) or seen (243); IP env all items aborted [measured, docs/208 §12] |
| offload3-apply-correctness (2026-11-06, `8911a627`) | BP, IP | Mode R + apply + dry | no `autoff_*` counter in any row (`fixActivity.counters` has 0 of 44 keys): the "applied > 0" gates were never read; `autshadow.jsonl` has 90-100 `off-bcast` A2 records per bcast job [verified] |
| unseen-script-throttle A/B (2026-11-01) and unseenpark-1113 | BP, IP | `unseenprobe` classifier | `unseen_env_calls` / `unseen_env_unseen` per run (§2.2) [measured] |

Abort reasons are the native `kAsAbortName` order (`native/ts3native/autshadow.cpp:392`: row, pos, memo, rng, slotpos, attach, query, ic, ..., simstale = 11, ..., list = 18); the `autoff_reasons` list puts env at 0-23 and bcast at 24-47 [verified].

## 2. Environment score: the cause

### 2.1 What the counters say [measured]

- `bp-offu-apply`: 101 answers kept, then no Sim update met any of them: `applied`, `apply_stale`, the three `scope_*` counters and `autoff_env_dry` (one per pickup with `autoffdry` on) are all 0. Only 8 answers are accounted as lost (asked_expired 3, never_asked 5); the other ~93 were replaced by a newer cut for the same Sim (`KeepEnv` replaces a Sim's unused answer without counting it, §4 D5). So the same few Sims were asked batch after batch and none of them updated.
- Without the scoped pick (`bp-offu-dry`) the pickup works: 473 pickups in 10 min, 470 equal to the live driver (99.4%). The pick and the store are fine; the scope filter leaves no Sim that updates.

### 2.2 Why: EA's LOD rule and the unseen rule are near complements [verified + measured]

- `SimUpdate.Simulate` runs `CalculateSimEnvironmentScore` only for **high-LOD** Sims within the per-tick limit; low-LOD Sims run none (docs/144 §1.1 [verified, decompiled]).
- High LOD (`Autonomy.IsSufficientlyOnScreenForHighLODSimulation`, cached 60 ticks): the active household; Sims on the active lot or the active Sim's lot; hosted guests of a selectable host; daycare children; resort workers; otherwise only outdoors **and** on screen (docs/144 §1.2 [verified]).
- Unseen (`UfWhy` = 0, `UnseenFallback.cs:121`): not household, not on the active household lot, the camera lot or any lot in view (`UfLotSeen`), not on screen, not near viewers (`kUfSimRadiusM`), not held, not socialising with a seen Sim [verified, code].
- So unseen ∩ high-LOD is limited to: Sims inside the 60-tick LOD cache lag after leaving the screen, resort workers and daycare children off screen, guests away from the host's lot. That is exactly the set the scoped pick finds: a Sim that just left view still has an `sEnvDue` period, passes `UfWhy` = 0, gets picked, and never updates again because it is now low LOD; after 240 ticks it moves to `pick_overdue` (3434 in the apply job).
- Measured share of env calls made for unseen Sims (`unseen_env_unseen / unseen_env_calls`, `UnseenThrottle.cs` classifier, `probe-out/profile/*-ab-fixes.jsonl`):

| World | Runs | Env calls per run | Unseen | Share |
|---|---|---|---|---|
| BP | 9 (2026-11-01 A/B x 8, unseenpark-1113) | 6,172-18,804 | 15-29 | **0.14-0.24%** |
| IP | 8 (night-1101 x 7, unseenpark-1113) | 8,858-18,477 | 0 | **0.00%** |
| SV | none | - | - | not sampled; same LOD rule, expected ~0 [inferred] |

The `UnseenThrottle` classifier is close to `UfWhy` but not identical (older camera test, no lot rule) [inferred: both exclude household and on-screen Sims; `UfWhy` also excludes seen lots, so its share can only be lower].

### 2.3 What unseen env work exists per world [measured BP/IP, inferred SV]

Env cost 18.3 ms/s BP, 17.4 IP (docs/129 §2.1). Unseen work = cost x share: **BP ~0.03 ms/s, IP 0**. The movable part (~58%, docs/208 §8.4) is ~0.02 ms/s BP. Any unseen-only env design, worker or not, has nothing to gain. This answers the docs/208 §13 "Reopen when": the cause is EA's LOD rule, not the pick, not the window, not a bug.

### 2.4 Where the env work really is [measured]

Per 10 min BP: ~9,300 env updates (`apply_none` 9298 in the apply job counts every update without an answer). The 11-14 pickups split household 153 / seen non-household 243 (docs/208 §12). So roughly **40% household, 60% seen NPCs, ~0% unseen** [inferred from pickups, which favour due Sims, not from all updates]. Any gain has to come from seen Sims, which needs an exact answer (seen NPCs: go-live "exact when visible"; household: CLAUDE.md "proven-exact speedups stay"), or from household Sims under `[Experimental] HouseholdToo=1` (docs/271: worker answers are a realistic-only site).

### 2.5 Worker coverage even without the scope rule [measured + inferred]

Even with every Sim allowed, the worker path covers few updates: 473 pickups / ~9,300 updates = **5%** (`bp-offu-dry`). Limits:
- Batch rate: ~930-1,060 requests per 10 min (11-06 `autshadow_requests`), i.e. ~1.7 batches/s, x `kEnvMax` 8 = 13.6 Sims/s against ~15.5 updates/s: the cap is ~85% before any loss [inferred from counters].
- Aborts: 2265 of 3923 items (58%): `pos` 1190, `ic` 866, `list` 107, `row` 63, `simstale` 37, `slotpos` 2; A5 357; A2 139 [measured].
- Repeats: a due Sim is re-asked every batch until it updates; ~622 of 1162 kept answers were overwritten unused (§4 D5).

## 3. Broadcaster candidate set: same problem?

### 3.1 Scope: no [measured, docs/144 §2.1]

Unseen share of the pulses that still run after `kBroadcasterThrottle`: **~65% BP, ~72% IP, ~11% SV** (docs/144 §2.1: counters measured, share derived). In `bp-offu-apply` the scoped pick refused 4091 broadcasters and kept 2244 answers: unseen bcast work exists. Bcast fails for other reasons.

### 3.2 Cause 1: the answer dies before the next pulse [verified code + measured]

- A broadcaster pulses once per sim-minute (docs/144 §2.1 [verified, decompiled]) = 37.5 simulator ticks (`SimClock.kSimulatorTicksPerSimMinute`, docs/04a [verified]).
- The request asks the 16 **newest** broadcasters of the pulse ring (`OffRequest`, `AutOffload.cs:213`), i.e. ones that just pulsed: their next pulse is ~37 ticks away.
- Apply accepts an answer up to `kBcApplyTicks` = 15 ticks old (`ReadyBcFor(b, apply ? kBcApplyTicks : kDryTicks)`). So by design almost no answer is alive at the next pulse.
- Counters: with the 240-tick dry window, 3862 of 4144 answers (93%) met a pulse (`bp-offu-dry`); with the 15-tick apply window, 4 of 2244 (0.2%, `bp-offu-apply`); 127,399 pulses found no answer. This is the env §8.1 mistake ("asked right after the update") repeated for bcast.
- The 15-tick limit is an inference (I-404-4, never measured). The dry check measures the real error: at up to 240 ticks, the rebuilt set equals the game's set in **99.1%** (3828 / 3862) and in the same order in 98.9% [measured]. Not exact, so unseen-only, but far from the "must be ~1 tick" the margin argument assumed.

### 3.3 Cause 2: 72% of bcast items abort on `simstale` [measured + verified code]

`autoff_reasons[35]` (bcast + 11 = `simstale`) = 12,777 of 12,854 bcast aborts (`bp-offu-dry`), 4,109 of 4,130 (`bp-offu-apply`). The bcast driver walks `LotManager.Actors` and reads every Sim's position row (§8.3 range answer), but the bcast request only adds the broadcasting object's row (`AddRow(q.Obj..., 0)`); a Sim not refreshed at this cut is published stale and its read aborts (`autshadow.cpp:1526`, docs/122 §2.3) [verified]. `AsLearn` promotes the Sim for later cuts, but with ~60 Sims and a 16-broadcaster batch the set keeps changing [inferred].

### 3.4 Cause 3: the batch rate caps coverage [measured + inferred]

~212 pulses/s run on BP (127,399 / 600 s), ~138/s of them unseen (65%). The worker gets ~1.7 batches/s x 16 = ~27 broadcasters/s: at most **~20%** of unseen pulses even with perfect pickup and zero aborts. At docs/208 §8.4's ~9-10 ms/s per 100% apply, the unseen ceiling is 65% x 20% x ~9.5 = **~1.2 ms/s BP**.

### 3.5 Does the code still match master?

Yes for the 11-16 runs (`ecbc9c76`, status line above). The 11-06 run (`8911a627`) had the same bcast apply path (`kBcApplyTicks` 15, `kBcMax` 16, newest-ring pick [verified: `git show 8911a627:...AutOffload.cs`]) but no scope gate, pickup fix or scoped pick (added since: `d5ff21b60`, `8e87fd566`, docs/208 §10-§12). Its PASS says nothing about apply: the rows carry no `autoff_*` counters [verified].

## 4. Hidden defects found on the way

| # | Defect | Evidence | Effect | Tag |
|---|---|---|---|---|
| D1 | Mode R A2 is not 0. docs/208 §3.2: "worker and reference read the same cut answer ... A1 and A2 must be 0" | `bp-offu-dry`: bcast A2 366 of 4,510 compared (8.1%), env A2 139 of 1,301 (10.7%); `off-bcast` records all `wf=2` with candidate counts differing both ways (worker 1 / ref 0, worker 0 / ref 2); same shape in the 11-06 and 11-15 smoke logs (capped at 100 lines) | Mode R would filter them, but see D2. Cause [inferred]: the worker reads Sim position rows that are not this cut's values (rows kept by events, docs/122 §14.27 `SimEvRows`) while the reference reads live; it would also explain env `pos` aborts | measured; cause inferred |
| D2 | `[AutShadow] Mode=2` skips the cut reference after warm-in (`AutReference`: `if (sReqs[0].Mode == 2) { if (sWarmIn >= sWarmInMax) return; ...}`, `AutShadow.cs:1107`), so `OffFinish` runs with `noref` and keeps every non-aborted answer unchecked | `bp-offu-apply` (no `autoffnoref`): ready = items - A4 - A5 exactly (env 267-153-13 = 101, bcast 6449-4130-75 = 2244); env A0 0, bcast A0 32 | Apply runs apply A2-type answers (~8-11%) that Mode R would have dropped; the dry check is the only guard | verified code + measured |
| D3 | The 11-06 apply-correctness PASS was never a gate on apply | 0 `autoff_*` keys in its rows | "11-06 apply correctness PASS" (docs/129, docs/208) must read "ran without crash; apply unmeasured" | verified |
| D4 | bcast freshness window 15 ticks vs pulse period 37.5 ticks, newest-ring pick | §3.2 | apply share ~0.2% | verified + measured |
| D5 | `KeepEnv` replaces a Sim's unused answer silently (`if (e.S == w.S) d = e;`) | ready 1162 = used 473 + lost 67 + ~622 uncounted | loss accounting incomplete; harmless otherwise | verified code |

## 5. Redesigns, ranked

Gains are sim-thread ms/s [inferred] from docs/129 samples and the shares above; each starts with a **dry probe** (counts only, game result unchanged) per "measure before build". Effort = wall-clock for Claude with agents (agent build ~10-60 min, live job ~12 min); game runs listed separately.

| Rank | Redesign | Sims it touches | Expected ms/s [inferred] | Risk | Effort | Game runs | Stop rule |
|---|---|---|---|---|---|---|---|
| **1 (R1)** | **Env list memo on the sim thread, exact.** Memo the three per-lot / per-room lists the env scan builds, keyed by lot, room and object type, valid while the lot's object membership is unchanged | all (exact: household by the proven-exact rule, after the dry proof) | BP 2-4, IP 2-4 (ceiling 7.7 / 7.3 at 100% hits) | medium: invalidation must catch every add, remove and room change | dry probe ~1 h; apply ~2-3 h | dry: 1 BP + 1 IP job; apply: correctness BP+IP, then ABBA timing BP+IP | dry: repeat-equal < 40% of env list calls on BP or IP -> park; apply: any memo != live list on household -> household exempt; < 1.5 ms/s ABBA -> park |
| **2 (R2)** | **Bcast candidates from a per-tick Sim position table on the sim thread.** Replace `Queries.GetObjects<Sim>(pos, r)` in `GetInRadiusSims` with a scan of one position table built at the first pulse of each tick; game's filters, LOS and callbacks unchanged | unseen broadcasters with no household Sim in range (`BcScopeOf`), household and seen keep the game's call | BP ~3, IP ~3, SV ~0.5 | low-medium: footprint vs centre test (I-404-3) and order (I-402-5) are not exact, hence unseen only | dry probe ~1 h; apply ~1-2 h | dry: 1 BP + 1 IP; apply: correctness, ABBA BP+IP | dry: set-equal < 99% of unseen pulses, or table build > 50% of the query time saved -> park |
| 3 (R3) | Keep the bcast worker, fix D1-D4: pick broadcasters by pulse due time (as env §8.1), window and margin scaled to age (`margin = vmax x age`), add kind-1 rows for Sims near each broadcaster, find D1, `kBcMax` 16 -> 32 | unseen | <= ~1.2 BP (batch-rate cap, §3.4); IP similar | high: Satb, rows, Mode R | 3-5 h + D1 hunt | 2-3 correctness rounds | only if R2 fails; stop if applied share < 50% of unseen-eligible answers after one round (throttles park rule) |
| 4 (R4) | Env worker under `[Experimental] HouseholdToo=1`: household Sims take the worker answer (realistic-only site per docs/271 Y2); seen NPCs keep the game's call unless exact; needs coverage work (`kEnvMax`, `pos` / `ic` aborts, D1, D2) | household (opt-in), unseen | opt-in players only: <= ~1.5 BP (5% coverage today; ~20% after fixes x 40% household x ~10 ms/s movable [inferred]); default players 0 | medium: opt-in only, never default | 3-4 h | 2 correctness + 1 timing | only after R1 lands; skip if R1 already takes the list part |
| 5 (R5) | Env worker unseen-only (the parked design) | unseen | <= 0.03 BP, 0 IP | - | - | - | **closed**: §2.3 |

### 5.1 R1 in detail: env list memo

**What the game does per update [verified, call graph].** `CalculateSimEnvironmentScore` calls `Lot.GetObjectsInRoom<GameObject>(room)` (indoor), `Lot.GetObjects<IAromainator>()` plus `GetObjectsInRoom<IAromainator>(room)` (indoor), `Lot.GetObjects<EnvironmentObject>()` per residential lot in range (outdoor), and `WorldLot.GetObjects<T>()` (= `Queries.GetGlobalObjects<T>()` filtered by `LotCurrent`, a list + `ToArray`). `Lot.GetObjectsInRoom<T>` tries EA's per-type `ObjectCache` (`Dictionary<Type, IObjectCache>`), else calls `Lot.GetObjects<T>()` (the native `Query_GetObjectsOnLot` walk, a fresh managed array) and keeps objects whose `RoomId` matches or whose portal connects to the room. Our driver mirrors this (`AutOffload.cs:661-672`). Samples (docs/129 §2.1): `Lot.GetObjectsInRoom` 22, `WorldLot.GetObjects` 11, `Lot.GetObjects` 7 of 95 = **42% of env, ~7.7 ms/s BP, ~7.3 IP**; the scoring (`CalculateObjectEnvironmentScore` 10, contributing 5) and the events stay live.

**Design.**
- Hook point: the existing `CalculateSimEnvironmentScore` hook (`EnvHook`) and `EnvApply`, which already replays the game's method with the scan replaced; R1 replaces only the list calls inside a live scan (no worker, no cut, no Satb), so the per-object scores, events and reactions run live in the game's order.
- Key: (lot id, room id, type) for room lists; (lot id, type) for lot lists; world lot keyed the same. Value: the array the game built, in its order.
- Validity: the native layer already tracks per-lot object events for the autshadow cut (lot list generation, `AsLearn ... LOT`, docs/122 §14.20 / §14.27) [inferred: needs a small IC `LotGen(lotId)` returning a counter bumped on add, remove, move, parent change on the lot]. Sims are `GameObject`s and move between rooms: the memo stores the array but checks, on a hit, each Sim in it still has the same `RoomId` and that no other Sim on the lot entered the room (the lot's Sim list, a few `RoomId` reads); any change = miss = the game's call.
- Exactness: same array, same order, same objects = the game's own answer; the dry probe proves it per world before any apply.
- Household: exact, so the household rule allows it ("proven-exact speedups stay"); if the dry probe shows any mismatch, household Sims go through `Fixes.HouseholdExempt` like other exact-but-unproven sites.

**Dry probe (build first).** Switch `envlistmemo` (default off), mode dry: at each env update the game runs as is; the hook records each list result per key and counts (a) `repeat_equal`: the result equals the last one for the same key (reference-equal elementwise), (b) `repeat_diff`, (c) `first`, (d) what the validity rule would have said (`would_hit`, `would_hit_wrong`). Gate for apply: `would_hit / calls` >= 40% and `would_hit_wrong` = 0 on BP and IP. Cost: one array compare per list call (a few us) [inferred].

**Prior art.** `kSpatialCache` (docs/20 §4a, PLAN) cached `GetObjects(Type, lot)` per tick with a copy on every hit and a clear on every tick and `DestroyObject`: 24% hits, A/B +5% / -5% in noisy October runs. R1 differs: generation-keyed across ticks (an env update per Sim every ~75 ticks), no copy (the env scan only reads), only at the env call site.

### 5.2 R2 in detail: bcast position table

**Design.**
- At the first `GetInRadiusSims` of a tick (unseen broadcaster, `BcScopeOf` with no candidates = apply, as the scoped pick does today), build one table of in-world Sims (id, position, Sim) from `LotManager.Actors` [inferred cost: ~60 `Position` reads, ~30 us/tick, ~1.4 ms/s at ~46 it/s; cheaper if `[UnseenTable]`'s native per-tick rows export positions].
- Per pulse: squared distance against radius + buffer + footprint margin (`kBcFoot`), then the game's own filters, LOS / could-see and callbacks on the sim thread exactly as the dry rebuild (`BcRebuild`) does today. The answer is this tick's positions, so no freshness window, no worker, no `simstale`.
- Not exact: the native query tests footprint bounds against the circle and returns quadtree order (I-404-3, I-402-5), so household Sims in range or a seen circle = the game's call (`BcScopeOf`, unchanged). HouseholdToo does not lift it unless the dry probe shows set and order equal on 100% of household-range pulses.
- Saves the query: 40 of 78 samples = ~8.4 ms/s BP, x 65% unseen = ~5.5, less the table and the scan [inferred] = **~3 ms/s BP**, ~3 IP (12.8 x 51% x 72%, less the table), ~0.5 SV (11% unseen).

**Dry probe.** Switch `bcposdry` (default off): at each unseen pulse, build the table answer and compare it with the game's set: `set_eq`, `order_eq`, `missing`, `extra`, plus the table build and scan time (us counters). Gate: `set_eq` >= 99.5% and timing share as in the stop rule.

### 5.3 Why not the other ideas

- **Different pickup point for env** (ask Sims right before their update): solves nothing under `autoffunseen`; the Sims are not there (§2.2).
- **Precompute per lot on the worker**: the list part is per lot, but a worker answer needs the same cut, rows and freshness machinery that already fail; on the sim thread the same memo is exact and simpler (R1).
- **Sim-thread memo of the whole env score**: per-object scores read traits and object state (`GetEnvironmentScore(Sim)`, dirty / broken state) that object events do not cover; only the list part has a clean invalidation [inferred].
- **HouseholdToo for bcast**: order and footprint differences are not proven realistic for reactions of controlled Sims; stays exempt (docs/271 rule "if not sure, exempt").

## 6. Recommended next build and queue

1. **Build (one agent, ~1-2 h):** branch `feature/env-bcast-dry-probes-1117` with both dry probes (`envlistmemo` dry, `bcposdry`), default off, counters reserved via `tools/registry.ts reserve fixes`; `selftest-all`; a game-free selftest for the key / validity and the distance scan. No change to `autoffenv` / `autoffbcast`.
2. **Queue (after the running chain; ~25 min game time):** `env-bcast-dry-probe.json`: smoke 4 min BP, then `bp-probe` and `ip-probe` 10 min each, both probes on, Apex + bridge, `stopQueueOnFail` on the smoke; gates: probe counters > 0 and read back, 0 exceptions, no new crash. No timing.
3. **Then:** R1 apply if `would_hit` >= 40% and `would_hit_wrong` = 0; R2 apply if `set_eq` >= 99.5%. Each apply gets one correctness job per world and an ABBA timing pair; ships ON at a clean 3-5 ms/s (clear-gain rule), else parked.
4. **Docs / cleanup that need no game time:** mark D3 in docs/129 and docs/208 (11-06 apply unmeasured); fix D5 accounting and add an A2 gate (`autoff_*_a2 == 0`) to any future Mode R queue; leave `autoffenv` / `autoffbcast` apply default off. Do not run `mono-offload-timing-1115.json`.

## 7. Open claims (enter in docs/51 when a build lands)

- I-274-1 [inferred]: unseen ∩ high-LOD is limited to the LOD cache lag, resort workers, daycare children and away guests (§2.2); `UfWhy`'s share is at most the `UnseenThrottle` share.
- I-274-2 [inferred]: Mode R A2 comes from Sim position rows kept by events instead of refreshed at the cut (D1).
- I-274-3 [inferred]: the env list results repeat often enough between membership changes for >= 40% hits (R1 gate).
- I-274-4 [inferred]: a per-tick managed position table costs < 50% of the native range queries it replaces (R2 gate).
- I-274-5 [inferred]: native per-lot object events cover add, remove, move and parent change for every object type the env scan lists (R1 validity). Superseded by §8.1: no native per-lot counter exists; R1 uses managed events (I-495-1).

## 8. Dry probes built (2026-11-16, branch feature/envlist-bcpos-probes-1116, NOT RUN)

Both probes live in `mods/ts3fixes/src/EnvBcProbes.cs` (game-free rule in `AutOffloadCore.cs`: `ListMemo`, `Scan`, `SetDiff`; offline cases in `tools/selftest/autoffload`). Test switches, default off, no tuning. They only count: the game's own lists and broadcaster sets run and are used on every call, so no Sim (household, selected, seen or unseen) gets a different result. Counters 1091-1114 (docs/registry.md), agent keys `envlistmemo_*`, `bcposdry_*`. Queue: `tools/harness/queues/env-bcast-dry-probe-1116.json` (4-min BP smoke, then BP and IP 10 min each, both probes on, Bin state `apex-rewind`).

### 8.1 R1 invalidation source: verdict

The native per-lot object-change counter that §5.1 assumed does not exist [verified, `autshadow.cpp`]. `g_asListGen` is one global counter (object events 4/5/6 and world event `0x11`, `AsObjEv`, `AsWorldEvent`) and runs only with `[AutShadow]` on. The per-lot go-live watch (`AsGwTouch`, `ObjStale`) stamps a lot only on add and destroy: it misses lot moves (world event `0x11` carries no object, I-124-16) and every room change. So R1 uses **managed signals**, which are what the game's own lists depend on:

| Signal | What it covers | Tag |
|---|---|---|
| `World.OnObjectLotUpdate` (world event `0x11`, `LotManager.UpdateObjectLot`) | every lot change and every room or level change of a script object. `GameObject.mRoomId` (the `RoomId` that `Lot.GetObjectsInRoom` filters on) is written only in `InternalOnSetRoomAndLevel`, which only `LotManager.AddObjectToLot` and `UpdateObjectLot` call | verified, decompiled |
| `World.OnObjectPendingDestruction` | destroy (`RemoveObjectFromLot(destructed)`) | verified, decompiled |
| `World.OnObjectPlacedInLot` | placement | verified (the event exists; RouteFailLog uses it) |
| `LotManager.PlaceObjectOnLot` (hooked) | script object start-up: `GameObject` start-up calls it and it fires no event, so the object joins the lot silently | verified gap, covered by the hook |
| `LotManager.ExitBuildBuyMode` (hooked) | walls and portals change room connections: full clear | conservative |
| lot added, removed, bounds or type changed; world load finished or quit | full clear | verified events |

Handler order: ours goes in front of LotManager's on `World.sOnObjectLotUpdateEventHandler` (set by reflection), so the object still carries its old lot (`InternalLotCurrent`) and room (`StaleRoomId`). A lot change bumps both lots. A room change bumps the old and the new room. A door, portal or room-connection object bumps its whole lot, because it is listed in every room it connects. Conservative extras: every bump is repeated at the first probe call of a later tick, because the native lot vector may change after the managed event within the same tick (destroy order, [inferred]); an event whose object has no managed object bumps the event's lot. What stays unproven is the native list's host filter (handle map, exclusion list `+0xBFC`, docs/122 §14.26) changing with no managed event [inferred rare, I-495-1]. `envlistmemo_hit_wrong` measures exactly that gap.

### 8.2 What the probes count

- `envlistmemo`: per env update (`scans`), before the game's scan, the scan's own list calls. Indoor: `GetObjectsInRoom<GameObject>(room)`, `GetObjects<IAromainator>()` and `GetObjectsInRoom<IAromainator>(room)`, unless the room is empty. Outdoor on a residential lot: `GetObjects<EnvironmentObject>()` for each lot in range. Each result is compared element by element (reference and order) with the memo entry:
  - `first`: no entry yet;
  - `hit`: valid and equal;
  - `hit_wrong`: valid but different, i.e. an invalidation miss. The first 20 are logged as `EnvListMemo: wrong hit lot .. room .. list ..`;
  - `miss_same`: invalidated but equal (what a finer rule could still add);
  - `miss_diff`: invalidated and different;
  - `list_ms` / `hit_ms`: time of all compared calls / of the would-hit calls (the upper bound of the saving).

  The probe's own calls double the list cost while it is on, so it never belongs in a timing job.
- `bcposdry`: counts every `GetInRadiusSims` call (`pulses`). The compare runs only for an unseen broadcaster with no household Sim on its lot or in range (`BcScopeOf`, the same rule as the autoffunseen pick). The table answer is built from one `LotManager.Actors` position table per tick: a ground-plane scan at radius + buffer + `kBcFoot`, the game's filters (`BcKeep`), then `BcRebuild` (line of sight, could-see, the reacting rule). It is compared with the game's set: `compared`, `set_eq`, `order_eq`, `missing`, `extra`. Cost counters: `tables`, `table_ms`, `scan_ms` (scope check + scan + filters + rebuild) and `game_ms` (the game's call on compared pulses). `aborts` counts a RoomCheck override, a mod Sim subclass, more than 64 candidates or more than 512 actors. `BcKeep` is the filter part of `BcCandidates`, now shared (same checks, same order). `autoffbcast` pre-empts it on the same pulse; the two are not run together.

### 8.3 Gates

- R1 apply only if `hit / calls >= 40%` on BP **and** IP and `hit_wrong = 0`; otherwise park. A wrong hit names the missed change first.
- R2 apply only if `set_eq / compared >= 99%` and `table_ms + scan_ms <= 50% of game_ms`; otherwise park. `order_eq` is information only: R2 stays unseen-only either way.

### 8.4 Probe results (2026-11-16, `env-bcast-dry-probe-1116`, chain-1117a, all 3 jobs PASS)

Counters from the job rows (`probe-out/queue/env-bcast-dry-probe-1116-2026-11-16.jsonl` in the lane worktree; deltas over the job) [measured]. Timing invalid (noise, heavy samplers), so the ms columns are probe-side sums, not a gain.

| Job | R1 calls | hit | hit rate | hit_wrong | miss_same / miss_diff | hit_ms (upper bound) | R2 compared | set_eq | table + scan ms vs game ms |
|---|---|---|---|---|---|---|---|---|---|
| bp-probe-smoke (4 min) | 7,978 | 5,630 | 70.6% | **3** | 2,140 / 171 | 786 | 26,777 | 99.77% | 930 + 3,052 vs 2,170 |
| bp-probe (10 min) | 14,480 | 10,247 | 70.8% | **5** | 3,762 / 427 | 2,225 (~3.7 ms/s) | 49,202 | 99.82% | 2,456 + 7,116 vs 5,248 (182%) |
| ip-probe (10 min) | 20,355 | 15,030 | 73.8% | **15** | 4,750 / 544 | 2,352 (~3.9 ms/s) | 55,142 | 99.80% | 2,380 + 5,195 vs 3,959 (191%) |

Gate verdicts (§8.3):

- **R1: not met.** The hit rate passes (>= 40% on both worlds), but `hit_wrong` is 5 (BP) and 15 (IP), not 0: the managed lot/room events miss some list change. Per the gate, the missed change gets named before any apply. The wrong-hit log lines (`EnvListMemo: wrong hit lot .. room .. list .. n ..`) never reached the job rows: `queue.ts fixesApplied` dropped them (no dotted method name). Fixed 2026-11-17 on `feature/mono-laneb-1117` (the filter keeps `EnvListMemo: wrong hit` lines, self-checked). Next: queue `envlistmemo-wronghit-1117` (BP + IP, 10 min each, `envlistmemo` only, master-1117c set) reads them from `fixesApplied`; then fix the invalidation once, rerun the same queue, and apply only at `hit_wrong = 0`. No R1 apply build until then.
- **R2: parked.** `set_eq` passes (99.8%), but the table + scan cost is 182% / 191% of the game's own range queries against the 50% bar [measured; the probe pays the table build on every pulse it compares, so a lean apply could cost less, inferred]. I-274-4 failed.

### 8.5 Wrong-hit lines and the dying-object fix (2026-11-17, `envlistmemo-wronghit-1117`, branch `feature/envlistmemo-dying-1117`, built, NOT RUN)

`envlistmemo-wronghit-1117` (chain-1117i, both jobs PASS, master-1117c) logged 11 BP and 18 IP wrong hits (`hit_wrong` 11 / 18 of 10,417 / 17,818 hits, ~0.1%) [measured]. Every line is list 0 (`GetObjectsInRoom<GameObject>(room)`), in one busy room per world (BP one lot, rooms 2 and 18; IP one lot, room 5), with the list size drifting (BP 24-43, IP 23-31). The lot-wide lists (1 `IAromainator`, 3 `EnvironmentObject`) and the room aromainator list (2) never hit wrong [measured]. The old line had no diff (only the new size), so the object kind is not named by the data.

What can change list 0 [verified, decompiled]: the native per-lot query (`Queries.GetObjects(type, lotId)`) and the managed `mRoomId`, whose only writer is `GameObject.InternalOnSetRoomAndLevel`, called only from `LotManager.UpdateObjectLot` (same lot) and `AddObjectToLot` (lot change), both inside the hooked `OnObjectLotUpdate`. So a room change cannot pass unseen; the miss is a native lot-list change with no managed event. Cause chosen [inferred, ledger I-506-3]: `OnObjectPendingDestruction` fires before the deferred native destroy; the old rule bumped the lot then and once more at the next tick, but a plate, glass or food object in a kitchen or dining room can stay listed for longer and then drop out with no event. That fits "transient objects, one busy room, never the lot-wide env/aromainator lists".

Fix (in the probe, switch `envlistmemo`, default off, counts only): `ElGone` records the object and its lot(s) (cap 4096; overflow clears the memo); every probe call bumps those lots while `Objects.IsValid` is true, then once more via `Bump`'s next-tick repeat, and drops the entry. World quit / lot reset clears the list. Each wrong-hit line now carries the diff: `was <n>`, up to 3 `gone` and 3 `new` objects as `Type(room .. destroyed dying invalid child notinworld)`, or `reorder` (`ListMemo.WrongOld`, selftest case added). Cost: one `IsValid` per dying object per probe call (a few at a time) [inferred]; the hit rate drops on lots with a pending destroy.

Queue `envlistmemo-dying-correct-1117.json` (BP + IP, 10 min each, `ts3fixes.package.envlistmemo-1117` on the master-1117c native set): FAIL on any wrong hit (`envlistmemo_hit_wrong<1`); read hit/calls (>= 40%) from the row. Pass: R1 apply build next (household / selected / unknown scope exempt via `Fixes.HouseholdExempt`, the apply's own correctness queue + ABBA). Fail: the diff names the kind; if it is not a dying object, the list changes through a native path with no managed event and R1 parks.

**Result (2026-11-18, chain-1118a step 1, `envlistmemo-dying-correct-1117`): bp-dying FAIL, ip-dying not run.** BP 10 min (13.6 min job): calls 10,020, hit 7,243 (**72.3%**), `hit_wrong` **7** (0.1% of would-hits), miss_same 2,458, miss_diff 276, first 36, exc 0 [measured, job row]. The FAIL stopped the queue (`stopQueueOnFail`), so IP has no data; resume: `node tools/harness/queue.ts tools/harness/queues/envlistmemo-dying-correct-1117.json --from ip-dying`. Machine noise was high (other CPU 25%), which does not touch these counts.

All 7 wrong-hit lines (`fixesApplied`) are list 0 on one lot (`..0336`, the same busy lot as 11-17), room 2 (5 lines, n 24, 25, 26, 27, 27) and room 11 (2 lines, n 19), and every one reads `was <n> reorder` with `was` = `n` [measured]:

| Kind | Count |
|---|---|
| destroyed / pending-destroy object (`gone`/`new` with `destroyed`, `dying` or `invalid`) | 0 |
| add or remove with no event (`gone`/`new`, live object) | 0 |
| reorder only (same objects, same count, different order) | **7** |
| other | 0 |

Verdict: the dying-object cause (I-506-3) is **not** what is left: no line names a dying, destroyed or added object. The decision rule's park branch ("the lines don't show a destroyed object" = an unseen membership change) does not apply either, because the membership never changes; what is left is a different, narrower cause: **the order of the room list changes with the same objects in it**. `GetObjectsInRoom<GameObject>` keeps the order of the native lot walk (`Query_GetObjectsOnLot`), so the native lot vector reorders while no managed lot or room event fires for anything in that room [inferred: a remove-and-reinsert or swap-remove of some *other* object on the lot, e.g. one leaving another room or being parented to or from a Sim; not named by the data, because the diff logs only room-2/11 objects]. The 11-17 run (11 BP lines, no diff) may have mixed both kinds; the dying fix cannot be credited with removing any, since that run had no diff.

Not parked. Fix estimate: (1) diag, ~30 min build + one 15-min BP job: on a wrong hit also log the first moved index with both objects' types (Sim or not, `child`) and the lot's events since the memo entry (count of `OnObjectLotUpdate` / parent changes for that lot), so the reordering path gets a name; (2) then either bump the lot on that path (if it is a managed event or a hookable call such as parent/unparent, ~1 h build + BP/IP rerun), or make the apply order-tolerant for unseen Sims only (set compare, `Fixes.HouseholdExempt` Sims always take the game's call; needs a static check that `CalculateSimEnvironmentScore` sums order-independently except float rounding, ~1 h). R1 stays dry until `hit_wrong` = 0 or the order-tolerant variant is proven.

### 8.6 Is a reorder a wrong answer? Consumer check and the reorder diag (2026-11-18, branch `feature/envlistmemo-reorder-diag-1118`, built, NOT RUN)

The memoized list has one consumer: `SimUpdate.CalculateSimEnvironmentScore` (the only caller of the probed list calls; its only call site is the `SimUpdate` tick, hooked by `EnvHook`) [verified, decompiled]. What its indoor loop over `GetObjectsInRoom<GameObject>(room)` does with each object, and whether order matters (ledger §513):

| Use in the loop | Order-independent? |
|---|---|
| Positive scores go to a list, which `CalculateContributingObjectScore` sorts descending before the weighted sum; equal values are interchangeable | yes [verified] |
| Negative scores: `negativeEnvironment += num` in list order (float32 through a `ref` param), then clamp | **no, rounding only**: float addition is not associative; differs by ulps when 3+ negative objects of mixed magnitude reorder [inferred] |
| `roomPerceivedToHaveLight`, window flag (`flag2`), ComeAndSee count | yes (OR / count) [verified] |
| Sims in the list: `DoSimEnvironmentUpdateInternal(..., ref foundEnemy, ref foundLover, ref reactionPlayed, ...)`: salute, pet, horse, Bonehilda, startle, enemy, lover and wave reactions are each gated on `!reactionPlayed` / `!foundEnemy` / `!foundLover`, so the **first** qualifying Sim in list order gets the reaction | **no, first-match** [verified] |
| `SendObjectEnvironmentUpdateEvents` / `SendSimEnvironmentUpdateEvents` (saw dirty / broken object, Sim events) | same set of events, order follows the list; listeners that act once per event kind would see a different first object [inferred] |
| Aromainators added from the lot-wide list (`objects2[k]`, `k < num3`): uses list 1's order (an EA quirk: the first `num3` of the lot list, not the ones outside the room) | list 1, never hit wrong [measured, §8.5] |

Verdict: **order-dependent**. A reorder is a real wrong answer when the moved part changes the Sims' relative order (which Sim gets the reaction) and, at ulp level, when negative scorers move. So the dry gate stays element-by-element (`hit_wrong` counts reorders), and no set-compare change was made.

Diag (switch `envlistmemo`, default off, counts only; no new counters): a reorder line now reads `was <n> reorder at <i> old <Type(...)> now <Type(...)> simorder moved|same ev <e> age <t>`: the first moved index with both objects (`ElDesc` now also marks `sim`), whether the Sims' subsequence order changed, the lot's `Bump` events (each lot/room/destroy/place event once, not the next-tick repeats; on a valid entry these can only be other-room events) and the ticks since the list was last seen equal (`ListMemo.WrongEv` / `WrongAge`, selftest cases added). Build `ts3fixes.package.envlistmemo-reorder-1118` (master tuning; master-1117c native set in the queue).

Queue `envlistmemo-reorder-diag-1118.json` (BP + IP 10 min, Apex + bridge; RUN FIRST before any R1 fix; replaces the unrun `ip-dying` job, since this build keeps the dying bump): no FAIL on wrong hits (`stopQueueOnFail` off), gate = probe counters read back, exc 0, 0 script errors. Read the lines:

- all `simorder same` with a non-Sim first moved object: only ulp rounding and event order differ; fit = **unseen-only reuse** (the memo's list is the game's own order from a few ticks ago, so the answer stays realistic; `Fixes.HouseholdExempt` / selected / unknown-scope Sims always take the game's call), ~1 h build + its own correctness queue.
- `simorder moved`, or `ev` > 0 on every line: the reorder follows an event on another room of the lot (a remove-and-reinsert in the native lot vector, [inferred]); fit = **lot bump** on that event (bump the whole lot instead of the room for the event kind named, ~1 h build + BP/IP rerun), then `hit_wrong` = 0 again gates the exact apply.
- `ev` 0 and `simorder moved`: the reorder has no managed event; R1 goes unseen-only (first rule) or parks.
