# 803: step-asides B (jobyieldfpend, jobyieldpark, jobyieldstart, obstacleasync, interruptasync) and their ts3native parts (draft, needs test)

Answer: five default-off ts3fixes switches that keep the sim thread from waiting for a running async route job at five more places, with two ts3native parts: `DISCDUE` (a GetOption2 query, no ini key) and `[RouteAsync] ObstacleKind1` (ini key, default 0). Nothing here has been judged in an in-load A/B yet; the PR is a draft until the test in section 4 is run.

## 1. What each does

- `jobyieldfpend`: step-aside (docs/183 `StepAside`) before `RoutingComponent.UpdateDynamicFootprint(owner, true)`, the footprint switch-on at the end of a route (`DoRoute`'s end, `BoatFollowSubPathRouteAction.PerformAction`). The switch-on waits for a running job (a forced discourage drain). Townies only: a Sim owner and every Sim its posture routes with it, or a boat and all its occupants; any household, selected or unknown-scope member keeps EA's call. Counters [3427-3429].
- `jobyieldpark`: the same before `ParkingSpace.ParkVehicle`, whose `ParentToSlot` waits for a running job. Only for a townie arriving home on its own task (stack check for `Sim.InternalOnSetLotCurrent`), every occupant a townie, not on the active household's lot or a lot an active household Sim is on, not a food or ice cream truck. The space is reserved for the car before the sleep, and the reservation is put back on every early exit (car or space destroyed, space taken, task reset). Counters [3430-3435].
- `jobyieldstart` (needs ts3native `DISCDUE`): before the own START and after the collect (the CANCEL), a townie's task asks DISCDUE: "would this poll force a drain that waits for the running job?" If yes it sleeps while JOBBUSY (cap 10 s), so the drain finds no job. Without the ts3native part the game answers false and nothing changes. Counters [3436-3439].
- `obstacleasync` (needs `ObstacleKind1`): a townie's `Replan` made while its current route action is exactly EA's `ObstacleEncounteredRouteInterruptAction` carries a tag (probe channel bits 10 + 11, a label only) and runs as a ts3native job; the Sim, already stopped, sleeps a tick at a time. `interruptasync`: the same for the Destination-Obstructed, Change-Destination and Replan interrupt actions. Counters [3440-3445].
- ts3native `[RouteAsync] ObstacleKind1=1` (default 0): an own `Replan` START starts without Kinds bit 1 only when the route carries the tag and its scope answer is unseen (2) or seen NPC (3); household, unknown and untagged Replans stay sync, and with kind 1 in neither Kinds nor ForeignKinds every other `Replan` call goes straight to the game's IC (as unhooked). Counters (spare slots `hgSpare[23..25]`): tagged STARTs accepted, tagged STARTs refused at the scope gate, pass-through IC calls.
- ts3native `DISCDUE` (`discourage.cpp`): answers 1 only when the job owner thread asks, `[RouteAsync] JobYield` is on, a job runs and the sim thread may yield, the oldest deferred footprint record is at least 250 ms old (the age at which the poll forces a drain). No side effects. Logged as "disc due: asks N due M".

## 2. Scope and household rule

Every managed switch asks `StepTownie` (an NPC Sim of a known other household); household, selected, unknown-scope and household-less Sims keep EA's behaviour. ObstacleKind1 refuses household and unknown scope natively as well. Off: the hooks and the native paths are not taken (DISCDUE and the tag answer false on a build without them).

## 3. Evidence [natural-play games, one machine; the Bridgeport rtwait pair re-read from the native end-of-game lines, the rest from the run notes]

- Bridgeport apartment save, game jj1 (without these) against jk2 (with all five on, plus three other local changes that are not in this PR: the `UofDefer` native key, `fakemetaseen`, `routablecache`): rtwait 11.4 s to 2.9 s. The inventory of those runs gives the same direction on Twinbrook (4.05 s to 0.67 s) and Isla Paradiso (4.67 s to 0.39 s), not re-read here. These are bundled runs: no single switch is isolated.
- `jobyieldstart` started 35-156 times per game (START asks answered due) with 4.6-28.6 s of sleep; `jobyieldfpend` asked 7.7-9.2k times, slept 2.4-7.2k, 244-1080 s of task time slept per game (that is task sleep, not sim-thread stall: check that Sims are not held, see 4).
- `obstacleasync` alone (in-load A/B oa1, 2 games, new Isla Paradiso save): freezes >= 300 ms 26.4 against 32.7 per 10 min, >= 1 s 0 against 0: no gain visible, the obstacle phase there is 0.1-0.3 s per 10 min. It is the base of `interruptasync`; the interrupt actions were not measured apart.
- Crashes: none tied to these in the games above.

## 4. Deciding test (not run yet)

Bridgeport and Isla Paradiso in-load A/B of the four ts3fixes switches `jobyieldfpend`, `jobyieldpark`, `jobyieldstart` and `interruptasync` (with `obstacleasync` on in both arms) as one `--ab` group, two games each, both orders: rtwait and disc ms, freezes >= 300 ms and >= 1 s, and a check that townies are not stalled (`jobyieldfpend` slept 1080 s of task time in one Bridgeport game). The household variants stay off in both arms.

## 5. Risks

- `jobyieldpark` writes a private field of `ParkingSpace` (`mReservedVehicle`) before sleeping; the restore paths are listed above, a mod that replaces `ParkVehicle` is not covered.
- `DISCDUE` and the tag read native state the sim thread owns; they are sim-thread only and read-only, but they are new GetOption2 channels (optHi `0x8A000000`).
- `ObstacleKind1` uses three spare counter slots instead of a named counters block.
