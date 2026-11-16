# 518: Async PlanFromPoint for unseen NPCs' routable checks (`routableasync`)

2026-11-15/16. This is an offload, not a speedup: one PlanFromPoint per routable check moves off the sim thread, for unseen
NPCs only, with the same IC and the same verdict. Default off. It was first written as §3 of a local doc 584 (unseen
visit trips), so the tick and run numbers come from those runs.

## 1. The 692 ms PlanFromPoint on the sim thread (tick 20979)


`ufplan: kind 2 692 ms why -1` with no Sim name, `ufplansplit ... no wait, result Succeeded, dist 58`. `why -1` with no
name means `UfPlanBody` returned before scoping: our own checks, EA's `IsPointInLotSafelyRoutable`, the stuck check, a
pet or a non-Sim follower. A succeeded PlanFromPoint over ~58 m fits `IsPointInLotSafelyRoutable` (a PlanFromPoint
from the spot to the lot's 4 corners and 4 edge midpoints) [inferred]; its callers are `AttemptToFindSafeLocation`
(every reset), `FindRoutablePointInsideNearFrontDoor`, `CreateTeleportInstanceToPosition` and our `SpotVerdict`.

Why sync: `AsyncKindStart(2)` sends an own START only for the route's own Sim task, and ts3native refuses own kind-2
STARTs with `[RouteAsync] Kinds=0` unless they carry the watchdog tag (`WatchdogKind2`, A1 only); foreign STARTs need
`ForeignKinds` bit 2, and release has `ForeignKinds=4` (ReplanFromPosition). Both are ts3native config, already
hooked (`hooked mask 6`); turning on `Kinds=2` (or `ForeignKinds=6`) is an A/B, not code. Plans from a context that
cannot yield (a reset run outside the Sim's task, alarms) stay sync whatever the config.

Callers [verified, decompiled; 26 call sites]: `Sim.AttemptToFindSafeLocation` (every reset and `AttemptToPutInSafeLocation`),
`Sim.FindRoutablePointInsideNearFrontDoor` (send-home, NRaas instantiation), `Sim.CreateTeleportInstanceToPosition`,
`Service.CreateSim` / `GetPositionNearLotCorners`, `ServiceSituation.IsGoodSpawnPoint`, `PerformerBase.GetPositionForShow`,
Pregnancy, Boat, Stroller, teleporters and others, plus ours through `SpotVerdict` (SafeSpot for the trip shortcuts, the
watchdog). The plan is always kind 2 (`Route.PlanFromPoint` of a throwaway adult route whose follower is the Sim). Which
caller produced tick 20979 is not in that log. Scale: 411 local test runs hold 19,274 slow unscoped kind-2 plans (median 239 ms, max 3,291 ms, ~4,960 s in all).

**`routableasync` (default off, for an A/B with `[RouteAsync] Kinds=2`).** `AsyncKindStart(2)` sends a START only from
inside `IsPointInLotSafelyRoutable` (`sInRoutableCheck`, now kept per task so a check sleeping on its job never tags
another task's plans) for an unseen NPC (`UfWhy` 0: never household, selected, unknown scope or pets) and outside the
watchdog's own A1 path; every other PlanFromPoint is refused locally and stays sync, as with `Kinds=0`. The job takes the
pre-plan callback (doors, tombs, closed venues) as A1 does. Why it is safe for those callers: the job runs the game's
own PlanFromPoint IC on the worker (the same IC and handshake as the watchdog's A1 jobs, `WatchdogKind2`, which have a
sync shadow compare arm, `watchdogasyncshadow`); the route is a throwaway (no follower moves, only its result is read); the Sim's own task sleeps until READY
only where `CheckYieldingContext` allows a yield, else the check stays sync; with `KdOwnPlanFromPointMs=0` no job is cut,
so the verdict is the planner's own, one or more frames later. A cut (non-zero deadline) would answer "not routable"
for an unseen NPC only, and SpotVerdict would cache that cell for a sim-day, so the A/B keeps it at 0.
Counters 2736 let through, 2737 refused here, 2738 let through but not started.

A/B lines (in-load ABBA, `--ab asyncreplan` toggles arMode: shadow = sync, 1 = async):
```
[RouteAsync]
Kinds=2
KindsMode=1
KdOwnPlanFromPointMs=0
```
EARLYFIX `"routableasync":true` (both arms). Read: ufplan / planwait lines,
hitchwatch, 2736-2738; correctness: `watchdogasyncshadow` style mismatches are not wired for this path, so compare the
unseen-NPC reset placements (moved lines) between arms. `ForeignKinds=6` (adds PlanFromPoint from other yielding tasks)
is a second step: none of the callers above plans from another Sim's task except service spawns, which run in
alarm / situation code that mostly cannot yield [inferred].

Change (log only): `ufplan` / `ufplansplit` lines of unscoped plans now carry `src <kind> <Sim> [in <task Sim>'s task]`
(`routable`, `spot`, `watchdog`, `stuckcheck`, `leg`, `pet` or the follower's type), and since 2f6ce3e4 the refused
START's sync plan is timed (`sync N ms (START refused)` instead of `no wait`). The next `ufplansplit` run names the caller.

## 2. In-game result: one short run (Bridgeport, 2 x 6 min, routableasync only)

The engine was the same as the s66 baseline, with `[RouteAsync] Kinds=2` and EARLYFIX `"routableasync":true`.

| | s66 baseline (Kinds=0) | routableasync |
|---|---|---|
| kind-2 (routable check) plan average | 228 ms | 238 ms |
| kind-0 plan average | 232 ms | 235 ms |
| kind-2 jobs run async | 0 | 48 |
| game hitches | | 1 (368 ms) |
| unseen-scorecard | | PASS 6/6: recovery 85/90 = 94.4%, 0 household cuts, shortcuts ended 0 |

- [measured, one run] There was no measurable speed change either way. This is the expected result for an offload: the
  plans run as long as before, but on the worker. The run shows the path is stable; it does not show any gain.
- [not measured] Counters 2736-2738 were not dumped in this run, so the let-through / refused / not-started split is
  unknown. The 48 async jobs come from the ts3native kind-2 job count.
- [inferred] The verdicts match the sync path: the same IC runs, with the pre-plan callback, and no deadline (`KdOwnPlanFromPointMs=0`).
  No sync shadow compare is wired for this path.
