# External Sims 3 gameplay-bug leads (2026-11-15)

Research note, untracked (do not commit as is). Static web and source reading only, no game run. All descriptions are in our own words; no code was copied.

**Coverage tags:**
- **[covered: X]**: this project fixes or explicitly analysed the same bug.
- **[partial: X]**: the project handles one cause or a sibling bug.
- **[ext: simler90]**: simler90's Gameplay Core Mod, which our installer ships (`simler90-core`, docs/07 §2.1), already fixes it. These need no work unless we drop that mod or support players without it.
- **[new]**: nothing in the project covers it.

The coverage check grepped the EA type and member names in `docs/*.md` and `mods/ts3fixes/src`. A miss there does not prove absence, so confirm in the doc before starting work.

NRaas Overwatch and ErrorTrap run in the maintainer's own game, so the NRaas leads marked [new] are already worked around there. They are new for this project and for players without NRaas.

## Licences

| Source | Licence | Reuse rule |
|---|---|---|
| NRaas (Chain-Reaction/NRaas) | no licence file (all rights reserved) | behaviour spec only (AGENTS.md "Community code") |
| Smooth Patch (LazyDuchess) | no formal licence; the MTS page asks people to link the original | read only |
| simler90 Gameplay Core Mod | MTS: modified or alternative versions allowed on non-pay sites | spec preferred; credit the author |
| swiffyjk/TS3-Grocery-Fixes | GPL-3.0 | spec only, unless our code goes GPL |
| Destrospean/FixCareerDataTS3Mod | 0BSD | free |
| FoulPlay/houseboatfix, Kelvin-Ng jetpack fix, LazyDuchess CrossEyeFix, VMPYRC/TS3-Revamp | none | read only |
| OpenBarrio | GPL-3.0 | spec only |
| Buzzler (MTS), Arsil (MTS) | "do anything" / "free to modify" | free with credit |
| other single-fix MTS mods | none stated | read only |

## Counts

| Source | Leads | Covered | Partial | ext simler90 | New |
|---|---|---|---|---|---|
| Smooth Patch 2.x | 6 (all CAS, UI or performance; no gameplay fixes) | 1 (docs/21 §6 analysed it) | - | - | 5 (low value, non-gameplay) |
| NRaas Overwatch, ErrorTrap, StoryProgression | 105 | 11 | 13 | - | 81 |
| simler90 core changelog (MTS) | 51 | 3 (also ours) | - | 48 | 0 |
| MTS single-bug fix mods | 22 | 0 | 1 | 2 | 19 |
| Crinrict Sims 3 Help Blog | 31 | 0 | 6 | 1 | 24 (several duplicate NRaas leads) |
| Other (asileesims, thesimswiki, EA forum snippets) | 4 | 0 | 1 | 0 | 3 |
| GitHub repos | 8 | 1 (houseboat, docs/153 + installed fix) | - | - | 6 (1 is a feature only) |
| OpenBarrio (not already in docs/547-550) | 31 native "EA bug" comments; 0 gameplay | - | - | - | 31 native |

## Sources that could not be read

- **NRaas wiki:** nraas.net returned 403 and nraas.wikidot.com returned 404.
- **EA Answers HQ and forums.ea.com:** every page returned 403. The "Hero Approved fixing mods" list is known only from search snippets.
- **sims.fandom.com glitch pages:** 402.
- **Carl's Sims 3 Guide:** reachable, but it has no bug list, only reader comments.
- **Steam guide 1131162350:** 429.
- **Two MTS pages returned 401:** Get to Know Fix and Seasons Master Fix.

Not found: no project named "reseline" or any of its variants. The coordinator later clarified that the user meant Roslyn, which another agent handles.

---

## A. Top 30 [new], ranked by player impact

In the table, `#N` refers to the NRaas list (section C), `Bn`/`Cn`/`Dn` to the community lists (section E), and `GHn` to the GitHub repos (section B).

