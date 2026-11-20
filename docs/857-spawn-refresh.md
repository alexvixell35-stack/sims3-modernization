# 857. `spawnfix` and `spawnoffworld`: a spawn refreshes one spawner, and spawners out of the world stop spawning (2026-11-14, contributor, spawnfix ON, spawnoffworld off since the 2026-11-20 import)

Code: `mods/ts3fixes/src/SpawnFix.cs`, patches in `Fixes.cs` (`FireEvent`, `Add/RemoveObjectFromSpawner`, `ImportContent`, `TrySpawnObject`, `SpawnerBase.OnStartup/Dispose`, `FishingSpotData.Activate/DeactivateSpot`), tunables `kSpawnFix`, `kSpawnOffWorld` (False), switches `spawnfix`, `spawnoffworld`. Counters 1328-1344 (docs/registry.md). Tags: [verified] = decompiled scripts read locally (not committed) or logs; [inferred] = reasoning.

## 1. Problem [verified, decompiled scripts]

Every spawn, every removal of a spawned object that had a helper effect, every spawner at save load and every fishing spot (de)activation calls `CollectionHelper.Refresh()`. That fires `SetHelperSearchType`, which every `SpawnerBase` subscribes to in `OnStartup`: one spawn = one handler call per spawner in the world, a world load = N x N. A handler removes the spawner's map tag from the active Sim, re-adds it when a helper is selected (new map tag, two map UI events, a new visual effect per spawned object) and may walk the spawner's object set.

## 2. `spawnfix`

`CollectionHelperSingleton.FireEvent` is the one door every refresh goes through. With the switch on:

1. Skip: the last fan-out had search type "none" for the same active Sim and this one is "none" again. Every handler would only stop a spawner that started nothing; `LotIds` is cleared as EA does.
2. Single: the call came from `Add/RemoveObjectFromSpawner`, `ImportContent` or a fishing spot (de)activation, with the same search type and active Sim as the last fan-out: only that spawner's handler runs. `LotIds` is not cleared (only `TreasureSpawnPoint` uses it, and it is not touched). The spawner must have run `OnStartup`.
3. Anything else (type or Sim change, `SetMode`, `TearOfHorus`, helper removed, a spawner started since the last fan-out, ten sim minutes since it) runs EA's full fan-out, which also re-syncs the map tags after a Sim switch.

Any exception in the fast path falls back to EA's full fan-out. No Sim is moved, faded or changed; the only Sim-related input is the active Sim as a cache key, and a change of it always fans out.

## 3. `spawnoffworld` (separate, changes gameplay)

`GameObject.RemoveFromWorld` clears the InWorld flag only; the spawn alarm stays and `UpdateSpawnAlarm` tests only the lot, so a spawner taken out of the world (seasonal or festival swap) keeps creating objects around its old position. With the switch on, `TrySpawnObject` does nothing for such a spawner; the alarm re-arms as EA's `UpdateSpawnAlarm` does and it spawns again once back in the world. Fewer objects appear, so this is its own switch.

## 4. Evidence

- `spawnfix` fires [measured, ts3native / ts3fixes counters of about 110 local games with it on]: 185-293 fan-outs skipped and 37k-83k handler calls avoided per game. Its time effect is not measured (the ms counters 1338 / 1342 / 1343 exist for that).
- `spawnoffworld`: never turned on.
- Crashes: none tied to either.

Deciding test (not run): Isla Paradiso, one game, in-load A/B of `spawnfix`: ms in full fan-outs (1338) and hitch frames in spawn bursts; then a smoke game with `spawnoffworld` on (counters 1339 / 1341 fire, no errors).

## 5. Not fixed

`UpdateSpawnAlarm` walks the lot's Sims for the Gatherer trait on every spawn and removal (a cache would change timing); `TryPlaceSpawnedObjectInWorld` tries `kMaxSpawnAttempts + 1` find-good-location calls per spawn (RNG stream and result would change); `CauseEffectSpawner.ClearSpawnedObjects` destroys while enumerating its object set.
