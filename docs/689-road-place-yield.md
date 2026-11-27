# 689: the remaining route-plan hitches of the 30-min heap-512 soaks; Bridgeport's is the one docs/800 `jobyieldroad` fixes

2026-11-25. Logs: 30-min heap-512 soaks s560c/d, s562a-c, s570a-f (test-branch asi, `[HitchAttr]`, `[StallWatch]`,
ufplan / ufplansplit lines), decompiled C#, our native source. Docs only, no code: the top routing cause is already
fixed upstream (docs/800 `jobyieldroad` + `jobyieldroadow`, ON since 2026-11-19); the soaks ran on a test branch that
predates that import. §6 re-measures docs/800 on that branch.

Not redone here: docs/142 (early-out), 164 (phase split), 202 (unseen fallback), 640 (disc yield), 672, 676, routedefer,
RouteAsync, crowdport, docs/682 fglinbound.

## 1. Method (token-free)

Per hitch frame >= 300 ms: the native `hitch: frame` split (sync plan ms by phase world / water / post / insert / cell /
lot, job wait ms and site, lock918, gc, cpu), the stall watch's innermost script frame, and the ufplan / ufplansplit
lines of the frame (`pre / wait / post`, polls). Per run: every `route jobs: sim waited N ms for a job at <site>` line by
site, own/foreign job and "task could yield". Scripts: `h150.py` (ts3-runs) for the old labels, a cause split per frame
(scratch, not committed).

## 2. Classified table

| World, runs | Label in h150 | Who / for what | Phase, thread | Real cause | Frames >= 300 ms, ms per run |
|---|---|---|---|---|---|
| Bridgeport s560d, s562a-c | "slow plan: marked" (6-14 a run, 300-700 ms) | unseen NPCs (`why 0 marked`), kind 0 async plans, own task, short trips (dist 9 m...) | worker job; the ufplan wall time is `pre 0 wait N post 0`, 1 poll | **victims**: they wait through a frame that the sim spends in the next row | - |
| Bridgeport s560d, s562a-c | (same frames) | `World_FindPlaceOnRoadOffScreen`: `TrafficManager` parked-traffic spawn, `CarNpcManager.FindOffScreenPlaceOnRoad` (taxis, NPC cars), `Service`, food / ice-cream trucks; non-Sim tasks mostly | **sim thread**: the call's own plan enters our `RunPlan`, finds the sim's own async job running (`A_RUNNING`, owner = sim thread) and waits in `WaitAsyncDone(SW_RUNPLAN)`; "task could yield yes" | **top cause** (§3) | 22 / 16 / 19 / 31 frames, 8.8 / 6.5 / 7.0 / 14.0 s; all such waits 480 / 467 / 473 / 480, 30.8 / 27.3 / 28.0 / 33.6 s |
| Bridgeport s560d | "other (fglinbound)" 1.4 s | `FindGoodLocationNearby` for a `SocialJigTwoPerson` | sim thread, placement, leaf `World_FindGoodLocation` | docs/682's lot-wide indoors stage (fglinbound was off in s560) | 1 frame, 1.4 s |
| Sims University s560c, s570a-c | "slow plan: own task" 0.9-1.2 s | NPC async plans (`why 4`, Ann Conners, dist 268 m) | worker; `pre 0 wait 1182 (2 polls)` | **victims** of script-CPU frames (`DreamsAndPromises`, `Query_GetObjects` after `Skewer.OnHouseholdChanged`); sync plans of those frames <= 173 ms | 0 routing frames |
| Sims University s560c | "other (crowdport)" 1.4 s | class end at 2 PM, crowdport rabbit-hole exits | 22 sync `PlanFromPoint` plans, 116 ms (cell 59, post 36) | mostly script CPU (`DreamsAndPromises`); routing ~8% of the frame | 1 frame |
| Sunset Valley s570e/f | - | runplan 20 / 18 waits, 1.5 / 1.1 s; disc 140 / 100, 7.4 / 4.8 s | - | small; disc = docs/640 | - |

So in these soaks only Bridgeport has a routing-caused hitch class that matters, and it is not a plan's own cost: it is
the sim thread waiting for a job.

