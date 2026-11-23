# 668: route storm, short forced-routing test arms

Date: 2026-11-19, results 2026-11-20. Status: built, run in game (s141-s147, §3). 2026-11-20: six opt-in kinds (§1.1), built, not run yet.

Idle play at 1x shows only ~460-540 route plans per minute in Isla Paradiso (s140 IP, 2 x 15 min) and 1-10 game hitches per
15 min. That is too few to compare a fix's off and on arms in under 12-15 min each. A route storm forces routing hard with the
game's own interactions, so an arm needs about 4 min.

## 1. Agent command `route_storm` (RouteStormHandlers.cs)

`route_storm {count?: 40, kinds?: [...], repeatS?: 0, seed?: 1, household?: false}` returns the first wave.
`{status: true}` returns the totals. `{stop: true, cancel?: true}` stops the waves and cancels the pushed interactions that are
still queued. Start needs Live mode; stop and status work in any mode.

**Sims.** Resident NPCs already instantiated in the world, sorted by SimDescriptionId and shuffled with `Random(seed, wave)`.
- Never the active household, a selectable Sim or the selected Sim (unless `household: true`).
- No babies or toddlers, no service Sims, no special households (service, tourist, alien).
- Pets and horses only for kind `pets`.
- Each Sim gets one push per wave. A Sim is skipped while an earlier push of the storm is still in its queue.

**Kinds.** A push is only ever the game's own interaction at a non-user priority, as autonomy or a situation queues it
[verified against the decompiled callers]. A kind may repeat in `kinds` to weight it. The default is the seven kinds in
this table; the opt-in kinds of §1.1 run only when named.

| kind | interaction | EA precedent |
|---|---|---|
| `lot` | `VisitCommunityLot` (Autonomous priority, autonomous) to a random lot from the farthest half of the community lots | a townie's own outing |
| `home` | `Sim.MakeSimGoHome(sim, false, NonCriticalNPCBehavior)` | `Occupation.SendSimHome`, situations |
| `boat` | a lot trip to a lot behind >= 60 m of open sea (4 tries), else to a houseboat, dive, resort or port lot | water taxis, boats, gangways |
| `meet` | `VisitLot` / `VisitCommunityLot` onto the lot another picked NPC is on (or its home) | converging visits |
| `nopath` | `Terrain.GoHere` at NonCriticalNPCBehavior to a sealed spot (`SealedPoint`) or a point well out to sea | no-path retries |
| `elevator` | residents of elevator lots go home; others visit a lot with `ElevatorDoors` (Bridgeport high-rises) | apartments, elevators |
| `pets` | `GoToLot` at NonCriticalNPCBehavior, autonomous, to a far community lot | `WildHorsePool` |

A refused visit, for example one blocked by a `Lot.CommonVisitTest` gate (hours, greeted, visit frequency), falls back to
`GoToLot` at NonCriticalNPCBehavior, which has fewer gates. Houseboat lots always use `GoToLot`.

`meet` does not drive `FollowerMeetUpAtPoint`: it sends Sims one by one to the same lot, with no leader and no follower
[verified: no followers in `StormVisit`]. The opt-in kind `follow` (§1.1) does.

**Waves.** With `repeatS > 0` (at least 5 s), a `OneShotFunctionTask` runs a wave every `repeatS` wall-clock seconds until
stop. It skips waves outside Live mode. Each wave writes `route_storm: wave N pushed P (S seen, U unseen), failed F` to
ts3native.log, and start and stop write their own lines.

**Results.** Pushes and failures per kind, the first 40 failures with their reasons, and the Sims used. Each Sim is marked
seen or unseen, by ts3fixes `UfWhy` (0 = unseen) when that is reachable, else by camera distance over 60 m (`seen_by` says
which).

## 1.1 Opt-in kinds for fixes the seven never reach (RouteStormKinds2.cs)

These push EA's own calls on NPCs only, like the seven. The status and wave replies have `pushed`, `failed` and `skipped`
per kind. `skips` / `failures` give the reasons. A skip means a precondition is missing: no fitting Sim, no expansion pack, no
strays, or the other mod is absent. `objects_made`, `objects_removed` and `objects_left` count the cars, guitars and laptops
the storm made. The wave log line adds `skipped K; kinds taxi 3, jam 2, ...`.

