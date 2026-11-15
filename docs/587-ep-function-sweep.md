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
