# 647: NPC shuffleboard streaks (`shuffleboardnpccap`)

2026-11-19, OFF run logs (health.jsonl `definition` field, ufplan lines) + decompiled script code, built, **NOT RUN**
(package tag `npcloopsc`). Managed ts3fixes switch `shuffleboardnpccap`, default off, `ShuffleboardNpcCap.cs`, pure rule
`ShuffleboardNpcCapCore.cs`, self-check `tools/selftest/shuffleboardnpccap`. Slots 4090-4093 (block 4090-4099,
docs/registry.md).

## 1. What the logs show

Bridgeport bars (Eugi's, Waylon's Haunt): NPCs play shuffleboard for hours. Rafael Striker played alone from 16:00 to
02:00 (phase-20261116-1503): 29 route plans, about one every 17 sim-minutes [measured, log].

## 2. EA's loop [verified, decompiled `Shuffleboard`]

- `Join.Run` puts the Sim on a team; with fewer than two users it sets `mShouldRestart`. `PushNextInteractionAfterJoinOrPlay`
  pushes `Play` (the current player) or `Wait` as a must-run continuation.
- `Play.Run` (continuation): when `mShouldRestart` or `mShouldClearRound` is set (a round start), it routes to the play
  slot (`RouteToPlay`), then `SetupForNewGame` clears the board. Every throw routes again (`DoAction` -> `RouteToPlay`).
  After the throw it pushes the next Play / Wait.
- `EndRound` counts a round only with two teams (`mRound++` under `mTeams.Count > 1`); game over (`kNumberOfRounds`, 5)
  sets `mShouldRestart` and `mGameOver` and gives every player `ExitReason.Finished`, which ends their Play / Wait.
- So a Sim playing **alone never reaches game over**: rounds go on until a motive or an outside cancel ends them. With two
  teams the game ends after 5 rounds, and autonomy can pick `Join` again straight away.

## 3. The fix (default off)

At a round start (`Play.Run` pushed as a continuation, `mShouldRestart` or `mShouldClearRound` set, before EA's own code
runs) the board's streak is judged:

- **Who:** only when every Sim using the board (`ActorsUsingMe` plus the actor) is a non-exempt NPC. The fix cuts an
  interaction, so the check is `HouseholdExempt || IsPlayerSim`: a household, selected or unknown-scope player keeps the
  whole board on EA's path ([4091]). `HouseholdToo` never lifts it.
- **Streak** (per board): round starts seen, first and last sim time; a round start more than
  `kShuffleboardNpcCapGapHours` (1) after the last one begins a new streak.
- **Cap**, never before one round is done: the night window `kShuffleboardNpcCapNightFrom..To` (23-06); or
  `kShuffleboardNpcCapGames` (2) games of EA's `kNumberOfRounds` rounds (a solo Sim's rounds count the same); or
  `kShuffleboardNpcCapHours` (3) sim-hours since the first round start. The hours cap is needed for solo play: its rounds
  are slow (the logged one made one plan per ~17 sim-minutes, about 3 throws a round) and it has no game over.
- **End:** EA's own path for a Sim that cannot play on, as `Play.Run` does when `RouteToPlay` leaves an exit reason:
  `Shuffleboard.Quit(Actor)` and `Run` returns true. Nothing has entered the state machine yet, so no animation is cut, and
  no route is planned. `Play.Cleanup` then calls `MakeSureSomeoneIsPlaying`: a remaining player gets the turn, its Play
  sees the same round start and the same capped streak, and ends too. With every player gone `mTeams` is null; the next
  `Join` sets `mShouldRestart` and `SetupForNewGame(true)` resets pucks and rounds, as after any EA quit.
- **Re-join cooldown:** an ended Sim's **autonomous** `Join` tests (any board) fail for `kShuffleboardNpcCapCooldownHours`
  (2), so autonomy does not restart the streak at once. User-directed joins and exempt Sims always get EA's test.

Two-team games already end at game over; for them the cap only stops re-joins past 2 games (the streak spans games while
the gap is under an hour).

Counters: [4090] Plays ended, [4091] round starts (or cooled Join tests) left to EA for an exempt player, [4092] errors,
[4093] autonomous re-joins refused. Agent `shuffleboardnpccap_ended`, `_exempt`, `_errors`, `_rejoins_refused`.

Log (at most 10 per real minute):
`shuffleboardnpccap: <Sim> stops playing (<games|night|hours>) after <n> rounds / <h> sim-h at <lot>, <time>; no autonomous re-join for 2 sim-h`.

## 4. Limits [inferred]

- A round start whose Play quits by EA's own route failure keeps the round flags set, so the next player's round start is
  counted again (the streak caps a round early at worst).
- `PlayWith` (one Sim asks another to play) is not refused during the cooldown; it is a social pick, not the solo loop.
- Pucks left on the board stay until the next game's `SetupForNewGame`, as after any EA quit.

## 5. Test

`TS3_CSC=<csc> node tools/selftest/shuffleboardnpccap/run.ts` (night window, rule order, solo hours cap, games cap, gap
and clock-back resets, capped streak kept for the other players, cooldown, bounded tables). In game: NOT RUN; an OFF/ON
pair on a Bridgeport bar lot with an NPC playing alone should show the `shuffleboardnpccap:` line and no Play route plans
for that Sim after it.