| Rank | Pack | System | EA type.member | Symptom | Cause | Source |
|---|---|---|---|---|---|---|
| 1 | base | careers | `Career.CareerLoc` (`RabbitHoleProxy`, `Enter/ExitSlots`), `CareerManager` | Sim cannot go to work after its rabbit hole was replaced or deleted; the work command disappears | job points at a missing or slotless rabbit hole; NRaas moves it to the nearest location | #25, C2 |
| 2 | LN | elevators | `ElevatorInterior.ElevatorPortalComponent.mAssignedSims` | Sims frozen waiting for an elevator that never comes | assigned-rider table keeps Sims that never ride | #2, C6 |
| 3 | base | doors | `CommonDoor.mLockType`/`mLockOwner` | door locked to "selected household" with a deleted owner; Sims cannot path and EA's lock test throws | owner not cleared on delete | #30 |
| 4 | base | household | `Household` members vs `SimDescription.mHousehold`; uninstantiated members | household Sim missing or invisible on the home lot, or pointing at another household | instantiation skipped; household back-pointer wrong | #11, #62 |
| 5 | base | venue staff | `RoleManager`, `RoleManagerTask`, `IRoleGiver.CurrentRole`, `Role.mSim` | bartenders and venue staff never re-staffed; `RoleManagerTask` throws every run | roles whose Sim is gone are kept | #96 (listed in docs/184, no fix) |
| 6 | base | opportunities | `Household.mCompletedHouseholdOpportunities` | an opportunity in progress is also listed as completed, so it cannot finish or be offered again | stale entry | #31 |
| 7 | SN/Uni | opportunities | `EP7_FortuneTellerCareer_HelpInvestigation_Police`, `EP9_Academics_StudyAt*` data | opportunities offered to Sims without the career or not enrolled | requirement list empty in EA data | #87 |
| 8 | base/store worlds | combo rabbit holes | `ComboRabbitHole.MetaAds`, theatre `DesiredSimFunction`; `IStadium` piece use list | nobody visits combo theatres or stadiums on their own; pro-game attendees not on the piece's use list | meta ads not on the building's lot; use list updated only on the building | #76, #72 (not in docs/551) |
| 9 | SN/base | alarms | `LunarCycleManager.PossiblySpawnZombie`, `MeteorShower.RandomMeteorShowerCallback`, duplicate or orphan alarms after load | extra zombie waves and meteor showers, repeated events | alarms duplicated on load | #67 (trick-or-treat and royalties already covered: docs/571, 574) |
| 10 | SN | werewolves/lunar | `LunarCycleManager` alarms, `OccultWerewolf.ForceHumanformation` | werewolves stuck in wolf form and not aging; zombies every day after a full-moon save | lunar state saved mid-event; alarms not persisted | #83, C20 |
| 11 | base | aging | `AgingManager` no-cake transition timers, `DayPassedCallback`, `CancelAgingAlarmsForSim` | early or double aging; midlife-crisis moodlet stuck; no-school worlds break graduation | dangling timers; EA day callback | #18, #85 |
| 12 | Gen | aging | aging loop | one bad Sim stops aging for every Sim after it, so the whole town stops aging | an exception inside the per-Sim loop | B2 |
| 13 | AMB/base | consignment | consignment static lists | unsold items never returned after 5 days; count goes negative and the buy menu locks | over-age or invalid entries kept | #78, D2 (docs/568 only notes the daily-sale gate) |
| 14 | LN | bartending pay | `kMadeMoneyBartending`, `BarAdvanced.MakeDrink` | bartending tips not credited unless they come from `MakeDrink` | funds added on one path only | #16 |
| 15 | base | gardening | `HarvestPlant.Seed`, growth state | plants lose their seed or get stuck and cannot be harvested; gardening abandoned partway | seed saved separately; bad state | #95, #40, C22 |
| 16 | base/IP | lot | `Lot.mSavedData` (fire, puddle, laundry, alarm managers), `ResortManager.mOwnerLot` | fires, puddles or laundry on that lot not handled; resort broken | sub-objects null after load | #48 |
| 17 | base | toddlers | toddler away from home with no teen-or-older | toddler left alone on a community lot | adults leave without the toddler | #12 (Overwatch spreading only) |
| 18 | Uni | university | `AcademicCareer.mDegree`, `AcademicCareer.sAcademicSims` | broken enrolment, cannot graduate, cannot return home | degree missing from the table; gone Sims in the static list | #17, C12 |
| 19 | base/AMB | careers | `Careers.LawEnforcement` (`ReportSubject`, `SimsInterviewed`, `SimsReported`), `Careers.Medical` | career tab shows work "in 1 hour"; work hours reset; errors | career keeps invalid SimDescription references | GH4 (0BSD), B8 |
| 20 | base | school | grade A path for a child with an absent parent | child reset to the lot edge after school every day | A-grade code path | C1 |
| 21 | Gen | boarding school | boarding school state | Sim shown as enrolled but stays home or never returns; misses graduation | not stated | C27 |
| 22 | ITF | Plumbots | `TraitChipManager.OnSimDisposed`, `DreamsAndPromisesManager.RemoveExistingLifetimeWish` | Plumbots lose trait chips and lifetime wish on reset or household split; motives freeze | lifetime wish not zeroed | B5 |
| 23 | ITF | opportunities | `Opportunities_EP11` weights and `TargetWorldRequired` | skill opportunity calls almost never come in the home world; some calls do nothing when answered | weight 500 vs 5-10; world test skipped when picking but enforced when answering | B3 |
| 24 | LN | mood lamp | mood-lamp buff | stuck moodlet blocks Try for Baby, travel, age-up and appearance changes | not stated | C19 |
| 25 | IP | resort | resort check-in | game freezes when a self-employed Sim checks into a resort | not stated | C29 |
| 26 | IP | diving | diving skill | Sim resets on the diving board | skill cut from 5 levels to 2; Sims above level 2 break | C18 |
| 27 | Gen | laundry | `WashingMachine.mWashState`, `Dryer.CurDryerState`, `Clothesline` | community washers, dryers and lines stay full or unusable | NPC loads abandoned mid-cycle | #4 |
| 28 | base | parties | `Sim.sOnLotChangedDelegates` (`NpcParty.Happening`) | errors on every lot change | handlers of a party whose host is gone stay subscribed | #42 (listed in docs/184) |
| 29 | base/Gen | outfits | `SimDescription` Career/Singed/Special outfits, `CareerOutfitIndex` | runaway lab-coat outfits pile up; corrupt imaginary-friend outfits; wrong work outfit | outfits never trimmed; index past the count | #5, #24 |
| 30 | Uni | motives | Juiced motive curve | drunk ("lush") state spreads over the town | EA tuning curve | #79 |

