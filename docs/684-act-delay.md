# 684. Act delay: where a Sim's time goes between "queued" and "acting"

2026-11-20 (UTC), `feat/act-delay-1120`. Inputs: ilspycmd output of the Steam 1.67 `Sims3GameplaySystems`, `SimIFace`
and `ScriptCore` (`InteractionQueue`, `InteractionInstance`, `Sim`, `SimRoutingComponent`, `RoutingComponent`,
`AutonomyManager`, `IdleManager`, `StateMachineClient`), our source (`RouteBackoff.cs`, ts3native `[RouteAsync]`), docs/631,
637, 670, 674. Built: ts3fixes `actdelay` (read-only probe, default off). Ran in the game on the contributor's combo branch: s218 / s219
(nothing recorded, §7), s223-s226 (§8), and with the §8.1 stamps in the §9.1 OutfitFirst runs. The probe changes nothing.

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
`"live"`: the swaps (nine since §8). `"reset"`: totals to zero. `"outfit"`: the §9 trigger. Counters 4270-4289 (docs/registry.md;
4280-4289 are §8's), `kCounterSlots` 4290.
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

## 7. s218 / s219: nothing recorded, and why [verified: source + the runs' switch list; fix NOT RUN]

Both runs (storm, calm; package from combo `44e04f12`, switch on at runtime) answered `0 acted`, every bucket n=0, no
`actdelay` line, `members found=True`, errors 0.

- **Cause:** `Fixes.Patch` stacks swaps of one method: the hook patched later runs first and reaches the earlier one only
  through its own stub. `initstacktrace` (`IstHook.AddedHook`, SeasonsHunt594.cs) also swaps
  `InteractionInstance.OnAddedToQueue`, was patched after the probe (`ApplyInitStackTrace` came later in `Fixes.Apply`),
  and with its switch on (it was, in both runs' `earlyfix.txt`) re-does EA's body without calling its stub. So the
  probe's `AddedHook` was never entered, no record was ever opened, and every other stamp found no record to write to.
- **Why the check did not show it:** `members found=True` only says the swaps went in (`EpLive` counts swaps per hook
  class). It cannot see a hook that is in place but not reached.
- **Fix:** `ApplyActDelay()` is the last call of `Fixes.Apply`, so the probe's hooks are the outermost of every stack
  made there. No tuning-gated patch (`ApplyTuned`) touches the probe's seven methods.
- **So it cannot hide again:** each hook counts its entries, switch on or off. `ActDelayCheck("result")` and `("live")`
  print `hooks entered: OnAddedToQueue N, ...` and `DEAD HOOK <name> 0 with <other> N` when one side of a pair that the
  same game path enters stays at zero past 50 entries of the other; the same line goes to the log once a minute.
  `tools/probe-swap-order.ts` (in `selftest-all --ci`) fails when another file's swap of a probe method is applied
  after the probe's; on the old order it names `IstHook`. The core self-test covers the health rule and the
  "no Queued, no record" shape.
- **Read-back for the next run:** after a minute in live mode, `ActDelayCheck("live")` must say `verdict: ok` with
  `OnAddedToQueue` and `PushRunningInteraction` both above zero.

## 8. s223-s226: what the idle time of the top types is [per row: verified = decompiled code + the run's log, inferred = code + timing only]

Runs s223 / s224 (storm 2 min, calm 6 min) and s225 / s226 (the same with PlayerFirst / TaskServe) ranked household idle
before the first visible action as `preroute` 61-74 %, `prerun` 15-28 %, plans 3-4 %, `wake` 5 %. Reading each top type's
`Run` shows that most of that time is not a Sim standing still: the probe had no stamp for it. The tick column of the
lines says the same: every long `preroute` spans hundreds of sim ticks (a sleeping task), none is sim-thread CPU.

| Type (who) | What the time is | Methods | Bug? |
|---|---|---|---|
| `GoToSchoolInRabbitHole` (household, 2.0-5.4 s `preroute`) | **The school bus ride.** The interaction is queued while the Sim sits in the carpool; `Run` starts with `CarpoolManager.WaitForCarpool`, which sleeps until the vehicle arrives, then `Vehicle.GetOut`, then the route into the school. Every such line in s224 / s226 follows a `Car.GetOut < Vehicle.GetOut < CarpoolManager.WaitForCarpool` task line [verified] | `RabbitHoleInteraction.Run`, `CarpoolManager.WaitForCarpool` | no: probe artefact |
| `GoHome` (household, 2.5-2.8 s `preroute`, both children at 15.63) | **The bus ride home**: `GoHome.Run` also starts with `WaitForCarpool` [verified code; the two lines stand right after the carpool get-out line] | `GoHome.Run` | no: probe artefact |
| `SocialInteractionB` (household and NPC, 0.3-1.5 s `preroute`) | **The receiver waits for the starter to walk over.** `ReceiveSocialInteraction` plays the idle (`LoopIdle`) and sleeps until the starter's first route is done (`mbInitialRoute`), then places the jig, plans both routes and waits for the starter's approach (`mbApproachRoute`); `StartSync` / `WaitForSynchronizationLevelWithSim` (Routed, then Committed) after it. The receiver is meant to stand and wait [verified code; the share per instance is what the new `sync` stamp measures] | `SocialInteractionB.Run`, `SocialInteraction.ReceiveSocialInteraction` | EA design (the 30 sim-minute give-up when the starter is gone is the known bug, `socialreceivefail` / `socialunlinkend`) |
| `ChildUtils+ChildPlaceholderInteraction` (household, 0.7-1.4 s) | **The child waits for the adult** of the paired interaction (pick up, feed, read to): `StartSync(false)`, `WaitForSynchronizationLevelWithSim(Committed)` [verified code] | `ChildPlaceholderInteraction.Run` | EA design |
| `Bed+Bed_ReadToSleep` (household, 2.6-3.2 s `preroute`), `Bed_BeReadToSleep` (the same time as `prerun`) | **The reader waits for the child to get into bed**: sleeps until the child's linked interaction runs (routes near the bed if far), `LoopIdle` in a loop, then routes to the bed side. The child's time is its get-in-bed transition (1 transition with a route, first visible 54-69 ms) [verified code] | `Bed_ReadToSleep.Run` | EA design |
| `EatHeldFood` (all Sims, `prerun` ~1.05-1.4 s + `preroute` ~0.3 s) | **Walking to a seat and sitting down** (the posture transition: 2-3 plans, first visible after 80-150 ms), then in `Run` the chair is pulled in (`SitTransitionAngledToStraight` run inside `Run`) before the eat state machine is set up [verified code for the order; that the 0.3 s is the chair is inferred: the new line names the interaction run inside] | `InteractionQueue.ProcessOneInteraction`, `EatHeldFood.Run` | no: visible action |
| `ReadBook` (`prerun` ~1.0-1.4 s, 1 plan, first visible ~70 ms), `Guitar+Jam`, `TakeBath`, `BeKingOrQueenForADayWhileSitting` | **The sit-down / get-in transition with its route** [verified from the lines: transitions with plans, first visible under 200 ms] | `ProcessOneInteraction` | no: visible action |
| `ReactionInteraction`, `ServingContainer_CleanUp`, `Fridge_Have`, `Guitar+Watch`, `UseToilet`, `ReactToContraction`, `GetInCarpool` (`prerun` 430-650 ms, 1 transition, 0 plans, no state machine set up) | **One posture transition without a route**, most likely standing up from a seat or leaving the social posture: it animates on the posture's own state machine, which the probe cannot see (`SetActor` is not called again) [inferred; the new line names the transition's class] | `ProcessOneInteraction`, the posture's `GetStandingTransition` | no (to confirm by name) |
| `ResortTower+ExitTower` (NPC, ~1.1 s) | **A 10 sim-minute stay inside the tower** before the Sim comes out: `DoTimedLoop(10f)` (10 sim-minutes = 375 ticks; the lines show 330-440) [verified code] | `ExitTower.Run` | EA design, Sim not visible |
| `WorkInRabbitHole` (NPC, 0.5-1.9 s, then an animation, no plan yet) | **The work outfit**: `RouteNearEntranceAndIntoBuilding` calls `Sim.SwitchToOutfitWithSpin(GoingToWork)`, whose `SwitchOutfitHelper.Wait(true)` plays the idle and sleeps a tick at a time until the native swap calls back; the spin's `SetActor` is the animation the probe then sees. Waits of Sims leaving in the same minute rise in steps (s226 hour 8.14-8.16: 486, 546, 594, 704 ms), the shape of one build queue [inferred: code + timing; the new `outfit` stamp measures it] | `WorkInRabbitHole.RouteNearEntranceAndIntoBuilding`, `Sim.SwitchOutfitHelper.Wait` | EA design; the queue order is §9 |
| `CarpoolManager+GetInCarpool` (household) | mostly `queue` (the Sim was busy); the school / work outfit switch of its `Run` took 57-104 ms in the four household lines, so no long outfit wait for an on-screen household Sim in these runs [verified from the lines] | `GetInCarpool.Run` | no |

So for household Sims in these four runs: the two largest types are rides, the socials and child / bed types are EA's
sync waits (one Sim waits for the other by design), the rest are visible transitions. **No EA stall bug was found in the
top types.** The outfit build is confirmed in code as the wait before work / school / carpool trips, but the only long
waits seen are NPCs going to work; a household Sim's long outfit wait (the firefighter report, docs/681 §5) needs the Sim
away from the camera or a busy build queue, which these runs did not have.

### 8.1 The probe now measures the suspects directly

| Stamp | How | Shows as |
|---|---|---|
| ride / hidden | at `PushRunningInteraction` of the head: the Sim's posture is `SittingInVehicle` / `InRabbitHolePosture` | the instance ends there as `queued -> ride` / `-> hidden` (queue, wake, prerun kept, no preroute); report line `not idle at Run start` |
| outfit | `Sim.SwitchOutfitHelper.Wait` (stacked on `OutfitWaitHook`): time inside, per wait that slept (>= 1 ms) | per Sim kind p50 / p90 / max (`outfit` column, own n), per instance `outfit waits N (ms)`, per type `of that outfit N`, a line `actdelay outfit: N ms in SwitchOutfitHelper.Wait, <kind>, to <category>, on screen ..., in <interaction>` from `kActDelayOutfitLogMs` (200) |
| sync | `InteractionInstance.StartSync` (own swap), `Sim.WaitForSynchronizationLevelWithSim` and `SocialInteraction.ReceiveSocialInteraction` (first statements of `SocialReceiveFail.cs`'s hooks, the only swaps of those two); nested waits count once, plan time inside is left to the plan buckets, a wait after an animation act is not counted | `sync` column (instances with a sync wait), per instance `sync waits N (ms)`, per type `sync N` |
| names | the class of the last posture transition and of the first interaction run inside `Run` | `transitions 1 (468 ms, 0 plans, last Sit+StandUp)`, `ran inside 1 (first SitTransitionAngledToStraight)` |

The buckets still add up to queued -> act; outfit and sync overlap them. The time and the Sim's name are dropped from a
line that would pass the native log buffer (470 characters). `Wait` and `StartSync` keep a frame of ours on a sleeping task
(that time is what they measure); both have a stub, rows in `docs/data/swap-stack-audit.csv`. Counters 4280-4289:
outfit waits that slept / their ms / longest / household or selected / instances with a sync wait / their ms / Run began
in a vehicle / in a rabbit hole / long outfit waits with the Sim on screen / long outfit waits. Self-test: 211,901 checks
(cases 8-11 and the random stream cover the new events).

## 9. The outfit build queue: `[OutfitFirst]` (ts3native, default off)

**Where the swap waits** [verified: Ghidra decompile + objdump of the Steam 1.67 code, analysis copy]. Script side:
`SwitchOutfitHelper.Start` -> `Sim.SwitchToOutfitDelayed` -> `CASUtils.SetOutfitInGameObjectDelayed` (IC `0x5A9E90`);
`Sim.SwitchToOutfitWithoutSpin` reaches the same call. Native side:

1. `0x5ADD40` (the swap message, id `0x30290F0`) re-posts itself while the outfit's resources load or the Sim has no
   animation instance (docs/224 §5), then posts `0x6A4479F1` with the outfit object's provider interface (object `+0x28`)
   and the callback.
2. The model manager's handler `0x703CB0` -> `0x700150` -> `0x6F4910` -> `0x6DD160` (thiscall on the model proxy,
   `ret 0Ch`): stores callback and provider in "DelayedSwapParams" (proxy `+0x44`) and calls the provider's attach
   (`+0x18` = `0x5E6720`) with the Sim's scene object id.
3. The proxy's update `0x6DC5B0` asks the provider (`+0x10` = `0x5E4EB0` -> SimService `RequestSim 0x5EE910`) every step
   until the LOD is built, then runs the callback, which is the helper's `Ready`.
4. `RequestSim` puts a build job in the SimService's list; one job is built at a time, and when it is done `0x5EDB30`
   takes the ready job whose scene object is nearest the camera (thumbstall.cpp header). Its one call site is `0x5F12A5`.

So the swap's job competes by camera distance with every Sim LOD build. A Sim that changes clothes away from the camera
(a household Sim on another lot or floor, any townie) stands in `SwitchOutfitHelper.Wait` (`LoopIdle`) behind every
nearer Sim's build. That order is a choice of EA's that fits LOD builds; for a swap a script task is waiting on it is a
priority inversion, not a tuning value.

**What `[OutfitFirst]` does** (`thumbstall.cpp`, core `outfitfirst-core.h`):

- The call of `0x6DD160` at `0x6F492C` (18 bytes checked at `0x6F4926`) goes through `OfSwap`, which notes the
  provider's object when the provider's vtable has the RequestSim wrapper at `+0x10`, and calls the original with the
  same arguments.
- The pick's one call site (`0x5F12A5`, shared with `[ThumbStall]` Action 3 through `TsPickInstall`) runs `TsPick`:
  `Mode=1` calls the game's pick and files what it took (a noted object's job: how long it sat ready and how many jobs
  were built before it); `Mode=2` takes the oldest pending swap's ready job first, unlinked as the game unlinks its own
  choice. `[ThumbStall]`'s own pick still comes first.
