# 588: EA story progression, a full hand-read

Date: 2026-11-15. Static analysis only; nothing was built or run.

**Scope:**
- every type in `Sims3.Gameplay.StoryProgression` (53 files, about 13k lines), all managers and actions;
- the life-event callees (household, aging, death, pregnancy, genetics), read by a second reader;
- the careers, celebrity, service and pet pools, lots, culling and travel callees, read by a third reader (§4).

**Method:**
- `type-review-packet.py` and the scanners ranked the files first. They flagged only the Notifications listeners, which are released in Shutdown, plus the foreach scans in §5.
- I then read every function.
- Each type was checked against simler90's Gameplay Core Mod UPDATE242. I decompiled its copies of `Sims3GameplaySystems` and `simler90Extras` (`StoryProgressionStuff`) with the same tool as the EA original, so the diffs are clean.
- I also checked ts3fixes and `proven-fixes.txt`.
- I used the story-progression tuning (the `Demographics*` `_XML` files, extracted read-only with `tools/static/tuning-xml-extract.ts`) to see which demographics and actions are actually live.

**Marks:** verified = proven by reading the code path; inferred = the effect follows but was not measured; latent = needs an unusual state.

**Household rule.** Every action targets NPC households, because `SelectRandom*` skips the active and travel households. #7 is the exception, where that guard can go stale.

## 1. How it works

`StoryProgressionService` runs a task once a day at 1 AM, or once a minute in accelerated mode. The task does 4-10 rounds of `TryOneAction`. On return to a travel world, `DoTravelActions` does up to 100.

Each round:
1. `Demographics.Collect()` rebuilds about 20-30 demographics over every lot, household and Sim.
2. `GenerateActions` makes 10 draws of a demographic weighted by its error, with 5 actions per draw. Each action picks its target when it is constructed.
3. Each action is scored with `CalculateNewError`: the demographics are cloned, `action.UpdateDemographics(clones)` is applied, and the new total error is read.
4. One action is picked by weight and executed.

So story progression only works if the demographics count the world correctly and each action predicts its own effect correctly. Most of the bugs below break one of those two things.

## 2. Story-progression bugs (EA, not fixed by simler90 or ts3fixes)

