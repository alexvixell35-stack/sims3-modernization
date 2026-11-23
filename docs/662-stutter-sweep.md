# 662. Stutter sweep 2026-11-19: what the 408 hitches really were, and the fixes for our own costs

2026-11-19. Source: 29 Isla Paradiso / Bridgeport phase runs of 2026-11-19 (`<runs>/phase-20261119-*/ts3native-full.log`,
release ini: `[RouteAsync] Kinds=0 ForeignKinds=4 WatchdogKind2=1 PlanDeadlineMs=300 KindDeadlines=1`), ts3fixes / ts3native
source at combo 91e7f9b6, decompiled scripts (read only). Branch `feat/stutter-sweep`. Counter slots 4244-4249 (docs/registry.md).
Built, selftests pass. In game only inside the s174-s189 on-set storms (the two ts3fixes switches were on there, but their
counters were never dumped: RAN, NOT MEASURED). Upstream PR: both ts3fixes switches default off; the native `KindsUnseen` of §4
is not part of it (native routing work, posted separately or not at all).

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

**Fix** (tuning `kStuckTaskFlag`, switch `stucktaskflag`, **default off** (it was on in the contributor's branch): a bug fix inside our own wrapper, the shape of
`sInWdCheck` / `sInOwnCheck` since PR #306): `sStuckIn` is a property that holds only in `sStuckRunTask`
(`StutterSweepCore.StuckInTask`). The check's own probes keep every tag (OwnerTaskSlot, PROBE round, owprobeplans, counters).
Off = the old global flag. Counter **[4244]** = plans of other tasks inside a check's window (what the old flag mislabelled).

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

**Fix** (tuning `kWatchdogBusyDefer`, switch `watchdogbusydefer`, **default off**; on in the contributor's branch): the refused A1 START answers
`FailFast` inside the check and `SpotVerdict` returns null (no verdict, nothing cached), the same "no verdict now" the per-tick
budget already gives; `CheckIdle` retries in a later tick (up to 4 tries a pass, then the next pass). The verdict is the
planner's own, later. Cap: a Sim deferred `kWdDeferMax` = 8 times in a row plans sync as before, so a START that stays refused
cannot hold a verdict off (`StutterSweepCore.WdBusyDefer`). A1 never picks a household, selected or unknown-scope Sim.
Counters **[4245]** refused A1 STARTs, **[4246]** checks deferred, **[4247]** cap reached.

**Expected:** the 7 sync watchdog plans (1.3 s, 1 hitch of 337 ms) become deferrals. Small; it closes the last sync path of
our own checks. Tests: selftest `stuttersweep`; repro `watchdogbusydefer-check`.

The stuck-check drain's own probes (460 async plans, 166 s of job time) are left as they are: the existing opt-in
`stuckchecknear` (same verdict, nearest point first, fewer failed floods, docs/163 §3) is the decision-identical saving;
`stuckcheckcap` changes when a reset can happen and stays out. Turning `kStuckCheckNear` on is a tuning-only A/B.

## 4. `[RouteAsync] KindsUnseen`: own-task replans of unseen NPCs as A10 jobs (native, default 0) [verified code, compile only]

**What is left sync (§1).** 550 `ufplansplit` lines in the 29 logs are kinds 1-3, `sync ... (START refused), why 0`; they led
94 hitches >= 200 ms (45 s): ReplanFromPoint 31 / 17.3 s, PlanFromPoint 29 / 16.5 s, Replan 34 / 11.7 s. ts3fixes already
sends the own START and, as the first thing in every plan, the route's scope (`UfPlanBody` -> UserDiag op 11 for UfWhy 0).
ts3native refused them only because `Kinds=0` (rejected in docs/136 for moved waits across **all** Sims).

**Build** (ts3native, `a10.cpp` + `ts3native.cpp` `A10StartBody`, `plandeadline.cpp` `KdScopeUnseen`):
- ini `[RouteAsync] KindsUnseen` (bits as `Kinds`: 1 Replan, 2 PlanFromPoint, 4 ReplanFromPosition; default 0, shipped ini
  documents it at 0). A kind in it is hooked (as `ForeignKinds` / `WatchdogKind2` hook theirs).
- Gate: an own START of such a kind passes the kind gate only when ts3fixes' scope answer for **this route** is unseen
  (`g_kdScopeH == handle && g_kdScope == 2`, before `KdScopeTake`). No answer, household / seen (1), seen NPC (3) and another
  route's answer stay refused `NoStart` and plan sync exactly as today. `HhExempt` is the gate, so `[Experimental]
  HouseholdToo=1` lifts it (a worker-computed answer, docs/271 realistic-only).
- `ArPass`: a kind hooked only for `KindsUnseen` / `WatchdogKind2` (not in `Kinds` / `ForeignKinds`) sends every call that
  READY did not arm and that meets no job of its route straight to the game's IC (the old `ArWdPass`, generalised): household
  Replans keep today's unhooked path (no `SyncIc`, no `SyncCapMs`). Kind 3 is a `ForeignKinds` kind in the release ini, so its
  path is unchanged.
- After the gate everything is A10's (docs/87, docs/176): NoYield, Active (route being followed), M1, queue, D drains, H2,
  CANCEL, collect checks; the worker runs the whole original IC, so the answer is the game's own planner's.
- Log: `route async: KindsUnseen: own kind N START of unseen route ... passes the kind gate (passes a b c, not unseen d e f)`
  at the 1st, 2nd, 4th, 8th ... pass of each kind, 24 lines a session at most. Counts also land in the existing `arStarted[]` /
  `arRefused[]` / `arRf3` counters (no new counter layout).
- Selftest `TestA10` case 13b (no answer / household / seen NPC / other route refused, unseen starts and collects, `ArPass`
  both ways, kind 2 not in the mask refused, HouseholdToo lift counted).

**Known difference from the sync plan [inferred].** The job is an own A10 job, so `KindDeadlines` applies (`KdOwnReplanMs` /
`KdOwnPlanFromPointMs` / `KdOwnReplanFromPositionMs` = 250 ms, doubled after a cut, uncut on the third try, docs/197). The sync
kind-1 / kind-2 plans today have no IC session (unhooked) and only `PlanDeadlineMs`' Route_PlanLocked session, which often
has no exit (docs/643 §1). So a job of an unseen route that runs past 250 ms is cut where the sync plan ran on; the
follower's backoff makes the second retry 500 ms and the third uncut. Unseen Sims only (the household rule allows cuts for
them); jobs that end under 250 ms (most: the sync median is ~20 ms, docs/247 §13.3) give the identical route.

**Expected:** up to the 94 hitches / 45 s of §1 leave the sim thread, minus refusals A10 keeps (Active for a followed route,
M1, queue full) and minus the moved waits: a job holds `+0x918` on the worker, and sim takers wait behind it unless
TailUnlock / P5 lending admit them (docs/176 §6). Measure with the repro `ip-kindsunseen-crowd` (two loads, ini 0 vs 7) and
`hitch-ab.ts`; the household lines (`why 3`) must stay sync.

**Build checks:** `clang-cl` compiles `ts3native.cpp` and `selftest.cpp` (i686, xwin). The selftest exe cannot run here: built
with clang it faults in `TestRouteCache` (clang x86 SEH does not catch the deliberate faults inside `__try`), before `TestA10`.
Run `native/ts3native/build.cmd` (MSVC) before any game run.

## 5. Object destroy / release ("P5c final release" near hitches): characterised, no fix [measured; type names verified]

The lines come from `p5c.cpp` `P5cObjLog` ([CrashForensics] on, first 4096 parks and 4096 final releases a session): a destroy
while a route job runs parks one reference (design G, `[Discourage] DestroyPark` / P5c window) and the last Release runs on
the sim thread at the next no-job flush point (START / READY polls, save, closing).

- 29 logs: ~111,000 parked + released objects (22 of 29 logs hit the 4096-line cap, so late-session ones are not logged).
- Resource type of the key (`+0x18..+0x24`): **0x2DA18F83 = `kEffectsObjectResourceID` (VFX effect objects) 81%**;
  0x319E4F1D = OBJD game objects 19% (top instances 0x37F, 0x0098B3B5, 0x8FE, 0x0098DB49); 0x02DC343F 1.4%. Top effect
  instances 0x71DDDE5B (35k), 0xAEC39CA4 (13k), 0x3876EF4A (6k), 0x17FF8C1F (4k).
- Park age (destroy -> final release): p50 38 ms, p90 128 ms, max 11.3 s (a job that long).
- **Not denser in hitches:** release lines per log line are 0.21 inside hitch windows (40 lines before to 10 after a
  >= 200 ms hitch) against 0.32 over the whole logged span. The first ranking's "P5c near the hitch" is co-location: effect
  objects are created and destroyed all the time (~3,800 a session), so most windows hold some.

No fix: nothing shows the release itself is slow, and the destroy work is EA's own (moved to the flush point, not added).
To decide it, the next run would need a time stamp per `P5cFlush` (count and us) in the hitch frame; not built.

## 6. Left, and why

- **Household (why 3) sync plans:** 7 hitches / 2.5 s. `KindsUnseen` keeps them sync; an exact path for them needs proof
  the job equals the sync plan under every cut, which A10 cannot give while `KindDeadlines` cuts own jobs (§4).
- **Seen NPCs (why 4-8) sync plans:** 18 hitches / 9 s. Same mechanism as §4; a second mask (scope 3) is a one-line
  extension once §4 is measured. Not built, so the first A/B reads unseen only.
- **Kind 0 sync plans (M1Pending / Los):** 8 hitches / 3.8 s; not a kind gate, A10's own refusals.
- **Async victims (~245 hitches / ~95 s):** the frame cause is elsewhere (lock-holder waits behind worker jobs, boats, pets,
  harness). These logs had no `HitchAttr` frame keys; the docs/247 §13.4 C/J1 per-site drain counters are the next step.
- **Stuck-check probes:** 460 async drain plans; the decision-identical saving is the existing opt-in `stuckchecknear` (§3).
