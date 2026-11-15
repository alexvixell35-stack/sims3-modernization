# 584: Unseen visit trips, Sims stuck upstairs, the 692 ms PlanFromPoint, last search in the census

2026-11-15, from run `phase-20261115-2043` (s55, Bridgeport). Static, built, **not run**. All new switches default off.

## 1. Unseen NPCs crossing town on foot (`visitcar`, `visitwalkfinish`)

**Root [verified, decompiled].** `VisitCommunityLot.Run` routes to the lot's front door first (`Lot.RouteToFrontDoor`, a
plain `CreateRoute()`, car allowed). With no front door (parks, plazas) or when that route fails, it creates the lot
route and, for an autonomous visit, draws `RandomUtil.RandomChance(Lot.ChanceOfGoingToCommunityLotByCar)`
(`Lot.kChanceOfGoingToCommunityLotByCar = 50`); on false it turns `EnablePlanningAsCar` off. Half of those visits walk
however far the lot is. In run 2043 unseen walkers covered ~1 m per sim-minute for hours (one ~800 m in 13 sim-hours).
The same flip is in `GoToCommunityLot`, `VisitLotWithDate` and `RabbitHole.RouteNearEntranceAndEnterRabbitHole`; only
the visit is changed.

**EA's distance.** `Car.GetWorldDrivingMinimumDistance()` (`kDistanceToDestinationSoSimWillDrive` 30 m, with Egypt,
China, France and downtown variants, all 30), which `Car` also hands to the planner as
`Route.SetMinimumDistanceForCarTravel`. `visitcar` uses it as the threshold.

**`visitcar`.** Hook on `Lot.get_ChanceOfGoingToCommunityLotByCar` (pass-through) plus `VisitCommunityLotHook.RunHook`:
an autonomous visit by an NPC that `TownieUnseen` calls unseen at Run start (never household, selected or unknown
scope; standing, not riding), with the lot farther than EA's driving distance, reads 100 once in that Run's task. That
is one of the two outcomes EA's own flip gives, so the result is a vanilla outcome; the planner still decides whether a
car is used. Counters 2722 judged, 2723 kept driving, 2724 left to EA (household, selected, seen at start).

**`visitwalkfinish` (backstop for walks already underway).** Every 10 sim-minutes (alarm started by the first visit
Run with the switch on, NeverPersisted), an NPC whose visit Run started 60+ sim-minutes ago and is still routing to the
lot, unseen, with the lot out of every viewer's reach, gets EA's route interrupt
(`SimRoutingComponent.RequestRouteInterruptAction`, soft stop, as `SimScriptAdaptor`'s toddler walk-fall). The callback
runs in the Sim's task, checks again, places the Sim on a verified safe spot of the lot (`SafeSpot`, as the visit
shortcut) and ends the route (`ActionResult.Terminate`). Run then plans again from the lot (front-door branch) or
returns false; in that case the hook runs Run's own arrival step (`MakeSimDoSomethingAfterArrivingAtVenue` when the
visit looks for actions on arrival, the grouping situation) and returns true. Refused at the callback: the walk goes on
(the interrupt re-queues a clone of the follow action, EA's resume). Exemptions are the visit shortcut's
(`ShortcutToLot`):
- EP10 resort lots: Run sends resort visitors through the resort tower / check-in path; diving lots need the boat or
  mermaid water route (`EnablePlanningAsBoat`). A placement would skip that.
- Households with pets, and any Sim in a grouping situation: `GetFollowers` may bring pets, household members or a
  horse (`NpcShouldRideHorse`); moving the leader would leave them across town.
- Moving houseboats, apartment shells, the active household's lot, world lots.

Counters 2725 interrupts asked, 2726 walks finished, 2727 refused at the callback, 2728 errors. Open [inferred]: what
`DoRoute` reports for a terminated route (a route-fail count on an unseen Sim at most); check in the first run.

## 2. Tom Wordy upstairs, Lenny Shutter in Founders Keep room 28

`hiddenroomnpc` (HiddenRoom.cs `HrPass`) has **no floor filter**: it judges any NPC standing in a room that
`Lot.IsRoomHidden` marks, on any level, with EA's allowed-room flood (`HrReachesOutside`). It did not act on Tom because
he was never in a hidden room: every census row of run 2043 has him in room 40 (or 28 in the one `routecause` line,
level 1 of his own Crib) with `hidden_room false` and `trapped false` (the same flood: an allowed way out exists). His
failing plan was `DisposeOutside` to an outdoor trash can (`NoPathToDestination`, holding a TrashPile), after which he
stood idle; the stuck watchdog's idle check (EA's `IsPointInLotSafelyRoutable`, lot edges) did not reset him, so the
planner could reach the lot edge from his spot [inferred from no reset 46 line]. That is the idle question (§4), not a
closed room; widening the hidden-room rescue would not have moved him.

The floor-0 limits are in the harness side only: `HiddenRoomCheck`'s scan lists floor-0 rooms and its repro lot is the
first lot with a floor-0 marker; its trap picks a floor-0 marker first (HiddenRoomChecks.cs). Lenny's upstream FAIL (`bp-unseen-tp-goal-rerun`)
read `UfTpCleanup`, which only moves NPCs our own teleport placed there, 1 s after the trap; `hiddenroomnpc` needs two
10-minute passes (or a failed plan). No rescue change was made; the check strings stay as they are.

