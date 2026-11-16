# 587. Expansion and stuff pack function sweep (2026-11-15)

Static only: decompiled scripts plus shipped tuning XML, no game run, nothing built. This is the EP/SP half of the docs/586 coverage sweep (upstream issue #422 makes EP/SP fixes the contributor's track). A parallel pass reads the base-game types from the same map, so the two passes do not overlap.

## 1. Method

1. **Pack tag.** `tools/static/ep-pack-classify.py` (new) tags every row of `docs/data/function-coverage-1115.csv` with a pack: EP1-EP11, store, or base. It matches a keyword table on namespace and type name first, then falls back to `ProductVersion.EPn` references in the type's own file. Sims3StoreObjects types are tagged store. The tag is a ranking aid, not ground truth (base types such as Fireplace are tagged EP2 and were dropped by hand). Result: 2,800 uncovered non-base types.
2. **Scanners.** The rea2 scanners (`pick-loop-scan.py`, `random-empty-scan.py`, `foreach-mutate-scan.py`, `managed-lifecycle-scan.py`) and `unset-field-push-scan.py` were rerun over all three gameplay assemblies. Their hits were added to the CSV's `hits` column (ReSharper, semgrep, InferSharp and Roslyn from the analysis/roslyn-scan-1115 PASS=2 outputs). ReSharper and Roslyn were not rerun, to stay under the RAM cap during a game test; their outputs were current.
3. **Rank.** Rank = (hits + extra) × (1 + reach). The top 105 types with any hit were taken, plus the 45 largest reach ≥ 2 types with no scanner hit (the scanners miss their shapes). Hand-picked big occult and pack managers that docs only named in passing were added too: OccultGenie, OccultVampire, OccultPlantSim, OccultMermaid, OccultUnicorn, HorseManager, EquestrianCenter, AcademicCareer, UniversityWelcomeKit, Houseboat, AlienUtils, TimePortal, FutureDescendantService and Jetpack. Total: 150 types, about 83,000 decompiled lines.
4. **Hand read.** Five readers, one per pack group, read every type function by function against one checklist:
   - null or stale references;
   - unreleased listeners, alarms and effects;
   - copy-paste and wrong-field errors;
   - inverted conditions;
   - endless, O(n²) and first-item loops;
   - state that is saved but never loaded;
   - exceptions in common paths.

   Every reported lead was then re-read at the cited lines before it was accepted here.
5. **Dedupe.** Each finding was grepped in `mods/ts3fixes/src` of every worktree, `tools/harness/sets/proven-fixes.txt`, all `docs/` (including 571 and 579-586) and `docs/data/ilpatch-convert.csv`.
6. **simler90 Gameplay Core Mod UPDATE242.** Each type was decompiled with `ilspycmd -t` from both the mod's DLL copies and EA's DLLs, then diffed. Two-line diffs are attribute-order noise. Every finding below was checked in the mod's copy of the method.
7. **Shipped tuning.** `tools/static/tuning-xml-dump.py` (new) dumps the tuning XML (type `0x0333406C`) out of `GameplayData.package`, read-only. Findings that depend on `[Tunable]` values were checked against the shipped values, not the code defaults. This removed one false positive (HorseManager, §4).
8. **Native RE.** None of the failing paths turns on a native call's behaviour. The one native object involved is `VisualEffect`: finding 8 leaks its handles, and Ghidra adds nothing to that. So no REA pass was needed.

## 2. Types read (150)

- **Pets (EP5):**
  - Buffs, food and behaviour: BuffExhausted, MakeGourmetFoodForPet, EatHarvestablePet, PetStartleBehavior, PetWatchableFoodInteractionHelper, FoodScrap.
  - Toys and objects: CatCondo, DogBall, ChewToy, CatToy, CatToyMouse, CatToyWallDancer, HayStack, DoggySwimHere, DogDigHole.
  - Minor pets: Bird, MinorPetSpawner, MinorPetData, MinorPetCarryPosture.
  - Postures and managers: PetPoolPetCreator, PetstoreRegister, SocialWorkerPetPutUpSituation, UnicornPool, LeadingHorsePosture, PetSittingOnGroundPosture, CarryingPetPosture, PetCarrySystem, PetManager, HorseBeingLedPosture.
  - Big types: OccultUnicorn, HorseManager, EquestrianCenter.
- **Supernatural and Late Night (EP7, EP3):**
  - Objects: MechanicalBull, MagicBroomArena, NectarRack, NectarBottle, MetalMineralPotionDisplayCase, CrystalFlower, BladderFlowPotion, FairyGold, FairySetBoobyTrap.
  - Gnomes: MagicGnome, MagicGnomeEvil, MagicGnomeElvis.
  - Buffs: BuffVampireBite, BuffPermaZombie, BuffMummysCurse.
  - Bars and hot tubs: BartenderService, BartenderServiceSituation, HotTub4Seated, HotTubGrotto, HotTubGrottoCeleb.
  - Occults: OccultMummy, OccultMagicPointSystem, CASPlantSimData, OccultWitch, OccultGenie, OccultVampire, OccultPlantSim.
- **University, Generations and Ambitions (EP9, EP4, EP2):**
  - University: AcademicSituationBase, AcademicTextBook, AcademicDegreeStaticData, AcademicCareer, UniversityWelcomeKit, UniversityMascotRole, StudyWithGroup, MiniDegree, PoliceBreakJuiceKeggerPartySituation, TailgatingParty, PingPongTable, GeneSplicingExperiment.
  - Generations: AfterschoolActivity, DaycareToddlerDropoffSituation, DaycareWorkdaySituation, DaycareChildMonitor, WeddingCake, WeddingParty, ScavengerManager, VanityDresser.
  - Ambitions: BoxOfMystery, Miner, TattooChair, Tattooing, SkinnyDipClothingPile, ClothingPileDry, SculptureComponent, Widget, PortfolioActiveCareer, InventionData, Invention.
- **Seasons and Island Paradise (EP8, EP10):**
  - Seasons: Umbrella, SoccerGoal, SpringDanceFloor, BuffHaunted, BuffWretchingRainbows, SnowConeMachine, BuffAlienBoostDrain, DynamicChallengeLeafCircumstance, KissingBooth, FireworksBundle, DeepSnowEffectManager, AlienUtils.
  - Island Paradise: TreasureComponent, ResortBuffetTable, MarineLifeSurfaceSpawner, KrakenManager, MarineLifeStateFinalInvestigate, MarineLifeStateInvestigate, BonfireParty, BoatSailBoat, BoatPaddleBoat, LifeguardChair, Houseboat, OccultMermaid.
- **Into the Future, World Adventures, Showtime and store:**
  - Into the Future: RobotDataManager, HoloDisc, HoloComputer, HoloPet, PhilanthropistRecordData, TimeKeeperRecordData, ParentOfRoboticsRecordData, FutureSkill, BuffRobotForm, BotBuildingSkill, FutureBar, BuffEmbracingTheFuture, NaniteTerrarium, TraitChipStaticData, TVHolographic, TimePortal, FutureDescendantService, Jetpack.
  - World Adventures: RockGemMetalBase, TombObjectData, PhotographySubjects, CollectableRelic, AncientPortal, GemCuttingMachine, HideableTombObject, VisaManager, MeteorMagnet, BurialGround, TombStatTracking.
  - Showtime: CrowdMonsterUpdate, FrontStageKit, BalancingBall, GiantRingOfFire, PerformanceCareerEvent.
  - Store: WishingWell, BabyMonitor.

## 3. Findings, ranked (handed to the fix agent; nothing built here)

No finding below is fixed in ts3fixes, proven-fixes, earlier docs or simler90 UPDATE242.

| # | Type.Method | Failure | Household-visible | Tag |
|---|---|---|---|---|
| 1 | `MetalMineralPotionDisplayCase.DoReset` | `DoReset` calls `Cleanup()`, which outside build mode runs `Inventory.DestroyItems()`. Any reset of the case deletes every stored gem, metal and potion: a Sim reset mid PutAway, a lot reset, or the reset-object cheat. Sibling display cases empty themselves only in `Dispose`. | yes | [verified] |
| 2 | `UniversityWelcomeKit.TakeAptitudeTest.GetSUATScore` | `kGradeBonus` (shipped 1.3, 1.15, 1, 0.75, 0.5) is ordered for `School.Grade` (A = 0). The academic path indexes it with `AcademicGrade` (F = 0, A = 4), so an A GPA scores ×0.5. The school path is gated on `CurrentGrade() > Grade.A`, so A students get ×1.0 while B students get ×1.15. Result: lower SUAT scores and fewer scholarships for the best students. | yes | [verified] |
| 3 | `OccultMermaid.SummonShark.Run` | When `TryToPlaceAndFadeIn` fails, the shark is destroyed and the field is set to null, then `RequestState(NewlySpawned)` is called on it: NullReferenceException. The fallback that reuses a nearby shark is unreachable. | yes (Evil or MeanSpirited mermaids) | [verified] |
| 4 | `Jetpack.FlyHere.Cleanup` | `GetActiveJetpack().StopFX()` has no null check. If Fly Here is queued and the jetpack posture ends first (stop jetpack, transform), its dequeue or cleanup throws. Sibling cleanups use `?.`; ts3fixes patches only FlyAround (docs/572 §12). | yes | [verified] |
| 5 | `NaniteTerrarium.ReverseEngineer.Cleanup` | Cleanup destroys the target whenever there is an exit reason other than Canceled. A failed route (RouteFailed) or a priority interrupt therefore destroys a placed terrarium and its nanite with no result. | yes | [verified] |
| 6 | `SpringDanceFloor.SimDancers` / `CheckForDanceWinners` | Dance minutes are only ever added; nothing clears them after the last-day-of-spring check. Time from earlier festivals passes the 3 h threshold, so king or queen can be crowned without dancing this year. The dictionary also holds dead or moved-out Sims. | yes | [verified] (accumulation), [inferred] (survives a save) |
| 7 | `NectarBottle.DuplicateData` | `MadeByLevel10Sim` is not copied, and `UpdateVisualState`/FX are not refreshed. Nectar bought or tasted from a rack (NectarRack lines 278 and 371) or bought at a NectarRegister loses its level-10 effects: FlameOn, FullOfLife, de-age, and the special bottle. | yes | [verified] |
| 8 | `EatHarvestablePet.StartEatingVfx` / `StopEatingVfx` | The effect is created into a local variable and never stored in `mEatGroundVfx`/`mEatFallVfx`. Stop and Cleanup are no-ops, and every start event (6 handlers) leaks a started native effect on the pet's mouth. Cats, deer and raccoons get an empty effect name. | yes (visual, horses and dogs) | [verified] |
| 9 | `ResortBuffetTable.ConsumeMoreFood` | Picks a random buffet table, including tables set to FoodType.None or with their food cleaned up. `GetRandomObjectFromList` on that table's empty ServingContainerGroup list then throws from `EatHeldFood.Run`. | yes (resorts) | [verified] code, [inferred] frequency |
| 10 | `KrakenManager.KrakenAttackEndAlarmCallback` | The occupant loop breaks on the first Sailor before it tests FriendOfTheKraken. The guaranteed immunity is lost, and the attack happens whenever the 50% sailor roll fails. `Boat.RewardReleaseTheKraken` tests both. KrakenAttackOneShot also leaks the out-of-world jig on placement failure. | rare | [verified] |
| 11 | `KissingBooth.AttendToKissingBooth.Run` | The retry loop `while (ok && n > 0)` is inverted: it re-routes after a success and gives up after the first failure. The early return when someone else is attending leaves the booth footprint disabled and `mSelectableSimAttendingBooth` set. | low | [verified] |
| 12 | `BuffMummysCurse.OnTimeout` | `GetRandomElement()` can remove a hidden or occult trait. Siblings use `GetRandomVisibleElement()`. `AddElement(Evil)` then fails on a full visible trait list. | rare | [verified] |
| 13 | `MechanicalBull.RideBull.Cleanup` | A cancelled or dequeued second rider sets `mCurrentRider = null` while the first Sim is still riding. Watchers stop, and Test lets more rides queue on a bull that is in use. | rare | [verified] |
| 14 | `MagicBroomArena.WatchStuntRide.Loop` and `RideBroomArena.Run` | (a) The ride ends while a watcher routes to tip, and `mCurrentPerformer` is used after it is set to null: NRE, after the selectable watcher has already paid. (b) Run clears the broom the player chose via SetMagicBroom, so the arena uses its own best broom. | rare / low | [verified] |
| 15 | `BalancingBall.BalanceBall.Run` | `Actor.Occupation as PerformanceArtistCareer` is dereferenced, but Test lets any Sim use a ball that is off stage. A level 6+ Singer or Magician hits an NRE mid-animation. GiantRingOfFire's Test checks the career. | rare | [verified] code, [inferred] reach |
| 16 | `MagicGnomeElvis.TryToCreateElvisGnome` | Registered in PostWorldInitializers (EP6) as a daily 23:00 alarm. A Singer at level 7+ gives a 20% chance per night, and a Fried PB&B plate gives 10%. There is no existing-gnome cap (MagicGnomeVampire has one) and no ActiveActor null check, so gnomes pile up on the home lot. FindGoodLocationNearby failing leaks the out-of-world gnome and creates a second one. | yes | [verified] code, [inferred] intent |
| 17 | `CatToyWallDancer.GetValidSims` | `relationship == null && requiredLtr > 0` is inverted. With the shipped −10, Sims and pets the actor has no relationship with are left out of the Play With picker. | low | [verified] |
| 18 | `MakeGourmetFoodForPet.Run` | In a horse-only household where the Sim already carries Horse Granola, a coin flip picks a dog or cat recipe. The sibling branch has the `(dogs or cats)` guard; this one does not. | low | [verified] |
| 19 | `TraitChipStaticData.RemoveComponent` | Family-inventory crystals count as available but are removed from `sim.Inventory`, so they are never consumed. This favours the player. | yes (minor) | [verified] |
| 20 | `BotBuildingSkill.MergeTravelData` | `mNanitesCollected` is not merged, while its count is. After future travel, an already-collected nanite type counts again. | low | [verified] |

Inferred and rarer (listed for the fix agent, not ranked):
- `Houseboat`: `mAutopilotRoutes` is persisted, but the AutopilotFunction one-shot is not restarted on load. Autopilot and Return to Port then queue forever until the boat is reset.
- `BuffEmbracingTheFuture.OnAddition`: ignores travelReaddition, so the +1 Future skill-gain modifier stacks on each return.
- `BoxOfMystery`: RouteLock is not cleared in PerformTrick.Cleanup, so the volunteer loops forever. `WooHooInBoxOfMystery.Run` returns true after a failed sync, so TryForBaby can start a pregnancy without the woohoo.
- `DaycareWorkdaySituation`: the late-parent alarm handle is never stored or removed, so it fires after the lot has gone inactive.
- `AcademicCareer.SetNewProfessor`: unguarded `OccupationAsAcademicCareer` on stale `sAcademicSims` entries.
- `PingPongTable`: autonomous ping pong and juice pong can pick a child, a pet, or (juice pong) a bot as the opponent.
- `SoccerGoal.ShootOut.DoOneRound`: the shipped array has 11 entries (0-10). If the goalie's loop reads the kicker's Athletic skill before the kicker's `AddElement`, the difference is 11 and the index is out of range.
- `DoggySwimHere.RouteActorToDestination`: random pick from an empty unobstructed-slot list.
- `GeneSplicingExperiment.OnEpicSuccess`: dereference before the null check.
- `FutureDescendantService`: size-0 descendant households give an NRE in ProcessDescendantHouseholds and InvalidateHouseholdsForSimDesc.
- `TreasureComponent.GetAdventureTreasureInternal`: null TreasureInfo goes unchecked into the pick.
- `Bird.FinalizeDeath`: null owner lot.
- `TailgatingParty`, `VictoryParty`, `MasqueradeBall`: every chair counts against a 1-entry object list.
- `PhilanthropistRecordData`: the kWinLotto listener is never removed.
- `MeteorMagnet`: cancel orphans the fragment.
- `OccultMermaid.Dehydrate`: look-ats stay disabled.

## 4. Dropped or already covered

- `HorseManager.GetHorseCost` champion multiplier looked inverted with the code defaults (0.5, 0.2). The shipped tuning is 1.5 and 1.3, so `bonus + bonus − 1` is correct. Not a bug [verified, tuning XML].
- `PoliceBreakJuiceKeggerPartySituation.OnScoldCompletion` (party dereferenced before its null check): simler90 UPDATE242 moves the call inside the check.
- `FutureSkill.MergeTravelData` drops `mFoodSynthesized`: simler90 adds the merge.
- `Tattooing.GetTimeToTattoo` and `SculptureComponent.StartMelting`: already in `docs/data/ilpatch-convert.csv`.
- MagicGnome PosseUp and MagicGnomeEvil chance: ilpatch-convert.
- OccultVampire SunsetCallback: external-bug-leads-1115.
- OccultGenie RemoveSim and RessurectSim: docs/559 and ilpatch-convert.
- EquestrianCenter horse traits: docs/572 §13.
- Readers' scanner-shape false positives (each checked):
  - foreach-remove loops that break after the removal;
  - inclusive `RandomUtil.GetInt` bounds;
  - paired alarms and broadcasters (FairyGold, BonfireParty, DeepSnowEffectManager, NectarRack, GemCuttingMachine);
  - UnicornPool despawn copy-paste (dead code);
  - HorseBeingLedPosture double decrement (clamped);
  - CrystalFlower index (Test excludes Neutral).

## 5. Next

- Fix agent: findings 1-8 are the household-visible ones with plain code defects.
- Rerun `ep-pack-classify.py` after the next sweep. The CSV rows for these 150 types now read `hand-read=yes`, `read-in` += `docs/587`, with `rank` cleared.
- Pack types still uncovered after this pass: about 2,650. `ep-pack-classify.py` lists them in rank order.

## 6. Batch EP1: built (2026-11-15, branch `feat/ep-batch1-1115`, NOT RUN)

Sources: `mods/ts3fixes/src/EpBatch1aLogic.cs` (§1-§10), `EpBatch1bLogic.cs` (§12-§20), `EpBatch1Checks.cs`. Counter slots 1400-1465 (pinned block `fixes-1400`, `docs/registry.md`; 1466-1499 kept for EP batch 2). Every switch is off by default. Each has a pure `Ts3Fixes.Fixes.<Name>Check("run"|"result")` and a repro `<switch>-check` in `tools/harness/repros.json` (off: `verdict: BROKEN`, on: `verdict: FIXED`). Swap rows are in `docs/data/swap-stack-audit.csv`; every hook has a stub.

The user rule for this batch was to fix loop, order and logic defects in every EA function read for a fix. Extras found that way are marked **RE**.

| § | Switch | Check | Hook | Household-visible | Notes |
|---|---|---|---|---|---|
| 1 | `displaycasereset` | `DisplayCaseResetCheck` | DoReset + Cleanup: inside a reset, Cleanup keeps the items and only stops the effects | yes | Build-mode move and Dispose keep EA's path. |
| 2 | `suatgradebonus` | `SuatGradeBonusCheck` | GetSUATScore: GPA path reads the bonus best-first; a school A gets bonus[0] | yes | Shipped tuning is 1.3/1.15/1/0.75/0.5. The two paths index the same array in opposite enum orders, and the school gate skips exactly A, so this is a code bug. |
| 3 | `mermaidsharknull` | `MermaidSharkNullCheck` | Copy of SummonShark.Run: RequestState runs only after a successful placement | yes | Yields; protected by the stub remap. |
| 4 | `jetpackflyherecleanup` | `JetpackFlyHereCleanupCheck` | FlyHere.Cleanup without an active jetpack: jigs destroyed, base Cleanup, no StopFX | yes | |
| 5 | `naniteroutefail` | `NaniteRouteFailCheck` | ReverseEngineer.Cleanup destroys the terrarium only once it is in the actor's inventory | yes | |
| 6 | `springdanceclear` | `SpringDanceClearCheck` | The dance tally is cleared after CheckForDanceWinners | yes | RE, not fixed: the queen branch can both mail the sculpture and add it to inventory. |
| 7 | `nectarcopylevel10` | `NectarCopyLevel10Check` | DuplicateData copies MadeByLevel10Sim and refreshes the visual and effects | yes | RE, not fixed (no trigger): NectarRegister uses the bottle before its null check. |
| 8 | `harvesteatvfx` | `HarvestEatVfxCheck` | Start/StopEatingVfx store, stop and clear the effect | yes | RE: species with no effect got `Create("")`; now they get nothing. |
| 9 | `buffetemptypick` | `BuffetEmptyPickCheck` | ConsumeMoreFood picks only tables with servings (room first, then lot) | yes | |
| 10 | `krakenfriendsailor` | `KrakenFriendSailorCheck` | The alarm notes boats where EA's scan misses a friend; the next attack one-shot on such a boat does nothing | rare | Deliberate releases are untouched. RE, not fixed: the kraken jig leaks on failure paths. |
| 12 | `mummycursetrait` | `MummyCurseTraitCheck` | OnTimeout removes a visible trait | yes | |
| 13 | `bullridercleanup` | `BullRiderCleanupCheck` | A rider still riding this bull is restored after another Sim's RideBull.Cleanup | yes | |
| 14 | `broomtipperformer` | `BroomTipPerformerCheck` | RouteToTipJar returns false when the performer is gone after the route | yes | §14b (chosen broom) stays parked. |
| 15 | `balancingballcareer` | `BalancingBallCareerCheck` | BalanceBall Test requires PerformanceArtistCareer, as GiantRingOfFire does | yes | |
| 16 | `elvisgnomeguard` | `ElvisGnomeGuardCheck` | Guards for no ActiveActor or home lot; the leaked out-of-world gnome is destroyed; RE: plates without a CookingProcess skipped, one music loop and one stop alarm per play, Stop finds every gnome | yes | |
| 16 | `elvisgnomecap` | `ElvisGnomeCapCheck` | No spawn while the home lot has an Elvis gnome | yes | Intent inferred (MagicGnomeVampire caps), so this is a separate switch. |
| 17 | `cattoypickerrel` | `CatToyPickerRelCheck` | GetValidSims admits no relationship when requiredLtr <= 0 | yes | Shipped value is -10. |
| 18 | `gourmetpetrecipe` | `GourmetPetRecipeCheck` | Copy of Run: the granola coin flip needs dogs or cats; RE: no draw from an empty list | yes | Run does not yield. |
| 19 | `chipfamilycrystal` | `ChipFamilyCrystalCheck` | Crystal components found in the family inventory are removed from it | yes | RE: no family inventory no longer throws. |
| 19 | `chipanycount` **RE** | `ChipAnyCountCheck` | An "Any" chip component asks medium and large blanks only for what is still missing | yes | |
| 19 | `chipcomponentorphan` **RE** | `ChipComponentOrphanCheck` | Components that left the inventories during RemoveComponentsFromInventory and are in no inventory are destroyed | no | |
| 20 | `nanitemerge` | `NaniteMergeCheck` | MergeTravelData copies the collected-nanite list; RE: the schematics list gets its own copy | yes | |

Parked: §11 KissingBooth retry loop (low impact; the fix needs a full Run copy) and §14b.

Verification:
- `build-fixes.ts` builds clean.
- `reflect-members-check.ts`: 500 lookups, 0 wrong kind.
- `swap-stack-audit.ts` gate and self-check pass.
- `registry.ts --self-check` and `check-docs-code.ts` pass.

Next: one off/on run of the 22 repros.

## 7. Batch EP2: built (2026-11-15, same branch, NOT RUN)

Source: `EpBatch2Logic.cs` and `EpBatch2Checks.cs`. Slots 1466-1477, inside the same pinned block. Every switch is off by default. The file follows the same pattern as §6: a check, a repro and audit rows for each switch.

| § | Switch | Check | What changes | Evidence |
|---|---|---|---|---|
| 7.1 | `consignmentlastday` | `ConsignmentLastDayCheck` | Fixes the Consignment, PotionShop and BotShop registers. A sale on day 5 reads the last price. An item past its lifespan does not roll for a sale, so EA's own return branch mails it home. | Verified in the decompile and against the shipped tuning: lifespan 5, 5 prices in all four tuning resources. Age is incremented before the roll, so a sale on day 5 or 6 indexes past the end of the array. The IndexOutOfRangeException aborts the daily alarm for every owner after that one. |
| 7.2 | `bartendinghourlypay` | `BartendingHourlyPayCheck` | OnStopBartending pays the hourly wage it adds to the totals, through `SimDescription.ModifyFunds`. | Verified. The shipped bar data has HourlyPay 5-100 on 51 of 53 bar types. Neither the method, its only caller nor any listener calls ModifyFunds; only tips are paid. |
| 7.3 | `influencefollowerbonus` | `InfluenceFollowerBonusCheck` | The influence skill-gain modifier becomes `followers / (float)divisor`, with the same [-1, 2] clamp. | Verified that the division is integer; the shipped divisor is 500, not the 20,000 in the code default. The intent to scale is inferred from the float clamp and from the double division in the protest-score sibling. docs/583 G1. |
| 7.4 | `broomarenachoice` | `BroomArenaChoiceCheck` | The broom chosen from the broom's own menu is kept with the interaction and used if it is still usable. | Verified (§14b). Run clears it and AfterRouteRun resets the from-inventory flag. RE: SetMagicBroom wrote the arena's shared broom at push time, which changed the broom of a ride already in progress. |

All four are household-visible.

Not fixed, found by RE:
- The consignment loop also throws for an item whose object was deleted while consigned (no trigger seen).
- A bartending shift that ends without StopTending carries its tips into the next session's notice.

**KissingBooth (§11): parked.** `AttendToKissingBooth.Run`'s retry loop is `while (flag2 && num > 0)`, which is inverted:
- on success it routes to the slot twice more (zero-length re-plans);
- on failure it never retries.

A fix needs either a copy of Run, which uses the private AttendingKissingBooth posture and five private members, or a hook on `Sim.RouteToSlot`, a hot routing method used everywhere. Re-running Run after a false return is not safe: its other false paths would kick a non-selectable attendant out on the retry. Cost and risk outweigh two extra route plans per attend.

## 8. Tranche 2 and batch EP3: built (2026-11-15, same branch, NOT RUN)

**Tranche 2.** The next 150 ranked EP/SP types from `ep-pack-classify.py` were read by hand, plus 10 rarer inferred leads from tranche 1. EA's `StoryProgression` namespace was skipped (owned by the story-progression work). Every confirmed lead was checked against simler90 UPDATE242 (none of the built ones is fixed there) and against the shipped tuning where a [Tunable] default mattered.

Inferred leads from tranche 1:
- CONFIRMED: DaycareWorkdaySituation late-parent alarm (built as 8.10); FutureDescendantService size-0 household (parked, below).
- DROPPED: GeneSplicingExperiment.OnEpicSuccess (EP9 object always exists); TreasureComponent.GetAdventureTreasureInternal (keys validated by the caller and all present in shipped XML); Bird.FinalizeDeath null owner lot (world lot only, no normal-play path); PhilanthropistRecordData listener; AcademicCareer.SetNewProfessor; SoccerGoal; Houseboat autopilot; BuffEmbracingTheFuture (no reachable defect).

**Batch EP3.** Source: `EpBatch3Logic.cs` and `EpBatch3Checks.cs`. Slots 1478-1498 (`fixes-1400`) and 1500-1520 (`fixes-1500`). Every switch is off by default, with a pure check, a repro and audit rows, as in §6.

| § | Switch | Check | What changes | Evidence |
|---|---|---|---|---|
| 8.1 | `fortunetellerscamodds` | `FortuneTellerScamOddsCheck` | Scam-artist levels 1-9 get their own array, built with EA's scam interpolation (after EA builds the tables and at world load). | Verified: `CreateReadingChancesArrays` adds one array to both tables, then overwrites it with the mystic values. |
| 8.2 | `consignmentskillcurve` | `ConsignmentSkillCurveCheck` | `sSkillVsSaleChanceCurve` is rebuilt from EA's own skill arrays in all three registers. | Verified, shipped tuning: the curve is built from the frequency arrays (X 0,3,5), which Curve.Fx extrapolates to -2.25 at skill 10 instead of 1. Sales fall as Consignment skill rises. |
| 8.3 | `consignmentstockleak` | `ConsignmentStockLeakCheck` | Expired and rotated shop-stock objects are destroyed before EA drops them, if they are still out of the world and in no inventory. | Verified: `RemoveAt` with no Destroy in ItemDictionary and OnMagicHourAlarm. That is 5 potions or bots a day, plus every expiry. |
| 8.4 | `herbqualityfloor` | `HerbQualityFloorCheck` | A Horrifying herb is not lowered further. | Verified: there is no floor, so quality 0 is read out of range. `IncreaseHerbQuality` already has the matching cap. |
| 8.5 | `snackbowlslobvfx` | `SnackBowlSlobVfxCheck` | The slob food effect uses `FireOneShotEffect`. | Verified: the effect is created and started, and its handle is dropped. |
| 8.6 | `worldpondskatetest` | `WorldPondSkateTestCheck` | Skate on a world pond also has to pass the shared skate test for the area EA picked. | Verified: the result of `SharedCanSkateOnObjectTest` is discarded. It refuses Sims in robot form, Sims already skating there, and full areas. |
| 8.7 | `pingpongopponent` | `PingPongOpponentCheck` | Both opponent lists keep only humans aged Child or older. | Verified: there is no species test and, for the autonomous pick, no age test, so pets, babies and toddlers can be picked. |
| 8.8 | `mermaiddehydratelook` | `MermaidDehydrateLookCheck` | Cleanup re-enables look-ats once StandardEntry has run. | Verified: Run disables them and nothing in Dehydrate turns them back on. |
| 8.9 | `meteormagnetorphan` | `MeteorMagnetOrphanCheck` | After Cleanup, a fragment still out of the world and in no inventory is destroyed. | Verified: a cancelled attract never shows the fragment or puts it in an inventory. |
| 8.10 | `daycarelatealarm` | `DaycareLateAlarmCheck` | The late-pickup alarm is skipped if the child is gone or no longer monitored. Force removals run as EA. | Verified: `HandleRemoval` has no null or key checks. |
| 8.11 | `boxofmysterywoohoo` | `BoxOfMysteryWooHooCheck` | For the Try for Baby initiator, Run succeeds only if EA's `RunPostWoohoo` ran with this box. | Verified, shipped kBabyMadeChance 50: base Run returns true after a failed StartSync. |
| 8.12 | `bodysculptorweight` | `BodySculptorWeightCheck` | The Run's own SetBodyShape call gets EA's target weight in place of the mirrored value. Failures and "Original" are unchanged. | Verified: `num5 = 1 + num4` mirrors negative weights around -0.5. Thin makes thin Sims fatter, and Weak/Strong flip them. |
| 8.13 | `nectarconsignvalue` | `NectarConsignValueCheck` | The value of stocked nectar is rolled over p[2]..p[3]. | Verified, shipped rows: `GetInt(p[2], p[2])` always gives the minimum. |
| 8.14 | `straygnometns` | `StrayGnomeTnsCheck` | The new disallow type is set on every gnome before EA's Run, and the notice shows once. | Verified: SetAllowStrays shows the notice before the type is set. RE: one copy per gnome. |

All but 8.9 are household-visible. Found by RE and fixed: 8.14's duplicate notices. Found by RE, not fixed: the null checks around ShowTooltip and AppreciateDepreciateDailyUpdate in the register stock code; FruitVeggieStand has the same curve line as 8.2; PotionShop and BotShop have the same nectar line but ship no nectar rows; the autonomous ping-pong pick on an empty list.

Parked, with reasons:
- FutureDescendantService size-0 households (`ProcessDescendantHouseholds` NRE on dirty names; progenitors not packed back). `Instantiate` and `ProcessDescendantHouseholds` yield. A safe prefix would edit persisted state (`Household.mDirtyNameSimIds`, unpack lists), so it needs its own design and a future-trip test.
- The following need copies of yielding Run methods with several private members, which is not worth the risk in this batch:
  - DoggySwimHere.RouteActorToDestination (pick from an empty list);
  - FragmentedObject.Reconstruct.RunFromInventory (destroys N+1 fragments);
  - AcademicLabSituation.PushLabSituationInteraction (ignores lot lab objects when the inventory is empty);
  - AcademicBusinessPlanner.UseBusinessPlanner.Run (writes the shared Definition; can pocket a planner someone else is using).
- Party chair count (`num == Length` pays only with exactly one chair): which objects count as "chair" is inferred. The OnOver bonus order is unclear.
- Low or cosmetic: ClubLight Clamp result discarded; DaycareTransportSituation; CounterIsland.PlaceSnackBowl charge order; Dresser.PutOnUniform helper leak; VomitPile.Eat roll; MarineLifeStateWander PostPlanCallback; Fireplace mLitFireFX; PutInInventory null SimOwner.

## 9. Copies of yielding methods, and batch EP4a (2026-11-16, same branch, NOT RUN)

**Is a full copy of a yielding Run safe? Yes.** It is as safe as any wrapper around a yielding method, which ts3fixes already has many of.
- EA's Run methods are not iterators. They yield through `Simulator.Sleep`, routes and animations inside the Sim's own task, so a copy keeps its yields just by calling the same members.
- Saved stacks (docs/242 §2) handle copies the way they handle every swap:
  - A save made while a Sim is inside the copy names the frame by the hook. It resumes in the copy under the same build.
  - A save made inside EA's method is pointed by `savedstacks` at the stub, which holds EA's IL, and finishes as EA's.
  - The one cost is shared with every yielding hook: a later build that changes the copy's IL resets a Sim saved inside it once.
- How the copies are written:
  - Each copy is EA's body with only the lines marked `// changed`.
  - The copy's hook class derives from the interaction's base class, as BJfHook, BNrHook and BGpHook already do, so it can reach protected members.
  - Private members are reached through reflection.
  - With the switch off, the copy runs EA's IL.

| § | Switch | Check | What changes | Evidence |
|---|---|---|---|---|
| 9.1 | `dogswimslots` | `DogSwimSlotsCheck` | Copy of `DoggySwimHere.RouteActorToDestination`: the hydrophobic reroute loop runs only when a slot is free. | Verified: `GetRandomObjectFromList` throws `ArgumentException` on an empty list. |
| 9.2 | `fragmentreconstruct` | `FragmentReconstructCheck` | Copy of `Reconstruct.RunFromInventory`: the clicked fragment is picked first, and the trailing `Clear` is null-guarded. | Verified: when the Target is not among the first N, N+1 fragments are used up; how often that happens is inferred. RE: the Clear throws when too few fragments are free. |
| 9.3 | `labobjectpush` | `LabObjectPushCheck` | Wrapper, no copy needed (the method does not yield): if the student's inventory list is empty, a live lot lab object is pushed with EA's own call. | Verified: the lot objects are added only when the inventory has one. Intent inferred. |
| 9.4 | `businessplannerrun` | `BusinessPlannerRunCheck` | Copy of `UseBusinessPlanner.Run`: the use roll is kept in a local, and the caption shows it. After a failed wait or route, the planner is put back only if this run took it out of the inventory. | Verified: the roll is written on the shared Singleton definition, and `TryToAdd` pockets a planner another Sim is using. |
| 9.5 | `futuredescendantsize0` | `FutureDescendantSize0Check` | `Instantiate` skips size-0 households. Copy of `ProcessDescendantHouseholds`: skips null households, missing progenitors, and resize loops that make no progress. | Verified, shipped size mapping: these throw, or hang (RE: `RemoveDescendant` can remove nobody and still return true). |

**FutureDescendant: load safety.** The fix writes no state that EA would not write:
- it skips EA's unpack of progenitors whose result EA throws away;
- it skips the statements that throw;
- it leaves a resize loop that is stuck.

A save made with the switch on loads unchanged with it off. All five fixes are household-visible.

## 10. Tranche 3 and batch EP4b (2026-11-16, NOT RUN)

**Tranche 3.** 146 EP types were read by hand:
- first, the 40 EP types in `docs/data/usage-rank-1115.csv` (real-use rank) not read before;
- then the scanner rank;
- store content and EA's StoryProgression namespace were skipped.

Most of the use-ranked types are Mimics, spawner or fragment subclasses with no logic of their own. 8 leads were confirmed. Every built one is unfixed in simler90 UPDATE242.

| § | Switch | Check | What changes | Evidence |
|---|---|---|---|---|
| 10.1 | `junglegymexitqueue` | `JungleGymExitQueueCheck` | Copy of `JungleGymLookOutTower.RouteOutOfJungleGym`: entries for Sims who are gone or off the tower, with no pending alarm, are dropped. Each exit alarm removes its own Sim's entry. `ResetSafeToExit` returns on an empty queue. | Verified code, shipped 3 / 45 min. Reach inferred: a Sim reset during the wait leaves a stale entry for good, and every later Sim then waits 45 minutes. |
| 10.2 | `wildhorselot` | `WildHorseLotCheck` | Copy of `WildHorses.FindGoodLot` (static, does not yield): `< 0` instead of `<= 0`. RE: a lot whose gnome bars the horse is not a fallback either. | Verified, shipped tuning: big parks get weight ×1 instead of ×2. |
| 10.3 | `harvesterunripe` | `HarvesterUnripeCheck` | `HarvestPlant.OnSuctioned` does nothing on a plant that fails `HarvestTest`. | Verified: the Harvester calls `DoHarvest` on unripe or dead plants, which still rolls Gatherer and Science extras. |
| 10.4 | `hottubseatposture` | `HotTubSeatPostureCheck` | `EnsureSeatPosture` skips the re-init while the sore alarm is pending, and the first Idle does not repeat it. | Verified: each seated social adds another AlwaysPersisted sore alarm. |
| 10.5 | `teenpartybreakup` | `TeenPartyBreakUpCheck` | Copy of `TeenParty.BreakUpParty.Run` without the unused `situation.Lot` read. | Verified: an NRE when the party ended during the route. The kegger sibling is already fixed (ilpatch row 73). |
| 10.6 | `dogswimaroundlist` | `DogSwimAroundListCheck` | After `DoggySwimAround.Run`, a dog still in the pool is put back on the pool's use list. | Verified: `pool.ActorsUsingMe.Remove(Actor)` edits the live list. |
| 10.7 | `tutorballoonalarm` | `TutorBalloonAlarmCheck` | The balloon alarm is removed after `ConfigureInteraction` and started in `PreSocialLoop`. | Verified: it starts at queue time. A rejected queue insert leaks it into the save (reach inferred). |

All seven are household-visible.

Parked or dropped:
- `TeenParty.CollapseTeenPartyScenario`: it modifies `sAllSituations` while iterating over it. It is reachable only when a Generations save is loaded without Generations.
- `JungleGym.PlayOn` `.Minute`: a wrong operand, but unreachable with the shipped times. The same pattern is in HalfPipe.cs:170, which is for a later tranche.
- `WildHorses.SpawnWildHorse`: a null `InstantiateOffScreen` result is used without a check; reach inferred.
- Scan pass 4 leads (docs/583 §8.1 on `analysis/roslyn-scan-1115`) are queued for the next batch:
  - 18 sites that start a VisualEffect and drop it (fix: `SubmitOneShotEffect`). SnackBowl is already 8.5.
  - The Consignment reputation fallback name (cosmetic).