Next in line: #43 travel early-departure errors (listed in docs/184 as "N"); B20 whim-tracker exception on every lot switch; C11 stuck bills after University; B22 money tree stops producing for a Gatherer-trait Sim (IP); B13 harvester stuck in inventory (AMB); B4 Laser Rhythm-a-con challenge cannot complete at level 10 (ITF); B1 Late Night mood penalty after sleep (check `Well Rested` in `Instantiator.cs` first); #23 broken or over-max skills; #57 Sims listed as seasonal lot decor; #84 imaginary-friend trait learned randomly / doll lost; #50 werewolf look lost after load.

---

## B. Smooth Patch (LazyDuchess)

No public script source on GitHub; LazyDuchess's repos contain only the ASI and launcher. The script DLL was already decompiled for behaviour in docs/21 §6. Source for the list below: https://modthesims.info/d/658759 (changelog).

| Fix | Kind | Coverage |
|---|---|---|
| CAS music stops at random (`kCASMusicFix`) | CAS audio | [new], non-gameplay |
| CAS Sim voices sometimes silent | CAS audio | [new], non-gameplay |
| Buy/Build previews spin too fast (tied to frame rate) | UI | [new], non-gameplay |
| CAS clothing loads as you scroll; faster Create-a-Style and drag and drop | performance | [covered: docs/21 §6, coexists with casgrid] |
| 2.0 HF1 null refs in body hair / pet coat sections | Smooth Patch's own bug | n/a |
| ASI tick limiter, borderless window, FPS cap | performance | S3SS replaces it (docs/07) |

## GitHub repos

| # | Repo | Pack | EA type.member | Symptom | Coverage |
|---|---|---|---|---|---|
| GH1 | FoulPlay/houseboatfix | IP | `SimRoutingComponent.EnterHouseboatThroughGangway` | freeze getting on or off a houseboat (water planning off) | [covered: docs/153; installed fix and simler90] |
| GH2 | swiffyjk/TS3-Grocery-Fixes (GPL-3.0) | base | `RecipeShoppingRabbitHole.CreateRecipeItemList` | "Shop by Recipe" tab is empty when nobody has Cooking skill | [new], low |
| GH3 | Kelvin-Ng/the-sims3-jetpack-fix | ITF | `GameObject.DoRoute`, `Sim.RouteToPoint/RouteToLot` | jetpack equipped and unequipped over and over on short routes | [new], mostly a feature |
| GH4 | Destrospean/FixCareerDataTS3Mod (0BSD) | base | `Careers.LawEnforcement`, `Careers.Medical` | stale Sim references in career data | [new] (rank 19) |
| GH5 | LazyDuchess/TS3-CrossEyeFix | base | `LookAtMgr` | cross-eyed Sims (no minimum look-at distance) | [new], cosmetic |
| GH6 | VMPYRC/TS3-Revamp | many | XML tuning (Opportunities EP1-11, BoardingSchool, ImprovedProtestSituation...) | "bug fixes" not itemised; needs an XML diff | [new], unknown |
| GH7 | DandelionSprout/Sims3Gamemods | ? | - | mentions an unpublished Pestilence Plague fix and a Shoppingcenter 7000 relics-register routing error | [new], no detail |

**OpenBarrio** (GPL-3.0): no gameplay fixes; it keeps EA's bugs on purpose. Not in docs/547-550:
- the save-folder mover ("Error 16") at `0x004DC100`;
- the Test Lab tool;
- 31 native "EA bug" comments, only two of which docs/550 already lists: null-pointer use after a failed allocation in `packed_file.cpp` and `Animation`, uninitialised stack reads in the font parser and UI hit test, a SpeedTree read before the buffer when the count is 0, an iterator increment skipped around `0x6C9620`, an off-by-one near `0x4F4260`, and a nested retry in `packed_file.cpp:644` that reports success after a failure.

These are native leads for the crash work, not gameplay.

---

## C. NRaas (https://github.com/Chain-Reaction/NRaas/blob/master/<path>)

Path short forms: `OW` = NRaasOverwatch/OverwatchSpace, `ET` = NRaasErrorTrap/ErrorTrapSpace/Checks, `SP` = NRaasStoryProgression/StoryProgressionSpace, `NR` = NRaas/Helpers.