## 3. The 692 ms PlanFromPoint on the sim thread (tick 20979)

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
caller produced tick 20979 is not in that log; the `src` label below names it in the next run. Scale: 411 runs in
ts3-runs hold 19,274 slow unscoped kind-2 plans (median 239 ms, max 3,291 ms, ~4,960 s in all).

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
EARLYFIX `"routableasync":true` and `"ufplansplit":true` (both arms). Read: ufplan / planwait lines with `src routable`,
hitchwatch, 2736-2738; correctness: `watchdogasyncshadow` style mismatches are not wired for this path, so compare the
unseen-NPC reset placements (moved lines) between arms. `ForeignKinds=6` (adds PlanFromPoint from other yielding tasks)
is a second step: none of the callers above plans from another Sim's task except service spawns, which run in
alarm / situation code that mostly cannot yield [inferred].

Change (log only): `ufplan` / `ufplansplit` lines of unscoped plans now carry `src <kind> <Sim> [in <task Sim>'s task]`
(`routable`, `spot`, `watchdog`, `stuckcheck`, `leg`, `pet` or the follower's type), and since 2f6ce3e4 the refused
START's sync plan is timed (`sync N ms (START refused)` instead of `no wait`). The next `ufplansplit` run names the caller.

## 4. Last search in the census (`autlastsearch`, log only)

`AutPushHook` (AutLive.cs, on `Autonomy.ConsiderPerformingAutonomousAction`, installed in kAutLive packages: default
on) records each Sim's last search that reached the push decision: `pushed <definition>`, `kept <head> over <pick>`, or
`none` (nothing scored). ts3agent's census adds `last_search` and `last_search_min` per Sim (null when the switch is off).
An idle Sim with `none` a few minutes ago is an empty search; with a stale or missing entry, autonomy is not running for it.

## 5. Per-update list copies (GC leads from docs/583 §7.2), ranked by call rate; `motivebuf`

Call rates from the code paths [verified, decompiled] and run 2043's census (123 Sims in the world on average, 6 in the
household); rates are upper bounds where unseenthrottle stretches unseen Sims' updates.

| Rank | Site | Called | Calls / sim-hour (Bridgeport, 123 Sims) | Allocations per call |
|---:|---|---|---:|---|
| 1 | `Motives.UpdateMotives` | `Autonomy.Update`, every Sim, once >= 1 sim-minute has passed (the active Sim every update) | ~7,400+ | List + array (+ ValueCollection on Mono 2.x) |
| 1 | `BuffManager.UpdateBuffs` | same call, right after | ~7,400+ | List + array (copy of `mDelayedBuffs`, almost always empty); a second List only when a buff times out |
| 3 | `ThoughtBalloonManager.Update` | same call, only while `NeedsToUpdate` (a balloon showing: seen Sims) | small | ArrayList |
| 4 | `DreamsAndPromisesManager.Update` | household Sims only (6), when set to update | small | Dictionary + up to 5 Lists |
| 5 | `RoleManagerTask.Simulate`, `TrafficManager.Simulate`, `SeasonsManager.Update` | once per their task's tick, world-wide | per tick, not per Sim | list of lists / lot list + table / float[2] |

`motivebuf` (default off, MotiveBuf.cs) takes rank 1a: a swap of `UpdateMotives` that snapshots into a pooled list (taken
per call, so a Sim sleeping mid-loop keeps its own) instead of a new one, with EA's timer, sleep, destroyed-Sim return and
`MotiveChanged` unchanged. Proven-exact, so it covers household Sims too (CLAUDE.md: exact speedups stay); identical
results are the point of `motivebufcheck` (pure check: every snapshot compared with EA's copy by count and reference,
mismatches counted in 2731 and must be 0). Allocations saved per sim-hour = 2 x the rise of counter 2729 (at least).

Rank 1b (`UpdateBuffs`): ts3native IL patch `ilp_buffbuf` (default off, needs `[IlPatch] Enabled=1`; spec
`mods/ilpatch/specs/buffbuf-delayed-copy.json`, EA 1.67 body only; other builds fail the hash and run EA's body). Inserted
at the top: `if (!mDelayedBuffs.GetEnumerator().MoveNext()) goto 0x89` (the code after the first loop). With an empty
list, EA copies it into a new List and walks nothing, so skipping is exact; with buffs pending, EA's body runs unchanged.
`ListSorted` has no `Count` field reference in the module (an IL patch can only use existing tokens), so the test
enumerates: that box stays, the copy's List (and its array on this Mono [inferred]) goes. `buffbufcheck` (ts3fixes,
off) runs right before each `UpdateBuffs` from `Autonomy.Update` (after the motive hook): calls (2733), empty lists = the
skips (2734), and `ListSorted.Count` vs enumeration (2735, must be 0: EA's other code trusts Count). Tooling:
`tools/ilpatch-convert.ts --dump` / `--fill` (hand specs), and MemberRefs whose return type ends in `>` now key right.
The rest are per-tick or rare; measure the GC share first (§6).

## 6. GC measure for the next A/B

`tools/harness/gc-rate.ts <off log> <on log> [--sim-hours H,H]` reads the [MemDiag] samples: marks (Boehm collections,
`GC_gc_no`) per hour and live-set growth per hour (gcHeap - gcFree at the `ev=gc-after` samples, least squares; every
sample as an upper bound when there are fewer than two), per arm and on - off. Run 2043 for reference: 1.5 h, 164 marks
(108/h), live set 215 -> 298 MB (+49 MB/h). The gc fields are still flagged unvalidated in the log.
