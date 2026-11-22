# 686: household Sims standing at a stair top or doorway: EA's obstacle stand-and-wait never pushes or yields once its replan fails

2026-11-21, branch `fix/stand-wait-1121`. Storm logs s141-s291 (Isla Paradiso, Collins household, 2-minute route storms) +
decompiled C#. Built, default off, **NOT RUN**. Switches `standwaitfix`, `standwaitfixhh`, `standwaitlog`; counters
4300-4319; `StandWait.cs`, `StandWaitCore.cs`; repros `standwait-check`, `standwait-landing-crowd`.
§7 (branch `fix/privwait-1121`): waits at a privacy-locked room, switches `standwaitlocked`, `standwaitlockedhh`, counters
4340-4351, `StandWaitLocked.cs`, `StandWaitLockedCore.cs`, repro `standwaitlocked-check`. **Correction to §1-§2:** the
wait is 20 sim-minutes, not 50 (§7.1); "50" below is the default in the code.

## 1. What the logs show

50 loops (one Sim, one route, 4 or more `Replan ... FailedBecause...` routefail lines) in 32 of the runs s141-s291; 44 of
them are the five Collins Sims on their home lot, most at the second-floor stair top and landing (927-929, 58, 1203-1208).
The loops are in runs without any of our route fixes too.

| Plan result of the loop | Loops | Of them 11 or more replans |
|---|---|---|
| NoPathToDestination, blocker a Sim | 11 | 6 |
| NoPathToDestination, blocker a social jig | 4 | 2 |
| NoPathToDestination, blocker another object (sink, stairs, region footprint) | 7 | 6 |
| NoDestinationSpecified, no blocker named | 15 | 5 |
| DestinationPortalLocked | 12 | 9 |
| NoPathToDestination, no blocker | 1 | 1 |

- **Length [verified, counts; time inferred].** Replan counts peak at 13-14 (21 of 50 loops; 29 have 11 or more). The storms
  run at about 7.5 sim-minutes per real second (hour stamps of the `actdelay` lines against their ms stamps), and the
  bounded loops last 3-7 real seconds = 25-50 sim-minutes. 13-14 cycles of about 3.7 sim-minutes is EA's 50-minute
  wait: these loops did not end because the way cleared, they ran out. At normal speed that is a Sim standing for
  about 50 seconds; the short loops (4-8 replans) are 15-30 seconds.
- **Who blocks whom [verified, blocker ids against the Sims' follower ids].** Every named Sim blocker is another
  household Sim. Three groups loop at the same time, one replan each per cycle, in step:
  - s219: Rosemarie (Bed_ReadToSleep) and Cristobel, 0.1 m apart at the stair exit 927.4,58,1202.7, each the other's
    blocker, both 13 replans;
  - s233: Christopher (14), Cristobel (13) and Rosemarie (16) within 0.3 m at the same exit, blockers each other;
  - s248: Christopher (Fridge_EatLeftovers, about to go down) stands 0.36 m from the centre of a social jig placed at
    the stair top; Rosemarie's route into that jig fails because he stands in it, his own replan fails because the jig
    covers the stair's entry; both 13 replans.
  - s291: Christopher's way down is blocked by Martina (toddler), whose own BePushed route failed on Rosemarie.
- **DestinationPortalLocked [verified goal, cause inferred].** Ten of the twelve have goals 930-934,58,1201-1202 (the
  bathroom, the goal of `UseToilet` in two other loops): a door locked while the Sim was on its way. No Sim to push.

## 2. Why the loop lasts [verified, decompiled `Sims3.Gameplay.Routing`]

- `ObstacleEncounteredRouteInterruptAction.PerformAction` (inserted by `SimSteeringAdaptor.HandleEventEncounteredObstacles`)
  turns the Sim's own footprint on, sets "blocked by people" on the interrupted route and runs a
  `StandAndWaitController` for `kDefaultStandAndWaitDuration` = 50 sim-minutes. One cycle = one `wait_ping` / `wait_pong`
  state of the StandAndWait machine, then the cycle handler.
- The handler, `StandAndWaitCycleHandler`: when the route's plan has failed it replans, and **when that replan fails it
  returns "keep waiting"**. Everything after that return is skipped for the cycle: the test for
  `CancelExternal | MidRoutePushRequested`, `ShouldIgnoreAllObstacles`, `CollectObstacles` and the `PushSims` of the
  pushable Sims in the way. The check frequency is 1, so this repeats every cycle.
- A waiting Sim counts as a pushable obstacle for others (`CollectObstacles`), and a push of a routing Sim is only a
  request (`RequestMidRoutePush`: a flag and the `MidRoutePushRequested` exit reason) that the pushed Sim's own route
  code must notice.
- So: two Sims within each other's footprint (`IsSimAvoidable` is false under 0.3 m while the other waits) both enter
  the wait; with "blocked by people" each one's replan fails on the other; from then on neither pushes, neither sees a
  push, and a player's cancel is not seen either. Nothing can end the wait but the 50 minutes. Then the action ends
  with `Terminate` (route failed), unless EA's crowd rule applies (3 or more lot obstacles and 4 or more Sims within
  1 m: `StartIgnoringObstacles` and walk on).
- The sibling `DestinationObstructedRouteInterruptAction` tests the exit reasons first and pushes the Sims at the
  destination (radius 0.6 m) every cycle, before its replan. The order in the obstacle action is the bug.
- What each cycle waits for: nothing it can get. Each cycle is one more failed replan (the `Replan` routefail line).

Not checked in native code **[inferred]**: that a plan with "blocked by people" ignores the planning Sim's own footprint
(the managed side treats its own id as "no obstruction" in `DestinationObstructed`). The `standwait:` lines of an off
run answer it: a wait with blocked cycles, no Sim named and a goal under 0.3 m away would be a self-block.

## 3. Fix (`StandWait.cs`, rules in `StandWaitCore.cs`)

Hooks: `StandAndWaitCycleHandler` (runs EA's body first) and `PerformAction` (begin / end of a wait). A cycle counts as
**blocked** when the plan had failed before EA's body, EA answered "keep waiting" and the plan is still failed: EA left
through the early return. Only then, in this order:

1. **Yield.** The Sim has `MidRoutePushRequested` or `CancelExternal`: EA's own exit (cancel without push = `Terminate`,
   `CancelledByScript`, elected to exit). A pushed Sim then steps aside in EA's `MidRouteBePushed` and replans; a
   cancelled route ends.
2. **Pass.** From blocked cycle `kStandWaitPassCycle` (2; one cycle later, once, when a push of ours was accepted the
   cycle before) and then every `kStandWaitPassEvery` (3) cycles: one replan without "blocked by people". If it
   succeeds, only people were in the way: `StartIgnoringObstacles(kStandWaitPassMinutes = 5)` and
   `ContinueAndFollowPath`, EA's crowd exit without the 50 minutes. If it fails (locked door, object, jig) the option
   is set back and EA's wait goes on. Never when a Sim other than the waiter's own interaction partner stands within
   0.6 m of the destination point (the Sim would end inside it).
3. **Push.** EA's `PushSims(list, false)` for: the plan's blocker when it is a Sim, Sims within 0.6 m of the route's
   destination point (EA's radius in `DestinationObstructed`) and within 0.5 m of the waiter (EA's end-of-route
   radius). Not pushed: the waiter's partner (target or linked actor, either way round), Sims EA calls not pushable,
   Sims out of scope. **Mutual wait** (the blocker is itself in a blocked run and named the waiter last cycle): only the
   higher-ranked Sim pushes (interaction priority, then the selected Sim, then the lower object id); the other
   withholds its push and yields at rule 1 on its next cycle.

No teleport, no skipped check, no shorter EA timer: every action is one EA takes itself in the same handler or its
sibling. A wait whose plan fails without people keeps EA's 50 minutes (the door and object rows of §1), except that a
player's cancel now ends it.

**Scope.** `standwaitfix`: Sims the player does not control. `standwaitfixhh` in addition: household and selected Sims,
as waiter and as pushed Sim (`Fixes.HouseholdExempt`; `[Experimental] HouseholdToo=1` lifts it as well). The result for
a household Sim differs from EA's (it moves after one or two cycles instead of standing 50 minutes), so it has its own
switch.

**Compare mode.** The hooks are always in. With the switches off nothing changes and the counters record blocked
cycles, waits that ran to the timeout and what the rules would have done (`would yield` / `push` / `pass`).
`standwaitlog` writes one line per wait that had blocked cycles:

`standwait: <Sim> <selected|household|npc>, <interaction>: blocked N of C cycles, M sim-min, at x,y,z, plan <result>
blocker <type>[, mutual with <names>], end <yield|pass|replan ok after push|timeout|other>, fix <on|off|exempt>: ...,
timeout cycles left L`

`timeout cycles left` = the cycles the wait had left to EA's timeout at its own pace; summed for resolved waits in
counter 4316 it is the upper bound of the cycles saved (the blocker might have left earlier on its own).

Counters: `docs/registry.md` 4300-4319.

## 4. Checks (2026-11-21, no game)

- `node tools/selftest/standwait/run.ts`: the decision table (yield, cancel, partner, scope, destination, mutual order,
  strict order of `Outranks` on 2000 random pairs), the pass schedule, the end kinds, and a model of 2-4 Sims in one
  spot run cycle by cycle in random task order: EA's rules = 13 cycles, with the rules everyone is through within
  Sims + 2 cycles (500 runs each).
- `StandWaitCheck run` (repro `standwait-check`): off `BROKEN`, on `FIXED`, with controls.
- ts3fixes builds (`build-fixes.ts`), `refs-resolve-check` 0 unresolved; `selftest-all.ts --ci` pass.

## 5. Not covered, open

- Door locked on the way (12 loops, 9 long): EA's wait stays. Ending it with EA's door route-fail after a few cycles
  would shorten EA's own timer; not built.
- Object or jig in the way with no Sim to push (7 + part of 4): EA's wait stays.
- `NoDestinationSpecified` with no blocker named (15): covered only when a Sim stands at the destination or in the
  waiter; the off-arm `standwait:` lines will show how many.
- The cycle length (one wait animation, about 3.7 sim-minutes) is EA's; the fix acts on the first blocked cycle, so a
  pushed Sim moves one cycle after the jam forms, the pusher one cycle after that.
- The stand-and-wait inside a push route (`MidRouteBePushed`; repro `upstairs-landing-jam` arm c: push routes
  replanned 15-18 times) is the same action and gets the same rules, but such a Sim cannot be pushed again
  (`IsInvolvedInPush`), so there it is rule 2 that ends it.

## 6. Run plan

Package `ts3fixes` of this branch (no native change). Storms as s266 (`s266-run.sh` keys, `STORM_SEED=152`,
`STORM_MIN=2`), `standwaitlog` on in both arms:

- off arm: `MF_EXTRA="... standwaitlog"`; on arm: `MF_EXTRA="... standwaitlog standwaitfix standwaitfixhh"`;
  interleaved off / on, 8 or more each.
- repro `standwait-landing-crowd` (four arms in one load), twice; `standwait-check` once.
- Read: household loops per run and their lengths (`loops.py`), `standwait:` lines (end kinds; off arm: `would ...`),
  `StandWaitCheck result`.
- Pass: on arms have fewer loops of 4 or more replans and none of 11 or more with a Sim or `mutual with` named; every
  on-arm `standwait:` line with a Sim named ends `yield`, `pass` or `replan ok after push` within 4 blocked cycles;
  errors 0; no new routefail kinds, stuck Sims, crashes or `route false` beyond the off arms; no teleport (none is in
  the code; the `gohere end` lines stay `route ok`).

## 7. Waits at a privacy-locked room (`standwaitlocked`, 2026-11-21, `fix/privwait-1121`, built, default off, NOT RUN)

### 7.1 What the logs show (`standwait:` lines of s294-s365, household and selected Sims)

