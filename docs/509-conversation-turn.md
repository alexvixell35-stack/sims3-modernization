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
- **Prior fixes** [verified, IL]: NRaas ErrorTrap only drops a destroyed Sim from `mWhoseTurn` (its conversation
  dereference). No fix for a living holder that never takes the turn in the NRaas assemblies we have, and none found online.

**Not ported** (mid-method, docs/506): the player's core build also let `RunAutonomy` score socials for the townie on
its own turn when another member is local. With the hook, the local member takes the turn instead, which ends the same
wait from the other side.

## 3. Checks

- **Self-test** (`node tools/selftest/convturn/run.ts`): every input combination of `ConvTurnCore.Stuck` against the
  rule written out a second time, plus the named cases (off-screen holder, local holder talking, idle 10 and 20 minutes,
  busy with a non-social, the asker on turn, nobody on turn).
- **Repro** `conversation-turn-check` (queue `conversation-turn.json`, Baseline-BP, `Fixes.ConvTurnCheck`): puts the
  active household's two Sims in one conversation with EA's `SocialComponent.JoinConversationWith` (what EA's
  `StartSocializingWith` calls when a social starts), then in one call per step gives the turn to the other member,
  backdates the last social by 20 sim minutes (a silent holder) and runs the hook's check, as EA's
  `ScoreSocialsOnSims` would. `expect stuck` (off): seen once, nothing taken, the turn stays with the holder.
  `expect taken` (on): seen once, the turn is the asker's. The not-stuck cases (holder local and talking, idle under
  20 minutes, the asker holding the turn) are in the self-test only: in a joined conversation the holder is always busy
  with a non-social, which the hook counts as stuck. Each throws on a wrong outcome; `pick` throws when no conversation formed.
- **What the repro does not build**: the off-screen holder of the live report. Both Sims of the repro are local, so it
  checks the hook's decision and its effect on a real conversation, not that EA would wait forever.

## 4. Results

**Runs 1-4** (2026-11-01, Baseline-BP, FAIL in the set-up, before any expect step): the repro started the conversation
with a queued Chat, and autonomy won each time. Run 1: the pie-menu label was not found. Run 2: the Chat sat behind the
Sims' other interactions. Run 3: both Sims went to bed and EA refused the Chat. Run 4: the Chat was queued, but 40 s
later neither Sim was in a conversation. The repro now joins the two with EA's own call, which needs no free Sims and no
wait.

**Run 5** (2026-11-01, Baseline-BP, FAIL in the on arm's set-up): the join worked (2 members). Off arm: `expect stuck`
as expected, seen +1, taken +0, the turn stays with the holder. Before the on arm, EA ended the conversation (Tiara
went to bed, Lala walked away), and `pick` threw. Each arm now forms its own conversation.

**Run 6** (FAIL in the off arm's set-up): the conversation ended in the second between `pick` and `expect stuck`. An
expect step that finds it ended now joins the same two again in its own call, so no tick passes before the check; its
result says so.

**Run 7** (FAIL in the off arm's set-up): the conversation ended in the second between `chat` and `pick`. `pick` now
joins the same two again in its own call when it finds no conversation, as the expect steps do.

**Run 8** (FAIL in the on arm's control step): the off arm passed (rejoined in the step, seen +1, taken +0, the turn
stayed with the holder). `expect control` saw the turn as stuck: in a conversation the repro joins, the holder is busy
with a non-social interaction, which the hook counts as stuck by design. The control step is out of the repro.

**Run 9** (2026-11-01, Baseline-BP, PASS in 8.7 min). Off: `pick` and `expect stuck` each found the conversation
ended and joined the two again in the same call; seen +1, taken +0, the turn stayed with the holder (Lala). On: the
conversation held; seen +1, taken +1, the turn is the asker's (Tiara). The `state` step right after: turn Tiara, 33.0
sim minutes since the last social (20 backdated plus about 13 that passed), so no social followed within that time; both
Sims were busy with other interactions. Ledger I-907-2: the turn moving to the asker is verified; that EA then scores and
the conversation goes on is not shown yet.
