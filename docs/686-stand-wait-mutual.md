# 686: household Sims standing at a stair top or doorway: EA's obstacle stand-and-wait never pushes or yields once its replan fails

2026-11-21, branch `fix/stand-wait-1121`. Storm logs s141-s291 (Isla Paradiso, Collins household, 2-minute route storms) +
decompiled C#. Built, default off, **NOT RUN**. Switches `standwaitfix`, `standwaitfixhh`, `standwaitlog`; counters
4300-4319; `StandWait.cs`, `StandWaitCore.cs`; repro `standwait-check`.
§7 (waits at a privacy-locked room, `standwaitlocked`), §10 and §12-§13 stay on the contributor's combo branch for now; §8, §9 and §11 are
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
2 or 3 second wait". Clipping is not the standard. It binds `simpass`, `petrelax` (§11) and `clipwait` (§9):

| Where | What walks through at once | What waits first |
|---|---|---|
| Portals: doors, gates, stairs, ladders, spiral stairs, elevator lanes (`sharelanes`, `laddershare`, `spiralshare`, `angleentry`) | lane sharing as built; `simpass` and `petrelax` when the walker or a Sim or pet in its way is within `kClipPortalNear` (1.5 m) of a portal lane, on the stairs included | nothing |
| Anywhere else: open space, hallways, rooms | nothing | the game's stand-and-wait runs `kClipGraceMinutes` (2.5 sim-minutes, 2-3 seconds at normal speed), with its own replans and pushes; then `simpass`, `petrelax` or `clipwait` end it |

- **Near a portal** (`ShareLanesCore.NearLane`): the distance in three dimensions from a Sim to the line between a
  lane's two ends is under `kClipPortalNear`. So a Sim anywhere on the stairs, in a doorway or within 1.5 m of a
  lane end is near; a Sim on the floor above a door is not. The lanes of a lot are read once per 30 sim-minutes
  (`ShlLanesOf`); the world lot has none. `kClipPortalNear = 0` = the grace everywhere; `kClipGraceMinutes = 0` =
  at once everywhere (the behaviour before this rule).
