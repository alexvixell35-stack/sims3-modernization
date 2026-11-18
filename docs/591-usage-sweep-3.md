# 591. Usage sweep 3 (ranks 201-378 and callees of ranks 21-100)

Date: 2026-11-16. Static read, nothing built. Follows docs/589 and docs/590. `*.Mimics.*` types were skipped because they have no code of their own.

Every item was checked against ts3fixes (src, docs, proven-fixes, ilpatch-convert) and simler90 UPDATE242 (docs/data/mod-method-diff.csv plus an ilspycmd diff at the flagged sites). V = verified in code, I = inferred, HH = household-visible.

Dropped because they are already fixed:
- By simler90: ActiveFireFighter.LeaveJobNow and EnterRelaxing.ShouldChangeIntoSleepwear.
- By ts3fixes: picniccamera, cleanflag, UpgradeFailAlarm, PortraitPose, GenieBanish, Batch582g JungleGymRef, docs/567 Repairable, the docs/577 Visit greet alarm and SnugglePetB.

## 1. Callees of ranks 21-100

| # | Site | Bug | Fix | V/I | HH |
|---|---|---|---|---|---|
| C1 | Skill.ComputeSkillGainMod | The Socially Awkward science bonus (+15%) applies to every skill once the Sim has Science | Also require `mSkillGuid == Science` | V | yes |
| C2 | LogicSkill.UpdateChessTournamentStatistics | Comparison reversed; stores the opponent's record, not its rank | `skill.mChessRank >= mRank..`; assign mChessRank | V | yes |
| C3 | LogicSkill GetAffinity / FindSuitableChessRankOpponent | 5-entry array, 6 ranks: IndexOutOfRange after reaching Chess Master; table flags left set | Clamp the index | I (tuning) | yes |
| C4 | GenericPrivacySituation Undisturbed intruder check | A departed Sim stays in OtherSims, so no room check when they return; per-minute RemoveAllChecks | Remove from OtherSims | V | yes |
| C5 | RockBand.OppGigMasterCompleted | `>` needs 11 gigs; the text says 10 | `>=` | V | yes |
| C6 | RandomUtil.GetIntMultiple | Integer division before Ceiling; truncation means the top multiple is never returned (sales never 80%, etc.) | Ceiling on a double; inclusive max | V | yes |
| C7 | Lot.UpdateDoorPrivacy | A null SyncTarget throws in a try/catch, so later doors lose privacy | Skip null SyncTargets | V | yes |
| C8 | BandSkill.ImportCompositions | Adds null compositions; `value` is not guarded | Skip nulls | V | yes |

## 2. Ranks 201-378

| # | Site | Bug | Fix | V/I | HH |
|---|---|---|---|---|---|
| R1 | LeafPile Burn.Run | mBeingBurned is set before AddFire, whose false result (non-owned lot) is ignored; the pile stays unusable for good | Clear the flag when AddFire fails | V | yes |
| R2 | CatchPrey.RollDiceForCatchingPrey | Precedence: snakes and turtles are always caught | `Opp && (Lizard\|\|Snake\|\|Turtle)` | V | yes |
| R3 | PicnicBasket ants | The ant alarm is never removed on clean, dispose or reset; ants re-arm; FX leak | Remove the alarm; Dispose the FX | V | yes |
| R4 | GroupingSituation.ScoreDateParticipant | `value` is null when StartTime is read; NRE leaves the date stuck | TryGetValue first | V | yes |
| R5 | PetSurfacePosture.LoyalDogLayDownCallback | LayDownHere targeting a Sim fails its own Test, so the behaviour never runs | Target the pet itself | V | yes |
| R6 | VisitSituation.Remove | Listeners stay in mEventListeners and are re-added on load, so strikes count double | Remove the dict entry | V | yes |
| R7 | ScienceComponent.ActivateSound | StartVisualEffects is called twice, leaving the first FX set behind | Drop the second call | V | yes |
| R8 | ScienceComponent AoE moodlets | UseTimeout used for AoE; area buffs never removed | AoeTimeout; undo buffs | V/I | yes |
| R9 | Daycare.GetExperience | `new Relationship` instead of `Get`, so liking is always 0 | Relationship.Get | V | yes |
| R10 | RepairableComponent.UpdateBreakage | Every wild party gets the keg multiplier with EP9 | Test for a JuiceKeggerParty | V | yes |
| R11 | GroupingSituation.ContinueRomanticSTC | Group cooldown in Minutes instead of Hours | Hours | V | yes |
| R12 | LightGameObject SetIntensityRoom/House | Copies `Intensity`, not mCustomIntensity | Copy mCustomIntensity | V | yes |
| R13 | PetEatPrey.Run | DisableInteractions never undone; a cancelled eat still destroys the prey and gives the buff | Undo; gate on the loop result | V | yes |
| R14 | School graduation | A stale None entry blocks the alarm for every later graduate | Re-arm / purge | V/I | yes |
| R15 | HorseJump RunJumpingCourse.DoPlan | Null route deref on first plan | `bReplan &&` guard | V | yes |
| R16 | SculptureComponent.StartMelting | Null Artist in the fire path; melt alarm overwritten | Guard; remove the old alarm | V | yes |
| R17 | TreasureComponent | Dried-food relic multiplier has no trait check; hoarder bonus before the null check | Add the check; reorder | V | yes |
| R18 | PortalComponent.GetFreeRoutingLane | Slot.None makes a no-exit loop (sim-thread hang) | Break on None | I (native) | yes |
| R19 | TutorSkillInteraction.ConfigureInteraction | Persisted balloon alarm armed at CreateInstance | Arm in PreSocialLoop | V/I | yes |
| R20 | Bookshelf_ReadToToddler.Run | StandardExit with no return | `return false` | V | yes |
| R21 | Newspaper.GetReadInteraction; VisitSituation | Shared Singleton mutation | New Definition per use | V/I | yes |
| R22 | GetSelfPickedUp, PresentTo, CatJungleGym parts, LitterBox reset, ServoBotRepair double count, Gem cut Gatherer | see agent notes | | V/I | yes |
| R23 | Lower | FireFighter null Handiness/CurrentJob, MakeMoveSimA seat2, ResortMaintenance loop, KittyBag self, ScienceComponent Import, Bar snack bowl refund, Daycare null order and `>`, PetBed `Count < 0`, Ocean GetInOcean orphan, tinker off-by-one, OccultImaginaryFriend, OccultPlantSim HUD, Medical tooltip, ComeAndSee first-match, BoobyTrap static tuning, ResortFoodStand WB default | | V/I | mixed |

## 3. Speedups (proven exact; sent to the perf agent for the census)

- LightGameObject.AutoLightsOff: return early before the lot query when the loop cannot act (runs on every room change).
- GenericPrivacySituation.ClearRoom.Init: `break` instead of `continue` when nobody needs shooing (saves up to 3 lot scans per privacy start).
- RepairableComponent.UpdateBreakage: run the party scan once.
- Daycare.GetExperience: no throwaway Relationship.
- CatJungleGym: cache Enum.GetValues.
- LeafPile GetClosestLeafPuddle: drop the unused GetWorldPosition.

## Batch 12

Built 2026-11-16, all default off, NOT RUN: see docs/590 "Batch 12" for the switch table and the leads not built.
