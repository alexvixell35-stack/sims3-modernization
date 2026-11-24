# 686: household Sims standing at a stair top or doorway: EA's obstacle stand-and-wait never pushes or yields once its replan fails

2026-11-21, branch `fix/stand-wait-1121`. Storm logs s141-s291 (Isla Paradiso, Collins household, 2-minute route storms) +
decompiled C#. Built, default off, **NOT RUN**. Switches `standwaitfix`, `standwaitfixhh`, `standwaitlog`; counters
4300-4319; `StandWait.cs`, `StandWaitCore.cs`; repro `standwait-check`.
§7 (waits at a privacy-locked room, `standwaitlocked`), §10 and §12-§13 stay on the contributor's combo branch for now; §8, §9 and §11 are
§7 (waits at a privacy-locked room, `standwaitlocked`) and the other sections stay on the contributor's combo branch for now; §8 and §14 are
§7 (waits at a privacy-locked room, `standwaitlocked`) and the other sections stay on the contributor's combo branch for now; §8, §14 and §15 are
§7 (waits at a privacy-locked room, `standwaitlocked`) and the other sections stay on the contributor's combo branch for now; §8, §14 and §19 are
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
## 12. A chore whose spot is closed is done from within reach (`reachuse`, `reachusehh`; 2026-11-22, `feat/reachuse-1122`, built, default off, rule 1 run for a pile §12.7)

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
  rule, the covered tile, scope). `build-fixes.ts` builds, `refs-resolve-check` 0 unresolved; selftest
  `blockeduse` passes; `swap-stack-audit` has the new row
  (`Fire.PlanRouteToExtinguish`, stub `PlanOrig`).
