# 589: Usage sweep (the code Sims use most, read for bugs)

Date: 2026-11-16. Static analysis only; nothing was built or run.

The user asked for this sweep: "a lot of game areas are untouched, especially the ones Sims use most". The earlier sweeps (docs/639, docs/588) ranked types by scanner hits. This one ranks them by how much our own runs actually use them, and then hand-reads the most-used code for bugs.

## 1. Usage ranking

`tools/static/usage-rank.py` builds the ranking, reading our run data read-only from the `~/dev/ts3-runs/phase-*` run directories:

- **health.jsonl** holds per-Sim snapshots of the interaction at the head of each Sim's queue. Each gap between snapshots (the `since_min` delta) is credited to the head interaction's definition, which gives sim-minutes per type. A change of head interaction between two snapshots counts as one start. In total, 137,539 sim-minutes were observed.
- **aut\*.json** holds the autonomy census rows (`Definition @ ObjectClass`), which give test calls and milliseconds.
- **docs/data/jit-hot.csv** gives samples for the managed methods that run hottest.

Each definition is mapped to its outer class, and each object to its class; the class is then found in the decompiled tree. The output is `docs/data/usage-rank-1115.csv`, with columns type, uses per sim-hour, sim-minutes, starts, autonomy calls and milliseconds, JIT samples, score, and hand-read. It covers 378 EA types.

**What "hand-read" means here.** That column is copied from `function-coverage-1115.csv`, where it means "named in some doc". Most of the top types were previously named only in performance docs, and nobody had read them for bugs. So this sweep bug-read the top used types whatever that column says.

- Rerun: `python3 -I tools/static/usage-rank.py <ts3-runs> <decompiled> docs/data/function-coverage-1115.csv docs/data/usage-rank-1115.csv <jit-hot.csv>`
- Thin "Mimics" subclasses (chairs, tables, beds and others) have no logic of their own, so their base class was read instead.

**Who read what.** I read ViewObjects, AthleticGameObject, AutonomouslyGoForMealInRestaurant, FollowerMeetUpAtPoint and TableDining. Two helper agents read the rest of the top 100 function by function:

- **Everyday objects:** Bed and BedSleep, Shower, Toilet, Bathtub, Sink, the Fridge interactions, Stove, Food and Snack, EatHeldFood, ServingContainer, Counter, Dishwasher, TrashcanIndoor, TV, Stereo, Computer, Phone and PhoneCell, Mirror, Bookshelf and the ReadBook family, ChairDining, ChairLounge, Sofa, SitAndWait, the instruments, and ChessTable.
- **Sim core and social:**
  - motives and mood: Motive, Motives, BuffManager, MoodManager;
  - interactions and posture: InteractionQueue, ReactionInteraction, RemainStanding, Posture, SittingPosture, CarrySystem, IdleManager;
  - socials: SocialInteractionA and B, Conversation, SocialComponent, Relationship and the long-term relationship (LTR) code;
  - child care: the 11 child interactions;
  - work and travel: WorkInRabbitHole, CarpoolManager, VisitCommunityLot, VisitLot, GoHome, and the Autonomy scoring functions.

I spot-checked the top findings from both agents in the code. Each finding was checked against the ts3fixes sources in every s3m-* worktree and against `proven-fixes.txt`. It was also checked against simler90 UPDATE242, by decompiling its copy and EA's with the same tool and diffing. No finding below is fixed by either.

**Household.** These paths are shared by played Sims and NPCs, so most fixes reach the household. That is as the user wants: these are EA bug fixes that restore what EA intended.

**Marks:** verified = proven by reading the path; inferred = the effect follows but was not measured.

## 2. Bugs, ranked by use times impact

All of these are visible to the household. Line numbers are approximate. Paths are under `game-copy/decompiled/Sims3GameplayObjects` (objects) or `Sims3GameplaySystems` (Sim core).

