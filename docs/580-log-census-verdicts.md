# 580: Log-census batch (docs/579-log-issues.md): root causes, verdicts and routecauselog

Follow-up to the log census in `docs/579-log-issues.md` (cleanup branch, `tools/harness/log-issues.ts`, 152 logs).
It takes each listed lead to a root cause or to the data still needed. Run data: `phase-20261115-1146` (all switches on)
and `-0523` (all off), `leak-*.json` snapshots. "[verified, decompiled]" marks EA code that was read; "[measured]"
marks run data.

| # | Lead | Verdict | Next |
|---|------|---------|------|
| 1 | Event listeners kCaughtCheatingOnPartner / kRelationshipLTRChanged / kSawSim grow | Mostly household wish nodes, held by their managers: not a proven leak | Census now gives wish triggers and wishes about gone Sims |
| 2 | Broadcasters on one lot 12 -> 35, queued socials 16 -> 41 | Phase content, not a leak | none |
| 3 | School work-day alarms 65 -> 110 | Not a bug: count follows the number of school-age townies | none |
| 5 | DisposeOutside / GetInPool / PlaceWelcomeKit / RouteAway fail with no plan nodes | Cause not visible in current logs | `routecauselog` |
| 6 | Household ToiletExpensive DestinationPortalLocked / NoPath, same Sim 5+ times | Privacy locks during another Sim's toilet or shower use; the owner is not logged yet | `routecauselog` |
| 7 | ServingContainer locked / no path | Same shape as row 6 | `routecauselog` |
| 8 | Socials started on a "sleeping" partner | "sleeping" is EA's `SynchronizationSleeping` flag, not a sleeping Sim. 0.0-min failures end on an exit reason that was already set | syncwait lines now print exit reasons |
| 9 | RouteAway -> Terrain fails uncut | Radial wander with no reachable spot; parameters not logged | `routecauselog` |
| 10 | UniversityDiploma piling up in inventories | Not a bug: at most one per townie | none |
| 11 | NPC horses in Late Night apartment units (coordinator lead) | Vanilla can produce it | `PetHomeCensus` |

## §1 Listener growth

[measured] In 1146, total listeners went from 7629 to 8455 (+826). Of these:
- +705 are `ActiveDreamNode.RunFeedbackFunction`, the wish nodes of the 6 household Sims;
- every wish node is still held by its owner's `DreamsAndPromisesManager` (orphans 0);
- no listener has a gone owner or target (0).

[verified, decompiled] Each `ActiveDreamNode` adds one listener for its trigger event
(`ResetListener`). Notifications has a single kCaughtCheatingOnPartner listener. So the per-event growth follows the
size of the wish trees, which grows with the number of relationships.

The leak census (`LcDreamSubject`) now also reports:
- `dream_listeners.by_trigger`: wish nodes per trigger event;
- `dream_listeners.subject_gone_by_trigger`: wishes about a Sim that has neither a SimDescription nor a
  MiniSimDescription.

A non-zero `subject_gone` count would make this a leak.

## §2 Broadcasters and queued socials

