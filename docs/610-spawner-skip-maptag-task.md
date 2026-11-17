# 610: Spawner placement skip and map tag task restart

2026-11-17, static, r67; both fixes proven in game by their force checks (§5). Two default-off ts3fixes switches in `mods/ts3fixes/src/SpawnerMapTag79.cs`, plus a
tool-only scan of the other targets of this pass (§4). RE with tools only: `ilspycmd` (C# and `-il`) on `game-copy/assemblies/*.dll`,
`node tools/managed-callgraph.ts callers`, the Roslyn walker (`tools/static/hot-logic-walker --files`, 209 files: every
`PlaceAtGoodLocation` / `FindGoodLocation*` caller plus the elevator, stair, ladder and portal component files), `ast-grep`
(loops around a good-location search) and semgrep (`ea-gameplay-logic`, `ea-loop-logic`).

Household rule: spawners and map tags have no Sim in them. Both fixes end in a state EA's own code produces, so there is no
household exemption ("household included").

| switch | slots | hook (swap) | Check / repro |
|---|---|---|---|
| `spawnerskip` | 3424-3429 | `ObjectSpawner.TryPlaceSpawnedObjectInWorld(ref IGameObject, bool, FindGoodLocationBooleans)` and `GlobalFunctions.PlaceAtGoodLocation(IGameObject, FindGoodLocationParams, bool)` (SpkPlaceHook) | `SpawnerSkipCheck`, `spawnerskip-check` |
| `maptagtaskalive` | 3430-3434 | `MapTagController+MapTagVisibilityUpdate.Simulate` (MtkVisHook), `MapTagController.Simulate` (MtkCtlHook) | `MapTagTaskAliveCheck`, `maptagtaskalive-check` |

Tunables: `kSpawnerSkipFails` (3), `kSpawnerSkipRetry` (8).

## 1. Data [verified, ts3native.log]

`spawnplace:` lines (alarmtime, kHitchCauseHooks, placements >= 10 ms) in the user's `phase-2026111[67]*` runs: 536
placements; 459 found a spot on the first search, 70 failed all 11 searches, 7 needed 2-10. All 70 failures were
`deleteObjOnFailure` true (object destroyed). Failing spawners repeat: InsectSpawner20 24 fails / 9 successes,
InsectSpawner36 19 / 49, InsectSpawner15 11 / 29, InsectSpawner18 9 / 18. A failing placement costs 11-70 ms on the main
thread. Outcomes are bimodal (first try or never), which points at a lasting cause in the spawner's surroundings
(`IsValidSpawnedObjectPosition`: inside `InvalidSpawnDiameter` and `World.IsPositionOutside == mPlacedOutside`) [inferred].

## 2. spawnerskip [verified, ilspycmd ObjectSpawner]

`TryPlaceSpawnedObjectInWorld` adds the object to the world, sets a random facing, then loops `kMaxSpawnAttempts` (10)
down to -1: 11 random start points in the spawn square (snapped to the floor unless a water cause-and-effect spawner),
each with a full `PlaceAtGoodLocation`. When all fail: destroy (`deleteObjOnFailure`) or a random facing and the last
start point. Callers: every `SpawnObject` (insects, rocks/gems, seeds, wildflowers, minor pets, sea life, shells, dig
sites, junkyard, crystal plants, cause-and-effect) and `InsectSpawner.ForceSpawnInsects`. `InsectSpawner.SpawnObject` places
up to three object classes (minor pet, wildflower, insect jig) with different placement checks.

Fix: a streak per spawner, object class, constraints and failure mode. After `kSpawnerSkipFails` full searches fail in a
row, a spawn takes EA's failure ending directly with the same calls (`AddToWorld`, then `Destroy`; or one random start
point in the square, `World.SnapToFloor`, random facing, `SetPosition`). Every `kSpawnerSkipRetry`-th spawn runs the full
search again; a found spot clears the streak; so does any change of the spawner position, its lot, the lot's
`TimeOfLastBuildBuy` (set by `LotManager.ExitBuildBuyMode`) or `mPlacedOutside`. `ICauseEffectSpawner` always runs EA's body
(its zero-diameter branch tests one spot, no search). The end state of a skipped spawn is EA's failure end state; a skipped
spawn whose search would have found a spot (a surroundings change the key does not see, e.g. an object removed by a Sim)
ends as a failure, until the next retry. With the defaults a stuck spawner runs 11 searches on 1 spawn in 8 instead of
every spawn.

The hook replaces HitchCause's `HcSpawnHook` (same two swaps; the `spawnplace:` line is unchanged and still written only
with kHitchCauseHooks + alarmtime). Counters: [3424] spawns given EA's failure ending without the search, [3425] full
searches while armed, [3426] streaks armed, [3427] errors, [3428] `PlaceAtGoodLocation` searches by spawner placement,
[3429] spawns placed by a search ("count searches and placements": 3428 / 3429 per arm).

