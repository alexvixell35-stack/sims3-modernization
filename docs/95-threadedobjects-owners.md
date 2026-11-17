# 95. Which script work owns ThreadedObjects time (route left out)

Today's hitch logs put ThreadedObjects minus route time at 59% (BP) and 34% (IP) of hitch time. Route search is handled on its own track (docs/18, docs/84-90), so this doc ranks the **rest**: which script tasks, interactions and methods own ThreadedObjects time and its long slices, from existing captures only. No game, probe or queue run was made. Builds on docs/64 (route owns the long tail), docs/74 (script hot spots, fixes built and off), docs/04a, docs/28 and docs/59 (autonomy).

Tags: **[measured]** = counted from the captures with `tools/harness/stall-split.ts --owners`; **[verified]** = read in the decompiled script DLLs (`game-copy/decompiled/`, not copied here) or in our own mod source; **[inferred]** = reasoning, to confirm.

## 1. Answer

- **No single script owner dominates the average.** With route-IC leaves and empty stacks left out, ThreadedObjects script time splits into Sim interaction steps 23-30% (most of it the script side of routing: `DoRoute`, portals, walk styles, slot lookups), `Lot.Simulate` 15-20%, `SimUpdate` ~16%, autonomy 9-12%, native event callbacks ~6%, alarms and one-shot functions ~3% [measured, async on, both worlds].
- **Long non-route slices have three owners the average hides** [measured]:
  1. **Our own `SafeSpot` (ts3fixes rabbit-hole shortcut) calling `FindGoodLocation`** during the Isla Paradiso school run: **22%** of script samples inside non-route slices of 100 ms or more in IP (10% at 50 ms), the same in all three IP runs (6, 8, 8 samples at >= 50 ms).
  2. **NPC role starts** (`Role.StartRole` for bouncers, bartenders, paparazzi, run from a one-shot task): **7.9%** of BP samples in slices of 100 ms or more against 0.5% on average; the cost is one Sim instantiation (inventory seeding, commodity map, safe-spot checks).
  3. **Alarm callbacks** as a group: `AlarmManager.Simulate` 9.6% (BP) / 16% (IP) of samples in slices of 100 ms or more against ~4% on average (pet pool pet creation, insect and seed spawners, relationship decay, spawner clean-up, stray pets).
- **The biggest steady owner is already built:** `Lot.UpdateDetailPriority` (docs/74 §6.1, `kDetailPriorityCache`, off) is **4.1% (BP) / 6.8% (IP)** of non-route script time, ~24 / 36 ms/s, larger than docs/74's 15 ms/s estimate [measured; ms/s inferred]. Turning it on after its A/B is the cheapest win in this doc.
- **Async routing moves some cost into script-visible ICs.** With async on, `Slots_GetPositionOfSlotImpl` (via `PortalComponent.GetFreeRoutingLane` -> `GetNearestSlot`), `Simulator_DestroyObjectImpl`, `World_ObjectSetHiddenFlags`, `RouteManager_IsOkToIdleAtCurrentLocation` and `World_ArePointsConnectedByLand` hold 3-7% of long-slice samples; with async off they are 0-0.8% [measured]. These are the native waits docs/88 (destroy) and docs/90 (gate-12 F-B slot position, design SH) already target, not script work [inferred from the on/off split].
- **Unaddressed top 5** (section 5): SafeSpot `FindGoodLocation` burst; the velvet-rope bouncer's `WatchDoor` loop re-routing to its slot every loop (BP, ~2.8% steady); NPC role-start instantiation; the reaction broadcaster's radius query (residual after `kBroadcasterThrottle`); motive buff triggers removing absent buffs. Together their caps are ~35-45 ms/s on average and ~0.5 s per minute of long-slice time in the worlds where they apply [inferred].

## 2. Data and method

