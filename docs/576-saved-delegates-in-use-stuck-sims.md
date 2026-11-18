# 576: saved delegates to swapped methods; objects stuck "in use"; locked and stuck Sims

2026-11-15. Static only. Built, NOT RUN. The EA code was read in `game-copy/decompiled`, which stays local. Native code was read in the Ghidra project `game-copy/ghidra/TS3`, at 3G and under nice.

## §1 swapdelegates: a saved delegate to a swapped method loads as null (our bug, every switch off)

**What is wrong.** Every one of our swaps is installed even with all switches off. A save then names the hook class instead of the game class for any delegate to a swapped method. On load, EA drops whatever held that delegate. For example, no NPC trick-or-treaters come after a save on Spooky Day before 19:00, and the NPC holiday party is lost the same way. The NRaas agent found this and verified the EA side.

**The persist path** [verified, ScriptCore decompiled; mono `MonoMethod` read with ilspycmd from `game-copy/assemblies/mscorlib.dll`]:

1. **What the delegate stores.** `DelegateFormatter.Write` and `MulticastDelegateFormatter.Write` write `(Target, Method)` for each invocation entry.
2. **What the method record holds.** `PersistWriter`, case `MethodInst`, writes `Method.DeclaringType`, `Method.Name` (lambda names are cut at `>`) and the parameter types.
3. **Why the class is wrong.** `MonoMethod.DeclaringType` is `MonoMethodInfo.get_method_info(mhandle).parent`, which is the native method's class. `SwapMethod` (`Fixes.cs`) copies the hook's whole header except the name into the target, and that includes the class word, because the hook body's tokens resolve through it. So the save names the **hook class** with the **original method name**.
4. **How the load resolves it.** `PersistReader`, case `MethodInst`, gets the class from the class table (`PersistedClass` -> `Persist.GetTypeByUnqualifiedName`). It then calls `type.GetMethod(name, Instance|Static|Public|NonPublic|FlattenHierarchy|ExactBinding, null, types, null)`.
5. **Why it comes back null.** The hook class has no method of that name. The one exception is a hook that derives from the game class, such as `BrPregnancyHook : Pregnancy`, where the lookup finds the inherited method. So the result is null.
6. **What EA does with the null.** The formatter's `Precreate` returns a null delegate. `AlarmManager.Read` then drops the alarm. Other holders keep a null field, for example a loop delegate kept in a saved frame.

**Fix** (`SwapDelegates.cs`, ON by default because it repairs our own swaps, as `savedstacks` does; counters [2687] hook classes mapped and [2688] hook classes left alone, both gauges):
- **The mechanism is EA's own class remap.** `Persist.GetTypeByUnqualifiedName` asks `Persist.TypeFound`. The game's handler, `Simulator.TypeFoundHandler`, is registered in `Simulator`'s static ctor. It replaces a found type by `ScriptProxy.SubstituteClass(FullName)`, which is the table that `ScriptProxy.SetLogicReplacement` fills.
- **Which hook classes are mapped.** `Patch` now records each swap's owner class, captured before the swap. A hook class whose swaps all target one game class, and which declares no methods besides its hook/stub pairs, is mapped to that game class. The reader then finds the target under its own name, and the delegate runs our hook exactly as it did before the save.
- **Which are left alone.** A hook class with targets in two classes, or with other methods, is left alone. Its other methods could be helpers, `this`-capturing lambdas or its own callbacks, and a save names those by the hook class. `SwapDelegatesCheck result` lists them.
- **Saved stack frames.** The same lookup now resolves a saved frame in a hook body. It resumes in the body it was saved in, with a matching checksum. `savedstacks` still remaps the frames it cannot resolve.
- **What it does not touch.** Nothing is written differently, saves made without our package are untouched, and nothing changes for game classes.
- **Switching it off.** The table is reapplied at world load, so turning the switch off takes effect at the next load.

**Affected list.** These are swapped targets that EA passes as a delegate, found by `swap_delegates.py` over the audit's ~450 targets, then filtered to delegate contexts and the target's own class. For each I give where the delegate is stored and whether that storage is saved:
- **Alarms (saved, dropped on load):**
  - `TrickOrTreatSituation.SendNPCTrickOrTreaters`
  - `NpcSeasonalPartyManager.ScheduleNpcParty`
  - `ResortManager.WeddingCallback`
  - `VisitSituation.VisitorWantsToComeIn.WaitedTooLongForGreeting`
  - `PrivateEyeInteractions.SnoopForClues.SnoopAlarmDelegate`
  - `SocialComponent.RelationshipDecay`
  - `Gig.PrepareVenueForShow`
  - `Wildflower.OnGrowWildflowersOnLot`
  - `NPCShowManager.CreateNPCShow`
  - `Phone.ConsiderGeneratingPhoneCall` (also the re-arm alarm of 573 §3)
  - `LotManager.CleanUpLotsAndInventories`
  - `PetPoolManager.AlarmCallBack`
  - `Pregnancy.HaveTheBaby` (already safe through inheritance)