| # | Where | Failure | Household sees it | Mark |
|---|---|---|---|---|
| 1 | `Demographics.NumberFriends`, `NumberBestFriends`, `NumberEnemies` and `HighestRomance`: `Add(SimDescription)` | `Collect` reuses the incremental Add. For each friend, that Add also moves the friend's own entry up by one, or takes another Sim's entry out of the bucket if the friend has not been added yet. The histograms come out skewed toward "more friends" and "more romance". The live Friends tuning answers with `Antagonize`/`AntagonizeExisting`, so NPC friendships are pushed down and romance is mis-steered. The cost is also O(Sims x relationships²) per Collect. | no | verified (the bias is inferred from tuning) |
| 2 | `HouseholdTooBigDemographic` and `HouseholdTooSmallDemographic` copy constructors | `mLotsOverfull = other.mLotsOverfull` shares the live dictionary with every clone, so scoring a candidate action changes the real demographic. All later scores in that round depend on the order the actions were scored in. | no | verified |
| 3 | `HouseholdTooBig`, `HouseholdTooSmall` and `HorsePopulation`: `Add(HouseholdCreationSpec)` | `Household.Find(household.HomeLotId)` passes a lot id to a household-id lookup, so it always returns null. Predicted move-ins never count toward too-big, too-small or box-stall, and the move and merge actions get no credit. `TooSmall` also dereferences `LotHome` before its null check. | no | verified |
| 4 | `HistogramDemographic.GetActionItem` | `ForcedBucketNumber = num` stores the position in the under-filled list, not the bucket (`list2[num]`). Targeted actions therefore pick Sims and households from the wrong bucket, or from none. The neighbour test also indexes `mErrorCurve.Points` with bucket numbers. | no | verified |
| 5 | `CreateAndMoveIn`, `MoveInHousehold.UpdateDemographics` | The new family spec is added twice (once by Create, once by MoveIn) with `HomeLotId` 0. The prediction counts the family twice and shows no lot becoming occupied, so "Create and Move In" (multiplier 2) never looks like it raises lot density. Only the hard `AllowMoreStoryProgressionMoveIns` gate holds it back. | no | verified |
| 6 | `LotDensity` | The denominator leaves out `kEP1_PlayerOwnable` and `kEP9_Apartment` lots, but the numerator counts their households. Density is overstated in worlds with those lots, which blocks move-ins and favours move-outs. With more households than lots, the uint subtraction underflows. | no | verified (impact limited to those worlds) |
| 7 | Most `IsValid()` methods (everything except Kill*, Emigrate, Merge, RemovePet and RemoveRoommate) | They never recheck that the target is not in the active or travel household. A round yields about 50 times (`Simulator.Sleep(0)`) between choosing the target and executing. A household switch, or a target who joins the active household in that window, lets MoveOut, MoveTo*, job, fitness or occult actions hit a played Sim. | **yes** | inferred (race) |
| 8 | `AddRoommate.Execute` | In worlds other than University it builds `AcquireOccupationParameters` and throws it away, so non-University roommates never get a job. | no | verified |
| 9 | `StoryAction.FindOccultSimDescription` | It builds the full candidate list, then returns the first occult in household-list order. Remove* therefore always strips the same household's occult. | no | verified |
| 10 | `Demographics.Collect` | The guard is `sHousehold != null && IsSpecialHousehold` where `== null ||` was meant, so a null entry is dereferenced. Other story code treats null entries as possible. | no | latent |
| 11 | `DoTravelActions`, `ElderDensity` and the occult population demographics | The travel loop runs up to 100 full Collect-and-generate rounds and does not stop when no action runs. Elder and occult `Value` call `Household.EveryHumanSimDescription()`, which allocates a list of every Sim on each read, about 4 times per clone per scored action. `ElderDensity` has no Add or Remove, so `KillElderSim` never gets predicted credit. | no | verified (cost is inferred) |
| 12 | `MergeHouseholds.Execute` | Removing the last member runs `HandleLastSimsDeath` (MoveOut and Destroy), and then `household2.Destroy()` runs a second time. | no | inferred |

### Fix directions

These are fix directions for the builder; nothing was implemented here. Each one also tidies the function, per the "organize when re-coding" rule.

1. Give Collect its own add that records only the Sim's own count. Compute each Sim's friend, best-friend, enemy and top-romance counts once per Collect in a dictionary keyed by Sim id, so the cost is O(relationships). Keep the neighbour-adjusting path for `Modify` and `Add(RelationshipSpec)` only.
2. Copy the dictionaries in the copy constructors with `new Dictionary<ulong,int>(other...)`.
3. Resolve the lot with `LotManager.GetLot(spec.HomeLotId)` and read its metrics. Do one shared null-safe check of dorm, apartment and lot.
4. Store `list2[num]`. Choose increase or decrease by comparing that bucket's error with its neighbours' buckets (`num-1`, `num+1`, clamped), and drop the mixed index spaces.
5. Override `CreateAndMoveIn.UpdateDemographics` to add one copy of the spec with `HomeLotId = mLotId`.
6. Use one `IsStoryLot(lot)` predicate for both the lot count and the occupied count, and do the arithmetic in `int`.
7. Add a central guard in `TryOneAction` right before `Execute`: skip the action if any affected household is active, the travel household, or holds the selected Sim. A Sim of unknown scope counts as household.
8. Call `mSimDescription.AcquireOccupation(params)`.
9. Filter on the occult type inside the predicate and pick at random.
10. Change the guard to `sHousehold == null || sHousehold.IsSpecialHousehold`.
11. Break out of the travel loop when a round executes nothing. Cache the human and elder counts during Collect, and give `ElderDensity` Add and Remove.
12. Use `RemoveSimKeepHousehold` inside the loop and Destroy once at the end.

## 3. Life-event callees (second reader, spot-checked)

