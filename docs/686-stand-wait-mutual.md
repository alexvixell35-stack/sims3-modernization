# 686: household Sims standing at a stair top or doorway: EA's obstacle stand-and-wait never pushes or yields once its replan fails

2026-11-21, branch `fix/stand-wait-1121`. Storm logs s141-s291 (Isla Paradiso, Collins household, 2-minute route storms) +
decompiled C#. Built, default off, **NOT RUN**. Switches `standwaitfix`, `standwaitfixhh`, `standwaitlog`; counters
4300-4319; `StandWait.cs`, `StandWaitCore.cs`; repro `standwait-check`.
§7 (waits at a privacy-locked room, `standwaitlocked`) and §9-§13 stay on the contributor's combo branch for now; §8 is
in this file. **Correction to §1-§2:** the wait in the tuning XML is 20 sim-minutes, not 50; "50" below is the default
in the code.

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

## 8. Sims share stairs, doors and ladders and walk through each other (`sharelanes`, `laddershare`, `simpass`, `angleentry`; 2026-11-21, `feat/sharelanes-1121`, built, default off; storms s444-s495, see §8.7)

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
| `simpass` (`simpasshh`) | Only Sims in the way (toddler or older), none within 0.6 m of the route's end, walker not already ignoring: `StartIgnoringObstacles(kSimPassMinutes = 3)`, then the game's handler runs and skips them. **At once** (at the obstacle event, or `ContinueAndFollowPath` at a wait that still starts) only near a portal; anywhere else the wait begins and its first cycle after `kClipGraceMinutes` ends it the same way (§8.8). | `StartIgnoringObstacles`, the game's exit from a wait in a crowd |
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
  4. Repros `upstairs-landing-jam`, `standwait-landing-crowd`, `ladderfoot-force` with the pairs on (the last two need agent steps and loads that are not upstream yet).
- Pass: errors 4367 = 0, no new script errors, no Sim stuck in a portal (`StairWaitEndCheck result` waits asleep 0
  after the storm), door left open with nobody near = fail (use count), no new routefail kinds or `route false`
  beyond the off arms, `gohere end` lines stay `route ok`.

### 8.7 Storm results (2026-11-22, Steam Deck, Isla Paradiso test lot)

16 route storms s480-s495 (2-minute storms, the same load and seed each time). On = `sharelanes`, `laddershare`,
`spiralshare`, `simpass`, `angleentry`, each with its household switch. Both arms had `standwaitfix`, `standwaitfixhh`,
`standwaitlog`, `lanewaitlog` and `routefaillog` on (and the combo branch's `standwaitlocked`, §7, which is not in this
file), so the table compares the lane switches on top of `standwaitfix`; `standwaitfix` itself was not switched.

| | off arms | on arms |
|---|---|---|
| lane waits (`lanewait:`) | 11 waits, about 26 sim-minutes in all | 0 |
| obstacle stand-and-waits with blocked cycles (`standwait:`) | 1 wait of 13.1 sim-minutes | 0 |
| townie waits at the spiral stair | 7-12 sim-minutes each | none |
| `sharelanes` errors / crashes | - | 0 / 0 |
| hitches of 500 ms or more | none | none |

Per on-storm on average: `simpass` acted about 55 times, `angleentry` about 270 times, the spiral stair's line was
skipped about 12 times. Earlier batches s444-s473 (after the fix for a ladder with an empty `SimQueue`, which threw in
the line hook): errors 0. Not watched by eye yet: how two Sims crossing on stairs, a door or a ladder look, and the
angled stair and ladder entry.

### 8.8 The timing rule: at once only at portals (2026-11-22, built, NOT RUN)

A player's rule: "doors and shower and stairs etc can be almost always but other things should have at least that
2 or 3 second wait". Clipping is not the standard. It binds `simpass`:

| Where | What walks through at once | What waits first |
|---|---|---|
| Portals: doors, gates, stairs, ladders, spiral stairs, elevator lanes (`sharelanes`, `laddershare`, `spiralshare`, `angleentry`) | lane sharing as built; `simpass` when the walker or a Sim in its way is within `kClipPortalNear` (1.5 m) of a portal lane, on the stairs included | nothing |
| Anywhere else: open space, hallways, rooms | nothing | the game's stand-and-wait runs `kClipGraceMinutes` (2.5 sim-minutes, 2-3 seconds at normal speed), with its own replans and pushes; then `simpass` ends it |