[measured] `max_on_one_lot` goes up and down (5, 26, 15 across 1146's snapshots) as party food and Sims come and go.
`gone_object` and `on_other_lot` stay 0. `queued_social` and `queued_social_gone_target` are 0 or 2.

## §3 School alarms

[measured] The School.* alarm counts per snapshot over 40 runs are 0, 60, 65, 70, 75, 80 and 85: always a multiple of
5, one alarm per school day for each child. The count is 0 when the census runs outside the alarms' window. It rises as
spawn phases add school-age townies. `gone_owner` is 0 in every snapshot.

[verified, decompiled] `Career.OnSetupWorkDayAlarms` removes the old handles before it adds new ones.
`Occupation.CleanUpAlarmHandles` runs on Cleanup and on leaving the job.

## §5, §9 NPC zero-plan failures

[verified, decompiled] The code paths behind each failure:

- **DisposeOutside** (`TrashPile.PutHeldTrashInCan`, forceOutdoor): picks the closest `TrashcanOutside` on the actor's
  lot and routes to it. An outdoor failure drops the bag and returns false. The interaction is pushed by
  `TrashcanIndoor` when the indoor can is full.
- **GetInPool** (`GetInPool.Run`): calls `PlanToSlot` with the pool's Inside routing-slot group. Result 10
  (NoDestinationSpecified) means that group is empty. Humans are refused at fountains, pets are not.
  [measured] The Sims seen were "Clubber" NPCs, in `ufloop` restarts.
- **PlaceWelcomeKit** (University mascot): runs `PutDown.DisallowCarryButSameRoom`. If that fails, the kit is
  destroyed and the intro is not marked complete.
- **RouteAway**: runs `Sim.Wander` with a radial destination limited to the Sim's room and the rooms next to it.

The native job line has no goal, so the remaining step is data. **`routecauselog`** (RouteCauseLog.cs, log only, off by
default, counters 2709 lines / 2710 dropped) logs these failures for NPCs with:
- goal, the Sim's spot and the interaction target;
- for pools: the inside slot count, the fountain flag and the species;
- for trash: the lot's outdoor cans (in use / tipped), the carried object and whether the Sim is outside;
- for RouteAway: its distances and outdoor limit.

Check: `RouteCauseLogCheck`.

## §6, §7 Locked doors on the household lot

[measured] Of the UseToilet failures, the DestinationPortalLocked rows are mostly near-goal Plan/Replan rows from
several rooms. The far rows are the probe (docs/578 §4).

[verified, decompiled] `UseToilet.Run` queues only on the toilet's own `Line`. A Sim who is showering or bathing in
the same room holds a `PrivacySituation` that locks the door, so the toilet looks free and the route fails at the door.

`routecauselog` logs every DestinationPortalLocked failure, household included, with:
- the locked doors (`SimRoutingComponent.LockedDoorsDuringPlan`, rooms);
- the privacy situations of the target room (type, owner, the owner's interaction).

That shows whether the lock is legitimate (someone inside) or stale (docs/577 §3). Autonomy choices are not changed
here: a household fix needs the stale case proven first.

## §8 Social sync failures on a "sleeping" partner

[verified, decompiled] `Sim.WaitForSynchronizationLevelWithSim` sets `SynchronizationSleeping` on the waiter. When the
partner is already at the level, the loop body never runs, and the final `HasExitReason(exitReasonInterrupt)` decides.
So "failed, 0.0 min, checks 0" lines (PillowFight, TakeCellPhonePhotoPaired, ZombifiedSocials) failed on an exit reason
the initiator already had. Failed `syncwait:` lines now end with `own exit <reasons>, partner exit <reasons>` (synctime
switch, SyncWaitLog.cs). The Test-side question waits for those reasons.

## §10 Diplomas

[verified, decompiled] Only two paths give diplomas to NPCs:
- `StoryProgression.AddAcademicsDegree`, whose `IsValid` requires `!HasCompletedAnyDegree`, so once per Sim;
- `TravelUtil.SpecialStoryProgressionAtUniversity`.

Each graduate gets one `UniversityDiploma` (`AcademicDegree.GiveUniversityDiploma`, which destroys it when the
inventory refuses it). The count grows with spawned townies.

## §11 NPC horses in Late Night apartment units

[verified, decompiled] How vanilla gets there:
- `LotManager.SelectRandomLotForNPCMoveIn` (MoveToLargerHouse, MoveToSmallerHouse, MoveInHousehold) skips only
  `IsApartmentLot` (the EP9 University subtype) and dorms. It has no horse or box-stall rule.
- MergeHouseholds and MoveOutHousehold keep horses with their household.
- `AddHorse` alone requires a box stall per horse.
- Stairs refuse horses, puppies, kittens and wild animals. `ElevatorDoors` refuses horses, toddlers and wild animals.

So a horse household can be moved into a tower unit that only stairs or an elevator reach.

[measured] In 1335, Ariel stood idle on its own unit lot for over 2000 sim-min. Our stuck watchdog had moved it (it was
then on a floor tile).

`PetHomeCensus("list")` (read-only) prints, for every pet:
- its home lot: box stalls, elevators, shells, front-door level;
- where the pet stands: lot, level, room;
- its GoHome plans, made as EA makes them.

A fix waits for that data.

The spawner's `!IsApartmentLot` rule does not exclude Late Night towers. It should prefer a lot with a box stall, else
exclude lots with `ElevatorDoors`, `BuildableShells` or `GetLevelOfOutsideDoor() > 0`.

## Tests

- `routecauselog` on (log only): `routecause:` lines; counters 2709 / 2710.
- synctime on: failed `syncwait:` lines end with `own exit ..., partner exit ...`.
- Leak census: `events.dream_listeners.by_trigger` and `subject_gone_by_trigger`.
- `Ts3Fixes.Fixes.PetHomeCensus list` in a world with pets, and in Bridgeport for the horse units.
- Checks: `RouteCauseLogCheck run` (off: BROKEN, on: FIXED), `PuddleReachCheck run` (docs/578 §3).

## §12 Health-census failure spam (phase-20261115-1335, 114 proven fixes on)

`sim-health-report.ts` counts an interaction as "spam" when it fails, is cancelled by the game, or ends "short" (head
for less than 1 sim-min) 3 or more times between two samples. The 1335 samples are 15 sim-hours apart, not 2. Almost all
of the rows are short ends. [measured] f/gc/short per window: Fashion Designer 0/0/6, Lily-Bo 1/4/3, Tiara 0/1/2, Joey
0/1/4, poor Bartender 0/0/3, Ariel 0/0/5 and 0/1/8, Ti Bellino 0/3/1. 1335 is the only run with a health census, so no
fixes-off comparison exists. Older logs have no lines for these interactions apart from GoToGarden UnseenFallback cuts.

Each interaction [verified, decompiled]:
- **Sim.SwitchToOutfit** (Ariel Horse, x12): Run always returns true and only spins. It is pushed only by
  `PushSwitchToOutfitInteraction` (21 EA callers: parties' hosts and guests, careers, school, pools, socials). It is
  short by design. Which caller targets the horse is not logged. Harmless: a missing category falls back to Everyday.
  The real problem is the horse stuck on its unit (§11).
- **LunacyFreakOut** (x3 each): Run calls `PlayReaction(FreakOut, NowOrLater)` and returns at once, so it is always
  short. It fails only on an exit reason that was already set. `lunacysocials` gives every Lunatic Sim EA's own
  30-180 min random-social alarm, where vanilla gave only the last one: 3 in 15 h is that rate. Not a regression.
- **ReactionInteraction** (x3): the reaction animation queued by the freak-outs; short by design.
- **GoToGarden** (poor Bartender, x6): on the home lot, Run hands off to `MakeSimDoSomethingAfterArrivingAtVenue` and
  returns. Off the lot, our trip shortcut (default on) places unseen townies, so the interaction is short.
- **GoHome** (Tiara, Joey, x3): the `gohome` shortcut (default on) places unseen townies at home, so GoHome ends at
  once. The 2 game-cancels and Tiara's VisitLot result-10 plans are EA.
- **BarAdvanced.IdleTend** (Ti Bellino, gc 3): the bartender's idle loop is cancelled when an order comes in. By design.

Verdict: none is a failure. The census should not count by-design short interactions: SwitchToOutfit, LunacyFreakOut,
ReactionInteraction, meta-autonomy hand-offs (GoToGarden), shortcut arrivals (GoHome), and the idle tend loop.

## §13 Live census 1506 (117 switches on)

The data here is `health.jsonl` plus the live log. 1335 and 1506 are the only runs with a health census, both with
fixes on, so there is no fixes-off arm.

1. **Restaurant trips "ended by our shortcuts"** (AutonomouslyGoForMealInRestaurant 134 ends over 59 Sims, GoHome 94,
   VisitCommunityLot 60):
   - [verified, ts3agent SimHealthHandlers] `ours` means a ts3fixes move happened while that head ran. It does not mean
     we ended it.
   - [verified, Fixes.cs RabbitHoleHook] The `rabbithole` shortcut places the Sim by the entrance, then runs EA's
     `RouteNearEntranceAndEnterRabbitHole`. On success EA's Run sets Hunger to max.
   - [measured] The top Sims ate out 4-5 times in 88.6 sim-hours (about 1.3 a day), which is not a loop. Hunger itself
     is not in the census.
2. **Kirby Wise x Sim.Scare** [measured, syncwait lines; verified, BaseScare.Run]:
   - Scare queues `BeScared` with AddNext. The target keeps running WorkOut, BedSleep, Sleep, BedNap or Watch, with the
     BeScared still queued, so Kirby's Routed or Committed wait runs to 40 min or fails when the target's state changes.
     The receiver-side 0.0-min failures follow from the initiator's failure (both exits SynchronizationFailed).
   - The cause is EA's Test: it rejects a sleeping target, but not one about to sleep or busy in a long activity.
   - Not changed: both Sims are household, and an early end would differ from vanilla when the target finishes within
     40 min. `npcbusypartner` covers NPC pairs (Started waits only).
3. **Long trips**:
   - Maggie: the visit head sat 139 min at home before the car trip, then about 150 min driving (SittingInVehicle,
     WorldRoad). `unseenfb` placed her at the route's goal (home, after the visit had turned into Go Home).
   - Rod: about 150 min walking a 148 m trip on the road.
   - Bronson's 292-min "Go Home" ended at home. The rows on "Lot ulong.Max" are Sims on the street or road, in transit.
4. **Idle for hours with an empty queue** [verified, Autonomy.ShouldRunLocalAutonomy]:
   - Off-screen NPCs (low LOD: not on the active lot, not the active household, not on screen) run no local autonomy,
     only meta autonomy (trips). Standing idle on their home lot is EA's LOD design.
   - Alexander French and Brigit Hemlock are tending bars (`BarAdvanced.IdleTend`, TendingPosture, hidden name), which
     also disables their autonomy (`BarAdvanced` IncrementAutonomyDisabled).
   - Farrah is in GoToVirtualHome.

## §14 Root of the Scare waits (`scarepriority`, built, off by default)

How the wait happens [verified, decompiled]:
1. `Sim.BaseScare.Run` (shared by `Scare` and `StaticShock`) creates `BeScared` / `BeShocked` with **its own
   `GetPriority()`** and calls `Target.InteractionQueue.AddNext`.
2. `AddInteraction` with ForceNext inserts after a running (locked) head, so the receiver lands at index 1.
3. `RemoveLowerPriorityInteractions` cancels the target's head only when the newcomer's priority is strictly higher.
   For an autonomous Scare both are `Autonomous`, so BeScared waits behind WorkOut, BedSleep, Watch or ReadBook.
4. The scarer routes, then waits in `WaitForSynchronizationLevelWithSim(Target, Routed)` and then `Committed`, each up to
   `BaseScare.kMaxSyncTime` = 40 sim-min (tunable).

EA's own rule for receivers: `SocialInteraction.CreateSocialB` raises an `Autonomous` priority to
`NonCriticalNPCBehavior` before pushing the B side. That is why ordinary socials interrupt an autonomous activity and
Scare does not. BaseScare predates or skips that rule: an EA inconsistency.

[measured, 153 run logs plus the live log] Failed or timed-out `syncwait` with the receiver "linked queued":

| Initiator | Waits | Sim-min | Household initiator | Note |
|---|---|---|---|---|
| Scare | 115 | 3423 | 115 | partner busy in WorkOut 42, BedSleep 13, Watch 11, ReadBook 8, DanceSolo 8 |
| DanceTogetherA | 93 | 4027 | 6 | social path, already raised; partner busy elsewhere (docs/577 §4) |
| SocialInteractionA | 54 | 1791 | 24 | social path, already raised |

Other `Run`s that push a receiver with the raw `GetPriority()` and then wait (scan of decompiled Run bodies):
- BeShocked (StaticShock);
- SimBeLuredToBag;
- ListenToSerenade;
- MakeMoveSimB;
- BeSatOnByPet;
- BeUntied;
- GoMakeWish;
- the B sides of the bed, seated and sauna cuddles, StartDreamWith and three WooHoo variants.

None of these besides Scare shows up in the logs as failed waits.

Proposed root fix (`scarepriority`, off by default):
- When Scare or StaticShock reaches `SetupJazzGraph` (after its route, before the 40-min waits) and its linked receiver
  is still queued with `Autonomous` priority, raise the receiver to `NonCriticalNPCBehavior` (`SetPriority`). EA's
  `InteractionPriorityChangedCallback` then runs `RemoveLowerPriorityInteractions`, which cancels only an autonomous
  head.
- This copies EA's own CreateSocialB rule.
- Player clicks are unchanged:
  - a player-directed Scare already has UserDirected priority, which beats any autonomous head;
  - a target busy with something the player ordered (UserDirected) still outranks the raised receiver.
- Only an autonomous prank on an autonomously busy Sim changes: it now interrupts the activity, as every autonomous
  social does.
- Optionally apply the same rule to StaticShock and SimBeLuredToBag.

**Built (2026-11-15):** `scarepriority` (`ScarePriority.cs`, tuning `kScarePriority`, off; counters 3541 raised, 1107 seen
with the switch off, 1108 errors). It hooks `SetupJazzGraph` of `Sim+Scare`, `Sim+StaticShock` and
`Sim+ScareWithMegaPhone` (all three run `BaseScare.Run`) and, after EA's body, raises the linked receiver to
`NonCriticalNPCBehavior` when it is still in the target's queue, not its head, and `Autonomous`. Check
`ScarePriorityCheck` (repro `scarepriority-check`), controls: a player-ordered head and a MustRun head are kept, an
already raised or player-directed receiver is unchanged, a receiver no longer queued or already the head is unchanged,
and an unraised receiver keeps EA's wait. Not tested in game yet. Counter 3542 in an off run gives the number of scares
the switch would change.

## §15 `outfitwaitpoll` household rule (2026-11-15)

`outfitwaitpoll` ships on. Before this change, any wait not ready after 60 sim-minutes was read once per sim-minute,
household Sims' waits included, so a household Sim's late-ready outfit change could resume up to a sim-minute later
than in EA's game. That broke the household rule (CLAUDE.md).

Change (`OutfitWait.cs`, `OutfitWaitCore.PollAllowed`): at each wake past the 60-minute mark, a scope check runs before
the minute delay. A live Sim that `IsPlayerSim` calls the player's (household, selected, selectable, or unknown scope)
keeps EA's per-tick loop. An NPC's wait is polled as before. A gone Sim's wait is also polled as before, so the issue #40
dead-wait end still runs. The check is read at every wake, so a Sim that joins the household mid-wait goes back to EA's
loop. Counter [3544] counts waits kept on EA's loop by this rule, once per wait. A household Sim's stale wait is no longer
marked or logged as stale ([680-682], [2003-2013] now count NPC and gone-Sim waits only).

Checks: `OutfitWaitCheck("scope")` (pure): a household Sim past the mark sleeps 1 tick. Controls: an NPC and a gone Sim
sleep a sim-minute with the switch on, and nothing changes before the mark. Self-test `tools/selftest/outfitwait` has
`PollAllowed` cases. Not run in game.


## §16 Growth leads from phase-20261115-1506 (leak-0 / leak-4 / leak-9, alarms-0..9)

1. **Duplicate `Theatre.ResetSandwichBoards (MovieCineplex)`: EA bug, fix `sandwichalarm` built (off).** The count went
   0, 2, 4, ... 17 over the ten snapshots. [verified, decompiled] `Theatre.ResetSandwichBoardFromPlayerConcert` (the
   one-shot that `EndPlayerConcert` / `EndPlayerConvention` add) re-adds the daily alarm `mEveningShowSandwichResetBoardAlarm`
   and overwrites the handle without removing the one it holds. If a second show starts before the first show's one-shot
   fires, the first one-shot re-adds the alarm after the start removed it, and the handle is overwritten again. The
   orphan repeats daily and is saved (`AlwaysPersisted`). It only resets the board graphic. Fix: `SandwichAlarm.cs`
   removes the held handle before EA's body (counters 2800 removed, 1111 seen with the switch off, 1112 errors; check
   `SandwichAlarmCheck`). Saved duplicates: at world load (switch on) each theatre keeps the `ResetSandwichBoards`
   alarm whose handle it holds (or, if that handle is stale, the first one, which becomes the held one) and removes its other
   `ResetSandwichBoards` alarms; other callbacks and other theatres are untouched (counter 2803). Not in the hourly sweep:
   with the hook on no new duplicates appear.
2. **Alarm growth (global 1984 -> 2440, total 2876 -> 3387): Sims added by the test, no orphans.** `gone_owner` = 0 and
   `gone_callback_target` = 0 in every snapshot. By owner type: SimDescription 992 -> 1269, Sim 391 -> 496, null 116 -> 151,
   InsectJig +41, Bartender +34, Wildflower +29. By callback: career / school workday alarms and CelebrityManager
   (89 -> 109) grow with the Sims the run spawned (33 humans with occults and 8 pets). "(other N)" is a bucket of N types,
   not a count (712 -> 777). Nothing repeats forever except the sandwich-board duplicates.
3. **Household dream listeners 6389 -> 7365 -> 7535 (leak-0, -4, -9): wish churn that levels off, not a leak.** Every
   listener is held by a live household node; `subject_gone` and orphans are 0. The growth is relationship triggers
   (kRelationshipLTRChanged 384 -> 701, kSocialInteraction 535 -> 703, new kCaughtCheatingWithSim / OnPartner 134 each),
   which grow with the Sims the household meets. Report only; household wishes are not changed.
4. **Syncwait timeouts (18):** Scare 7 (partner in WorkOut, MopPuddle, Watch, ReadBook, GetInCarpool: the §14 cause;
   `scarepriority` was not on), DanceTogetherA receiver 4 (Completed, docs/577 §4), plain social pairs 3, seated social 2,
   ZombifiedSocials 1, GiveFlowers 1.

## §17 Dream listeners: why ~1000 per household Sim (2026-11-15, RE, report only)

Lifecycle [verified, decompiled `DreamsAndPromisesManager` / `ActiveDreamNode`]:
- `mActiveNodes` is the whole active frontier of the Sim's wish trees, not only the wishes on screen. Each active node holds
  one `EventTracker` listener for its trigger (`ResetListener` removes the old listener before adding one, so a node never
  has two). `mDreamNodes` are the offered wishes; `mDisplayedDreamNodes` the top 3 in the panel; `mPromiseNodes` the 5 promise slots.
- **A wish leaving the panel does not remove its node.** In `Update`, a wish whose `TimeActive` passes `DisplayTime` is only
  taken out of `mDreamNodes` / `mDisplayedDreamNodes`. The node stays active, listening, until `TimeActive > SatisfyTime`
  (`UpdateStatus.TimedOut` -> `RemoveActiveNode`, which removes the listener). Root nodes, promised nodes and nodes with
  `SatisfyTime == 0` never time out. That is EA's design: hidden nodes complete silently and score their children.
- Completion: `RemoveActiveNode` (listener removed), or for a repeatable node either `Reset(true)` in place
  (`TimeBetweenRepeats <= 0`) or a move to `mSleepingNodes` (listener removed) until it wakes.
- Dismissal (`CancelDream`) only takes the node off the offered lists. Promise breaking and trait / age changes go through
  the same `RemoveActiveNode` or rebuild the manager (`ResetDnP`, `NullDnPManager` clears every listener).
- Subject-specific nodes (wishes about a Sim or object) are made per subject. The count grows with the Sims the household
  meets, and they are removed when the subject goes (`mPotentialRemovalNodes`). That matches the census: growth in
  relationship and social triggers, levelling off, and subject_gone 0.

EA defects found (household data: report only, no change):
1. `AddActiveNode` files Pet and SimOrPet subjects in `mPotentialRemovalNodes`; `RemoveActiveNode` removes only Sim,
   OutOfWorldSim and Object ones. Pet-subject entries stay after their node is gone: a node reference leak with no
   listener attached.
2. A repeatable node woken from `mSleepingNodes` goes back into `mActiveNodes` without `AddActiveNode`, so it is not
   re-filed in `mPotentialRemovalNodes`. If its subject later leaves the world, the node is not removed with the rest
   [inferred: rare; subject_gone was 0 in 1506].
No path re-adds a duplicate node for the same primitive and subject (`IsNodeAlreadyActive` guards the scoring paths), but
the census below counts that too.

Pets: `Sim.SetupSelectableSimManagers` gives a manager to any selectable `ChildOrAbove` Sim and does not test species.
1506 had no household pets, so whether household pets get wish trees was not measured.

Census (log-only, ts3agent `leak_census`): `dream_listeners.nodes_by_manager`, per manager: active, displayed, shown,
promised, hidden never_times_out / timed / past_display, dup, no_listener, sleeping, stale_removal (defect 1). Not run yet.

## §18 `dreamcleanup` (built, off by default; memory only, household managers included, approved)

Fixes the two §17 defects in the wish manager's clean-up index (`mPotentialRemovalNodes`), nothing else:
1. After EA's `RemoveActiveNode(node, true)`, a Pet or SimOrPet subject node is taken out of the index under the key
   `AddActiveNode` filed it with (the SimDescriptionId). New hook `DcHook.RemoveHook` (`DreamCleanup.cs`).
2. After EA's full `Update`, a node that was in `mSleepingNodes` before and is active now is filed as `AddActiveNode`
   would file it, unless it is already there. Shares `WlMgr.UpdateHook` (`WishListenerFixes.cs`, one patch per method):
   the sleeping list is copied before EA's body only when it is not empty.

1b. Same hook: an Object-subject node whose object was destroyed first (`Subject.mObject` null, so EA has no key) is found
   in the index and taken out. With 1 and 1b, the `dnppetkeys` / `dnpobjkeys` hourly sweeps (`DnpRemovalKeys.cs`) are
   needed only for entries already in a save.

Both parts act where the stale entry is made (root cause), not in a later sweep. No listener, timer, panel list, promise or score is touched, so which wishes appear, and when, stays EA's. Counters 2804
unfiled, 1115 woken nodes filed, 1116 either case seen with the switch off, 1117 errors. Check `DreamCleanupCheck` (pure,
on a model manager). Controls: a Sim-subject node is left to EA, a promised node is not filed, no double filing, a node
with no subject key or still asleep is not filed, and the model's listener count, panel list and promise slot are
identical before and after. Not run in game. The §17 census (`stale_removal`) shows whether it matters in a save.

### §17 addendum: full audit of EA's wish code (2026-11-15, decompiled; ranked, root-cause fixes first)

Rule (user): fix where the bad state is created; sweeps only for state already in a save.

| # | Finding [verified, decompiled] | Cost | Same wishes for the player? | Status / proposal |
|---|---|---|---|---|
| 1 | `ObjectDestroyedDelegate` on `kEventObjectDisposed` (what `OnLoadFixup` registers, so live in vanilla after any load): when a removed node was a displayed promise it reads `dnPSubjectDestroyedEvent.SimDescription`, but that event is a plain object-disposed event (`dnPSubjectDestroyedEvent` null): NullReferenceException. The rest of that subject's nodes are not removed, the index entry stays, and the UI never gets `FireBreakPromise`. | per destroyed object that is the subject of a promised wish | No: the fix shows the promise failing (EA's intent) where vanilla throws. EA bug, household-visible | not built; needs approval (one null guard) |
| 2 | `RemoveActiveNode` does not unfile Pet / SimOrPet subjects, and an Object subject whose object is gone (key from `Subject.mObject`, now null) | node refs held per pet / object wish | yes (index only) | `dreamcleanup` 1 / 1b, at the creation point; `dnppetkeys` / `dnpobjkeys` sweeps now only for saved state |
| 3 | `Update` wakes sleeping repeatables without `AddActiveNode`, so they are not re-filed | rare missed subject clean-up | yes | `dreamcleanup` 2, at the creation point |
| 4 | The constructor registers both destroyed-subject listeners on `kDnPSubjectDestroyed` (one should be `kEventObjectDisposed`): the handler runs twice per Sim destruction, and object destruction is missed until a reload | 2 calls per event per manager | yes (as after a reload) | `dnpdisposelistener` exists (at creation), default off: recommend testing it |
| 5 | A dead Sim's object-disposed path keys the lookup by the `SimDescription` object, but entries are filed by id: it never matches (the `DnPSubjectDestroyedEvent` path covers it) | one wasted lookup | yes | not worth a hook |
| 6 | Listener cost: wish listeners are keyed by owner, so an event runs only its Sim's listeners for that event id (about 100 for kSocialInteraction per household Sim), not O(n²). `IsSatisfiableByThirdParty` nodes are keyed 0 and run for every Sim's event | unknown | n/a | census `dream_listeners.global_by_event` added; measure before any change |
| 7 | `mActiveNodes` is a List (about 1000): `Remove` and `Contains` are linear, per timeout or completion | small | yes (an index gives the same order) | low priority; measure with ScriptHotSpots first |
| 8 | `SetupForNewSim` activates the root of every tree for every Sim (age, species and occult checks happen later in each node's check function). Roots never time out, so this is most of the ~1000 listeners | memory, plus feedback calls on matching events | No guarantee: the manager is not rebuilt on most age-ups, so pruning by age could drop wishes later | do not build |
| 9 | Save / load: `SetupFromExport` rebuilds from export data only for travel and passport; a normal load keeps the saved nodes and re-registers listeners in `OnLoadFixup` | none | n/a | nothing to fix |

Re-check against the rule: `sandwichalarm` acts in `ResetSandwichBoardFromPlayerConcert`, the call that makes the orphan
(root); its world-load pass only removes duplicates already saved. `dreamcleanup` acts in `RemoveActiveNode` and right
after the `Update` wake, where the stale entries are made (root); nothing in it is a sweep.
