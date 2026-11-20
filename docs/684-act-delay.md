# 684. Act delay: where a Sim's time goes between "queued" and "acting"

2026-11-20 (UTC), `feat/act-delay-1120`. Inputs: ilspycmd output of the Steam 1.67 `Sims3GameplaySystems`, `SimIFace`
and `ScriptCore` (`InteractionQueue`, `InteractionInstance`, `Sim`, `SimRoutingComponent`, `RoutingComponent`,
`AutonomyManager`, `IdleManager`, `StateMachineClient`), our source (`RouteBackoff.cs`, ts3native `[RouteAsync]`), docs/631,
637, 670, 674. Built: ts3fixes `actdelay` (read-only probe, default off). **Nothing here has run in the game.**

Goal (Rodney): a Sim that was given, or picked, an interaction should not visibly stand and wait before it starts.
Step 1 is this probe: measure every interaction instance. Step 2 (§5) is the list of known candidates from the code,
ranked, each with the bucket that would show it. Fixes come after the two runs of §6.

## 1. The path of one interaction [verified: decompiled code]

1. **Queued.** Every `Add` / `AddNext` / `PushAsContinuation` ends in `InteractionQueue.InsertInteraction`, which inserts
   and calls `InteractionInstance.OnAddedToQueue`.
2. **Head.** It waits until the entries before it are removed. Every removal goes through
   `InteractionQueue.RemoveInteraction(int, bool, bool)`, which calls `OnRemovedFromQueue(index == 0)`.
3. **The Sim's loop takes it.** `Sim.Simulate` loops `DoInteraction`. With a head: distress / non-distress idles are
   offered first unless the head is MustRun, Prioritized or a continuation, then `ProcessOneInteraction`. With no head:
   `LoopIdle` (posture idle), an autonomy request, then `Simulator.Sleep` of 2 ticks (not in the autonomy queue), 10 (in
   it) or 20 (autonomy disabled): `kSimLoopSleepTicksWhen*`. After each interaction one more `Sleep(0)`.
4. **Before Run.** `ProcessOneInteraction`: baby / toddler handling (may plan a route), then up to 4 (10 in all) posture
   transitions, each its own interaction run with `RunInteractionWithoutCleanup`, then `RunTest`, `CallCallbackOnStart`.
5. **Run.** `RunInteractionWithoutCleanup`: `PushRunningInteraction(this)`, then `Run()`; `PopRunningInteraction` in the
   `finally`.
6. **Route.** Run's route helpers end in `ScriptCore.Route.Plan` / `Replan` / `PlanFromPoint` / `ReplanFromPoint` (our
   hooks since `backoff`; with `[RouteAsync]` the task sleeps a tick at a time in `AsyncWait` until the job is ready), then
   `SimRoutingComponent.DoRoute` -> `DoSingleRoute` -> `RoutingComponent.DoRoute`, which calls
   `OnAboutToStartFollowingRoute` once the plan stands, then pushes and runs the route actions.
7. **No route.** The interaction names the Sim as an actor of an animation state machine (`StateMachineClient.SetActor`)
   and requests states.

## 2. The probe

`mods/ts3fixes/src/ActDelay.cs` (hooks, log, check) and `ActDelayCore.cs` (records, buckets, aggregate; no game types).
Switch `actdelay` (tuning `kActDelay`, default off; `kActDelayLogMs` 500, `kActDelayLinesPerMin` 30).