The 650 ErrorTrap Dereferences/Performers files all follow one pattern: after load they clear fields, delegates, alarms and listeners that point at destroyed objects or deleted SimDescriptions. [covered: docs/184 §1, 205, 522, 529, deadrefsweep]

| # | File | Pack | EA type.member | Symptom | Coverage |
|---|---|---|---|---|---|
| 1 | OW/Alarms/CleanupConcert | Showtime | `ShowVenue.EndPlayerConcert`, `SearchLight.mSMC` | search lights and player concerts never end | [new] (docs/551 covers combo lights only) |
| 2 | OW/Alarms/CleanupElevators | LN | `ElevatorPortalComponent.mAssignedSims` | Sims frozen at elevators | [new] |
| 3 | OW/Alarms/CleanupInventory | base | inventory stacks, `InventoryItem.Object` | ghost or unusable items, wrong counts | [partial: familyinvghost] |
| 4 | OW/Alarms/CleanupLaundromat | Gen | washer, dryer, clothesline state | community laundry stays unusable | [new] |
| 5 | OW/Alarms/CleanupOutfits | base/Gen/IP | Singed/Career/Special outfits, `CareerOutfitIndex` | runaway outfits, corrupt outfits | [new] |
| 6 | OW/Alarms/CleanupRelationships | base | `Relationship.sAllRelationships`, `SimDescription.mPartner` | duplicate relationship rows; partner pointing at a deleted Sim | [new] |
| 7 | OW/Alarms/CleanupSituations | Gen | `FieldTripSituation` | field trips never end, children held | [partial: fieldtripadult docs/565] |
| 8 | OW/Alarms/CleanupSkillModifiers | base/SN | `SkillManager.mSkillModifiers` | permanent super-fast skill gain | [partial: trainboost docs/569] |
| 9 | OW/Alarms/CleanupVehicles | base/AMB/IP/Uni | `ParkingSpace.mReservedVehicle`, `MooringPost`, NPC cars | orphan cars and boats; parking spaces stay reserved | [covered: docs/544, 257] |
| 10 | OW/Alarms/ResetAllGnomes | SN | `MagicGnomeBase.IsAngry` | gnome stuck in prank state | [new], low |
| 11 | OW/Alarms/RecoverMissingSims | base | household instantiation | household Sim missing or invisible | [new] (spreading only, docs/145) |
| 12 | OW/Alarms/RecoverStrandedToddlers | base | - | toddler alone on a community lot | [new] (spreading only) |
| 13 | OW/Alarms/ResetCheck | base | `AgeUpNpcSituation`, `AgingState` | inactive Sim stuck in age-up or past maximum age | [partial: docs/503, 512] |
| 14 | OW/Alarms/RestartDigitalPhotos | store | `MultiImageObject.mAlarmNextSlide` | digital frames stop cycling | [new], cosmetic |
| 15 | OW/Alarms/StuckCheck | base | routing, `Lot.IsRoomHidden` | stuck Sims | [covered: docs/552, 184/185, 576] |
| 16 | OW/Loadup/BarMoonlightingListener | LN | `kMadeMoneyBartending`, `BarAdvanced.MakeDrink` | bartending money lost | [new] |
| 17 | OW/Loadup/CleanupAcademics | Uni | `AcademicCareer.mDegree`, `sAcademicSims` | enrolment broken | [new] |
| 18 | OW/Loadup/CleanupAging | base | `AgingManager` timers | early or double aging; Grim, raccoons and deer age | [new] |
| 19 | OW/Loadup/CleanupAutonomy | base | `Sim.mAutonomy`, `AutonomyManager.sInstance` | Sims do nothing on their own | [new] |
| 20 | OW/Loadup/CleanupBadPostures | base | `Posture.PreviousPosture` cycle | Sim frozen | [covered: posturepopcycle] |
| 21 | OW/Loadup/CleanupBogusNewsArticles | base | `News.HotSpotArticle.mLot` | errors | [new], minor |
| 22 | OW/Loadup/CleanupBonehilda | SN | `BonehildaCoffin.mBonehildaSim` | Bonehilda never comes | [new] |
| 23 | OW/Loadup/CleanupBrokenSkills + Preload/FixPetFatigue | many | skill data, `SkillLevel` > max, `sPetSkillFatigueTuning` | skill errors; over-max skills; pets never tire | [new] |
| 24 | OW/Loadup/CleanupCareerOutfitIndices | base | `CareerOutfitIndex` | wrong or missing work outfit | [new] |
| 25 | OW/Loadup/CleanupCareers | base | `Career.CareerLoc`, `Occupation.Coworkers` | cannot go to work | [partial: coworkers docs/529/205; relocation is new] |
| 26 | OW/Loadup/CleanupCauseEffect | ITF | `CauseEffectService` ids, `TimeStatueRecordData` | Time Almanac errors (C28) | [new] |
| 27 | OW/Loadup/CleanupCelebrityLevel | LN | `CelebrityManager.mLevel` out of range | errors | [new] |
| 28 | OW/Loadup/CleanupCellPhones | base | `PhoneCell` | duplicate phones | [new] |
| 29 | OW/Loadup/CleanupCollecting | base/AMB/IP | `Collecting` dictionaries, gem cut | journal errors; cut gems not recorded (C21) | [new] |
| 30 | OW/Loadup/CleanupCommonDoor | base | `CommonDoor` lock owner | door blocks Sims | [new] |
| 31 | OW/Loadup/CleanupCompletedOpportunities | base | `mCompletedHouseholdOpportunities` | active opportunity also marked completed | [new] |
| 32 | OW/Loadup/CleanupDnPExportData | AMB | `SimDescription.DnPExportData` | stale wishes re-applied | [covered: docs/210] |
| 33 | OW/Loadup/CleanupDrinkRecipes | ? | `HotBeverageMachine.SimDrinkRecipes` | leak, errors | [new] (leak noted in docs/191) |
| 34 | OW/Loadup/CleanupFakeMetaAutonomy | base | `FakeMetaAutonomy` pool | phantom townies | [new] |
| 35 | OW/Loadup/CleanupFutureDescendantService | ITF | `DescendantHouseholdsMap` | duplicate descendants | [new] |
| 36 | OW/Loadup/CleanupGems | AMB | `Gem.mCutName == ""` | errors | [new], minor |
| 37 | OW/Loadup/CleanupGenericPerformanceObjects | Showtime | `GenericPerformanceObject.SimOwner` | props lose their owner | [new] |
| 38 | OW/Loadup/CleanupGigs | Showtime | `PerformanceCareer.mSteadyGigs` | gig errors (C5) | [new] |
| 39 | OW/Loadup/CleanupGroupScienceStation | Uni | `mActiveMeteorites` | stuck effects | [new] |
| 40 | OW/Loadup/CleanupHarvestPlant | base/SN | `HarvestPlant` state | stuck plant | [new] |
| 41 | OW/Loadup/CleanupHomework | base | `Homework`, `School.OwnersHomework` | duplicate homework | [new] |
| 42 | OW/Loadup/CleanupHostlessNPCParties | base | `sOnLotChangedDelegates` | errors on every lot change | [new] |
| 43 | OW/Loadup/CleanupInvalidEarlyDepatures | AMB | `GameStates.sTravelData.mEarlyDepartures` | travel errors | [new] |
| 44 | OW/Loadup/CleanupInvalidGameObjectRelationships | base | `SimDescription.GameObjectRelationships` | Fixup fails | [covered: docs/522] |
| 45 | OW/Loadup/CleanupKnownCompositions | Showtime | `SingingInfo` lists | errors | [new] |
| 46 | OW/Loadup/CleanupKnownRecipes | base | `Cooking.KnownRecipes` | menu errors | [new] |
| 47 | OW/Loadup/CleanupMascotServiceRequest | Uni | mascot service requests | stuck request | [new] |
| 48 | OW/Loadup/CleanupMissingLotSavedData | base/IP | `Lot.mSavedData` | lot managers missing | [new] |
| 49 | OW/Loadup/CleanupMountedFish | base | `MountedFish.mFish` | mounted fish broken | [new] |
| 50 | OW/Loadup/CleanupOccult | SN | `OccultWerewolf.FixupOccult` | werewolf look lost | [new] |
| 51 | OW/Loadup/CleanupOpportunityHistory | base | `OpportunityHistory` | export throws | [covered: docs/210] |
| 52 | OW/Loadup/CleanupPatterns | base | `Pattern.sPatternCategories` | CAST errors | [new], minor |
| 53 | OW/Loadup/CleanupPetAdoption | Pets | `PetAdoption.sNeighborAdoption` | adoption errors | [new] |
| 54 | OW/Loadup/CleanupPlumbbobs | base | `PlumbBob` | several or charred plumbbobs | [new] |
| 55 | OW/Loadup/CleanupReactionBroadcasters | base | lot reaction lists | repeated reactions | [new] |
| 56 | OW/Loadup/CleanupRelationships | base | `sAllRelationships` gone keys | errors | [covered: docs/535] |
| 57 | OW/Loadup/CleanupSeasonalLotMarkers | Seasons | `SeasonalLotMarker.mSeasonalItems` holding Sims | Sims treated as decor (effect inferred) | [new] |
| 58 | OW/Loadup/CleanupServicePools | base/IP | `Service.mPool`, `ResortWorker` | services never come | [partial: docs/504] |
| 59 | OW/Loadup/CleanupShapeMultipliers | base | `mShapeDeltaMultiplier` | body changes explode | [partial: docs/569] |
| 60 | OW/Loadup/CleanupShortTermContext | base | `Relationship.mSTC` | social errors | [new] |
| 61 | OW/Loadup/CleanupShowStage | Showtime | `ShowStage.mSlaveList` | stage stuck | [new] |
| 62 | OW/Loadup/CleanupSimHousehold | base | `SimDescription.mHousehold` | household mismatch | [new] |
| 63 | OW/Loadup/CleanupSituationEvents | base | `EventTracker` listeners of dead situations | callbacks throw | [partial: docs/576] |
| 64 | OW/Loadup/CleanupSituationInteractions | base | interaction callbacks into gone situations | Sim stuck | [partial: docs/576] |
| 65 | OW/Loadup/CleanupSituations | base | grouping/hosted/privacy situations | Sims stuck in a group; bathroom locked | [partial: docs/554, 576, 577, 503] |
| 66 | OW/Loadup/CleanupSkillGain | base | `SkillManager.mOverallModifier`, `StopSkillGain` | skill gain continues while idle | [new] |
| 67 | OW/Loadup/CleanupTimers | base/SN/Seasons | duplicate alarms (zombie, meteor shower) | repeated events | [partial: docs/571, 574] |
| 68 | OW/Loadup/CleanupTraits | base | `TraitManager` unknown traits | errors | [new] |
| 69-71 | OW/Loadup/ClearInvalid{Burglar,SocialWorker,TimeTraveler}ServiceRequests | base/ITF | `mLotsRequested` | stuck service requests | [new] |
| 72 | OW/Loadup/ComboStadiumFix | base | stadium piece use list | pro-game attendees wrong | [new] |
| 73 | OW/Loadup/CorrectThumbnailHelper | Gen | prom thumbnails | errors | [new], minor |
| 74 | OW/Loadup/DisableFreeVacations | Gen | `ParentsLeavingTownSituation` | household broken | [covered: docs/210] |
| 75 | OW/Loadup/FixActiveTopics | base/LN/AMB | `ActiveTopic.AddToSim` | skill-unlocked socials missing | [new] |
| 76 | OW/Loadup/FixComboRabbitHoleMetaAds | base | `ComboRabbitHole.MetaAds` | empty combo venues | [new] |
| 77 | OW/Loadup/FixComboRabbitHoleOpportunities | base | `Opportunity.TargetObject` | opportunity step never completes | [covered: comboopp docs/551] |
| 78 | OW/Loadup/FixConsignment | AMB | consignment lists | items stuck or lost | [new] |
| 79 | OW/Loadup/FixLushEpidemic | Uni | Juiced motive curve | town-wide drunkenness | [new] |
| 80 | OW/Loadup/FixPetSurfaceComponent | no Pets | `Counter.PetSurfaceComponent` | errors | [new], minor |
| 81 | OW/Loadup/FixPreEP10Bars | IP | `BarAdvanced.mInteractions` | old bars broken | [new] |
| 82 | OW/Loadup/FixToddlerChat | base | Chat interaction | toddlers have no Chat (C31) | [new], minor |
| 83 | OW/Loadup/HandleLunarCycle | SN | `LunarCycleManager` | werewolves and zombies stuck | [new] |
| 84 | OW/Loadup/RepairImaginaryFriends | Gen | `ImaginaryFriendHiddenTrait`, doll id | trait learned randomly; doll lost | [new] |
| 85 | OW/Loadup/ReplaceAgingAlarm | base/Gen | `AgingManager.DayPassedCallback` | stuck midlife moodlet; no-school graduation broken | [new] |
| 86 | OW/Preload/FixFishData | store | `Fish.sFishData` Box | error fishing up a chest (related: B14) | [new] |
| 87 | OW/Preload/FixOpportunities | SN/Uni | opportunity requirements | wrong offers | [new] |
| 88 | OW/Preload/FixOrderPizza | LN/IP | `BarAdvanced.MakeFood` | ordering pizza at the bar broken | [new] |
| 89 | OW/Preload/FixTestBarBrawl | LN | `TestBarBrawl` | children fight adults | [new] |
| 90 | ET/CheckBakeSaleTable | Gen | `DemandTable` | errors | [new] |
| 91 | ET/CheckBook | base | `Book.mBookId` | errors | [new], minor |
| 92 | ET/CheckChessTable | base | `mSimsPlayingForChessRank` | ranked chess errors | [new] |
| 93 | ET/CheckFish, CheckFishingSpot | base | `Fish.mData` | fishing errors | [new] |
| 94 | ET/CheckGameObject | base | orphan inventory object | ghost items | [partial: familyinvghost] |
| 95 | ET/CheckIngredient, CheckHarvestPlantFrostable, CheckPlantLargeTree | base/Seasons | `HarvestPlant.Seed` | plant unusable | [new] |
| 96 | ET/CheckIRoleGiver, CheckRoleManager, CheckRoleManagerTask | base | roles | venue staff gone; task throws | [new] |
| 97 | ET/CheckMoondial | SN | lunar phase | errors | [new], minor |
| 98 | ET/CheckPicnicTable | base | `PicnicTable.mInsideSeats` | cannot sit | [new] |
| 99 | ET/CheckVehicle | base | `CarRoutingComponent` | car cannot drive | [covered: docs/513] |
| 100 | NR/Corrections.RemoveFreeStuffAlarm | LN | `CelebrityManager.RemoveFreeStuffAlarm` | EA method throws | [new] |
| 101 | SP/.../ServiceCleanupScenario | base | NPC household shared inventory | null or bloated | [new] |
| 102 | SP/.../PregnancyCheckScenario | base | `Pregnancy.mHourOfPregnancy` | pregnancy stuck | [partial: docs/558] |
| 103 | SP/.../UpdateCareerScenario | base | `Occupation.Coworkers` null | errors | [new], minor |
| 104 | SP Money/SellBikeScenario | Gen | bicycle with toddlers | routing error | [new], low |
| 105 | NR/Corrections.CorrectSaveGameLocks | base | `Gameflow.mSaveGameLocks` | cannot save | [covered: docs/25] |