- Live: `reachuse-check` once (off `BROKEN`, on `FIXED`, `members found=True`; `fix_applied`
  `Fire.PlanRouteToExtinguish: ok`). A storm save, off / on interleaved with `reachuselog routefaillog`: off arm
  4446 and 4447 up; on arm `reachuse reachusehh` (with `mopunreachable` for rule 4): 4450 > 0, `routefail:` lines
  of MopPuddle with `Plan ... FailedBecauseNo(Path|Destination)` down, 4459 = 0, no `reachuse: error at` line.
  Before the arms: save with a Sim mid-route and load it (the `DoRoute` hook's entry changed).
  `ReachUseCheck lot` lists the puddles, burnt tiles, fires, piles and plants the game's plan cannot reach and
  whether a place in reach exists. By eye: a puddle in a nook behind a chair (mopped from the open side), a
  kitchen fire behind a counter with the firefighter called.

### 12.7 Run in the game (2026-11-22, Deck, Sunset Valley new game) [verified]

**When the game fails at all.** Every ring above starts at about 1 m and has no outer limit, so a pile or puddle
ringed by furniture in a big room is done from any free place of that room, 2 m away or more. The game's plan only
fails where the target's room has no place 1 m away that can be stood on. By hand (session `fr-m1`): a nook ringed
by fridges (cleaned from exactly 1.0 m), a closet 1 x 2 with the pile in its middle (cleaned from the far wall,
exactly 1.0 m), a closet 1 x 1 (cannot be entered, the door is the blocker; the game fails and the rule keeps: "no
place within 2.0 m can be walked to", the places outside are behind the wall).

**Repro `reachuse-force`** (run `ru-r2`; it stays on the contributor's combo branch: its agent step `add_trash` is not upstream yet): a closet one tile wide and two deep, a trash pile 1.3 m behind its door.

| | off | on (`reachuse reachusehh`) |
|---|---|---|
| Clean Up, Sim at the mailbox | `routefail: ... DisposeTrash target TrashPile, Plan ... FailedBecauseNoPathToDestination blocker Object ... DoorSingleCountry`; the pile stays | the same failed plan, then `reachuse: selected Bea Reach, DisposeTrash -> TrashPile at ... (small, FailedBecauseNoPathToDestination): from 0.0 m at ..., walked up`; the pile is carried to the outdoor can |
| Counters | tries (4446) 1, would (4447) 1 | small (4451) 1, errors 0 |
| `ReachUseCheck lot` after | targets 1, closed 1, in reach 1 | targets 0 |

- The Sim walks onto the pile ("from 0.0 m"): the rule's own plan has no least distance for the small kind
  (`MinDist` 0) and prefers the place nearest the target. The pick-up plays there. Not changed.
- A puddle on the closet's far tile is mopped by the game itself from the closet's middle: no case.
- `ReachUseCheck lot` made one plan in any room and read "wall between" for a pile in a closet; it now plans as the
  rule does, the target's room first, with the game's 1 m ring for piles (fixed, check only).
- Open [inferred, not run]: for a puddle the rule may end within 0.6 m of the tile's centre, where the game's
  `PuddleManager.CanMopPuddle(ignoreSims: false)` counts the mopping Sim itself as blocker and plays the route-fail.
  No layout in these runs produced a failed mop plan with a place in reach, so it is not changed.
- Not run: fires, plants, slot chores (rule 3), replans that fail during the walk (most of §12.2's failures).
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

## 15. Partners shower together; a Sim with no usable toilet takes a shower (`showertogether`, `showerpee`; 2026-11-22, `feat/showershare-1122`, built, default off, run §15.5)

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
  bladder delta when the shower becomes an actor of its state machine (`StateMachineClient.SetActor`, `SmcSetActor.cs`
  (actdelay's hook on the combo branch); 5 sim-minutes to full), and the bladder is set full when the Sim steps out. Hygiene is the shower's own.
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

### 15.5 Run in the game (2026-11-22, Deck, Sunset Valley new game) [verified]

Repro `showershare-force` (run `sh-r7`, exit 0; by hand in sessions `sh-m1`, `sh-m2`): four young adults, Bea and
Bo partners, a room without roof, one shower, later an all-in-one bathroom.

| | off | on |
|---|---|---|
| Take Shower for Bea, then Bo | `in use by 1, in line 1`, Bo waits at the door (`linewait: Bo Share household, TakeShower target ShowerBasic: ShowerBasic line, waited 11.9 sim-min, line length 1, outcome cancelled`), would (4495) 1 | `showertogether: Bo Share household, TakeShower target ShowerBasic: joins Bea Share selected in ShowerBasic`, `in use by 2`, joined (4494) 1, both end clean and stand outside, exits held (4500) 1 when both end together (`sh-m1`, `sh-r5`; 0 in `sh-r7`), errors 0 |
| Bladder -97, no toilet, no orders | Pee Self, Embarrassed, Smelly; would (4506) 1 | `showerpee: Di Share household, none: no usable toilet, takes a shower in ShowerBasic ... at the last warning`, `... bladder fills in ShowerBasic`; 4503 1, 4502 1, bladder 98, hygiene 99, no accident |
| All-in-one bathroom | `in use by 1, in line 1`, would 2 | `showertogether: Bo Share household, UseAllInOneBathroom target AllInOneBathroom: joins Bea Share selected`, `in use by 2`, 4509 1, both come out |

- How it looks: both Sims stand on the shower's one place inside each other, under one censor mosaic; from outside
  it reads as one occupied shower. They step out one after the other and dry off side by side.
- Answers to §15.3: the walk to `RoutingSlot_0` succeeds while the partner showers; the two state machines did not
  disturb each other in these runs; the all-in-one bathroom's footprint opens for the partner.
- **Fixed here: a partner that arrives while the other leaves.** Bea nearly clean, Bo 8 s away: Bo joins (no line),
  Bea's exit footprint and then Bea herself cover the slot, Bo's walk fails and his Take Shower ended, where the
  game's line would have held him (`sh-m1`, twice). The walk hook now waits until the partner no longer uses the
  shower (cap 10 sim-min) plus 2 sim-min, takes the route-fail exit reason off and walks again, up to three times:
  `showertogether: Bo Share household: the walk failed while Bea Share left ShowerBasic, waited 6.3 sim-min, walks
  again` (twice in `sh-r7`), then Bo showers. He still plays the route-fail reaction once per failed walk.
- The shower lasts about a minute at speed 1 from hygiene -95, so a second order 20 s late is this late case.
- Not run: 4504 (the failure itself replaced: the last warning always came first), a shower tub, a teen or friend
  as control, a visitor, 4508.
- The forced repro `showershare-force` stays on the contributor's combo branch: its agent steps and the
  harness's `noKeepAlive` flag are not upstream yet.
- Harness: profile-world's `keep_alive` fills every household motive below -50 each 30 s and hid the bladder
  failure in three runs; a repro entry with `"noKeepAlive": true` runs without it.

## 16. No route-fail balloon for looking at things (`viewlenient`; 2026-11-22, `feat/viewlenient-1122`, built, default off, NOT RUN)

A player's remark: "Routing errors contemplating surroundings or viewing something makes no sense." A Sim that
only looks at something needs no exact spot; a failed plan to the chosen spot should not end in the route-fail
balloon when a place with a view is at hand.

### 16.1 How the game routes a spectator [verified, decompiled `Sims3.Gameplay` / `Sims3GameplayObjects`]

| kind | class | route | on a failed plan |
|---|---|---|---|
| contemplate | `Terrain.Contemplate` (Genius trait, autonomous on residential and venue lots) | `PlanToPointRadialRange` 0.3-1 m round the clicked point, valid rooms = the room of one randomly picked object near the Sim | `DoRoute` false: the next object is tried (a balloon each time), with none left "bored" and the interaction ends |
| art | `ViewObjects` (`IViewable`: paintings, sculptures, photos, altars) | `CreateRouteToObject`: a ring of the viewable's min..max distance, one level; in range with a view only a turn | ends |
| performer | `MusicalInstrument.WatchBase<T>` (`Guitar+Watch`, `Drums+Watch`, ...) | `DoRouteForWatchOrDance`: a dance floor near the instrument, else a ring 1 m .. 0.9 x the sound radius in the instrument's room (front half, then all round, then platforms allowed) | ends |
| tv | `TV.WatchTV`, standing | `TV.RouteToCone`: 2-6 m, a 3/8 pi cone in front of the TV, in its room | ends |
| stars | `Sim.WatchTheStars` (two Sims) | `SocialJigTwoPerson.RouteToJigA/B` to a jig placed by `FindGoodDeltaLevelPosition` | both Sims' interactions end |

All of them hand the plan to `SimRoutingComponent.DoRoute`. A plan that failed before the walk gets
`DoRouteFailureBehavior` (the balloon, for a locked door the door reaction) when `Route.DoRouteFail` is set, the
exit reason `RouteFailed` and false. A walk that ends on a failed replan answers false with no balloon of its own
(the balloon there is only for a Sim standing on the goal or a turn jig).

Route failures in the storm logs (`ts3fixes: routefail:` lines of `ts3native-full.log`, all phases) [verified]:

| interaction | no destination | no path | portal locked | aborted | first plan / from a point / replan |
|---|---|---|---|---|---|
| Contemplate (target Terrain) | 296 | 4 | 0 | 0 | 297 / 3 / 0 |
| Watch (Guitar 61, KeyBoard 24, LaserHarp 18, Drums 5, GrandPiano 4, BassGuitar 1) | 1 | 76 | 36 | 0 | 6 / 59 / 48 |
| WatchTV | 6 | 55 | 6 | 0 | 43 / 18 / 6 |
| WatchTheStars (target Sim) | 7 | 25 | 0 | 9 | 35 / 5 / 1 |
| ViewObjects (paintings 4, altar 2) | 0 | 6 | 0 | 0 | 0 / 6 / 0 |

Contemplate's 296 "no destination" plans are the room filter: the picked object's room is not the room of the
point, so the ring has no place at all. `ViewText` (6, a phone) has no route of its own and is left out.
`ListenToGhostStory` (46, a group round a storyteller) and `LookOutWindow` (12, a window's slot) are not in.

### 16.2 What is built

`ViewLenient.cs` / `ViewLenientCore.cs`, no swap of its own: the case is a third step in blockeduse's `DoRoute`
hook (`BuRouteHook.RouteBlocked` -> `RouteReach` -> `RouteView`), reached for a failed plan (the first plan, or a
replan that ended the walk with no exit reason) that blockeduse and reachuse left alone.

- Which: the Sim's running interaction is one of the five kinds (class names, `ViewLenientCore.Kind`), the plan
  failed with no destination, no path, obstructed goal, a Sim on the goal, no free slot or portal locked
  (`PlanKind`), the route would show the balloon (`DoRouteFail`), the Sim is a human on foot.
- contemplate: a standing Sim stays where it is and the route answers "arrived" (the game then turns it to the
  object it picked). A sitting, lying or swimming Sim walks a normal plan to the nearest place within 6 m of the
  point.
- art, performer, tv: the failed route must go to the target (goal within range + 1.5 m). The Sim stands within
  the game's range (the viewable's `MaxDistanceFromObjectToView`; 0.9 x the instrument's `DanceRadius`; 6 m for a
  TV), on the target's level (1.5 m), no light-blocking wall between it and the target (for a painting or a TV a
  point 0.3 m in front of it), for a TV on its front side: it turns to the target, "arrived". Otherwise a normal
  plan to the nearest such place (reachuse's `RuNearPlan`: the target's room first, then any room) is walked and
  tested again at its end.
- stars, or no such place (also a walk that was stopped): the game's own answer (exit reason, false) with the
  route's `DoRouteFail` cleared for that call, so no balloon. A target with no place is not searched again by
  that Sim for 30 sim-minutes (quiet end at once).
- Scope: `viewlenient` = Sims the player does not control, `viewlenienthh` = household and selected Sims, each
  stands alone. Off = compare mode (counted as "would"). `viewlenientlog` = one `viewlenient:` line per act.
- `ReachUse.cs` lost its copy of the search: `RuPlace` and `ViewLenient.cs` share `RuNearPlan` and `RuWall`.

Counters 4510-4525 (`kCounterSlots` 4526): tries 4510, would 4511, exempt 4512, handled by kind: contemplate
4513, art 4514, performer 4515, tv 4516, stars 4517; by outcome: stayed in place 4518, walked to the nearest
place 4519, quiet ends 4520; kept as the game has it 4521, walk stopped or ended with no view 4522, errors 4523
(`viewlenient: error at <site>`), quiet ends while cooling down 4524, tries after a walk 4525.

### 16.3 Limits [inferred unless marked]

- Not in: socials, Go Here, every interaction not named in §16.1, routes to routing slots (blockeduse), aborted
  plans (WatchTheStars has 9), pets, riders, Sims in vehicles.
- Watch the Stars is a pose for two on a jig: only the balloon goes, the interaction ends as before.
- Contemplate in place may look at an object in another room (the game picks it before the route).
- A Sim that stays in place turns at once (`SetForward`, as reachuse does), not with a turn animation.
- A failed route onto a dance floor near an instrument (`RouteOntoDanceFloorAndFaceObject`) is answered the same
  way when it would show the balloon: the Sim watches from the nearest place and is entered in that dance floor's
  use list.
- A TV watched from a place the game's cone would not give (up to 6.5 m, anywhere on the front side): `WatchTV`
  runs its own facing test after the route.
- `RouteReach`'s body changed (it calls `RouteView`): a Sim saved in a reachuse walk by an older build resets once.
- The household switch changes what a player-ordered View / Watch does when its spot is closed (it is done from
  the nearest place); Go Here is never touched.

### 16.4 Checks (2026-11-22, no game) and run plan

- `node tools/selftest/viewlenient/run.ts`: 56 checks ok. reachuse 53, blockeduse 45, showershare 41 ok.
  `swap-stack-audit`: no change. Build ok.
- Repro `viewlenient-check` (any save): `ViewLenientCheck run` off -> `verdict: BROKEN`, on -> `verdict: FIXED`,
  `controls ok=True`; `result` = counters and the last line; `lot` = the TVs, viewables and instruments of the
  selected Sim's lot its plan cannot reach as the game does, and what the rule finds.
- Storms: with the switches off 4510 and 4511 count the cases (Contemplate is common: expect most of 4510 there);
  with `viewlenient` on 4513 and 4518 rise together (contemplate in place), `routefail:` lines for Contemplate /
  Watch / WatchTV stay (they are written at the plan) but no balloon follows; 4523 must stay 0.

### 16.5 Storms (2026-11-22, Deck) and what is still open

Ten route storms s520-s529 (5 on, 5 off, the switches of §16-§18 together): no contemplate route failure happened
in either arm, so the storms did not exercise this rule. Errors 0, crashes 0. **Not run in the game yet**: a forced
repro (a sculpture or a view spot whose view slot cannot be reached) is pending.

## 17. Talk socials from a distance (`socialreach`; 2026-11-22, `feat/socialreach-1122`, built, default off, NOT RUN)

A player's wish, after Sims 4: Sims should be able to chat at a distance (across a counter, a fence, a crowded
room) instead of ending the social with the route-fail balloon. No clipping: every walk is a normal plan.

### 17.1 How the game places a social [verified, decompiled `Sims3.Gameplay.Socializing.SocialInteraction`]

`SocialInteractionA` runs on the starter, `SocialInteractionB` on the partner; both are `SocialInteraction`.

| step | method (task) | route | on a failure |
|---|---|---|---|
| 1 approach | `BeginSocialInteraction` -> `SocialRouteA` (starter) | other level, or farther than 5 m (`kSocialRouteMinDist`) from a partner that is not walking: two ring destinations round the partner, the social's distance .. 3 m (`kSocialRouteRadiusMax`), valid room = the partner's. Else a turn (`PlanRouteTurnToFace`). `DoRoute` | result code `PathPlanToToSocialTargetFailed`, the starter's `Run` leaves the conversation, adds `RouteFailed`, and with a group walks away 3 m |
| 2 placement | `ReceiveSocialInteraction` -> `SetupTwoPersonSocial` (partner) | `RouteSimsToSocial`: the starter's walk to a ring at exactly the social's distance in the partner's room, facing it, planned from the end of the starter's current walk; a turn for the partner. Roles swapped when the starter is outside and not at home and the partner at home. Partner too close to a wall or object (`Route.IsOkToIdleAtCurrentLocation`): a step aside (`Wander` 0.35-0.5 m), then the ring again. Still too close, or in a doorway: `SocialJigTwoPerson` placed at the partner, at the starter, then near the mailbox or in an adjacent room (within 6 m), both walk to its slots | no plan: false. `SocialInteractionB.Run` adds `RouteFailed`, copies it to the starter and waits for the starter's social to end |
| 3 walk and face | both `DoRoute` the routes of step 2; then, when they do not stand exactly face to face at the social's distance (`SimsAreFacing`), the partner and the starter each `RouteTurnToFace` the other | the partner's failed route or turn: step 2's failure. The starter's: its own failure as in step 1 |

Conversations of three or more use `SetupGroupSocial` (positions from `SocialGroupInteractionSituation`); a
baby uses `SetupTwoPersonSocialWithBaby`. Neither is touched.

How the game marks socials whose Sims touch [verified]:

- `ActionData.SocialJig` (the `jig` attribute of the social's data). `SocialJigOnePerson` (the default) = each
  Sim plays its own animation. Any other value (`SocialJigTwoPerson`, `_1Unit`, `_1_25Unit`, `_1_68Unit`,
  `_0_35Unit`, `_Dog_GrowlJig`, `UnderwaterSocial_Jig`) makes `SocialInteractionA.Run` set `mbPairedSocial`,
  and a paired social is refused unless the pair stands aligned at the social's distance
  (`VerifyPositionAndAlignment`).
- `ActionData.ForceCreateJig`, a horse or an adult full-size dog on either side: the social creates its own jig
  (`GetSocialDistanceAndSetupJig`), and both walk to its slots.
- Socials with steps of their own are subclasses or other classes: `PillowFight`, `PlayTagInteraction`,
  `PlayCatchSocialInteraction`, `DanceTogetherA`, `Pool.PoolSocialInteractionB`, `WatchTheStars`, ...

### 17.2 The failures in the storm logs [verified, `ts3fixes: routefail:` lines, all phases]

3,331 lines name `SocialInteractionA` (1,139) or `SocialInteractionB` (2,192), on 1,534 routes (a route that
replans writes one line per failed replan); the biggest group of all 19,338 route-fail lines. The lines do not
name the social (the action key), so the share of talk socials is not known [not measured].

| | A (starter) | B (partner) |
|---|---|---|
| no path | 547 | 1,831 |
| portal locked | 405 | 70 |
| no destination | 187 | 206 |
| planner aborted | 0 | 85 |
| first plan / from a point / replan / replan from a point | 148 / 338 / 619 / 34 | 523 / 283 / 1,363 / 23 |
| goal within 1 m of the Sim / 1-3 m / 3-6 m / farther / another level | 305 / 529 / 89 / 64 / 152 | 1,610 / 353 / 72 / 6 / 151 |
| blocker: a Sim / the placement jig / another object / none named | 206 / 20 / 111 / 802 | 757 / 29 / 667 / 739 |

2,797 lines have their goal within 3 m on the Sim's level. Named blockers besides Sims: beds 271, crib 117,
bathtub 107, sink 65, dresser 56, the two-person jig 45 (small rooms; toddlers are frequent). The partner's
1,610 lines within 1 m are the short moves of step 2 (the step aside, the jig slot, the turn) failing next to a
Sim or a piece of furniture.

### 17.3 What is built

`SocialReach.cs` / `SocialReachCore.cs`. Covered: the plain classes `SocialInteractionA` / `SocialInteractionB`
(no subclass) whose data says `SocialJigOnePerson`, that made no jig of their own, between two humans, no baby,
nobody riding, ridden, swimming or diving (`SocialReachCore.Cover`). Failed plans that count: no path, no
destination, all places obstructed, a Sim on the place; a locked door and an aborted plan keep the game's
answer and are not counted (`PlanKind`).

a. In `DoRoute` (blockeduse's hook, fourth step: `RouteBlocked` -> `RouteReach` -> `RouteView` ->
   `RouteSocial`), a failed plan (the first, or a replan that ended the walk with no exit reason) of a Sim whose
   running interaction is a covered social:
   - within 3 m of the partner (flat), on its level (1.5 m), no light-blocking wall of either Sim's lot between:
     the Sim turns to the partner and the route answers "arrived";
   - else, the starter only, standing, not for a failed turn: a normal plan to the nearest place 0.7-2 m from the
     partner with no wall between (reachuse's `RuNearPlan`: the partner's room first, then any room) is walked;
     it answers "arrived" when the walk ends within 3 m with no wall;
   - the partner never walks; its walk to the game's placement jig is left to the game (it has more tries).
b. `SetupTwoPersonSocial` (swap `SrSetupHook`, with a stub) answered false with no exit reason on either Sim
   and neither social cancelled: with the starter (or the end of its current walk) within 3 m, same level, no
   wall, the pair is placed where it stands: both routes none, and step 3's facing turns both with the game's
   own turn. Else, with a standing starter that is not walking, the starter's route becomes the plan of (a)'s
   second case. The placement jig of the game's failed tries is destroyed (the game's `Cleanup` would do it at
   the social's end).

After either, the game's flow goes on unchanged: sync levels, the conversation, the animation, the outcome.
Scope: `socialreach` = pairs with no Sim the player controls, `socialreachhh` = pairs with a household or
selected Sim; each stands alone. Off = compare mode (counted as "would"). `socialreachlog` = one `socialreach:`
line per act and per kept case. A walk search that found no place is not repeated for that pair for 30
sim-minutes.

Counters 4526-4537 (`kCounterSlots` 4550): tries 4526, would 4527, exempt 4528, paired or jig socials left alone
4529; handled by kind: the starter's route 4530, the partner's route 4531, the placement 4532; by outcome: from
where they stand 4533, walked to the nearest place 4534; kept as the game has it 4535 (reasons in
`SocialReachCheck result`: too far, other level, wall between, sitting or lying, starter still walking, a walk
to the placement jig, cooling down), walk stopped or ended out of reach 4536, errors 4537
(`socialreach: error at <site>`, sites: begin, arrive, placement, lot).

### 17.4 Limits [inferred unless marked]

- Excluded, the game's behaviour stays: every paired or jig social (hug, kiss, slap, fight, handshake, woohoo,
  carry, anything the data gives a two-person jig), every social with a pet, a horse or a baby, every subclass
  (pillow fight, tag, catch, dance, pool socials), conversations placed as a group, another level, a wall
  between, a locked door.
- The talk animations are played where the Sims stand, up to 3 m apart; an animation that hands something over
  or points at the partner is not known from the data and is not excluded when its jig is "one person"
  [not checked per social].
- A Sim that stays in place after a failed route turns at once (`SetForward`); in the placement case the game's
  own turn is played.
- Across a counter or fence the test is the game's light-blocking wall test: a fence is no wall; a half wall or
  window wall is [not checked].
- The partner's failed step aside (`Wander`, no balloon) is answered "arrived" too when the starter is within
  3 m; the game then goes on to its jig tries as after a successful step.
- One social can count several tries (its route, then its placement).
- A turn in place that fails because another Sim overlaps (757 partner lines name a Sim) is the common partner
  case: the social then runs with the Sims as they stand, overlap included.
- `RouteView`'s body changed (it calls `RouteSocial`), `Keep` now passes unstuck (§18): a Sim saved inside these
  helper frames by an older build resets once. `SetupTwoPersonSocial` yields; its swap has a stub
  (`docs/data/swap-stack-audit.csv`).

### 17.5 Checks (2026-11-22, no game) and run plan

- `node tools/selftest/socialreach/run.ts`: 54 checks ok. Build ok, `swap-stack-audit` ok (one new row).
- Repro `socialreach-check` (any save): `SocialReachCheck run` off -> `verdict: BROKEN`, on -> `verdict: FIXED`,
  `controls ok=True`, `members found=True`; `result` = counters, kept reasons, the last line; `lot` = the Sims on
  the selected Sim's lot the game's placement ring cannot reach and what the rule finds.
- Forced repro, not built as an entry: a lot with a counter row (or a fence) across a room, Sim A on one side,
  Sim B on the other, 2 m apart, no way round within the room; `socialreachhh` and `socialreachlog` on; A:
  Chat with B. Off: balloon, `routefail:` line for SocialInteractionA or B, 4526 and 4527 rise. On: the two
  talk across the counter, 4532 + 4533 (or 4530 + 4533) rise, one `socialreach: ... placed where they stand`
  line, no balloon. Control on the same lot: Hug across the counter keeps the balloon, 4529 rises.
- Storms: off, 4526 / 4527 give the share of talk socials among the 1,534 failed routes; on, 4533 should carry
  most of it (2,797 of 3,331 lines are within 3 m), 4537 must stay 0, and the `SocialInteractionA/B` route-fail
  lines stay (they are written at the plan) while the balloons go.

## 18. Boxed-in Sims walk out through objects (`unstuck`; 2026-11-22, `feat/socialreach-1122`, built, default off, NOT RUN)

A player's wish: a Sim or pet that is trapped (furniture put round it, a spot it was reset or snapped to) should
walk out instead of failing every route. This is the one rule here that clips on purpose, so it waits: at least
2.5 sim-minutes (2-3 s at normal speed) and three failed plans from one spot, and it never passes a wall.

### 18.1 What the game has [verified, decompiled]

- `LotManager.RunStuckCheckHelper`: armed for every actor at the nightly lot update, it looks 20-60 sim-minutes
  later at a Sim that stands where it stood, is not on the active household's lot and is in no rabbit hole. On a
  lot: up to `Sim.kNumberOfPlanAttemptsForStuckSim` (10) plans to random lot points; on the world lot: four
  plans of 2 m, selectable Sims only. All failed: looked at again 20-60 minutes later, and the third time the
  Sim is reset (`SetObjectToReset`). `FewerPlans.cs` already wraps it (`StuckCheckRun`, counters 297-301).
- No `Sim.IsStuck`, no per-Sim counter of failed routes: `SimRoutingComponent.DoRoute` answers each failed plan
  on its own (route-fail behaviour, exit reason, false).
- NRaas Overwatch's nightly Stuck Check plans one route per standing Sim and resets as well
  (`OverwatchStuck.cs`).

So a boxed-in Sim on the played lot is never helped, and elsewhere it is reset after up to three sim-hours.

### 18.2 What is built

`Unstuck.cs` / `UnstuckCore.cs`, no swap of its own: the last step of blockeduse's `DoRoute` hook. `Keep`, the
hook's one exit for "the game's answer", now passes `RouteStuck` first, so every failed plan that blockeduse,
reachuse, viewlenient and socialreach leave to the game is seen.

- Signal: failed plans of the kinds no path, no destination, all places obstructed are counted per Sim and spot
  (0.6 m). Three from one spot, the first at least 2.5 sim-minutes ago, with no successful plan of 3 m or more
  from the Sim's spot between them (`BuAfterPlan` clears the count, only while Sims are counted). A pause of 120
  sim-minutes or another spot starts a new count. The game's stuck check stands for count and grace: a Sim it
  found no way for (`StuckCheckRun`'s outcome, any round) is looked at on its next failed plan.
- Never: a Sim that sits or lies in something (posture container), swims, dives, rides or is ridden, is carried
  or moved by a routing parent, a baby, a Sim with an exit reason. A Sim that was looked at is not looked at
  again for 30 sim-minutes.
- Probe (plans only): a normal plan to a ring 3-10 m round the Sim succeeds = it can walk, not boxed in (kept).
  Else a plan with `Route.RouteOption.PassThroughObjects` (walls still block) to the nearest ring, inner radius
  0.75, 1.5, then 3 m, at most 12 m long, whose end is routable (`IsPointRoutable`) and has a normal plan on to
  a ring 3-10 m away (the end is no second box). None = walled in (kept; the game's reset remains the answer).
- Act: the plan through the objects is walked as a real route. Then the failed route is planned again from the
  new spot and walked when it now succeeds; otherwise the game's answer for the failed plan.
- Scope: `unstuck` = Sims and pets the player does not control, `unstuckhh` = household and selected ones; each
  stands alone. Off = compare mode: the signal is counted ("would look"), no probe is made. `unstucklog` = one
  `unstuck:` line per act and per kept probe.

Counters 4538-4549: failed plans left to the game 4538, stuck signals 4539, Sims the game's stuck check found
no way for 4540, would look (off) 4541, exempt 4542, kept: sitting, lying, swimming, riding, carried or
cancelled 4543, kept: can walk 3 m away 4544, kept: walled in 4545, walked out through objects 4546, walk out
stopped 4547, the failed route planned again after the walk out: ok 4548, errors 4549
(`unstuck: error at <site>`, sites: begin, after the walk, plan seen, game signal, lot).

### 18.3 Limits [inferred unless marked]

- "Boxed in" means no normal plan to any place 3-10 m away. A Sim shut in a doorless room larger than that, or
  on an island, is not helped (walled in or "can walk"); a Sim in a pool with no ladder is excluded.
- A room whose only door is blocked by an object counts as boxed in: the Sim walks out through that object.
- Whether a pass-through-objects plan may end inside a footprint is not known; the end is tested with
  `IsPointRoutable` and a normal plan on from it [not run].
- The signal needs the Sim to try to walk: a Sim that stands idle and never plans is not seen (the game's check
  and its reset remain).
- With the switches off the signal count (4539, 4541) includes Sims the probe would call free: three failed
  plans from one spot over 2.5 minutes is common for a Sim whose targets are closed, not only for a boxed-in
  one. The probe count is only known with a switch on (4544 against 4546).
- Every failed plan now costs one table lookup; every plan one count test while Sims are counted (as blockeduse's
  way-out test).
- The count table holds 256 Sims; when full, counts older than 30 sim-minutes go.

### 18.4 Checks (2026-11-22, no game)

- `node tools/selftest/unstuck/run.ts`: 50 checks ok. reachuse 53, viewlenient 56, blockeduse 45 ok.
- Repro `unstuck-check` (any save): `UnstuckCheck run` off -> `verdict: BROKEN`, on -> `verdict: FIXED`,
  `controls ok=True`; `result` = counters and the last line; `lot` = the probe for the selected Sim and up to
  12 other Sims and pets on its lot.

### 18.5 Run plan

- Forced repro, not built as an entry: in buy mode put four pieces of furniture (counters or bookcases, no
  seats) round a standing household Sim in the middle of a room; `unstuckhh` and `unstucklog` on;
  `UnstuckCheck lot` must name it "boxed in, a way out through objects". Order Go Here three times across the
  room, 2.5 sim-minutes or more from the first to the third. Off: three balloons, 4539 and 4541 rise by one. On:
  at the third the Sim walks out through the furniture (4546, one `unstuck: ... walked out through objects`
  line) and then walks to the clicked point (4548). Controls: the same Sim seated on a chair inside the box is
  kept (4543); a Sim in a doorless walled room is kept (4545); an unreachable Go Here target for a free Sim is
  kept (4544).
- Storms: off, 4538 against 4539 says how often the signal fires; on (`unstuck`), 4544 is expected to carry
  nearly all of it and 4546 to be rare; 4549 must stay 0.

### 18.6 Storms for §17-§18 (2026-11-22, Deck)

Ten route storms s520-s529 (5 on, 5 off; §16-§18's switches together). `socialreach` acted once: `npc Liliana
Ichtaca, SocialInteractionB Share Interests -> Edward Dregg (FailedBecauseNoPathToDestination, a turn): talks from
where it stands`. `unstuck` walked a Sim out once (0.7 m; its route still had no plan afterwards). Errors 0, crashes
0; hitches per storm 4.8 on vs 5.8 off. **Not run in the game as a forced repro yet** (pending).
## 19. Crowded transport portals are skipped (`crowdport`; 2026-11-22, `feat/portalcrowd-1122`, built, default off, NOT RUN)

A player's wish: at elevators, rabbit holes, the subway and the like a Sim may be teleported when too many Sims
are going in. These count as portals for the timing rule (§8.7), so they may act almost at once: a crowd is
`kCrowdPortMin` Sims (3) waiting for or using the portal, this Sim included, or the Sim's own wait behind
another Sim has passed `kCrowdPortWait` sim-minutes (1). A single Sim with nobody in its way walks as in the
game. Where the game has a teleport of its own, that path is used.

### 19.1 Transport portals with a line [verified, decompiled `Sims3.Gameplay` / `Sims3GameplayObjects` unless marked]

| Portal | How Sims wait | Timeout | The game's own skip |
|---|---|---|---|
| Elevator (`ElevatorDoors`, `ElevatorInterior`) | `PortalComponent.AcquireLaneRouteAction.WaitInLine`: the doors' `SimQueue` (`kMaxSimsInElevatorLine` 12, stay where you are, ordered by distance), asked again every 0.25 sim-min while the lane (1 per door) is not locked | line 15 sim-min (`kElevatorSimQueueWaitBeforeTeleportSkipTime`), lane 30 (`kElevatorGiveupTime`) | `ElevatorDoorsPortalComponent.TryTeleportSkip` after the line timeout or with 2 riders in the car: doors open here, car not in use, same direction, then `OnPortalStart(bAlwaysTeleport)`: fade, set into the car, replan |
| Rabbit hole, going in (`RabbitHole.AnimateEnterRabbitHole`) | `WaitForEnterSlot`: one Sim holds an enter slot until its walk to it ends; the others sleep 10 ticks a round, blind to cancels | a reservation is broken after 15 sim-min per holder | none for a walking Sim; `doRoute: false` (stations) enters from where the Sim stands |
| Rabbit hole, coming out (`AnimateExitRabbitHole`) | NPCs sleep 30 ticks a round until `kNpcExitFreq` (15 sim-min) has passed since the last Sim left; then `GetExitSlot` (one Sim per exit slot, 15 sim-min) | one NPC per 15 sim-min, no upper bound | `CanSimSkipExitWait`: selectable Sims, school pupils; `rhexitunseen` adds unseen NPCs (docs/613 §2) |
| Subway (`Subway.SubwayPortalComponent`), hover train (`HoverTrainStation`) | no line: lane 0 (in) and lane 1 (out) are one-way, and `GetFreeRoutingLane` hands a lane to every Sim that starts at the same slot; `IsLaneUnobstructed` is always true. In: `AnimateEnterRabbitHole(doRoute: false)` at the lane's slot. Out: `AnimateExitRabbitHole`, so arriving NPC riders stand in the exit pacing above | ride `TimeToDestination` + `kTravelExiting` | the entry is already a fade at the slot |
| Tube elevator (`TubeElevator`, Into the Future), firefighter pole | `SimLine.WaitForTurn` (tube: 3 long, 15 sim-min), then an animation with the object as an actor | 15 sim-min, then the interaction fails | none |
| Time portal | `RouteToObjectRadialRange` 1.5 m, no line | - | - |
| Showtime venues, stadium, school, offices | rabbit holes: the two rows above (school pupils skip the exit pacing) | | |
| Taxi, own car, boat taxi, school bus, carpool [inferred, not read again for this section] | one vehicle per Sim or group; no shared line found in earlier work (docs/642) | | |

### 19.2 What is built

Switches `crowdport` (Sims the player does not control), `crowdporthh` (household and selected Sims too),
`crowdportlog` (one `crowdport:` line per act); tunables `kCrowdPortMin` (3, never under 2), `kCrowdPortWait`
(1 sim-minute, 0-15). Rules in `CrowdPortCore.cs`, game side in `CrowdPort.cs`.

| Kind | Where | What happens |
|---|---|---|
| Elevator | `SimQueue.WaitForTurn` hook (sharelanes' `LdsQueueHook.WaitHook`), doors' own line only | Crowd or long wait, and the skip's own conditions hold (doors open, car free, same direction, no exit reason on the Sim): the wait is answered false with `TimedOut`, exactly what the game's line returns after 15 sim-minutes, so `WaitInLine` runs `TryTeleportSkip` itself. Else the Sim stands in the game's line with the timeout cut to `kCrowdPortWait` (or what is left of it), after which the game tries its skip. |
| Rabbit hole, in | `RabbitHole.WaitForEnterSlot` hook (objshare's), a slot held by another Sim, when `rhshare` does not already walk the Sim there | Our wait (at most `kCrowdPortWait`; a cancel, a free slot or the game's hand-over ends it; the Sim is in `WaitingToEnterSims` meanwhile). On a crowd or at the end of it: fade out, `SetPosition` on the enter slot, fade in (the sequence of the game's elevator skip). No reservation is taken; the game's route to the slot (now zero long), door, walk-in animation, use list, posture and events follow unchanged, and its cleanup finds the tables as with `rhshare`. Holder out of scope or the Sim over 40 m away: the game's wait. |
| Rabbit hole, out | new swap `RabbitHole.AnimateExitRabbitHole` (5 arguments; with the switch off the game's body alone) | An NPC's pacing wait is ours: the game's loop, plus a burst: when `kCrowdPortMin` NPCs wait at one rabbit hole, every one waiting at that moment goes on (the game's body is called with `skipWait`). `GetExitSlot` still gives one Sim per exit slot, so the door paces them. `CanSimSkipExitWait` is asked once, as before. |
| Station | the two rabbit hole rows (a `Subway` or `HoverTrainStation` counts as "station") | Going in by the portal has no line (§19.1). Arriving riders leave with the crowd. |
| Other | the line hook | A crowd in a tube elevator's or firefighter pole's line is counted and logged; the game's line is kept (its animation uses the object as an actor). |

Horses, riders, Sims carrying a child and toddlers keep the game's code everywhere, so a carried child is
never split from its carrier. The scope test is the plain player-Sim test: the experimental `HouseholdToo` flag
does not put household Sims in scope, only `crowdporthh` does.

### 19.3 Limits [inferred unless marked]

- Not run in the game.
- Rabbit hole, in: the brief asked for no door animation. The game has no path that puts a walking Sim inside
  without `AnimateEnterRabbitHole`; its tail (door, walk-in, fade) is kept so every piece of bookkeeping is the
  game's. Seen: the Sim fades where it stands, appears on the slot and walks in, overlapping others there.
- Elevator: the skip needs open doors on the Sim's floor. A car that is elsewhere is waited for as before, with
  the game's skip tried every `kCrowdPortWait` instead of once after 15 sim-minutes. A faked timeout whose skip
  the game then refuses (`OnApproachingPortalObject` false) lets the Sim try the lane without being in the line.
  The game clears the Sim's exit reasons after a refused skip, as it does after its own timeout.
- Rabbit hole, out: a burst resets nothing in the game's clock but its usual stamp, so the next NPC waits 15
  sim-minutes again unless a new crowd forms. Two waiting NPCs keep the game's pacing.
- A household holder with `crowdporthh` off keeps every Sim behind it on the game's wait.
- Tube elevators and firefighter poles are counted only.

### 19.4 Checks (2026-11-22, no game)

- `node tools/selftest/crowdport/run.ts`: 46 checks ok. objshare 29, sharelanes ok.
- Swap audit: one new row (`CpExitHook.ExitHook`, yields, stub remap).
- Repro `crowdport-check` (any save): `CrowdPortCheck run` off -> `verdict: BROKEN`, on -> `verdict: FIXED`,
  `controls ok=True`; `result` = counters 4550-4565 and the last line; `lot` = elevator doors with a line on the
  selected Sim's lot, rabbit holes with reserved enter slots or NPCs in our exit wait.

### 19.5 Run plan

Forced repros, not built as entries; `crowdporthh` and `crowdportlog` on, each off then on:

- Elevator: a high-rise lot, 5 household Sims on the ground floor, all ordered Go Here to an upper floor at
  once. Off: `linewait:` lines for `ElevatorDoors`, Sims stand up to 15 sim-minutes. On: `crowdport: elevator:
  ... skips the line` per Sim from the third on (4550), 4561 while the car is away, no Sim left at the doors.
- Rabbit hole: the same 5 Sims ordered into one rabbit hole (bookstore, Attend Class) at once, `rhshare` off.
  Off: `linewait: ... rhdoor` waits. On: `crowdport: rabbit hole in:` lines (4551), every Sim inside, 4563 for
  slots that came free first. With `rhshare` on as well the Sims walk (4479) and 4551 stays low.
- Subway (Bridgeport): 5 Sims ordered to a far lot so that they route through one station. Expected in both
  arms: no lane wait going in; `station out` lines only when NPC riders arrive together.
- NPC exits: any office tower at closing time with `rhexitunseen` off, camera on the door. Off: 4564 stays 0
  (the hook asks nothing), NPCs come out 15 sim-minutes apart. On: 4564 counts the waits, 4552 the Sims that
  left with a crowd, 4565 those the game's pacing let out.
- In every arm 4556 must stay 0 and no `crowdport: error at` line may appear.

### 19.6 Storms (2026-11-22, Deck)

Route storms s520-s529 with the switch on in half: 3 rabbit-hole-in acts at a busy diner and grocery (`is set on
enter slot ... 2 Sims there, waited 1.3 sim-min`), 7 NPC exit waits ended by the game's own pacing, errors 0.
**Not run in the game as a forced repro yet** (pending): crowded elevators, the exit burst and stations did not occur.
## 20. WooHoo partners and paired socials are snapped onto their spots (`woohoosnap`, `pairsnap`; 2026-11-22, `feat/woohoosnap-1122`, built, default off, NOT RUN)

A player's wish: "WooHoo. If they get to kissing and just can't reach the bed, one of them, or the shower or wherever
they are woohooing, you can teleport them, that's a big issue a lot of times." `WooHooSnap.cs`, rules in
`WooHooSnapCore.cs`. The game's tests (age, relationship, privacy, object in use) are untouched; only the walk is
replaced.

### 20.1 How the game routes the two partners [verified, decompiled `Sims3GameplayObjects`, `Sims3GameplaySystems`, `Sims3StoreObjects`]

The social's callback (`SocialCallback.OnWooHoo` / `OnTryForBaby`) asks the object's own social
(`IWooHooSocialInteraction.PushWooHoo`) or, without one, the nearest bed. Each partner then walks by itself; every
walk ends in `SimRoutingComponent.DoRoute`, and a failed one ends the WooHoo for both (a plain `return false`, the
other side times out after 30 s or sees the missing partner).

| object | starter | partner | after the walk |
|---|---|---|---|
| bed, tent, igloo | `EnterRelaxing`: `RouteToUnusedPart`; then `Beds.WooHoo` (the only `IIsWooHoo`) from the queue | `EnterRelaxing`: `RouteToUnusedPart` | `PartData.IsRoutingSlotObstructed` ends Relax on a closed slot |
| shower, shower tub | `Shower.WooHoo`: ring 1.5-4 m (result unused), then `RouteToSlot` slot 0 | `TakeShower` (`HavingWooHoo`): `RouteToSlotAndCheckInUse`; linked to the starter's | exit waits while slot 0 is obstructed (no time limit) |
| all-in-one bathroom | `WoohooInAllInOneBathroom`: `RouteToSlotAndCheckInUse` slot 1 | `...B`: `RouteToSlot` slot 2 | 30 s wait for the partner |
| wardrobe | `WooHooInWardrobeA`: radius 1.5 m, then a slot 0 route | `WooHooInWardrobeB` goes first: `RouteToSlotAndCheckInUse` slot 0 | each watches the other's interaction |
| leaf pile, hay stack | `WoohooInPileOrStack`: `RouteToSlotListAndCheckInUse` | `...B`: cone 1-2 m, then a slot route | `CanWooHooIn`, 30 s wait |
| sarcophagus, time machine, bot station, box of mystery | `<Object>+WooHoo`: `RouteToSlotAndCheckInUse`; the partner's interaction is made after it | the same class: a slot route that ignores the starter | `ActorsUsingMe` |
| actor trailer, photo booth | `<Object>+WooHoo`: slot 0 | the same class, linked | `HasSpaceFor` |
| elevator | `ElevatorDoors+WooHoo`: ring at the doors, then slot 0 of the car | the same, slot 1 | three 30 s sync waits |
| underwater cave | `WoohooInCave`: slot 0 | the same, after the starter | sync |
| gypsy caravan, resort tower | rabbit hole entry (`RouteNearEntranceAndEnterRabbitHole`) | the same | |
| hot tub, sauna, treehouse, fairy house, hot air balloon | no walk in the WooHoo: both are in the object already (`GetIn` earlier, an ordinary slot route) | | |
| rabbit holes (`WooHooInRabbitHoleWith`) | no walk: both are inside | | |

Paired socials (kiss, hug: `ActionData.SocialJig` other than one-person): §17.1's three steps, then
`SocialInteractionA.VerifyPositionAndAlignment` refuses to play unless the two face each other (dot product -1
within 0.001), stand at the social's distance within 2 mm and at one height within 0.133 m. A social with its own
jig (`ForceCreateJig`) places it with `SocialJigTwoPerson.PlaceJig` and plans `RouteToJigA` / `RouteToJigB` (slot
routes on the jig); a failed plan there ends the social before any walk.

### 20.2 What is built

`woohoosnap`, first step of the `DoRoute` hook's chain (BlockedUse.cs), for a failed plan (no destination, no free
slot, places obstructed, no path, a Sim on the place, the start closed) of a human whose running interaction is a
WooHoo step: a class named `WooHoo` / `Woohoo` / `TryForBaby` (with the class it is nested in), an interaction
linked to one (the shower's `TakeShower`), or a bed's `EnterRelaxing` while a bed WooHoo of the pair waits in a
queue. Sim, partner and object must stand on one lot (not the world lot).

1. Walk up: unless the Sim stands in the object's room within 3 m, the game's "near the object" plan (1-3 m) is
   walked when there is one. With none the Sim is moved from where it stands (another level, a closed room).
2. Snap: onto the nearest wanted slot of the route (parts in use are never wanted), facing as the slot does; the
   route answers "arrived" with that slot. A route without slots (the ring round a shower, wardrobe or elevator):
   onto a free place the game's placement search finds at the route's goal.
3. The Sim is remembered for blockeduse's way out (§10.3 step 4), counted in 4574 when it is used.
4. `GameObject.IsRoutingSlotObstructed` (ShowerShare.cs's hook) answers "free" for a Sim that was let onto a closed
   slot of that object, by this rule or by blockeduse. Before, a bed's Relax ended right after blockeduse's snap
   and a shower's exit would have waited for the closed slot without limit.

`pairsnap`, for paired socials of two humans (no baby, nobody riding, swimming or sitting) within 3 m on one level
(height within 0.133 m):

- a. `SetupTwoPersonSocial` answered false (SocialReach.cs's hook; its talk rule looks first): the partner (the
  hook's own Sim) is put at the social's distance from the starter, or from the end of the starter's walk: the
  first of eight ring places (towards where it stood, then 45, 90, 135 degrees to either side, then behind) with
  no wall between that is routable, else the first without a wall. Both face each other; no routes.
- b. In `DoRoute`: a failed plan or walk of either Sim in such a social: onto its jig slot (within 4 m) when the
  route leads to a jig, else onto a ring place round the other; while the other still walks the Sim only turns.
- c. `SocialJigTwoPerson.PlanRouteToJigA` / `RouteToJigB` answered a failed plan: planned again with "pass through
  objects" and "no slot obstruction test" (walls count); a plan of at most 6 m is kept and walked.

Scope: `woohoosnap` / `pairsnap` = pairs without a Sim the player controls, `...hh` = pairs with one; each alone.
Off = compare mode (4567 / 4577). Counters 4566-4581 (`kCounterSlots` 4582, table in docs/registry.md). Lines with
`woohoosnaplog` / `pairsnaplog`: `woohoosnap: <who> <Sim>, <Interaction> with <Partner> -> <Object> (...): snap <m>
to slot ... | kept, <why>`, `pairsnap: ... the partner snaps ... | snap ... onto the jig slot | planned through
objects | kept, <why>`, `...: snap out from ... to ...`; errors always: `woohoosnap: error at <site>`,
`pairsnap: error at <site>`.

### 20.3 Not built, and why

- Hot tub, sauna, treehouse and fairy house: the walk is an ordinary `GetIn` long before the WooHoo; blockeduse's case.
- Rabbit hole WooHoo: no walk. Hover train WooHoo: a group walk (`RouteOutside` with a follower).
- A social's jig that cannot be placed at all (`PlaceJig` false) and a starter's approach from beyond 5 m.
- Horses and pets (box stall, pet house).

### 20.4 Limits [inferred unless marked]

- A snap is a visible jump, for WooHoo of any length on the lot. The Sim then stands in or behind what closed the slot.
- The partner is read from the interaction's target, its linked interaction or a Sim field; when none is set yet
  (the starter of a sarcophagus WooHoo before the partner's interaction exists) scope is decided by the Sim alone.
- Rule a moves the partner from its own task and sets the starter's facing from there; a starter still walking is
  left to the game's facing step, and the game's alignment test then decides (2 mm).
- A ring place that is not routable leaves the Sim in furniture until its next plan (blockeduse's way out).
- Rule 4 changes blockeduse too (default off): a Sim it let onto a slot is no longer refused at that object.
- `RouteBlocked` gained one step: Sims saved while they walk a "near" or clip route of the hook are reset once at
  the first load with this build (docs/242 §2). Two new swaps (plans only, with stubs).

### 20.5 Checks (2026-11-22, no game) and run plan

- `node tools/selftest/woohoosnap/run.ts`: 56 checks. `WooHooSnapCheck run | result | lot`, `PairSnapCheck run |
  result | lot`. Repros `woohoosnap-check`, `pairsnap-check` (rules, counters, lot lists). Not run.
- Forced repro `woohoosnap-force` (outline): `make_household` two young adult partners (romantic interest set) on an
  empty lot, `build_layout` a room with a double bed in the middle, `bed_setup` closes one side with a fridge on its
  routing slot (as `makebedboth-force`), autonomy off; order WooHoo on the bed. Off: the second Sim's
  `routefail: ... EnterRelaxing`, 4566 and 4567 count. On (`woohoosnap`, `woohoosnaphh`, log): `woohoosnap: ... snap
  ... to slot`, 4569 = 1, both Sims in the bed, the WooHoo plays; afterwards 4574 or blockeduse's "walked out".
  Second arm: the same with a shower whose slot a dresser covers (4570).
- Forced repro `pairsnap-force` (outline): the same household, a room cut by a row of counters with one Sim on each
  side 1.5 m apart (`move_sim`), order Kiss. Off: route-fail balloon, 4576 / 4577. On: `pairsnap: ... the partner
  snaps`, 4579 = 1, the kiss plays, no `kept` line.
- Pass: 4575 = 0 and 4581 = 0, no `error at` line, no Sim left in furniture after its next walk, `applied` shows
  the two jig swaps `ok`, `blockeduse-force` and `showershare-force` unchanged.

## 30. A social's stand-and-wait for a spot that cannot be reached without people either (`standwaitsocial`; 2026-11-24, `post/standwaitsocial-1124`, built, default off, PROVEN by storm s549a)
- Numbered as on the combo branch (§21-§29 there: partnerpull, readwalk, queuewake, slotretry, lineswitch, reactoverlay,
  linestale, their PRs pending).
- Lead (s543a, Bridgeport, upper floor, lot 0xF5900033BA9E8C10, room 8): Indie Girl and later Kirby Wise
  (`SocialInteractionB`) stood at 501.9,34.1,1535.5 with their jig goal 0.4 m away; every replan failed in ~27 ms (no
  path, 0 plans), blocker = the starter (Matty Crewe, Katelyn Missoni) 0.1 m away; standwaitfix's people-free passes
  failed too (4 plans); standwait ended by timeout after 20.8 / 21.6 sim-min, then socialreach let them talk.
- Our switches ruled out first: s544a = Bridgeport, seed 152, every managed fix and every native route key off (the
  read-only keys too), log switches only. No social receiver froze (household zero-plan route fails 4 vs 34 in s543a),
  but no receiver was pulled either. The on-arm replays: s545a Kirby Wise froze again (21.6 sim-min, same spot, same
  goal); s546a, s548a: no freeze.
- Mechanism [verified from the logs]: every freeze follows a `partnerpull` pull of a receiver sitting at the computer
  at 502.5,34.1,1535.2 (`Make Inappropriate Forum Post`, `Work Indefinitely`); the starters were 2.6-20 m away at the
  pull (s546a pulled lines now name the partner's posture and the distance), so the starter arrives at its spot by the
  chair while the receiver stands up. The receiver lands 0.2-0.3 m from the starter and its jig spot (placed while it
  sat) is out of reach with or without people. EA's code: the same happens whenever the computer user ends its action
  inside the starter's 15-minute wait; partnerpull makes it frequent. A seat rule in partnerpull (keep a seated partner
  with the starter within 1.2 m) was tried and dropped: the starter is never that close at the pull.
- Fix `standwaitsocial` (`StandWaitSocial.cs`, `StandWaitSocialCore.cs`, counters 4687-4691, `StandWaitSocialCheck`, no
  swap): in standwaitfix's blocked cycle, after `kStandWaitSocialPasses` (2) failed people-free passes of a waiter whose
  running interaction is `SocialInteractionA` / `B`, the cycle handler answers "not elected to exit" and the route fails
  as at the timeout (standwaitlocked's exit). Waits that people alone block (the pass works) are not touched. Needs
  standwaitfix (the passes are its plans). `standwaitsocial` NPCs, `standwaitsocialhh` household and selected Sims.
  standwait lines get a ", social: N failed people-free passes, gave up cycle C" tail. Selftest
  `tools/selftest/standwaitsocial` (7 checks).
- How often: Bridgeport storms with partnerpull: s543a 2 frozen receivers, s545a 1, s546a 0, s548a 0 (Matty Crewe's
  receiver wait at 492.6,34.1,1537.8 ended by standwaitfix's yield after 7.7 sim-min), s549a 1; 0 in s541 / s542 /
  s543b / s543c; 0 long SocialInteractionB stand-waits in ~150 storms without partnerpull (SocialInteractionA waits of
  11-22 sim-min there are EA's starter waits, not this).
- Run s549a (Deck, Bridgeport new game, seed 152, 5 min, build r9d, s541 all-on list + standwaitsocial(+hh)): Kirby
  Wise (selected, `SocialInteractionB`, at 492.6,34.1,1537.8, the second computer of the lot, starter Indie Girl)
  "blocked 5 of 7 cycles, 11.4 sim-min ... social: 2 failed people-free passes, gave up cycle 5"; socialreach then let
  him talk from where he stood (0.1 m). Off (s543a, s545a): the same waits ran 20.8-21.6 sim-min to the timeout. Saved
  about 10 sim-min a case (the passes come every few cycles, so the second failed pass lands at cycle 5). Errors 0.
  Status: built, default off, PROVEN by storm (one case on, three off).
