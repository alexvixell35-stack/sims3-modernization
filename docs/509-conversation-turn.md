# 509. A conversation turn held by a Sim that never takes it (`convturn`, issue #166 build 19)

A conversation between a player's Sim and a townie can stop for good: both stand there, nobody speaks, until a need or
the player ends it. One contributor's census tool saw it as "in a conversation, not their turn" for 30 and 21 checks in a
row, twice in one session on a world lot (issue #166, build 19). This doc explains why, and describes the ts3fixes switch
`convturn` that hands such a turn to the Sim who can take it. Off by default. Counters 2031-2032 (registry `fixes-2031`),
ledger §907.

Claim marks: **[verified]** = read in the game's IL by us (`ildasm`; no code reproduced here) or seen live;
**[inferred]** = our reading, not yet checked in game.

## 1. Why the turn never moves

- **The turn passes only on a pick** [verified, IL]. `Conversation.mWhoseTurn` is written in three places:
  `Initialize` (the first member), `UpdateOnSelectingInteraction` (when the Sim on turn selects a social, the turn moves
  to `NextSim`, and `mWhenTalkedLast` is set to the elapsed sim minutes), and `RemoveSim`. No timer moves it.
- **Only the Sim on turn scores socials** [verified, IL]. `SocialComponent.ScoreSocialsOnSims()` returns at once unless
  `Conversation.WhoseTurn` is its own Sim; otherwise it picks a member and scores the socials for that pair.
- **Only local Sims ask** [verified, IL]. `Autonomy.RunAutonomy` calls `ScoreSocialsOnSims()` for a Sim in a
  conversation only when `ShouldRunLocalAutonomy` is true: the Sim is in the active household, on the active lot or on
  the selected Sim's lot (not a world lot), a party guest, or on screen (docs/59, B4).
- **So** [inferred]: when the turn sits with a townie that runs no local autonomy, the townie never scores and the
  player's Sim never gets the turn. The conversation waits until one of them leaves it for another reason.

## 2. The fix (`convturn`, `mods/ts3fixes/src/ConvTurn.cs`)

A hook on `SocialComponent.ScoreSocialsOnSims()` (the no-argument overload). Before EA's method, it checks whether the
turn is stuck for the asking Sim (`ConvTurnCore.Stuck`): the holder is nobody, gone, has no autonomy, does not run local
autonomy (`Autonomy.IsRunningLocalAutonomy`, which every local run refreshes), has no queue, is busy with an interaction
that is not a social, or nobody has picked a social for 15 sim minutes. If so, and the switch is on, the asker gets the
turn by the same plain store of `mWhoseTurn` that EA's own turn changes use. EA's method then runs unchanged, as on the
asker's own turn.

- Only local Sims call the method, so two off-screen townies talking to each other are unchanged.
- The check runs before every call with the switch off too, and counts what it would do: [2031] stuck turns seen by a
  scoring call (one stuck conversation counts once per scoring call), [2032] turns taken.
- The check is in a try/catch: it never stops EA's scoring.

**Not ported** (mid-method, docs/506): the player's core build also let `RunAutonomy` score socials for the townie on
its own turn when another member is local. With the hook, the local member takes the turn instead, which ends the same
wait from the other side.

## 3. Checks

- **Self-test** (`node tools/selftest/convturn/run.ts`): every input combination of `ConvTurnCore.Stuck` against the
  rule written out a second time, plus the named cases (off-screen holder, local holder talking, idle 10 and 20 minutes,
  busy with a non-social, the asker on turn, nobody on turn).
- **Repro** `conversation-turn-check` (queue `conversation-turn.json`, Baseline-BP, `Fixes.ConvTurnCheck`): starts a
  chat between the active household's two Sims, then in one call per step gives the turn to the other member,
  backdates the last social by 20 sim minutes (a silent holder) and runs the hook's check, as EA's
  `ScoreSocialsOnSims` would. `expect stuck` (off): seen once, nothing taken, the turn stays with the holder.
  `expect control` (on, the holder local and the last social just now): nothing seen. `expect taken` (on): seen once,
  the turn is the asker's. Each throws on a wrong outcome; `pick` throws when no conversation formed.
- **What the repro does not build**: the off-screen holder of the live report. Both Sims of the repro are local, so it
  checks the hook's decision and its effect on a real conversation, not that EA would wait forever.

## 4. Results

Not run yet.
