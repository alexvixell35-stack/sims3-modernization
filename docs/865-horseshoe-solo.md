# 865: Horseshoes played alone never end, `horseshoesolo`

2026-11-22 (UTC), static (EA code read in a local decompile, not committed) plus our test logs. Fix built, default off.
Decision check passed in game (pass-11 contributor beta); no live solo game yet.

## 1. Question

Festival NPCs (Springtime / Eerie Spring Festival, `NpcParty` situation) stood at a horseshoe court throwing alone for
17-53 game hours in our test logs, Go Home queued behind; all 6 seen were non-household [verified in our logs]. Why does a
solo horseshoe game never end, and what else can keep a court's game stuck?

## 2. Findings [verified, decompiled `Sims3GameplayObjects.dll`, `HorseshoeCourt` and its nested interactions]

A court keeps its game in private fields: `mPlayerQueue` (`Queue<Sim>`, the turn order), `mPlayersWhoHaveThrown`, `mScores`,
`mGameOver`, `mShouldReset`. `Join` (named "Practice" when the queue is empty) and `AskToJoin` queue Sims and give them a
`WaitToThrow`; a `WaitToThrow` whose Sim is at the queue front pushes `TakeTurn` as a continuation and finishes.

`TakeTurn.Run`:

| Step | What |
|---|---|
| throw loop | runs while the Sim has no exit reason (one more throw if the last one ended with the Enter animation); after `kNumThrowsPerTurn` (3) throws it adds `ExitReason.Finished` |
| `DoEndOfTurnChecks(score)` | with `mShouldReset` it resets the game; otherwise it **returns at once when `mPlayerQueue.Count <= 1`**: no score, no winner check, so `mGameOver` is never set in a solo game |
| after it | `mPlayerQueue.Dequeue()`; `flag2 = Actor.OnlyHasExitReason(Finished)` (exact equality of the exit-reason bits, `Sim.OnlyHasExitReason`) |
| `!mGameOver && flag2` | the Sim is queued again; at the front it gets the next `TakeTurn` through `TryPushAsContinuation` (ahead of everything already in its queue), else a `WaitToThrow` |
| `!flag2` | no re-push: `StandardExit`, then `Actor.RouteAway(2, 5, ...)` |

So:

