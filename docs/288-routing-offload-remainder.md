# 288. Routing offload remainder: own A10 Kinds for unseen NPCs only

Written 2026-11-18 (offload lane A, agent 2). Static analysis plus one native build; no game run. Ledger: docs/51 §517.
Source list: offload inventory 1118 item 3 (route residual 65 / 53 / 11 ms/s BP / IP / SV on rebaseline-1116c, timing-invalid).
P6 waits stay stopped (docs/adr/0005); nothing here touches A_HOLD.

## 1. Question

Which part of the route residual can still leave the sim thread? Two candidates: (a) the own-task Replan-family ICs that A10
(docs/87 §5, `[RouteAsync] Kinds`) can run as route jobs but that ship synchronous (`Kinds=0`), and (b) sim waits behind route
jobs (`rtLock` / `rtJob`, `haUs[0]` / `haUs[2]`). (b) was the P6 ladder and is stopped; the live levers there are CutOnWait,
KdUncutCeilMs and JobYield (docs/183, docs/264). This doc builds only the safe half of (a) and a diag that sizes both.

## 2. What exists (not rebuilt)

- `[RouteAsync] Kinds` bit mask (value 1 = Replan 0x765490, 2 = PlanFromPoint 0x765240, 4 = ReplanFromPosition 0x765550;
  the native test is `g_arOwnKinds >> (idx - 1)`) [verified, ts3native.cpp GetOptionHook]. `KindsMode` 0 = shadow (every check
  runs, START refused, counted in `arShadow*`), runtime switch `--ab asyncreplan` (counters v40 `ar*`). Built 0.22.0, never run
  with own-task kinds. `ForeignKinds=4` ships ON (docs/176).
- `[RouteAsync] H2Ic` (docs/177 §4, v102 `hx*`, default 0) and `RefcountGuard` (docs/177 §7, default 1) are the race guards an
  async route IC needs (footprint writes from script ICs during a job; the refcount query release).
- Measurement: `[RouteJobPhases] SyncPlans` (docs/164, v97 `syp*`: every sim-thread sync plan by phase, entry and result),
  `arSyncUs*` / `arSyncN*` (sim time of hooked IC calls that planned there), `[HitchAttr]` `haUs[4]` (sim waits: +0x918, other
  lock, job wait, plan waited for the job).

## 3. RUN FIRST: route split diag

`tools/harness/queues/offload-a2-route-diag-1118.json` (BP, IP, SV, 10 min each, one arm, ~45 min): release defaults +
`Kinds=7 KindsMode=0` (shadow: every plan stays synchronous, as in release) + `SyncPlans=1` + `HitchAttr`. Per world it gives:

| Reason | Counter |
|---|---|
| own Replan / PlanFromPoint / ReplanFromPosition sync plan time | `arSyncUs*` / `arSyncN*` |
| of those, STARTs of routes known unseen (the only ones §4 may move) | `arShadow*` |
| of those, household / selected / seen-NPC / unknown routes (stay sync) | `arScopeRefused` |
| all sim-thread sync plans (IC path vs direct `Route_PlanLocked`) | `sypUs`, `sypEntry`, `sypPhaseUs` |
| waits behind route jobs | `haUs[0]` (+0x918), `haUs[2]` (job wait), `haUs[3]`; profiler `rtLock` / `rtJob` |

Decision rule: time and correct-check `Kinds=3` (§4) only if `arShadow` x mean `arSyncUs` of Replan or PlanFromPoint is
>= ~3 ms/s on BP or IP. If the waits dominate, no new Kinds work: the remaining levers are the shipped JobYield / CutOnWait.

## 4. Built: the own Kinds gate (0.36.281-offloada2, counters v213)

`ArOwnGateOk` (ts3native.cpp, before `GetOptionHook`), armed by `InstallA10` whenever `Kinds` has a bit. No new switch: it is
the household rule for an existing default-off switch.