## D. NRaas's own bugs seen while reading

Only for a PR if they affect us (memory rule).

- **CleanupRelationships (loadup):** removes inner keys from the outer table.
- **CleanupReactionBroadcasters:** the exit-callback duplicate check uses the enter lookup.
- **ClearInvalidBurglarServiceRequests:** adds a missing lot to the remove list twice; all three ClearInvalid* files log "Time Traveler".
- **CheckBook:** its null test looks inverted.

---

## E. Community lists

### simler90 Gameplay Core Mod changelog (https://modthesims.info/d/659969)

Everything below is [ext: simler90], already fixed in the installed core mod, except where marked "also ours".

`Punishment.IsSimGoingToSchoolOrWorkOrTraveling` (grounded child counts as sneaking out; also ours: GenerationsLogic.cs) · `HouseParty.GetPartyOutcome`/`IsGreatParty` (also ours: PartyFixes.cs) · `CampaignFundraiser.ShowPartyEndTNS` · `Autonomy.OnceADayCallback` (inactive households never pay bills) · `KillSim`/`Urnstone.FinalizeSimDeathRelationships` (mourning never applies) · `Urnstone.CheckForAbandonedChildren` (children deleted) · `LotManager.CleanUpLotsAndInventories` (stranded babies and toddlers) · `PetAdoption.PutPetBackToPool` (homeless young never age; docs/534 is a different bug) · SP `KillSim.CanKillSim` (babies killed; C8) · SP `MoveInWithPartner`, `AddSim`/`GenerateOffspring` · `SocialCallback.OnKissMakeup` · `PromSituation.HasNoDateOrDateIsNotPartner` · `OccultVampire.SunsetCallback` (fast skills) · `FireFightingJob.FindIsolatedFirePosition` (also analysed: docs/570) · `PoolLounger.LoungerRelax` · `Gift.OnRemovalFromMailbox` (thank-you notes; B6) · Genealogy uncle tests (lost travelling household) · `CareerManager.OnLoadFixup` (career history wiped on travel) · `Sim.Placeholder.DoDematerialize` (playable ghosts deleted) · `AgingState.MergeTravelInformation` (double age-up) · `AgingManager.SimIsOldEnoughToTransition` · `Styling.DisplayCAS` (age reset) · `FutureDescendantService.UpdateHouseholdHomeLot` · `GameStates.ImportTravellingHousehold` (romance reputation) · romance not ended on death · `Mailbox.CreateRegularBillForHousehold` (future homes billed) · vehicle sale money to the wrong household · `ResortManager.TryHaveWedding` · `GoOutToYard` dorm loop · swimwear dress code · `Sim.GetCareerCar` limo pile-up · career outfit naked (`GetCategoryAndIndexToUse`) · abandoned vehicles · disposed inventories not paid · `BandInstrument.Serenade` stuck · `ImprovedProtestSituation` loops (B10) · services idle off camera (`IsSufficientlyOnScreenForHighLODSimulation`) · `AcademicDegreeManager.GetMiniDegrees` pile-up · stray pets stuck · active firefighter greeting · `Medical.VictimTest` picks pets · `BedMultiPart.ClaimOwnership` · `SnubManager.OnSnubTimeout` · jealousy and romance visibility · `OnAskToMoveInAccepted` from tourist households · `Household.Add` (ITF households lost) · `UnderwaterCave.TentacleWave` resets every diver (IP) · `PerformerPerformForTips` loop · `Genetics.InheritBodyShape` · travel skill-data loss (`NectarSkill.ImportContent`).