1. A Sim alone at a court throws turn after turn for good. Only an exit reason other than Finished stops it: a cancel, a
   reset, or a later-queued interaction of higher priority (the queue cancels the earlier one with
   `ExitReason.HigherPriorityNext`; the player's commands normally outrank an autonomous game [inferred]). An NPC whose
   queued Go Home does not outrank the game never gets there.
2. `TakeTurn.Cleanup` never takes the Sim out of `mPlayerQueue` (only `WaitToThrow.Cleanup` does, when it did not finish).
   A Sim reset or deleted mid-turn, or whose queued `TakeTurn` is cancelled before it runs, stays in the queue; at the front
   every later player loops in `WaitToThrow` waiting for `Peek()` to be itself. `DeadRefs` (`HorseshoeHolders`, deadrefsweep)
   clears `mPlayersWhoHaveThrown` and `mScores` of destroyed Sims, not `mPlayerQueue`.
3. `RemoveSimFromQueue(Sim)` (private) rebuilds the queue without the Sim and touches nothing else; EA calls it on every
   cancel path it has.

`InteractionQueue.CancelInteraction(inst, false)` on a running head interaction (head locked) adds
`ExitReason.CanceledByScript` to the Sim, which is EA's standard script cancel [verified, `CancelNthInteraction`].

## 3. Fix: `horseshoesolo` (ts3fixes `HorseshoeSolo.cs`, `HorseshoeSoloCore.cs`, tunings `kHorseshoeSolo` default off, `kHorseshoeSoloHours` = 2)

A pass every 10 sim minutes (`AlarmManager.Global`, never saved, started at world load) over every `HorseshoeCourt`:

- **Lost players leave the queue** (`HsCore.Remove`): a queued Sim that is destroyed leaves at once; a queued Sim with no
  `TakeTurn` / `WaitToThrow` on this court anywhere in its interaction queue leaves when that held at the previous pass too
  (two passes, so no hand-over between EA's interactions counts). Through EA's `RemoveSimFromQueue`. Any court, household
  Sims too: only the court's queue changes, never a Sim.
- **Solo cap** (`HsCore.Cut`): when the queue holds one Sim whose running interaction (`GetCurrentInteraction`) is `TakeTurn`
  on this court, the Sim is not household, selected or of unknown scope (`IsPlayerSim`), and it has played alone for
  `kHorseshoeSoloHours` sim-hours since the fix first saw it so, or a non-autonomous interaction is queued behind the game:
  the Sim gets `ExitReason.CanceledByScript`. The throw in progress finishes, then `TakeTurn` takes EA's own `!flag2` path
  (no re-push, `StandardExit`, `RouteAway`); the queued Go Home or command runs next. A Sim already leaving (an exit
  reason other than Finished pending) is left to EA. The clock restarts with each new solo game.
- With the switch off the pass only counts what it would do (2193, per pass).
- Household rule: the cut uses `IsPlayerSim`, the base of the shared `HouseholdExempt` without its `HouseholdToo` lift,
  because a cut is never lifted (CLAUDE.md). The household's own solo practice is never touched.
- `kHorseshoeSoloHours = 0` keeps only the "something non-autonomous waits" rule.
- No method swap (so no swap-stack-audit rows); reflection on `mPlayerQueue`, `RemoveSimFromQueue(Sim)` and the nested
  `TakeTurn` / `WaitToThrow` types, all checked by `refs-resolve-check` against the game assemblies.

[inferred] Situation pushes such as the festival's Go Home are queued non-autonomous; if not, the 2 h cap still ends the
game. [inferred] A cut NPC may pick the court again by autonomy; that starts a new solo clock (a new choice, not the loop).

Larger or streamed worlds: the pass reads `GetObjects<HorseshoeCourt>()` (a type index, few objects) and keeps per-Sim state
only for Sims seen at a court, dropped at the next pass once they leave; nothing tied to world size.

## 4. Counters (contributor range, reserved by hand: free in source and registry here and in the open contributor work; `registry.ts reserve --contributor` found no 4 free slots)

[2191] solo turns ended, [2192] gone or idle players removed from court queues, [2193] seen with the switch off (would end
or remove), [2194] errors.

## 5. Check and repro

`Ts3Fixes.Fixes.HorseshoeSoloCheck`: `run` puts four layouts (an NPC alone 3 h, an NPC alone 0.5 h with Go Home waiting, a
deleted Sim in the queue, a queued Sim not playing at two passes) and six controls (a household Sim alone 10 h with a command
waiting, a two-player game, a short solo game, a solo Sim not in `TakeTurn`, a Sim not playing at one pass only, a Sim
playing) through `HsCore`: off `verdict: BROKEN`, on `verdict: FIXED`. `scan`: live courts, queue sizes, who is queued
(household, GONE, NOT PLAYING) and solo hours tracked. `result`: the counters. Repro `horseshoe-solo`; offline selftest
`tools/selftest/horseshoesolo`.

End to end (not done): a festival lot (Springtime / Eerie Spring Festival) or any lot with a court, an NPC told to Practice
(`setup_sims` with `autonomy: false`, then push `Join` on the court); `scan` shows it alone; on: within 2 h 10 min 2191 rises,
the NPC routes away. Gone player: two Sims queued, delete the one throwing; on: 2192 rises and the other takes its turn.

## 6. Not done / next

- In game (contributor beta pass 11, Isla Paradiso): repro `horseshoe-solo` off `verdict: BROKEN`, on `verdict: FIXED`,
  controls kept, 2194 = 0, every reflected member found; `scan`: no horseshoe court in that world. Still to do: the
  end-to-end festival case.
- A two-player game where one player keeps getting skipped is not covered (not seen).

## Import 2026-11-22 (maintainer)

PR #643, counters 2191-2194 kept. `horseshoesolo` is **ON by default** at import (Rodney's rule, contributor repro with harness evidence), with a weaker proof than the others: repro `horseshoe-solo` off BROKEN / on FIXED, controls kept, errors 0 in game (Isla Paradiso), but that world had **no horseshoe court**, so the live cut (a non-household NPC alone for `kHorseshoeSoloHours` = 2 sim-hours) has not run in game. [inferred] to work live; to be confirmed on a festival world. Household rule in code: `IsPlayerSim` (household, selected, unknown scope never cut; the `HouseholdToo` lift does not apply to cuts). The lost-player removal only drops queue entries of destroyed or not-playing Sims through EA's `RemoveSimFromQueue`, it does not change what a Sim does.