- Same jobs, same builds, another order; the swap, its callback and the script side are untouched. Nothing is skipped,
  faked or cut, so every Sim is covered, household included (the visible result is the same change, sooner). A noted
  swap with no job after `MaxAgeMs` (60 s) is dropped: its LOD was cached or the swap was cancelled. 32 swaps at most
  are held (the oldest is pushed out).
- Not built: starting the build when the interaction is queued. The only script way to build without applying is a
  second `SwitchOutfitHelper.Start`, and EA's own helper then aborts ours (the build restarts) or loses its callback
  while ours is pending (docs/224 §5, the one-callback table); not exact for a household Sim.

Ini (`ts3native.ini`): `[OutfitFirst] Enabled=0` (1 = install), `Mode=1` (1 measure, 2 build first), `LogMs=100`,
`MaxAgeMs=60000`. No runtime switch, no counter version, no exported block: log lines only.

Lines: `OK   outfit first ...`, `outfitfirst: swap job taken first | taken by the game's order N ms after the swap was
set up; ready for N ms, the game built K other jobs meanwhile; ...` (from `LogMs` ready, or passed over), and a summary
once a minute: `outfitfirst: mode N, swaps noted N (again, pushed out, no job), jobs taken first N, by the game's order N,
other jobs built past a ready swap N; ready ms in all N (longest N), set-up to build ms in all N`.