- **One grace.** `kClipGraceMinutes` replaces `kClipWaitGraceMinutes` (2); all three read it.
- **Each switch keeps its own grace path.** `simpass`: `SpsObstacleCycle` in the obstacle wait's cycle hook (plan
  intact, only Sims in the way, none at the route's end) ends the wait with the game's own "continue and follow the
  path". `petrelax`: `PrxClip` in the cycle hooks of both waits. Neither needs the `clipwait` switch; with it on,
  `clipwait` takes the waits the other two leave (a Sim at the route's end, a plan that failed on people, horses).
- `petrelax` rule 2 (§11.2): routes ignore a lying pet's floor jig only when the pet lies near a portal lane.
- Counters: no free slot in the blocks. `ShareLanesCheck result` and `PetRelaxCheck result` show, per session, the waits begun away from a
  portal and left to the grace, and the walks after it. 4381 now counts only walks near a portal; 4382 the waits
  ended at their begin (near a portal) or after the grace.
- Selftests: `sharelanes` (`Timing`: the rule, the near-lane test on a door and a stair lane), `petrelax`
  (`Timing`), `clipwait` (2.5).
- §8.2 and §8.7: the lane waits, lane shares and line skips are at doors, stairs and ladders and are not touched.
  The `simpass` count of §8.7 (about 55 acts per storm) was taken before this rule; away from portals those walks
  now come after the grace.

## 9. Sims walk through a Sim or pet that keeps them waiting (`clipwait`, `clipwaithh`; 2026-11-22, `feat/clipwait-1122`, built, default off, NOT RUN; here as the waits' plumbing for §11)

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

After `kClipGraceMinutes` (2.5 sim-minutes, 2-3 seconds at normal speed; the one grace of §8.8, shared with
`simpass` and `petrelax`; it was `kClipWaitGraceMinutes` = 2) of either stand-and-wait that the
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

`simpass` (§8) acts first where it applies (at once near a portal, elsewhere at the same grace, §8.8; nobody at the route's end); `clipwait`
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

## 11. Pets block less and get blocked less (`petrelax`, `petrelaxhh`; 2026-11-22, `feat/petrelax-1122`, built, default off, rule 1 and 2 run §11.6)

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

1. **Walk through** (`PetRelaxCore.Pass`). A small pet (cat, dog, puppy, kitten, raccoon) walks through the Sims
   and small pets in its way; a Sim walks through small pets when nothing else is in its way.
   `StartIgnoringObstacles(kPetRelaxMinutes = 3)`, the game's own timer; a Sim that also has a Sim in its way is
   left to `simpass` / `clipwait` (the timer ignores everything). **When (§8.8):** at once, at the obstacle event
   (before `simpass`) or at an obstacle wait that still begins, only when the walker or one of those in its way is
   within `kClipPortalNear` (1.5 m) of a portal lane; anywhere else the game's wait runs and the rule ends it in
   the first cycle after `kClipGraceMinutes` (2.5), in both stand-and-waits, with the `clipwait` switch on or off.
   Where the walk may end is `ClipWaitCore.Decide` unchanged (never into a Sim or pet that uses the object the
   walker goes to, never for a social, never at the end of a player's Go Here).
   **Horses, foals, deer** and Sims riding or leading a horse, as walker or in the way: kept (4439); they get the
   `clipwait` grace as any Sim.
2. **A pet lying near a portal is not an object.** `SimRoutingComponent.SetRouteOptions` (every route a Sim
   creates) adds the floor jigs of the lying pets on the walker's lot that lie within `kClipPortalNear` of a portal
   lane (read once, when the pet lay down) to the route's ignore list (`AddObjectToIgnoreForRoute`, the call the
   game makes for the pet itself). A pet that lies anywhere else is walked around as the game has it (§8.8); when
   there is no way around (the plan fails and names the pet's jig as its blocker, a pet across a hallway), the
   walker stands where it is for `kClipGraceMinutes`, then the route is planned again with the jig ignored and
   walked (`PrxPlanFailed`, from `PrxRouteHook`, a swap of `SimRoutingComponent.DoRoute` for routes that come in
   with a failed plan; up to 3 lying pets per plan; a cancelled Sim or a plan that fails without the pet too gets
   the game's answer). The pet itself is then passed without a second grace. `blockeduse` (#649) swaps the same
   method: whichever lands second calls `PrxPlanFailed` from the other's failed-plan step instead of a second swap. The
   jigs are listed when `LayDownHere.Run` returns and leave the list when the pet is up. In the obstacle event and
   the two waits every floor jig counts as its pet (rule 1 and its timing), and the route ignores it once the walk
   through begins.
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
- **One-tile hallways** as lie-down chokepoints: no cheap test for "hallway" in script. A pet lying across a
  hallway away from every door costs the walker the grace, not the route (rule 2, the failed plan).
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
- Before the grace every stand-and-wait cycle gathers who is in the way (the cycles of 2.5 sim-minutes per wait).
- The stand at a failed plan is a plain wait in `DoRoute` (half sim-minute steps, ended by any exit reason); the
  Sim plays no animation in it. A replan that fails on a lying pet in the middle of a walk is not covered here
  (the hook acts on routes that come in with a failed plan).
- §8.8: "near a portal" is read where the pet lay down; a door built next to a lying pet counts from its next nap.

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

### 11.6 Run in the game (2026-11-22, Deck, Sunset Valley new game) [verified]

Repro `petrelax-force` (run `pr-r1`, and by hand in session `fr-m1`): a house built with the real tools, a corridor
one tile wide and two long between two closed cells, a household dog adopted out of EA's pool (`pet_adopt {active}`)
and ordered to lie in the corridor with the player's own order (`Lay Down Here` on the floor, the dog selected).

| | off | on (`petrelax petrelaxhh`) |
|---|---|---|
| Go Here past the dog | `Plan ... FailedBecauseNoPathToDestination, blocker Object ... PetSurfaceFloorJig`; the game's Go Here then walks to 1.0 m before the dog and stops | `Plan ... Succeeded, path 3.6 m`, `petrelax: ... walked through at the obstacle event: <dog> (none)`, arrived 0.0 m from the goal after 5.4 s |
| Back again | a Sim behind the dog cannot leave (same failed plan, by hand) | `path 2.4 m`, walked through, arrived after 3.7 s |
| Counters | would (4437) up | 4430 = 2, 4434 = 4, errors 0 |

- Rule 2 (the jig on the route's ignore list) is what lets the plan through; the jig stays locked with its own key
  (`RoutingFootprintLocking`, read back by `pet_jigs`), and the plan succeeds all the same. §11.4's first limit is
  settled. Rule 1 then passes the dog itself at the obstacle event.
- After the walk the game queued its own `Move Away` for the Sim standing next to the dog (`RouteAway`,
  `destination accepted`), which is the game's push and no part of the rule.
- Not a case: a doorway without depth (a gap in a wall line). The game's `FindGoodLocationNearby` puts the jig
  beside it and the Sim walks through in both arms.
- The forced repro `petrelax-force` stays on the contributor's combo branch: it needs the agent steps `order_at`,
  `go_at`, `pet_adopt {active}` and `pet_jigs`, which are not upstream yet. `petrelax-check` is included.
- This run was before the timing rule of §8.8. With it the dog in this corridor is passed at once only when it
  lies within 1.5 m of a portal lane; otherwise the Sim stands the grace at the failed plan and then walks
  through. NOT RUN with §8.8. [inferred: `Route.Replan` on a route whose first plan failed plans it again from
  where the Sim stands, as it does inside the stand-and-wait.]
- Open: rule 3 (no lie-down on a lane end) and a walking pet behind Sims did not run; a dog standing idle is pushed by
  the game itself.