Check: 40 spawns of an always-failing and an always-succeeding spawner through the real decision code, off vs on (EA: 440
searches, 0 placed; on: far fewer, still 0 placed); controls: succeeding spawner identical, cause-and-effect and changed
surroundings never skip, retry 1 always searches; live: the swaps and the `SpawnDiameter` getter on a real spawner.

Hooked body re-read: the loop also recomputes `TombRoomManager.IsObjectInATombOrHiddenRoom(this)` and the room id of the
fixed spawner position on every attempt (invariant); left in EA's body (the fix does not rewrite it).

## 3. maptagtaskalive [verified, ilspycmd -il UI.dll]

`MapTagVisibilityUpdate.Simulate` walks the current tags 5 per tick. After an in-pass `Simulator.Sleep(0)`, when
`mbForceUpdate` got set (`OnMapTagAdded`, `OnMapTagRemoved`, `OnMapTagRefresh`) or `mCurrentMapTags` became null, it does
`leave IL_029a`, the method's `ret`. The loop top leaves the same way on `!mbActive` (`CASModeState` sets `MapTagsActive`
false, then true), a null model or a null list (the controller fills the list in its own Simulate). The task is created
once, in the `MapTagController` ctor (`LiveModeState.LoadUI`), so map tag show/hide (`ShowTag` has no other caller),
culling, the active-Sim tag auto-hide and the collision runs stop until the next UI load. `MapTagController.Simulate` leaves
on `!mbActive` or a null model the same way (no more `UpdateMapTagCamera` / `UpdateMapTags`). How often each exit fires in
play (whether the Simulator runs these tasks during CAS) is [inferred], not measured.

Fix: when EA's body returns with one of its exit conditions still true (nothing runs between EA's check and its `ret`) and
the controller is still the live one (`Dispose` clears `Instance` and destroys the task), sleep one tick and run EA's body
again. A `ResetException` (Dispose) ends it as before; a return with no exit condition true is EA's reset catch and ends it
too. While the controller is inactive the hook re-enters EA's body once per tick (counted each time). A task that EA ended
before the switch went on comes back at the next UI load; the Check's on-arm `run` restarts it for the live controller.
Counters: [3430] visibility restarts, [3431] visibility task ends with the switch off, [3432] controller restarts, [3433]
controller ends with the switch off, [3434] errors.

Check: a model of the pass (12 tags, 5 per tick) with a forced update mid-pass and an inactive-then-active toggle: off,
the task ends; on, it keeps running and does more passes; controls: no event gives the same passes either way, a dead
controller never restarts. Live: reports whether the visibility task runs, sets `mbForceUpdate` as a tag add does; `result`
shows whether the task still runs and a pass ran since.

## 4. Other targets (tool scan, nothing built)

- `PlaceAtGoodLocation` / `FindGoodLocation` callers (ast-grep, about 50 loops): `Sim` storyteller faint (`FindGoodLocationNearby`
  from the same position up to 3 times when the route fails: identical search repeated), debug spawners (5 identical
  searches), `SpiritMaker.MoveToNewLocation` (20 random points, fine), `Boat.GetAlternativeLandPositionForSim` (2 tries, then
  returns false with the water position as out value: callers decide). No player-visible drop of Sims found.
- Elevators: covered by docs/582 B4 (`elevatorkitten`, `elevatorresetriders`, `elevatorridertimeout`, `elevatorsavedlog`);
  semgrep `sleep-wait-no-exit` on `ElevatorDoors.Open/CloseElevatorDoors` (`while (mIsAnimating) Sleep`): the flag is
  cleared in `finally`, so a reset of the animating task releases the waiter. Lead only: a reset of a *waiting* task clears
  the flag while another task still animates.
- Stairs: `StairsPortalComponent.OnPortalStart` sleeps until `HandleStairsCompleted` (native callback) or `WakeAll` (load);
  no deadline (semgrep `sleep-wait-no-exit`). No stuck-on-stairs case in the logs or `docs/data/stuck-log.md`: lead only.
  `TubeElevator` climb loop ends only on exit reason or reaching the level (semgrep `retry-no-cap`): lead only.
- Distance: no straight-line "closest" pick in these files that the walker flagged (REPEATCALL hits are re-queries in
  wait loops, intended).

## 5. In-game result

Force checks, one load, package r67 (`phase-20261117-0127`): both switches off BROKEN, on FIXED, controls ok, members
found, error counters 0.
- `spawnerskip`: a failing spawner (SeedSpawnerRare3) runs 440 searches for 40 spawns with EA's body and 77 with the switch
  on, placed 0 both ways; a succeeding spawner is unchanged (40 searches, 40 placed). Live: 31 / 33 spawner searches, all
  placed by search; no streak armed in that load.
- `maptagtaskalive`: forced update and inactive toggle keep the task (passes 10/10 on, EA 0/2); no event: 10/10 both ways.
  Live: the visibility task ended 13 times with the switch off (3431) during the load.