| # | Where | Failure | Mark | Fix direction |
|---|---|---|---|---|
| U1 | `EatHeldFood.Run` (~590) and `Cleanup` (~341) | `Food.PostEat` runs at the end of Run and again, unguarded, in Cleanup. This doubles the kEatIEdible event (which counts toward wishes and opportunities) and the Stuffed and vampire-Nauseous buffs, and rolls food-truck GreasyFood and FoodPoisoning twice. A queued eat that is cancelled before it runs still posts an eat. | verified | Add a pending flag, set after PreEat and cleared when Run posts. Cleanup posts only while the flag is still set. A core-text patch was planned in `ilpatch-convert.csv` (99_o1) but never built. |
| U2 | `Motive.UpdateMotiveWithDecay` (1046, 1183-1197) | Inside the delta loop, the Carefree (x1.25), LaidbackLemon and EfficientChip multipliers are applied to the whole running total once per delta, on top of the one applied before the loop. Fun gain comes out at x1.56 or more. | verified | Sum the deltas first and apply each multiplier once after the loop. |
| U3 | `SocialInteractionA` fight: `SocialCallback` 865/925 | The fight's ReactionBroadcaster is released only in AfterFight. When the fight is cancelled, fails, the target leaves, or a Sim is reset, it never ends (TimeDuration -1). Nearby Sims keep reacting, and sleepers are woken. | verified | Dispose and null `SocialBroadcaster` in `SocialInteractionA.Cleanup`. |
| U4 | `ReactionInteraction.OneShotReaction` (183) | `ref reaction` overwrites the shared parameter, so one Sim's trait reaction (Coward, Evil, PointLaugh) carries over to every later Sim in the loop. | verified | Use a local copy per Sim. |
| U5 | `CarpoolManager.DoCarpoolPickupAndDropoff.Run` (161) | `mRider.Occupation.GetInteractionToTakeSimToWork()` throws a NullReferenceException for a school child, who has a School but no Occupation, when that child is riding a horse or on a lot that skips the carpool. The bus then offers GetIn to nobody. The prom limo takes the same path. | verified | For the school bus, use `School.CreateWorkInteractionInstance()` with a null guard, as lines 210-216 already do. Guard `Sim.ActiveActor`. |
| U6 | `TV.OnChannelChangeResetAlarms` (4115) and `SetSyncEventAlarm` (4238) | RemoveAlarm never resets the handle, and the alarm only re-arms when the handle is invalid. After the first channel change or turn-off, sync-event viewer reactions are gone for good. The stale handle is saved, so a reload does not fix it. MovieScreen.cs:1920 has the same pattern. | verified | One helper that removes the alarm and resets both handles. |
| U7 | `PhoneCell.TakePhoto.Run` (449) | When funds are short, it calls `ModifyFunds(cost - shortfall)`, which adds FamilyFunds instead of taking them. Funds double, and the shortfall still goes onto the bills. | verified | `ModifyFunds(-FamilyFunds)`. |
| U8 | `LongTermRelationship.ForceChangeState` (636/649) | The mask keeps only HasMet and Kissed, which wipes the HaveBeenFriends, HaveBeenBestFriends and HaveLostFriendship history bits. NewFriend fires again, the Charisma friend counts climb again, and LostAFriend can repeat. There are about 36 callers. | verified (the LTR XML is unchecked) | Keep those three bits. |
| U9 | `SocialComponent.GetGoodbyeSocial` (2034) | Just building the pie menu removes SoCold from both Sims and despawns a Haunted ghost. | verified | Make it a pure name lookup; OnHug already does the removal. |
| U10 | `Bathtub.TakeBath.Cleanup` | Cleanup runs unconditionally, so a second Sim's bath that times out in WaitForTurn or is cancelled while queued removes the current bather's DuckTimeFun alarm and push-away footprint. | verified | Guard it with a started flag. Also null mSituation after Exit (it is exited twice on the route-fail path), and dispose the old bubble FX. |
| U11 | `TV.WatchTV.Cleanup` (276-288) | A watch that never ran still stops Cooking, Gardening or Fishing skill gain from the last channel. | verified | `if (mSimsWatchingTv.Remove(Actor)) StopSkillGainForSim(Actor)`. |
| U12 | `Computer.PlayComputerGames.Run` (716-739) | An autonomous run writes its game type into the shared Singleton definition. Every later autonomous game is the same one, and a HoloComputer can get a normal computer's game. | verified | Keep the game type in an instance field. |
| U13 | `MoodManager.UpdateMood` (593-606) | `mPreviousMotiveToTank` is never reset when mood recovers, so the anger tank is skipped after the first time. The field is saved, so this survives a reload. | verified | Reset it whenever mood is not negative. |
| U14 | `ReactionInteraction.SetReactionType` (242) | A Grumpy Sim with a non-positive reaction never sets mReactionType, so it stays 0 (Angry). | verified | Set the type in both branches. |
| U15 | `AthleticGameObject.TrainSim.Run` | When the trainee leaves between Test and Run, `Trainee` is null and `Trainee.LotCurrent` in the wait loop throws at once. ts3fixes `trainboost` fixes the skill-gain leak on reset, but not this. | verified | `Trainee == null`: exit Finished. |
| U16 | `Stereo.UpgradeChangedCallback` (4074) and `DanceTogetherA.Cleanup` (683) | A speaker-upgrade change rebuilds the broadcaster on a stereo that is off or broken, which gives music buffs and wakes sleepers. Ending any dancing pair dismisses every dancer at that speaker, even for a pair cancelled while queued. | verified | Rebuild only if on and not broken. Drop the DismissDancers call. |
| U17 | `BuffManager.RemoveElement(ulong,bool)` (1245-1260) | When a delayed timer exists, it removes the timer and returns, leaving the live buff in place. | verified (frequency inferred) | Fall through to base.RemoveElement. |
| U18 | `SocialInteractionA.UpdateConversationWhenSocialStarts` (1986) and Run (1228/1250/1297) | A social with no matching rule leaves mTargetEffect null, and Run throws a NullReferenceException. Modded socials are the likely trigger. | verified | Exit when it is null. |
| U19 | `TV.SetHomeMovieDataDisc` and `SetDataDiscDisposedListener` (3216/3253) | Each disc change adds a listener and never removes the old one, so disposing an old disc clears the current movie and turns the TV off. | verified | Remove the old listener first. |
| U20 | Instruments | `JamWithInstrument.Cleanup` (BandInstrument 1312): base.Cleanup only runs when JamController is null, so a Sim joining a jam already in progress skips use-list, line and commodity cleanup. `WatchBase.Run`: `ActorsUsingMe[0]` has no count check. `Guitar.Play`: the gnome boost is added once per gnome but removed once. | verified | Always call base.Cleanup. Check the count. Remove count × boost in Cleanup. |
| U21 | `Fridge_OpenFridge.Definition.Test` | Dereferences `InventoryComp.InventoryUIModel` before the null check, which throws on a community-lot fridge. | verified | Reorder the null check. |
| U22 | `Posture.CancelPosture` (359) | Compares a definition with an instance, so the guard is always false and duplicate stand-ups are queued. | verified | Compare definitions. |
| U23 | IdleManager | `ScheduleCurrentDistressIdles` (1544): a `!testOnly` gate hides higher-priority buff distress. `GetFacialMoodIdles` (864/873): integer division gives weight 0 with 7 or more idles. `ParseMoodIdleData` never updates moodFlavor (the XML is unchecked). | verified | Move the gate, use float division, update the flavour. |
| U24 | `ViewObjects.Run` | EndCommodityUpdates runs before the 5-25 sim-minute WaitAndGroupTalk, so the viewing time gives no Fun. | inferred (may be intended) | Move EndCommodityUpdates after the wait. |

