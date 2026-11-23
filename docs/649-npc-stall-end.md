# 649. NPCs frozen in one interaction for hours (`npcstallend`)

2026-11-19. Static (local decompile `game-copy/decompiled`, not in git) + existing `health.jsonl` censuses
(`phase-20261115..19`, OFF/sweep arms; tick ~37.5 per sim-minute). Built (tag `npcstall`), default off, NOT RUN.
Slots 4100-4109 (`kCounterSlots` 4110; 4090-4099 belong to docs/647-648).

## 1. Census [measured]

Stalls = the same head interaction instance and the same spot (`interaction_min` and `still_min` both large).
Harness Sims (`unseengroups.txt`) and their spawns left out.

| Sims, run | Definition | Still | Motives | Verdict |
|---|---|---|---|---|
| Deidre Littler + Odine Perry, 20261116-1409 (Bridgeport Acres) | `PlayCatchObject+PlayCatchInteraction` / `+PlayCatchPlayer2` | 1372 min (overnight) | change | EA bug, §2 |
| Ace Wilde, 20261116-1503 | `Sim+SwitchToOutfit` | 1068 min | frozen | EA bug, `outfitwaitend` (docs/601 §1) |
| Tariq Scott, 20261119-1039 (ON arm, `outfitwaitend=on`) | `AthleticGameObject+WorkOut+CardioDefinition` | 1425 min | frozen (h -49, e -18.7) | EA bug, §3 |
| Girl Clubber, 20261117-0008 | `WorkOut+StrengthDefinition` | 921 min | frozen | outfit wait [inferred], §3 |
| Girl Clubber, 20261116-2143 | NRaas `GetMailEx+FakeDefinition` | 1065 min | frozen | noted only (NRaas code not patched) |
| Girl Clubber and 10 more, 9 runs | `TrashcanIndoor+EmptyTrash+FakeDefinition` | 400-1122 min | frozen | blocker unread, §4 |
| Matthew Hamming, Tom Wordy, ... | `VisitCommunityLot` | 6-18 h at the venue | - | generic rule |
| others >= 6 h | `DanceTogetherA`, `SocialInteractionB`, `PracticeChess`, `Guitar/Piano/Drums+Jam`, `SwingSet`, `PlayComputerGames` | 6-8 h | - | generic rule |

Kept by design (long legit kinds): `WorkInRabbitHole` (103 Sims), `PerformForTips`, `PianistPerform`, `WatchDoor`,
`ReadBook`, `Guitar+Watch`, `AttendShow`, `ThrowRice`. Mid-route (`routing` true, e.g. `FollowerMeetUpAtPoint`) left alone.

## 2. PlayCatch never times out [verified, decompiled]

`PlayCatchObject.PlayCatchInteraction.RunPostRoute` loops `while (!Actor.HasExitReason(ExitReason.Default))
PlayCatchLoop();`. It never calls `InteractionInstance.UpdateLoop` (PingPongTable and Shuffleboard do), so EA's fun
exit, stress exit and the 3 sim-hour `Autonomy.TimeoutForFunOrStressInteractions` never apply, and the low-LOD cap of
`Interaction.DoLoop` is not on its path. Player2 runs `DoLoop(ExitReason.Default)`, but it exits only through its own
`UpdateLoop` (needs tuning [inferred: none for the pushed Player2]) or the low-LOD cap ([inferred] the children were
high LOD; the census has no LOD field). Only a cancel or a higher-priority push ends it; the watchdog's [48] stationary reset came after a sim-day
(both ended at ~1529 min).

## 3. WorkOut: an outfit wait outfitwaitend leaves alone [verified + measured]