- **Near a portal** (`ShareLanesCore.NearLane`): the distance in three dimensions from a Sim to the line between a
  lane's two ends is under `kClipPortalNear`. So a Sim anywhere on the stairs, in a doorway or within 1.5 m of a
  lane end is near; a Sim on the floor above a door is not. The lanes of a lot are read once per 30 sim-minutes
  (`ShlLanesOf`); the world lot has none. `kClipPortalNear = 0` = the grace everywhere; `kClipGraceMinutes = 0` =
  at once everywhere (the behaviour before this rule).
- **The grace path** is simpass's own: `SpsObstacleCycle` in the obstacle wait's cycle hook (plan intact, only
  Sims in the way, none at the route's end) ends the wait with the game's own "continue and follow the path".
- Counters: no free slot in the block. `ShareLanesCheck result` shows, per session, the waits begun away from a
  portal and left to the grace, and the walks after it. 4381 now counts only walks near a portal; 4382 the waits
  ended at their begin (near a portal) or after the grace.
- Selftest: `sharelanes` (`Timing`: the rule, the near-lane test on a door and a stair lane).
- §8.2 and §8.7: the lane waits, lane shares and line skips are at doors, stairs and ladders and are not touched.
  The `simpass` count of §8.7 (about 55 acts per storm) was taken before this rule; away from portals those walks
  now come after the grace.

## 10. A slot closed by furniture or a wall is used anyway (`blockeduse`, `blockedusehh`; 2026-11-22, `feat/blockeduse-1122`, built, default off; forced repro run, §10.6)

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
  the objects with a closed slot). Repro `blockeduse-check` (rules, counters). The forced repro `blockeduse-force` (a room built with
  `build_layout`, a fridge put on the toilet's slot with the agent's `put_in_front` step) stays on the contributor's
  combo branch until that agent step is upstream; §10.6 gives its results.
- Run plan, about 30 min of game time: 1. load, `ts3fixes applied` must show `SimRoutingComponent.DoRoute: ok`,
  `Route.AddDestination: ok`, `Route.ClearDestinationList: ok`; save and load with the switches off, Sims mid-route
  must come back (`savedstacks` 761 = 0). 2. `blockeduse-force`. 3. By hand: a double bed with one side at a wall and
  two sleepers; a tub with a dresser in front. 4. One storm with both switches on.
- Pass: 4426 = 0, no `blockeduse: error at` line, no new script errors, the Sim ends on a routable spot after use
  (4420 or 4421 follows every 4418 / 4419), no Sim left standing in a wall, `gohere` lines unchanged.

### 10.6 Results (2026-11-22, Steam Deck, Sunset Valley new game)

- Forced repro `blockeduse-force`, run bu-r11 (exit 0): a two-Sim household on an empty lot, a toilet at a wall with a
  fridge on the tile in front of it. Before each arm `BlockedUseCheck lot` showed `ToiletModerate ... 1 of 1 slots
  closed (FailedBecauseNoPathToDestination blocker Object)`.
  - off: `routefail: ... UseToilet ... FailedBecauseNoPathToDestination blocker Object` (the fridge), `would` 1; the Sim
    does not use the toilet.
  - on: `blockeduse: ... clip 1.0 m to slot ..., walked up first`; counters walked up 1, clip 2 (the interaction routes
    to the slot twice), walked out 1, errors 0.
- The same off / on by hand in a second session (bu-m1).
- Not exercised yet: snap in / snap out (a slot the clip plan cannot reach, such as a bed side at a wall). Not
  confirmed by eye: no screenshot of the Sim seated. Not run: a save and load with a Sim mid-route (the `DoRoute`
  swap is installed whatever the switches say, and its stub keeps saved stacks loading).

## 13. A bed half that cannot be reached is made from the other side (`makebedboth`, `makebedbothhh`; 2026-11-22, `feat/makebedboth-1122`, built, default off; forced repro run, §13.5)

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
  walk. With `blockeduse` (§10, a separate change) Sims get into and out of a closed half, which makes such halves common.
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
  run, second half or first make, the parameter, scope). `build-fixes.ts` builds; the selftest compiles
  `BlockedUseCore.cs` too (the scope rule is shared with `blockeduse`); `swap-stack-audit` has the three new rows (`BedMakeBed.Run` yields, stub `RunOrig`).
- The forced repro `makebedboth-force` (a room built with `build_layout`, the bed set up with the agent's
  `bed_setup` step) stays on the contributor's combo branch until that agent step is upstream; §13.5 gives its results.
- Live: `makebedboth-check` once (off `BROKEN`, on `FIXED`, `members found=True`). A household with a double bed
  whose one side stands at a wall or behind furniture, `blockeduse blockedusehh` on so both halves get used:
  off arm 4462 and 4468 up, the bed stays half made; on arm `makebedboth makebedbothhh makebedbothlog`: 4463 or
  4464 up, `makebedboth: ... made from the ... side`, 4467 = 0, no `makebedboth: error at` line, the bed no
  longer offered. `MakeBedBothCheck lot` lists the lot's beds with an unmade half and the plan to each part.
  By eye: the far half's look after the run (13.3).