## 3. Root cause [verified in our source; caller set verified, decompiled; leaf share measured]

`World.FindPlaceOnRoadOffScreen` (SimIFace) -> `World_FindPlaceOnRoadOffScreen` (native) plans on the calling thread.
Our planner hook `RunPlan` (ts3native.cpp): when `g_async.state == A_RUNNING && me == g_async.owner` and the plan is not
nested in the job's managed call, it waits for the job (`WaitAsyncDone(SW_RUNPLAN)`), because the job holds the routing
lock (+0x918). The job is one the sim itself started a moment before (a Sim's async plan, often 300-700 ms on Bridgeport's
large graph), so the sim thread sits idle for the rest of it. JobYield (docs/183, RouteBackoff.cs `StepAside`) avoids
this wait for `Route.Plan` paths by sleeping the task on JOBBUSY, but `FindPlaceOnRoadOffScreen` never goes through a
Route object, so nothing yields. Stall leaf of the waits >= 150 ms: `World_FindPlaceOnRoadOffScreen` 52 of 60 (s560d),
56 of 66 (s562c). Bridgeport has the most traffic (`GeneratedTrafficTuning`, taxis), which is why the class is
Bridgeport's.

## 4. Fix: none needed upstream

Upstream already carries the fix: docs/800 `jobyieldroad` + `jobyieldroadow` (JobYieldRoad.cs, ON since the import of
2026-11-19). The soaks above ran on a contributor test branch that predates that import. A first design written before
this was noticed (`roadyield`: sleep before `Sims3.SimIFace.World.FindPlaceOnRoadOffScreen` on JOBBUSY) was dropped before
it ran: it is `jobyieldroad` again without the car fix docs/800 found necessary (a `CarNpcWithNoDriver`'s LiteDriver task
destroys its out-of-world car during the sleep). The test branch got a port of the road and traffic sites (same code,
default off there, its own counter numbers) for the A/B below.

## 5. A/B (s598)

Cloned from the s560 soak script: heap 512, 30 min, seed 156, new games from world files; a / b Bridgeport off / on,
c / d Sims University off / on (on = `jobyieldroad jobyieldroadow`, the rest of the setup unchanged).

## 6. Results (s598, 2026-11-26)

Both Bridgeport games ended early: the off arm (s598a) with an access violation at 0xF6BA804F (main thread, outside our
modules) after 25 min, the on arm (s598b) as an autotest HANG after 20 min; the harness then started a short second game
whose log replaced `ts3native-full.log`, so the numbers come from the autotest copies (`auto-*/cycle-1.log`), cut at the
"memory at 20 min" line for an equal window. Address space was as tight as in the 30-min runs that survived (s560d, s562c:
22-25 MB free, largest block 0-2 MB), so neither stop is tied to the switch yet; an unexplained stop keeps it default off.

| First 20 min, Bridgeport | s560d | s562c | s598a off | s598b on |
|---|---|---|---|---|
| sim waits at runplan (own job) | 295, 17.5 s | 305, 18.9 s | 296, 16.3 s | **15, 1.1 s** |
| of them >= 150 ms | 25, 5.7 s | 24, 6.2 s | 17, 3.7 s | **2, 0.5 s** |
| of them >= 300 ms | 3, 1.0 s | 6, 2.6 s | 3, 1.0 s | 1, 0.4 s |
| stall leaf `World_FindPlaceOnRoadOffScreen` | 89 | 110 | 70 | 6 |
| hitchwatch frames >= 300 ms | 12, 5.2 s | 15, 6.1 s | 5, 1.9 s | 2, 0.8 s |
| storm waves | 110 | 110 | 110 | 101 |

Sims University (s598c off / s598d on, full 30 min): no runplan waits >= 150 ms in either arm; h150 16 hitches 14.7 s vs 18
hitches 13.3 s, all script-CPU frames; as expected, the switch does nothing there.

Proof status: one Bridgeport A/B pair, sim-thread job waits at runplan -95%; not yet stable over a full 30 minutes (both
Bridgeport arms stopped early, cause open). Upstream has had the same code ON since 2026-11-19 (docs/800).
