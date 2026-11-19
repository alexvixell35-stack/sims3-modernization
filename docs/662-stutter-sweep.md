# 662. Stutter sweep 2026-11-19: what the 408 hitches really were, and the fixes for our own costs

2026-11-19. Source: 29 Isla Paradiso / Bridgeport phase runs of 2026-11-19 (`<runs>/phase-20261119-*/ts3native-full.log`,
release ini: `[RouteAsync] Kinds=0 ForeignKinds=4 WatchdogKind2=1 PlanDeadlineMs=300 KindDeadlines=1`), ts3fixes / ts3native
source at combo 91e7f9b6, decompiled scripts (read only). Branch `feat/stutter-sweep`. Counter slots 4240-4249 (docs/registry.md).
Built, selftests pass, NOT RUN in game.

## 1. Attribution: most "seen-Sim plan" hitches are async plans that slept through someone else's frame [measured]

The first ranking (game hitches >= 200 ms with agent `-`, 408 in all) named the longest `ufplan:` line inside each hitch.
`ufplansplit:` says how each of those plans waited: `sync N ms (START refused)` = the plan ran on the sim thread (the frame
waited for it); `pre .. wait N (P polls, longest L)` = an async job, the task slept one frame per poll, and `longest` is the
longest frame it slept through. An async plan inside a hitch is a victim of that frame, not its cause.

Re-attributed (script: one pass over the 29 logs; a hitch goes to an overlapping **sync** plan whose sync time is >= 50% of the
hitch, else it is "no sync plan"):

