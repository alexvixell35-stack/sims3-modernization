# 648: NPC paparazzi route checks and re-follows (`paparazzicooldown`)

2026-11-19, OFF run logs (health.jsonl `definition` field, ufplan lines) + decompiled script code, built, **NOT RUN**
(package tag `npcloopsc`). Managed ts3fixes switch `paparazzicooldown`, default off, `PaparazziCooldown.cs`, pure rule
`PaparazziCooldownCore.cs`, self-check `tools/selftest/paparazzicooldown`. Slots 4094-4097 (block 4090-4099,
docs/registry.md; 4098-4099 free).

## 1. What the logs show

- Patrick Patel (phase-20261119-0746): 14 route plans in one `RolePaparazzi+FollowCelebrity` [measured, log].
- Becca Bijou (phase-20261115-1617): the follow restarted 9 times [measured, log].

## 2. EA's loop [verified, decompiled `RolePaparazzi`]

- `FollowCelebrity.Run` routes close to the celebrity (`RouteCloseToSim`: a one-person jig, `FindGoodLocation`, a plan),
  then `DoLoop(Default, PaparazziLoopCallback, smc, kPaparazziRouteCheckTime)`: the callback runs every **1 sim-minute**
  of loop lifetime. Each call Wanders 1-2 m (a route plan), and when the celebrity is out of sight or past
  `kRefollowCelebrityRange` (10 m) it re-runs `RouteCloseToSim`. If that fails the follow ends.
- The follow ends when its lifetime passes `mLoopTime` (30-45 sim-minutes) or the celebrity leaves the lot; that branch
  calls `RolePaparazzi.ResetSimToFollow()` (`mFollowingSimId = 0`).
- `RolePaparazzi.SimulateRole` (every role pulse, while the paparazzo is not following) picks a celebrity on the lot other
  than `mFollowingSimId`. With the id reset, the same celebrity is picked again straight after each follow.

## 3. The fix (default off)

Scope: a non-exempt NPC paparazzo following a celebrity **outside the household scope**. Both Sims go through
`HouseholdExempt || IsPlayerSim` (a skip and a cut, so `HouseholdToo` never lifts it).

**Household celebrity: EA, always.** A household (or selected, or unknown-scope) celebrity keeps EA's paparazzi in full:
the player sees them as a feature of fame, and a household Sim never counts as unseen. The brief allowed acting when the
celebrity is unseen *or* not in the active household; since a household Sim is never unseen, both readings come to
"non-household celebrity only", and that is the rule. A household paparazzo is exempt likewise. Exempt follows are
counted in [4096].

- **(a) stretched route checks:** while the paparazzo is unseen (`TownieUnseen`: NPC, standing, not on screen, far from
  viewers) and the celebrity is unseen (not on screen, `FarFromViewers`), `PaparazziLoopCallback` runs EA's body at most
  once per `kPaparazziCooldownCheckMinutes` (10) of loop lifetime; the other calls return at once (no Wander, no
  re-route, no idle change). A due exit (lifetime past `mLoopTime`, or the celebrity on another lot) always runs EA's
  body, so the follow ends exactly as EA ends it. A seen moment runs EA's body and restarts the 10-minute wait. A photo or
  notes loop animation started by the last EA call simply keeps looping until the next one.
- **(b) re-follow cooldown:** when a follow's `Run` returns, the pair (paparazzo, celebrity) is kept with the sim time.
  A new `FollowCelebrity.Run` of the same pair within `kPaparazziCooldownHours` (2) returns false before doing anything
  (no jig, no route). EA's `mFollowingSimId` was set to that celebrity by `StartFollowingSim`, so the role's next pulses
  pick another celebrity on the lot, or none; the paparazzo leaves the lot at EA's `kStayOnLotTime`. A follow the
  celebrity asked for (`CalledToFollow`, Call Over Paparazzi) is never refused.

Counters: [4094] callbacks skipped (stretched checks), [4095] follows refused by the cooldown, [4096] follows left to EA
for an exempt Sim, [4097] errors. Agent `paparazzicooldown_stretched`, `_refused`, `_exempt`, `_errors`.

Log (at most 10 per real minute):
`paparazzicooldown: <paparazzo> following <celebrity>: both unseen, route checks every 10 sim-min` (once per follow),
`paparazzicooldown: <paparazzo> does not follow <celebrity> again yet (<n> sim-min of the cooldown left)`.

## 4. Limits [inferred]

- A refused follow leaves `mFollowingSimId` on that celebrity until EA resets it (the end of another follow, or
  `FindNewLot`), so on a lot with one celebrity the paparazzo may wait past the 2 h for its next follow of them.
- Celebrity points and the In the Limelight moodlet come from `Run`'s start, not the loop: a refused follow does not give
  them; an unseen NPC celebrity loses at most one award per 2 h per paparazzo.
- Stretched checks mean an unseen paparazzo drifts further from an unseen celebrity between checks; the next EA call
  routes it close again.

## 5. Test

`TS3_CSC=<csc> node tools/selftest/paparazzicooldown/run.ts` (skip rule, a 40-minute unseen follow = 4 EA runs, due
exits, seen moments, new follow instances, cooldown pairs and expiry, clock back, bounded tables). In game: NOT RUN; an
OFF/ON pair with an NPC celebrity followed off screen should show fewer `FollowCelebrity` plans per follow and the
`paparazzicooldown:` lines.