| Set | Runs (tag prefix `clean-night-2026-10-17-` / `clean-night-2-2026-10-18-` / `h6-m1-2026-10-1x-`) | Async (`RouteAsync.Mode`) | Aligned samples | Counted (script, not route leaf) |
|---|---|---|---|---|
| BP, async on | 10-17 `bp-env-on`, `bp-s2-on`, `bp-s2-shadow`; 10-18 `bp-ar-shadow`, `bp-g0ro-shadow`, `bp-g0w-shadow`, `bp-l1-shadow`, `bp-p10`, `bp-p1b`, `bp-s2cov-shadow`, `bp-slotro-shadow` | 1 | 9,755 | 5,668 (>= 50 ms: 993; >= 100 ms: 178) |
| IP, async on | 10-17 `ip-env-on`; 10-18 `ip-p10`, `ip-p1b` | 1 | 2,660 | 1,404 (220; 100) |
| BP, async off | 10-17 `bp-env-off` (clean night), h6-m1 `bp-env-off`, `bp-d-off-same`, `bp-h6-m1-seed-off` | 0 | 3,544 | 2,009 (237; 38) |
| IP, async off | 10-17 `ip-env-off` (clean night), h6-m1 `ip-env-off` | 0 | 1,767 | 891 (91; 18) |
| SV (reference only) | `sv`, `sv-all`, `sv-mod` (2026-10-12, older build, no `threaded` column) | n/a | 2,111 | 1,139 (176; 64) |

Async state per run comes from the queue files (`tools/harness/queues/clean-night.json`, `clean-night-2.json`, `h6-m1.json`): every 2026-10-18 job and the 10-17 `*-on`/S2 jobs run `RouteAsync.Mode=1`; the `env-off`, `d-off-same` and `seed-off` jobs `Mode=0`. All BP runs use `Baseline-BP-Spring` (h6-m1 seed runs: `Baseline-BP`), IP runs `Baseline-IP-Spring`, seed 12345, story progression off. The docs/74 hot-spot switches (`kDetailPriorityCache`, `kHoursMemo`, `kCaneEarlyOut`, `kTooltipThrottle`) and `kSpatialCache` were off; the UI caches (`kMotiveBarImageCache`, `kBuffGridInPlace`, `kSimNameCache`) and `kBroadcasterThrottle` on (`mods/ts3fixes/src/Instantiator.cs`, `tools/setup/build-fixes.ts`) [verified].

**`stall-split.ts --owners`** (new mode, reads only; `TS3_REPO=<main checkout>` from a worktree): aligns each run's managed samples with its profiler seconds as the default mode does (route-plan share vs route ms/s, docs/64 §2; all used runs r 0.23-0.71, runs under 0.2 left out). Buckets: **avg** = every aligned sample; **>= 50** / **>= 100** = samples within 100 ms of a hitch iteration whose ThreadedObjects ms **minus its route ms** is at least 50 / 100 ms (so long slices that are mostly route search do not count). Samples with an empty stack or a `ScriptCore.Route.*` leaf are left out of the denominator. Keys: category (docs/64 §2), task, interaction, own frame (first game/mod method above ICs and BCL), self (innermost frame), inclusive. Commands: `node tools/harness/stall-split.ts --owners <tags>` for each set; follow-up caller/callee splits came from a throwaway script over the same samples (not committed).

**Scale [inferred]:** the counted samples are ~58% (BP) / 53% (IP) of sim-thread wall time, so 1% of counted samples ~ **5.8 ms/s (BP), 5.3 ms/s (IP)**. Non-route ThreadedObjects slices (hitch logs, async-on set): BP 47 slices/min of >= 50 ms holding 3,580 ms/min, 6.6/min of >= 100 ms holding 952 ms/min; IP 26/min, 2,861 ms/min and 10/min, 1,748 ms/min [measured]. A share of a long-slice bucket times those ms/min gives the long-slice time an owner holds.

## 3. Owners by world [measured]

### 3.1 Category (async on; async off in brackets)

