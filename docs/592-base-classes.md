# 592. Usage sweep 4: base classes and shared helpers

> Posted upstream with the switches listed under "In-game results" at the end. Sections on other leads or switches are kept as research notes; those switches are not part of this change.


Date: 2026-11-16. Static read; the fixes built from it are proven in game (end of this doc). Follows docs/589-591.

**Scope:**
- The bases every interaction inherits: InteractionInstance, InteractionQueue, Interaction<,>, SocialInteraction/SocialComponent, Situation, Role/RoleData/RoleManager, Posture, CarrySystem, and the Sim exit-reason and use-list code.
- Autonomy and scoring.
- The Lot/LotManager helpers.
- RandomUtil, SimClock, AlarmManager and the other shared utilities.

Every item below was checked against ts3fixes (src, docs, proven-fixes, ilpatch-convert) and against simler90 UPDATE242 (mod-method-diff.csv, plus decompiles where unclear). V means verified in code; I means inferred; HH means the household can see it.

The plumbing every interaction runs through (Run/Cleanup, exit-reason copying, linked interactions, DoLoop, use lists, posture transitions, carry put-down) has no new high-severity bug.

## 1. Bugs

| # | Site | Bug | Fix | V/I | HH |
|---|---|---|---|---|---|
| S1 | Autonomy.CalculateScoreForObjectInteraction:1774 | The Loner penalty counts Sims in the actor's room, not the object's room (the tuning comment says the object's room), so a Loner never prefers an empty room | Compare against `target.RoomId` | V | yes |
| S2 | SocialComponent.LeaveConversation:898 | SocialsToday++, the Social Butterfly buff and its cheer animation, and ClearActionMemory all run before the null-Conversation return, so failed socials count | Move the return to the top | V | yes |
| S3 | SimClockUtils.OnFinishedLoadingSaveGameEventHandler:148 | On vacation arrival it passes `CurrentTicks + num` after already adding `num`, so alarms due within `num` hours of arrival bunch at arrival+num. The University branch passes CurrentTicks | `FixLoadAlarms(CurrentTicks)` | V (impact I) | yes |
| S4 | RandomUtil.GetIntMultiple:95 (from docs/591 C6) | Integer division before Ceiling, and truncation | ceilDiv/floorDiv with inclusive GetInt | V | yes |
| S5 | InteractionInstance.Cleanup:692 | The `!IsGameShuttingDown` check is inverted, so mVfxs is disposed only at shutdown; until then the effects wait for GC finalizers. The ts3fixes DjBooth cleanup copies the same check | Drop the `!`; fix the DjBooth copy too | V (IL) | maybe (lingering looped FX) |
| S6 | Lot.ObjectsToLookAtInRoom(roomId, false):2131 | RemoveAll on the live per-room list drops Sims from the look-at registry | Filter into a copy | V | yes (cosmetic) |
| S7 | InteractionInstance.EndCommodityUpdate:1552 | The clone is never removed from mActiveCommodityChanges; stale clones pile up, the oldest multiplier is used, and Modify removes the wrong one | Remove the clone in End | V (impact I) | rarely |
| S8 | DateAndTime(h,m,s) ctor:74 | Builds an absolute time from StartTicks, not a duration. simler90 fixes the Laundry callers; Grocery.cs:147 (lotto tooltip) still shows the wrong time | Callers use SimClock.Add | V | yes (text) |
| S9 | InteractionObjectPair.GetTraitIconKey:176 | The outer `ConstantChange >= 0` makes the dislike-icon branch unreachable. Check ilpatch `pie-menu-trait-icon-without-tradeoff` first | Drop the outer test | V | yes (UI) |
| S10 | InteractionQueue.GetFirstFreeIndexAfter:1496 | Breaks on MustRun, which inverts the AddInteraction block rule, so a jealousy reaction can run between the head and its MustRun continuation | `GroupId != g \|\| !MustRun` | I | rarely |
| S11 | Role.EndRole / RemoveSimFromRole:445-488 | The story-progression protection is never removed for an inactive or uninstantiated role, so the townie is protected for good | Unprotect whenever the flag is set | V path | no |
| S12 | Role.RemoveSimFromRole:480 | Wipes every Career outfit, including the new job's | Remove only mOutfitIndex | I | rarely |
| S13 | CarrySystem.PutDownOnSurface:780 | Fires ItemRemoved on placement (PutDown uses ItemPlaced) | ItemPlaced | V | no |

Latent, no live trigger today:
- AlarmManager.Simulate:577 loops forever when a repeat rounds to 0 ticks (fix: `Math.Max(num,1)`).
- Autonomy.ScoreInteractionsForCommodity:1348 retries the same index when an exception leaves nothing removed.
- TestInsaneAutonomy catch: `lotCurrent` can be null.
- ScoringFunctionsForPets.GetMaxMotiveDesire returns inf when there is no tuning.
- Posture.GetNextPostureIdle has a retry loop that never yields.

Tidy items:
- InsertInteraction has a dead null check.
- GetSelectedObjectsAsSims uses a castclass.
- PutClosestObjectAtIndex range is off by one.
- RoleManager.VerifyRoleGivingObjectRoles is O(n²).
- RoleData.GetRolesForWorld ignores `world`.
- CarrySystem PickUp hardcodes the event.
- Situation.RemoveAllInteractionsFromThisSituation never clears mForcedInteractions.
- Lot.RefreshObjectCachesIfNecessary is missing a break.

## 2. Callers of the shared bugs

**GetIntMultiple (S4).** Only Sims3GameplaySystems references it, in both EA 1.67 and simler90.

| Site | Feature | Tuning | Intended | Actual |
|---|---|---|---|---|
| ShoppingRabbitHole.TriggerSale:775 | Bookstore, recipe and relic store sales | 10/80/5 | 10..80 | 10..75 |
| Film.FinalizeScreenplay:904 | Film career screenplay value | 0/1500/100 | 0..1500 | 0..1400 |
| Sim.AbsentMindedAlarm:52844 | Absent-Minded idle | 10/30/1 | 10..30 | 10..29 |
| Sim:52889 | Nervous pet idle | 10/30/1 | 10..30 | 10..29 |
| Sim:52908 | Clueless pet idle | 10/30/1 | 10..30 | 10..29 |

**LeaveConversation (S2).** About 34 call sites:
- SocialInteractionA 660/1451/1486/1788/2027/2065
- FinishSocialAndLeaveConversation and FinishSocialContext
- Posture.CancelSocializing
- SocialCallback 1134/2445
- Bed cuddle, WooHoo and TryForBaby (about 12 sites)
- OccultFairy, BarBrawlSituation, SocialGroupInteractionSituation, Conversation:1699, SocialInteraction:1250, Sim.cs 23599/42696

**Cleanup mVfxs (S5).** Callers that set an effect actor by string name:
- FishHere (3)
- SculptingStation (3)
- SnowConeMachine (2)
- Snowman (2)
- FirewalkPit (2)
- SparklerBase (2)
- WishingWell (6)

**EndCommodityUpdate (S7).** Begin/End pairs inside a loop:
- DoPlant.RunCommon
- ServingContainer:490
- GypsyCaravan:513
- Mausoleum:243
- Terrain Contemplate:7767
- Sim.cs:26465
- plus 36 ModifyCommodityUpdate sites

**DateAndTime (S8).**
- Grocery lotto tooltip: still wrong.
- Laundry buffs: fixed by simler90.
- Relationship.GetWhenLastTalked, the CareerManager/CelebrityManager/SimDescription initialisers, Autonomy:226: harmless.

**ObjectsToLookAtInRoom (S6).** Only ShoppingRegister.cs:64 (Browse).

## 3. Speedups (exact)

- InteractionInstance.RemoveMotiveDelta(CommodityKind): a reverse RemoveAt loop instead of a temporary list. It runs at every motive End.
- Localization.FillInTokens:268/952: drop the HasLocalizedString lookup that only feeds the empty LogLocalizationResult. It runs for every localized string.
- AlarmManager.Read:933: skip the index build before RebuildIndices (load only).
- RandomUtil.GetRandomKeyValuePairFromPairedListDictionary: index mKeys/mValues directly instead of using the O(n) indexer.
- The Loner room count, once per pass (with S1).

## Batch 12

Built 2026-11-16, all default off: the switch table and the in-game results are at the end of this doc; docs/590 "Batch 12" lists the leads not built.

## Built fixes (from the docs/590 batch table)

EA bug fixes as EA intended, household included. Each switch is a ts3fixes Mono swap with a pure decision, a `<Name>Check`
(the hook's own handles, a live-path assert, controls) and a `<switch>-check` repro; 3 slots each (fixed / seen with the
switch off / errors). All default off.

| § | lead | switch | slots | fix |
|---|---|---|---|---|
| 37 | 592 S2 | `leaveconvnull` | 3172-3174 | leaving a conversation the Sim never joined does not count as a social |
| 38 | 592 S3 | `vacationalarms` | 3175-3177 | alarms on vacation arrival are not shifted twice |
| 39 | 592 S5 | `cleanupvfx` | 3178-3180 | an interaction's named effects are disposed at its cleanup in a live game (also in the djcleanup copy) |
| 40 | 592 S6 | `lookatcopy` | 3181-3183 | asking for a room's look-at objects without Sims no longer removes the Sims from the registry |
| 41 | 592 S8 | `lottotooltip` | 3184-3186 | the lotto 'not available' tooltip shows the real start hour |
| 42 | 592 S9 | `traiticondislike` | 3187-3189 | pie-menu entries that lower a trait motive show the dislike icon |
| 43 | 592 S11 | `roleunprotect` | 3190-3192 | a townie removed from an inactive role loses the role's story-progression protection |
| 44 | 592 S13 | `putdownplaced` | 3193-3195 | putting an object on a surface fires the placed event, not removed |

## In-game results (Bridgeport combined soaks, each switch off then on in one load)

| Switch | Off | On | Runs off BROKEN / on FIXED | Runs (phase-…) |
|---|---|---|---|---|
| `cleanupvfx` | BROKEN | FIXED | 1 | 20261116-0234 |
| `leaveconvnull` | BROKEN | FIXED | 1 | 20261116-0234 |
| `lookatcopy` | BROKEN | FIXED | 1 | 20261116-0234 |
| `lottotooltip` | BROKEN | FIXED | 1 | 20261116-0505 |
| `putdownplaced` | BROKEN | FIXED | 1 | 20261116-0234 |
| `roleunprotect` | BROKEN | FIXED | 1 | 20261116-0234 |
| `traiticondislike` | BROKEN | FIXED | 1 | 20261116-0234 |
| `vacationalarms` | BROKEN | FIXED | 1 | 20261116-0234 |

- [verified] The verdicts, controls ok and members found in the runs listed (`smallfix.txt` of each run).
- [inferred] How often each case happens in normal play: the checks call the same decision functions as the hooks.
