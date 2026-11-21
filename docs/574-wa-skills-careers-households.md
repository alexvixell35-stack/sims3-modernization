# 574: World Adventures, skills and self-employment, rabbit-hole careers, relationships and households

2026-11-15. Static only. The EA code was read in `game-copy/decompiled`, which stays local. Built, proven in game 2026-11-15 (off BROKEN / on FIXED).

**How the leads were found.** Four read-only lead passes, one per area. Every fix below was re-checked against EA's code. Overlap was checked with `docs/data/swap-stack-audit.csv` and `docs/index.md`; this covers hidden rooms (552), nectar (566), dead opportunity listeners and combo opportunities (562), and the 570-573 career and travel fixes. No EA method is hooked twice.

**Checks.** Each fix has a pure check, `Ts3Fixes.Fixes.<Name>Check("run"|"result")`, kept in `Batch574Checks.cs`. A check prints `controls ok=` and `members found=`. It gives `verdict: BROKEN` with the switch off, `verdict: FIXED` with it on, and `UNEXPECTED` otherwise. The repros are in `tools/harness/repros.json`, under the ids `<switch>-check`.

**Rules.**
- Every switch is off by default.
- Every fix is an EA bug fix, and no fix moves a Sim.
- Household outcomes change only where EA's code plainly fails its own intent.

| § | Switch | Slots | File | Status |
|---|---|---|---|---|
| 1 | `wildherdmother` | 2654-2655 | `HouseholdLogic.cs` | verified |
| 2 | `splitpartner` | 2656-2657 | `HouseholdLogic.cs` | verified |
| 3 | `movingflag` | 2658-2659 | `HouseholdLogic.cs` | code verified, trigger inferred |
| 4 | `sandscoins` | 2660-2661 | `WaSkillsLogic.cs` | verified |
| 5 | `tombmusic` | 2674-2675 | `WaSkillsLogic.cs` | verified, cosmetic |
| 6 | `artsellnoartist` | 2662-2663 | `WaSkillsLogic.cs` | code verified, how the painter disappears inferred |
| 7 | `royaltyorphan` | 2664-2665 | `WaSkillsLogic.cs` | code verified; that the orphan never fires is inferred |
| 8 | `promoteshift` | 2666-2667 | `CareerLogic.cs` | verified |
| 9 | `gotoworkstale` | 2672-2673 | `CareerLogic.cs` | code verified, needs a save in the 1-2 h before the push |

## §1 wildherdmother: taming or adopting a herd mother with foals throws (loop rule)

**The bug.**
- `WildHorsePool.FindNewHerdMother` loops over `herdMother.Genealogy.Children` and calls `child.RemoveDirectRelation(herdMother.Genealogy)`. That call takes the third branch of `Genealogy.RemoveDirectRelation`, which removes the child from the same list.
- So the loop throws "collection was modified" once the mother has at least one foal.
- `RemovePet` calls it first. Taming or adopting the mare (SocialCallback) therefore aborts before the household add, and so does the Grim Reaper pet clean-up. Her foals are left half re-parented.
- When there is no other adult mare, the `HerdMother` setter also throws on null.

**The fix.**
- The same steps, run over a copy of the list.
- A missing mother is tolerated.
- When no new mother is found, it is stored as none (`mHerdMotherId = 0`).

**Same file, not fixed.**
- `GetLeadHorse` tests `simDescription.IsFemale` where it means the candidate (wrong variable). This is cosmetic.
- `SetupNewHerdMother` removes parents by index while the list shrinks, but nothing calls it.

## §2 splitpartner: story-progression household split

**The bug.**
- `SplitHousehold.Execute` sets the leaver's `Partner = null`. The partner who stays keeps pointing at the leaver.
- EA's own `ClearPartner` clears both sides, and every other path uses it.
- The constructor's candidate list adds any Sim with an Ex in the household, with no age filter (`CanSplit` has one). So a child or teen can be moved out alone. A young adult with an Ex is also added twice.

**The fix.**
- After `Execute`, a one-sided Partner on the stayer is cleared.
- `IsValid` refuses a leaver below young adult.

