# 110. Wildflower post-load stall: cause and fix design

**Status 2026-10-18: W1 and the measure mode are built (offline only, see §11); no game run yet.**

Written 2026-10-18, static only (no game, probe or queue run for this doc). Inputs: the two first-minute `/taskstall` captures from `tools/harness/queues/first-minute.json` (`bp-firstmin-taskstall`, `probe-out/20261018-182629-taskstall.jsonl`; `ip-firstmin-taskstall`, `probe-out/20261018-183347-taskstall.jsonl`), the decompiled script DLLs (`game-copy/decompiled/`, not copied here), a Ghidra decompile of the native placement search (`IC_World_FindGoodLocation 0x78AE70` -> `0xB10A30` -> `0xB0E130` -> core `0xB470C0`, scratch only), docs/95 (alarm upkeep follow-ups), docs/96 §4.2 (cause C), docs/64, docs/74 (how ts3fixes hot-spot fixes are built) and `mods/ts3fixes/src`.

Tags: **[measured]** = from the captures; **[verified]** = read in the decompiled script DLLs, the disassembly or our own source; **[inferred]** = reasoning, still to confirm; **[community]** = what a community mod's page says it does, not checked by us.

## 1. Answer

- **Cause [verified + measured].** The stalls are the Seasons spring wildflower drain, not a load catch-up. At each spring transition `Wildflower.OnSeasonChanged` builds a list of community lots (shuffled, cut to the CPU-level cap of 5/10/20/30, plus the active home lot first) and starts a repeating **one-sim-hour** global alarm. Each alarm fire takes **one** lot off the list, creates one wildflower out of world and calls `GlobalFunctions.PlaceObjectOnLot` with up to **20 tries**, no yield, an unbounded search radius and a managed terrain check (grass/dirt/default paint only). Both the list and the alarm handle are persisted statics and the alarm is `AlwaysPersisted`, so a save made inside the drain window resumes it after every load. The harness saves are `Baseline-BP-Spring` / `Baseline-IP-Spring`, so every seeded run replays the same remaining fires in its first minute: that is the BP "ThreadedObjects triple" of docs/96 §4.2.
- **Live [measured].** BP: 3 stalls of 589, 585 and 953 ms (10.0, 22.4 and 46.6 s after the first load stall); IP: 4 stalls of 296, 304, 320 and 299 ms within 11 s (IP machine-load line "not comparable", other CPU 12%, so treat these as upper bounds). All 11 managed samples sit inside one native call, `World_FindGoodLocation` under `PlaceAtGoodLocation` -> `PlaceObjectOnLot` -> `OnGrowWildflowersOnLot` -> `AlarmTimerCallback.Invoke` -> `AlarmManager.Simulate` -> `Lot.Simulate`. None is in the managed terrain callback.
- **Why one fire costs 0.3-1 s [inferred].** The native core is an outward tile spiral that returns the first cell passing every test and gives up only after a failed-cell count of 8 × a size field of the search object (§3.3); it does not stop at a small radius. A fire that never finds a valid cell does that whole walk up to 20 times. Most likely this is the expensive case (all tries fail, the flower is destroyed). Fires that place quickly stay under the 250 ms capture threshold, which fits the gaps (some hourly fires leave no stall). Which case holds (20 short failing searches or one long search) is not known yet. The fix has to settle it first (§7, measure mode).
- **For players.** The same thing happens at most once per in-game spring (with Seasons): up to ~32 hourly hitches of 0.3-1 s over about 1.3 sim-days, fewer once 20 uncollected wildflowers are in the world (the drain then waits), and none while the Seasons temperature is under 15 °C. It shows up in every first minute only because our saves sit in that window. In the profile data it is a harness artefact that is also real gameplay.
- **Same pattern elsewhere?** No. The insect, rock/gem/metal and seed spawners also create wildflowers and minor pets (50% chances), but they place them through `ObjectSpawner.TryPlaceSpawnedObjectInWorld`: one search with a radius of half the spawner's spawn diameter and no managed check [verified]. Their cost is the per-spawn world counts docs/95 lists (~0.7% average), not long stalls. `MinorPet.BehaviorEscaped` does run an unbounded search, but from the pet's own position, with no terrain callback, and only for escaped pets [verified]. Nothing else in the script DLLs pairs `PlaceObjectOnLot` with a managed check and an unbounded radius on an alarm.
- **Recommended fix: W1, "wildflower placement as a yielding task"** (§7). Hook `OnGrowWildflowersOnLot` and keep its checks and list handling in the alarm. Run the placement in a `OneShotFunction` task with the game's own `RandomObjectPlacementParams.Yield` set, so each try runs in its own tick. Same lots, same flower count and types, same try limit, same success and failure rules. The flower appears up to ~20 ticks later and draws from the shared RNG in a different order. A measure mode runs the original body with counters first. **Gain cap:** if the 20-failing-tries reading holds, each 0.3-1 s fire becomes ~20 slices of ~15-50 ms: ~2.1 s per BP load and ≤ ~1.2 s per IP load in the harness, and in play ≤ ~32 hitches per spring gone [inferred]. If one long search turns out to dominate, W1 gains nothing and W3 (lot-bounded radius, a gameplay change) is the only cheap lever. **Effort:** ~1.5 Claude-with-Rodney days (1 build, 0.5 test).
- **Community reducer [community].** The ModTheSims "No (or fewer) Minor Pets and Wildflowers from RGM Spawners" (based on chuckles_82's original) lowers the chances that spawners produce minor pets and wildflowers. That is the spawner path, which is cheap and bounded, not the spring lot drain, so it would **not** remove these stalls [inferred from what it tunes]. It also changes gameplay. As a player-side alternative for this stall it is the wrong lever. The lever that works is the `kMaxLotsToGetFlowers` tuning (fewer spring lots). That also changes gameplay, so we would document it, not ship it (§8).

## 2. Data

| Capture | Stall (wall clock, UTC) | Stage | ms | Owner [measured] |
|---|---|---|---|---|
| BP `20261018-182629` | 10:28:30.2 / 10:28:30.8 | TaskObjects | 486 / 564 | NRaas `OnDelayedWorldLoadFinished` (Overwatch `TestDoesObjectNeedCleaning`, ErrorTrap), docs/96 §4.2 |
| | 10:28:40.2 | ThreadedObjects | 589 | wildflower (2 samples) |
| | 10:28:52.6 | ThreadedObjects | 585 | wildflower (2) |
| | 10:29:16.8 | ThreadedObjects | 953 | wildflower (3) |
| | 10:30:39 / 10:33:31 | TaskObjects | 393 / 454 | NRaas Overwatch daily `CleanupInventory` (docs/96 cause B) |
| IP `20261018-183347` | 10:35:48.0 / 10:35:48.6 | TaskObjects | 476 / 897 | NRaas load scans (as BP) |
| | 10:35:57.0, 59.2, 10:36:04.5, 06.8 | ThreadedObjects | 296, 304, 320, 299 | wildflower (1 sample each) |
| | rest (24 stalls to 10:38:04) | ThreadedObjects | 252-709 | route search (`RouteBackoffHook.PlanAsync` / `Replan` -> `Route_Plan` / `Route_ReplanFromPosition`), one product list, one footprint removal: docs/96 cause A/D, not this doc |

- The wildflower stalls all run on one task GUID per run (the world lot's `ScriptProxy`: the global `AlarmManager` is simulated from `Lot.Simulate`) [measured].
- Gaps between wildflower stalls: BP 12.4 s and 24.1 s, IP 2.2, 5.4 and 2.3 s [measured]. With one fire per sim hour, the BP gaps mean ~4.8 sim-min/s and one fire under the threshold in the 24 s gap. The IP gaps would need ~27 sim-min/s, which is more than docs/96's ~10 sim-min/s run average, so either IP runs much faster in its first seconds or more than one drain alarm is live (§3.4). Open; the measure mode logs the sim time of each fire [inferred].
- docs/96 §4.2 timed the BP triple at 6-8, 16-19 and 36-40 s after the first profiler row, in every seeded BP run. That is the same 10/12/24 s spacing as here, with a different zero point. So the BP ThreadedObjects part of class C (47 iterations, 30.6 s over all BP runs) is this drain [inferred, strong].

## 3. The code path [verified unless marked]

### 3.1 Spring list and alarm

- `Wildflower.PostWorldInit` subscribes to `kSeasonTransition` (not while travelling). On **Winter** it clears the drain and ends the lifespans of all wildflowers. On **Spring** it collects every community lot id, shuffles the list (`RandomUtil`) and cuts it to the CPU-level cap from tunable `kMaxLotsToGetFlowers` = {5, 10, 20, 30} (Low/Medium/High/Uber). The cut removes one fewer lot than needed, so cap + 1 lots remain. Then it inserts the active household's home lot at the front and starts `AlarmManager.Global.AddAlarmRepeating(1 h, then every 1 h, "grow wildflowers on lot", AlwaysPersisted)`. So one spring drain has at most cap + 2 fires (≤ 32 on Uber). The i7-11800H's `DeviceConfig` level is not checked [inferred: High or Uber].
- The new alarm handle overwrites the static without removing an old alarm. A second Spring event before a Winter would leave the first alarm running [verified in the handler; whether that ever happens, e.g. a save loaded across a transition, is unverified].
- Both statics (`sLotsToGetWildflowers`, `sGrowWildflowerInSpringAlarm`) carry `[PersistableStatic(false)]`: saved with the game, the flag only means "not only from the main menu". So a save inside the drain resumes it after every load.

### 3.2 One fire (`OnGrowWildflowersOnLot`)

1. `CanSpawnWildflowers()`: at least one wildflower product installed. With Seasons, temperature ≥ `kMinimumTemperatureForWildflowerSpawn` (15). Then a world-wide `Queries.GetObjects<Wildflower>()` counted up to `kMaxWildflowersInWorld` (20) uncollected. If false, the fire returns **without** taking a lot, so the drain waits and is cheap.
2. Take the first lot id off the list. If the lot exists: create one random wildflower out of world (weighted type from the spreadsheet, RNG). Build `RandomObjectPlacementParams(inside false, outside true)` with defaults `MaxTries` 20, `Yield` false, `StayInRoom` added to the default `PreferEmptyTiles | Routable`, `MaximumSearchRadius` 10,000,000. Set `PlacementCheckFunc` = `IsValidWildflowerLocation` (terrain paint must be Default, Dirt01/02 or Grass01/02).
3. `GlobalFunctions.PlaceObjectOnLot`: random forward (RNG). Then per try: `Lot.GetRandomPosition(outside, lot terrain or floor)` (RNG), set it as the start, `PlaceAtGoodLocation` -> `World.FindGoodLocation`. Stop at the first success. A `Simulator.Sleep(0)` between tries runs only if `Yield` is set. EA sets it in exactly one other caller (`PlayCatchObject`).
4. Success: `AddToWorld` + `FadeIn`. Failure after 20 tries: `Destroy`. If the list is now empty, remove the alarm.

Everything runs inside the alarm callback on the sim thread, in `AlarmManager.Simulate`. An alarm callback must not sleep: EA's own long alarm work queues a `OneShotFunction` instead (docs/95 §5.3, role starts).

### 3.3 The native search (`0xB470C0`) [verified static unless marked]

- `IC_World_FindGoodLocation` -> `0xB10A30` -> `0xB0E130` unpacks the boolean constraints into flags and calls the core `0xB470C0` **once**. The core walks cells outward from the start in eight directions (direction tables `0x106C618` / `0x106C638`, scaled by the offset scale), tests each cell (`0x869360` or `0x807B30`, then placement, room and routability tests), calls the script's `PlacementCheckFunc` through host slot `+0x134` for cells that pass the native tests (docs/62: host delegate call), and **returns at the first cell that passes everything**.
- The loop ends when the count of failed cells reaches **8 × the larger of two size fields (`+0x174` / `+0x178`)** of the object the search runs on [verified]. What that object is (world or lot) and where `MaximumSearchRadius` is read inside the core were not found in this pass [unverified]. So a search that finds nothing walks that whole budget, and a search that finds something costs only the cells up to the first hit.
- Consequence for fixes: a bounded first pass with an unbounded fallback cannot save time and keep the result. The unbounded search already stops at the same first hit, and when the bounded pass fails the fallback pays again [inferred from the first-hit return].
- The 11 samples never show `IsValidWildflowerLocation` or `GetTerrainSurfaceType` on top, so the time is in the native cell tests, not the managed callback [measured, small n].

### 3.4 Why it lands in the first minute

Not an alarm catch-up: the alarm keeps its persisted sim-time schedule, and nothing in `AlarmManager` replays missed fires for this alarm type [inferred: the gaps match hourly fires, not a burst at time 0]. The drain simply continues from the save. In BP the saved list held a few lots (≥ 3 slow fires and at least one fast one), and nothing after 47 s points at more. In IP, 4 slow fires in 11 s and none in the remaining 2 minutes. More than one live alarm (§3.1) would explain the tight IP spacing [inferred, open].

## 4. Other spawners and minor pets

| Path | Placement | Bounded? | Managed check | Stall risk |
|---|---|---|---|---|
| Spring drain `Wildflower.OnGrowWildflowersOnLot` | `PlaceObjectOnLot`, 20 tries, no yield | no (1e7) | yes (terrain paint) | **high** (this doc) |
| `WildflowerSpawner`, insect / RGM / seed spawners (50% wildflower or foreign minor pet first) | `ObjectSpawner.TryPlaceSpawnedObjectInWorld`, 1 search | yes, 0.5 × spawn diameter | no | low; per-spawn world counts `CanSpawnWildflowers` / `CanSpawnMoreMinorPetsInWorld` (docs/95, ~0.7% avg) |
| Spawner alarms (`TriggerSpawn`, `DeleteOldestUntakenObject`) | per spawner, `NeverPersisted` | - | - | not resumed from saves; no first-minute burst |
| `MinorPet.BehaviorEscaped` | 1 search from the pet (or a random lot spot on a room change) | no | no | rare: escaped pets only |
| `SeedSpawnerRarity` hit spawn | 1 search from the hit point | no | no | player action only |

So the first-minute stall belongs to the spring drain alone [verified for the listed callers]. The docs/95 "alarm upkeep" items stay as filed there: a per-tick memo for the two world counts.

## 5. Fix options

| # | Idea | Exactness | Risk | Effort (days) | Gain cap [inferred] |
|---|---|---|---|---|---|
| **W1** | **Placement as a `OneShotFunction` task with `Yield` set: one try per tick** | Same lots, counts, types, try limit and success rules. The flower appears ≤ ~20 ticks later. RNG draws interleave with other consumers, so seeded runs diverge, as with any timing change (docs/74 accepted "one tick late") | Save while a try is pending (flower out of world, §7.3); two fires overlapping (guard) | 1 + 0.5 test | per fire from 0.3-1 s to ~20 slices of 15-50 ms, **if** the tries fail; nothing if one search dominates |
| W2 | Work queue, N lots per tick | - | - | - | **none**: the game already does one lot per sim hour; the stall is inside one lot's placement |
| W3 | Bound `MaximumSearchRadius` to the lot (e.g. half its diagonal + margin) | Changes results: a flower that today lands far off the lot fails instead and the next try draws a new spot; it may also end up destroyed | Low code risk; needs `MaximumSearchRadius` semantics in the core (§3.3, unverified) | 0.5 + core read 0.5 | caps one search; helps only if the bound actually shortens the walk |
| W4 | Skip lots out of view / not loaded | Not result-equivalent (fewer or different flowers) | - | - | rejected |
| W5 | Cheaper first try: test the start cell's terrain before searching | Not exact: the spiral can still find a valid cell near an invalid start | - | 0.5 | rejected as a default; possible opt-in |
| W6 | Defer the resumed drain after a load (first fire ≥ N sim-minutes after load) | Shifts flower timing by N; does not remove a single stall, only moves it out of the first minute | low | 0.5 | first-minute hitches move later; 0 total gain |
| W7 | Run the search off the sim thread | `World_FindGoodLocation` is UNSAFE (docs/62: lot/object layer reads, host delegate call) | high | many | not now; would ride on the docs/18 snapshot work |
| W8 | Pre-draw the flower and its 20 start points in the alarm (RNG order as the original whenever all tries run), then search one per tick | Same RNG stream as the original when all 20 tries fail (the expected slow case); differs when an early try succeeds (the original draws fewer) | same as W1 | +0.5 over W1 | same as W1; closer to seeded A/B parity |

## 6. Recommendation

Build **W1** (with W8's pre-draw as an option only if seeded parity matters for the A/B). It is the only cheap change that keeps the drain's outcome rules. It reuses EA's own `Yield` flag and the alarm-to-`OneShotFunction` pattern, and it costs nothing when the fires are cheap. Ship it behind a measure mode first: if the counters show that slow fires are **one** long search (tries ≈ 1), stop and bring W3 to Rodney as a gameplay change instead.

## 7. Build spec (for a later builder; nothing built here)

### 7.1 Hook and switches

- File: `mods/ts3fixes/src/ScriptHotSpots.cs` (same pattern as `HoursMemoHook`: `Patch(typeof(Wildflower), "OnGrowWildflowersOnLot", Type.EmptyTypes, typeof(WildflowerTaskHook), "GrowHook", "GrowOrig")`; the method is private static with no arguments).
- Tunings in `Instantiator.cs`, both default **false**: `kWildflowerTask` (switch `wildflowertask`) and `kWildflowerProbe` (switch `wildflowerprobe`, measure mode). Add both to `Fixes.cs` switch lookup and to the `PatchHandlers.cs` name list and default-off list.
- Off (both): `GrowHook` calls `GrowOrig`. Game unchanged.
- `wildflowerprobe` on (measure mode, behaviour identical): `GrowHook` runs a copy of the original body, synchronously, with `Yield` false. It times each try (`Stopwatch` around each `PlaceAtGoodLocation`), counts tries, records the result and logs one line per fire: sim time, lot id, tries, per-try ms (max and sum), placed or destroyed, and the placed spot's distance from the try's start point and whether it is on the lot. That settles §1 "which case", the IP spacing (§2) and W3's premise in one load.
- `wildflowertask` on: `GrowHook` does steps 1-2 of §3.2 in the alarm (the same `CanSpawnWildflowers` check, list pop and list-empty alarm removal, in the original order). Then it queues one `OneShotFunction` that runs `PlaceObjectOnLot` with `Yield = true` and does the same success or failure handling. Guard: if the previous fire's task is still running, run this fire synchronously (the original path), so fires never overlap and the order of lots stays the same.

### 7.2 Counters (reserved 93-98 in `docs/registry.md`; 90-92 went to SafeSpot reuse)

| Slot | Meaning | Agent key |
|---|---|---|
| 93 | fires that took a lot | `wildflower_fires` |
| 94 | tries run | `wildflower_tries` |
| 95 | fires that placed a flower | `wildflower_placed` |
| 96 | fires that failed all tries (flower destroyed) | `wildflower_failed` |
| 97 | longest single fire (measure mode) or longest single try slice (task mode), ms | `wildflower_maxms` |
| 98 | task placements cut short by a save (flower destroyed, §7.3) | `wildflower_saveaborts` |

Raise the counter array to 99 in both `Fixes.cs` and `PatchHandlers.cs`, and read the new slots behind `c.Length > 98` (registry rule).

### 7.3 Risks and guards

- **Save during a pending placement** [inferred]: the flower exists out of world between tries. A save then would store an uncollected, unplaced wildflower that counts toward the 20 cap until its persisted lifespan alarm (10 days + 2-5 jitter) removes it. Guard: before each yield, if a save is in progress (the `SaveGameInProgress` check `SaveCleanup.cs` already uses), destroy the flower and end the task as a failed fire (counters 96 and 98). This matches a failed placement, which the original also allows.
- **World teardown / travel while pending:** the task ends when the world unloads. Check the flower and lot for null or invalid after each yield, destroy if needed.
- **Yield safety:** the task is our own `OneShotFunction`, not an alarm or event callback, so the docs/73 rules allow `Simulator.Sleep(0)` there. The search itself is unchanged.

### 7.4 Offline test (no game)

- `node tools/managed-callgraph.ts` closure of `Wildflower.OnGrowWildflowersOnLot` and `GlobalFunctions.PlaceObjectOnLot`: confirm the only yield site is the `Yield` branch and that no event or static write sits between tries.
- Mod self-check (house style, an assert-based check in the build): measure mode and the original produce the same counter deltas on a stub placement (tries stop at the first success, destroy after 20).

### 7.5 Repro and in-game checks (memory `repro-actions`: never wait for spring)

- Agent trigger action `wildflower_drain {lots: n}` (in `mods/ts3agent`) plus `tools/harness/repros.json` entry `wildflower-drain`. It fills the drain list through reflection with n community lots (or the game's own Spring handler logic) and invokes `OnGrowWildflowersOnLot` n times, one per sim hour, via the real alarm (fast-forward) or directly from a one-shot. Run it in one BP load with `--ab`: first half `wildflowertask` off and `wildflowerprobe` on, second half `wildflowertask` on. Pass: counters 95/93 (placed / fires) have the same ratio in both halves within noise, no `/taskstall` row ≥ 250 ms from `OnGrowWildflowersOnLot` in the task half, no script errors, flower count in the world as expected.
- **Verify run first:** one BP and one IP job like `bp-firstmin-taskstall` / `ip-firstmin-taskstall` (`profile: {minutes: 5, seed: 12345, taskstall: 250}`, S3PP `SimulatorStageProfiler` off) with `wildflowerprobe` on only, to read tries, per-try ms and the IP spacing. The first minute happens once per load, so a mid-run `--ab` cannot test it. The fix check is a separate BP job with `wildflowertask` on, compared with today's `20261018-182629` baseline. Pass: no wildflower stall ≥ 250 ms, counters show the same fires. Machine-load line clean (memory `external-load-check`), night run (memory `clean-night-testing`).
- Save matrix (memory `save-change-test-matrix`): needed since default on, queue `wildflower-save.json` (docs/247 §12.7). The save path is unchanged, apart from the abort guard, which the repro covers with one save during a pending placement.

## 8. Community spawn reducer vs ours

| | "No (or fewer) Minor Pets and Wildflowers from RGM Spawners" (ModTheSims, after chuckles_82) [community] | W1 (ours) |
|---|---|---|
| What it changes | lowers or zeroes the chances that spawners (RGM and similar) spawn minor pets or wildflowers instead of their normal objects | when each try of the spring drain runs (one per tick) |
| Hits this stall? | **no** [inferred]: the spring drain does not read those chances, only `kMaxLotsToGetFlowers` and the world cap | yes, if the tries fail (measure mode decides) |
| Gameplay | changed (fewer minor pets and wildflowers from spawners) | same rules and counts; flowers appear a few ticks later |

As a player-side alternative it is reasonable only for players who dislike spawner pets and flowers. It is not a fix for this hitch. The player-side lever that does hit it is an XML tuning override of `kMaxLotsToGetFlowers` (fewer community lots get spring flowers, so fewer slow fires). That is a gameplay change, so document it in `docs/PLAYERS.md` as an option, not a default, and only after W1's measure mode shows the fires are the cost. Not checked: whether the community package also overrides `kMaxLotsToGetFlowers` (its page names only spawners).

## 9. Limits

- 11 managed samples over 7 stalls; no native sample inside the core, so "20 failing tries" vs "one long search" is open. That is why §7 starts with measure mode.
- `MaximumSearchRadius` handling inside `0xB470C0` and the meaning of the `+0x174` / `+0x178` size fields were not located; W3 depends on them.
- IP stall lengths are upper bounds (load line not comparable). The IP fire spacing is unexplained (§2, §3.4).
- The players' CPU level (5/10/20/30 lots) was not read live.

## 10. Ledger

Rows I-110-1 to I-110-6 in docs/51 §46.

## 11. Built (2026-10-18, offline only)

- **Hook** `WildflowerTaskHook` in `mods/ts3fixes/src/ScriptHotSpots.cs`, patched over `Wildflower.OnGrowWildflowersOnLot`; the drain list and `ClearSpringSpawnAlarm` are reached by reflection (private statics). Tunings `kWildflowerProbe` / `kWildflowerTask` (both off) in `Instantiator.cs` and the build's tuning XML; switches in `Fixes.cs` and the agent's `patches` list (default off). Both off: the game's method runs unchanged.
- **Placement loop.** Probe and task modes run our own copy of the game's loop (random facing, then per try a random lot spot and one `PlaceAtGoodLocation`, stop at the first hit), not `PlaceObjectOnLot` with `Yield` set: that way each try is timed and the save guard sits at every yield. Same calls and RNG draws per try as the game's loop. In task mode the yield comes before tries 2..20 (the game's `Yield` branch sleeps after every failed try, including the last); the alarm-side steps (can-spawn check, list pop, flower, params, empty-list alarm removal) run in the alarm as in §7.1, so the alarm can be removed while the last lot's task still runs.
- **Overlap guard:** the running task's id is kept; a fire while that task is still alive runs the in-alarm loop (timed). A task that never ran (world unloaded) is no longer alive, so the guard cannot stick.
- **Save guard (§7.3):** checked before and after every yield (`OptionsModel.SaveGameInProgress`, as `SaveCleanup.cs`); also ends the fire when the flower or lot is destroyed or the world is going away. Known limit (code comment): a save that starts and finishes inside one yield still stores the out-of-world flower.
- **Counters 93-98** as §7.2 (`WildflowerFire` in `TickMemo.cs`, game-independent); [97] = whole fire in the alarm, longest single try in the task. One log line per fire in AppDomain data `ts3fixes.wildflower` (last 200), returned by the agent's `patches` as `wildflower_log`: sim minute, lot id, alarm or task, tries, max and sum ms per try, placed (distance from the try's start point, on or off the lot) or destroyed, abort reason.
- **Offline checks [verified]:** `tools/selftest/hotspots` (`WildflowerModel`: on 5,000 stub fires the hook's loop runs the same tries, gives the same result and the same counter deltas as the game's loop; [97] rules); managed call graph closure of `OnGrowWildflowersOnLot` and `PlaceObjectOnLot` (depth 3): the only yield site is `Simulator.Sleep` in the `Yield` branch; the only static writes are the drain statics in `ClearSpringSpawnAlarm` and `Lot.sRandomLevels`, a lazily built constant list inside `GetRandomPosition` (no write between tries); no event raise.
- **Repro and jobs (§7.5):** agent `wildflower_drain {lots, every_min, clear, stop}` (in `ReproHandlers.cs`) refills the drain list with community lots (own seed, the game's RNG untouched) and fires the game's handler from a `NeverPersisted` alarm; `clear` removes uncollected wildflowers lying in the world before each fire so the 20-flower cap does not stop the drain. Repros `wildflower-probe` (probe on for the whole load), `wildflower-drain` (A/B) and `wildflower-save-abort` in `tools/harness/repros.json`; queue `tools/harness/queues/wildflower.json` (probe first-minute BP and IP with `--taskstall 250`, then `bp-ab-wildflowertask`).
- **Open:** every live check of §7.5; whether `wildflower-probe`'s switch is set before the save's first fire (it is set right after the window start; docs/96 put the first BP fire 6-8 s after the first profiler row, so it should be [inferred]; `wildflower_fires` below the stall count in the probe job means a fire came earlier).

## 12. Measure-mode result 2026-10-18 (`bp-firstmin-wildflowerprobe` rerun, 20:03 PHT, PASS)

`wildflowerprobe` on for the whole window, BP Spring save, seed 12345, 5 min (counts only; machine load not checked for timing). Full per-fire log from `probe-out/profile/<tag>-repro.jsonl` (the first run lost it to the 200-character log cut, fixed in `profile-world.ts`).

| Counter | Value |
|---|---|
| fires | 12 |
| tries | 70 |
| placed | 9 |
| failed (all 20 tries, flower destroyed) | 3 |
| longest fire | 1,025 ms |

Per fire: the 9 placed fires took 1-2 tries and 0-36 ms. **The 3 slow fires are exactly the 20-failing-tries case**: 20 tries each, 551, 565 and 1,025 ms in total, longest single try 32, 35 and 65 ms [verified live]. So the docs/110 §5 reading holds and **W1 applies**: as a yielding task each slow fire becomes 20 slices of at most ~65 ms instead of one 0.55-1 s stall. W3 (bounded radius) is not needed. Next: the clean-night `bp-ab-wildflowertask` A/B on the `wildflower-drain` repro (docs/110 §7.5).
