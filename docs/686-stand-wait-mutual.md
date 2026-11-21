# 686: household Sims standing at a stair top or doorway: EA's obstacle stand-and-wait never pushes or yields once its replan fails

2026-11-21, branch `fix/stand-wait-1121`. Storm logs s141-s291 (Isla Paradiso, Collins household, 2-minute route storms) +
decompiled C#. Built, default off, **NOT RUN**. Switches `standwaitfix`, `standwaitfixhh`, `standwaitlog`; counters
4300-4319; `StandWait.cs`, `StandWaitCore.cs`; repros `standwait-check`, `standwait-landing-crowd`.

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