Checks: Linux host test of `outfitfirst-core.h` (`outfitfirst-host-test.cpp`, clang `-m32`, exit 0);
`TestOutfitFirstCore` (`outfitfirst-test.h`) and `TestOutfitFirstPick` (`thumbstall-test.h`: the swap hook's argument
pass-through, off / Mode 1 / Mode 2 on a fake ready ring, `[ThumbStall]`'s pick first) in the Windows selftest.
EA 1.69 and retail 1.67: three rows (`0x5E4EB0`, `0x6DD160`, `0x6F4926`) are unresolved (0), the feature logs SKIP
there.

Repro `outfitfirst-check`: the agent's `outfit_change` (EA's `SwitchToOutfitWithoutSpin`, the same delayed swap) on the
household and then on townies over the town, with the job ini at Mode 1 (off arm) or Mode 2 (on arm).

### 9.1 Results (natural play, Isla Paradiso) [measured]

Contributor rig: Linux, Wine + DXVK. Isla Paradiso, 6-minute calm natural-play soaks with the `actdelay` probe on (it
times every `SwitchOutfitHelper.Wait`). Single runs per arm; the two arms of a pair run the same save and keys.

| pair | run | mode | time swaps sat ready (longest) | other jobs built past a ready swap | outfit wait, all Sims p50 / p90 | household max | selected max |
|---|---|---|---|---|---|---|---|
| 1 | s233 | 1 (measure) | 6,327 ms (2,607) | 8 | 480 / 1,408 ms | 1,449 ms | 994 ms |
| 1 | s234 | 2 (first) | 3,988 ms (1,570) | 0 | 176 / 1,152 ms | 804 ms | 97 ms |
| 2 | s236 | 1, `[JobYield]` on | 7,255 ms (2,967) | | | | |
| 2 | s237 | 2, `[JobYield]` on | 2,472 ms (1,660), **-66%** | | | | |

