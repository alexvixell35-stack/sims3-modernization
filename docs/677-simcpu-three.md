# 677: the three sim-thread freezes left in the forced-routing storms

2026-11-20, `feat/simcpu-three`. Evidence: storm logs s149-s175 (stall-watch script stacks next to the `hitch:` lines),
decompiled C# (ilspycmd). Built: ts3fixes `nearestlotlazy` (`NearestLot.cs`, `NearestLotCore.cs`), default off, exact.
**Not exercised in the game:** s189 ran with it on and logged `nearestlot: same 0 DIFFERENT 0` (0 calls).

With every routing fix on, the frames of 300 ms or more in the storms have hitch cause `sim-cpu` and lock wait 0. Three
stall leaves were named: `World_ArePointsConnectedByLand`, `Query_CountObjectsOnLot`, `World_FindGoodLocation`.

## 1. Two of the three are one managed loop [verified, script stacks + decompile]

The full script stacks (20 samples over s140-s188 that carry more than the leaf frame):

| leaf | caller chain | samples |
|---|---|---|
| `World_ArePointsConnectedByLand` | `StreetParking.ParkingIsOnSamePieceOfLand` < `Lot.HasValidStreetParking` < `CarNpcManager.InitCarForLotRoute` | 7 |
| `World_ArePointsConnectedByLand` | `SimRoutingComponent.EnterHouseboatThroughGangway` < `CheckAndUpdateForHouseboats` < `PostPlanRouteCallback` | 10 |
| `Query_CountObjectsOnLot` | `Lot.CountObjects` < `UnchartedIslandMarker.DoesLotHaveUnchartedIslandMarker` < `IsUnchartedIsland` | 3 |
| `Query_CountObjectsOnLot` | `Lot.CountObjects` < `Lot.CreateMapTag` < `MapTagManager.AddTag` | 1 |

`CarNpcManager.InitCarForLotRoute` (an NPC car or taxi sent to a lot) runs
`targetLot.HasValidStreetParking() ? targetLot : LotManager.GetNearestLot(targetLot.Position, null, requireStreetParking: true)`.
`GetNearestLot` then does, per call:

1. `LotManager.AllLotsWithoutCommonExceptions`: for **every lot of the world** `UnchartedIslandMarker.IsUnchartedIsland(lot)`,
   which is `lot.CountObjects<UnchartedIslandMarker>()`: one native `Query_CountObjectsOnLot` per lot.
2. For **every lot** left, `lot.HasValidStreetParking()`: `ParkingIsOnSamePieceOfLand` calls
   `World.ArePointsConnectedByLand(lot position, parking spot)` for up to ten parking spots (it stops at the first spot
   that is not connected). Each call is two closest-waypoint searches (docs/676 §3) with live obstacle tests.
3. Only then the squared distance of the lots that passed, smallest wins.

So one call is (lots) object counts plus up to (lots x 10) land checks plus (lots x 20) waypoint snaps, to pick the one
nearest lot. In Isla Paradiso most lots sit on islands without street parking on their own land, so the first test of
`InitCarForLotRoute` fails often and the whole-world loop runs. That is why neither native call looks slow by itself
(docs/676: `sameLand` 30-60 calls, 0 ms in the planner) and why both leaves appear in the same frame: s170's 290 ms frame
has `World_ArePointsConnectedByLand` at 179 ms and `Query_CountObjectsOnLot` at 238 ms of the same stall, in the order of
`InitCarForLotRoute` (own lot's parking first, then the loop's counts). The Sims3Performance frames in those stacks are
its hook on the query path; the waste is the caller's loop, not that mod and not the query.

Not measured: how many lots and how many ms each part takes per call (the storms carry no timer on it). The new hook
logs that with the switch off (§2).

The second `ArePointsConnectedByLand` caller, `EnterHouseboatThroughGangway`, is one call per houseboat route; its 10
samples were not attributed to a frame of 300 ms or more. Left alone.

## 2. Built: `nearestlotlazy` (ts3fixes, default off, exact)

Hook on `LotManager.GetNearestLot(Vector3, List<Lot>, bool)` (the two shorter overloads call it). Switch on:

- one pass over `LotManager.AllLots` with the cheap tests EA also runs (`IsWorldLot`, the ignore list) and the squared
  distance to each lot;
- then the costly tests (`IsCommunityLotOfType(kEP10_Diving)`, `IsHouseboatLot`, `IsUnchartedIsland`, and
  `HasValidStreetParking` when asked) on the nearest lot, the next nearest, ... until one passes.

Same lot as EA's loop: EA takes the first lot in list order with the strictly smallest distance among the valid ones;
`NearestLotCore.LazyPick` takes the first lot in list order with the smallest distance among the untested ones and
returns the first valid one. The tests read state only. A lot EA can never take (distance NaN or not below
`float.MaxValue`) is never tested. An exception in our path counts as an error and EA's loop answers.
All Sims and callers: the answer is EA's, so no household exemption is needed.

