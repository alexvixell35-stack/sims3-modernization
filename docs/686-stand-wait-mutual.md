# 686: household Sims standing at a stair top or doorway: EA's obstacle stand-and-wait never pushes or yields once its replan fails

2026-11-21, branch `fix/stand-wait-1121`. Storm logs s141-s291 (Isla Paradiso, Collins household, 2-minute route storms) +
decompiled C#. Built, default off, **NOT RUN**. Switches `standwaitfix`, `standwaitfixhh`, `standwaitlog`; counters
4300-4319; `StandWait.cs`, `StandWaitCore.cs`; repro `standwait-check`.
§7 (waits at a privacy-locked room, `standwaitlocked`) and the other sections stay on the contributor's combo branch for now; §8 and §14 are
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

## 14. Sims share rabbit hole doors and serving platters; line waits logged (`rhshare`, `servingshare`, `linewaitlog`; 2026-11-22, `feat/objshare-1122`, built, default off, run §14.5)

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
  - `StateMachineClient.SetActor` (`SmcSetActor.cs`; on the combo branch actdelay's swap; one integer test while no run is open): the first call for a
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

### 14.5 Run in the game (2026-11-22, Deck, Sunset Valley new game) [verified]

Four household young adults, nobody autonomous; by hand in session `sh-m1`, then the entries below (exit 0 each). The
forced repros `servingshare-force` and `rhshare-force` stay on the contributor's combo branch: their agent steps
(`add_platter` and others) are not upstream yet. In these runs the `SetActor` hook was actdelay's; here it is
`SmcSetActor.cs` with the same body.

**Repro `servingshare-force`** (run `sv-r1`): a finished group meal on the ground of the front yard (agent step
`add_platter`: `Recipe.CreateFinishedFood`, Group), the Sims 6 m away on four sides, Grab a Serving for three at
once and the fourth 6 s later.

| | off | on (`servingshare servingsharehh`) |
|---|---|---|
| 8 servings, 4 Sims | `linewait: Di Platter household, ServingContainerGroup_GrabAServing target BowlL: BowlLarge line, waited 3.1 sim-min, line length 1, outcome got turn`, Cy 5.9 and Bo 5.0 sim-min (line length 2); 4 plates | no line wait; `servingshare: Di Platter household takes from BowlLarge with 1 other Sim(s), 4 left`, `Cy ... with 2 other Sim(s)`; 4 plates, servings 0 |
| Counters | line waits (4470) 3 more, runs at a busy platter (4485) 1, would (4488) 1 | took with another Sim (4487) 2, line waits unchanged, errors 0 |
| 2 servings, 3 Sims | | exactly 2 plates, servings 0, lines skipped (4486) 1 |
| 1 serving, 3 Sims | | exactly 1 plate, `servingshare: Bea Platter selected does not take from BowlLarge: 1 left, 1 being taken`, last serving refused (4491) 1 (2 in `sh-m1`), servings 0 |

- Sims that come from one side, or through one door, reach the platter one after the other and take 3 s apart
  with the switch on (6 s off): they never meet at it and 4487 stays 0. From several sides the takes overlap.
- 4485 and 4488 count a run only when the platter was in use or had a line when the run began (before the walk):
  orders given at one time leave them at 0. Counters only, not changed.
- The third Sim at a platter with 2 servings found it empty the game's own way; 4491 needs a Sim that asks while
  the last serving is being taken (the 1-serving arm).

**Repro `rhshare-force`** (run `rh-r1`): the four Sims put 20 m before City Hall's doors and ordered Tour City Hall.

| | off | on (`rhshare rhsharehh`) |
|---|---|---|
| Lines | `linewait: Di Rabbit household, VisitRabbitHole target CityHall: CityHall rhdoor, waited 0.9 sim-min, line length 2, outcome got turn`, Cy 2.1, Bea 5.1 sim-min | `rhshare: Bo Rabbit household walks to enter slot 1212927037 of CityHall held by another Sim, 1 waiting`, `linewait: Bo Rabbit household, ...: CityHall rhdoor, waited 0.0 sim-min, line length 1, outcome shared`; the same for Bea and Cy |
| Counters | rhdoor waits (4474) 4, 9.4 sim-min (4475), would (4480) 4 | walked to a held slot (4479) 3, sim-min unchanged, errors 0 |
| After 30 s | all four in the tour | all four in the tour, no reserved enter slot left |

- NPCs shared too in the on arm (`rhshare: Monika Morris npc walks to enter slot ... of Stadium`).
- Not run: the cancel exit (4483), dead reservations (4484), horses and riders (4482).
