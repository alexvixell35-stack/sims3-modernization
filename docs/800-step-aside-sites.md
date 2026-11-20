# 800: more step-aside sites for JobYield (jobyieldroad, jobyieldroadow, jobyieldspawn, jobyieldready, jobyieldfp)

Answer: `jobyield` (docs/183) makes a task sleep a tick at a time instead of letting the sim thread wait for a running async route job. It covers the plan sites in `RouteBackoff.cs`. Five more places run a plan, or flush the footprint queue, on the sim thread while a job runs. Each has a default-off switch that adds the same step-aside there. All five need `jobyield` and a ts3native with `[RouteAsync] JobYield`; without it the game answers "not busy" and nothing changes.

## 1. What waits, where [verified: native script stacks of the sim-thread waits, Bridgeport apartment save, 12-minute games]

| Switch | Site | Waits for the job at | Whom it touches |
|---|---|---|---|
| `jobyieldroad` | `World.FindPlaceOnRoadOffScreen` (traffic spawn, NPC car driving off, service car start) | the plan inside the IC | NPC cars (`CarNpc`); a car with an occupant that is not a townie, and food / ice cream trucks, are left out |
| `jobyieldroadow` | before `CreateObjectOutOfWorld("CarTaxiLightweight")` on the traffic task; no sleep for a `CarNpcWithNoDriver` out of the world | (fixes a car loss, see 2) | NPC cars |
| `jobyieldspawn` | start of `ObjectSpawner.TryPlaceSpawnedObjectInWorld` | the first `SetPosition` flush of the new object's footprint record | spawner objects (no Sims) |
| `jobyieldready` | `AsyncWait` READY polls | READY's forced DiscDrain | any plan that already goes through the async handshake; timing only (see Risks) |
| `jobyieldfp` | top-level `Terrain.CanTurnInPlace` / `ModifyRouteForSpecies` | test jig and footprint switches | townies only (`IsNpcSim` and a household); household, selected and unknown-scope Sims take no step-aside |

## 2. Why `jobyieldroad` needs `jobyieldroadow`

`CarNpcWithNoDriver.OnStartup`, run inside `CreateObjectOutOfWorld`, adds a LiteDriver task. Its `Simulate` starts with `if (!mCar.InWorld) { mCar.Destroy(); return; }`. EA never yields between `CreateObjectOutOfWorld` and `PlaceAt`, so the task finds the car placed. A sleep before the road IC lets the task run first and destroy the out-of-world car; EA then goes on with the dead car (the traffic class count is raised after the Dispose and never taken back, so the ambient traffic cap falls). In the local games with `jobyieldroad` alone 294 of 297 slept cars were gone after the sleep. `jobyieldroadow` moves the traffic task's step-aside before the car exists, skips the sleep for such a car, and answers "no place" for any other car that is gone after a sleep (each caller's own failure path handles it). Use the two together or not at all.

## 3. Evidence [verified: ts3native.log, natural-play games, one machine; not in-load A/Bs]

Bridgeport apartment save, runs fs1 (none of the five on) against ji1 / ji2 / jj1 (all five on, with the final `jobyieldroadow`; each also had a household variant of `jobyieldfp` on, not in this PR, and other local switches such as the outfit and trip shortcuts):

- Sim-thread waits for the job at the plan site ("runplan", from the native end-of-game line): 30,400 ms in 1,900 waits in fs1 against 453 ms in 40 waits (ji1), 778 ms in 73 (ji2), 601 ms in 76 (jj1).
- The other wait kinds rose: "rtwait" 7.9 s in fs1 against 10.2 / 10.7 / 11.4 s, "disc" 2.5 s against 3.2 / 3.3 / 3.7 s. So the sim thread did not stop waiting; the wait moved from the plan site to the others. No frame-time comparison is claimed here.
- An earlier version of the road step-aside without `jobyieldroadow` lost cars: 294 of 297 slept cars were gone after the sleep (games jg1 / jg2, from the run notes; the counter dumps kept in the logs are partial and were not re-read for this PR). That is why the two ship together.
- Not measured: an on / off in-load A/B of any of the five, the effect on traffic density, and the Sunset Valley, Isla Paradiso and Twinbrook numbers of the earlier notes (counted from other runs, not re-read).

## 4. Risks

- No conflict, no guard [verified, read]: JOBBUSY returns in the GetOption2 hook (ts3native.cpp `JyBusy`, before the READY path that calls `ReadyDrain`), and both serve calls only on the owner thread through `ServeInvoke` (`WaitForSingleObject(g_invReady, 0)` makes a call served once); `ReadyDrain` returns at once unless the job is `J_RUNNING`. So the two cannot double-drain or race: `jobyieldready` just starves `ReadyDrain` of polls while a job runs.
- With ReadyDrain on (PR #468) `jobyieldready` skips the READY polls that ReadyDrain drains in. In run gm7 (Bridgeport apartment save, 2026-11-16) ReadyDrain saw only 6 polls with `jobyieldready` on. His 2026-11-19 17-job soak (Apex + bridge, all PASS, ReadyDrain on, all five switches on) checked the pair: `jobyieldready` skipped 228,640 READY polls, no problems; both are ON.
- `jobyieldready` has no Sim scope (same as `jobyield`'s own `StepAside`): the skipped poll is timing only, the same drain runs, but a household Sim's plan goes through it too.
- The sleeps hold the asking task a few ticks longer (capped at 10 s by `StepAside`); a task reset passes through as in EA's own waits.