### MTS single-bug fix mods

| # | Pack | Symptom | Source | Coverage |
|---|---|---|---|---|
| B1 | LN | each sleep leaves a mood penalty; inactive Sims' social drains | https://modthesims.info/d/432449 (Buzzler, do anything) | [new?] check `Instantiator.cs` "Well Rested" |
| B2 | Gen | one bad Sim stops all aging | https://modthesims.info/d/447628 | [new] |
| B3 | ITF | opportunity call weights and world test | https://modthesims.info/d/560635 | [new] |
| B4 | ITF | Laser Rhythm-a-con challenge cannot complete at level 10 | https://modthesims.info/d/606740 | [new] |
| B5 | ITF | Plumbot trait chips and lifetime wish lost | https://modthesims.info/d/678484 | [new] |
| B6 | Gen | thank-you notes never appear | https://modthesims.info/d/620548 | [ext: simler90] |
| B7 | ITF | rift geyser reset in non-English games | https://modthesims.info/d/657211 | [new] |
| B8 | AMB/LN | Law Enforcement and Medical career data broken | https://modthesims.info/d/690810 | [new] |
| B9 | Uni | great-party wishes ignore the new outcomes | https://modthesims.info/d/556948 | [partial: PartyFixes.cs] |
| B10 | Uni | protest loops | https://modthesims.info/d/566803 | [ext: simler90] |
| B11 | SN | Restoration spell resets fresh fridge food | https://modthesims.info/d/556946 | [new] |
| B12 | SN | Proper trait decoration moodlet too weak | https://modthesims.info/d/580415 | [new], tuning |
| B13 | AMB | harvester stuck in inventory | https://modthesims.info/d/679470 | [new] |
| B14 | base | fishing never yields chests | https://modthesims.info/d/682060 | [new], tuning |
| B15 | AMB/LN | SimBots cannot be built in Bridgeport | https://modthesims.info/d/437402 | [new], tuning |
| B16 | ITF | Laser Rhythm-a-con advertises 0 fun | https://modthesims.info/d/517669 | [new], tuning |
| B17 | Gen | toddler falls cancel the walk | https://modthesims.info/d/685924 | [new] |
| B18 | base | Pick Up Child takes a long detour | https://modthesims.info/d/678824 | [new] |
| B19 | base | age-up hair or eyebrow glitch (`OutfitUtils.GetNakedKey`) | https://modthesims.info/d/675634 | [new] |
| B20 | base | whim-tracker exception on every lot switch | https://modthesims.info/d/645767 | [new] |
| B21 | LN | celebrity stars after one chat; butler vanishes; excavation routing | https://snootysims.com/wiki/sims-3/the-sim-3-late-night-fixes/ | [new] |
| B22 | IP | money tree stops producing for a Gatherer-trait Sim | EA "Hero Approved" list (search snippet) | [new] |