| # | Where | Failure | Household sees it | Mark |
|---|---|---|---|---|
| C1 | `Household.Dispose` | It only moves a household out when it has a real home lot. Townie households are disposed directly by `DestroyHouseholdsWithoutGuardians` (Edit Town), by EA `CheckForAbandonedChildren` (simler90 changes that one) and by the `Cleanup` final loop. Those households stay in `Lot.mVirtualSlotHouseholds`, the NPC door map and the mailbox, so slot counts stay inflated and dead graphs are kept, and probably saved. | no | verified (saving inferred) |
| C2 | `Household.Cleanup`, duplicate-member merge | The inner guard tests `sHousehold3` again instead of `sHousehold4` (copy-paste). A lot can then be handed to a special household or one queued for deletion. Members that were not shared are cleared and left as orphans. | **possibly** | verified (the trigger, a load with a member in two households, is inferred) |
| C3 | `SimDescription.FindSuitableVirtualHome` | `Math.Max(Count*kShellsLivableByServiceSims, Count-1)` ignores the 0.5 tuning, and a tuning value above 1 would index past the end of the list. | no | verified (that `Min` was meant is inferred) |
| C4 | `AgingState.AbortSimBuilderTask` | It re-arms a 5-minute alarm with no limit while `SwapPending` stays set. | no | latent |
| C5 | AgingManager graduation early-message alarm | The handle is overwritten on each warning day and never cancelled in `CancelAgingAlarmsForSim`. | no | verified (cost only) |
| C6 | `Pregnancy.CreateNewborns` and `CreateNewbornsBeforePacking` | The temporary household for an unpacked mini-sim father is destroyed before `RemoveTemporary`, and `UnpackSim()` has no null check. | yes (out-of-world father) | latent |
| C7 | `Genetics.MakeDescendant` and `AssignRandomTraits` | `array[0]` is read with no parents. The `IsFemale` test is inverted, which has no effect. | no | latent |

**Fixes:**
- **C1:** move out on `LotHome` or `VirtualLotHome`, and add an NPC-only sweep that drops disposed households from lot slots and door maps.
- **C2:** guard on `sHousehold4`, re-home the members that were not shared, exempt the active household, and use a HashSet to replace the O(H²·n·m) `Contains` calls.
- **C3:** use `Min`, with at least one lot.

## 4. Careers, pools, lots and travel callees (third reader)

| # | Where | Failure | Household sees it | Mark |
|---|---|---|---|---|
| P1 | `CareerManager.AcquireOccupation`, then `Career.LeaveJob` and `LeaveJobOnExitCheck` | If the Sim is at work, `LeaveJob(kTransfered)` only defers the leave (`mLeaveJobOnExit`), and the new career is attached anyway. When the Sim leaves work, `WorkInRabbitHole.Cleanup` runs the old career's `LeaveJobNow`, then `AssignOccupation(null)` and `AssignJob(null)`, which drops the new job. The Sim is jobless but still listed in the new location's workers and coworkers. Story `GetJob` reaches this for NPCs at work. | **yes**, if a household Sim takes a job while at work | verified |
| P2 | `Service.FindSimForAssignment` | When no free pool Sim fits, it creates a new NPC with no `kMaxNumNPCsInPool` check, and nothing trims the pool back. | no | verified (growth rate inferred) |
| P3 | `FakeMetaAutonomy` (the retired-service activity pool) and `Service.CheckIfSimShouldBeAddedToPool` | The pool cap is `int.MaxValue`, so the cull is skipped. `CullLeastSignificantSims` compares the candidate count, not the pool count. Every tick it also runs O(pool·services·pools) and O(n²) dedupe work, plus a daytime scan of every lot. | no | verified (cost inferred); the pool bloat overlaps external lead #34 |
| P4 | `CelebrityManager.RemoveFreeStuffAlarm` and `RemoveDeflectScandalCooldown` | Both dereference `OwnerSim` with no null check, so story `CelebrityLevelModification` gets an NRE for an uninstantiated celebrity. `OnLevelModified`'s `<=` runs `OnLevelUp` when the level does not change. | no | verified (trigger inferred) |
| P5 | `CareerManager.AcquireOccupation` | The old job is left before the pension-loss prompt, so answering "no" leaves the Sim jobless. | **yes** (player dialog) | verified |
| P6 | `Lot.VirtualMoveIn` | There is no Contains guard on the saved resident list, and the list overload passes `isVirtual: false`. | no | inferred |
| P7 | `PetPool.RandomRemovePet` | Ghost ids with no member are never removed, yet still count toward `IsFull`. | no | latent |