- An own-task kind START (not WatchdogKind2's tagged one, which A1 never sends for a household Sim) starts only for a route
  ts3fixes reports unseen: scope 2 for this handle (`KdScopePeek`, plandeadline.cpp; UserDiag op 11 sent by `UfPlanBody`
  right before the START). Household, selected, seen NPC (scope 3), unknown and no answer (old package, nested plan between
  send and START) plan synchronously = vanilla, counted `arScopeRefused`.
- With `KindsMode=1` it also needs H2Ic installed on all 7 slots with `H2IcMode=1` and `RefcountGuard` with Mode 1; else every
  own START is refused, counted `arPrereqRefused`. Shadow mode needs no guards (nothing starts).
- `[Experimental] HouseholdToo=1` lifts the scope part (the worker runs the game's own IC, so the answer is realistic,
  docs/271), never the guard part.
- Defaults: `Kinds=0` (unchanged), so release behaviour is unchanged. Selftest TestA10 case 15 covers each branch.
- Before 0.36.279 `Kinds` had no scope test at all (every Sim's own plans would have moved); that was never run.

## 5. Static findings (callers and hazards)

From the script DLLs and our sources (ledger §517 rows):

- 81 call sites of `ScriptCore.Route.Replan` / `PlanFromPoint` plus 58 through the `PlanTo*FromPoint` helpers; all pass the
  ts3fixes hooks [verified]. `AsyncEligible` allows an own START only on the route Sim's own task, in a yielding context,
  live mode, no pending jig [verified, RouteBackoff.cs].
- `SimRoutingComponent.DoRoute` (the main own Replan): sets `BlockedByPeople` on the route before, reads `PlanResult` and
  `IsPointObstructedBySim` after; the task sleeps in between, nothing outside `r` changes [verified code; risk low, inferred].
- `FollowRouteAction.ForceReplan` on a Sim never plans directly (it queues `ReplanRouteInterruptAction`) [verified].
- Route-event callbacks (`SteeringAdaptor.HandleEventNeedReplan`, `Terrain.RouteAppendCallback`) and
  `GoHereWithSituation.GoToNextState` (leader route read by followers) would be unsafe to sleep in; they are expected to be
  refused as NoYield / Active / foreign [inferred, open: verify with the correctness run's refusal split].
- `IsPointInLotSafelyRoutable` uses PlanFromPoint as a query; inside FindGoodLocation callbacks it cannot yield, so it stays
  sync [inferred].
- A kind 1/2 job holds +0x918 for the whole IC like a kind-0 job; H2Ic and RefcountGuard work per thread and lock, not per
  kind, so they cover the same hazards [inferred]. Gaps carried over from kind 0, not new: the TailUnlock / P5b lend overlap,
  and Replan's `route+0x1F4` increment on the worker read by other tasks (followers) [inferred].

## 6. Queues (written, dry-run checked, not launched)

- `offload-a2-route-diag-1118.json` **RUN FIRST** (§3).
- `offload-a2-kinds-correct-1118.json` (correctness, ~30 min): BP + IP 10 min, `Kinds=3 KindsMode=1 H2Ic=1 RefcountGuard=1`;
  gates `arStartedReplan >= 1`, `arCollectedReplan >= 1`, `arScopeRefused >= 1`, `arPrereqRefused < 1`, `hhTooLift < 1`,
  0 script errors, read-back of the A10 / H2Ic / refcount lines. Missing before default ON: save + reload spot check.
- `offload-a2-kinds-time-1118.json` (timing, needs quiet machine, ~75 min incl. smoke): ABBA `--ab asyncreplan` 27 min BP + IP,
  guards on in both arms. Watch route + rtLock + rtOtherLock + rtJob together: with ForeignKinds the waits moved (docs/176).

## 7. Open

- H2Ic has never had its A/B (`route-goal-ab.json` not run); the kinds correctness run is its first live use with Mode 1.
- `ReplanFromPosition` own STARTs (value 4) are gated the same way but not queued: ForeignKinds already moves the foreign ones.
