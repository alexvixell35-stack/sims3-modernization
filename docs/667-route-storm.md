# 667: route storm, short forced-routing test arms

Date: 2026-11-19. Status: built, first runs queued (s141-s143).

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
[verified against the decompiled callers]. A kind may repeat in `kinds` to weight it. The default is all seven kinds.

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

`FollowerMeetUpAtPoint` was not used. It needs a leader's `GoHereWithSituation` route, so `meet` uses converging lot visits
instead.

**Waves.** With `repeatS > 0` (at least 5 s), a `OneShotFunctionTask` runs a wave every `repeatS` wall-clock seconds until
stop. It skips waves outside Live mode. Each wave writes `route_storm: wave N pushed P (S seen, U unseen), failed F` to
ts3native.log, and start and stop write their own lines.

**Results.** Pushes and failures per kind, the first 40 failures with their reasons, and the Sims used. Each Sim is marked
seen or unseen, by ts3fixes `UfWhy` (0 = unseen) when that is reachable, else by camera distance over 60 m (`seen_by` says
which).

## 2. Harness

- **phasesoakr.sh phase `storm:M`.** It calls `route_storm` with `STORM_COUNT` (40), `STORM_KINDS` (space-separated, default
  all), `STORM_REPEAT` (30 s) and `STORM_SEED` (1). It reads `status` every 30 s and stops (cancelling the pushes) at the
  phase end.
- **`AGENTPKG`** installs a test agent package in place of `$REPO/build/ts3agent.package`. `idle` is unchanged.
- **`tools/harness/storm-table.py [--report route-stutter-report.md] [label=]dir...`** prints one row per phase. The phases
  are split at the harness `mark ev=p-` lines. Each row has:
  - game hitches >= 150 ms (count / ms) and >= 500 ms;
  - slow plans (`ufplan` lines) and those >= 300 ms;
  - plans and fails per minute, from the cumulative `route plans` lines;
  - storm waves and pushes, and `hitch: frame` causes.

  With `--report` it also prints the per-hitch cause rows of route-stutter-report.ts by phase. Last of all it prints the last
  fix-counter lines (nopath memo, route speed, height cache, KindsUnseen).

## 3. Queued tests (~/dev/ts3-runs)

| run | world | arms | storm |
|---|---|---|---|
| s141 | Isla Paradiso | boat fixes off (EARLYFIX `:false`) -> `fixon` -> on, one load | 4 + 4 min, kinds boat x2, lot, home, meet, nopath, pets, seed 141 |
| s142 | IP, then Bridgeport | measurement only, CI asi 44634118056 with the upgraded profiler keys, perf | 5 + 5 min, all kinds, seed 142 |
| s143 | Isla Paradiso | two loads, A all off, B on: KindsUnseen=1, NeighbourMemo=2 (shadow), HeightCache on, NoPathMemoFuzzy + Household (npmfuzzy, npmhousehold), stucktaskflag, watchdogbusydefer | 4 min each, all kinds, seed 143 |

s143 needs two loads because KindsUnseen and `[HeightCache]` have no runtime switch (docs/662 §4, docs/663 §3). Both arms keep
`NoPathMemo=1`, which the fuzzy and household memo needs (docs/643 §7.5).

Each script backs up the Bin pair and restores it with a sha check (`./sNNN-run.sh restore`). Each writes `sNNN.result`
with its phase dirs, `sNNN-report.md` and `sNNN-table.txt`.

## 4. What differs from natural play

- The routes are real EA interactions on real NPCs, but far more of them start at once. Treat the storm as a stress test:
  compare arms within one storm design and seed, never against idle hitch counts.
- `nopath` pushes go to points that EA autonomy would not pick. They exist to produce no-path retries.