`AthleticGameObject.WorkOut.Run` starts a `SwitchOutfitHelper(GoingToWorkOut)` and calls `Wait(idleIfNecessary: true)`
before its state machine and `DoLoop`. `SwitchOutfitHelper.Wait` loops `while (!mReady) Simulator.Sleep(1u)` with no exit
reason check (docs/601 §1). In Tariq's run `outfitwaitend` was on, yet the stale-wait classes were 2005 = 1 (cause
`cleared`: no pending outfit) and 2011 = 1 (EA dropped the helper's swap callback); 2003 (own) stayed 0. `outfitwaitend`
ends only own-cause waits, so this wait held the task for 24 sim-hours. Girl Clubber's WorkOut and Get Bills / Take Out
Trash (the route-time `GoingOutside` change in `SimScriptAdaptor.CheckForClothingChangesBeforeRouting`) show the same
frozen motives [inferred: no stale-wait class was recorded in those runs].

A cancel cannot reach such a task. With `npcstallend` on, `OutfitWait.cs OutfitWaitEndTry` also ends a cleared-cause wait
of a live off-screen NPC past `kOutfitWaitEndMinutes`, not inside `ChangeOutfit`, with outfitwaitend's own end (EA's
`Abort`, `mNoChange`, `SwitchToOutfitWithoutSpin`, laundry and toddler steps): `NpcStallEndCore.EndClearedWait`. With
`npcstallend` off the wait is kept as before (1979). Counter 4106 (and 1977).

## 4. Open: Take Out Trash

In 20261116-2233 (`outfitwaitpoll` and `outfitwaitend` on) Girl Clubber sat 1122 min in `EmptyTrash` with frozen motives
and no stale outfit wait (2003-2005 all 0), so her task is blocked elsewhere (`RouteToCan` or a `RequestState`): no stack
was captured. The generic rule cancels it; when the task does not reach an exit-reason check, 4104 counts it and the
watchdog's sim-day reset stays the backstop. Next step: a task-stack line in the 4104 log.

## 5. Fix `npcstallend` (`NpcStallEnd.cs`, `NpcStallEndCore.cs`)

Tunings `kNpcStallEnd` (off), `kNpcStallEndHours` 3 (1-1000, else 3). A repeating 10 sim-minute alarm (set on world load
from `UfTpCleanupWorldLoad`) tracks every Sim's head interaction instance and position. Progress = another head, no head,
or a move over 1 m. Past the mark, `NpcStallEndCore.Decide`:

- exempt (`HouseholdExempt` or `IsPlayerSim`: household, selected, selectable, unknown scope): never touched, 4101;
- busy (routing, rabbit hole posture, parented: vehicle, boat, helm, carried; a role Sim; baby or toddler) or an allowed
  kind (`Kind`: sleep, work or school, performing, service role, fishing, reading, watching): 4102;
- task inside `SwitchOutfitHelper.Wait` (flagged by the outfit-wait hook): left to outfitwaitend, 4105;
- else one EA cancel of that instance: `InteractionQueue.CancelInteraction(head, false)` (CanceledByScript; the
  interaction's own exit runs, so postures, animations, linked partners via `FinishLinkedInteraction` and commodity
  updates clean up as for a player cancel), 4100. Never twice: the same instance still there at a later pass counts 4104.

Each stall is counted once per verdict. 4103 errors, 4107 passes, 4108-4109 spare. Log (10 a real minute, 300 a session):
`npcstallend: <name> <definition> stuck H sim-h, ended` and `... cancel did not take (left to the watchdog)`.
Agent `patches`: `npcstallend_ended`, `_exempt`, `_skipped`, `_errors`, `_not_taken`, `_outfit_wait`,
`_cleared_waits_ended`, `_passes`.

Covered elsewhere: `outfitwaitend` (own-cause outfit waits, docs/601), `watchdog` + `watchdogidle` [48] (any non-player
Sim in one spot and interaction a whole sim-day: `SetObjectToReset`), `unseenloopskip` (docs/632, DoLoop low-LOD cap for
unseen NPCs). None ends a stall that sits outside `DoLoop` before a sim-day.

Self-check: `TS3_CSC=<csc> node tools/selftest/npcstallend/run.ts` (every census definition ends past 3 h and waits
below it, every kept kind is skipped, exempt never ends over all inputs, one cancel per instance, tuning bounds, the
cleared-wait rule and its six controls). Not yet: a repro entry and a live run (an ON arm next to an OFF arm on the
Bridgeport save; pass = 4100 > 0 and no stall over 3 h + 10 min among non-exempt NPCs outside the kept kinds).