**Lower impact or inferred.** All of these were read; none is fixed by ts3fixes or simler90.

**Objects:**
- **Sink:** a reset leaks the Disgusted broadcaster.
- **Dishwasher:** FX are nulled without Dispose, a native leak.
- **Fridge_Have:** the result of ChooseRecipeRandomly is not null-checked.
- **Bookshelf:** the commercial visual state flips between Base and Full.
- **BedSleep:** the spoon wake reason is overwritten each loop; use `|=`.
- **Toilet and Bathtub:** `No*OnTheLot` has an inverted null guard.
- **Shower:** footprint cleanup runs for a Sim that never showered, and the wait-to-leave loop has no exit.
- **Toilet:** `mTimeoutPaused` is not restored.
- **Food.RouteToFreeSurface:** gives up on a busy surface instead of trying the next.
- **Serenade:** cleanup does not stop the broadcaster.
- **PlayForTips:** cleanup clears shared state even when it never ran.
- **ForumPosting.TryGetPosting:** returns the first post.
- **Stove:** iterates a null ingredient stack.

**Sim core:**
- **Social menu:** stale greyed-out tooltips in AddSocials.
- **Conversation:** `mJoinCnt` is saved and can stay stuck, so outsiders wait 30 minutes and are then cancelled.
- **BuffSourcesList.TimeoutSources:** can return float.MinValue as a timeout.
- **Mermaid rain check:** can dereference a null Posture.
- **VisitCommunityLot:** can dereference a null resort-tower instantiation.
- **CarpoolManager:**
  - School delegates are never re-bound when the child ages up;
  - the car leaks when the driver is null;
  - `RiderDelegate` has no null check.
