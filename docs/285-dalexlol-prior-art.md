# 285. Dalexlol's switches: prior art in community fixes, NRaas, simler90 and patch mods

Written 2026-11-18. **Static only:** web search, a read of the local NRaas source clone (`game-copy/nraas-src` @2f57186, read-only, no license: paths, classes and methods cited, nothing copied), simler90 UPDATE242's changed-method list (`docs/data/mod-method-diff.csv`, docs/218) and our own hook source. No build, no game run, no queue.

**Question (Rodney):** do Dalexlol's fixes already exist as community fixes? Rule: check existing fixes first (web + NRaas grep), then prefer our own fix ON and the other mod's feature off, but list every conflict.

**Scope, 378 switch names (376 switches; `vhomewalk` = `virtualhomewalk`, `detailprioexact` = `detailpriorityexact`):** the 242 switches turned ON from the Dalexlol batches on master (232 from #452-#518, commit 2fb19b7e9; 10 from batch 2 #524-#529, commit 2f57fb94d) plus the 372 lines of `tools/harness/sets/proven-fixes.txt` in #540 (`59afad287`), 236 of which are batch switches. Target methods come from each hook's `Patch(typeof(T), "Method", ...)` call in `59afad287:mods/ts3fixes/src` (341 of 378 resolved; the rest from the switch comment).

Tags: **[verified]** read in source or data; **[inferred]** method-level match only, the other mod's body not read line by line; **[community]** web.

## Summary

| Class | Count | Meaning |
|---|---|---|
| a. performance / off-camera shortcut | 46 names (44 switches) | `unseen*`, `spot*`, `rhentry*`, memo/no-box/scan micro-opts, `zoomtier`, `playerfirst` |
| b. EA bug fix already handled elsewhere (same bug, or another mod cleans the leftover state) | 28 | 4 same-bug (1 verified, 3 inferred) in simler90, 24 partial (Overwatch / ErrorTrap / NRaas reset / simler90) |
| c. new, no prior fix found | 304 | EA script bugs nobody has published a fix for that we could find |

Plus 24 switches where another mod **replaces the EA code path**, so ours does not run while that mod is installed (no clash): 13 story-progression `sp*` (NRaas StoryProgression), 6 `spot*` (NRaas GoHere `GoHomeEx`), 3 consignment (NRaas Consigner), `boxofmysterywoohoo` and `pingpongopponent` (NRaas Woohooer).

**Main findings:**
1. **(a) has no script-level equivalent.** No community mod ends off-camera NPC loops, fakes unseen bar service or relaxes unseen rabbit-hole entry. The nearest are NRaas Register (fewer role/NPC Sims), NRaas StoryProgression and Overwatch Stuck Check (resets, not shortcuts), and one simler90 change (`PerformerPerformForTips.LoopHandler` gets an `IsNPC` branch, next to our `tipsnpcend`). S3PP, S3SS, Smooth Patch / LazyDuchess and tiny-ui-fix work at engine, frame-pacing or UI level; none touches these script paths (docs/01, docs/07 §1, docs/218) [verified for tiny-ui-fix by docs/218, community for the rest].
2. **simler90 is the only mod that fixes the same bugs at the same method** [verified by method list]: `megaphonerun` (simler90 rewrites `Sim+WakeWithMegaphone.Run`), and, by changed-method match only [inferred], `professorterm` (`AcademicCareer.RemoveProfessor`), `invitecallreply` (`Phone+RandomInvitationCall.OnAnswered`), `krakenfriendsailor` (`KrakenManager.KrakenAttackEndAlarmCallback`). 10 of our hook targets are methods simler90 changed (§2).
3. **NRaas fixes the leftovers, ours stops them happening.** Overwatch's load-time and nightly cleanups (`CleanupTimers`, `CleanupReactionBroadcasters`, `CleanupElevators`, `CleanupHostlessNPCParties`, `CleanupSituations`, `CleanupCellPhones`, `FixConsignment`, `HandleLunarCycle`) and ErrorTrap's `CheckRoleManager` repair state that about 20 of our switches stop being created. Both ON is fine: the cleanups just find less to do.
4. **ErrorTrap mitigates, it does not fix.** For our throw guards (`fridgetestnull`, `ovenmenunull`, `pokerdeadplayer`, `weathermanagerthrow`, `lifeguardquit`, `trainsimgone`, `roundupreturn`, `spcollectnull`, `celebalarmnull`, `teenpartybreakup`, `meltalarm`, `mermaidsharknull`, `buffetemptypick`, `jetpackflyherecleanup`, `wildherdmother`, `elvisgnomeguard`, ...) ErrorTrap's `OnScriptError` catches EA's exception and resets the Sim or object (docs/184 §1). Ours stops the exception, so ErrorTrap never fires there. These stay class c.
5. **No switch needs to stay OFF because of another mod.** Every hook checked wraps the EA (or simler90) body and calls `*Orig`; none replaces a method another mod changed. Conflict risks to watch are in §3.
6. **Web search found no published fix** for the phone family (busy after load, call schedule), elevator pets (the LittleMsSam elevator fix is Sims 4 only), Seasick, wild-horse sex ratio, consignment, professor firing or PermaZombie at the new moon. An EA patch fixed a different zombie bug (spawning after a full moon saved mid-moon). Plumbot Fixes Mod (Max_53) and nikel23's Opportunity phone call fix were checked: no overlap with `plumbotfacelistener`, `sentiencename`, `efficientchip` or the phone switches.

## 1. Method

1. Switch list and descriptions: `Instantiator.cs` `k*` comments at `59afad287`, and the `+` lines of the two "Contributor switches ON" commits.
2. Targets: each hook class that reads `ShortcutOn("<name>")` and is passed to `Patch(typeof(T), "M", ...)`; fallback ±60 lines around the switch name, then `Type.Method` tokens in the comment.
3. simler90 / tiny-ui-fix / ErrorTrap `ScriptCore`: target `Type.Method` against `docs/data/mod-method-diff.csv` (468 simler90 rows), and a keyword pass over simler90's changed types for related bug families (§2).
4. NRaas: every `.cs` outside `Sims3/` (5,108 files) searched for each target `Type.Method`; hits that are plain call sites were dropped by hand. Overwatch `Loadup/*` and `Alarms/*` and ErrorTrap `Checks/*` read for what they repair.
5. Web: grouped searches by bug family (phone, elevator, PermaZombie / lunar, university / professor, wild horses, consignment, Seasick, game speed, objects in use, off-screen NPC lag, core-mod fix lists); EA's "Common Game Fixing Mods - Hero Approved" thread returned 403 to scripts. Finds logged in docs/24.

## 2. simler90 overlaps (method-level)

| Switch | simler90 change (from `mod-method-diff.csv`) | Verdict |
|---|---|---|
| `megaphonerun` | `Sim+WakeWithMegaphone.Run`: +`Other::WakeWithMegaphoneRunAway`, -`RequestWalkStyle`, -`RouteAway` | same bug [verified]; our `WsMegaHook` only moves a Run request that the call added, so with simler90 it counts and does nothing. Keep ON |
| `professorterm` | `AcademicCareer.RemoveProfessor` / `RemoveStudent` branches | same bug [inferred]; ours returns before `OnTermCompleted` for professors, so simler90's change is not reached. Redundant, no clash |
| `invitecallreply` | `Phone+RandomInvitationCall.OnAnswered` IL 70 -> 101 | likely same bug [inferred]; ours wraps; check the decline reply once with simler90 |
| `krakenfriendsailor` | `KrakenManager.KrakenAttackEndAlarmCallback` branches | possibly same [inferred]; ours patches `KrakenAttackOneShot` too and calls `Orig` |
| `resumetns` | `Career.ShowUploadResumeTns` closure fix | different path; ours calls `ShowUploadResumeTns` after `GraduateInPlace.Run` for return-home graduates. **Risk: double notice with simler90** if simler90 also shows it there (not seen in the method list) |
| `snublistener` | `SnubManager` `SnubAgeUpFix` / `SnubTimeOutFix` | related, different leak; complementary |
| `petpoolghost` | `PetPool.RandomRemovePet` rewritten | ours runs first and calls simler90's body; complementary |
| `futuredescendantsize0` | `FutureDescendantHouseholdInfo.Instantiate` rewritten | ours wraps it; check size-0 households with simler90 |
| `tipsnpcend` (a) | `PerformerPerformForTips.LoopHandler` +`Sim.IsNPC` | related NPC end; ours wraps |
| `unseenservice*` (a) | `BarAdvanced+BarInteraction.Run` branches/constants | ours wraps `Run` and `EnqueueInteractionAndWait`; no clash expected |
| `simdisposelink`, `npcshowstuck`, `sprecheck` | `Sim.Dispose` (+inventory sell, boat fix), `NPCShowManager.CreateNPCShow` (NPC pick), `StoryProgressionService.TryOneAction` (+notification) | different bugs in the same methods; ours wraps |

Related simler90 families with no shared method: `ResortManager.TryHaveWedding` (partner pick) vs `resortweddingalarm` (rescheduling), `FutureSkill.MergeTravelData` (+`mFoodSynthesized`) vs `nanitemerge`, `ActiveFireFighter` vs `fireactivejob`, `SplitHousehold.Execute` vs `splitpartner`, `ChemistryLab+MakePotion` (outfits) vs `chemistryvfx`.

## 3. Conflict risks when both are installed

| Pair | Risk | Action |
|---|---|---|
| `resumetns` + simler90 | possible double upload-resume notice | one graduate repro with simler90 |
| `moonriselistener` / `permazombiemoon` + Overwatch `HandleLunarCycle` | Overwatch sends an extra `kMoonRise` at load and re-arms the lunar alarm; with EA's duplicate listeners that event fires the werewolf/fairy handlers more than once. Ours removes the duplicate, so ours makes Overwatch's re-send safer | none; keep ON |
| `elevatorresetriders` / `elevatorridertimeout` + Overwatch `CleanupElevators` | the same rider can get our failsafe reset and Overwatch's nightly reset (two resets) | harmless [inferred]; no action |
| `uselistprune` / `resetinusefree` / `putdowninuse` + NRaas `ResetSimTask` | both remove the Sim from use lists; idempotent | none |
| `spot*`, `sp*`, consignment, Woohooer-owned interactions | ours does not run while the NRaas mod owns the path | none; say so in release notes |
| Throw guards + ErrorTrap `OnScriptError` | ours prevents the throw; ErrorTrap's reset no longer happens for those cases (the intended result) | none |
| Our hooks on simler90-changed methods (§2) | the patcher binds by name, so it wraps simler90's body | keep the wrap-and-call-`Orig` pattern (`ts3-rule-reviewer` item) |

**Should any switch stay OFF?** No. None of the checked mods handles one of these bugs in a way ours would undo or fight. `resumetns` is the only one with a plausible visible side effect (a second notice) when simler90 is installed.

## 4. Follow-ups

- Rerun `tools/compat-scan.ts` with the 376 batch switches; `docs/data/compat-matrix.csv`'s overlap column predates the batches.
- Confirm the three [inferred] simler90 same-bug rows by reading simler90's shipped source (`game-copy/simler90/dec`) for `RemoveProfessor`, `RandomInvitationCall.OnAnswered` and `KrakenAttackEndAlarmCallback` (~20 min, static).
- One repro each with simler90 for `resumetns` and `futuredescendantsize0`.

## 5. Per-switch table

Batch = where the switch came from (batch 1 = #452-#518, batch 2 = #524-#529, #540 = proven-fixes list only). Prior art: `dup` same bug fixed elsewhere, `dup?` same method changed [inferred], `part` other mod repairs the leftover state, `inert` other mod replaces the code path (ours does not run with it), `none found` = class c.

| Switch | Batch | Class | Hook target(s) | What it fixes | Prior art |
|---|---|---|---|---|---|
| `agingalarmcancel` | batch 1 | c | `AgingManager.CancelAgingAlarmsForSim` | cancelling a Sim's aging alarms also cancels the graduation early-message alarm | none found |
| `artsellnoartist` | #540 | c | `ImageObject.SellImageObject` | selling art whose painter is gone pays and keeps the item | none found |
| `autonomyinuseblock` | batch 1 | c | `InteractionQueue.DeQueue` | an autonomous action that finds its object taken and returns false leaves no failure block, so autonomy re-pic | none found |
| `awkwardscience` | batch 1 | c | `Skill.ComputeSkillGainMod` | the Socially Awkward science gain bonus applies to Science only | none found |
| `backpacklotleak` | batch 1 | c | `Backpack.Dispose` `Backpack.OnLotChanged` | a backpack destroyed while in use stayed on the static lot-change event and threw on every lot change; now uns | none found |
| `bathcleanup` | batch 1 | c | `TakeBath.Cleanup` | a queued second bath's cleanup leaves the bather's duck alarm and footprint alone | none found |
| `bathsituationfx` | batch 1 | c | `Bathtub.DoTakeBathPreLoop` | a bath's privacy situation is not exited twice and the old bubble effect is disposed | none found |
| `birthdayagers` | #540 | c | `BirthdayParty.OnAgeTransition` `HouseParty.CleanUp` | birthday party records only the first Sim who ages up | none found |
| `boardingschoolmoney` | batch 1 | c | `FogMarker.Dispose` | boarding-school phone-call money ranges each read into their own pair (world load) | none found |
| `boatstaleswimmer` | batch 1 | c | `BoatMultipleOccupancy.OnRouteStarted` | boat route start removes swimmers with no Sim before EA's loop (no throw, swimmers waited for) | none found |
| `bouncerrole` | batch 1 | c | `VelvetRopes.ShouldBouncerNoticeSneaker` | the bouncer notices sneakers within the tuned radius (squared) and ropes without a bouncer role do not throw | none found |
| `bubblingspringsreset` | batch 1 | c | `BubblingSprings.DoReset` | a reset hot spring disposed its Nature's Hot Tub broadcaster and never recreated it; now recreated | none found |
| `buffremovedelayed` | batch 1 | c | `BuffManager.RemoveElement` | removing a buff with a pending delayed add also removes the live buff | none found |
| `businessplannerrun` | batch 1 | c | `UseBusinessPlanner.Run` | business planner: autonomous use is rolled per run and a Sim who gives up waiting no longer pockets the planne | none found |
| `butlerpayalarm` | batch 1 | c | `RoundUpTeens.Init` | a butler whose job ended stops charging the household every week | none found |
| `cancelposturedef` | batch 1 | c | `Posture.CancelPosture` | cancelling a posture does not queue a second stand-up when one is at the head | none found |
| `carpoolschoolskip` | batch 1 | c |  | a school child skipping the bus (horse, own lot) goes to school instead of crashing the pickup | none found |
| `catchpreyodds` | batch 1 | c | `PicnicBasket.HideAnts` `PicnicBasket.DoCleanUp` | snakes and turtles are a sure catch only with Slither Stalker, like lizards | none found |
| `cattoypickerrel` | batch 1 | c | `CatToyWallDancer.GetValidSims` | cat wall dancer Play With lists Sims with no relationship at the shipped -10 threshold | none found |
| `celebalarmnull` | batch 1 | c | `SimDescription.FindSuitableVirtualHome` | celebrity alarm removal works for a celebrity who is not instantiated | none found |
| `chemistryvfx` | batch 1 | c | `BuffHorrified.OnRemoval` `BuffTurboSleep.OnRemoval` | chemistry potion wear-off and potion discovery effects were started and dropped, never destroyed; now auto-des | none found |
| `chessdefeated` | batch 1 | c | `LogicSkill.UpdateChessTournamentStatistics` | the highest-ranking defeated chess opponent is the strongest one, stored with its rank | none found |
| `chestuselist` | batch 1 | c | `Phone.ReplaceHandset` `SocialInteractionA.Cleanup` | a cut treasure-chest open keeps the Sim on the chest use list | none found |
| `chocfountaingeyser` | batch 1 | c | `ChocolateFountain.FountainFilled` | each refill of the chocolate fountain adds another repeating geyser alarm | none found |
| `cleanupvfx` | batch 1 | c | `InteractionInstance.Cleanup` | an interaction's named effects are disposed at its cleanup in a live game (also in the djcleanup copy) | none found |
| `compositionnull` | batch 1 | c | `SingingInfo.ImportCompositions` `BandSkill.ImportCompositions` | singing and band composition imports skip missing keys and unknown compositions | none found |
| `computergametype` | batch 1 | c | `PlayComputerGames.Run` | an autonomous computer game's random pick is not kept in the shared definition | none found |
| `copyhomeworkfx` | batch 1 | c | `CastSpell.Cleanup` | a homework copy cut short removes its cheating broadcaster | none found |
| `curfewalarm` | #540 | c | `PromSituation.CleanUp` | curfew police alarm handle survives a world quit | none found |
| `dartboardvfx` | batch 1 | c | `BuffHorrified.OnRemoval` `BuffTurboSleep.OnRemoval` | expensive dartboard throw-result effects (4) were started and dropped; now auto-destroyed | none found |
| `daycareliking` | batch 1 | c | `Daycare.GetExperience` | daycare experience uses the child's real liking for the owner | none found |
| `digsiteemptypick` | batch 1 | c | `DigSite.ExcavateSomething` | an empty dig keeps the dig site treasure (household-visible) | none found |
| `disposebase` | batch 1 | c | `JunkPile.Dispose` `ShoppingRegister.Dispose` | JunkPile, ShoppingRegister, PetBedReward, BubbleColumn and FogMarker Dispose run GameObject.Dispose | none found |
| `disposevirtualslot` | batch 1 | c | `Household.Dispose` | a disposed townie household with only a virtual home leaves its lot slot, NPC door and mailbox | none found |
| `distressgate` | batch 1 | c | `IdleManager.ScheduleCurrentDistressIdles` | the higher-priority distress test finds buff distress too | none found |
| `dogswimaroundlist` | batch 1 | c | `HotTubPosture.EnsureSeatPosture` `BreakUpParty.Run` | a dog swimming around stays on the pool's use list | none found |
| `dogswimslots` | batch 1 | c | `DoggySwimHere.RouteActorToDestination` | a hydrophobic dog sent to swim with every pool slot blocked no longer throws | none found |
| `donationbuffchance` | batch 1 | c | `BuffInstanceGiggleFits.DoGiggleFit` `BuffInstancePlasmaticPlatitudes.Faint` | the giggle-fit and plasma-faint donation buffs rolled the percent chance inverted (85% instead of the shipped  | none found |
| `doorlockowner` | batch 1 | c | `Household.Remove` `Lot.MoveIn` | door locks to a dead or moved-out owner opened, re-pointed or pruned (household-visible) | none found |
| `eatpostonce` | batch 1 | c | `Food.PreEat` `Food.PostEat` | a finished eat is not posted a second time by EatHeldFood.Cleanup (kEatIEdible, Stuffed, food-truck rolls once | none found |
| `eatpreycancel` | batch 1 | c | `PetEatPrey.Run` `PetEatPrey.SharedNearDistanceBehavior` | a cancelled or failed prey eat keeps the prey and re-enables its interactions | none found |
| `efficientchip` | #540 | c | `SkillManager.RobotSkillsChanged` | the Efficient chip skill bonus only updates with Ability to Learn | none found |
| `elevatorkitten` | batch 1 | c | `ElevatorDoors.IsAllowedThrough` | kittens and puppies refused by the elevator route planner as the doors refuse them | none found |
| `elevatorresetriders` | batch 1 | b | `ElevatorPortalComponent.OnReset` | riders waiting in a resetting elevator get the failsafe reset at once, not after 30 sim-minutes (household-vis | part: Overwatch alarm `CleanupElevators` resets stuck elevators and riders nightly; ours fixes at once |
| `elevatorridertimeout` | batch 1 | b | `ElevatorPortalComponent.OnPortalStart` | each elevator rider keeps its own 30-minute failsafe start (household-visible) | part: Overwatch `CleanupElevators` (nightly) |
| `elevatorsavedlog` | batch 1 | c | `ElevatorPortalComponent.OnPortalStart` | log only: saved elevator rider entries by state at world load | none found |
| `equestriantrophyname` | batch 1 | c | `EquestrianTrophy.ImportContent` | equestrian trophies read the horse name from the rider key on import, showing the rider as the horse; now read | none found |
| `facialidleweight` | batch 1 | c | `IdleManager.GetFacialMoodIdles` | buff and trait facial idles share their chance as floats (7+ idles no longer weigh 0) | none found |
| `fairyauratimer` | #540 | c | `OccultFairy.FixUpOccult` | a fairy Creative or Mind aura gets its full time back on every load | none found |
| `fallenrockcollected` | batch 1 | c | `FallenRock.RockTimeoutCallback` | an imported meteorite's untracked timeout alarm destroyed it after the household collected it; now a collected | none found |
| `fearfaintundo` | batch 1 | c | `FearOfHumansFaint.Run` | a Fear of Humans faint that ends early restarts the robot and removes its jig | none found |
| `fightbroadcastend` | batch 1 | b | `SocialInteractionA.Cleanup` | a fight's reaction broadcaster ends with the social when the fight never reaches AfterFight | part: Overwatch `CleanupReactionBroadcasters` drops destroyed / duplicate lot broadcasters at load only |
| `fireactivejob` | #540 | c |  | the firefighter's current emergency (a static) survives a world quit; afterwards every new emergency is droppe | none found |
| `firefightercount` | batch 1 | c | `Firefighter.NeedsAssignment` | no more firefighters come than the pool's maximum | none found |
| `firetrapcount` | #540 | c | `FirefighterEmergencySituation.OnSimLotChanged` `FirefighterEmergencySituation.OnParticipantDeleted` | firefighter "free N Sims" counts trapped Sims who left the lot or sit in hidden apartment units | none found |
| `floorswitchuselist` | batch 1 | c | `BuffInstanceGiggleFits.DoGiggleFit` `BuffInstancePlasmaticPlatitudes.Faint` | standing on a tomb floor switch cut by a reset left the Sim on the switch's use list (Cleanup skips base); now | none found |
| `flowersinuse` | batch 1 | c | `SocialInteractionA.Cleanup` | a rejected Give Flowers leaves the bouquet in use for good | none found |
| `foodtraygrill` | batch 1 | c | `SingingInfo.ImportCompositions` `BandSkill.ImportCompositions` | a food tray whose grill step fails is put down and a failed grill push no longer throws | none found |
| `fragmentreconstruct` | batch 1 | c |  | reassembling fragments uses the clicked fragment and no extra one (and no NRE when too few are free) | none found |
| `freshclothingresume` | batch 1 | c | `LaundryManager.AddLaundryBuff` | Fresh Clothing resumes its saved time after a swim or shower, not restarted | none found |
| `fridgetestnull` | batch 1 | c | `TV.SetDataDiscDisposedListener` `Posture.CancelPosture` | opening a fridge with no inventory component is tested without a throw | none found |
| `frolicexit` | batch 1 | c | `FairySocialFrolic.FrolicLoop` | any critical motive ends a fairy frolic, not only the last one in the list | none found |
| `futuredescendantsize0` | batch 1 | b | `FutureDescendantHouseholdInfo.Instantiate` `FutureDescendantService.ProcessDescendantHouseholds` | future trip: size-0 descendant households no longer unpack progenitors or abort the descendant load step | part: simler90 rewrites `FutureDescendantHouseholdInfo.Instantiate`; Overwatch `CleanupFutureDescendantService` drops annihilated households at load |
| `fvstandskillcurve` | batch 1 | c | `FruitVeggieStand.InitializeData` | the fruit and veggie stand builds its skill curve from the frequency arrays | none found |
| `gigmaster` | batch 1 | c | `RockBand.get_OppGigMasterCompleted` | Gig Master completes at the number of gigs its text names | none found |
| `gigpullaudience` | #540 | c | `Gig.PrepareVenueForShow` | removed gigs still pull an NPC audience to the venue | none found |
| `gohereboatlist` | batch 1 | c | `MakeIgloo.Run` `VelvetRopes.ShouldBouncerNoticeSneaker` | a deleted Sim leaves the go-here boat and hover-train lists | none found |
| `goodbyepure` | batch 1 | c | `SocialComponent.GetGoodbyeSocial` | building the goodbye pie menu no longer removes SoCold or despawns a Haunted ghost | none found |
| `gotoworkstale` | #540 | c | `Career.RegularWorkDayGoToWorkHandle` | a stale persisted go-to-work alarm fires for a job the Sim left | none found |
| `gradvisitors` | #540 | c | `Call.GetAllValidCallees` | the graduation visitor list offers dead Sims, toddlers and pets | none found |
| `guitargnomeboost` | batch 1 | c | `IdleManager.ScheduleCurrentDistressIdles` | guitar play takes off every Elvis gnome boost it added | none found |
| `handsetreset` | batch 1 | c | `Phone.ReplaceHandset` | a landline keeps its handset hidden after a call cut by a reset | none found |
| `harvesterunripe` | batch 1 | c | `HarvestPlant.OnSuctioned` | the Harvester no longer harvests unripe or dead plants | none found |
| `harvestextras` | batch 1 | c | `HarvestPlant.DoHarvest` `HarvestPlant.UpdateGardeningSkillJournal` | Gatherer and Science extra harvestables reach the harvester's inventory, and mushroom collecting credits the h | none found |
| `harvestregion` | batch 1 | c | `HarvestPlant.GetGrowthPerformanceRegion` | a poorly grown plant's harvest gets no quality bonus (EA's bottom growth region) | none found |
| `hauntedgnomeinv` | batch 1 | c | `HayBale.OnScattered` | a haunted gnome loaded in an inventory kept spawning ghosts and scaring cowards; now cleaned like on inventory | none found |
| `hauntedumbrella` | #540 | c | `TrickOrTreatSituation.SendNPCTrickOrTreaters` `NpcSeasonalPartyManager.ScheduleNpcParty` | haunted house umbrella check is inverted | none found |
| `hayscattertime` | batch 1 | c | `HayBale.OnScattered` | hay bale scatter time was never reset, so later scatters on a haystack or stacked bale finished at once; now r | none found |
| `haystackvfx` | batch 1 | c | `BuffHorrified.OnRemoval` `BuffTurboSleep.OnRemoval` | haystack pile transition effect was started and dropped; now auto-destroyed | none found |
| `hhcleanupdup` | batch 1 | c | `Household.Cleanup` | Household.Cleanup folds a duplicate household only into a valid one and re-homes its unshared members (househo | none found |
| `holidaylights` | batch 1 | c | `OccultWerewolf.SetupSimHandlers` `HolidayHouseLights.ApplyLightsToWorld` | holiday house lights: pulse of at least 1, held trickle alarm removed before re-add and unsaved; load removes  | none found |
| `homeworkskillbonus` | batch 1 | c | `Homework.GetCompletionRate` | homework skill bonus below max level restored (household-visible) | none found |
| `horseranchchallenge` | batch 1 | c | `ChallengeDelegateFunctions.VisitAHorseRanchSimCheckFunction` | visit-a-horse-ranch challenge hidden for households without a horse (household-visible) | none found |
| `horsetraitscore` | #540 | c | `EnterEquestrianCompetition.AddScoreForTraits` | horse traits never count in equestrian competitions (the rider is checked) | none found |
| `hottubseatposture` | batch 1 | c | `HotTubPosture.EnsureSeatPosture` | hot tub seated socials no longer stack sore-buff alarms | none found |
| `houdinignomeleak` | batch 1 | c | `MagicGnomeHoudini.SpawnHoudini` | the magician gnome created near a magic box leaked when it could not be placed, and a failed create threw; now | none found |
| `hypnotrickrelease` | batch 1 | c | `PerformTrick.Run` | a hypnotized volunteer stays frozen because the trick never clears the route lock | none found |
| `igloolookats` | batch 1 | c | `MakeIgloo.Run` | an igloo builder's look-ats are turned back on after the build | none found |
| `intmultiple` | batch 1 | c | `RandomUtil.GetIntMultiple` | RandomUtil.GetIntMultiple can return its top multiple (sales up to 80%, idles up to 30 min) | none found |
| `invitecallreply` | #540 | b | `Phone.ScheduleNextCall` | declining a random invitation call plays the accept reply; a household-less caller throws | dup?: simler90 changes `Phone+RandomInvitationCall.OnAnswered` (IL 70 -> 101) [inferred same bug] |
| `jetpackflyingbuff` | #540 | c | `FlyAround.Cleanup` | Free Flying stays paused when Fly Around ends early | none found |
| `jogterrain` | batch 1 | c | `GoJogging.JogLoop` | a fatigued jogger's walk back is created on the terrain instead of throwing every minute | none found |
| `junglegymexitqueue` | batch 1 | c | `JungleGymLookOutTower.RouteOutOfJungleGym` `JungleGymLookOutTower.ResetSafeToExit` | look-out tower exit queue drops Sims who left (no permanent 45-minute wait) | none found |
| `kegbreakage` | batch 1 | c | `RepairableComponent.UpdateBreakage` | the juice kegger breakage multiplier applies only at a juice kegger party | none found |
| `labobjectpush` | batch 1 | c | `AcademicLabSituation.PushLabSituationInteraction` | university lab students without a lab item use the lab's own objects (intent inferred) | none found |
| `leafburnfail` | batch 1 | c |  | a leaf pile whose fire never started is usable again | none found |
| `leaveconvnull` | batch 1 | c | `SocialComponent.LeaveConversation` | leaving a conversation the Sim never joined does not count as a social | none found |
| `lifeguardquit` | batch 1 | c | `SurveyBeach.SurveyLot` | a lifeguard who quit mid-survey stops the loop instead of throwing in RescueCheck | none found |
| `lifeguardsurvey` | batch 1 | c | `Job.OnCompletion` | completed lifeguard beach survey's repeating event alarm removed (EA left it throwing forever); load removes s | none found |
| `lightcustom` | batch 1 | c | `LightGameObject.SetIntensityRoom` `LightGameObject.SetIntensityHouse` | a room or house custom light level copies the chosen level | none found |
| `lightningweights` | batch 1 | c | `Plant.UpdateSeasonalState` | lightning can strike Sims and objects again after a strike that found none | none found |
| `littercap` | #540 | c | `PetAdoption.CanAddPetInHouse` | a neighbour litter can push a household past 10 members | none found |
| `lookatcopy` | batch 1 | c | `Lot.ObjectsToLookAtInRoom` | asking for a room's look-at objects without Sims no longer removes the Sims from the registry | none found |
| `lotprune` | batch 1 | c | `MetaAutonomyManager.PruneLotsThatAreNotPlayerPreferred` | the player's lot-popularity map drops deleted, residential and no-longer-visited lots | none found |
| `loyaldoglay` | batch 1 | c | `PetSurfacePosture.LoyalDogLayDownCallback` | a Loyal dog's lie-down near its human targets the dog so its test passes | none found |
| `ltrhistorybits` | batch 1 | c | `LongTermRelationship.ForceChangeState` `LongTermRelationship.ChangeBitsForState` | a forced relationship change keeps the friendship history bits | none found |
| `lunacysocials` | #540 | c | `BuffLunacy.OnAddition` `BuffLunacy.OnRemoval` | only the last Sim given Lunacy does lunatic socials; none after a load | none found |
| `maildogchaser` | batch 1 | c | `Job.OnCompletion` | mail carrier's chasing dog cleared when the chase ends (EA kept it for every later delivery) | none found |
| `meltalarm` | batch 1 | c | `SculptureComponent.StartMelting` | an ice sculpture with no known artist melts without a throw and a restart replaces its melt alarm | none found |
| `moodidleflavor` | batch 1 | c | `IdleManager.ParseMoodIdleData` | MoodIdle continuation rows join their flavour instead of being dropped | none found |
| `moodtankreset` | batch 1 | c | `MoodManager.UpdateMood` | a recovered mood clears the remembered angry tank so the next angry mood tanks again | none found |
| `moondiallistener` | batch 1 | c | `MoonDial.Dispose` | a deleted moon dial removes its moon-change listener | none found |
| `moonriselistener` | batch 1 | b | `Job.OnCompletion` | fairy / werewolf kMoonRise listener removed before EA re-adds it at each instantiation | part: Overwatch `HandleLunarCycle` sends an extra `kMoonRise` at load: duplicate listeners fire twice more there; ours dedupes, so complementary |
| `mountfishcost` | batch 1 | c | `Firefighter.NeedsAssignment` | mounting a fish charges that fish's cost, not the last fish tested | none found |
| `movingflag` | #540 | c | `WildHorsePool.FindNewHerdMother` | a stale move-in-progress flag blocks every move after load | none found |
| `newspaperdef` | batch 1 | c | `Newspaper.GetReadInteraction` | reading the newspaper autonomously no longer renames the newspaper menu root | none found |
| `nocommercialcar` | batch 1 | c | `OpportunityManager.AcceptOpportunity` `ScavengerOpportunity.OnAdded` | driverless traffic car with no commercial lot waits and drives off instead of throwing | none found |
| `npcbusypartner` | batch 1 | c |  | an NPC starter waits 45 sim-min for an NPC partner whose linked action is still only queued (StartSync pre-syn | none found |
| `npcseasonalparty` | #540 | b | `TrickOrTreatSituation.SendNPCTrickOrTreaters` `NpcSeasonalPartyManager.ScheduleNpcParty` | after a holiday reload the player's party no longer cancels the NPC holiday party | part: Overwatch `CleanupHostlessNPCParties` (load) |
| `npcshowstuck` | #540 | c | `NPCShowManager.CreateNPCShow` | NPC shows stop for the rest of the save once an NPC gig is removed | none found |
| `nullsituation` | batch 1 | b | `Service.AddAssignment` `Service.CheckForAssignment` | a service whose situation could not be made no longer throws in AddAssignment and stops every service for the  | part: Overwatch `CleanupSituations` (load + nightly) removes broken situations |
| `oppcategoryprotect` | batch 1 | c | `OpportunityManager.AcceptOpportunity` | opportunity accept that EA rejects adds no cull protection | none found |
| `oppchainworld` | batch 1 | c | `Opportunity.ClearChainHistory` | opportunity chain history cleared from the world change when only the source needs another world (household-vi | none found |
| `oppfailcharge` | batch 1 | c | `Opportunity.IsFailed` | a failed opportunity's deleted-object penalty is charged once, not twice | none found |
| `ovenmenunull` | batch 1 | c | `PlaypenClassic.CreateLearnToTalkBroadcaster` `FruitVeggieStand.InitializeData` | picking food from an empty or unaffordable restaurant menu throws | none found |
| `partylotlistener` | batch 1 | b | `ScavengerOpportunity.OnAdded` | hostless NPC party lot-change listener removes itself | part: Overwatch `CleanupHostlessNPCParties` drops hostless NPC parties at load; ErrorTrap `CheckSim` drops unhosted NPC parties |
| `permazombiemoon` | #540 | b | `BuffTransformationInstance.OnLoadFixup` | a PermaZombie is never cured at the new moon after a save/load | part: Overwatch `HandleLunarCycle` re-arms the lunar alarm and re-sends `kMoonRise` at load [related; not read whether it cures] |
| `petdreamtimer` | batch 1 | c | `SleepAndNapOnObject.SleepLoop` | a sleeping pet's dream balloon timer resets after a dream of its human too | none found |
| `petdroolvfx` | batch 1 | c | `BuffHorrified.OnRemoval` `BuffTurboSleep.OnRemoval` | piggy-pet drool and spit-up effects when eating off the floor were started and dropped; now auto-destroyed | none found |
| `petharvestvfx` | batch 1 | c |  | a pet eating from a harvest plant stops its eating effect | none found |
| `phonecallrearm` | #540 | c | `Phone.ConsiderGeneratingPhoneCall` | random phone calls stop for the session after the prom-call branch throws | none found |
| `phonecallschedule` | #540 | c | `Phone.ScheduleNextCall` | a load or travel arrival before 7:00 gets no random phone calls that day | none found |
| `phonefixup` | #540 | c | `Sim.GrantSpecialObjects` | a Sim given objects after load keeps an old basic cell phone instead of the smartphone | none found |
| `phonegive` | #540 | b | `Sim.GivePhoneOfType` | replacing a phone keeps the wrong one with two phones, leaks a refused phone and drops ringtone and skins | part: Overwatch `CleanupCellPhones` removes duplicate phones at load |
| `phoneloadreset` | #540 | c | `Phone.ScheduleNextCall` `Phone.ConsiderGeneratingPhoneCall` | a phone saved during a call stays busy after load: no calls out, incoming calls aborted | none found |
| `photofunds` | batch 1 | c | `Photography.GetCostToTakePhoto` `Sim.ModifyFunds` | a phone photo with short funds takes the funds instead of adding them | none found |
| `photosubjectstars` | batch 1 | c | `SubjectDefinition.get_Difficulty` | photography journal subject stars use float division (UI) | none found |
| `picnicants` | batch 1 | c | `PicnicBasket.HideAnts` `PicnicBasket.DoCleanUp` | a packed picnic basket loses its ant alarm and the ant effect is disposed | none found |
| `plantdormantflip` | batch 1 | c | `Plant.UpdateSeasonalState` | a fruit tree with frost and leaf cover disagreeing stays dormant instead of flipping every evaluation | none found |
| `plantdormanttime` | batch 1 | c | `Plant.EvaluatePlantGrowth` | a plant coming out of dormancy does not lose a whole dormant span of water at once | none found |
| `playpentalkcast` | batch 1 | b | `PlaypenClassic.CreateLearnToTalkBroadcaster` | a second learn-to-talk broadcaster orphans the first, which never stops | part: Overwatch `CleanupReactionBroadcasters` (load only) |
| `plumbotfacelistener` | #540 | c | `OccultRobot.OnConversationLeft` | every plumbot reacts to every conversation end in town | none found |
| `pokerdeadplayer` | batch 1 | c | `FruitVeggieStand.InitializeData` | a seated Sim who no longer exists throws in the poker dealer and kills the table | none found |
| `prankditchempty` | batch 1 | c | `Vehicle.CreateTireEffectHelper` `Vehicle.CreateHoverEffectHelper` | doorbell-ditch prank with nobody left at home ends like EA's null pick instead of throwing (prankster reset) | none found |
| `privacyothers` | batch 1 | c | `LogicSkill.UpdateChessTournamentStatistics` `RockBand.get_OppGigMasterCompleted` | a Sim who left a private room's lot gets a new room check when it comes back | none found |
| `professorterm` | #540 | b | `AcademicCareer.OnTermCompleted` | university professors are fired at every term end and never replaced | dup?: simler90 changes `AcademicCareer.RemoveProfessor` / `RemoveStudent` branches [inferred same bug, not read]; ours skips `OnTermCompleted` for professors first, so both = redundant, no clash |
| `promcarpool` | #540 | c | `PromSituation.CleanUp` | the prom limo carpool repeats weekly after the prom | none found |
| `promoteshift` | #540 | c | `Occupation.IsTimeDuringWorkDay` `Career.IsRegularWorkTime` | after a promotion a day off counts as missed work, or the Sim waits in the rabbit hole a day | none found |
| `protestexit` | batch 1 | c | `ImprovedProtestSituation.CancelNPCProtest` | a cancelled NPC protest ends with Exit(), so the child protest's lot-change handler is removed | none found |
| `protestlastday` | #540 | c | `AcademicCareer.OnTermCompleted` | the NPC protest cooldown static survives a world quit | none found |
| `putdowninuse` | batch 1 | b | `InteractionQueue.OnReset` | a put-down on a surface cut after the in-use mark leaves the item in use and the surface held | part: NRaas `ResetSimTask` (only on NRaas resets) |
| `putdownplaced` | batch 1 | c | `SurfacePair.TriggerSlotUsed` | putting an object on a surface fires the placed event, not removed | none found |
| `reactioncopy` | batch 1 | c | `ReactionInteraction.OneShotReaction` | each nearby Sim starts a one-shot reaction from the caller's reaction, not an earlier Sim's trait reaction | none found |
| `reactiongrumpy` | batch 1 | c | `ReactionInteraction.SetReactionType` | a Grumpy Sim's non-positive reaction type is set instead of staying Angry | none found |
| `repairmanskill` | batch 1 | c | `Repairman.UpdateCreatedSim` | a repairman's handiness is the rolled level, not the roll added on every visit | none found |
| `resetinusefree` | batch 1 | b | `InteractionQueue.OnReset` | an item stays in use in a Sim inventory after the Sim is reset mid-interaction | part: NRaas `ResetSimTask` `RemoveFromUseList` (only on NRaas resets) |
| `residentspawnslot` | batch 1 | c | `SimDescription.Instantiate` | apartment resident spawn slot judged with the resident's door permissions, not the selected Sim's | none found |
| `resortweddingalarm` | #540 | c | `ResortManager.WeddingCallback` | a resort never schedules another NPC wedding after one fails late in the day | none found |
| `resumetns` | #540 | b | `GraduateInPlace.Run` | the upload-resume phone notice never shows for return-home graduates | part: simler90 fixes the closure in `Career.ShowUploadResumeTns`; ours adds the call for return-home graduates: check for a double notice with simler90 |
| `returnhomealarms` | #540 | c | `TravelUtil.TriggerTravelToUniversityWorld` | quitting right after a return home skips the next return-home sequence | none found |
| `rolenullguard` | batch 1 | b | `FutureFoodSynthesizer.TestIfSocialValid` | barista and future-food waiter with no role answer no instead of throwing | part: ErrorTrap `CheckRoleManager` / `CheckRoleManagerTask` drop null roles (load, per exception) |
| `roleunprotect` | batch 1 | c | `Role.RemoveSimFromRole` | a townie removed from an inactive role loses the role's story-progression protection | none found |
| `romancecooldown` | batch 1 | c | `GroupingSituation.ContinueRomanticSTC` | the bad-date cooldown in a group of three or more lasts hours, not minutes | none found |
| `roulettehackreset` | batch 1 | c | `PokerTable.SetupRound` `PokerTable.WaitForPlayersToCompleteAction` | a reset leaves the roulette table hacked forever | none found |
| `roundupreturn` | batch 1 | c | `RoundUpTeens.Init` | a curfew round-up with no active Sim leaves instead of throwing in Init | none found |
| `royaltyorphan` | #540 | b | `Writing.MergeTravelData` | an orphan royalty alarm piles up per trip for each writer | part: Overwatch `CleanupTimers` removes orphan `Writing.RoyaltyAlarm` alarms at load; ours stops them piling up |
| `rubbleuselist` | batch 1 | c | `EquestrianTrophy.ImportContent` `Backpack.Dispose` | clearing a rubble pile cut by a reset left the Sim on the pile's use list (Cleanup skips base); now taken off | none found |
| `sandscoins` | #540 | c | `ImageObject.SellImageObject` `Writing.MergeTravelData` | Sands of Understanding bonus coins are never given | none found |
| `sandwichalarm` | batch 1 | c | `Theatre.ResetSandwichBoardFromPlayerConcert` | a theatre removes the daily sandwich-board reset alarm it holds before a show-end re-adds it, so back-to-back  | none found |
| `saunatubclear` | batch 1 | c |  | the sauna stays marked in use after a reset or exception while bathing | none found |
| `scavengernolot` | batch 1 | c | `ScavengerOpportunity.OnAdded` | scavenger opportunity in a world without community lots is accepted without the throw | none found |
| `sciencefxstop` | batch 1 | c | `ScienceComponent.StartVisualEffects` | a science outcome's old effect set is stopped before a new one starts | none found |
| `seasickpaused` | #540 | c | `Sim.LotChanged` | Seasick stays paused forever after leaving a sailing houseboat or boarding a parked one | none found |
| `sentiencename` | #540 | c | `TraitChip.RemoveTraitChipFromSim` | removing the Sentience chip sets the first name to the full name | none found |
| `serviceassignexit` | batch 1 | c | `Service.OnAssignmentFinished` | service Sim asked to hang out leaves no stale (saved) assignment; load removes exited ones | none found |
| `serviceshowalarm` | batch 1 | c | `ServiceSituation.OnServiceStarting` `ServiceSituation.CleanUp` | service route retries do not re-arm the pending make-visible alarm (NPC shown once) | none found |
| `shelldoorsummon` | batch 1 | c |  | summoned proprietor's exit queued only after its teleport | none found |
| `shoodef` | batch 1 | c | `DislikesChildrenTraitShoo.Run` | the Dislikes Children shoo uses its own definition instead of writing into the shared shoo Singleton | none found |
| `showstagefxend` | batch 1 | c | `FXObject.ProcessEvent` `FXObject.StopEffects` | a show end during a stage one-shot effect was dropped, so stage lights and EP6 effects machines stayed on afte | none found |
| `shroomslots` | batch 1 | c | `HarvestPlant.CreateHarvestablesLoop` | mushrooms fill their slots in order and get their harvest timeout instead of skipping slots and throwing | none found |
| `slotmachinedefs` | batch 1 | c | `TreeOfProsperity.get_HasWishingWellOnLot` | autonomous slot play rewrites the shared stakes menu entries | none found |
| `sneakoutlistener` | #540 | c | `Punishment.SneakOutSuccess` `Punishment.ApplySnuckOutMoodlets` | a caught teen's left-over sneak-out listener gives Snuck Out on the next trip (school carpool) | none found |
| `snowfightjoinwait` | #540 | c | `SnowWaterFightArena.DoEndOfGame` | the snowball arena join-wait counter never resets, later games end at once | none found |
| `snublistener` | batch 1 | b | `Sim.Destroy` | a destroyed Sim drops its SnubManager age-up listener | part: simler90 `SnubAgeUpFix` / `SnubTimeOutFix` in `SnubManager`; ours drops the listener of a destroyed Sim |
| `soakedpetselfreact` | batch 1 | c |  | a soaked pet shaking off water queued the shake reaction on itself; now only other Sims react | none found |
| `socialnoeffect` | batch 1 | c | `SocialInteractionA.UpdateConversationWhenSocialStarts` | social starts with no matching rule are counted (the fix is ts3native ilp_socialnoeffect) | none found |
| `spbucketindex` | batch 1 | c | `CombinationAction.UpdateDemographics` | histogram-targeted story actions pick from the bucket that was chosen | inert: NRaas StoryProgression turns EA story progression off (`StoryProgressionService` alarm): ours only runs without it; simler90 also rewrites EA SP (`SplitHousehold.Execute`, `TryOneAction` etc.) |
| `spclonedict` | batch 1 | c | `Demographics.Collect` | scoring clones of the too-big / too-small demographics get their own lot dictionary | inert: NRaas StoryProgression turns EA story progression off (`StoryProgressionService` alarm): ours only runs without it; simler90 also rewrites EA SP (`SplitHousehold.Execute`, `TryOneAction` etc.) |
| `spcollectnull` | batch 1 | c | `Demographics.Collect` | Demographics.Collect skips a null household entry instead of throwing | inert: NRaas StoryProgression turns EA story progression off (`StoryProgressionService` alarm): ours only runs without it; simler90 also rewrites EA SP (`SplitHousehold.Execute`, `TryOneAction` etc.) |
| `spcreatemovein` | batch 1 | c | `CombinationAction.UpdateDemographics` | Create and Move In predicts the new family once, on its lot | inert: NRaas StoryProgression turns EA story progression off (`StoryProgressionService` alarm): ours only runs without it; simler90 also rewrites EA SP (`SplitHousehold.Execute`, `TryOneAction` etc.) |
| `spellbooksummonload` | batch 1 | c | `CastingStore.ImportContent` | a learned Summon spell is dropped from the spellbook on every import (travel, move, exported Sim) | none found |
| `spelltargetend` | batch 1 | c | `CastSpell.Cleanup` | a spell's target stops waiting when the cast ends | none found |
| `spjobatwork` | batch 1 | c | `Occupation.AssignOccupation` | a Sim who takes a new job while at work keeps it when the old job's deferred leave runs (household-visible) | inert: NRaas StoryProgression turns EA story progression off (`StoryProgressionService` alarm): ours only runs without it; simler90 also rewrites EA SP (`SplitHousehold.Execute`, `TryOneAction` etc.) |
| `splitpartner` | #540 | c | `WildHorsePool.FindNewHerdMother` | a story-progression split leaves the stayer with a one-sided partner and can move a teen out alone | inert: NRaas StoryProgression turns EA story progression off (`StoryProgressionService` alarm): ours only runs without it; simler90 also rewrites EA SP (`SplitHousehold.Execute`, `TryOneAction` etc.) |
| `splotdensity` | batch 1 | c | `CombinationAction.UpdateDemographics` | lot density counts households only on the lots it counts | inert: NRaas StoryProgression turns EA story progression off (`StoryProgressionService` alarm): ours only runs without it; simler90 also rewrites EA SP (`SplitHousehold.Execute`, `TryOneAction` etc.) |
| `splotlookup` | batch 1 | c | `Demographics.Collect` | predicted moves count toward too-big, too-small and box stalls (lot looked up by lot id) | inert: NRaas StoryProgression turns EA story progression off (`StoryProgressionService` alarm): ours only runs without it; simler90 also rewrites EA SP (`SplitHousehold.Execute`, `TryOneAction` etc.) |
| `spoccultpick` | batch 1 | c | `StoryAction.FindOccultSimDescription` | story occult removals pick a random occult Sim, not always the first | inert: NRaas StoryProgression turns EA story progression off (`StoryProgressionService` alarm): ours only runs without it; simler90 also rewrites EA SP (`SplitHousehold.Execute`, `TryOneAction` etc.) |
| `sppensionprompt` | batch 1 | c | `CareerManager.AcquireOccupation` `Occupation.ShowYesNoCareerOptionDialog` | rejoining the retired career asks the pension prompt before the old job is left (household-visible) | inert: NRaas StoryProgression turns EA story progression off (`StoryProgressionService` alarm): ours only runs without it; simler90 also rewrites EA SP (`SplitHousehold.Execute`, `TryOneAction` etc.) |
| `sprecheck` | batch 1 | c | `StoryProgressionService.TryOneAction` | story progression rechecks right before Execute that the action does not reach the active, travel or selected  | inert: NRaas StoryProgression turns EA story progression off (`StoryProgressionService` alarm): ours only runs without it; simler90 also rewrites EA SP (`SplitHousehold.Execute`, `TryOneAction` etc.) |
| `sprelcount` | batch 1 | c | `Demographics.Collect` | story progression's friend, best-friend, enemy and romance histograms count each Sim once (no incremental skew | inert: NRaas StoryProgression turns EA story progression off (`StoryProgressionService` alarm): ours only runs without it; simler90 also rewrites EA SP (`SplitHousehold.Execute`, `TryOneAction` etc.) |
| `sproommatejob` | batch 1 | c | `StoryAction.FindOccultSimDescription` | story roommates outside University get the job EA picked for them | inert: NRaas StoryProgression turns EA story progression off (`StoryProgressionService` alarm): ours only runs without it; simler90 also rewrites EA SP (`SplitHousehold.Execute`, `TryOneAction` etc.) |
| `starlightreset` | batch 1 | c | `GameObject.AddToUseList` | a lit StarLight reset no longer resets the selected Sim (household-visible) | none found |
| `steadygigmiss` | #540 | c | `PerformanceCareer.OnJobMissed` | any Sim's missed job counts against every performer's steady gig; not counted at all after a load | none found |
| `stereoupgrade` | batch 1 | c | `Stereo.UpgradeChangedCallback` `DanceTogetherA.Cleanup` | a speaker upgrade does not start music on a silent stereo; a dance pair's end dismisses only its own dancers | none found |
| `streetartmasterboost` | batch 1 | c | `StreetArtSkill.GetMuralData` | the Avant-Garde masterpiece boost went to row 3, not the Master row, so large murals never got it; now the Mas | none found |
| `tagbagsurfaceleak` | batch 1 | c | `EquestrianTrophy.ImportContent` | street art prep leaked its surface object when no target lot was found; now destroyed | none found |
| `talkoccupationchance` | batch 1 | c | `Definition.AddInteractions` | talk-about-occupation Unemployment chance 1/(count+1) as a float, not integer division | none found |
| `teenpartybreakup` | batch 1 | c | `BreakUpParty.Run` | Break Up Party no longer throws when the party ended during the route | none found |
| `teleportcommunitylot` | batch 1 | c | `Opportunity.ClearChainHistory` `SubjectDefinition.get_Difficulty` | teleport to community lot not offered when no valid lot exists (household-visible, menu) | none found |
| `timeportalvfx` | batch 1 | c | `BuffHorrified.OnRemoval` `BuffTurboSleep.OnRemoval` | time portal reset effect was started and dropped; now auto-destroyed | none found |
| `tombmusic` | #540 | c | `ImageObject.SellImageObject` `Writing.MergeTravelData` | tomb music stays silent after quitting inside a tomb | none found |
| `trainsimgone` | batch 1 | c | `TrainSim.Run` | training with the trainee gone ends instead of throwing | none found |
| `traiticondislike` | batch 1 | c | `InteractionObjectPair.GetTraitIconKey` | pie-menu entries that lower a trait motive show the dislike icon | none found |
| `treewishingwell` | batch 1 | c | `TreeOfProsperity.get_HasWishingWellOnLot` | the Tree of Prosperity never sees a wishing well, so its night shows never spawn | none found |
| `trickortreatalarm` | #540 | c | `TrickOrTreatSituation.SendNPCTrickOrTreaters` | the weekly NPC trick-or-treat alarm survives a save/load (NPC trick-or-treaters every week) | none found |
| `tricksocials` | batch 1 | c | `MagicianSocialTricks.Run` | a magician trick that ends early turns the watcher's socials back on | none found |
| `tutorballoonalarm` | batch 1 | c | `TutorSkillInteraction.ConfigureInteraction` `TutorSkillInteraction.PreSocialLoop` | tutoring thought balloons start with tutoring, not when queued (no leaked alarm) | none found |
| `tvdisclistener` | batch 1 | c | `TV.SetDataDiscDisposedListener` | a new home-movie disc replaces the old disc's disposed-listener | none found |
| `tvsyncalarm` | batch 1 | c | `TV.OnChannelChangeResetAlarms` `MovieScreen.OnChannelChangeResetAlarms` | a TV channel change resets the sync-event alarm handles so viewer reactions re-arm | none found |
| `tvwatchskill` | batch 1 | c | `WatchTV.Cleanup` `WatchTV.StopSkillGainForSim` | a WatchTV that never started does not stop the Sim's channel skill gain | none found |
| `twinbellyshape` | batch 1 | c | `SimDescription.SetPregnancy` | belly shape between twins and triplets uses float division | none found |
| `unitravelflag` | #540 | c | `TravelUtil.TriggerTravelToUniversityWorld` | an enrolment that queues no moving van leaves saving and travel blocked | none found |
| `uselistprune` | batch 1 | b | `SocialInteractionA.Cleanup` | objects stay in use by Sims who are gone (EA validator never called) | part: NRaas `ResetSimTask` calls `RemoveFromUseList` for the reset Sim; MasterController object reset; idempotent with ours |
| `vacationalarms` | batch 1 | c | `SimClockUtils.OnFinishedLoadingSaveGameEventHandler` | alarms on vacation arrival are not shifted twice | none found |
| `vampirefruitsnack` | batch 1 | c | `VampireFruit.EatEffects` | an Outstanding vampire fruit gave Sated instead of Sanguine Snack (> instead of the tuned >=); now >= | none found |
| `vehiclefxhelper` | batch 1 | c | `Vehicle.CreateTireEffectHelper` `Vehicle.CreateHoverEffectHelper` | vehicle tire / hover effect helper destroyed before EA creates the next one (orphan helpers kept 2 global list | none found |
| `virtualhomeshare` | batch 1 | c | `SimDescription.FindSuitableVirtualHome` | service Sims' virtual homes come from the tuned cheapest share of shells | none found |
| `virtualmoveindup` | batch 1 | c | `Lot.VirtualMoveIn` | a repeat virtual move-in does not list the service Sim twice | none found |
| `visitlisteners` | batch 1 | c | `VisitSituation.Remove` | a removed visit host's listeners are not re-added after a load | none found |
| `voodoofairyfadein` | batch 1 | c | `PerformTrick.Run` | a fairy bound to a voodoo doll stays invisible when the transform is cut short | none found |
| `wardrobewandvfx` | batch 1 | c | `BuffHorrified.OnRemoval` `BuffTurboSleep.OnRemoval` | wardrobe upgrade wand success/fail effects were started and dropped; now auto-destroyed | none found |
| `wellstealcooldown` | batch 1 | c | `Opportunity.IsFailed` | coins cannot be stolen from a wishing well right after a wish (EA's cooldown) | none found |
| `werewolffind` | batch 1 | c | `WerewolfHuntTerrain.FoundMetal` `WerewolfHuntTerrain.FoundGem` | a werewolf hunt with no metal or gem in range finds nothing instead of throwing, and an insect adds to the hun | none found |
| `wildherdmother` | #540 | c | `WildHorsePool.FindNewHerdMother` | taming or adopting a wild herd mother with foals throws and aborts | none found |
| `wildhorsegender` | #540 | c | `WildHorseCreator.DecideGender` | wild herds get 20% mares instead of the tuned 80% | none found |
| `wildhorselot` | batch 1 | c | `WildHorses.FindGoodLot` | wild horses weight big parks as tuned and respect stray-wildlife gnomes in the fallback | none found |
| `woohootywait` | #540 | c | `SendWooHootyText.GoToLotSuccess` | the WooHooty-text invitee can wait forever for the texter | none found |
| `coopeggquality` | batch 1 | c | `RandomUtil.GetWeightedIndex` | chicken coop eggs come out one quality tier low and never Perfect | none found |
| `doorprivacynull` | batch 1 | c | `Lot.UpdateDoorPrivacy` | a privacy situation with no sync target no longer strips the privacy of later doors | none found |
| `lottotooltip` | batch 1 | c | `InteractionObjectPair.GetTraitIconKey` | the lotto 'not available' tooltip shows the real start hour | none found |
| `motivemultonce` | batch 1 | c | `Motive.UpdateMotiveWithDecay` | the Carefree / LaidbackLemon / EfficientChip multiplier is applied once per motive update, not per matching de | none found |
| `petpoolghost` | batch 1 | b | `PetPool.RandomRemovePet` | pet pool ids whose pet is gone are dropped before a random removal | part: simler90 rewrites `PetPool.RandomRemovePet` (keeps pets with genealogy); ours drops ghost ids before it (wraps simler90 body) |
| `tempforecast` | batch 1 | c | `Opportunity.IsFailed` | the temperature forecast range follows the real day curve instead of its mirror | none found |
| `bouncerskip` | #540 | a |  |  | none found |
| `mopunreachable` | batch 1 | c | `Mopping.Run` `MopFloor.IsLocationMoppableBySim` | a mop run skips a puddle it cannot reach or that is blocked and mops the rest; a per-Sim memo stops autonomy r | none found |
| `petpuddletile` | batch 1 | c | `DrinkPuddle.Run` `PlayInPuddle.Run` | a pet drinking from or playing in the closest puddle removes that puddle, not the clicked one | none found |
| `initstacktrace` | batch 1 | a | `InteractionInstance.Init` `InteractionInstance.OnAddedToQueue` | InteractionInstance.Init / OnAddedToQueue without the dead O(depth^2) StackTrace walks (fields only read by co | none found |
| `weatherstonelistener` | batch 1 | c | `WeatherStone.Dispose` | WeatherStone.Dispose removes the summon's weather listener | none found |
| `pondclearscan` | batch 1 | a | `PondManager.AllPondsClear` `BuffSoaked.OnReAddition` | PondManager.AllPondsClear tests the world pond and the last pond in use before its world GetObjects scan (same | none found |
| `rainsheltercheap` | batch 1 | a | `BuffSoaked.OnReAddition` | the 10-sim-minute rain pass tests on-fire / plant water before the two native shelter tests (same result) | none found |
| `soakedstat` | batch 1 | c | `BuffSoaked.OnReAddition` | a rain re-add of Soaked does not count another TombStats soak (persisted stat) | none found |
| `weathermanagerthrow` | batch 1 | c | `BuffSoaked.OnReAddition` | RainEvent.PutOutFires skips the null firefighter dictionary (worlds without the service) instead of throwing,  | none found |
| `outerwearautoloop` | batch 1 | c |  | autonomous outerwear / toddler outerwear tests answer no when Run cannot change (player-sticky 2 h, disallow b | none found |
| `outerwearrestore` | batch 1 | c |  | ChangeIntoOuterwear stores the outfit to return to, as GoingOutside does | none found |
| `spoonslavechecks` | batch 1 | c | `BedSleep.LoopHandler` | the spooning slave partner gets EA's light-sleeper / TiredFromMoving / NoMonsters checks like every other slee | none found |
| `queueproximity` | batch 1 | c | `SimQueue.FindNewPlaceInLine` | SimQueue OrderByProximity places a new Sim by squared distance on both sides (elevator lines) | none found |
| `subroutefail` | batch 1 | c | `SimQueue.PlayRouteFail` | no route-fail balloon/animation when SimQueue.TryReplacementInteraction pushed a substitute object | none found |
| `petwakebed` | batch 1 | c | `WakeUpSim.Cleanup` | pet WakeUpSim Cleanup clears the bed part that still holds the pet (reset mid-wake) | none found |
| `slavewakereason` | batch 1 | c | `Sim.IsAtSynchronizationLevelWith` | the spooning slave keeps the exit reason that woke it across its wait for the master | none found |
| `outfitwaitend` | batch 1 | c | `SwitchOutfitHelper.Wait` | an off-screen NPC's outfit wait EA's loop holds forever (native swap never ready) ends with EA's instant switc | none found |
| `tpaptgoal` | #540 | a |  |  | none found |
| `hovertraintest` | batch 1 | a | `HoverTrainStation.OnStartupRabbitHole` | Sim.NPCUseHoverTrain.Definition.Test answers ">= 2 stations" from a memo of EA's last station query, forgotten | none found |
| `newcartest` | batch 1 | a | `SocialTest.TestHasNewCar` `SocialTest.TestHasExpensiveNewCar` | SocialTest.TestHasNewCar / TestHasExpensiveNewCar answered from a memo of the cars that were new at EA's last  | none found |
| `detailprioexact` | #540 | a |  |  | none found |
| `dreamcleanup` | batch 1 | c | `DreamsAndPromisesManager.RemoveActiveNode` | wish manager clean-up index kept right (pet-subject nodes unfiled when removed, woken repeatable wishes filed  | none found |
| `innerbeautyonce` | batch 1 | c | `BuffInstanceInnerBeauty.OnDidMeanSocial` | a mean social is handled once however many Sims have Inner Beauty | none found |
| `weddinglistener` | batch 1 | c | `WeddingParty.CleanUp` | wedding party that ends drops its marriage listener | none found |
| `pushwaitskip` | #540 | a | `RoutingComponent.PushSimsStatic` | RoutingComponent.PushSimsStatic's wait pass checks every pushed Sim once (EA steps twice and keeps the pusher  | none found |
| `suatgradebonus` | batch 1 | c | `MetalMineralPotionDisplayCase.DoReset` `MetalMineralPotionDisplayCase.Cleanup` | SUAT grade bonus indexed best-first on both paths (GPA order inverted, school A skipped) | none found |
| `tspawninvdup` | #540 | c | `TreasureSpawnPoint.SetupEventListening` |  | none found |
| `sarcmummynpc` | #540 | c | `Sarcophagus.CommonCleanup` | a reset or deleted Sarcophagus takes its tomb mummy out of NpcHousehold, as TombMummyDestroy does | none found |
| `megaphonerun` | batch 1 | b |  | Wake With Megaphone's Run request lasts only for its route-away (EA leaves the waker running for good) | dup: simler90 UPDATE242 rewrites `Sim+WakeWithMegaphone.Run` (own run-away, no `RequestWalkStyle`); ours stands down by itself (walk-style count unchanged) |
| `dragoncurserun` | batch 1 | c |  | the baby dragon's Phantasmal Curse target runs only during its route-away (EA leaves it running for good) | none found |
| `vhomewalk` | #540 | a | `GoToVirtualHomeInternal.Run` |  | none found |
| `speedskiplevel` | batch 1 | c | `Gameflow.SetGameSpeed` | Gameflow.SetGameSpeed tells the engine the level of the speed it set (EA: Skip's Normal fallback sent level 3) | none found |
| `speedskipnoactor` | batch 1 | c | `Gameflow.SetGameSpeed` | Skip with no active Sim falls back to Normal (EA froze time at scale 0) | none found |
| `speedrecalclock` | batch 1 | c | `Gameflow.RecalculateGameSpeed` | RecalculateGameSpeed keeps a held speed lock and does not persist the locked speed | none found |
| `speedlockowner` | batch 1 | c | `LotManager.EnterBuildBuyMode` `RealEstateManager.OnExitBuildBuy` | build/buy exit unlocks the game speed only if its enter took the lock | none found |
| `alarmzeroperiod` | batch 1 | c | `SocialCallback.AddHeartBroken` | a repeating alarm period of 0 ticks or less counts as 1 tick (EA hangs), catch-up in one division | none found |
| `heartbrokenbcast` | batch 1 | b | `SocialCallback.AddHeartBroken` | SocialCallback.AddHeartBroken disposes the heartbroken broadcaster a repeat breakup replaces (EA left it pulsi | part: Overwatch `CleanupReactionBroadcasters` (load only) |
| `spawnerskip` | batch 2 | a | `ObjectSpawner.TryPlaceSpawnedObjectInWorld` `GlobalFunctions.PlaceAtGoodLocation` | a spawner whose placement search failed kSpawnerSkipFails times in a row takes EA's failure ending at once, fu | none found |
| `maptagtaskalive` | batch 2 | c | `ObjectSpawner.TryPlaceSpawnedObjectInWorld` `GlobalFunctions.PlaceAtGoodLocation` | the map tag visibility and controller tasks restart on a forced update or inactive toggle instead of ending un | none found |
| `lotmgrcallbacks` | batch 2 | c | `LotManager.PreWorldLoad` `LotManager.Shutdown` | LotManager.Shutdown removes the six world callbacks PreWorldLoad adds (portal lock, door visibility, walls, cl | none found |
| `fireplaceherb` | batch 2 | c | `Fireplace.AddHerb` | Fireplace.AddHerb removes a burning herb first (EA leaked the first herb's VFX and haze broadcaster) | none found |
| `rummagebcast` | batch 2 | b | `Rummage.Cleanup` `TrashcanOutside.RummageBroadcasterCreate` | a reset outdoor-trash rummage disposes the can's rummage broadcaster (EA left it queueing reactions) | part: Overwatch `CleanupReactionBroadcasters` (load only) |
| `stairwaitend` | batch 2 | c | `StairsPortalComponent.OnPortalStart` | a stair traversal wait ends at EA's 30 sim-minute portal failsafe (elevator sibling) for NPCs; EA's loop had n | none found |
| `simdisposelink` | batch 2 | c | `Sim.Dispose` | Sim.Dispose of an unbound Sim keeps its description's link to the live Sim (EA unbound it: next Instantiate ma | none found |
| `rhexitunseen` | batch 2 | a | `RabbitHole.CanSimSkipExitWait` | an NPC no camera can see (EA's low-LOD test) skips the rabbit hole NPC exit pacing wait (kNpcExitFreq, one NPC | none found |
| `tipsnpcend` | #540 | a | `PerformerPerformForTips.LoopHandler` | an unseen NPC Perform For Tips ends at EA's low-LOD interaction cap (420 day / 120 night sim-min); EA's only e | part: simler90 adds an `IsNPC` branch to `PerformerPerformForTips.LoopHandler` |
| `barputdownwait` | batch 2 | c | `PutDown.TryToPlaceOnSurface` | the bartender's put-down helper finishes inline when its anim event never fired (EA's Wait looped forever) | none found |
| `putdownsurfaceuse` | #540 | c | `PutDown.TryToPlaceOnSurface` | PutDown.TryToPlaceOnSurface releases the surface and item it took when ParentToSlot fails | none found |
| `swiperestore` | #540 | c |  | a SwipeSomething whose family-inventory add fails puts the victim's object back and destroys the clone | none found |
| `unicornspawnfail` | #540 | c |  | an NPC unicorn spawn with no spot ends like EA's UnicornDespawn instead of staying invisible | none found |
| `datadiscputback` | batch 2 | c |  | DataDiscStorage add/dump-all puts a refused disc back and moves the rest (EA lost it) | none found |
| `spoilbroadcast` | #540 | b | `PreparedFood.ForceSpoil` | PreparedFood.ForceSpoil disposes the disgust broadcaster it replaces at each spoil stage | part: Overwatch `CleanupReactionBroadcasters` (load only) |
| `weatherstonerelisten` | #540 | c | `WeatherStone.AnimateChangeWeatherStoneState` | a successful weather stone summon removes a stale pending weather listener before adding its own | none found |
| `birthdayagerecord` | #540 | c | `BirthdayParty.OnAgeTransition` | a birthday party records every guest who ages up, not only the first | none found |
| `spotasync` | #540 | a | `GoHome.Run` |  | inert: NRaas GoHere replaces `GoHome` (`GoHomeEx.Run`): GoHere Sims skip ours (docs/104 guard) |
| `enumcmpnobox` | #540 | a | `List.Contains` | EqualityComparer<T>.Default for CommodityKind, BuffNames, SkillNames is a non-boxing comparer with the same ha | none found |
| `alarmcmpnobox` | #540 | a |  | EqualityComparer<AlarmHandle>.Default and the live AlarmManager.mTimers use a non-boxing comparer with EA's == | none found |
| `autmetarows` | #540 | a |  | AutShadow request rows kept per meta object and ad (GetType, allow-list probe, SfAllowed), same rows | none found |
| `buffremovemerge` | #540 | a | `LifeEventManager.Startup` `LifeEventManager.Shutdown` | BuffManager no-op removes return after one lookup (our two remove hooks merged) | none found |
| `swslots` | #540 | a |  | per-tick hooks read their switches from a slot array refilled per switch-table generation | none found |
| `stuckwatchscan` | #540 | a | `BuffManager.AddElementPaused` | the stuck watchdog's two passes share one Sim scan and reuse their dictionaries | none found |
| `lifeeventidfirst` | #540 | a | `LifeEventManager.Startup` `LifeEventManager.Shutdown` | LifeEventManager returns for event ids with no life event before building the household actor list | none found |
| `buffpausedtimeout` | #540 | c | `BuffManager.AddElementPaused` | a paused-only moodlet re-add gets the tuned timeout instead of float.MinValue | none found |
| `mooddriftnoalloc` | #540 | a | `BuffManager.AddElementPaused` | the mood drift pass sums buffs with a scratch array and a struct walk (no allocation) | none found |
| `timeralarmleak` | #540 | b | `ActiveTimerNode.RemoveListenerFromTracker` `ActiveTimerNode.TimerCallback` | a wish timer node's alarm is removed when the node leaves play, and an orphan timer alarm ends at its next fir | part: Overwatch `CleanupTimers` removes empty / bad-Sim alarms at load |
| `wishfeedbackkeep` | #540 | c | `ActiveTimerNode.RemoveListenerFromTracker` `ActiveTimerNode.TimerCallback` | a live wish keeps its event listener when its feedback function throws (EventTracker dropped it for good) | none found |
| `wishsubjectgone` | #540 | c | `DreamsAndPromisesManager.ObjectDestroyedDelegate` `DreamsAndPromisesManager.SetToUpdate` | a gone subject's promised wish gets EA's failure notice and sting; wishes kept after a death stay filed under  | none found |
| `wishupdatelost` | #540 | c | `DreamsAndPromisesManager.SetToUpdate` `DreamsAndPromisesManager.ChildTakenBySocialWorkerCallback` | a SetToUpdate made while the wish drain runs that manager's Update is kept (EA's drain removed it) | none found |
| `socialworkerpromise` | #540 | c | `DreamsAndPromisesManager.ChildTakenBySocialWorkerCallback` | a promised wish about a child taken by the social worker is removed (EA killed it but its timeout skips promis | none found |
| `buffsrcempty` | #540 | a | `LifeEventManager.Startup` `LifeEventManager.Shutdown` | emptied moodlet ref-count entries are dropped after UpdateBuffs, so a later id-less add is not cut to one upda | none found |
| `displaycasereset` | #540 | c | `MetalMineralPotionDisplayCase.DoReset` `MetalMineralPotionDisplayCase.Cleanup` | a reset of the gem/metal/potion display case keeps its contents | none found |
| `groupchatpriority` | #540 | c | `SeatedSocialInteractionA.PushGroupChatB` `MountedSocialInteractionA.PushGroupChatB` | an autonomous group chat's GroupChatB still queued behind the receiver's autonomous head is raised to NonCriti | none found |
| `spotedgememo` | #540 | a |  |  | inert: NRaas GoHere replaces `GoHome` (`GoHomeEx.Run`): GoHere Sims skip ours (docs/104 guard) |
| `spotcut` | #540 | a |  |  | inert: NRaas GoHere replaces `GoHome` (`GoHomeEx.Run`): GoHere Sims skip ours (docs/104 guard) |
| `unseenservicewait` | #540 | a | `BarInteraction.Run` |  | none found |
| `spotbusydefer` | #540 | a |  |  | inert: NRaas GoHere replaces `GoHome` (`GoHomeEx.Run`): GoHere Sims skip ours (docs/104 guard) |
| `unseenpianist` | #540 | a |  | an unseen NPC pianist's PianistPerform loop passes every kUnseenLoopSkipStepMin (needs kUnseenLoopSkipStep), n | none found |
| `unseenmusicwatch` | #540 | a |  | an unseen NPC watching a piano (Piano+Watch) ends at kUnseenLoopSkipCapMin through EA's low-LOD end | none found |
| `unseenjam` | #540 | a |  | an unseen NPC's Guitar+Jam loop passes every kUnseenLoopSkipStepMin (needs kUnseenLoopSkipStep), never ended e | none found |
| `unseenshow` | #540 | a |  | an unseen NPC's ShowVenue+AutonomouslyAttendShow wait passes every kUnseenLoopSkipStepMin (needs kUnseenLoopSk | none found |
| `bouncerplace` | #540 | a |  | an unseen velvet-rope bouncer is placed on RoutingSlot_2 facing its way instead of routing there (WatchDoor Ru | none found |
| `unseenservice_offscreen` | #540 | a | `BarInteraction.Run` |  | none found |
| `unseenloopskip` | #540 | a | `Autonomy.get_MaxTimeForLowLODInteractionDuringTheDay` `Autonomy.get_MaxTimeForLowLODInteractionDuringTheNight` | an unseen NPC's allow-listed autonomous loop ends at kUnseenLoopSkipCapMin through EA's own low-LOD end (DoLoo | none found |
| `rhentryrelaxnear` | #540 | a | `RabbitHole.RouteNearEntranceAndEnterRabbitHole` | an off-screen NPC near viewers whose entry route failed walks to another door of the same rabbit hole; no tele | none found |
| `playerfirst` | #540 | a |  | a route the player ordered (household / selected Sim, UserDirected interaction) is marked for ts3native's asyn | none found |
| `routefailprobe` | #540 | a | `RabbitHole.RouteNearEntranceAndEnterRabbitHole` | an IsPointInLotSafelyRoutable spot probe run on the selected Sim for a placed NPC is logged as a probe, not as | none found |
| `rhentryrelax` | #540 | a | `RabbitHole.RouteNearEntranceAndEnterRabbitHole` | an unseen NPC whose rabbit hole entry route failed arrives by EA's route-less entry (AnimateEnterRabbitHole do | none found |
| `unseenservice` | #540 | a | `BarAdvanced.EnqueueInteractionAndWait` `BarInteraction.Run` | a Late Night bar drink, food or bartender chat between two unseen low-LOD NPCs completes at once with EA's end | none found |
| `zoomtier` | #540 | a |  | an on-screen NPC with the camera >= 250 m away or in map view counts as off screen for the tier-2 (off-screen, | none found |
| `balancingballcareer` | #540 | c | `MagicBroomArena.RouteToTipJar` `MagicGnomeElvis.TryToCreateElvisGnome` | balancing ball trick offered to performance artists only (Run assumes the career) | none found |
| `bartendinghourlypay` | #540 | c | `Bartending.OnStopBartending` | bartending hourly pay that EA announces is paid | none found |
| `bodysculptorweight` | #540 | c | `SocialCallback.RunPostWoohoo` | the body sculptor no longer mirrors thin weights (Thin makes thin Sims thinner) | none found |
| `boxofmysterywoohoo` | #540 | c | `WooHooInBoxOfMystery.Run` | Try for Baby in the Box of Mystery rolls pregnancy only after a woohoo that happened | inert: NRaas Woohooer replaces the box Try for Baby (`BoxOfMysteryWooHoo`): ours does not run for Woohooer's interaction |
| `broomarenachoice` | #540 | c | `MagicBroomArena.GetMagicBroomToUse` | Ride in Arena from a broom uses that broom (and no longer rewrites a running ride's broom) | none found |
| `broomtipperformer` | #540 | c | `MagicBroomArena.RouteToTipJar` | broom arena watcher skips the tip when the ride ended during the route (no paid NRE) | none found |
| `buffetemptypick` | #540 | c | `ResortBuffetTable.ConsumeMoreFood` | resort buffet second helping only picks tables with food (no ArgumentException) | none found |
| `bullridercleanup` | #540 | c | `BuffMummysCurse.OnTimeout` `MagicBroomArena.RouteToTipJar` | a second rider's cleanup no longer clears the Sim still riding the mechanical bull | none found |
| `chipanycount` | #540 | c | `TraitChipStaticData.RemoveComponentsFromInventory` `TraitChipStaticData.RemoveComponent` | "Any" chip component takes only the missing count from medium/large blanks | none found |
| `chipcomponentorphan` | #540 | c | `TraitChipStaticData.RemoveComponentsFromInventory` `TraitChipStaticData.RemoveComponent` | consumed trait chip components destroyed instead of left in the world | none found |
| `chipfamilycrystal` | #540 | c | `TraitChipStaticData.RemoveComponentsFromInventory` `TraitChipStaticData.RemoveComponent` | trait chip crafting consumes family-inventory crystals from the family inventory | none found |
| `consignmentlastday` | #540 | c | `ConsignmentRegister.GetValueOfSale` `ConsignmentRegister.GetChanceOfSale` | consigned items sell on day 5 at the last price and return after it (no IndexOutOfRange in the daily alarm) | inert: NRaas Consigner removes EA daily alarms (`ConsignmentRegister.sDailyAlarm` etc.) and runs its own `ConsignmentHelper.Consign`; it still calls EA `GetValueOfSale` / `GetChanceOfSale`, so value/chance fixes apply, the day-5 rule is Consigner's |
| `consignmentskillcurve` | #540 | c | `FortuneTellerCareer.CreateReadingChancesArrays` | consignment sale chance uses the skill curve, not the frequency curve (sales no longer fall as Consignment ski | inert: NRaas Consigner calls EA `GetChanceOfSale`: ours still applies inside Consigner |
| `consignmentstockleak` | #540 | b | `BotShopRegister.InitializeData` | expired and rotated shop-stock objects of the three consignment registers are destroyed, not leaked | part: Overwatch `FixConsignment` calls `Consignments.Cleanup` at load |
| `daycarelatealarm` | #540 | c | `AttractSpaceObject.Cleanup` `WooHooInBoxOfMystery.Run` | a late-parent daycare alarm for a child who already left is skipped instead of throwing | none found |
| `elvisgnomecap` | #540 | c | `MagicGnomeElvis.TryToCreateElvisGnome` `MagicGnomeElvis.TryToCreateElvisGnomeNearFPBB` | one Elvis gnome per home lot (intent inferred from MagicGnomeVampire) | none found |
| `elvisgnomeguard` | #540 | c | `MagicGnomeElvis.TryToCreateElvisGnome` `MagicGnomeElvis.TryToCreateElvisGnomeNearFPBB` | Elvis gnome spawn/music guards: no-home throws, FPBB plate NRE, leaked gnome, stacked music loops | none found |
| `fortunetellerscamodds` | #540 | c | `FortuneTellerCareer.CreateReadingChancesArrays` | scam-artist fortune tellers at levels 1-9 use the scam odds, not the mystic odds (shared array) | none found |
| `gourmetpetrecipe` | #540 | c | `CatToyWallDancer.GetValidSims` | gourmet pet food: horse-only household keeps granola; no empty-list throw | none found |
| `harvesteatvfx` | #540 | c | `NectarBottle.DuplicateData` `ResortBuffetTable.ConsumeMoreFood` | pet harvestable-eating effects stored and stopped (no leaked native effects) | none found |
| `herbqualityfloor` | #540 | c | `HerbalMedicineExperiment.DecreaseHerbQuality` | herbal medicine experiment no longer lowers a Horrifying herb out of range | none found |
| `influencefollowerbonus` | #540 | c | `SocialNetworkingSkill.ModifierForInfluenceSkills` | influence skill-gain bonus scales with blog followers (float division; intent inferred) | none found |
| `jetpackflyherecleanup` | #540 | c | `FlyHere.Cleanup` | Fly Here cleanup without an active jetpack skips StopFX (no NRE, jigs destroyed) | none found |
| `krakenfriendsailor` | #540 | b | `KrakenManager.KrakenAttackEndAlarmCallback` `KrakenManager.KrakenAttackOneShot` | Friend of the Kraken immunity kept when a Sailor sits before the friend | dup?: simler90 changes `KrakenManager.KrakenAttackEndAlarmCallback` branches [inferred] |
| `mermaiddehydratelook` | #540 | c | `PingPongTable.GetSimsOnLotWithSim` `PingPongTable.GetPotentialPlayers` | a mermaid's look-ats come back after Dehydrate | none found |
| `mermaidsharknull` | #540 | c | `SummonShark.Run` | Summon Shark failed placement takes EA's existing-shark fallback instead of a NullReferenceException | none found |
| `meteormagnetorphan` | #540 | c | `AttractSpaceObject.Cleanup` | a cancelled meteor-magnet attract destroys its out-of-world fragment | none found |
| `mummycursetrait` | #540 | c | `BuffMummysCurse.OnTimeout` | mummy curse removes a visible trait (not a hidden one) before adding Evil | none found |
| `nanitemerge` | #540 | c | `BotBuildingSkill.MergeTravelData` | future travel merges the collected nanite list (and copies the schematics list) | none found |
| `naniteroutefail` | #540 | c | `ReverseEngineer.Cleanup` | nanite terrarium not destroyed when reverse engineering ends before the pickup | none found |
| `nectarconsignvalue` | #540 | c | `SimDescription.SetBodyShape` `StrayWildlifeGnome.ShowTns` | consignment-shop nectar bottles roll their value over the tuned range, not its minimum | inert: NRaas Consigner calls EA `GetValueOfSale`: ours still applies |
| `nectarcopylevel10` | #540 | c | `NectarBottle.DuplicateData` | bought/tasted nectar copies keep the level-10 maker flag and its effects | none found |
| `petputdownwait` | #540 | c |  | a carried pet waits 30 sim-min for a carrier whose PutDownPet already failed (no FinishLinkedInteraction on it | none found |
| `pingpongopponent` | #540 | c | `WorldPond.CanSkateOnWorldPond` | ping pong opponents and picker exclude pets, babies and toddlers | inert: NRaas Woohooer `PlayJuicePongEx` replaces juice-pong player pick |
| `puddlereach` | #540 | c |  | autonomy stops re-picking a rain-puddle splash whose run just reached no puddle on the same lot and puddle set | none found |
| `scarepriority` | #540 | c |  | an autonomous Scare/StaticShock receiver still queued after the scarer routed is raised to NonCriticalNPCBehav | none found |
| `snackbowlslobvfx` | #540 | c | `HerbalMedicineExperiment.DecreaseHerbQuality` `WorldPond.CanSkateOnWorldPond` | slob snack-bowl food effect is a one-shot (no leaked effect per snack) | none found |
| `springdanceclear` | #540 | c | `SpringDanceFloor.CheckForDanceWinners` | spring dance floor tally cleared after the king/queen judging | none found |
| `straygnometns` | #540 | c | `StrayWildlifeGnome.ShowTns` | the stray-wildlife gnome notice names the chosen animal, once | none found |
| `worldpondskatetest` | #540 | c | `WorldPond.CanSkateOnWorldPond` | Skate on a frozen world pond honours the shared skate test (robot form, already skating, full area) | none found |
| `tagunseen` | batch 1 | a |  | opt-in: all-unseen-townie Tag games skip the run routes (TagUnseen.cs, docs/60 §3) | none found |
| `spotcheck` | batch 1 | a | `GoHome.Run` | opt-in: a GoHome / lot-trip teleport whose landing spot is in view walks instead (Fixes.cs, docs/560 §7) | inert: NRaas GoHere replaces `GoHome` (`GoHomeEx.Run`): GoHere Sims skip ours (docs/104 guard) |
| `safespotpool` | batch 1 | a | `GoHome.Run` | opt-in: SafeSpot never answers a pool or below-ground outdoor cell (Fixes.cs BelowGroundOutdoors, docs/74 §13) | inert: NRaas GoHere replaces `GoHome` (`GoHomeEx.Run`): GoHere Sims skip ours (docs/104 guard) |
| `unseenoutfitdefer` | batch 1 | a | `Sim.RefreshCurrentOutfit` `SimDescription.Instantiate` | issue #451, off: an unseen townie's instant outfit build waits until the Sim can be seen (UnseenOutfitDefer.cs | none found |
| `virtualhomewalk` | batch 1 | a |  | GoToVirtualHomeInternal takes back its walk-style request (busted-party guests with apartment homes kept runni | none found |
| `detailpriorityexact` | batch 1 | a | `Lot.CalculateDetailPriority` | Lot.CalculateDetailPriority gives EA's answer without building the household actor lists on every lot pass (ex | none found |