**Who.** NPC households.

## §3 movingflag: a stale "move in progress" flag

**The bug.**
- `MovingSituation.sbMovingInProgress` is a `[PersistableStatic]` that only the situation's `CleanUp` clears.
- `OnParticipantDeleted` never lowers the wave count, so a situation can stall. A save made mid-move can also reload with a stale `true`.
- Edit Town, the phone and computer move options, and the move-in socials then fail with "MoveInProgress".

**The fix.** At world load, a set flag with no `MovingSituation` left in `Situation.sAllSituations` is cleared. Loaded root situations register in that list from their constructor.

## §4 sandscoins: Sands of Understanding bonus coins never given

**The bug.**
- `TreasureChest.OpenWithAnimation.TreasureChestOpenedCallback` creates an `AncientCoinPile` out of the world, shows "bonus coins", clears the flag and removes the sands.
- It never adds the pile anywhere. The household gets no coins, and a leaked object goes into the save.

**The fix.**
- EA's steps, with the pile added to the opener's inventory. There, EA's own `AncientCoinPile.ItemComp_OnAddToInventoryEvent` credits `Household.AddAncientCoinCount` and removes the pile.
- A refused pile is destroyed.
- EA's own block then sees the cleared flag and does not run again.

## §5 tombmusic: tomb music statics survive a world quit (cosmetic)

**The bug.** `TombRoomManager.sMusicType`, `sTombMusicHandle` and `sTombPlaylistHandle` are plain statics and are never reset. Quit inside a tomb, then enter a tomb room with the same music type: EA only moves a dead handle, so no music plays.

**The fix.** All three are reset at world quit.

## §6 artsellnoartist: selling art whose painter is gone

**The bug.**
- `ImageObject.SellImageObject(Sim,int,bool)` runs in this order:
  1. pays the seller and sends the sold event;
  2. dereferences `Artist` (`SimDescription.Find(PainterSimId)`) with no null check;
  3. destroys the item.
- Paintings or photos whose painter was culled, or that were imported from another world, throw at step 2. The item stays and can be sold again.
- The sell-all loops (camera, phone) stop at that item.

**The fix.** When the artist is gone, the artist's sold event is skipped. Payment and destroy run as EA meant. With an artist present, EA's code runs unchanged.

**Not fixed.** `EaselCanvas.SoldFor` (the buy-mode sell path) also dereferences `Artist` without a guard. It is parked for a second pass.

## §7 royaltyorphan: an orphan royalty alarm per trip

**The bug.**
- `SkillManager` imports a skill before setting its owner, so `Writing.ImportRoyaltyAlarms` creates `new RoyaltyAlarm(skill)` with a null alarm owner.
- `Writing.MergeTravelData` then builds a new alarm for the home skill, sharing the same `RoyaltyList`, and never removes the imported one.
- Dispose can't find an alarm with a null owner. So one persisted weekly alarm, holding the disposed travel copy, piles up per trip for each writer.
- NRaas Overwatch has a cleanup for exactly these alarms (community-reported).

**The fix.** After the merge, the imported skill's alarm is removed. Its royalty list stays shared with the home alarm.

## §8 promoteshift: the shift on the day a new level starts

**The bug.**
- **Missed day off.** `Occupation.IsTimeDuringWorkDay` has a same-day-as-promotion branch: not hired today, and more than `MinHoursBetweenShiftsAfterPromotion` (8 h) before the start. That branch never checks `DaysOfWeekToWork`. So a night-shift Sim promoted after midnight into a day off (Criminal, BotArena) is "missing work" that night and loses performance on a day off.
- **Stuck in the rabbit hole.** `Career.IsRegularWorkTime` answers false for the whole day the level starts. A Sim sent to the allowed shift therefore waits inside the rabbit hole (`WorkInRabbitHole` loop at line 268) until the next scheduled day: about one day, about two for BotArena 2->3.

**The fix.**
- The same-day rule also requires a work day.
- `IsRegularWorkTime` applies the same same-day rule, then EA's own window test, instead of refusing the whole day.

## §9 gotoworkstale: a stale persisted go-to-work alarm