| Interaction on the line | Blocked cycles | Sim-minutes | End |
|---|---|---|---|
| `SocialInteractionA` / `B` target Sim (s294, s341 Rosemarie) | 10-13 | 21-22 | `timeout` |
| `none` (s298 Cristobel, s295 / s336 Omar), standing at 927.5,58.0,1203.3 | 8-13 | 15-27 | `timeout` (s295: the lock ended in cycle 8) |
| `PillowFight` / `PlayTag` target Sim | fewer | shorter | |

All have plan `FailedBecauseDestinationPortalLocked`, blocker `None`.

- **The wait is 20 sim-minutes [verified, game data].** `kDefaultStandAndWaitDuration` is 50 in the code and
  `value="20"` in the game's tuning XML (`SimRoutingComponent`, `GameplayData.package`). `StandAndWaitController.Run`
  tests the time after each cycle, so a wait of 13-14 cycles of about 1.6 sim-minutes ends at 20.8-22.4: the `timeout`
  lines. Nothing else cuts it. §1's "13-14 cycles of 3.7 minutes = 50" was a wrong time estimate; the cycle counts stand.
- **What the `none` route is [verified for s295 / s336, inferred for s298].** A posture transition:
  `InteractionQueue` runs the transition interaction of the queue's head before the head counts as running, so
  `Sim.CurrentInteraction` is null and the routefail / standwait lines say `none`. In s295 and s336 the next `actdelay`
  line of the same Sim is `Bed_BeReadToSleep` (autonomous), "transitions 1-2 (6.7-7.0 s, 21-24 plans, last
  EnterRelaxing)": the child called to bed for a bedtime story walks to its bed, the adult reading stands at
  927.7,58.0,1202.7. Not the stuck check, not a follow-up of a social. A privacy lock needs an interaction to apply
  (below), which a Sim with neither a running nor a transition interaction does not have. The `standwait:` line now
  names the transition: `none (transition <X> for <head interaction>)`.
- **Rooms [verified, positions and room ids of routefail lines].** On the second floor (y 58) room 9 is the hall the
  waiters stand in, room 3 the stair area west of it (927-929, 1201-1202), room 15 the bathroom (toilet goal
  931.4,58.0,1201.5), rooms 6 / 4 / 7 / 10 the others; the third floor (y 61) is room 11. The goal 930.4,58.0,1201.5
  lies between the samples of room 3 and room 15; which of the two it belongs to is not known from the logs. The new
  line part `lock privacy (<situation type> of <Sim>[, begun in room N])` names the situation and its Sim.

### 7.2 What EA does [verified, decompiled, unless marked]

- **The lock.** `PrivacySituation.OnPortalPathPlan` (subscribed to `Lot.PortalPathPlan`, asked by the planner for each
  portal) locks a portal when its `RoomTo` is the room the situation's Sim (`SyncTarget`) is in **now**, for a planning
  Sim whose running, else transition, interaction is autonomous with priority up to `Privacy`, or not autonomous with
  priority below `UserDirected`, unless `OtherSimCanIgnorePrivacy` (toddlers and below, romantic partners, imaginary
  friends: `Sim.CanIgnoreNormalPrivacySituationsWith`). So a **player's own order is never privacy-locked**, and neither
  is a Sim without an interaction. Situations: toilet, urinal, shower, bath, sponge bath, woohoo (bed, shower, hot tub,
  sauna). A toilet's is created after the Sim reached the toilet and exits at the end of `Run` and in `Cleanup`;
  shower and bath alike.