| kind | what it pushes [verified in the decompiled source] | fix it exercises | log lines that prove it ran |
|---|---|---|---|
| `taxi` | `CarNpcManager.CreateServiceCar(ServiceType.None)` (a sedan) + `DriveToLotInServiceCar.Definition(car, startOffscreen: false, routeToFrontDoor, getOut, forceCar: true)` at NonCriticalNPCBehavior. The Sim walks to the car, drives and gets out. The target is a far lot from those without valid street parking, or a houseboat lot (`no_parking_lots` in the wave reply); else a far community lot. `DriveToLotInServiceCarWithinTime` -> `InitCarForLotRoute` -> `LotManager.GetNearestLot(pos, null, requireStreetParking: true)`, which EA calls only for such lots | `nearestlotlazy` (docs/677) | `ts3fixes: nearestlot:` (EA's loop >= 50 ms with the switch off; ours >= 20 ms, or shadow `same` / `DIFFERENT`), and `reflect_call Ts3Fixes.Fixes.NearestLotLazyCheck ["result"]` for the session call counts |
| `jam` | `Guitar.Play` (private, its `Singleton` read by reflection), Autonomous, else NonCriticalNPCBehavior, on a guitar in the Sim's inventory. If it has none, the storm makes one with `CreateObjectOutOfWorld("musicalInstrumentGuitar")` + `Inventory.TryToAdd`, as BoardingSchool does. Path: `ReadyToPlay` -> `RouteBeforePlaying` -> `BandInstrument.MoveJigToGoodSpot` -> `GlobalFunctions.FindGoodLocationNearby`. Only Sims on a real lot (the world lot takes `FindGoodLocation`). The first pass alternates indoors Sims (little free space) and outdoors Sims | `[FglFacing]`, `[FglMemo]`, ts3fixes `fglnonescan` (docs/671 §11, docs/675) | `fgl prof: search N ms ...` (ts3native, `[FglProf] Enabled=1`, searches >= `LogMs`, 50 ms), `ts3fixes: fglnonescan:` |
| `laptop` | `Sim.UseLaptopHere` on a laptop in inventory, made the same way if needed (`ComputerLaptop`, as GameDeveloperCareer does). Skipped without University (the jig is EP9) | same staged `FindGoodLocationNearby` | same |
| `putdown` | `PickUpChild` on a toddler of the Sim's household on the same lot. A task waits until the Sim carries it (~60 s), then pushes `PutDownChild` at NonCriticalNPCBehavior. Skipped when no NPC teen or older has a toddler with it | same staged search (`PutDownChild`) | same |
| `follow` | `GoToLot` to a far community lot with `Followers = [an NPC within 30 m on the same lot]` (its own household first). Path: `DoRouteWithFollowers` -> `LeaderGoHereWith` -> `GoHereWithSituation` -> `FollowerMeetUpAtPoint` (a `GoHereWithHoldingInteraction`) on the follower | follower meet-up fixes | the follower's `FollowerMeetUpAtPoint` in `sim_info` / interaction queue; the route lines of both Sims |
| `stray` | `StrayPets.StrayPetHelper` (the hourly alarm's body), at most once per wave, by `MethodInfo.Invoke` (no delegate). Unless `household` is set, `sLastActiveHouseholdVisit` is set to now for the call, which holds off the "active household not visited for too long" branch. It is restored afterwards unless the helper set it itself. Skipped without Pets (EP5), without strays, or when no loaded type has `StrayPetInactiveLotCheck` (the other modder's core, docs/data/mod-method-diff.csv) | the stray-pet lot loop (`ArePointsConnectedByLand` per lot, 322 ms frames) | `route_storm: stray helper called (N strays, Type.StrayPetInactiveLotCheck)`, then the hitch lines of that frame |

Not driven: the City Hall award ceremony. The existing checks are the right vehicle, and a storm kind would add nothing.
`EventCeremonyCheck` books a real ceremony with a real crowd, and `AwardKeepCheck` tests the lost-award path. For
`ceremonyspot`'s older-save repair (commit 58936df3, a ceremony booked before the switch was on), run it by hand:

    ag patches '{"ceremonyspot":false,"awardkeep":false}'        # book it the old way: EA leaves the jig at the origin
    ag reflect_call '{"path":"Ts3Fixes.Fixes.EventCeremonyCheck","args":["start 8"]}'
    ag patches '{"ceremonyspot":true,"awardkeep":true}'          # at once, before the honoree reaches City Hall
    ag reflect_call '{"path":"Ts3Fixes.Fixes.EventCeremonyCheck","args":["result"]}'   # every 10 s until not OPEN

The repair worked when `result` shows `spot 2333=` one higher than before, and the ceremony ends `GIVEN` or `PICKUP`, not
`LOST`. This proves the repair only in a world whose City Hall EA really puts at the origin (combo rabbit holes, e.g.
Appaloosa Plains, docs/551). Elsewhere 2333 stays put, because the jig is not near the origin.

Cleanup: when a push is over, `stop` and each later wave remove the objects the storm made: a car with no occupants, a guitar
or laptop not in use (taken out of the inventory first). On `stop`, objects still in use go to a task that retries for up to
60 s. A Sim's own guitar or laptop is used as it is and never removed.

Run them, e.g. `STORM_KINDS="taxi jam laptop putdown follow stray" STORM_COUNT=24`. Isla Paradiso has the most lots without
street parking (taxi). Pets plus the other modder's core are needed for `stray`.

## 2. Harness

- **phasesoakr.sh phase `storm:M`** (the local run script, not in this repo). It calls `route_storm` with `STORM_COUNT` (40), `STORM_KINDS` (space-separated, default
  all), `STORM_REPEAT` (30 s) and `STORM_SEED` (1). It reads `status` every 30 s and stops (cancelling the pushes) at the
  phase end.
- **`AGENTPKG`** installs a test agent package in place of `$REPO/build/ts3agent.package`. `idle` is unchanged.
- **`tools/harness/storm-table.py [--report route-stutter-report.md] [label=]dir...`** prints one row per phase. The phases
  are split at the harness `mark ev=p-` lines. Each row has:
  - game hitches >= 150 ms (count / ms) and >= 500 ms;
  - slow plans (`ufplan` lines) and those >= 300 ms;
  - plans and fails per minute, from the cumulative `route plans` lines;
  - storm waves and pushes, and `hitch: frame` causes;
  - `host ms`: the sum of the `host N/M` column of the `route candidate split` lines, which is the managed door asks under
    the planner (combo docs/667 §2; `[RouteJobPhases] CandidateSplit` is in PR #681, without it the column stays empty);
  - `skip` (skipped pushes of the opt-in kinds) and `nl/fgl/fgln/s`: the per-phase counts of `ts3fixes: nearestlot:`,
    `fgl prof: search`, `ts3fixes: fglnonescan:` and `route_storm: stray helper called` lines (§1.1);
  - `mism n/f/l`: the NeighbourMemo and FloodMemo shadow mismatches (the largest `mismatch N` of the minute lines), plus the
    count of `MEMO MISMATCH` lines. All three must be 0 before a memo serves. (FloodMemo is combo-only work.)

  With `--report` it also prints the per-hitch cause rows of route-stutter-report.ts by phase. Last of all it prints the last
  fix-counter lines (nopath memo, route speed, height cache, KindsUnseen).

## 3. Runs and results (~/dev/ts3-runs, 2026-11-20)

| run | world | arms | storm |
|---|---|---|---|
| s141 | Isla Paradiso | boat fixes off -> on, one load | 4 + 4 min, kinds boat x2, lot, home, meet, nopath, pets, seed 141 |
| s142 | IP, then Bridgeport | measurement only (the upgraded profiler keys of docs/666 §5) | 5 + 5 min, all kinds, seed 142 |
| s143, s144 | Isla Paradiso | two loads: off, then on (bundle below) | 4 min each, 40 routes every 30 s, all kinds |
| s145 | Isla Paradiso | two loads: off, on (the on load crashed, below) | 2 min each, 80 routes every 10 s |
| s146 | Isla Paradiso | on only | 2 min, 80 routes every 10 s |
| s147 | Isla Paradiso | on without the NeighbourMemo / HeightCache caches | 2 min, 80 routes every 10 s |

The "on" arm sets everything at once, so the gain belongs to the bundle, not to one switch: native `[RouteAsync]
KindsUnseen=1, TaskServe=1, DoorMemo=1, NoPathMemoFuzzy=1, NoPathMemoHousehold=1`, `[RouteSnap] DoorMemoX=3`, `[RouteSpeed]
NeighbourMemo` (2 = shadow in s143, 1 = serve in s144 / s146), `[HeightCache] Enabled=1`, `[RouteSpeed] FloodMemo=2`;
ts3fixes `npmfuzzy npmhousehold stucktaskflag watchdogbusydefer doorgen`. Both arms ran the docs/666 §5 profiler keys and
`NoPathMemo=1`. KindsUnseen, NoPathMemo and FloodMemo are combo-only (not in this tree); TaskServe, DoorMemo, DoorMemoX,
HeightCache and doorgen were already upstream.

| run | arm | game hitches >= 150 ms (n / ms) | door-ask host ms |
|---|---|---|---|
| s143 | off | 11 / 4,167 | 2,621 |
| s143 | on | 3 / 1,034 | 279 |
| s144 | off | 17 / 6,705 | 4,037 |
| s144 | on | 4 / 1,336 | 433 |
| s145 | off | 19 / 7,691 | 1,519 |
| s146 | on | 3 / 1,284 | 233 |
| s147 | on, no caches | 8 / 2,816 | 168 |

NeighbourMemo: s143 shadow 105,102 checks, 0 mismatches; s146 serve 16,761 hits. The fuzzy / household no-path memo
served 0 in every on run.

**Crash, unexplained:** the s145 on load crashed 9 s into the storm (main thread C0000005 at eip 0x15F0, stack refs
0092E0DB 006FD425 0071CAE4): 1 of the 4 full-bundle on loads (s143, s144, s145, s146); s147 (without the caches) also ran
clean. Not reproduced since; cause unknown. It neither clears nor blames any one switch of the bundle.

## 4. What differs from natural play

- The routes are real EA interactions on real NPCs, but far more of them start at once. Treat the storm as a stress test:
  compare arms within one storm design and seed, never against idle hitch counts.
- `nopath` pushes go to points that EA autonomy would not pick. They exist to produce no-path retries.