**The bug.**
- `Occupation.CreateGoToWorkHandle` adds an `AlwaysPersisted` alarm, but `mRegularWorkDayGoToWorkHandle` is `[Persistable(false)]`. After a load, nothing can remove that alarm.
- If the Sim quits, is fired or retires before it fires, `Career.RegularWorkDayGoToWorkHandle` builds a work interaction from a null `CareerLoc` and throws.
- After a promotion that moves the hours, the Sim gets a stale push at the old time.

**The fix.** The callback does nothing when the career is no longer the owner's occupation, or has no location or level.

## Parked leads

| Lead | Why parked |
|---|---|
| `Opportunity.ClearChainHistory` uses `o4.TargetWorldRequired` on both sides of an `||` (copy-paste; `o3` was meant) | typo verified, effect inferred; fixing the condition sends more calls into a branch that dereferences null at the chain root, so the static recursive method needs a careful full rewrite |
| `TreasureChest.OpenWithAnimation.Cleanup` never calls `base.Cleanup()` | the Sim reset on an exception clears use lists; impact unclear |
| `MartialArts.FindSparTournamentOpponent` clamps rank-0 opponents with the rank-1 floor | changes NPC skill levels; intent inferred |
| `AncientCoinPile` size uses `new Random().Next(min,max)` (max is excluded, and a fresh seed per pile) | low value |
| `Relationship.DecayAll` checks boarding school only on the higher-id Sim | `EaSpread.cs:564` (our existing file) copies the same check; the cleanup agent owns existing files |
| `LongTermRelationship.UpdateLTR` shortens Betrayed on every update, not only on improvement | intent inferred; needs two hooks |
| `RomanceVisibilityState.ExecuteCleanSlateBehavior` has no null check on the spouse's romance state | inferred trigger |
| `Occupation.DaysUntilWork` uses up a leave day on non-work mornings, so the "starts in" text is off by a day and `EnsureVacationDays` adds an unpaid day | `AutShadowChaLists.cs:782` lists the method; next batch |
| Weekly royalties never count toward Professional Author XP | may be by design; owner's call |
| `Writing.AddBookToLibraries` has no cap, and a failed `TryToAdd` leaks the book | impact inferred |
| Same title as both novel and comic: the static `BookWrittenDataList` throws on finish | rare |
| `SculptingStation.Sell` credits `Sim.ActiveActor`; buy-mode art sales send `kSoldObject` twice | low |
| `InventingSkill.sObjectsBeingDetonated` is never cleared | low |
| `WorkInRabbitHole.BeforeEnteringRabbitHole` has `!IsAllowedToWork() && IsAllowedToWork()` (never true) | crash-triage path (doc 67); pair it with a bounded wait later |
| `Career` performance divided by `Metrics.Count == 0` | mod careers only |

## Loop-logic pass (standing rule)

- **Real EA cases, now fixed:**
  - §1: a foreach over a list it modifies;
  - §8: a rabbit-hole wait that cannot end the same day.
- **Parked:** `SetupNewHerdMother` (index removal while the list shrinks; no caller). `Household.ExtractRoommateSims` (modifies its list while walking it; no caller).
- **Checked clean:**
  - the carpool `WaitForCarpool` timeout;
  - `Visa.UpdatePoints` (bounded at 3);
  - the dive-well TNS loop;
  - `TombTrap` re-entry;
  - skill `UpdateProgress` and decay;
  - `ChangeState` BestFriend recursion;
  - the `PhoneService` loops (docs/573).
- **Our own new code:**
  - the herd-mother walk uses a copy of the list;
  - there are no per-tick scans; the moving-flag scan of `sAllSituations` runs once, at load;
  - all reflection members are cached statics.

## Tests (combined run, off then on)

All checks are pure and work in any world. In-game, end-to-end where possible:
- **§1:** tame a wild mare with a foal (Appaloosa Plains).
- **§4:** use Sands of Understanding on a chest, open it, and look at the coin count.
- **§6:** sell a painting by a culled townie.
- **§8:** promote a criminal on Tuesday night, then watch Wednesday.