| Category | BP avg | BP >= 50 | BP >= 100 | IP avg | IP >= 50 | IP >= 100 |
|---|---|---|---|---|---|---|
| Sim interaction step | 29.7 (27.3) | 35.4 (30.8) | 32.0 (39.5) | 23.4 (21.3) | 33.2 (27.5) | **49.0** (44.4) |
| `Lot.Simulate` / reactions | 14.7 (14.9) | 14.5 (15.6) | 15.2 (23.7) | 19.7 (18.2) | 20.0 (16.5) | 22.0 (11.1) |
| `SimUpdate` (motives, buffs) | 15.6 (16.3) | 13.6 (13.9) | 11.8 (13.2) | 15.8 (15.5) | 15.5 (14.3) | 10.0 (0) |
| autonomy search | 8.9 (10.8) | 7.4 (11.0) | 7.3 (2.6) | 12.2 (10.9) | 7.3 (5.5) | 4.0 (0) |
| native event callbacks (`OnObjectLotUpdate` -> `LotManager.DispatchMessage`) | 6.6 (7.4) | 5.4 (4.2) | 5.1 (0) | 5.9 (7.4) | 4.1 (3.3) | 0 (0) |
| alarms / one-shot functions | 3.4 (3.1) | 4.7 (6.3) | **10.7** (10.5) | 3.3 (2.4) | 5.5 (7.7) | 5.0 (0) |
| `SimBuilderTask` (age-up outfits) | 2.9 (1.6) | 1.7 (0.4) | 1.1 (0) | 3.8 (2.8) | 2.3 (2.2) | 0 (0) |
| NRaas | 1.7 (2.2) | 1.1 (2.1) | 2.2 (0) | 1.4 (2.8) | 2.3 (7.7) | 5.0 (22.2) |

Percent of counted samples. The async-off >= 100 columns rest on 38 (BP) and 18 (IP) samples: direction only.

### 3.2 Inclusive owners (async on)

| Owner (inclusive) | BP avg | BP >= 50 | BP >= 100 | IP avg | IP >= 50 | IP >= 100 | Status |
|---|---|---|---|---|---|---|---|
| Script side of routing (`SimRoutingComponent.DoRoute`, route leaf excluded) | 14.0 | 19.9 | 20.2 | 8.8 | 12.7 | 13.0 | route track (portals, walk styles, footprints) |
| autonomy (`Autonomy.RunAutonomy`) | 8.9 | 7.4 | 7.3 | 12.0 | 7.3 | 4.0 | gap 12 (off-thread plan) |
| `Motives.UpdateMotives` | 6.0 | 6.3 | 3.9 | 6.2 | 6.4 | 4.0 | **unaddressed** (§5.5) |
| `Lot.UpdateReactions` (broadcasters) | 5.8 | 4.6 | 2.8 | 5.3 | 2.7 | 2.0 | `kBroadcasterThrottle` on; residual §5.4 |
| `Lot.UpdateDetailPriority` | 4.1 | 4.3 | 2.3 | 6.8 | 4.6 | 4.0 | built, off (docs/74 §6.1) |
| `OutfitUtils` / outfit switches | 4.6 | 3.6 | 5.6 | 4.7 | 3.6 | 0 | not addressed (age-up outfits, role outfits) |
| LynMods `StudentUnionLibrary.VisitCommunityLotLabLecture` (installed CC mod) | 4.1 | 5.6 | 6.2 | 2.3 | 4.6 | 7.0 | third-party; mostly its route and `FindBestActionForCommodityOnLot` |
| `AlarmManager.Simulate` | 3.7 | 4.4 | 9.6 | 4.3 | 11.4 | **16.0** | partly unaddressed (§4) |
| `WatchDoor.Run` (velvet-rope bouncer) | 2.8 | 3.4 | 3.9 | 0 | 0 | 0 | **unaddressed** (§5.2) |
| `PortalComponent.LockRoutingLane` | 2.4 | 4.6 | 6.2 | 1.1 | 5.0 | 6.0 | async IC waits (docs/90) |
| `Ts3Fixes.Fixes.SafeSpot` | 0.4 | 1.1 | 2.3 | 1.8 | 10.0 | **22.0** | **ours, unaddressed** (§5.1) |
| `Role.StartRole` | 0.5 | 2.0 | **7.9** | 0.1 | 0.5 | 1.0 | **unaddressed** (§5.3) |
| `TombRoomManager.AboutToPlanRouteCallback` | 1.8 | 1.4 | 0 | 0.5 | 0 | 0 | `kSpatialCache` (off) |
| `Lot.GetHoursOfOperation` | 1.3 | 1.3 | 1.1 | 0.9 | 0.5 | 0 | built, off (docs/74 §6.3) |
| `HudModel.UpdateSimInfo` | 2.1 | 1.6 | 0.6 | 2.9 | 3.6 | 3.0 | UI caches on; residual `OnMoodChanged` -> occult info |
| `TooltipManager` | 0.8 | 0.4 | 0 | 1.0 | 0 | 0 | built, off (docs/74 §5) |
| `Cane.SimRequiresCaneWalk` | 0.8 | 0.8 | 0.6 | 0.6 | 0.9 | 0 | built, off (docs/74 §6.2) |
| `OnMotivesChanged` | 1.2 | 0.9 | 0 | 1.1 | 1.8 | 2.0 | UI caches on (residual) |

