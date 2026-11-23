# 641. Slow spot checks are frame witnesses, not the hitch

Status: built 2026-11-18 (r104-spot), NOT RUN. Switch `spotwaitattr` (default off, log and counters only), counters
4662-4665 (moved from the contributor branch's 3860-3863, which main uses). The `ufplansplit` dist fix is a bug fix inside that log switch (no new switch).

## 1. Question

Tonight's runs show 15 `ufplansplit` lines over 250 ms for SafeSpot checks (`src spot:<caller>@<lot>`, kind 2) in
8 runs, up to 683 ms, with `dist` from 221 to 1326 m, e.g. phase-20261118-0145:
`ufplansplit: kind 2 683 ms, pre 0 wait 683 (1 polls) ... src spot:rabbithole@Flying V's Coffeehouse ..., dist 269`
followed by `hitchwatch: 665 ms`. Did the check block the sim thread for its job, and why is dist so long?

## 2. dist was measured from the wrong point (our log bug)

- [verified, decompiled] EA `Sim.IsPointInLotSafelyRoutable(lot, pos)` builds an adult route, adds 8
  `RadialRangeDestination`s (the 4 lot corners and 4 edge midpoints, radius 1 m, 2 m on a houseboat lot) and calls
  `route.PlanFromPoint(pos)`. The plan runs from the spot inside the lot to that lot's own edge.
- [verified, code] `UfSplitLog` measured dist from the follower Sim's current position to
  `GetApproximateOriginalDestination()`. For a SafeSpot check the Sim is still wherever it was (at home, on the road),
  so dist was the Sim's distance to the target lot, not the plan's length. Neither the plan origin nor its goal is wrong.
- Fix (in `ufplansplit`): for kind 2 / 3 dist is now from the plan's start point, with `(sim N m away)` for the old value.

## 3. The check yields; the frame is long elsewhere

- [verified, code] The check does yield: `AsyncWait` sleeps one frame per READY poll (`Simulator.Sleep(0)`). "1 polls"
  means the job was READY at the first check after one frame; the wall time is that frame.
- [verified, logs] The job is short. 0145 has only 2 `route async: long job` lines (>= 200 ms) in the whole run, none
  for this check, and the periodic `route jobs sim waits` max is 32-47 ms: the sim thread never waited on a route job
  longer than that. So the 683 ms is the rest of the game frame, not this job.
- [verified, logs] Other plans in the same frame read the same time: phase-20261118-0045 tick 958567 has kind 0 plans
  of 695 / 431 / 318 ms and spot checks of 507 / 322 ms, all 1 poll.
- [verified, logs] The Starlight 11:27-11:54 AM stall exists without any spot check: phase-20261117-2258 has
  `hitchwatch: 622 ms, tick 25774, 11:27 AM` with no spot plan; 2116 has 622 ms at 11:54 AM with a watchdog check as the
  witness; 0145 has 665 ms at 11:28 AM with the spot check as the witness. The same `src watchdog` 624 ms line is one.
- [inferred, logs + decompiled] All three are followed at once by the first `routefail job: ... Tend target
  ShowStageLarge` line: the stage proprietor's `ShowStage.Tend.Run` (route to the lot, then `InitSimFest` /
  `SetupSimFest` when a SimFest is scheduled). The stall is likely the SimFest setup on the sim thread. Not measured:
  next lead for that hitch.
- [inferred, code] spotcut's deadline does apply: these are spotasync jobs (only unseen NPCs, `H4Core.SpotAsync`: never household,
  selected or unknown scope), sent with the unseen-fallback MARK (`SpotCutMark`), so ts3native caps them at the unseen deadline (220 ms less
  learned overrun); none was cut because none ran that long (no long-job line, no `cut` counter rise to check yet). Nothing in spotasync / spotcut / spotedgememo is wrong here.

## 4. spotwaitattr (default off)

Log only. For every kind-2 check inside `IsPointInLotSafelyRoutable` (spot, watchdog, EA's own callers) whose async wait
is >= 250 ms, `AsyncWait` records the longest single poll; the wait is frame-bound when one poll holds >= 80 % of it
(the stall is elsewhere in that frame), else job-bound (the job itself ran long over several frames).
`ufplansplit` lines also print `longest N` next to the poll count.

Counters: [4662] waits >= 250 ms, [4663] frame-bound, [4664] job-bound, [4665] frame-bound ms. Line `spotwait:`
(60 a session). Household rule: no behaviour change, nothing to exempt.

## 5. Test

Package `ts3fixes.r104-spot.package`. Starlight Shores, Sunday 11:00 AM-12:00 PM, `spotwaitattr` and `ufplansplit` on.
Check: `spotwait: frame-bound` lines at the 11:27 AM stall (expected: frame-bound, longest poll ~ the hitchwatch ms),
`spotwaitattr_job` near 0, and `ufplansplit` kind 2 dist now a few m `(sim N m away)`.

## Starlight 11:27 AM hitch, stall stacks (2026-11-18, s105, asi aa011899 `StallWatch.StallMs=250`)

The sampler was live (`stall watch ... after 250 ms ..., poll 83 ms`) and logged sub-second sim-thread stalls during load, yet logged nothing at the 589 ms hitch (Sun 11:26 AM) or the 562 ms one (8:32 AM). Simulate iterations kept finishing, so the time went outside the sim thread's Simulate loop: inferred to be the main thread (likely the ShowStage / SimFest asset setup; the first `routefail job: ... Tend target ShowStageLarge` follows each time). Next step if it is worth chasing: a main-thread sampler; the sim-thread one cannot see it.

## Addendum: Isla Paradiso night with ms stall stacks (s106, phase-20261118-0404)

asi aa011899, `StallMs=250 EveryMs=100 Lines=4`, r103 package, unseenboatland off. 9 game hitches (300-384 ms), 7 of them at
Sun. 11:16-11:34 PM. **No sim-thread stall line at any of the 11 PM hitches** (verified: interleaved hitchwatch and stall
lines in log order). The only stall near the night window is 276 ms at Mon. 1:02 AM: RouteGate site 7 "Q reset (clear
path)" 0x61E420 (stub `RgStub7` -> RgEnter -> spin/backoff on the gate lock) waiting for a running async route job (known
gate wait, one event).

Caveat (inferred): hitchwatch measures the gap between two ticks of a script task, so a 300-380 ms gap can be two
consecutive sim iterations of ~150-190 ms each, under the 250 ms stall threshold. Next: s107 repeats with `StallMs=150`
(the floor) before concluding the IP night hitches are off the sim thread.

## Addendum: IP night at the 150 ms floor (s107, phase-20261118-2356)

Same setup as s106 with `StallMs=150 EveryMs=50 Lines=3`. Game hitches 312-388 ms at Sun. 10:51, 11:02, 11:11 and 11:16 PM.
**Zero sim-thread stall lines in the whole 10:45-11:20 PM window** (log lines 1850-2060), so no single Simulate iteration
reached 150 ms there. A 300-390 ms gap between hitchwatch task ticks with every iteration under 150 ms means the hitchwatch
task skipped at least two iterations, i.e. the script scheduler's time slice was used up by other managed tasks for 2-3
iterations in a row (inferred), or the frame wait between iterations ran long. Either way it is not a native sim-thread
stall that a stack can show. Next tool, if this is chased: a managed per-iteration task census (which Mono tasks ran in the
iterations of the gap), not more stall stacks. Stall lines elsewhere in the run: RouteGate site 7 waits again (174-192 ms),
plus long iterations at load and exit (519-1285 ms) outside play.