The third reader also found the apartment-resident-list leak that C1 describes, independently.

**Fixes:**
- **P1:** finish the deferred leave now, or route the change through `mTransferCareer`/`DoTransferCareer`. Run `LeaveJobOnExitCheck` only when `Occupation == this`.
- **P2:** create a new NPC only while the pool is below its cap, and otherwise return null so the request retries.
- **P3:** give the activity pool a real cap, cull against `mPool`, dedupe with a HashSet, and cache the venue list once per day.
- **P4:** remove the alarms with `AlarmManager.Global.RemoveAlarm(handle)`, use strict `<`/`>`, and return early when the owner is null.
- **P5:** show both prompts before any `LeaveJob`.

## 5. Already fixed, or not a bug

**simler90 UPDATE242:**
- KillSim with no mausoleum (orphan SimDescription): simler90 disposes it.
- GenerateOffspring partner filters: long-distance partners, special, active and travel households, plumbots and plant Sims.
- MoveOut, Emigrate and MoveIn lot selection, and neighbour pet adoption.
- MoveInWithPartner checks for pets and ghosts.
- AddAcademicsDegree requires young adult or older.
- ChangeFitness skips robots.
- RemovePet only targets elder pets.
- Mermaid and transmutation death types.
- Relationship and romance state rework in ChangeRelationship.
- Death relationship finalisation, the mini-sim death helper, and the elder minimum-age units.

**ts3fixes:** `birthroute`, `deadrefguards`, `relremovesafe`, `reltablesapart` and `ageupabort` cover the birth and dead-reference cases.

**Checked and dropped:**
- `ThrowHouseParty.Execute` is empty, but its tuning multiplier is 0 in every world, so it never runs.
- The `kEventCareerPromotion` double listener only adds gossip, and `Gossip.AddGossip` dedupes it.
- The `GenerateOffspring` "days" parameter really carries aging years, which is the same unit as `GetAgingStageLength`, so it is only a naming problem.
- The scanner's SplitHousehold foreach-remove breaks right after the remove.
- The first-item scans in Merge, Emigrate and MoveOut are early-out predicates.
- `GetWeightedIndex` on an all-zero array returns 0, and the zero score then blocks Execute.

## 6. Next

- Live check: dump the Friends histogram from `Collect()` next to the true per-Sim counts. #1 predicts a shift toward higher counts.
- Live check: log Antagonize against Befriend picks over one week, with and without a fix for #1.
- Fix #2, #3 and #5 together, because all three corrupt action scoring. Measure the total error and the action mix after 7 sim-days.

## 7. Batch 10: built (2026-11-16, all default off, NOT RUN)

User rule (2026-11-15): "fix, make it as EA intended, even for our household". No household exemption beyond EA's own; household-visible fixes are marked. Code: `mods/ts3fixes/src/Batch588Logic.cs`, `Batch588bLogic.cs`, checks in `Batch588Checks.cs`, `Batch588bChecks.cs`. Counters 2929-2985 (3 per switch, in this order). Every hooked method was diffed against simler90 UPDATE242 (decompiled with references): only `TryOneAction` (one notification call, made by sprecheck through reflection when simler90's DLL is loaded) and `PetPool.RandomRemovePet` (prefixed only) differ.