- **Situation and interaction fields or saved frames:**
  - `BoardingSchoolPickUpSituation.CreateCarAndRoute.OnFailure`
  - `Phone.SendWooHootyText.GoToLotSuccess`
  - `WeddingCeremony.RouteToWeddingArch.OnRouteFailed`
  - `GameObject.DestroyObjectOnFadeOutCallBack`
  - `BedSleep.LoopHandler`
  - `VelvetRopes.WatchDoor.LoopDel`
  - `OccultVampire.VampireHunt.LoopDel`
  - `School.AttendGraduation` / `GraduateInCityHall.WaitingLoopDel`
  - `BonFire.SpiceHitFire`
  - `Punishment.SneakOutSuccess`
- **Event listeners and UI (not saved, unaffected):**
  - `FirefighterEmergencySituation.OnSimLotChanged`
  - `OccultRobot.OnConversationLeft`
  - `PerformanceCareer.OnJobMissed`
  - `BirthdayParty.OnAgeTransition`
  - `HighFertilityStore.OnNewBaby`
  - `HudController.OnCameraMapViewEnabled`
  - `PlayFlowInWorldFlow.PlayGame`
  - `OptionsModel.SaveGameTask`
  - `CASClothingCategory.AddGridItem`

**Impact per affected entry.** All of these swaps are installed with every switch off.
- "Released" is the first tag that contains the hook's commit (`git tag --contains`). "none" means it is only on master after v0.2.9.
- "Saved?" is the `AlarmType` or the holder.
- "After load" is what a lost delegate does [verified from the EA code unless marked inferred].
- `Shuffleboard.Play.Cleanup` was a scan false positive (the hit was BabyDragon's own `Cleanup`) and is dropped.

| Target (hook, file) | Commit | Released | Saved? | After load |
|---|---|---|---|---|
| `GameObject.DestroyObjectOnFadeOutCallBack` (FadeDestroyHook, DisposeSpread.cs) | 853e1796 | **v0.1.6** | `FadeOut` alarm, AlwaysPersisted | **Stays lost:** an object saved during its fade-to-destroy window (minutes) is never destroyed and stays faded out in the world |
| `VelvetRopes.WatchDoor.LoopDel` (BouncerHook, RouteSources.cs) | 318b16cd | **v0.1.5** | loop delegate in a saved frame | Heals: the null loop delegate throws and the bouncer is reset [inferred: DoLoop with null] |
| `School.GraduateInCityHall` / `AttendGraduation.WaitingLoopDel` (GraduationTableHook, EaFixes.cs) | b99f3702 | **v0.2.5** | loop delegate in a saved frame | Heals by a reset of a Sim saved mid-ceremony [inferred]; now split into `GraduationCityHallHook` / `GraduationAttendHook` and mapped |
| `Wildflower.OnGrowWildflowersOnLot` (WildflowerTaskHook, ScriptHotSpots.cs) | b5581059 | **v0.1.4** | alarm, AlwaysPersisted | No change vs vanilla: EA does not save the lot list (`[PersistableStatic(false)] sLotsToGetWildflowers`), so the reloaded alarm does nothing in vanilla either |
| `SocialComponent.RelationshipDecay` (DecayHook, EaSpread.cs) | b2e2edee | v0.1.5 | NeverPersisted (re-added at startup) | unaffected |
| `LotManager.CleanUpLotsAndInventories` (LotCleanupHook) | b2e2edee | v0.1.5 | NeverPersisted | unaffected |
| `PetPoolManager.AlarmCallBack` (PetPoolHook) | b2e2edee | v0.1.5 | NeverPersisted | unaffected |
| `Phone.ConsiderGeneratingPhoneCall` (PhCallRearmHook) | d8971c8f | none | NeverPersisted | unaffected |
| `NPCShowManager.CreateNPCShow`, `ResortManager.WeddingCallback` | 1e6ed2b2 | none | NeverPersisted | unaffected |
| `TrickOrTreatSituation.SendNPCTrickOrTreaters` (TrickOrTreatHook) | 1e6ed2b2 | none | AlwaysPersisted | Lost for that Spooky Day; HolidayManager sets it again on the next one |
| `NpcSeasonalPartyManager.ScheduleNpcParty` (SeasonalPartyHook) | 1e6ed2b2 | none | AlwaysPersisted | Lost until the next schedule [inferred] |
| `VisitSituation.VisitorWantsToComeIn.WaitedTooLongForGreeting` (VgaWaitHook) | 0f9c9cae | none | AlwaysPersisted | The visitor's give-up timer is lost: they wait at the door until something else ends the situation [inferred] |
| `PrivateEyeInteractions.SnoopForClues.SnoopAlarmDelegate` (PeSnoopHook) | 7155fe6f | none | AlwaysPersisted | Snoop end alarm lost [inferred: the interaction then ends on its own exit] |
| `Gig.PrepareVenueForShow` (GigPullHook) | 1e6ed2b2 | none | AlwaysPersisted | Venue prep lost for that gig [inferred] |
| `BoardingSchoolPickUpSituation.CreateCarAndRoute.OnFailure`, `Phone.SendWooHootyText.GoToLotSuccess`, `WeddingCeremony.RouteToWeddingArch.OnRouteFailed` | 61730641 / d8971c8f | none | situation or interaction callbacks | A failure or success callback is skipped once [inferred] |
| `BedSleep.LoopHandler`, `OccultVampire.VampireHunt.LoopDel`, `BonFire.SpiceHitFire` (event handler), `Punishment.SneakOutSuccess` (EventTracker, not saved) | 0f9c9cae / ce541fd9 / 1e6ed2b2 | none | frames, one-shot handler, listener | Frame cases heal by reset [inferred]; the listener is unaffected |

**Shipped and lost for good:** only `DestroyObjectOnFadeOutCallBack` (v0.1.6 and later). The window is short, so how often players hit it is inferred to be rare. The other shipped entries are not saved, heal by a reset, or match vanilla. A save already affected keeps the faded object; `swapdelegates` does not bring back an alarm that an earlier load dropped.

**Left alone by rule, among the affected:**
- `GraduationTableHook` targeted two classes. It is now split into `GraduationCityHallHook` and `GraduationAttendHook` (GraduationTable.cs, EaFixes.cs), so both are mapped.
- Static scan (`hook_elig.py`): no other affected hook class has helper methods. The live list comes from the check's `result`.

**Check.** `SwapDelegatesCheck` runs every recorded swapped target through the reader's own lookup, as written by the writer:
- With the switch off, the class is the hook class as written: BROKEN.
- With it on, the class goes through the live `Persist.GetTypeByUnqualifiedName` remap: FIXED.
- The probe is `TrickOrTreatSituation.SendNPCTrickOrTreaters`.
- The control is an unswapped method (`AlarmManager.AddAlarm`), which round-trips both ways.
- It prints the count of targets read back and lost, and the lost ones.

**Not covered.** A player who uninstalls our package keeps saves that name hook classes. Avoiding that needs a write-side change, which would be the formatter `Write` methods. That is parked.

## §2 resetinusefree: items stay "in use" after a Sim reset

**Scan.** `inuse_scan.py` covered the 53 `Inventory.SetInUse` call sites. For each site it found the enclosing class and method, and whether the class clears the mark in `Cleanup` or a finally path. 22 classes clear it in `Cleanup` and are safe. The rest clear it only on the success path, or never:

| Class | Why the mark is never cleared on a cut path | Status |
|---|---|---|
| `PutDown.TryToPlaceOnSurface`, `VoodooDoll+PutDown` | The `ParentToSlot` failure returns false with the item in use and the surface still listing the Sim; `Cleanup` only destroys the jig | verified, fixed in §3 |
| `SocialCallback.GiveFlowersBeforeCallback` | Only the Accept after-callback clears it | fixed in 575 §3 |
| `Terrarium.EatBug`, `Scrap.EatScrap`, `Spirit.SpiritJar.Release`, `FishTank.Kill` | The item is consumed on success; a reset before that leaves it in use | covered by §2 |
| `NectarMaker.SquishGrapes` | Every fruit in the barrel is marked and never cleared; `MakeNectar` then consumes all of it | by design (fruit locked once squishing starts) |
| `OmniPlant.Feed` -> `DoFeed` | The mark is handed to the queued `DoFeed`, whose `Cleanup` clears it; queued interactions get `Cleanup` (`InteractionQueue.CleanupInteraction`) | safe |
| `ToyBox`, `Subway.PlayForTips`, `Photography`, `Bed`/`Crib` ReadToSleep | Cleared through `Cleanup` -> helper | safe |
| `Crib.ParentStuffedAnimal` | Object state, not an interaction: in use while it sits in the crib | by design, excluded in §2 |
| Placement interactions (`IPlaceableObject_Place`, `BonFire`/`FirePit` Place, `VideoCamera`, `StartAnythingButton`, `DonatableComponent`) | Cleared after a modal step in `Run`; a reset in between leaves it in use | covered by §2 |

**Native and reset path** [verified]:
- `Sim.DoReset` runs `InteractionQueue.OnReset`, which calls `Cleanup` on every interaction.
- When the Sim is still on its current target's use list after that, `DoReset` adds the target to the reset. `GameObject.DoReset` clears `mActorsUsingMe`.
- Nothing clears in-use marks in the Sim's inventory. `InventoryItem.mInUse` is saved and has no other writer.
- Side finding: this target reset also clears the treasure chest in 575 §5. That leaves `chestuselist` with only the rare "cut without a Sim reset" path, so it is low value.

**Fix.** A postfix on `InteractionQueue.OnReset`. Every item still in use in that Sim's own inventory is set not in use, because the queue is now empty and nothing of this Sim can hold the mark. Toddlers and babies are skipped (the crib stuffed animal). Counters [2689] items freed, [2690] errors.

## §3 putdowninuse: a cut put-down on a surface

**Bug** [verified]. See the table above. The surface use list keeps a live Sim, so `uselistprune` (575 §4) does not drop the entry. A Sim reset does not reset the surface either: the current target is the item, not the surface.

**Fix.** Prefixes on `PutDown.Cleanup` and `VoodooDoll+PutDown.Cleanup`. When the item is still in the Sim's inventory and still in use (the put-down did not finish):
- the item is set not in use;
- surfaces within 3 m that still list the Sim are released, unless another queued interaction of that Sim targets them.

A finished put-down has removed the item from the inventory, so nothing changes. Counters [2691] put-downs repaired, [2692] errors.

## §4 firetrapcount hidden-unit trigger (docs/570 §5 b), RE pass

These steps are verified in the decompiled code:
- `FireFightingJob.FindPossiblePositions` calls `Lot.GetRoutableIndoorPositions(firefighter, ...)`.
- On a lot with virtual residential slots, that method **skips public rooms** and keeps unit rooms. This corrects docs/570's "skips only public rooms" wording; the effect is the same: unit rooms are candidates.
- Each candidate spot must pass `route.PlanToPointRadius(spot, 0.5).Succeeded()`, planned for the firefighter. `Succeeded` is strict: `RoutePlanResultType.Succeeded` only, no partial paths.
- The planner asks the lot's portals through `Sim.OnPortalPathPlan`. `CommonDoor.IsAllowedThrough` returns `DoorRestriction` for any door with a hidden room on either side, for every Sim (docs/552 §1). So a hidden unit behind a door is rejected.
- Stairs and elevators have no hidden-room test.

**Trigger:** a hidden neighbour unit reached by stairs or an elevator, for example a two-storey unit or stairs that land inside a unit. The code path is verified; that the user's lot has such a unit is inferred. `firetrapcount` (b) drops hidden or fogged spots whatever the route says, so it covers this.

## §5 Stuck and locked Sims, pets and occults: leads read this pass (not fixed)

| Lead | Finding | Status |
|---|---|---|
| `PrivacySituation.PostLoad` | It re-registers with `SyncTarget.RoomId` and has no null check: `Lot.AddPrivacySituation` runs, then it throws when the Sim is gone. `mNumberOfPrivacySituations` is not saved and is read only through `HasPrivacySituation` (imaginary friend). Low impact; the portal callback is simply missing | verified code, low impact, parked |
| Same definition failing in a loop | Autonomy has no per-definition failure memory; only route fails per hour are logged (`Autonomy.cs` 723) | next batch: needs a live failure log (`routefaillog` data) to rank sources |
| Pets, horses and occults | Not reached in this pass | next batch (docs/577) |

## Tests

- **Pure checks:**
  - `swapdelegates-check`: off BROKEN, on FIXED. The control is an unswapped callback. `result` lists the hook classes left alone; there should be none among the affected.
  - `resetinusefree-check`
  - `putdowninuse-check`
- **In game:**
  - Save on Spooky Day before 19:00 and reload: trick-or-treaters arrive.
  - Reset a Sim during a counter put-down: the item can be dragged and the counter can be used; counters 2689 and 2691.