| Stamp | Hook (pass-through) | Path to a yield (`tools/swap-stack-audit.ts`) |
|---|---|---|
| queued | `InteractionInstance.OnAddedToQueue` (stacked on `initstacktrace`'s hook) | only through a virtual `InUse` getter of one store object |
| removed, next head | `InteractionInstance.OnRemovedFromQueue` | only through `Gameflow.NotifyOfInteractionRemoval`, 8 calls deep |
| wake, Run begins, transitions | `InteractionQueue.PushRunningInteraction` | none |
| Run ended, transition ended | `InteractionQueue.PopRunningInteraction` | none |
| plan begin / end / result | the four existing `RouteBackoffHook` plan hooks (two calls each) | existing hooks |
| slept on a route job | `RouteBackoffHook.AsyncWait` (one stamp before the polls, one call after) | existing |
| the Sim moves | `SimRoutingComponent.OnAboutToStartFollowingRoute` | `Terrain.ModifyRouteForSpecies` -> the boat turn-around search sleeps (boat routes) |
| animation set-up | `StateMachineClient.SetActor` | only through `Sim.SetSacsDefaultParameters`, 19 calls deep |
| placement search time | `GlobalFunctions.FindGoodLocationNearby` (stacked on `RrNearbyHook`) | none |

Three targets have no path to a yield; four have one in the conservative call graph (every override of every virtual
call), so a frame of ours can sit on a sleeping task's stack there. Each swap has a stub, the case `savedstacks` remaps
at load (docs/242); the rows are in `docs/data/swap-stack-audit.csv`. With the switch off each hook is one static bool
test and the call of the original. `Simulator.Sleep`, `WaitForExitReason`,
`RequestState` and `ProcessOneInteraction` always hold a sleeping frame and are **not** hooked; waits show through the sim ticks instead:
every stamp carries real time (the microsecond `StopWatch`) and `SimClock.CurrentTicks`, and a bucket that spans ticks
was a task asleep, one with zero ticks was the sim thread busy or blocked.

Buckets, per instance, adding up to queued -> act:

| Bucket | From -> to | What it holds |
|---|---|---|
| `queue` | queued -> nothing in front | behind earlier interactions: the Sim is busy, not idle |
| `wake` | head -> first transition or Run | the Sim's loop sleep (2 / 10 / 20 ticks), idles offered first, baby handling, a frozen sim thread |
| `prerun` | -> Run | posture transitions and tests (count, ms and plans of the transitions are listed) |
| `preroute` | Run -> first plan asked | Run's own setup; all of Run -> act when Run never plans |
| `plan-cpu` | inside plans | plan time on the sim thread: sync plans, `+0x918` waits inside them, the job hand-over |
| `plan-job` | inside plans | the task asleep in `AsyncWait` for a ts3native route job (queue wait, host calls, parks, the READY tick) |
| `retry` | between plans | first plan asked -> last plan returned, minus the plans: re-plans, placement searches, push waits |
| `postplan` | last plan -> act | route-fail checks, lead-ins, anything between the plan and the first step |

"Decision / setup before Run" of the brief = `wake` + `prerun`. Also kept per instance: plans, failed plans, job waits,
placement searches (count, ms), `first visible` (a transition's route counts as visible movement), ticks before Run and
in Run. Beside the instance buckets, `pick` = how long a Sim's queue stood empty before it queued something itself.

**Acted** = the first `OnAboutToStartFollowingRoute` inside Run. When the Sim was set up on a state machine at least 4
ticks before the first plan (or no plan was asked), that set-up is the act; an instance whose Run ends with a set-up and no
route is finished at the set-up; one set up for 30 s is finished by the minute sweep. An instance whose Run ends with
neither is "ended without acting": counted, kept out of the percentiles, logged when it asked for a route (the "stood
there, then gave up" shape).

Aggregates: per Sim kind (selected / household / npc, judged when queued; unknown = household) a log histogram per
bucket (exact below 4 ms, four bins an octave, so p50 / p90 are within 12.5 %; max exact); per interaction type (the
instance's class; socials by action key) count, sum, mean, max, per-bucket means. Fixed tables: 1024 open records, 512
types, 16 kept lines; no allocation per event once warm; a record 30 min old is dropped.

Log (`ts3native.log` through `DiagLine`):

```
actdelay: 1500 ms queued -> move, npc user Objects.Plumbing.Toilet+UseToilet: queue 300, wake 250, prerun 250, preroute 50, plan-cpu 200, plan-job 200, retry 150, postplan 100 (2 plans, 1 failed, 1 job waits); first visible 550, transitions 1 (250 ms, 1 plans), placement searches 1 (60 ms), ticks 28 before Run + 30 in Run, day 3 hour 14.10, Sim <name>
actdelay minute: 212 acted (selected 3, household 20, npc 189), 31 over 500 ms, queued -> act p50 176 p90 1408 max 5200 ms; mean ms queue 12 wake 150 prerun 30 preroute 22 plan-cpu 8 plan-job 95 retry 14 postplan 40; slowest <type>; ended without acting 14, cancelled before Run 40, open 120, dropped 0
```

`ActDelayCheck("result")`: switch and hook state, totals, the p50 / p90 / max table per bucket and kind (and `pick`), the
top 15 types by summed queued -> act ms, the last 16 instance lines. `"run"`: the bucket model in the game's Mono.
`"live"`: the seven swaps. `"reset"`: totals to zero. Counters 4270-4279 (docs/registry.md), `kCounterSlots` 4280.
Repro `actdelay-check`. Self-test `tools/selftest/actdelay` (fixed cases for each rule, 400,000 random events: every
finished instance's buckets add up to its total).

## 3. Limits [by construction]

- `plan-cpu` does not split a sync plan's search from its wait on `+0x918`: ts3native has the numbers and ts3fixes has no
  way to read them without a native change. Join by time with `route lock wait:` / `route async: sync plan` /
  `route jobs: sim waited N ms for a job` lines (run with `HitchAttr.RouteLockMs=20 HitchAttr.JobWaitLogMs=20`, as the
  storm script does).
- Plans count only when made in the Sim's own task. A Sim seated in a vehicle follows through the vehicle's component:
  no "moves" stamp, such an instance ends as animation or "ended without acting".
- `SetActor` is the set-up, not the first frame of the clip; the 4-tick rule keeps a set-up right before a route from
  counting as the act.
- Baby / toddler handling plans before the first transition land in `wake`.
- Autonomy's own search time is before "queued": it shows only as `pick`.
- A plan cut short by a reset leaves its record's plan depth open; the record goes with the reset queue.

## 4. Checks

- `node tools/selftest/actdelay/run.ts`: PASS (183,080 checks).
- ts3fixes build (`build-fixes.ts --tag act`), check-live-map 5 hook classes / 7 swaps for `actdelay`;
  `tools/swap-stack-audit.ts`: the 7 swaps in the table, each with a stub.
- `node tools/selftest-all.ts --ci`: see the commit message.
- Not run in the game.

## 5. Candidates for visible idle before acting, from the code (ranked by expected share; to be re-ranked by §6)

EA's:

| # | Candidate | Source | Bucket |
|---|---|---|---|
| E1 | The Sim's loop sleeps 10 ticks while in the autonomy queue, 20 with autonomy disabled, 2 otherwise, and looks at its queue only when it wakes: a command given to an idle Sim waits up to that long (at 37.5 ticks a sim-minute, 10 ticks ~ 0.27 s and 20 ticks ~ 0.53 s at speed 1 [inferred from the tick constant]) | `Sim.DoInteraction` [verified] | `wake` |
| E2 | Posture transitions before Run: up to 4 tries (10 in all), each a full interaction with its own route and animation; a failed one updates block time and tries again | `ProcessOneInteraction` [verified] | `prerun`, transitions n / ms |
| E3 | `AutonomyManager.Simulate` serves one Sim a tick (alternating high / low LOD lists), so N Sims waiting for a decision wait N ticks; plus `kAutonomyDelayNormal` 5 sim-min (2 after a user cancel, 7 while socialising) before a Sim with an empty queue asks at all, unless its last interaction was autonomous and succeeded. The delay is EA design (not to be tuned); the one-per-tick service is a rate limit | `AutonomyManager`, `Sim.DoInteraction` [verified] | `pick` |
| E4 | Route failure: a failed plan runs `DoRouteFailureBehavior` (fail-path plans, the shrug) and callers such as `RouteToSlotList`, `RouteToUnusedPart` plan again for the next slot | `SimRoutingComponent` [verified] | `retry`, plans / failed |
| E5 | Waiting for other Sims: `PushSims(wait)` polls every 10 ticks for up to `TotalSimMinutesToWaitForSimsToBePushed`; socials wait in `WaitForSynchronizationLevelWithSim` for the partner to finish what it does; lines (`SimLine`) | `RoutingComponent.PushSims`, `Sim` [verified] | `retry` / `postplan` (push), `preroute` with many ticks (sync) |
| E6 | Placement searches inside Run (`FindGoodLocationNearby`: jigs, put-downs), sync on the sim thread | docs/671, 682 [measured there] | `preroute` / `retry` with 0 ticks, placement searches n / ms |
| E7 | Route lead-in and the idle offered before a non-prioritised head (`DistressIdle`, `PlayNonDistressIdleIfNecessary`); the posture idle has to bridge out before the first clip | `Sim.DoInteraction`, `DoSingleRoute` [verified call sites; blocking time inferred] | `wake`, `postplan` |
| E8 | One `Simulator.Sleep(0)` after every interaction, and the `StackTrace` built in `OnAddedToQueue` | `Sim.DoInteraction`, `InteractionInstance` [verified] | `queue` (1 tick), sim cpu |

Ours:

| # | Candidate | Source | Bucket |
|---|---|---|---|
| O1 | Async route jobs: the task sleeps at least one tick per plan (READY is polled once a tick), longer when the worker is busy with other Sims' jobs (docs/631: household jobs queued behind townie jobs, mean 353 ms, max 1965) | `AsyncWait` [verified], docs/631 [measured] | `plan-job` |
| O2 | Host calls: a job waits a sim tick per portal door ask unless `TaskServe` / door memos answer it | docs/637, 674 [measured] | `plan-job` |
| O3 | Sync fallback plans and other sim-thread takers waiting on `+0x918` behind a job (`lock918` frames); they stall every Sim at once | docs/670 [measured] | `plan-cpu` (own plan), any bucket with 0 ticks (others) |
| O4 | `SkipRetry` parks (`route skip retry: ... parked`): the START waits up to 30 READY ticks instead of a sync plan; unseen Sims only | docs/670 §20 [built, not run] | `plan-job` |
| O5 | M1Retry / refused STARTs retried, `kAsyncMaxPolls` 1000 before the plan is made again synchronously | `RouteBackoff.cs` [verified] | `plan-job`, then `plan-cpu` |
| O6 | Unseen-fallback holds, route backoff, no-path memos: they shorten plans for NPCs but a hold sleeps before the plan | docs/202 [verified in source] | `plan-cpu` with ticks |
| O7 | The probe's own hooks and the other pass-through hooks on these paths | this doc | none expected; compare `wake` p50 on / off is not possible, so judge by sim it/s |

## 6. Run plan (both with the probe on; the game is not started by this branch)

Package: `build/ts3fixes.package` of this branch (copy as `pkg/ts3fixes.act-1120.package`).

(a) The 2-minute Isla Paradiso route storm, on arm only, as s214 / s215 but with `actdelay` added:

```
ONLY_ON=1 MF_EXTRA="doorshare nearestlotlazy actdelay" ON_KEYS="<the K of s215>" STORM_MIN=2 STORM_SEED=152 \
  AGENTPKG_AB=$PWD/pkg/ts3agent.r191-storm2.package FIXPKG_AB=$PWD/pkg/ts3fixes.act-1120.package ./storm-ab.sh sNNN > sNNN.out 2>&1
```

(b) A calm 10-minute soak, autonomy only, same load and keys: the same line with `PHASE_AB=idle STORM_MIN=10`.

In both, before the session ends, fetch the aggregate (a background loop as `setshadow` in `s214-run.sh`, after ~90 % of
the phase): `node tools/harness/ag.mjs reflect_call '{"path":"Ts3Fixes.Fixes.ActDelayCheck","args":["result"]}'`.
Read: `grep -c '^actdelay: ' ts3native-full.log`, the `actdelay minute:` lines, the result's table and top 15. First
check that the measurement works: `actdelay switch on`, `members found=True`, errors 4275=0, npc acted > 0, and in (b)
household acted > 0. Then rank §5 by the bucket sums of the over-threshold lines, storm against calm.