- **GetSelfPickedUp:** a sorted-list key collision, plus relationships created with every teen or older Sim on the lot.
- **SittingPosture:** `CanGroupChat` can dereference null.
- **InteractionQueue:** baby and toddler insert can dereference null.
- **CarrySystem.PickUp:** early returns skip the look-at restore.
- **Conversation.RemoveSim:** always passes the turn to member 0.
- **FindSimsOfRelationshipType:** returns one Sim more than the max.
- **CalculateAttractionScore:** tests Mummy twice.

## 3. Proven-exact speedups (sent to the perf agent)

**Allocation-free rewrites:**
- `Food.LotHasUsableCounter`/Stove/Microwave/Grill and `Sink.GetBrushTeethScore` do `GetObjects<T>(pred).Count > 0`, which allocates a list. An early-exit loop gives the same answer; Fridge_Have runs these four times per AddInteractions.
- `TV.CountOfHumansWatching` builds two lists only to count; count instead.
- `HasAnyElement(params)` allocates an array on every call (Motive 1107 every tick, MoodManager 342/348, GoHome.Test). Use static readonly arrays instead.

**Fewer lookups:**
- `PlayVideoGameSystemOnTVOnline.Test`: run GetClosestObject only in the branch that uses it, do the Nerd check first, and use an any-match for the partner list.
- `Autonomy.CalculateScoreForSocial`, the Feeling* buff checks: one GetElement replaces HasElement plus GetElement.
- `BuffManager.UpdateBuffs`: skip the copy when mDelayedBuffs is empty, and use TryGetValue.
- `SocialComponent.SocializeWithMultiplier`: filter in a loop rather than building `Household.Sims` per candidate.
- `FindSimOfRelationshipType`: RemoveAll instead of restarting the loop. The order is kept, so the random pick is the same.
- `Relationship.GetMiniRelationships`: drop a redundant Contains.

**Caching:**
- **Conversation:**
  - `GetSocialsForSTC`: hoist `FirstSTC`, drop the second list copy, and skip the lookup when the flag is already set.
  - `IsSuitable`: pass ActionData in rather than calling Get four times per rule.
  - Cache the procedural-effect `Delegate.CreateDelegate` per MethodInfo.
- `IdleManager.TestIdleInfo`: allocate `object[]` only when ProceduralTest is set.
- `InteractionQueue.InsertInteraction`: cache the delegate, and create the list lazily.
- `CarpoolManager.LoopUpdate`: return early when there are no passengers.

## 4. Read with no bug

**Everyday objects:**
- the ReadBook family, Snack, Mirror, most of the Fridge interactions;
- Bed reading, Shower WooHoo, SitAndWait, Sofa, ChairLounge, ChairDining;
- Counter, TrashcanIndoor, ServingContainer, ChessTable, Drums, BassGuitar, Phone;
- TableDining, AutonomouslyGoForMealInRestaurant, FollowerMeetUpAtPoint.

**Sim core:**
- the 10 child-care interactions (TimedCry, TossInAir, Snuggle, ChangeDiaper, PlayPeekAboo, FeedOnFloor, FeedToddlerInHighChair, PutChildInCrib, TeachToTalk, TeachToWalk);
- VisitLot, GoHome and CarpoolWithDriver;
- the Autonomy scoring functions;
- RemainStanding, SocialInteractionB, and the rest of Relationship and LongTermRelationship.

## 5. Next

- Live check for U1: count kEatIEdible events per meal.
- Live check for U2: log the Fun delta of a Carefree Sim.
- Live check for U6: count sync-event reactions before and after a channel change.
- U1, U2, U7 and U8 change results for the household. They are EA bug fixes, so they need the harness off/on check before shipping.

## 6. Batch 11 (built 2026-11-16, all default off, NOT RUN)

The user: "fix all of these", as EA intended, household included. Every lead below reaches the household. Each switch has a pure harness check (`Ts3Fixes.Fixes.<Check>` run / result, a repro in `tools/harness/repros.json`); all are in `tools/harness/sets/proven-fixes.txt` line 130 for the off/on run. Every hooked method was diffed against simler90 UPDATE242 (decompiled with the same tool): none of the hooked bodies differ.