### 3.3 Self (innermost frame) in long non-route slices (async on)

BP >= 50: `Slots_GetPositionOfSlotImpl` 3.8%, `SACS_RequestStateImpl` 3.6% (doors and animation requests), `Simulator_DestroyObjectImpl` 2.4%, `World_ObjectSetHiddenFlags` 2.3%, `Query_GetObjectsOnLot` 2.2%, `Dictionary.GetPrev` 2.2%, `Query_GetObjects` 2.1%, `Query_GetObjectsInRange` 1.8%, `DefaultComparer.Equals` 1.8% (3.9% at >= 100: `CommodityInteractionMap` removes during role starts), `World_FindGoodLocation` 1.7%. IP >= 100: `World_FindGoodLocation` **22%**, `Slots_GetPositionOfSlotImpl` 7%, `RouteManager_IsOkToIdleAtCurrentLocation` 4%, `World_RemoveObjectFromObjectManagerImpl` 4%, `World_ArePointsConnectedByLand` 4%. So most long-slice self time is native ICs called from script, not interpreted C# [measured].

### 3.4 Sunset Valley (old data, reference)

Only the 2026-10-12 proxy runs (whole iteration as the slice, older build without most fixes, winter save): in slices of 100 ms or more `PondManager.RouteToSkatingLocation` (ice skating) 20% and `SimBuilderTask` 14%; averages close to BP's (SimUpdate 18%, autonomy 13%) [measured, 64 samples at >= 100]. Not used for ranking.

### 3.5 /taskstall records

Only the 2026-10-13/14 captures exist (docs/64 §5): of the 10 sampled ThreadedObjects stalls of 250 ms or more, 2 are not route: a door `SacsRequest` (268 ms) and the kleptomaniac trait's `GetObjectProductListFiltered` in a rabbit hole (393 ms). Both reappear here only as small shares (`Door.SacsRequest` 1.3% of BP >= 50).

## 4. What is already addressed, and what is not