- **A route planned to that room fails at once**: plan result `FailedBecauseDestinationPortalLocked`, `DoRoute`
  false, `DoRouteFailureBehavior`, the interaction ends. Autonomy does not pick targets in the room at all
  (`Autonomy.CheckAvailabilityOnLot`: `IsAllowedInRoom`, fed by the situation's room callback; for a social it tests
  the other Sim's room).
- **A forced replan at the start of the obstacle wait fails at once too**: `PerformAction` with
  `mbForceInitialReplan` returns `Terminate` when that replan fails, without waiting.
- **Only the wait's own cycle is different.** A Sim already on its way when the room goes private, which then meets an
  obstacle without the forced replan (a Sim it may wait for, or a portal action before it), enters the wait; each
  cycle's replan fails on the lock; the handler's early return (§2) answers "keep waiting" until the 20 minutes are
  over; then `Terminate`: the route fails exactly as it would have at once. The wait is built for people who move
  away; a privacy lock does not move.
- **After the timeout** [verified]: `mbElectedToExit` false, so `mReturnValue = Terminate`; EA's crowd rule and
  route-fail animation as in §2; a posture transition is retried by `InteractionQueue` (up to 10 tries, each a new plan
  that fails at once while the lock holds), then `PostureTransitionFailed`.
- EA has a ready test, `Lot.RoomLockedByPrivacy(Sim, room)`, that nothing in the game's script assemblies calls.

### 7.3 Fix (`StandWaitLocked.cs`, rules in `StandWaitLockedCore.cs`)

In a blocked cycle (§3) whose plan failed with `FailedBecauseDestinationPortalLocked`, the lock is classified:

- **privacy**: a privacy situation of the destination's room (`GetApproximateOriginalDestination`), or of the room the
  interaction's target Sim is in, whose Sim is in that room now and which holds for the waiter by EA's own rule above
  (same expression, same `OtherSimCanIgnorePrivacy`), and the waiter is not in that room itself;
- **door**: no such situation and the Sim's own door rule refused a door in the plan (`LockedDoorsDuringPlan`: the
  player's door locks, hidden rooms, pets);
- **privacy situation that does not lock this Sim**, **other**: the rest.

Only **privacy** acts: after `kStandWaitLockedCycles` (2) such cycles in a row, about 3 sim-minutes, the hook answers
"stop" without electing an exit. That is the timeout's own exit: `Terminate`, EA's crowd rule, EA's route-fail, the
interaction or transition ends as EA ends it and autonomy picks something else (not in that room: `IsAllowedInRoom`).
A lock that ends within those cycles lets the Sim walk on as before. No teleport, no lock opened, no new search.

Kept as EA has it: a player's own order (never locked, so never here); toddlers and partners (never locked); door locks
and every other lock (the 20-minute wait); a Sim inside the room; with `standwaitfix` on, a pending push or cancel is
answered first by its yield (§3 rule 1). A room that is privacy-locked and also behind a player-locked door counts as
privacy: every portal into it is locked for the Sim whatever the door says.

Why EA would plausibly do the same: it is EA's own answer for the same Sim, lock and route in the two neighbouring
cases (plan from a standstill; forced replan at the start of the wait); the long wait only happens through the early
return that §2 already shows to be an ordering slip; and the wait cannot succeed before the situation ends (how often
it ends within the 20 minutes is not known yet: off-arm counter 4350 against 4349 measures it).

**Scope.** `standwaitlocked`: Sims the player does not control. `standwaitlockedhh` in addition: household and selected
Sims (`Fixes.HouseholdExempt`; `[Experimental] HouseholdToo=1` lifts it as well). The household result differs from EA's
(the Sim gives up after 3 minutes instead of 20), hence the own switch. The harness's ungreeted visitors
(`GoHere target Terrain`, plan `DestinationPortalLocked` at the front door) are not privacy locks and keep EA's wait.

**Compare mode / log.** Hooks are §3's. Off: counters and the line say what would happen. The `standwait:` line gets
a new end kind `gave up (privacy lock)` and, for a wait with locked cycles, a tail:

`, lock <privacy (<SituationType> of <Sim>[, begun in room N]) | door | privacy situation that does not lock this Sim |
other>: L locked cycles, P privacy, fix <on|off|exempt>: <gave up cycle N | would give up cycle N | exempt (household) |
kept (...)>[, lock ended in cycle C]`

Counters 4340-4351 (`docs/registry.md`): locked cycles / waits, privacy cycles / waits, gave up, would give up, exempt
waits, waits at another lock, timeout cycles left of the ended waits, privacy waits that ran to the timeout, privacy
waits whose lock ended while the Sim waited, errors. `StandWaitLockedCheck run | result | lot` (`lot`: the privacy
situations on the selected Sim's lot and whether they lock it now; read-only).

### 7.4 Third-floor strands (s321, s365): not this rule [static answer; the s366+ strand lines decide]

Asked: can a privacy lock on the toilet's room close the stair from room 11, and can the situation outlive the toilet
use? By the code: a situation locks only portals **into the room its Sim stands in at that moment**; it is created at
the toilet and exits in `Run`'s end and in `Cleanup`, and no path that leaves it registered was found. The toilet goal
931.4,58.0,1201.5 is in room 15, not in the hall (room 9). So a toilet situation closes the stair down from room 11
only if that stair's lower end is in room 15, or if a situation's Sim stands in the room the stair lands in (the lock
follows the Sim: a situation still alive while its Sim stands at the stair top in room 3 would lock every way into room
3, stairs included, and plans through it would fail with `NoPathToDestination`, plans into it with
`DestinationPortalLocked`). Whether that happened is in the `routefail strand privacy:` lines (`StrandLog.cs`) and in
this section's `lock privacy (... begun in room N)` tail. A strand is a plan that fails at the start, not a wait:
`standwaitlocked` does not change it, and would only make Sims at such a lock give up sooner.

### 7.5 Checks (2026-11-21, no game) and run plan

- `node tools/selftest/standwaitlocked/run.ts`: EA's gate for every flag / priority pair, lock kinds, the decision
  table, and a wait model (off 20-22 minutes, on 2 cycles, a lock that ends in time is walked through, the fix never
  lengthens a wait; 500 random waits). `standwait` selftest extended for the new end kind.
- `StandWaitLockedCheck run` (repro `standwaitlocked-check`): off `BROKEN`, on `FIXED`, with controls. No forced repro:
  it needs a room going private while a Sim is on its way there and an obstacle on that way; several of the storms
  s294-s365 have one.
- Storms as §6 with `standwaitlog`: off arm `MF_EXTRA="... standwaitlog"`, on arm
  `... standwaitlog standwaitlocked standwaitlockedhh`.
- Read, off arm: `standwait:` lines with `lock privacy (...)`: `would give up cycle 2`, their end (`timeout` or
  `lock ended in cycle C`), the situation type and Sim, the transition named after `none`. On arm: the same waits end
  `gave up (privacy lock)` with 2 blocked cycles and 3-4 sim-minutes; lines with `lock door` / `other` unchanged
  (the ungreeted visitors); counter 4351 = 0; the `actdelay` line of the interaction ends (`ended without acting`) or
  goes on to another target; no new stuck Sims, strands, `route false` or routefail kinds beyond the off arms; no Sim
  repeating the same wait every few minutes (same Sim, same goal, 3 or more `gave up` lines in a run would be a retry
  loop to look at).

## 8. Sims share stairs, doors and ladders and walk through each other (`sharelanes`, `laddershare`, `simpass`, `angleentry`; 2026-11-21, `feat/sharelanes-1121`, built, default off, NOT RUN)

A player's wish: Sims may use the same stairs and doors at the same time in both directions and enter stairs and
ladders at an angle; clipping through each other is fine, waiting is not. No teleport, nothing unstable. `ShareLanes.cs`,
rules in `ShareLanesCore.cs`, counters 4364-4389, check `ShareLanesCheck run | result | lot`, selftest
`tools/selftest/sharelanes`, repro `sharelanes-check`. Each behaviour has a household switch; all off.

### 8.1 How the game serialises a portal [verified, decompiled `Sims3.Gameplay` unless marked]

- **The lane table.** `PortalComponent.mLockedLaneIndices` maps a Sim to the lane it holds (index, start slot, end
  slot). `PortalAction.PerformAction` calls `LockRoutingLane` when the Sim begins the sub-path that *ends* at the
  portal, so a lane is held for the whole approach, not only the crossing. `GetFreeRoutingLane` walks the start slots
  nearest first and refuses a lane when the first holder of that lane entered at another slot (or it is an elevator
  lane): **same direction shares, opposite direction waits.** Then come the portal's `DoAdditionalLaneChecks` and
  `IsLaneUnobstructed`; a lane that fails only those is still handed out as a fallback when no lane passes.
- **The wait.** With no lane, `PortalAction` inserts `WaitForLaneAndPlantToItRouteInterruptCallback`: the Sim walks to
  a spot 0.75-3 m from the portal (45 degree cone, same room) and `AcquireLaneRouteAction` asks again every 0.25
  sim-minutes for 20 sim-minutes, footprint on. Portals with `NoLanesAvailableFailsRoute` fail the route instead.
- **Lanes.** Stairs: routing slots / 2 lanes (2 on the stairs logged), lane test ignores Sims. Doors: one lane per
  door leaf (a single door has **one**), `DoAdditionalLaneChecks` pushes standing Sims off the lane ends and waits for
  them. `NonAnimatedDoor` (gates, arches): as doors, open state instead of a leaf. Ladder: 2 lanes, lane test ignores
  Sims.
- **What several Sims on one lane touch.** The table is keyed by Sim. `Door.Open` / `Close` count users per leaf
  (`mUseCount`, close alarm at 0), so two directions keep the door open until the last Sim is through; only the swing
  side is the first opener's. The use list is per Sim. `OnSimRouteActionsFinished` and `FreeRoutingLane` are per Sim.
  The stair walk is native and per Sim: `RouteManager_BeginPortalRouting 0x74D720` -> `0x955FB0` drops the Sim's
  locomotion body, makes a new `Locomotion/AnimatedSimStairsBody` (0x160 bytes) and initialises it in `0x8555E0` from
  the spec (stair object, lane, the two slot positions, direction); `0x844BE0` tests the geometry only. No lane
  occupancy is read there **[verified in the decompile of those three functions; callees below depth 2 not read]**.
  The script side keeps one sleeping task per Sim (`mSleepingTasks`). So nothing but looks depends on one Sim per
  lane; the direction test is a presentation rule.
- **The ladder has a second gate.** `Ladder.LadderPortalComponent.OnPortalStart` runs the interaction
  `PortalClimb`, whose `Run` first waits for its turn in `Ladder.Line` (`SimQueue`, wait where you stand, at most 3
  Sims, 30 sim-minutes; a fourth Sim gets `ObjectInUse` and a route failure). One climber at a time, the others
  stand on the lane ends: the standing Sims of docs/183 §9. The climb enters the state machine `ladder` with the
  ladder as an actor **[inferred: two Sims in that machine at once are not known to be safe; not run]**.
- **Standing Sims close a portal for planning** only in a plan with "blocked by people" (the obstacle stand-and-wait
  sets it): `PortalEdgeCost 0x634500` -> `0x6338A0` (docs/183 §9). All 8 `ladderfoot:` lines of the storms are
  `Replan`.
- **Sims in the way.** `SimSteeringAdaptor.HandleEventEncounteredObstacles` skips every obstacle while the Sim's
  `ShouldIgnoreAllObstacles` timer runs and then inserts no wait (unless the event forces a replan). The game starts
  that timer itself after a portal with a Sim within 0.15 m (`EndOfPortalInvicibilityDuration`, 5 sim-minutes) and
  after a wait in a crowd. The crossing itself (`PortalTraversalAction`) already runs with avoidance off.
- **The approach.** Stairs (`PortalAnimateThroughAction.DoPortal`): the route's first sub-path is the native plan to
  the lane's start; when the Sim did not get the planned lane, or the lane says `ForcePortalPlan`, the game replaces
  it by `GetPathToLane`, a plan to the slot's point with no facing. The walk to the lane start uses the stop
  animation; then `OnPortalStart` -> `StartStairTraversal`. In `0x8555E0` the body **sets the Sim's transform itself**:
  position = the spec's begin position, facing from the two lane ends (`0x860460`, vtable `+0x30`); when the body
  cannot be made the Sim is set to the end position (result 2, "skip"). So the stair walk does not need the Sim to
  arrive facing the stairs, but it does need it at the lane's start point: starting elsewhere would be a jump.
  Ladder: `GetPathToLane` plans to the *slot* (`PlanToSlotNotPosition`), which carries the slot's facing; the climb
  up then sets the Sim's position and facing in script (`RoutingSlot_3`), the climb down plays from where the Sim
  stands and only places it at the bottom.

### 8.2 Measured (storm logs, read-only)

316 logs of 2026-11-20/21 (`phase-2026112[01]*`), 100 `standwait:` lines in 49 of them (the line exists only with
`standwaitlog`):

| Who | Waits | Sim-min standing | Of them plan `DestinationPortalLocked` |
|---|---|---|---|
| household / selected | 39 | 565.7 | 26 waits, 422.1 sim-min = **75%** |
| other Sims | 61 | 1102.9 | 52 waits, 1015.6 sim-min = **92%** |

Runs s420-s431 alone (12 logs, 5 lines): household 3 of 4 waits, 35.7 of 40.8 sim-min (88%). Most household lines
are at 927.5,58,1203-1205, the foot of Colonial Casa's ladder, and say `would pass` / `pass`: a plan without people
succeeds, so standing Sims were the lock. Failed-plan lines in the 316 logs: `DestinationPortalLocked` 752 of 4,037.

**Not measured by any log so far:** the lane wait of §8.1 itself (no line, no counter). Counters 4370-4374 and
`lanewaitlog` do that from now on, switch off or on.

### 8.3 What is built

| Switch (+ household) | What it does | The game's own step it uses |
|---|---|---|
| `sharelanes` (`sharelaneshh`) | A lock the game refused on stairs, a door, a gate or a ladder only by the direction test is searched once more: nearest start slot first, lanes held from the other end count as free, the portal's extra checks and obstruction test run as in the game, its fallback lane is kept. Exact types only (`StairsPortalComponent`, `DoorPortalComponent`, `NonAnimatedDoorPortalComponent`, `LadderPortalComponent`); elevators, spiral stairs, turnstiles, tomb doors, ropes, pools keep the table. | `GetFreeRoutingLane`'s own order and checks; `LockRoutingLane` then enters the Sim as always (door count, use list). |
| same | A blocked stand-and-wait cycle whose plan says `DestinationPortalLocked` plans without "blocked by people" on the first such cycle (then every 3rd); success = only people closed it, the Sim walks on with the ignore-obstacles timer. Failure = a real lock, the wait goes on (counter 4377). Not with a Sim other than the partner at the route's end. | the pass step of `standwaitfix` (§3 rule 2), without waiting for cycle 2 and without that switch |
| `laddershare` (`laddersharehh`) | `SimQueue.WaitForTurn` answers "your turn" for a `PortalClimb` on its ladder's own line; the Sim is not entered in the line. | none: this drops a wait the game has. Own switch because of the state machine question above. |
| `spiralshare` (`spiralsharehh`) | Spiral stairs (`StaircaseSpiralPortalComponent`, exact type): the `sharelanes` search for its one lane, and `SimQueue.WaitForTurn` answers "your turn" for the staircase's walk (`UseInternal`) on its own line. Both are needed: the Sims in the line hold the lane from their end, so the Sim coming the other way waits for the whole line (7.2 and 12.0 sim-min in s465 / s469, the only lane waits left with the other pairs on). The railing slide keeps its line. Counters 4390-4397. | `GetFreeRoutingLane`'s own order and checks; the line: none, a wait the game has is dropped |
| `simpass` (`simpasshh`) | At the obstacle event: only Sims in the way (toddler or older), none within 0.6 m of the route's end, walker not already ignoring: `StartIgnoringObstacles(kSimPassMinutes = 3)`, then the game's handler runs and skips them. A wait that still starts (forced replan) returns `ContinueAndFollowPath` at once under the same test. | `StartIgnoringObstacles`, the game's exit from a wait in a crowd |
| `angleentry` (`angleentryhh`) | Stairs: when the Sim got its planned lane, the first sub-path is replaced by `GetPathToLane` (kept when that plan fails). Ladder climbed from below: `GetPathToLane` gets the lane without `PlanToSlotNotPosition`, so the game takes its point plan. Climb down: unchanged. | the game's own replacement approach for a lane that was not the planned one |
| `lanewaitlog` | `lanewait: <Sim> <scope>, <interaction>: <Type> 0x<id> (<kind>), X sim-min from the refusal, got a lane / gave up, held then by: ..., at x,y,z` | log only |

**Scope.** Without the household switch a household or selected Sim (`Fixes.HouseholdExempt`) is exempt as the
arriving Sim, and a lane it holds from the other end stays closed for everybody; `simpass` needs the walker and every
Sim in the way in scope; `laddershare` the climber and everybody in the line. The storms' waits are household Sims,
so test arms need both switches of a pair.

**Off = compare mode.** The hooks are always in: refused locks by cause, lane waits and their length, what each
switch would have done.

**Why spiral stairs can share [verified, decompiled].** The walk is one interaction per Sim with its own state
machine and the Sim as the only actor (the slide alone names the staircase as an actor); it places the Sim at its
animation events and at the end. The staircase keeps nothing per walker but the table and the use list, both per Sim;
the route's end frees the lane and takes the Sim off the use list whatever the walk did. The lane's extra check only
pushes Sims that stand on a lane end and are not routing. On stacked spiral stairs the game itself moves a walker
onto the next staircase without that staircase's lane or line, so two Sims on one spiral staircase in opposite
directions already happen. What is new is the look: Sims walk through each other on the steps. The line's cap (three
Sims, the fourth fails with "object in use") goes with the line. Two storms with the pair on next to the other
four pairs (s478, s479, 2026-11-22; counters read in s479 only): line skipped 15, no spiral lock refused at all (nobody
stands in the line holding the lane), lane waits 0, errors 0; both runs no crash, no script error, route failures as in the four runs without it
(138 / 151 against 118-160). Two runs do not show the wait gone (the four runs without it had no lane wait
either); by eye not checked.

**For seamless doors both `sharelanes` and `simpass` are needed:** the lane is shared, but a Sim walking up to the
door still meets the one coming out of it as an obstacle.

### 8.4 Not built, and why

- **Entering stairs from the side (cutting the corner past the lane's start point).** The stair body places the Sim
  at the spec's begin position when it starts (§8.1). A begin position other than where the Sim stands is a jump; a
  begin position off the slot is untested geometry for `0x844BE0`. Needs a native change (blend from the current
  transform) and live runs; not safe from script.
- **The stop animation at the lane's start.** `DoPortal` adds `UseStopAnim` unless the portal object is an
  `IPortalIgnoreStartStopAnim`. Dropping it means rewriting `DoPortal` or changing the flag in `PerformRoute`
  for these actions; whether the stair body takes over a Sim that has not stopped is not known. Left as is.
- **The ladder climb down from an angle.** It plays from the Sim's own transform; a wrong facing would walk the
  animation off the ladder until the event that places the Sim. Keeps the slot plan.
- **The endpoint test in the native search** (`0x6338A0` ignoring Sim footprints for these portals): needs a native
  build (MSVC, not on this machine). The script side covers the logged cases: every one is a replan with "blocked
  by people" inside the stand-and-wait, which `sharelanes` now retries without people at once and `simpass` mostly
  never reaches.

### 8.5 Limits [inferred unless marked]

- `angleentry` on stairs changes the look only if the native approach sub-path carries an end facing that the point
  plan lacks. Not checked in the native code. It costs one more short plan per stair use. Compare by eye.
- A shared fallback lane (a Sim on a lane end) comes back with "not the preferred lane", as in the game: the door
  action then replans the three sub-paths around the door and replans after it.
- `would share` (4368) counts refused calls, and a waiting Sim asks every 0.25 sim-minutes: read waits from 4370.
- `simpass` makes the walker unpushable while its timer runs (the game's rule for an ignoring Sim).
- Two Sims ending a stair walk on the same slot is what the game already has for same-direction sharing; its own
  0.15 m rule then starts the ignore timer.

### 8.6 Checks (2026-11-21, no game) and run plan

- `node tools/selftest/sharelanes/run.ts`: lane classes against the game's first-holder test (4,000 random tables),
  decision by switch and scope, the lane choice with fallback, a crossing model (6 Sims, 300 runs: the game's table
  6,329 refused steps at a one-lane door, 57 on two-lane stairs, shared 0 and every table empty at the end), portal
  pass schedule, walk-through rule, ladder rules, wait counter.
- `build-fixes.ts` builds, `refs-resolve-check` 0 unresolved.
- Live, one pair at a time, off / on interleaved, `lanewaitlog standwaitlog routefaillog` on in both:
  1. `sharelanes-check` once (off `BROKEN`, on `FIXED`, `members found=True`; the `fix_applied` lines
     `PortalComponent.GetFreeRoutingLane: ok`, `...WaitForLaneAndPlantToItRouteInterruptCallback: ok`,
     `AcquireLaneRouteAction.PerformAction: ok`, `PortalComponent.GetPathToLane: ok`, `SimQueue.WaitForTurn: ok`,
     `SimSteeringAdaptor.HandleEventEncounteredObstacles: ok`).
  2. Storms as s420 (Colonial Casa, 2-minute route storms): off arm gives the first numbers for lane waits
     (4370-4374, `lanewait:` lines). On arm `sharelanes sharelaneshh`: 4366 > 0, lane waits and their sim-minutes
     down, `standwait:` lines with `DestinationPortalLocked` end `pass` in 1 blocked cycle or carry 4377.
  3. Then `simpass simpasshh` added: `standwait:` lines with a Sim blocker gone, 4381 > 0; then `laddershare
     laddersharehh` (watch the ladder with two climbers: animation, both arrive); then `angleentry angleentryhh`
     (by eye at the stairs and the ladder foot; 4386 / 4387 > 0, 4389 low).
  4. Repros `upstairs-landing-jam`, `standwait-landing-crowd`, `ladderfoot-force` with the pairs on.
- Pass: errors 4367 = 0, no new script errors, no Sim stuck in a portal (`StairWaitEndCheck result` waits asleep 0
  after the storm), door left open with nobody near = fail (use count), no new routefail kinds or `route false`
  beyond the off arms, `gohere end` lines stay `route ok`.

## 9. Sims walk through a Sim or pet that keeps them waiting (`clipwait`, `clipwaithh`; 2026-11-22, `feat/clipwait-1122`, built, default off, NOT RUN)

A player's wish: a Sim busy at a mirror, standing, or chatting in a hallway should not keep a passing Sim waiting;
after a moment the walker clips through, as in other games. No teleport, the Sim walks its own route. Two Sims at
one object are not wanted. `ClipWait.cs`, rules in `ClipWaitCore.cs`, counters 4398-4413, check
`ClipWaitCheck run | result`, selftest `tools/selftest/clipwait`, repro `clipwait-check`.

### 9.1 Where a Sim waits for a Sim outside portals [verified, decompiled `Sims3.Gameplay` unless marked]

| Case | The game's code | Cost | How often |
|---|---|---|---|
| A Sim or pet in the route's way | `SimSteeringAdaptor.HandleEventEncounteredObstacles` -> `ObstacleEncounteredRouteInterruptAction`: stand-and-wait, each cycle a replan with "blocked by people" and a push of the pushable Sims | up to `kDefaultStandAndWaitDuration` (50 sim-min in code, 20 in tuning), then route failed | every meeting of two Sims on a route; 374 storm logs of 11-20/22: 422 failed plans with a Sim as blocker (`NoPathToDestination`), 9 `standwait:` lines with a Sim blocker (3 ran to the timeout). Waits whose plan succeeds (the walker stands while the other Sim is pushed or leaves) had no counter |
| A Sim that cannot be pushed | `RoutingComponent.PushSimsStatic`: a Sim in a conversation is skipped, a Sim in an interaction only moves when the interaction is an `IBePushedCanCancel` that allows it | the wait above runs until that Sim is done | the mirror, sink, fridge and hallway-chat cases; not measured |
| A Sim on the route's end point | `HandleEventDestinationObstructed` -> `DestinationObstructedRouteInterruptAction`: stand-and-wait, pushes Sims within 0.6 m of the end every cycle, replans every few | same duration; ends with the "Sim in the way" balloon (`PlayRouteFailureIfAppropriate`) | not measured before (1 stall-watch stack in the logs); counters 4409-4411 now |
| Arrived inside a Sim | `SimRoutingComponent.DoRoute`, only for routes with `CheckForFootprintsNearGoals` (dance floor, stereo, karaoke, jukebox, DJ booth, some spawners): push, replan around people, else an emergency walk 0.6-2.5 m away and route failed | one short walk + the failed interaction | rare, those objects only |
| Pushed out of the way | `PushSimsStatic` with `waitForPushes` (doors, gates, footprints of objects being placed, pet landing spots): the pusher idles up to `TotalSimMinutesToWaitForSimsToBePushed` | short | doors: §8; else rare |
| A pet lying in the path | a pet is a `Sim` for all of the above (`ToddlerOrAbove`); a running pet does not stop for Sims | as rows 1-3 | not measured |
| Car, bike, bed side | route ends at an object's slot: row 3 | | |

Not a routing wait and not touched: an object in use (`InUse`, lines such as `SimQueue`) is the game's rule for
one user at a time.

### 9.2 What is built

After `kClipWaitGraceMinutes` (2 sim-minutes, about 2 seconds at normal speed) of either stand-and-wait that the
game wants to continue, `ClipWaitCore.Decide`:

- **Walk through.** Only Sims and pets in the way (toddler or older; anything else in the way keeps the wait): when
  the route's plan had failed it is planned once without "blocked by people" (still failed = a real block, the wait
  goes on, 4408); then `StartIgnoringObstacles(kClipWaitMinutes = 3)` and the wait ends with the game's own
  "continue and follow the path". This is the exit the game takes itself after a wait in a crowd.
- **The route's end.** A Sim within the game's 0.6 m of the end is walked into only when the route ends on a plain
  floor spot, or at an object that Sim is not using (its interaction, transition, posture and parent are not that
  object; `kClipWaitObjectDest = false` limits it to floor spots). Never when the walker's interaction targets a Sim
  or the route goes to a Sim (social placement), never on a Go Here the player gave to a household Sim, never when
  the Sim at the end uses the object. The route's next destination-obstructed event then starts no new wait
  (4412). When the end may not be walked into but other Sims block the way, the walker passes those and the game's
  own handling of the end runs when it arrives.
- **Why a grace and not at once.** The first cycles are the game's own answer: a replan around the Sim and a push
  of a Sim that can step aside. Both look better than clipping and take under one cycle when they work. The rule is
  judged at the end of a wait cycle, so the real wait is one or two cycles.

`simpass` (§8) acts earlier where it applies (at the obstacle event, no grace, nobody at the route's end); `clipwait`
is the rule for the waits that still begin, with or without `simpass`. Lanes and lines (`sharelanes`,
`laddershare`, `spiralshare`) are other code paths. `standwaitfix` runs first in a blocked cycle.

**Scope.** `clipwait`: walker and every Sim walked through are Sims the player does not control. `clipwaithh`:
household and selected Sims too. Off = compare mode: the waits are counted and what the rule would do (4402).

### 9.3 Not built

- **Arrival inside a Sim** (`DoRoute`, `CheckForFootprintsNearGoals`): the game's replan there finds another spot
  around the object, which is the better answer; only its fallback fails. Rare objects.
- **Two Sims at one object's slot in use**: not wanted.
- **Babies** on the floor stay obstacles (the game treats them as objects).
- **The native planner's endpoint test**: needs a native build; the script side covers the waits.

### 9.4 Limits [inferred unless marked]

- That the walk goes on to an occupied end point when the destination event starts no wait is taken from the
  game's own cases with that handler off (ghosts, portal crossings); not run.
- The walker cannot be pushed while its timer runs (the game's rule for an ignoring Sim), 3 sim-minutes per walk.
- A failed plan names one blocker; the Sims within 0.5 m of the waiter are the second guess. A plan that fails
  because of Sims farther away (a portal closed by standing Sims) is `sharelanes`' case, not this one.

### 9.5 Checks (2026-11-22, no game) and run plan

- `node tools/selftest/clipwait/run.ts`: the rule's cases and a sweep of every input combination (go only on, Sims
  only, past the grace, in scope; a walk ends in a Sim only where allowed).
- `build-fixes.ts` builds, `refs-resolve-check` 0 unresolved; selftests `standwait` and `sharelanes` pass.
- Live: `clipwait-check` once (off `BROKEN`, on `FIXED`, `members found=True`). Storms off / on interleaved with
  `clipwaitlog standwaitlog routefaillog`: off arm gives the first numbers (4398 waits past the grace, 4402 would,
  4409-4411 destination waits, their sim-minutes and failures); on arm `clipwait clipwaithh`: 4399 / 4400 > 0, 4411
  and the `standwait:` timeouts with a Sim blocker down, 4401 = 0, no `clipwait: error at` line. By eye: a Sim at a
  sink or mirror in a corridor, a chatting pair in a doorway, a dog lying in a hallway.
## 10. A slot closed by furniture or a wall is used anyway (`blockeduse`, `blockedusehh`; 2026-11-22, `feat/blockeduse-1122`, built, default off, NOT RUN)

A player's wish: "if he can't get in because there's something in the way he could just go in, and bed, if one of the
sides is next to the wall just teleport him to the animation and then back". Clipping on the last stretch and a short
jump onto the slot and back are fine; ordinary walking stays real. `BlockedUse.cs`, rules in `BlockedUseCore.cs`.

### 10.1 How object use reaches a slot [verified, decompiled `Sims3.Gameplay`, `ScriptCore`, unless marked]

- `RoutingComponent.RouteToSlot` (one slot, or a list with an optional preferred slot: 735 call sites with its
  `...AndCheckInUse` / `RouteToSlotList` wrappers) makes a route, adds one slot destination (`Route.PlanToSlot`), plans
  and calls `DoRoute`. `SimRoutingComponent.RouteToUnusedPart` (beds, seats; 20 sites) adds one slot destination per
  part: weight 0 for a part in use, 1000 for the preferred part, 500 for the others, plus a "near the object" goal
  while the Sim is not close; once close, only the slots of free parts are left. So the game takes whichever free bed
  side it can reach; a side that cannot be reached is only a failure when it is the last one free.
- `SimRoutingComponent.DoRoute` tests the plan result first. A failed plan: route-fail behaviour (a walk to 1-3 m from
  the blocker in its room, the balloon), exit reason `RouteFailed`, `false`; the interaction ends. Afterwards the
  callers read the route's plan result only: the slot's name hash, and `RouteToUnusedPart` the end point, which it
  compares with the slot's place to find the part.
- The route's destination list is native and cannot be read back, so the slots a plan wanted are noted when they are
  added (`Route.AddDestination`, cleared with `Route.ClearDestinationList`).
- The game has its own switches for a relaxed plan: `PassThroughObjects` (used for ghosts, with `PassThroughWalls`)
  and `UserOverrideSlotObstruction` + `DoSlotObstructionChecksUserOverride = false` (carry, repair, houseboat routes).
- Not covered by a route at all: interaction tests that plan to a slot and hide or grey the interaction when the plan
  fails (`Sink.CanRouteToSink` and others among the 178 direct `PlanToSlot` sites).

### 10.2 Measured (storm logs `phase-*`, `routefail:` lines, read-only)

56,328 failed plans in 1,042 logs (household Sims; storms on crowded lots, so Sims and locked doors dominate). Of the
6,622 from object-use interactions (bed, tub, shower, toilet, sink, fridge, seats, counters, mirrors):

| result, blocker | lines |
|---|---|
| no path, no blocker named | 1,886 |
| destination portal locked | 1,826 |
| no destination (every wanted slot refused before the search) [what refused them: not logged] | 1,719 |
| no path, a Sim | 735 |
| no path, furniture | 403 |
| no path, a wall | 16 |

Furniture cases by pair: `TakeBath` blocked by a litter pile 107, mirror interactions by the double bed 127, toilet by
a dining chair or bar stool 18, tub by a dresser 14, sink / toilet by the crib or the tub 31. No log line says how
often a bed side at a wall fails: with the other side free the plan succeeds.

### 10.3 What is built

In `DoRoute`, for a failed plan whose route has slot destinations on an object, result "no path", "no destination" or
"slots / points obstructed", blocker not a Sim:

1. Walk up. Unless the Sim stands in the object's room, on its level, within 3 m of a wanted slot, the game's own
   "near the object" plan (1-3 m, the object's room: the plan of its route-fail behaviour) is walked as a normal
   route. No such plan = the object is out of reach (another level, a closed room): the game's behaviour stays.
2. Clip. The route is planned again with "pass through objects" and "no slot obstruction test"; walls still count. A
   plan of at most 6 m whose end no Sim stands on is walked; the options are put back afterwards.
3. Snap. Otherwise the Sim is put on the nearest wanted slot within 4 m (1 m in height) that no Sim stands on, facing
   as the slot does, and the route answers "arrived" with that slot's hash and place, which is all the callers read.
   A slot behind a wall (a bed side at the wall) takes this branch: the way round is longer than 6 m or there is none.
4. Out. The Sim is remembered with the spot it stood on before step 2 or 3. Later, when it plans a route (`Plan` /
   `Replan`), is done with that interaction, sits or lies in nothing and is still at the slot: a plan that succeeds
   ends the entry (it walks). A plan that fails is followed by one plan to the remembered spot; if that fails too the
   Sim is put on that spot (or, if that spot is not routable any more, on a spot the game's placement search finds
   near it) and the plan is made again.

Scope: `blockeduse` = Sims the player does not control, `blockedusehh` = household and selected Sims, each alone
(`IsPlayerSim`; `[Experimental] HouseholdToo` does not lift it: this is a teleport). Humans only, not riding, not in
a vehicle. Not with a Sim, a door or stair, a rabbit hole or a vehicle as the object; not on the world lot. Go Here and
every other point route has no slot destination and never comes here. A case that kept the game's behaviour is not
tried again by that Sim on that object for 30 sim-minutes. Off = compare mode: 4414 / 4415 count.

Counters 4414-4429 (`kCounterSlots` 4430): failed slot plans / would / exempt / walked up / clip / snap in / snap out /
walked out / not this kind / kept: out of reach / kept: no slot in range / walk up stopped / errors / clip walk
stopped / snap out to a found spot / kept: cooling down. Lines `blockeduse: <who> <Sim> -> <Object> ...: clip | snap in
| kept`, `blockeduse: ... snap out from ... to ...`, `blockeduse: error at <site>` (20 a minute, 300 a session).

### 10.4 Limits [inferred unless marked]

- "No destination" is taken as "every slot refused by an obstruction" from the logs' shape; a plan that answers it for
  another reason costs one walk up and one extra plan, then keeps the game's behaviour and cools down.
- A snap is a visible jump of up to 4 m; a clip walks through furniture for up to 6 m. The animation that follows
  plays where the game would play it; with a wall at the slot the Sim stands in or behind the wall for its length.
- The way out waits for the Sim's next plan. A Sim with nothing to do stands on the closed slot until autonomy or the
  player gives it a route.
- Three swaps are new: `Route.AddDestination`, `Route.ClearDestinationList` (no yield) and
  `SimRoutingComponent.DoRoute` (yields; stub, `savedstacks`). Every Sim saved mid-route has a frame in the `DoRoute`
  hook; its body is two lines on purpose, since a build that changes it resets those Sims once at the next load
  (docs/242 §2). The earlier `DoRoute` hook was removed for that reason because it only logged.
- Pets, interaction tests that plan to a slot (10.1), jig routes (socials, `SocialJigTwoPerson` blockers) and objects
  the Sim reaches by a radius route (TV, easel at a distance) are not covered.

### 10.5 Checks (2026-11-22, no game) and run plan

- `node tools/selftest/blockeduse/run.ts`: 45 checks of the rules. `BlockedUseCheck run | result | lot` (`lot`: plans
  of the selected Sim to every routing slot of the lot's tubs, showers, toilets, sinks, beds, chairs and sofas, lists
  the objects with a closed slot). Repros `blockeduse-check` (rules, counters) and `blockeduse-force` (a room built
  with `build_layout`: a toilet with an end table on its slot, the Sim ordered to use it, off then on). Neither has run.
- Run plan, about 30 min of game time: 1. load, `ts3fixes applied` must show `SimRoutingComponent.DoRoute: ok`,
  `Route.AddDestination: ok`, `Route.ClearDestinationList: ok`; save and load with the switches off, Sims mid-route
  must come back (`savedstacks` 761 = 0). 2. `blockeduse-force`. 3. By hand: a double bed with one side at a wall and
  two sleepers; a tub with a dresser in front. 4. One storm with both switches on.
- Pass: 4426 = 0, no `blockeduse: error at` line, no new script errors, the Sim ends on a routable spot after use
  (4420 or 4421 follows every 4418 / 4419), no Sim left standing in a wall, `gohere` lines unchanged.

## 11. Pets block less and get blocked less (`petrelax`, `petrelaxhh`; 2026-11-22, `feat/petrelax-1122`, built, default off, NOT RUN)

A player's wish: pets block Sims and get blocked a lot, be less strict with them; clipping is fine. No teleport,
every Sim and pet walks its own route. `PetRelax.cs`, rules in `PetRelaxCore.cs`, counters 4430-4445, check
`PetRelaxCheck run | result | lot`, selftest `tools/selftest/petrelax`, repro `petrelax-check`.

### 11.1 How pets block and get blocked [verified, decompiled `Sims3.Gameplay` unless marked]

| Case | The game's code | Cost | How often |
|---|---|---|---|
| A pet that cannot be pushed | `RoutingComponent.PushSimsStatic`: a Sim with a running or transition interaction is pushed only when that interaction is an `IBePushedCanCancel` (three classes in the game, none a pet's). A pet that naps, sits, begs, follows, watches or scratches stays. Only a pet with no interaction is pushed (`BePushed`, with larger distances for dogs, foals and horses) | the walker's obstacle or destination stand-and-wait of §9.1 runs until the pet is done or the route fails | pets are in such interactions most of the time; not measured (no pet lot ran with `standwaitlog`) |
| A pet lying on the floor | `PetSurfacePosture.LayDownHere` (lie down, nap, sleep): autonomous = the pet's own position (favourite spot by chance, "next to my sleeping mother / mate / owner" on the home lot), then `FindGoodLocationNearby` for a `PetSurfaceFloorJig`. `SocialJig.OnStartup` locks the jig's routing footprint (`RoutingFootprintLocking`, `MustHaveKey` = the jig), and only the pet's own route ignores it | the lying pet is an **object** for every other Sim's plan: a doorway, stair foot or hallway it lies in is closed (`NoPathToDestination`, blocker Object), and `simpass` / `clipwait` keep a wait with an object in it | every floor nap; where = where the pet stood, so often next to its Sim, in doorways and at stair feet. Not measured |
| A pet on a lane end | door: `DoAdditionalLaneChecks` pushes standing Sims (a busy pet stays; the crossing then runs with avoidance off, so the Sim walks through it), and refuses the lane when a lane slot is blocked by an object (the floor jig); stairs and ladders: a standing Sim closes the portal in a plan with "blocked by people" (§8.1) | lane refused or fallback lane; `DestinationPortalLocked` in the stand-and-wait | part of the rows below |
| A pet behind Sims | a walking pet gets the same obstacle event and stand-and-wait as a Sim. Only a running pet (`PetRun`, gallop) skips every Sim in `HandleEventEncounteredObstacles` and cannot be pushed: the game itself clips running pets through Sims | §9.1 rows 1-3 | 65 run logs with pet interactions in their `routefail:` lines, 1,084 failed plans of pets: 68 `NoPathToDestination` with a Sim as blocker (42 of them `Replan` = inside a stand-and-wait; litter box 25, scratching 17, begging 12), 250 with an object as blocker |
| Following, greeting, begging | `PetFollowSimAround`, `PetInitateSocial`, `PetBegForFood`: routes to a Sim; the pet then stands next to it, unpushable | the Sim's next route starts with the pet in its way: row 1 | `PetFollowSimAround` 240 failed plans, the most of any pet interaction |
| Horses | no door (`CommonDoor`: only a large gate), so `DestinationPortalLocked` on every plan into a house: 448 of the 1,084 failed plans, 387 of them two horses of one household | EA's design, not a wait | not touched |
| Bowls, beds, litter boxes | one user at a time (`InUse`), the game's rule | | not touched |

### 11.2 What is built

1. **Walk through at once** (`PetRelaxCore.Pass`). At the obstacle event (before `simpass`) and at an obstacle
   wait that still begins: a small pet (cat, dog, puppy, kitten, raccoon) walks through the Sims and small pets in
   its way; a Sim walks through small pets when nothing else is in its way. `StartIgnoringObstacles(kPetRelaxMinutes
   = 3)`, the game's own timer; a Sim that also has a Sim in its way is left to `simpass` / `clipwait` (the timer
   ignores everything). In the two stand-and-waits the same rule ends the wait in its first cycle instead of after
   the clipwait grace; where the walk may end is `ClipWaitCore.Decide` unchanged (never into a Sim or pet that uses
   the object the walker goes to, never for a social, never at the end of a player's Go Here).
   **Horses, foals, deer** and Sims riding or leading a horse, as walker or in the way: kept (4439); they get the
   `clipwait` grace as any Sim.
2. **A lying pet is not an object.** `SimRoutingComponent.SetRouteOptions` (every route a Sim creates) adds the
   floor jigs of the lying pets on the walker's lot to the route's ignore list (`AddObjectToIgnoreForRoute`, the
   call the game makes for the pet itself). The jigs are listed when `LayDownHere.Run` returns and leave the list
   when the pet is up. In the obstacle event and the two waits a floor jig counts as its pet, and the route ignores
   it from then on. The pet itself is then a Sim in the way: rule 1.
3. **Not on a lane end.** Before the game's autonomous floor lie-down at the pet's own position: when a stair, door,
   gate, ladder, spiral stair or elevator lane end of the lot is within `kPetRelaxPortalClear` (1.25 m, same floor),
   the pet walks to a spot 2 or 3 m away (16 candidates, the direction away from the lane end first, none near any
   lane end, at most 4 plans), then the game's lie-down runs from where it stands. No spot or a failed walk: the
   game's lie-down as it was (4442). Not on the world lot, not for a spot the player chose.

**Scope.** `petrelax`: walker and everyone walked through are Sims and pets the player does not control (townies'
pets, strays, service pets); `petrelaxhh` adds household and selected Sims and pets. Rule 2 needs the walker and
the lying pet in scope. Off = compare mode (4437 would; 4443 / 4444 count the lie-downs).

### 11.3 Not built, and why

- **Pushable pet idles** (an `IBePushedCanCancel` answer for naps and sits): with rules 1 and 2 nobody waits for
  the pet, and a push would wake a sleeping pet (energy, moodlets): a change of EA's design, not wanted.
- **One-tile hallways** as lie-down chokepoints: no cheap test for "hallway" in script; rule 2 lets Sims through
  wherever the pet lies.
- **Favourite and "next to my owner" sleeping spots** on a lane end: the player's or EA's choice of spot; rule 2
  covers the passing Sims.
- **Horse naps** (`HorseNap`, own jig, outdoors) and horses at house doors: large bodies, EA's door rule.
- **Bowls, beds, litter boxes in use**: the game's one-user rule.
- **The door's lane test with a floor jig on a lane slot** (`IsRoutingSlotObstructed` is native and does not read
  the route's ignore list): the door hands out its fallback lane; rule 3 keeps new jigs off lane ends.

### 11.4 Limits [inferred unless marked]

- That a route with the jig on its ignore list plans and walks through the jig's footprint is taken from the
  game's own use for the lying pet; not run for other Sims.
- A route may now end where a pet lies; the destination-obstructed event then runs rule 1.
- The walker cannot be pushed while its timer runs (as `simpass`).
- Rule 3 reads the lot's object list once per autonomous floor lie-down.
- Before the clipwait grace every stand-and-wait cycle now gathers who is in the way (one or two cycles per wait).

### 11.5 Checks (2026-11-22, no game) and run plan

- `node tools/selftest/petrelax/run.ts`: the rule's cases, a sweep of every input combination (go only with a small
  pet in the case, no large body, no object, on and in scope), the lie-down rule, distance test and candidates.
- `build-fixes.ts` builds, `refs-resolve-check` 0 unresolved; selftests `clipwait` and `sharelanes` pass.
- Live: `petrelax-check` once (off `BROKEN`, on `FIXED`, `members found=True`; `fix_applied`
  `PetSurfacePosture.LayDownHere.Run: ok`). A pet household (the storms' cat and horse lots), off / on interleaved
  with `petrelaxlog standwaitlog routefaillog`: off arm 4437 would, 4443 / 4444 lie-downs; on arm `petrelax
  petrelaxhh`: 4430-4435 > 0, pets' `NoPathToDestination` with a Sim blocker and `standwait:` lines naming a pet
  down, 4436 = 0, no `petrelax: error at` line. `PetRelaxCheck lot` lists the pets and each lying pet's distance to
  a lane end. By eye: a cat napping in a doorway (Sims pass through it), a dog following its Sim through a door, a
  pet lying down at a stair foot (walks off first).

## 12. A chore whose spot is closed is done from within reach (`reachuse`, `reachusehh`; 2026-11-22, `feat/reachuse-1122`, built, default off, NOT RUN)

A player's wish: a firefighter, a maid or a Sim cleaning should do the job from a step further away when the place
is blocked, not give up. `ReachUse.cs`, rules in `ReachUseCore.cs`, counters 4446-4461, check
`ReachUseCheck run | result | lot`, selftest `tools/selftest/reachuse`, repro `reachuse-check`.

### 12.1 How the chores walk [verified, decompiled `Sims3.Gameplay` unless marked]

- Every route below ends in `SimRoutingComponent.DoRoute`, whose first test is the plan result: a failed plan runs
  the route-fail behaviour, adds the exit reason `RouteFailed` and returns false (§10.1). A replan that fails during
  the walk ends the same way after the walk.
- Mop (`Terrain.Mopping`: `MopPuddle`, `MopBurnt`): `RouteToPuddle` plans a ring from 1 m round the puddle,
  restricted to the puddle's room and one level; a second plan drops the footprint test near the goal. After the
  walk `PuddleManager.CanMopPuddle` refuses a tile covered by an object or a Sim.
- Fire (`ExtinguishBase.RemoveFire`, pushed for household Sims and by the firefighter's situation as
  `Fire.Extinguish`): `Fire.PlanRouteToExtinguish` plans 0.5-2.5 m in the fire's room, then 0-2.5 m. The caller
  tests the plan before `DoRoute`: a fire whose plan failed is passed over for the next one, and with none left
  nothing is walked. A burning Sim (`Sim.ExtinguishSim`) is a 2 m ring round the Sim, straight into `DoRoute`.
- Pick up, clean away (`CarrySystem.PickUp`, trash and ash piles): `RouteToObjectRadius` in the object's room when
  the thing lies on the floor; a thing on a surface is reached by the surface's routing slots.
- Plants (`Plant.DoPlantRoute`): a ring from 0.7 m round the soil, in its room.
- Wash up at a sink, clean a toilet / tub / stove / counter, repair, fill a pet bowl: routing slots of the object
  (§10's case).
- Picking up a baby or toddler (`PickUpChild`) is a social interaction with its own jig: not a chore route.

### 12.2 Measured (storm logs `phase-*`, `routefail:` lines, read-only)

57,664 failed plans (spot probes left out). "Near" = the goal within 4 m of the Sim on its level. The logs hold no
`standwait: ... near:` lines.

| interaction | failed | near | how it fails near |
|---|---|---|---|
| MopPuddle | 3,614 | 2,296 | no path 743, no destination 1,027, portal locked 512 |
| UseToilet | 2,960 | 1,656 | §10 |
| socials (A, B) | 3,884 | 2,779 | a Sim as target: out of scope |
| WashDishes | 1,253 | 1,146 | sink slots: no destination 841, no path 183, portal locked 96 |
| Fridge / serving (eat) | 1,646 | 1,271 | object use, §10 |
| Clean (toilet, counter, sink) | 468 | 331 | slots |
| PutAwayLeftovers | 172 | 153 | plate: no destination 99, no path 31 |
| ServingContainer_CleanUp | 852 | 112 | plate / bowl |
| PutDownHeldObject | 101 | 92 | the walk to a surface, not to the target |
| CleanSink / CleanBathtub / Stove_Clean | 235 | 171 | slots |
| Snack_CleanUp | 74 | 68 | no path |
| RecycleNewspaper | 133 | 55 | |
| GetHomework | 47 | 47 | no path 35 |
| RepairShower | 68 | 37 | slots |
| PutCarriedChildInCrib | 72 | 55 | crib slots, §10 |
| FillBowl / UseLitterBox / CleanLitterBox / DisposeTrash | 66 | 39 | |
| ReactToFire | 3,206 | 10 | fleeing, not a chore |
| Extinguish | 8 | 0 | the fire's plan is tested before the route: failures do not reach the log |

Gardening interactions do not appear (the storm lots have no gardens).

### 12.3 What is built

1. Point routes in `DoRoute` (the hook of §10): a failed plan with no slot destination, either the first plan or
   a replan that ended the walk (the walk answers false, the plan is failed, no exit reason on the Sim; a cancelled
   or interrupted walk keeps its answer). Plan results taken: no path, no destination, obstructed, destination
   blocked by a Sim (the chore is done from beside it); "portal locked" and aborted plans keep the game's
   behaviour. The Sim's running interaction gives the kind and the reach:

   | kind | interactions | reach | why |
   |---|---|---|---|
   | fire | `ExtinguishBase`, `ExtinguishSim` | 4 m, not nearer than 0.5 m | the game's own ring ends at 2.5 m; the spray is drawn from the Sim to the fire |
   | floor | `Terrain.Mopping` | 2.5 m | the game mops from 1 m; a tile and a half more |
   | small | targets `TrashPile`, `AshPile`, `Newspaper`, `Homework`, `LitterPile`, `FoodScrap`, `ToyScraps`; names with `CleanUp`, `PutAwayLeftovers`, `Recycle`, `PickUp`, `ThrowAway`, `DisposeTrash`, `PutInInventory` | 2 m | the game picks up from about 0.5 m; an arm and a tile more |
   | plant | any interaction on an object of `Sims3.Gameplay.Objects.Gardening` | 2 m | the game tends from 0.7 m |

   The route must be the one to the interaction's target (its goal within 1.5 m of the target; a terrain target's
   goal is the place). A Sim that stands within reach, on the target's level (1.5 m in height), with no wall between
   (`World.WallExistsBetweenPoints`, light-blocking walls, the test of the game's own line-of-sight helper) and
   sits or lies in nothing turns to the target and the route answers "arrived". Otherwise a normal plan to the
   nearest point within reach is made, in the target's room first, then in any room; its end must pass the same
   test; the Sim walks it and the route answers "arrived". The interaction then runs where the Sim stands.
2. Fires: `Fire.PlanRouteToExtinguish` (new swap, plans only) answers a failed plan with the plan of rule 1 (a
   plan to where the Sim stands when it is in reach already), so the fire is not passed over.
3. Slot chores (kind chore: names with `Clean`, `WashDishes`, `Repair`, `Unclog`, `FillBowl`, `EmptyTrash`,
   `TakeOutTrash`, `MakeBed`; and the kinds of rule 1 when the route goes to slots): a failed plan to the slots of
   the interaction's target, or of the surface the target lies on, gets §10's rules (walk up, clip, snap onto the
   slot, out again) with `reachuse` / `reachusehh` standing in for `blockeduse` / `blockedusehh`. The animations
   there need the slot.
4. A puddle tile covered by an object (not a Sim) is mopped when `mopunreachable`'s run is in use
   (`MopUnreachable.cs`, its copy of the game's tile step); the game's own run keeps refusing it.

Scope: `reachuse` = Sims the player does not control (townies, service Sims, firefighters, maids, repairmen),
`reachusehh` = household and selected Sims; each stands alone. Humans, not riding, not in a vehicle. Go Here and
every other interaction are untouched. A case that kept the game's behaviour is not tried again for 30 sim-minutes
(per Sim and goal tile). Off = compare mode. `reachuselog` writes one `reachuse:` line per act (capped).

Counters: 4446 failed point plans of a reach kind / 4447 would, off / 4448 exempt (household without `reachusehh`)
/ 4449 fire / 4450 floor / 4451 small / 4452 plant done from range / 4453 of them without a walk / 4454 slot chores
given to §10's rules / 4455 kept: nothing in reach / 4456 kept: other level or lot / 4457 kept: wall between /
4458 walk stopped / 4459 errors (`reachuse: error at <site>`) / 4460 kept: cooling down / 4461 other failed point
plans.

### 12.4 Not built, and why

- `DestinationPortalLocked` (512 of MopPuddle's near failures): a privacy-locked room, §7's subject.
- Walking through objects to a point (clip) for point routes: the Sim would end inside furniture with no slot to
  come out of. Slot routes have it (§10).
- Picking up a baby or toddler: a social with a two-person jig; a Sim as target is only taken for a Sim on fire.
- `PutDownHeldObject`, the walk of a clean-up to the dishwasher, sink or trash can: slots of another object than
  the target; `blockeduse` covers them.
- Burning windows (`Windows.PlanRouteToExtinguish`, a cone in front of the window), pets, the world lot.

### 12.5 Limits [inferred unless marked]

- The interaction's animation plays where the Sim stands: a mop or a pick-up 2 m from the thing shows a gap, a
  picked-up thing jumps into the hand. The mop jig is put on the puddle as the game does [verified].
- `WallExistsBetweenPoints` is read as "a wall crosses the straight line"; a doorway in that wall still counts as
  a wall, so a target seen through a door is kept.
- `GetApproximateOriginalDestination` of a failed plan is taken as the ring's centre; when it is not valid the
  target's place is used.
- The fire plan for a Sim in reach is a plan to its own place; `DoRoute` is expected to answer true for it.
- Replans are most of the failures (MopPuddle near: first plan 434, replan 1,091, plan from a point 765; WashDishes
  near: first plan 10, replan 945), so the hook's entry method `RouteHook` was changed once to pass a walk that
  ended on a failed plan to the rules (`RouteEnded`). [verified, docs/242 §2] A save made by a build with §10's
  hook (2026-11-22 builds only, never released) resets the Sims that were mid-route once at its first load. §10's
  slot rules get such walks too, and their counters 4414-4429 now include them.
- After a failed replan the game may have played its route-fail reaction inside the walk before the chore is done
  from where the Sim stands.
- Which plan a `PlanFromPoint` failure belongs to is not known from the logs; one that does not end in `DoRoute`
  is not covered.

### 12.6 Checks (2026-11-22, no game) and run plan

- `node tools/selftest/reachuse/run.ts`: 53 checks (plan results, kinds, reach per kind, the goal test, the place test, the slot
  rule, the covered tile, scope). `build-fixes.ts` builds, `refs-resolve-check` 0 unresolved; selftests
  `blockeduse`, `clipwait`, `petrelax`, `standwait`, `sharelanes` pass; `swap-stack-audit` has the new row
  (`Fire.PlanRouteToExtinguish`, stub `PlanOrig`).
- Live: `reachuse-check` once (off `BROKEN`, on `FIXED`, `members found=True`; `fix_applied`
  `Fire.PlanRouteToExtinguish: ok`). A storm save, off / on interleaved with `reachuselog routefaillog`: off arm
  4446 and 4447 up; on arm `reachuse reachusehh` (with `mopunreachable` for rule 4): 4450 > 0, `routefail:` lines
  of MopPuddle with `Plan ... FailedBecauseNo(Path|Destination)` down, 4459 = 0, no `reachuse: error at` line.
  Before the arms: save with a Sim mid-route and load it (the `DoRoute` hook's entry changed).
  `ReachUseCheck lot` lists the puddles, burnt tiles, fires, piles and plants the game's plan cannot reach and
  whether a place in reach exists. By eye: a puddle in a nook behind a chair (mopped from the open side), a
  kitchen fire behind a counter with the firefighter called.

## 13. A bed half that cannot be reached is made from the other side (`makebedboth`, `makebedbothhh`; 2026-11-22, `feat/makebedboth-1122`, built, default off, NOT RUN)

A player's wish: when half of a bed cannot be reached, the Sim makes it from the other side and the whole bed is
made. `MakeBedBoth.cs`, rules in `MakeBedBothCore.cs`, counters 4462-4469, check
`MakeBedBothCheck run | result | lot`, selftest `tools/selftest/makebedboth`, repro `makebedboth-check`.

### 13.1 How Make Bed works [verified, decompiled `Sims3.Gameplay.Objects.Beds` unless marked]

- **The walk.** `BedMakeBed.Run` collects the routing slots of every part that is unmade with no Sim in it and
  walks to one (`RouteToSlotList`). A double bed has two parts (`BedData`, slots 0 and 1), a single bed too
  (`BedSingleData`, slots 4 and 5: its two sides).
- **The run.** State machine `BedMakeBed`, `Enter_MakeBed_<Route0|Route1>`, loop `MakeBed`,
  `Exit_MakeBed_<...>`, then `BedMade = true` on that part. When the other part is unmade the interaction pushes
  itself again as a continuation.
- **The bug.** When the other part's slot is closed the continuation's walk fails. The bed stays half made, the
  definition's `Test` passes for any unmade part not in use, so autonomy offers the bed again and fails again,
  and the room keeps the unmade bed's penalty. A bed whose only unmade part is the closed one fails on the first
  walk. With `blockeduse` (§10) Sims get into and out of a closed half, which makes such halves common.
- **The look.** No script call sets a half's made or unmade look. `BedData.BedMade` keeps a flag and the "dirty"
  alarm; `Bed.DoReset` sets the flags only. The look is the bed model's last animation pose, left by the bed's
  track of the get-out and make clips.
- **The clips** [verified, JAZZ `BedMakeBed` in `Game/Bin/Jazz/JazzData.package`, CLIP names from
  `FullBuild0`]. The Sim plays `a2o_bed_make_start / pat / smooth / stop_x`, the bed `..._bed`, as two tracks.
  `Route1` mirrors both. The parameter `MakeOppositeSide` changes the bed's mirror alone: at `Route0` the bed is
  mirrored when it is set, at `Route1` the bed is mirrored when it is not set. So with the parameter the Sim plays
  at the side it stands on and the bed plays the other half. The Sim is not moved: its clips are relative to its
  own slot.
- **Who sets it.** `BedData.SetPartParameters` sets it false; `BedSingleData` sets it to "other part unmade".
  A single bed's parts copy "made" to each other and Make Bed walks to the unmade part, so in the game the
  parameter is never true during Make Bed [inferred].

### 13.2 What is built

At the start of `BedMakeBed.Run` (swap with stub `RunOrig`), for a bed with two plain parts (`BedData` or
`BedSingleData`; no tent, igloo, pod, upper bunk) of which exactly one is unmade and free:

1. A route plan to that part's slots succeeds: the game's run. Double beds whose both sides are open are walked
   round as before.
2. Otherwise, when the other part is made, has no Sim and is not in use, and a plan to its slots succeeds: the two
   parts' made flags are swapped for the run. The game's own run then walks to the other part's slot (no walk when
   the Sim stands there after the first half), animates there and sets that part made at its end. Both parts are
   made, the game pushes no continuation, a Murphy bed gets its Close, the events fire as in the game.
   `BedData.SetPartParameters` and `BedSingleData.SetPartParameters` (two swaps, no yield) set
   `MakeOppositeSide` for that part while this Sim is in it, so the bed animates the far half.
3. The run does not end with that part made (walk failed, part taken, reset, error): the flags are put back.

Both halves unmade and one closed: the game's run makes the open one, and its continuation is case 2. The plans
are read-only (`Route.PlanToSlot`), at most two per Make Bed run on such a bed. With the bed made `Test` no longer
passes, so autonomy stops offering it. No change to `Test` itself.

Scope: `makebedboth` = Sims the player does not control, `makebedbothhh` = household and selected Sims, each
stands alone. Off = counted (`would`), nothing changed. `makebedbothlog` writes one `makebedboth:` line per act.

| Counter | Meaning |
|---|---|
| 4462 | Make Bed runs on a bed with exactly one unmade free half |
| 4463 | second half made from the first side |
| 4464 | closed half made from the other side (first make) |
| 4465 | kept: the half can be reached (the game's walk) |
| 4466 | kept: no side to make it from (other half taken, or closed too) |
| 4467 | errors (`makebedboth: error at begin | end | parameter | run`) |
| 4468 | would: switch off, or household without `makebedbothhh` |
| 4469 | run ended early, flags put back |

### 13.3 Limits [inferred unless marked]

- **By eye, not yet seen.** The Sim pats the half in front of it while the far half is the one that moves. The
  mirrored bed track on a double bed with the Sim's unmirrored track is the game's own state-machine path, but one
  the game never takes, so the far half's look after it needs a look in the game. If the track does not move the
  far half, the flags are still right (no retries, no penalty) and that half looks unmade until it is next used.
- The bed's mirror is not cleared on the `Route0` exit path of the state machine. Whether that outlives the state
  machine is not known.
- During the walk in case 2 the flags are swapped: another Sim's Make Bed can pick the same part, as it can in the
  game for any unmade part. A Sim that gets into the part first makes the game's run fail and the flags go back.
- A save made during such a run resumes in the stub (the game's run): the bed ends made, the parameter is not set
  again after the load.
- The closed half's "dirty" state restarts its 30-minute alarm when the flags are put back.
- With `blockeduse` on as well this rule comes first: the Sim stays at the open side.

### 13.4 Checks (2026-11-22, no game) and run plan

- `node tools/selftest/makebedboth/run.ts`: 406 checks (the decision table over every input, the end of the
  run, second half or first make, the parameter, scope). `build-fixes.ts` builds; selftests `blockeduse` and
  `reachuse` pass; `swap-stack-audit` has the three new rows (`BedMakeBed.Run` yields, stub `RunOrig`).
- Live: `makebedboth-check` once (off `BROKEN`, on `FIXED`, `members found=True`). A household with a double bed
  whose one side stands at a wall or behind furniture, `blockeduse blockedusehh` on so both halves get used:
  off arm 4462 and 4468 up, the bed stays half made; on arm `makebedboth makebedbothhh makebedbothlog`: 4463 or
  4464 up, `makebedboth: ... made from the ... side`, 4467 = 0, no `makebedboth: error at` line, the bed no
  longer offered. `MakeBedBothCheck lot` lists the lot's beds with an unmade half and the plan to each part.
  By eye: the far half's look after the run (13.3).

## 14. Sims share rabbit hole doors and serving platters; line waits logged (`rhshare`, `servingshare`, `linewaitlog`; 2026-11-22, `feat/objshare-1122`, built, default off, NOT RUN)

A player's wish: Sims share more things, clipping is fine. Privacy objects (toilet, shower, bath, bed) are not
touched. Source `ObjShare.cs`, rules `ObjShareCore.cs`, counters 4470-4493, `kCounterSlots` 4494, check
`ObjShareCheck`, selftest `tools/selftest/objshare`, repros `objshare-check`, `rhshare-rush`, `servingshare-buffet`.

### 14.1 Where Sims queue at objects [verified, decompiled `Sims3.Gameplay` unless marked]

- `SimQueue.WaitForTurn` is the one line of the game: 58 classes own one. A Sim is entered, walks behind the Sim
  before it, and its place is looked at once per check period; after the timeout it gets `ObjectInUse` + `TimedOut`
  and the route-fail balloon. After the line most objects test `InUse` again.
- `RabbitHole.AnimateEnterRabbitHole` calls `WaitForEnterSlot` before each walk to its enter slot. One Sim holds
  the slot (`ReservedEnterSlots`, `ReservedEnterSlotsPerPerson`) until that walk has ended; the caller's `finally`
  takes the reservation off and names the nearest Sim of `WaitingToEnterSims` as the next holder. The waiting loop
  sleeps 10 ticks a turn, never looks at exit reasons (a cancelled Sim stays), and breaks a reservation only after
  15 sim-minutes with the same holder. The caller's retry path takes the slot's reservation off without asking
  whose it is. Riders and horses come through the same call.
- `ServingContainerGroup.RunBodyForGrabAServing` (platters, buffet food, pizza through its override's base call):
  a walk near the platter, the platter's line (`servings - 1` long, 10 sim-minutes), the surface slot's walk (which
  waits in the surface's line when it has one), then `InUse` -> `ObjectInUse`, `HasFoodLeft`, `StandardEntry`, the
  state machine `TakeAServing` (actors: the Sim and its own plate) and `DecrementServings` after the take. Food on
  an `IBuffetTable` is never decremented. Nothing but the line and the use list keeps two Sims from both passing
  `HasFoodLeft` on the last serving.

### 14.2 What is built

- `linewaitlog` (log only). `LdsQueueHook.WaitHook` (sharelanes' swap of `WaitForTurn`, no second swap) times the
  game's body: from half a sim-minute one line and counters 4470-4473; shorter ones 4493, a refusal at once at a
  line with Sims in it 4492. The enter-slot hook below does the same for every wait at a held slot (4474-4477).
  `linewait: <Sim> npc|household|selected, <interaction>: <TargetType> line|rhdoor, waited N sim-min, line length L, outcome got turn|timed out|cancelled|shared, at x,y,z`.
  Line: `timed out` = refused after the whole timeout, `cancelled` = refused earlier. rhdoor: `timed out` = the
  wait lasted the 15 sim-minutes after which a reservation is broken, `shared` = rhshare let the Sim walk.
  The counters run with the switch off. With `linewaitlog` on rhshare and servingshare write one line per act too.
- `rhshare` / `rhsharehh`: swap of `RabbitHole.WaitForEnterSlot` (yields, stub `WaitOrig`). When another Sim
  holds the slot:
  - holder in scope and a plain walker: the Sim returns at once and walks to the slot. It takes no reservation and
    is not in the waiting list, so the holder's `finally` and the hand-over are what they were (4479).
  - the reservation names no living Sim: it is removed, the game's code reserves for this Sim (4484).
  - holder out of scope, or a horse / rider / carrier: the Sim waits in our loop, entered in the waiting list. It
    is the game's loop with three more exits: an exit reason on the Sim (4483; the caller's walk then fails and it
    ends), the holder changing to one it may share with, and a dead holder. After 15 sim-minutes with one holder
    the reservation is removed as the game does. Then the game's own code runs (free slot or handed over).
  - horses, riders, Sims carrying a child and toddlers keep the game's wait (4482 with the kept holders).
- `servingshare` / `servingsharehh`: swap of `ServingContainerGroup.RunBodyForGrabAServing` (yields, stub
  `GrabOrig`) opens a run for a Sim in scope when every Sim in the platter's line and use list is in scope; the
  game's own body runs inside it.
  - `WaitForTurn` for a Sim whose run has not begun its take answers true without entering the Sim, for the
    platter's line and the surface's, while the Sims in that line are in scope (4486 when somebody was there).
  - `ServingContainerGroup.HasFoodLeft` (swap, no yield; one integer test while no run is open): the run's first
    call is the test before the take. Servings that other runs are taking now count as gone; with none left it
    answers false and the game's run ends as at an empty platter (4491). Otherwise the serving is spoken for. The
    run's second call is the game's test after `DecrementServings`, which ends the reservation. Buffets always
    have food. Other callers get the game's answer.
  - `StateMachineClient.SetActor` (actdelay's swap, one integer test while no run is open): the first call for a
    Sim that has passed the food test is its take; the Sim leaves the platter's use list there, before the first
    yield, so the next Sim's `InUse` test passes (4487 counts takes that began while another was running).

### 14.3 Limits [inferred unless marked]

- Scope is not exact at the platter: a Sim out of scope that arrives while a Sim in scope takes finds the line
  empty and the platter free, so it does not wait either.
- While a Sim takes (a few seconds) the platter is not in use for other interactions (pick up, clean up, drag).
  A platter destroyed in those seconds would make the game's `DecrementServings` run on a dead object.
- A Sim that leaves the enter-slot wait on a cancel is handed to the caller's walk, which has to fail on the exit
  reason. If a route ignores it the Sim walks to the slot and enters.
- A sharing Sim whose walk fails runs the game's retry path, which takes the holder's reservation off; the next
  waiting Sim is then named while the holder still walks. The tables stay consistent, Sims overlap.
- Sims walking to one slot stand in each other's footprint at the door: the obstacle wait there is `clipwait` /
  `simpass`' business (§8, §9).
- `LdsQueueHook.WaitHook` changed: Sims saved in a line by an older build are reset once at the first load
  (docs/242 §2).
- A serving run's identity is the Sim's task: a `HasFoodLeft` call on the same platter from that task before the
  game's own test would be taken for it (none found in the walk).

### 14.4 Checks (2026-11-22, no game) and run plan

- `node tools/selftest/objshare/run.ts`: 29 checks. `build-fixes.ts` builds; selftests `sharelanes`, `clipwait`,
  `standwait` pass; `swap-stack-audit` has the three new rows.
- Live: `objshare-check` once (off `BROKEN`, on `FIXED`, one switch on `MIXED`). Then a storm with the switches
  off and `linewaitlog` on to rank the lines by 4470-4477, before any on arm.
- `rhshare-rush`: a town at 08:00-09:00 on a weekday (school and work), or a festival / showtime venue. Off arm:
  4474 / 4475 and `rhdoor` lines; on arm `rhshare rhsharehh`: 4479 up, 4475 near zero, no `objshare: error at`.
  Cancel a household Sim that waits behind a held slot (hh off): 4483.
- `servingshare-buffet`: a party or a community lot with a buffet table / a platter on a counter and 4+ hungry
  Sims. Off arm: `ServingContainerGroup... line` waits; on arm: 4486 / 4487 up; a platter with 2 servings and 3
  Sims: 4491 once, servings never below 0 (`ObjShareCheck lot`).

## 15. Partners shower together; a Sim with no usable toilet takes a shower (`showertogether`, `showerpee`; 2026-11-22, `feat/showershare-1122`, built, default off, NOT RUN)

A player's wish: romantic Sims shower together (they clip and come out one at a time), also in the all-in-one
bathroom, and a Sim that has to pee with the toilet busy or no toilet on the lot does it in the shower, as a
shower: no new interaction. Source `ShowerShare.cs`, rules `ShowerShareCore.cs`, counters 4494-4509,
`kCounterSlots` 4510, check `ShowerShareCheck`, selftest `tools/selftest/showershare`, repro `showershare-check`.

### 15.1 How the game's shower, all-in-one bathroom and bladder failure work [verified, decompiled `Sims3.Gameplay` unless marked]

- `Shower.TakeShower.Run` (target `IShowerable`: every shower and shower tub): `SimLine.WaitForTurn` (line of 2,
  waits outside the room's door, evicts after 15 sim-minutes), `RouteToSlotAndCheckInUse(RoutingSlot_0)`, a
  `ShowerPrivacySituation`, `StandardEntry` (use list), a second walk to the slot, the outfit change, its own
  "Shower" state machine (actors `x` and `Shower`), `SimInShower = Actor`, the loop until hygiene is full, then
  `WaitToLeaveShower`, the exit animation and `StandardExit`.
- `SimRoutingComponent.RouteToSlotAndCheckInUse`: `PreRouteCheckForLine` (asks the line once more, never
  waiting), `RouteToSlot`, then `InUse` -> exit reason `ObjectInUse`.
- `WaitToLeaveShower`: `EnableFootprintAndPushSims(824351308, actor)` pushes every other Sim off the exit
  footprint, then sleeps while `IsRoutingSlotObstructed(actor, RoutingSlot_0)`.
- Privacy: `ShowerPrivacySituation.OtherSimCanIgnorePrivacy` is true for a Sim in a romantic relationship
  (`Sim.CanIgnoreNormalPrivacySituationsWith` -> `IsInRomanticRelationshipWith`: the relationship's long-term state
  is romantic). A partner is neither shooed out nor reacted to; nothing had to change there.
- The game's own second Sim in a shower is `Shower.WooHoo`: actor `y` on the first Sim's state machine, on
  `RoutingSlot_0`. There is no "shower with" interaction to unlock, so a partner runs its own Take Shower with
  its own state machine on the same slot.
- `AllInOneBathroom.UseAllInOneBathroom`: its line (6, cut ahead of lower priority), the same in-use walk, no
  privacy situation, a footprint shut while a Sim is inside, the Sim fades out; hygiene and the bladder fill
  (bladder in 5 sim-minutes). No exit wait.
- Bladder: `Motive.MotiveDistress(sim, Bladder)` runs when the motive is at its minimum and queues
  `Sim.BladderFailure` (puddle, embarrassment) unless one is queued. `BuffReallyHasToPee` is added shortly before.
  Toilets fill the bladder with a motive delta on the interaction (`Motives.LerpToFill`).

### 15.2 What is built

`showertogether` (NPCs) / `showertogetherhh` (household and selected Sims too; both Sims must be in scope):

- Rule (`ShowerShareCore.Join`): exactly one other Sim uses the object or is in its line, it runs the same
  interaction there (Take Shower, or the all-in-one bathroom's use; not a bath, a repair, a WooHoo), both are
  humans and no robots, both young adults or older, the game's romantic test holds, and their liking is not
  below zero.
- The line: `LdsQueueHook.WaitHook` (sharelanes' swap of `SimQueue.WaitForTurn`) returns at once for such a Sim
  and notes the two as the object's pair. A third Sim keeps the line.
- The in-use test: swap of `SimRoutingComponent.RouteToSlotAndCheckInUse` (yields, stub `InUseOrig`). Switch
  off: the game's body. Switch on: the game's three steps in the hook, with "in use" passing while every Sim
  using the object is of its pair (so a pair formed during a walk still counts after it). For an all-in-one
  bathroom the partner's walk first opens the footprint the Sim inside shut.
- The exit (showers): swap of `GameObject.PushSimsFromFootprint` (yields, stub `PushOrig`) adds the partner to
  the push's exceptions for footprint 824351308, and swap of `GameObject.IsRoutingSlotObstructed` (8 arguments,
  no yield, stub `ObstructedOrig`) answers the game's wait for a Sim of a pair: obstructed while the partner
  plays its exit and is still in the use list (cap 10 sim-minutes), free otherwise. So the partner in the
  shower does not hold the leaving Sim, and two leaving Sims come out one after the other.

`showerpee` / `showerpeehh`:

- Rule (`ShowerShareCore.Pee`): a human Sim, child or older, no robot, not swimming, on a lot; no toilet, urinal
  or all-in-one bathroom there that is not broken, not used by another Sim, with nobody else in its line (a
  urinal only for a male Sim); and a shower that is not broken and free, or held by a partner `showertogether`
  lets the Sim join.
- At the last warning (swap of `BuffReallyHasToPee.OnAddition`, stub `AddOrig`): the game's Take Shower is
  queued next at high priority, unless the Sim is selectable and has orders from the player.
- At the failure (swap of `Motive.MotiveDistress`, stub `DistressOrig`): the same shower is queued instead of
  `BladderFailure`, and further distress calls are held back while that shower is queued or runs. A shower that
  has not begun within 30 sim-minutes, or that left the queue, gives the failure back to the game, and the Sim is
  not sent again for 60 sim-minutes.
- The bladder: a Take Shower started by such a Sim (pushed or of its own accord, noted in the line hook) gets a
  bladder delta when the shower becomes an actor of its state machine (`StateMachineClient.SetActor`, ActDelay's
  hook; 5 sim-minutes to full), and the bladder is set full when the Sim steps out. Hygiene is the shower's own.
- An all-in-one bathroom is a toilet for this rule: when it is free the game uses it.

`showersharelog`: one `showertogether:` / `showerpee:` line per act.

### 15.3 Limits [inferred unless marked]

- Not run. Open questions for the first run: whether a walk to `RoutingSlot_0` succeeds while the partner stands
  on it (the game's own shower WooHoo does exactly that), whether two "Shower" state machines on one shower
  fight over the door and water effects (they overlap; the first exit closes the door on the second Sim), and
  whether the all-in-one bathroom's shut footprint covers its routing slot.
- Autonomy does not offer a shower that is in use, so a join happens for a directed Sim, for a Sim whose shower
  was queued by `showerpee`, and for a Sim that was already walking there. No autonomy change was made.
- `SimInShower` is the last Sim that began, and is cleared by the first that ends: "WooHoo in shower" may be
  missing on the shower until the next shower begins.
- All-in-one bathroom: no exit wait exists in the game, so both exits may overlap; the second Sim's use starts
  no second sound, and the first exit stops the sound.
- A toilet that cannot be reached counts as usable (no route test is made); a toilet on another level too.
- `showerpee` sends a visitor into the host's shower; the alternative was a puddle.
- The failure is held back for up to 30 sim-minutes while the Sim walks to the shower; its bladder stays at the
  minimum for that time.
- With `showertogether` on, every `RouteToSlotAndCheckInUse` in the game runs through the hook's three steps
  (the same calls in the same order).
- `LdsQueueHook.WaitHook` changed: Sims saved in a line by an older build are reset once at the first load
  (docs/242 §2).

### 15.4 Checks (2026-11-22, no game) and run plan

- `node tools/selftest/showershare/run.ts`: 41 checks. `build-fixes.ts` builds; selftests `objshare`,
  `sharelanes`, `standwait` pass; `swap-stack-audit` has the five new rows.
- Live: `showershare-check` once (off `BROKEN`, on `FIXED`, one switch on `MIXED`).
- Forced repro, partners (outline, existing agent steps): `make_household` with two young adults on an empty
  lot; `build_layout` one room with a door; `buy_object` one shower (then a shower tub, then an all-in-one
  bathroom); `modify_relationship` to Partner (and a control pair: friends; a teen); `set_motive` Hygiene -80 for
  both; `do_interaction` Take Shower for the first, the same for the second once the first is in the use list
  (`ShowerShareCheck lot`). Off arm: the second waits at the door, 4495 up. On arm (`showertogether`
  `showertogetherhh` `showersharelog`): a `showertogether: ... joins ...` line, 4494 up, both in the use list,
  no shoo, 4500 up when both end together, both end standing outside, 4501 = 0.
- Forced repro, bladder (outline): the same lot without a toilet (or with the toilet in use by the other Sim);
  `set_motive` Bladder -95 for the active Sim, no orders. Off arm: the puddle, 4506 up. On arm (`showerpee`
  `showerpeehh`): `showerpee: ... takes a shower ...` and `... bladder fills ...` lines, 4503 or 4504 and 4502
  up, no puddle, bladder full after the shower. Control: a free toilet on the lot -> the game's way, nothing
  counted; no shower -> 4505 and the puddle.