| § | Lead | Switch | Check | Household-visible |
|---|---|---|---|---|
| 1 | #1 relationship histograms skewed in Collect | `sprelcount` | SpRelCountCheck | no |
| 2 | #10 null household in Collect | `spcollectnull` | SpCollectNullCheck | no |
| 3 | #2 clone shares the lot dictionary | `spclonedict` | SpCloneDictCheck | no |
| 4 | #3 lot id passed to Household.Find | `splotlookup` | SpLotLookupCheck | no |
| 5 | #4 ForcedBucketNumber position vs bucket | `spbucketindex` | SpBucketIndexCheck | no |
| 6 | #5 CreateAndMoveIn spec added twice | `spcreatemovein` | SpCreateMoveInCheck | no |
| 7 | #6 LotDensity numerator vs denominator | `splotdensity` | SpLotDensityCheck | no |
| 8 | #8 roommate job dropped | `sproommatejob` | SpRoommateJobCheck | no |
| 9 | #9 first occult always picked | `spoccultpick` | SpOccultPickCheck | no |
| 10 | P1 new job lost when the old one's deferred leave runs | `spjobatwork` | SpJobAtWorkCheck | **yes** |
| 11 | #7 target recheck right before Execute | `sprecheck` | SpRecheckCheck | **yes** (protects it) |
| 12 | P5 pension prompt after the leave | `sppensionprompt` | SpPensionPromptCheck | **yes** |
| 13 | C2 Household.Cleanup wrong guard, orphans | `hhcleanupdup` | HhCleanupDupCheck | possibly |
| 14 | C3 virtual home share Max vs Min | `virtualhomeshare` | VirtualHomeShareCheck | no |
| 15 | P4 celebrity alarm removal with no Sim | `celebalarmnull` | CelebAlarmNullCheck | no |
| 16 | C1 disposed virtual-home household left in slots | `disposevirtualslot` | DisposeVirtualSlotCheck | no |
| 17 | C5 graduation early-message alarm never cancelled | `agingalarmcancel` | AgingAlarmCancelCheck | no change shown |
| 18 | P6 duplicate virtual move-in | `virtualmoveindup` | VirtualMoveInDupCheck | no |
| 19 | P7 pet pool ghost ids | `petpoolghost` | PetPoolGhostCheck | no |

**Readings where EA's intent was ambiguous (per the user, the one the code and tuning support):**
- §5: the neighbour test compares the error EA recorded for buckets `list2[num-1]` and `list2[num+1]` (the first curve point of each), keeping EA's list-order neighbours; only the mixed index spaces go. `DemographicTuning.GetBucketWeight` is left as EA's.
- §7: AllowMoreStoryProgressionMoveIns is not changed: its uint difference is cast to int, so an "underflow" already reads negative and refuses. Only the numerator's lot test is aligned.
- §11: EA's selectors and AddRoommate's dorm branch all exclude the active household, so the recheck is EA's intent. A Sim id that resolves to nothing cannot be an active-household member, so it does not block; an error while checking blocks (unknown scope counts as household). AddRoommate's apartment branch has no active-household test; the recheck covers it.
- §12: the pension prompt is asked first; the identical prompt EA asks later in the same call is answered yes for the player (the player already said yes), so the pension still ends where EA ends it.
- §13: unshared members are moved into the larger household (EA's Household.Add, as its own re-home pass does). EA's re-test of sHousehold3 also stopped a second fold; that is kept as a break. No active-household exemption (user rule).
- §14: the tunable's comment ("lower end percentage of buildable shells habitable by service sims") and the cost-sorted list make Min the reading.
- §15: OnLevelModified's `<=` is left: equal levels only re-check the skill and journal and show nothing.
- §2 (P1 in §4): the fix keeps the old career's cleanup (location, coworkers, alarms) and only stops its AssignOccupation(null) from clearing a newer occupation.

**Not built, one reason each:**
- #11 travel loop: a round that runs nothing is a fresh random draw; stopping early changes how many actions EA runs on return, so it is not a bug fix. ElderDensity Add/Remove: its Value reads the live world (EveryHumanSimDescription, all households), which Collect's Add set does not match; adding Add/Remove would change what it measures; caching the count is not exact across the round's yields.
- #12 MergeHouseholds double Destroy: the second Destroy targets an already destroyed id (inferred harmless), and the fix means rewriting simler90's modified Execute.
- C4 AbortSimBuilderTask: one alarm at a time while EA waits for the swap; a cap would stop a builder mid-swap (latent).
- C6 Pregnancy: Destroy before RemoveTemporary only reorders plain list work that still runs; the UnpackSim null needs a father mini-Sim whose unpack fails and a rewrite of EA's whole newborn method in a household birth path (latent).
- C7 Genetics: needs a caller with no parents (none in EA code); the inverted IsFemale test has no effect.
- P2 service pool cap: returning null when full needs every FindSimForAssignment override to retry; a wrong cap means a service never arrives.
- P3 FakeMetaAutonomy pool: EA set the cap to int.MaxValue, so a cap is not EA's intent; the per-tick cost is a perf design of its own.