| Lead | Switch | Check | Slots | File | What the fix does |
|---|---|---|---|---|---|
| U1 | `eatpostonce` | `EatPostOnceCheck` | 1351-1353 | Batch589Logic.cs | a finished eat is not posted a second time by EatHeldFood.Cleanup (kEatIEdible, Stuffed, food-truck rolls once) |
| U2 | `motivemultonce` | `MotiveMultOnceCheck` | 1354-1356 | Batch589Logic.cs | the Carefree / LaidbackLemon / EfficientChip multiplier is applied once per motive update, not per matching delta |
| U3 | `fightbroadcastend` | `FightBroadcastEndCheck` | 1357-1359 | Batch589Logic.cs | a fight's reaction broadcaster ends with the social when the fight never reaches AfterFight |
| U4 | `reactioncopy` | `ReactionCopyCheck` | 1360-1362 | Batch589Logic.cs | each nearby Sim starts a one-shot reaction from the caller's reaction, not an earlier Sim's trait reaction |
| U5 | `carpoolschoolskip` | `CarpoolSchoolSkipCheck` | 1363-1365 | Batch589Logic.cs | a school child skipping the bus (horse, own lot) goes to school instead of crashing the pickup |
| U6 | `tvsyncalarm` | `TvSyncAlarmCheck` | 1366-1368 | Batch589bLogic.cs | a TV channel change resets the sync-event alarm handles so viewer reactions re-arm |
| U7 | `photofunds` | `PhotoFundsCheck` | 1369-1371 | Batch589bLogic.cs | a phone photo with short funds takes the funds instead of adding them |
| U8 | `ltrhistorybits` | `LtrHistoryBitsCheck` | 1372-1374 | Batch589bLogic.cs | a forced relationship change keeps the friendship history bits |
| U9 | `goodbyepure` | `GoodbyePureCheck` | 1375-1377 | Batch589bLogic.cs | building the goodbye pie menu no longer removes SoCold or despawns a Haunted ghost |
| U10 | `bathcleanup` | `BathCleanupCheck` | 1378-1380 | Batch589bLogic.cs | a queued second bath's cleanup leaves the bather's duck alarm and footprint alone |
| U11 | `tvwatchskill` | `TvWatchSkillCheck` | 1381-1383 | Batch589bLogic.cs | a WatchTV that never started does not stop the Sim's channel skill gain |
| U12 | `computergametype` | `ComputerGameTypeCheck` | 1384-1386 | Batch589bLogic.cs | an autonomous computer game's random pick is not kept in the shared definition |
| U13 | `moodtankreset` | `MoodTankResetCheck` | 1387-1389 | Batch589bLogic.cs | a recovered mood clears the remembered angry tank so the next angry mood tanks again |
| U14 | `reactiongrumpy` | `ReactionGrumpyCheck` | 1390-1392 | Batch589bLogic.cs | a Grumpy Sim's non-positive reaction type is set instead of staying Angry |
| U15 | `trainsimgone` | `TrainSimGoneCheck` | 1393-1395 | Batch589bLogic.cs | training with the trainee gone ends instead of throwing |
| U16 | `stereoupgrade` | `StereoUpgradeCheck` | 1396-1398 | Batch589bLogic.cs | a speaker upgrade does not start music on a silent stereo; a dance pair's end dismisses only its own dancers |
| U17 | `buffremovedelayed` | `BuffRemoveDelayedCheck` | 1700-1702 | Batch589cLogic.cs | removing a buff with a pending delayed add also removes the live buff |
| U19 | `tvdisclistener` | `TvDiscListenerCheck` | 1703-1705 | Batch589cLogic.cs | a new home-movie disc replaces the old disc's disposed-listener |
| U21 | `fridgetestnull` | `FridgeTestNullCheck` | 1706-1708 | Batch589cLogic.cs | opening a fridge with no inventory component is tested without a throw |
| U22 | `cancelposturedef` | `CancelPostureDefCheck` | 1709-1711 | Batch589cLogic.cs | cancelling a posture does not queue a second stand-up when one is at the head |
| U23 | `facialidleweight` | `FacialIdleWeightCheck` | 1712-1714 | Batch589cLogic.cs | buff and trait facial idles share their chance as floats (7+ idles no longer weigh 0) |
| U10 | `bathsituationfx` | `BathSituationFxCheck` | 1715-1717 | Batch589dLogic.cs | a bath's privacy situation is not exited twice and the old bubble effect is disposed |
| U18 | `socialnoeffect` | `SocialNoEffectCheck` | 1718-1720 | Batch589dLogic.cs | social starts with no matching rule are counted (the fix is ts3native ilp_socialnoeffect) |
| U20 | `guitargnomeboost` | `GuitarGnomeBoostCheck` | 1721-1723 | Batch589dLogic.cs | guitar play takes off every Elvis gnome boost it added |
| U23 | `distressgate` | `DistressGateCheck` | 1724-1726 | Batch589dLogic.cs | the higher-priority distress test finds buff distress too |
| U23 | `moodidleflavor` | `MoodIdleFlavorCheck` | 1727-1729 | Batch589dLogic.cs | MoodIdle continuation rows join their flavour instead of being dropped |