| cause | hitches | s |
|---|---|---|
| no sync plan: async plans slept through it (frame cause elsewhere: P5c releases nearby in 121, lock-holder lines in 72) | ~245 | ~95 |
| sync own-task plans, why 0 (`UnseenFallbackCore.kSeen` = **unseen** NPC, "act"; not a Sim in view) | 102 | 48.6 |
| - kind 3 ReplanFromPoint | 31 | 17.3 |
| - kind 2 PlanFromPoint | 29 | 16.5 |
| - kind 1 Replan | 34 | 11.7 |
| - kind 0 Plan (refused M1Pending / Los, not a kind gate) | 8 | 3.8 |
| sync boats (BoatTaxi Replan / RFP, BoatSail ...) | 14 | 20.0 |
| sync seen NPCs (why 4-8) | 18 | 9.0 |
| sync household (why 3) | 7 | 2.5 |
| sync pets | 2 | 0.8 |
| sync "stuckcheck" (all four in a Sim's own task, see §2) | 4 | 2.3 |
| sync watchdog | 1 | 0.3 |

Why the why-0 plans are sync: the release ini has `Kinds=0`, so ts3native refuses every **own-task** START of kinds 1-3 as
`NoStart` (`A10StartBody`, `kRf3KindsOff`) and ts3fixes plans them on the sim thread (`RouteBackoff.cs` `ReplanAsync` /
`PlanFromPointAsyncBody` / `ReplanFromPointAsync`). ts3fixes already sends the START (switch `asyncreplan`, on) and the route's
scope right before it (`UfPlanBody`: UserDiag op 11 = unseen). §4 lets ts3native start those for routes known unseen.

The managed callers cannot be split further from these logs: the `route async: sync plan ... caller` addresses are JIT
addresses, and `LogFallbackStack=0`, `routesrc` off. Static callers (`tools/managed-callgraph.ts callers`): Replan from
`SimRoutingComponent.DoRoute`, the route interrupt actions (`ReplanRouteInterruptAction`, `ObstacleEncountered...`,
`DestinationObstructed...`, `ChangeDestination...`), `FollowRouteAction` / `PortalRouteThroughAction.ForceReplan`, social jigs;
ReplanFromPoint as docs/176 §2; PlanFromPoint mostly `Sim.IsPointInLotSafelyRoutable` (docs/584 §3).

Own costs (task 1) measured the same way: watchdog 543 slow plans, all but 7 already async (A1, `watchdogasync`); stuck-check
drain 460 async (FallbackAsync foreign STARTs); spot checks async (spotasync). Their sim-thread share is the 7 + 3 sync plans
(§3) and the `+0x918` hold of their jobs (lock-holder waits; not split per job in these logs).

## 2. `stucktaskflag`: the stuck check's flag leaked to every task [verified code, measured]

**Bug.** `FewerPlans.cs` `StuckCheckRun` set a plain static `sStuckIn` for the whole check. With `stuckcheckasync` (on) the
probe plans run as FallbackAsync jobs and the drain task sleeps; every other task that planned meanwhile read `sStuckIn` true:

- `UfPlanBody`: `ownCheck` true, so the plan was never MARKed unseen, and the `birthroute` / `goalhhexempt` scope tests were
  skipped (an unseen NPC whose goal is the household lot kept scope 11 = unseen for ts3native's KindDeadlines);
- `UfSrc`: "src stuckcheck"; `UnseenBoat` / `UnseenBoatLand` skipped the plan; gvattr class wrong.

**Measured:** of the 1,238 `ufplansplit` lines labelled `src stuckcheck`, 460 + 3 are the drain task's own probes; **~770 are
Sims' plans in their own (or another Sim's) task**: 305 s of plan time, 151 >= 300 ms (kind 0 async 732, kind 2 sync 7 up to
3.9 s, kind 3 17, kind 1 6). So the "stuckcheck 1218 slow plans, 467 s" row of the first ranking was mostly other Sims.

**Fix** (tuning `kStuckTaskFlag`, switch `stucktaskflag`, **default on**: a bug fix inside our own wrapper, the shape of
`sInWdCheck` / `sInOwnCheck` since PR #306): `sStuckIn` is a property that holds only in `sStuckRunTask`
(`StutterSweepCore.StuckInTask`). The check's own probes keep every tag (OwnerTaskSlot, PROBE round, owprobeplans, counters).
Off = the old global flag. Counter **[4240]** = plans of other tasks inside a check's window (what the old flag mislabelled).

**Expected:** the ~770 plans get the scope rules every other plan gets: unseen ones are MARKed (UnseenFallback deadline on the
sync ones: the 7 kind-2 plans of 0.15-3.9 s become ~0.3 s sync), household-goal and birth routes are exempt again. Hitch gain is
the 4 sync hitches (2.3 s) plus whatever the MARK cuts off the async jobs' `+0x918` hold; not measured.

Tests: `node tools/selftest/stuttersweep/run.ts`; repro `stucktaskflag-check` (pure decision with controls). In play: every
`src stuckcheck` line is in the drain or alarm task; `fixes:stucktaskflag_other_tasks` > 0 on nights with a stuck check.

## 3. `watchdogbusydefer`: a refused A1 watchdog check no longer plans on the sim thread [verified code, measured]

**Cost.** The watchdog's idle check (`CheckIdle` -> `SpotVerdict` -> `IsPointInLotSafelyRoutable`) goes out as an own-task
tagged job when ts3native would start it (A1, `watchdogasync`, `WdA1Would` q 1/5/7/9). A START that ts3native still refuses
(the worker runs another job: M1 lock test, queue full) fell through `PlanFromPointAsyncBody` to a sync plan, which waits on
`+0x918` for the running job. Measured: 543 slow watchdog plans in 29 runs, 536 async, **7 sync 151-312 ms** (one 337 ms hitch).
`watchdoglockskip` (off) only skips when a job runs *before* the check; spotbusydefer (docs/635) covers the spot checks only.

**Fix** (tuning `kWatchdogBusyDefer`, switch `watchdogbusydefer`, **default on**): the refused A1 START answers
`FailFast` inside the check and `SpotVerdict` returns null (no verdict, nothing cached), the same "no verdict now" the per-tick
budget already gives; `CheckIdle` retries in a later tick (up to 4 tries a pass, then the next pass). The verdict is the
planner's own, later. Cap: a Sim deferred `kWdDeferMax` = 8 times in a row plans sync as before, so a START that stays refused
cannot hold a verdict off (`StutterSweepCore.WdBusyDefer`). A1 never picks a household, selected or unknown-scope Sim.
Counters **[4241]** refused A1 STARTs, **[4242]** checks deferred, **[4243]** cap reached.

**Expected:** the 7 sync watchdog plans (1.3 s, 1 hitch of 337 ms) become deferrals. Small; it closes the last sync path of
our own checks. Tests: selftest `stuttersweep`; repro `watchdogbusydefer-check`.

The stuck-check drain's own probes (460 async plans, 166 s of job time) are left as they are: the existing opt-in
`stuckchecknear` (same verdict, nearest point first, fewer failed floods, docs/163 §3) is the decision-identical saving;
`stuckcheckcap` changes when a reset can happen and stays out. Turning `kStuckCheckNear` on is a tuning-only A/B.
