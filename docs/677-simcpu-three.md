# 677: the three sim-thread freezes left in the forced-routing storms

2026-11-20, `feat/simcpu-three`. Evidence: storm logs s149-s175 (stall-watch script stacks next to the `hitch:` lines),
decompiled C# (ilspycmd). Built: ts3fixes `nearestlotlazy` (`NearestLot.cs`, `NearestLotCore.cs`), default off, exact.
**NOT RUN in the game.**

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

## 3. `World_FindGoodLocation` ~340 ms with FglFacing and FglMemo on [inferred, stack scans only]

`[FglProf]` was off in s170-s173, so there is no search line for these frames, and the stall-watch script stack shows
only the leaf frame. What the three native stack scans per freeze show (s170 #33 346 ms, s171 #32 340 ms, s173 #26
395 ms):

- under `IC_World_FindGoodLocation` the scan holds `interp_mono_invoke` return addresses (`ts3native+0x7123A` x9-10,
  `TS3W+0x79650C`) and under those route-plan and portal-check frames (`RoutingWorld_PortalCheckLocked`,
  `LotQuadTree_CollectMasked`, `LotRouting_FootprintsNearPoint`, `0x7465CB`-`0x7468E2`);
- that is the shape of the search's **managed placement check** running a route plan per offered spot
  (`Sim.IsPointSafelyRoutable.Test` from `Fixes.SafeSpot` / `FindRoutablePointInsideNearFrontDoor`: 25 and 1 of the 67
  full FindGoodLocation script stacks), not of the native tile scan.

If so, the time is in plans made inside the callback, which FglFacing (fewer facings per tile) and FglMemo (repeat of an
identical failed native search) do not touch: FglFacing saves tests, the callback only runs for tiles that passed them,
and FglMemo's key does not see a search whose result depends on a managed check. The profiled runs agree on the native
side: the slowest single search is 54 ms in s169 and 219 ms in s158 (the guitar jig's indoors scan, docs/675
`fglnonescan`); no profiled native search reaches 340 ms.

**Not proven**: stack scans keep stale return addresses, and no run has FglProf on together with these frames. Nothing
built here. Next storm: `[FglProf] Enabled=1 LogMs=50 Frames=16` on the s170 arm; the search line of the 340 ms frame
gives object, tiles, tests, `search rest` (callback time shows there) and the managed caller. If the callback is
confirmed, the fix belongs with `safespotcap` and SafeSpot's own memo (ts3fixes), not in `fglprof.cpp`.

## 4. Next 2-minute storm

- Arm A (measure): s170 ini, `kNearestLotLazy=False`, plus `[FglProf] Enabled=1 LogMs=50 Frames=16`. Expect
  `ts3fixes: nearestlot: EA's loop N ms` next to the `ArePointsConnectedByLand` / `CountObjectsOnLot` frames.
- Arm B: the same with `kNearestLotLazy=True`, `kNearestLotLazyShadow=True` for the first run (every line `shadow same`),
  then shadow off: no frame of 300 ms or more with those two leaves, `nearestlot:` lines rare and small.