### Crinrict Sims 3 Help Blog (https://sims3.crinrict.com/en/category/bugs)

Symptoms only; the blog names no EA types.

| # | Pack | Symptom | Coverage |
|---|---|---|---|
| C1 | base | A-grade child reset after school | [new] |
| C2 | base | Sim cannot go to work | [partial] = #25 |
| C3 | base/LN | Sim will not use its bed | [new] |
| C4 | base | inventory items vanish | [partial: familyinvghost] |
| C5 | Showtime | steady gigs | = #38 |
| C6 | LN | elevators | = #2 |
| C7 | base | cannot marry | [new] |
| C8 | base | babies die in Story Progression | [ext: simler90] |
| C9 | base | stuck social jigs block areas | [new] (CleanupJigs crashed NRaas) |
| C10 | Uni | protest skill multiplier saved permanently | [partial: docs/569] |
| C11 | Uni/IP | stuck bills after University | [new] |
| C12 | Uni | cannot return from University | ~ #17, [new] |
| C13 | base | mailbox and online dating reset | [partial: docs/528] |
| C14 | base | greeting cards cause route failures | [new] |
| C15 | Seasons-less | wedding issues | [new] |
| C16 | Seasons-less | space rock and telescope reset | [new] |
| C17 | base | opportunities at multi-rabbit-hole buildings | [covered: docs/551] |
| C18 | IP | diving board reset | [new] |
| C19 | LN | mood lamp moodlet | [new] |
| C20 | SN | lunar and werewolf issues | = #83 |
| C21 | WA | gems and metals | = #29 |
| C22 | base | gardening abandoned | = #95 |
| C23 | LN | photo booth adds relationships | [new] |
| C24 | Showtime | performer resets beside the stage | [new] |
| C25 | WA | fish vanish after travel | [new] |
| C26 | Pets | hunting and racing skill not learned | [new] |
| C27 | Gen | boarding school | [new] |
| C28 | ITF | Time Almanac | = #26 |
| C29 | IP | resort freeze | [new] |
| C30 | base | rabbit-hole doors near the lot edge | [partial: docs/505] |
| C31 | Gen | toddler store objects reset skills | [new] |

The C pages are at `https://sims3.crinrict.com/en/<yyyy>/<mm>/<slug>.html`; the slugs are in the research agent's notes.

### Other sources

| # | Pack | Symptom | Source | Coverage |
|---|---|---|---|---|
| D1 | Pets | horse riding certificate, prize money and foal bugs | https://asileesims.wordpress.com/2011/11/10/the-sims-3-pets-list-of-glitches-bugs-errors-with-some-solutions/ | [new] |
| D2 | base/AMB | consignment items not returned | https://www.thesimswiki.com/wiki/Bugs | = #78 |
| D3 | WA | count-based wish progress reset on vacation | https://www.thesimswiki.com/wiki/Bugs | [new] |
| D4 | Gen | 8 Sims + toddler with imaginary friend stops aging | EA forum (search snippet) | [new] |