| Owner | Share (counted samples, async on) | Status |
|---|---|---|
| `Lot.UpdateDetailPriority` | BP 4.1% / IP 6.8% avg | **built, off** (docs/74 §6.1): turn on after `ts3fixes-hotspots.json`; the largest steady item in this doc |
| `Lot.GetHoursOfOperation`, `Cane.SimRequiresCaneWalk`, `TooltipManager` | 0.6-1.3% each | built, off (docs/74 §5, §6.2, §6.3) |
| `TombRoomManager.AboutToPlanRouteCallback` (plan callbacks; also most of `WatchDoor`'s non-route cost) | BP 1.8% | built, off (`kSpatialCache`, docs/20 §4a) |
| motive panel, names, moodlet grid | `OnMotivesChanged` ~1.2% | on since v0.1.2; residual: `SimInfo.OnMoodChanged` -> `HudModel.UpdateSimInfo` -> `OccultManager.GetSimOccultInfo` (15 of its 23 samples), not covered |
| reaction broadcasters | 5.3-5.8% | `kBroadcasterThrottle` on (off-screen pulses thinned); residual radius query §5.4 |
| autonomy scoring | 9-12% avg, lift < 1 in long slices | gap 12 off-thread plan (docs/59, docs/75, docs/78); not a stall owner |
| slot position / destroy / hidden flags / idle-location ICs under async | 3-7% of long-slice samples, ~0 with async off | native route track: docs/90 (SH), docs/88 (destroy waits) |
| script side of routing (`DoRoute`, portals, `GetAdjustedWalkStyleForRoute` 2.6%) | 14-20% BP | route track; walk-style comparer and cane early-out are ours |
| **SafeSpot `FindGoodLocation` burst** | IP 22% of >= 100 | **not addressed** (§5.1) |
| **bouncer `WatchDoor` loop** | BP 2.8% avg | **not addressed** (§5.2) |
| **NPC role start instantiation** | BP 7.9% of >= 100 | **not addressed** (§5.3) |
| **broadcaster radius query** | BP 1.6% avg (`GetInRadiusSims` -> `Queries.GetObjects`) | **not addressed** (§5.4) |
| **motive buff triggers** | `Motive.Update` 4.5% avg | **not addressed** (§5.5) |
| alarm upkeep: `MinorPet.CanSpawnMoreMinorPetsInWorld` and `Wildflower.CanSpawnWildflowers` (world-wide `GetObjects` per spawn), pet pool `CreatePet` (`CASLogic.RandomizePet`), `Relationship.DecayAll`, spawner `DeleteOldestUntakenObject` | ~0.7% (spawner counts) + ~0.8% (pet pool) avg BP; 1-2% of >= 50 | not addressed; next after the top 5 (count memo per sim tick with a spawn-time increment; the pet pool is CAS work) |
| `SimBuilderTask.UpdateSimOutfitsOnAgeUp` (`MakePartsAgeAppropriate`) | 2.9-3.8% avg, lift < 1 | not addressed; steady, not a stall owner here (docs/64 S5 saw it in older BP proxy runs) |
| LynMods lab lecture visits | 4-7% | third-party mod: report only |

## 5. Unaddressed top 5: static look and fix ideas

Gain caps are upper bounds: the owner's whole share, before any fix overhead [inferred]. Effort in Claude-with-Rodney days, including the switch, counters, repro and one A/B.

### 5.1 SafeSpot `FindGoodLocation` burst at the school run (ours, IP)

**Static look [verified, `mods/ts3fixes/src/Fixes.cs`].** `RabbitHoleHook.EnterHook` (fix b3) places an unseen townie by the rabbit hole entrance via `SafeSpot`. `SafeSpot` calls `GlobalFunctions.FindGoodLocation` up to five times per Sim (two starts from the lot's verified spots, three from the entrance) before the cached routability verdict; the verdict check has a per-tick budget, `FindGoodLocation` has none. In IP the samples sit under `GoToSchoolInRabbitHole.RouteNearEntranceAndIntoBuilding`: many children head to the same school in the same minutes, each paying 2-5 native placement searches in its own task [measured: 21 of 22 SafeSpot samples in IP slices >= 100 ms are `FindGoodLocation`]. Async off shows the same owner at a lower share (5.5% of >= 50), so async adds wait inside the IC but does not create it [measured, 2 runs].

**Fix idea.** (a) Budget `FindGoodLocation` calls in `SafeSpot` per sim tick (e.g. 2 calls or ~3 ms), and when over budget yield the Sim's task one tick (`Simulator.Sleep(0)` where the existing yield guard allows) instead of falling through to the full walk; the result is the same spot one tick later. (b) Try a verified spot first without a fresh search when nobody stands on it (one occupancy query instead of `FindGoodLocation`); that changes placement slightly, so behind its own switch. (c) With async routing on, measure whether the shortcut still saves sim-thread time against the async walk it replaces.
**Gain cap:** IP ~22% of >= 100 slices = ~385 ms/min of long-slice time and ~10% of >= 50 = ~290 ms/min (school-run minutes); average ~9 ms/s IP, ~2 ms/s BP [inferred]. **Effort:** 0.5-1 day.
**Status (2026-10-18).** Built, not run: ts3fixes switch `safespotreuse` (tuning `kSafeSpotReuse`, default off, verify `safespotreuseverify`, counters 90-92). A verified spot stores its facing, so later tries answer from it without `FindGoodLocation` while the lot's cached verdict holds (fix idea (b) done as reuse, not an occupancy query; no per-tick budget needed because only a lot with no verified spot still searches). Queue job `ip-safespot-ab` in `tools/harness/queues/stutter-fixes.json`.

### 5.2 Velvet-rope bouncer re-routes to its slot on every loop (BP)

**Static look [verified].** The bouncer's `VelvetRopes.WatchDoor` interaction routes to `RoutingSlot_2`, then loops the watch animation with a loop callback that calls `RouteToSlot` to the same slot again on every callback. Each call creates a route, runs the plan callbacks (`TombRoomManager.AboutToPlanRouteCallback` -> `AddForbiddenPortalsToRouteForLot` is 80 of the 133 `RouteBackoffHook.PlanBackoff` samples below it), plans (route-leaf samples, not counted here) and runs the follow, footprint and route-action setup even when the Sim already stands on the slot [measured: 129 of 130 `WatchDoor.LoopDel` samples are in `RouteToSlot`]. Present in every BP run (1-4 samples per run at >= 50 ms), absent in IP [measured].

**Fix idea.** Hook the loop callback: skip `RouteToSlot` when the Sim's position is within a small distance of the slot's routing position and it is not pushed or carried; otherwise call the game's method. Near-identical (a route to where the Sim stands is a no-op walk), so behind a switch with a verify counter (calls skipped / passed, distance histogram). `kSpatialCache` on also trims the plan callbacks.
**Gain cap:** ~2.8% of BP counted samples = ~16 ms/s average plus the route-plan time of those calls; ~3.4% of >= 50 = ~120 ms/min of long-slice time [inferred]. **Effort:** 0.5 day.

### 5.3 NPC role start instantiates a Sim in one slice (BP)

**Static look [verified].** Each role (bouncer, bartender, paparazzi and others) sets a daily start alarm; the alarm handler queues a `OneShotFunction` whose `StartRole` sets up the uniform, and when the role Sim has no body yet picks a move-in lot and instantiates it, then switches outfits and creates or freezes motives. Inside instantiation the samples show NPC inventory seeding (books for traits, umbrellas, trait objects, diplomas), the commodity interaction map being rebuilt (`CommodityInteractionMap.RemoveObject` -> `List.Remove` with `InteractionObjectPair.Equals`, the `DefaultComparer.Equals` self time), `FindGoodLocation` and our `RoutableCacheHook.IsPointInLotSafelyRoutable` [measured]. All roles of a venue start at the same hour, but each start is its own task, so one slice holds one instantiation [inferred from the loop shape, docs/64 §6].

**Fix idea.** Split the one-shot into several ticks: yield after the instantiation and before the outfit switch (the one-shot task may yield; the alarm is registered as yielding), and defer inventory seeding for role Sims by one tick. Same end state, a tick later [inferred; must check that nothing reads the role Sim's inventory or outfit in between]. Alternative with no timing change: speed the commodity map rebuild (a single `RemoveAll` pass keeps the list order of the remaining entries [inferred]; docs/74 §6.5 rejected an index because it changes order).
**Gain cap:** ~7.9% of BP >= 100 slices (~75 ms/min) and ~2.5% of >= 50 (~90 ms/min), at role start hours; average ~4 ms/s [inferred]. **Effort:** 1.5-2 days (yield safety, save during a split start).

### 5.4 Reaction broadcaster radius query (residual after `kBroadcasterThrottle`)

**Static look [verified].** `ReactionBroadcaster.GetInRadiusSims` runs a native radius query for Sims around the broadcasting object, then filters each hit (in world, not in a rabbit hole, species, tomb room, room, line of sight, could-see, can-react, sitting in a vehicle) into a new dictionary; it allocates a species list and the dictionary on every call. In samples, the radius query is 89 of 254 `PulseSims` samples, `CanSeeObject` 36, the enter callbacks 31 [measured, BP].

**Fix idea.** Empty-radius early-out: keep the active Sims' positions once per sim tick (the `RefreshViewers` pattern already in `Fixes.cs`, extended to all instantiated Sims) and return an empty result without the native query when no Sim is within the pulse radius plus the buffer. Identical whenever the check is a superset of the native one (use a slightly larger radius); the native query and filters still run when any Sim is near. The share of pulses with no Sim nearby is unknown: add a counter first.
**Gain cap:** the radius query share, ~1.6% BP = ~9 ms/s average, times the empty-pulse fraction; little long-slice time (lift < 1) [inferred]. **Effort:** 1 day.

### 5.5 Motive buff triggers remove buffs the Sim does not have

**Static look [verified].** Every motive update walks the motive's buff triggers; for the trigger whose range holds the value it removes every listed buff, and for every other trigger whose buff is a perma-moodlet it removes that buff, whether or not the Sim has it. `BuffManager.RemoveElement` on an absent buff still does an enum-keyed dictionary probe (boxing through the default comparer), scans the delayed-buff list and probes the buff table, then does nothing. Samples under `Motive.Update`: removes 31 of 254, `IsPermaMoodlet` 20, decay multiplier lookups 12 [measured, BP].

**Fix idea.** Hook `BuffManager.RemoveElement(BuffNames)` (or the trigger loop): return at once when the buff is not active, its ref-count list is empty and no delayed buff is queued; all three checks are cheap and the original is a no-op in exactly that case [verified: the original's three steps]. Exact.
**Gain cap:** ~0.9% of counted samples = ~5 ms/s in both worlds, ~30 ms/min of >= 50 slice time; steady, not a stall fix [inferred]. **Effort:** 0.5 day.

**Built 2026-11-15 (lane L4, branch `feature/lane-L4-1115`, NOT RUN):** ts3fixes `buffremovenoop` (`ExactMemos.cs`, tuning `kBuffRemoveNoOp`, off; counters 1018-1019). Early return only when the buff is not in `mValues`, `mBuffCountList` is empty (no per-name probe, so no boxing) and `mDelayedBuffs` is null or empty; anything else runs the game's method. Exactness model in `tools/selftest/hotspots`. docs/245 §5: about half of these samples are real removals, which still run the game's method.

## 6. Limits

- **Sampler coverage:** 5 Hz, ~180 s per run (minutes 1-4 of a 10-min run), stacks without time stamps placed by correlation (up to ~0.5 s off, docs/64 §9); the >= 100 buckets hold 178 (BP) and 100 (IP) counted samples, so rows under ~3% there are a few samples each. IP has only 3 async-on runs, all the same save and seed: the school-run burst is one repeated in-game moment, real but tied to that hour.
- **Async mix:** the ranking uses the async-on runs (all of today's data). Async off has fewer runs (4 BP, 2 IP) and different builds; the IC-wait owners in §1 are judged from that split, not from a same-build A/B.
- **Stage mix:** samples cannot tell ThreadedObjects from TaskObjects or other script stages; ThreadedObjects is ~90% of script stage time in these runs (~600 vs ~70 ms/s TaskObjects) [measured, seconds.csv], so NRaas `FunctionTask` time and the harness agent are mixed in at small shares.
- **ms/s conversions** use the counted-sample fraction of wall time; the long-slice ms/min use the hitch logs, which only list iterations of 50 ms wall or more.
- No live check of any fix idea; every gain above is a cap.

## 7. Ledger

Rows I-95-1 to I-95-7 in docs/51 §28.