**ts3native IL patches** (`[IlPatch] Enabled=1` plus the key; specs filled for ea-1.67 and simler90-242, `native/ts3native/ilpatch-specs.h` regenerated; needs a ts3native build):

- U18 `ilp_socialnoeffect` (b11-social-no-effect): SocialInteractionA.Run returns false at EA's own early return right after UpdateConversationWhenSocialStarts when no social rule matched. Run is 4292 bytes with heavy private state, so it is patched, not copied. ts3fixes `socialnoeffect` counts the null rules.
- U20 `ilp_jambasecleanup` (b11-jam-base-cleanup): JamWithInstrument`1.Cleanup always runs the base cleanup; the InviteToJoin removal stays tied to the jam's end. The class is generic, so one IL body covers every instrument (a C# swap of a generic instance is untested).
- U20 `ilp_watchnoplayer` (b11-watch-no-player): MusicalInstrument.WatchBase`1.Run returns false when nobody plays the instrument any more (EA read ActorsUsingMe[0] unchecked).

**Readings where EA's intent needed a choice:**

- U2: the multiplier is applied once to the total (pre-loop sources and deltas). With one delta and no pre-loop source the result equals EA's.
- U5: a skipping rider on the school bus gets School.CreateWorkInteractionInstance (what Run does for riders off the lot), other cars the Occupation's ride only when there is one; a teen with a job on the school bus goes to school.
- U7: the next ModifyFunds after a phone camera's cost lookup that adds exactly the Sim's funds while they are short is EA's buggy payment; no yield lies between the two calls.
- U12: the shared definition's pick is put back to None when the run ends; two autonomous games started together still share one pick.
- U16: the broadcaster is rebuilt only if one was running (the stereo was playing).
- U17: the live buff is removed when EA's early return left it.
- U23 (flavour): no-op if the shipped Idles XML has no continuation rows; that XML was not extracted.

**Not built:**

- U24 ViewObjects.Run: false positive. The view is the reaction animation; EndCommodityUpdates and the Exit animation end it, and WaitAndGroupTalk is the lingering chat afterwards (EatHeldFood's hang-around has the same shape).
- The lower-impact list of §2 is not in this batch.

**Review pass (2026-11-16, before batch 12; every hooked EA body and our hook re-read in full):**

- `carpoolschoolskip`: EA's loop handled a skipping rider only when RiderDelegate had listed it; one it had not was neither sent nor skipped, and got a GetIn its definition refuses (no ride at all). Our copy now sends every skipping rider (a destroyed one is only taken off).
- `photofunds`: the cost note is now the photographer's and this sim tick's only (any Sim when EA passes no Photography skill); before, any later ModifyFunds by any Sim could take it after a photo that was not taken, or after the camera UI's PricePerShot lookup.
- `buffremovedelayed`: the second removal now needs EA's early-return condition itself (a pending delayed add of that buff), not just "still live after".
- `cancelposturedef`: the copy reads the head after OnCancelPosture (EA's order); the off path no longer calls GetCancelTransition (it builds a new transition each call), so [3044] counts cancels run as EA.
- `bathcleanup` + `bathsituationfx` hooked TakeBath.Cleanup twice: now one swap (`BBcHook`, `ApplyTakeBathCleanup`) that calls `BsfBeforeCleanup`.
- State that could outlive a world (Sim-keyed eat marks, mood ids, photo note) is cleared at world quit (`Batch589WorldQuit`); `sMiPatched` (unused) removed.
- Checked and unchanged: the Motive copy (EA's Carefree / LaidbackLemon `else if`, riding benefit after the multiplier), OneShotReaction (no caller passes a null ignore list), GetGoodbyeSocial (only caller builds the socials list; OnHug does the removal when the hug happens), GetFacialMoodIdles, ParseMoodIdleData, Guitar.Play.Run (EA adds the boosts before base.Run and removes none when it throws). No loops without an exit: the carpool waits are EA's, capped by kExtraMinutesToWait.