- Tuning: `kNearestLotLazy` (false), `kNearestLotLazyShadow` (false: with the switch on, EA's loop runs too, its lot is
  returned and `ts3fixes: nearestlot: shadow same|DIFFERENT ...` is logged per call; costs EA's full time).
- Log lines: `ts3fixes: nearestlot: EA's loop N ms (switch off) ...` for a call of 50 ms or more with the switch off (the
  measurement §1 lacks); `ts3fixes: nearestlot: N ms, T of L lots tested ...` for one of ours of 20 ms or more.
- Counters: no slot; `NearestLotLazyCheck result` prints calls, ms, lots tested, shadow compares and differences, errors.
- Repro `nearestlotlazy-check`: `NearestLotLazyCheck run` asks EA's loop and ours the same 8 questions (4 lots, with and
  without street parking) and one call through the hooked method; off = BROKEN, on = FIXED.
- Offline: `tools/selftest/nearestlot` (20,000 random lists with equal distances, NaN, infinity and `float.MaxValue`:
  same index as EA's loop, never more tests; 66,944 tests against EA's 388,956), ts3fixes build, swap table row.

Not covered: the other users of `AllLotsWithoutCommonExceptions` (`GetFarthestLot`, `SelectRandomLotForNPCMoveIn`, the
four-argument `GetNearestLot`, map tags) still pay one object count per lot. If the `EA's loop` lines or later stacks
show them, the same order of tests applies there; a cache of the marker lots would need a proof that markers do not move.

## 3. `World_FindGoodLocation` ~340 ms with FglFacing and FglMemo on: the native search, caller not known

`[FglProf]` was off in s170-s173, so there is no search line for these frames, and the stall-watch script stack shows
only the leaf frame. The three native stack scans per freeze (s170 #33 346 ms, s171 #32 340 ms, s173 #26 395 ms):

- the innermost frames are `TS3W+0x7465CB`, `+0x7465D8`, `+0x7466FA`, `+0x7468E2`, `+0x746C7B`, `+0x747021`,
  `+0x74705C`: image addresses `0xB465CB`-`0xB4705C`, inside the placement test `0xB463F0` and the search
  `0xB47040` / `0xB47070` that `fglprof.cpp` hooks [verified against the address table rows]. Under them:
  `RoutingWorld_PortalCheckLocked`, `LotQuadTree_CollectMasked`, `QuadTreeCell_ClipScratch`,
  `LotRouting_FootprintsNearPoint` (the test's footprint and routable checks).
- **So the time is in the native tile scan and its placement tests, not in a managed check.** A first reading of
  these scans took the `ts3native+0x7123A` entries (our interpreter-invoke hook) for a managed placement callback that
  plans per spot; that was wrong: the scan lists them outside the search frames (the task's own invoke), and
  `Fixes.SafeSpot` passes no `PlacementCheckFunc` at all (`new World.FindGoodLocationParams(start)`; its route check
  `SpotVerdict` runs after the search returned).
- Callers with a full script stack over all runs: `GlobalFunctions.FindGoodLocationNearby` 36, `Fixes.SafeSpotBody` 25,
  `Sim.AttemptToFindSafeLocation` 2, others 4. Which of them ran the three frames above is not in the logs.

Why FglFacing and FglMemo did not cut these searches is **not known**: without the search line there is no object,
tile count, test count or first-failing check. Profiled runs for scale: the slowest search is 54 ms in s169 and 219 ms
in s158 (a guitar jig's indoors scan, 15,099 part tests; docs/675 `fglnonescan` merges its repeats). Candidates, none
confirmed: a whole-lot failed scan of a jig from `FindGoodLocationNearby` (three stages, `fglnonescan` off in those
runs), or a SafeSpot fresh search on a large lot (one failed search per 30 sim minutes since 9f20444a).

Nothing built: a cap or memo here would be a guess. Next storm: `[FglProf] Enabled=1 LogMs=50 Frames=16` on the s170
arm; the `fgl prof: search` line of the frame names object, lot, tiles, tests by check, repeat state and the managed
caller, and decides between `fglnonescan`, the SafeSpot memo and a native change in `fglprof.cpp`.

### 3.1 Not built in this pass

- A memo for `AllLotsWithoutCommonExceptions` (`GetFarthestLot`, `SelectRandomLotForNPCMoveIn`, the four-argument
  `GetNearestLot`, map tags): its answer changes with the lot table, a lot's type, houseboat flag, the marker objects
  on each lot and `sDiscoveredLots`. An exact memo needs an invalidation hook on each of those writers (marker create,
  destroy and move among them); they were not read, so no memo was built. The lazy order does not apply to callers
  that need the whole list.
- `EnterHouseboatThroughGangway`'s land check: one call per route, no frame of 300 ms attributed to it.

## 4. Next 2-minute storm

- Arm A (measure): s170 ini, `kNearestLotLazy=False`, plus `[FglProf] Enabled=1 LogMs=50 Frames=16`. Expect
  `ts3fixes: nearestlot: EA's loop N ms` next to the `ArePointsConnectedByLand` / `CountObjectsOnLot` frames.
- Arm B: the same with `kNearestLotLazy=True`, `kNearestLotLazyShadow=True` for the first run (every line `shadow same`),
  then shadow off: no frame of 300 ms or more with those two leaves, `nearestlot:` lines rare and small.