### 13.5 Results (2026-11-22, Steam Deck)

- Forced repro `makebedboth-force`, run mb-r2: off, one half stays unmade (`would` 1, `Left half closed: would be
  made from the Right side`); on, both halves made (`made from the Right side (second half)`), errors 0.
- By hand, a double bed with one side at a wall (session bu-m1): off, the Right half stays unmade with two
  `routefail: ... BedMakeBed ... FailedBecauseNoDestinationSpecified` lines; on, both halves made.
- Not confirmed: whether the far half's look visibly changes to made (13.3); the counters and the bed's flags say made.

## Import 2026-11-23 (maintainer): PRs #648, #649, #650

Imported by Dalexlol's commits of PR #648 (§1-§8: `standwaitfix`, `sharelanes`, `laddershare`, `spiralshare`, `simpass`, `angleentry`, the `...hh` household variants, `standwaitlog`, `lanewaitlog`), PR #649 (§10: `blockeduse`, `blockedusehh`) and PR #650 (§13: `makebedboth`, `makebedbothhh`, `makebedbothlog`). Stacked PRs #651 (petrelax, clipwait), #652 (reachuse), #654-#656 and #657-#659 are **not** imported. `docs/686` was the same file in all of them; the sections were merged here. Counters kept: 4300-4319, 4364-4397 (PR #648), 4414-4429 (#649), 4462-4469 (#650), `kCounterSlots` 4470; 4320-4363 and 4398-4413 are left to other work (4352-4363 are `ladderfootshoohh`, import 637-644).

| Switch | Default | Why |
|---|---|---|
| `sharelanes`, `laddershare`, `spiralshare` | ON | his natural route-storm A/B (16 storms, Isla Paradiso): lane waits 11 (about 26 sim-minutes) off / 0 on, spiral-stair waits of 7-12 sim-minutes gone, errors 0, no hitch of 500 ms or more. Caveat: both arms also had `standwaitfix`, the on arms had all five lane switches and their `hh` variants, no ms/s or p99 figures were taken, how crossing Sims look was not watched (§8.7). Not household: NPC scope only, see below |
| `blockeduse` | ON | his forced repro `blockeduse-force` (Sunset Valley, run bu-r11): off `FailedBecauseNoPathToDestination` for a toilet behind a fridge, on the Sim walks up (clip) with errors 0, same by hand. The repro itself needs the agent step `put_in_front` that is not upstream, so only `blockeduse-check` is in repros.json. Snap in / out was never exercised in game; the extra save/load test idea is dropped |
| `makebedboth` | ON | his forced repro `makebedboth-force` (run mb-r2): off one half unmade, on both made, errors 0, same by hand. The far half's look was not checked on screen. `makebedboth-check` in repros.json |
| `sharelaneshh`, `laddersharehh`, `spiralsharehh`, `blockedusehh`, `makebedbothhh`, `simpasshh`, `angleentryhh`, `standwaitfixhh` | OFF | household and selected Sims keep EA's behaviour (CLAUDE.md household rule). In code every switch exempts a household, selected or unknown-scope Sim unless its `hh` switch is on. Changed at import: the scope tests of `ShareLanes.cs` (`ShlScope`) and `StandWait.cs` (`SwfInScope`) now use `IsPlayerSim`, not `HouseholdExempt`, so `[Experimental] HouseholdToo` does not lift lane skips, clip-through or path changes; `BlockedUse` and `MakeBedBoth` never read it (the snap branch is never lifted) |
| `simpass` | OFF | the clip-through rule was changed after his storm run (walk through at once only within 1.5 m of a portal, else after 2.5 sim-minutes, §8.8) and is **NOT RUN** in game; walking through Sims is player-visible |
| `angleentry` | OFF | acted about 270 times per storm but there is no per-switch result and the angled entry was not watched by eye |
| `standwaitfix`, `standwaitlog`, `lanewaitlog`, `makebedbothlog` | OFF | `standwaitfix` is unproven alone (both A/B arms had it, §6 run plan open); the others only write log lines |

The `blockeduse` `DoRoute` swap is installed even with the switches off (saves keep a frame in it), so the save change matrix (docs/242 §2) still applies before the swap is relied on; `blockedusehh` and `HouseholdToo` must never lift its snap branch. PR #651 later adds a second swap of `SimRoutingComponent.DoRoute` (not imported). Repros `standwait-check`, `sharelanes-check`, `blockeduse-check`, `makebedboth-check` added to repros.json.
