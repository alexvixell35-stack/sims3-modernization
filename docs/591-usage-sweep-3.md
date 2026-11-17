# 591. Usage sweep 3 (ranks 201-378 and callees of ranks 21-100)

> Posted upstream with the switches listed under "In-game results" at the end. Sections on other leads or switches are kept as research notes; those switches are not part of this change.


Date: 2026-11-16. Static read; the fixes built from it are proven in game (end of this doc). Follows docs/589 and docs/590. `*.Mimics.*` types were skipped because they have no code of their own.

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

## Built fixes (from the docs/590 batch table)

EA bug fixes as EA intended, household included. Each switch is a ts3fixes Mono swap with a pure decision, a `<Name>Check`
(the hook's own handles, a live-path assert, controls) and a `<switch>-check` repro; 3 slots each (fixed / seen with the
switch off / errors). All default off.

| § | lead | switch | slots | fix |
|---|---|---|---|---|
| 18 | 591 C1 | `awkwardscience` | 3115-3117 | the Socially Awkward science gain bonus applies to Science only |
| 19 | 591 C2 | `chessdefeated` | 3118-3120 | the highest-ranking defeated chess opponent is the strongest one, stored with its rank |
| 20 | 591 C4 | `privacyothers` | 3121-3123 | a Sim who left a private room's lot gets a new room check when it comes back |
| 21 | 591 C5 | `gigmaster` | 3124-3126 | Gig Master completes at the number of gigs its text names |
| 22 | 591 C6, 592 S4 | `intmultiple` | 3127-3129 | RandomUtil.GetIntMultiple can return its top multiple (sales up to 80%, idles up to 30 min) |
| 23 | 591 C7 | `doorprivacynull` | 3130-3132 | a privacy situation with no sync target no longer strips the privacy of later doors |
| 24 | 591 R1 | `leafburnfail` | 3133-3135 | a leaf pile whose fire never started is usable again |
| 25 | 591 R2 | `catchpreyodds` | 3136-3138 | snakes and turtles are a sure catch only with Slither Stalker, like lizards |
| 26 | 591 R3 | `picnicants` | 3139-3141 | a packed picnic basket loses its ant alarm and the ant effect is disposed |
| 27 | 591 R5 | `loyaldoglay` | 3142-3144 | a Loyal dog's lie-down near its human targets the dog so its test passes |
| 28 | 591 R6 | `visitlisteners` | 3145-3147 | a removed visit host's listeners are not re-added after a load |
| 29 | 591 R7 | `sciencefxstop` | 3148-3150 | a science outcome's old effect set is stopped before a new one starts |
| 30 | 591 R9 | `daycareliking` | 3151-3153 | daycare experience uses the child's real liking for the owner |
| 31 | 591 R10 | `kegbreakage` | 3154-3156 | the juice kegger breakage multiplier applies only at a juice kegger party |
| 32 | 591 R11 | `romancecooldown` | 3157-3159 | the bad-date cooldown in a group of three or more lasts hours, not minutes |
| 33 | 591 R12 | `lightcustom` | 3160-3162 | a room or house custom light level copies the chosen level |
| 34 | 591 R13 | `eatpreycancel` | 3163-3165 | a cancelled or failed prey eat keeps the prey and re-enables its interactions |
| 35 | 591 R16 | `meltalarm` | 3166-3168 | an ice sculpture with no known artist melts without a throw and a restart replaces its melt alarm |
| 36 | 591 R21 | `newspaperdef` | 3169-3171 | reading the newspaper autonomously no longer renames the newspaper menu root |

## In-game results (Bridgeport combined soaks, each switch off then on in one load)

| Switch | Off | On | Runs off BROKEN / on FIXED | Runs (phase-…) |
|---|---|---|---|---|
| `awkwardscience` | BROKEN | FIXED | 1 | 20261116-0234 |
| `catchpreyodds` | BROKEN | FIXED | 1 | 20261116-0234 |
| `chessdefeated` | BROKEN | FIXED | 1 | 20261116-0234 |
| `daycareliking` | BROKEN | FIXED | 1 | 20261116-0234 |
| `doorprivacynull` | BROKEN | FIXED | 1 | 20261116-0505 |
| `eatpreycancel` | BROKEN | FIXED | 1 | 20261116-0234 |
| `gigmaster` | BROKEN | FIXED | 1 | 20261116-0234 |
| `intmultiple` | BROKEN | FIXED | 1 | 20261116-0234 |
| `kegbreakage` | BROKEN | FIXED | 1 | 20261116-0234 |
| `leafburnfail` | BROKEN | FIXED | 1 | 20261116-0234 |
| `lightcustom` | BROKEN | FIXED | 1 | 20261116-0234 |
| `loyaldoglay` | BROKEN | FIXED | 1 | 20261116-0234 |
| `meltalarm` | BROKEN | FIXED | 1 | 20261116-0234 |
| `newspaperdef` | BROKEN | FIXED | 1 | 20261116-0234 |
| `picnicants` | BROKEN | FIXED | 1 | 20261116-0234 |
| `privacyothers` | BROKEN | FIXED | 1 | 20261116-0234 |
| `romancecooldown` | BROKEN | FIXED | 1 | 20261116-0234 |
| `sciencefxstop` | BROKEN | FIXED | 1 | 20261116-0234 |
| `visitlisteners` | BROKEN | FIXED | 1 | 20261116-0234 |

- [verified] The verdicts, controls ok and members found in the runs listed (`smallfix.txt` of each run).
- [inferred] How often each case happens in normal play: the checks call the same decision functions as the hooks.