- Hitches (frames >= 300 ms): pair 1 0 / 2, pair 2 3 / 1; they swap sides between pairs and are sim-CPU frames, not
  outfit builds: no link to the mode.
- The ready time left in Mode 2 is a swap whose job became ready while another swap's job was being built (one build
  at a time); the rest of the outfit wait is the build itself (resource load + model build, docs/42 §4), which this
  feature does not touch.
- Verified: the call chain and the pick site (Ghidra + objdump); the order change in the log lines (Mode 2 `taken
  first`, 0 jobs past a ready swap). Inferred: the gain on other worlds (not measured).

## 10. Run plan (the game is not started by this branch)

Packages: `build/ts3fixes.package` of this branch (as `pkg/ts3fixes.idle-1120.package`) and an MSVC CI `ts3native.asi` of
this branch. As `~/dev/ts3-runs/s223-run.sh` (storm 2 min + calm 6 min, same keys, `actdelay` in `MF_EXTRA`), two pairs:

1. **off** (measure): ini `OutfitFirst.Enabled=1 OutfitFirst.Mode=1` added to the keys.
2. **on**: `OutfitFirst.Enabled=1 OutfitFirst.Mode=2`.

In the calm phase of each, once around sim hour 7.5 and once in the evening, call
`reflect_call Ts3Fixes.Fixes.ActDelayCheck ["outfit"]` twice 30 s apart (the trigger, with the camera on one Sim), and
fetch `ActDelayCheck result` near the end as before.

Read first that the measurement works: `OK   outfit first` with the mode, the hooks line has `SwitchOutfitHelper.Wait`,
`StartSync` and `ReceiveSocialInteraction` above 0, errors 4275=0, `GoToSchoolInRabbitHole` / `GoHome` lines now say
`queued -> ride`. Then:

- outfit: `outfit` column per kind and `actdelay outfit:` lines, off against on; `outfitfirst:` summary: off arm
  `ready ms in all` and `other jobs built past a ready swap` (the gain available), on arm `jobs taken first`.
  Verdict: on arm p90 / max outfit wait of household and NPC below the off arm by about the off arm's ready ms; no
  new exception, no missing outfit (every Sim wears the asked outfit after the trigger). Ready ms 0 in the off arm =
  N/A for the fix (the wait is the build itself); then the next lead is the build time (docs/42 §4), not the order.
- sync: the `sync` column and per-type `sync` against `preroute` of `SocialInteractionB`, `ChildPlaceholderInteraction`,
  `Bed_ReadToSleep`: sync ~ preroute confirms the design waits; a `preroute` far above sync + outfit names what is left.
- names: the transition class of the 430-650 ms `prerun` group.

