# 575: object use and ownership, with native reads (handset state, use lists, in-use inventory items)

2026-11-15. Static only. The EA code was read in `game-copy/decompiled`, which stays local. Native code was read in the Ghidra project `game-copy/ghidra/TS3`, through `tools/ghidra/run-headless-linux.sh` with `DecompileTo.java` and `DumpPtrs.java`, at `-Xmx3g` under nice. Built, NOT RUN.

This is the first batch under the standing rule "reverse engineer anything": each lead is followed from the script call into the native code. Native findings are marked [verified: decompile] or [inferred].

**Checks.** Each fix has a pure check, `Ts3Fixes.Fixes.<Name>Check("run"|"result")`, kept in `ObjectUseLogicChecks.cs`. A check prints `controls ok=` and `members found=`. It gives `verdict: BROKEN` with the switch off, `verdict: FIXED` with it on, and `UNEXPECTED` otherwise. The repros are in `tools/harness/repros.json`, under the ids `<switch>-check`.

**Rules.**
- Every switch is off by default.
- Every fix is an EA bug fix.
- No Sim is moved, and no object is created or destroyed.

| § | Switch | Slots | File | Status |
|---|---|---|---|---|
| 2 | `handsetreset` | 2679-2680 | `ObjectUseLogic.cs` | verified (script + native save/load) |
| 3 | `flowersinuse` | 2681-2682 | `ObjectUseLogic.cs` | verified (social data + script) |
| 4 | `uselistprune` | 2683-2684 | `ObjectUseLogic.cs` | mechanism verified, trigger inferred |
| 5 | `chestuselist` | 2685-2686 | `ObjectUseLogic.cs` | verified (docs/574 parked lead) |

## §1 Trigger revisit for today's [inferred] fixes

| Fix | What RE and a re-read show | Status now |
|---|---|---|
| `phoneloadreset` (573 §1) | The handset picture is native state, not just the managed flag. `PhoneWall`/`PhoneTable.SetHandsetState` call `GameObject.SetGeometryState` -> `IC_World_ObjectSetGeometryVisibilityState 0x78CB70`. That looks up the object (`ObjectService_FindById 0x939560`), takes its VisualState component (mask `0x11E92A8`, docs/45), hashes the state name with FNV-1a over the lowercased name (`0x7CFCC0`), and stores the hash at component `+0x14` through `0x95FEF0` (written only when it differs). The VisualState vtable is `0x1044AB4`. Its save slot `+0xC` = `0x95F220` writes `+0x8`, `+0x14` and `+0xC`. Its load slot `+0x8` = `0x95F290` reads `+0x14` back for stream version >= 2, from `ObjectService_CreateFromStream 0x939B60` -> `0x935A70`, one vtable `+8` call per component [verified: decompile]. So a hidden handset is saved and reloaded with the object. Managed side [verified]: `InteractionQueue.OnReset` runs `Cleanup` on every popped interaction, and the phone interactions' `Cleanup` calls `ReplaceHandset(actor, null)`. That clears `HandsetPickedUp` but plays no exit animation, and only the animation event (`ShowHandsetInPhoneModel`) shows the handset again. **Real trigger, found:** a reset or cancel during a landline call leaves the flag false with the geometry on "baseOnly", for good. `phoneloadreset` keys on the flag, so it misses this case: §2 adds the fix. The flag-true-at-load case of 573 §1 stays [inferred]: it needs an interaction that vanishes without `Cleanup`. | narrowed; new fix §2 |
| `phonefixup` (573 §10) | No native step is involved. Phones travel in the managed `Inventory.ImportContent` path, and the type check `Sim.FixUpCellPhone` runs only from `Sim.PostWorldLoadInit` [verified]. The paths that grant objects after a load (age-up, bin placement, instantiation) were re-read, and none runs the check. | mechanism verified; that players hit it with old phones is inferred |
| `unitravelflag` (573 §8) | `TravelUtil.PlayerMadeTravelRequest` is a plain static, not `[PersistableStatic]`, so it is never saved. It survives a world quit in memory. Writers: `GameStates.ClearTravelStatics` (from `GameStates` 1185 and `InWorldState` 233) and the travel and time-portal paths [verified]. `Gameflow.IsSaveDisabled` reads it ("Traveling" save error). | still inferred: needs a log of an aborted University trip |
| `movingflag` (574 §3) | `MovingSituation.sbMovingInProgress` is `[PersistableStatic]`, so the managed persist writer saves it. Situations come back at load through the parameterless ctor, which re-adds the root to `sAllSituations` [verified]. A new game from a world file exits every situation (`AutonomyManager` 217), so `CleanUp` clears the flag there. The only way left is a `MovingSituation` that never reaches `CleanUp` (an exception in `Exit`) before a save. No native involvement. | still inferred, narrowed |
| `firetrapcount` hidden units (570) | Not revisited in this pass. | open |

## §2 handsetreset: a landline shows no handset after a cut call

The mechanism is in §1, row `phoneloadreset`.

**Fix.**
- A postfix on the private `Phone.ReplaceHandset(Sim, StateMachineClient)`. When there is no state machine, so no exit animation will run, it calls the phone's own `SetHandsetState(false)`. This applies only to landlines; on cell and smart phones `SetHandsetState` is empty.
- At world load, every landline with the flag false and an empty use list gets the same call.

`0x95FEF0` changes nothing when the state is already right, so the call on a phone that is already fine is free [verified]. A landline in use at load is left alone, so a call resumed from a saved stack keeps its lifted handset.

**Counters.** [2679] handsets put back, [2680] errors.

## §3 flowersinuse: a rejected bouquet stays "in use" for good

**Bug** [verified]. In the social data (EP4 `Give Flowers`), both the Accept and the Reject rule run `GiveFlowersBeforeCallback`, which marks the bouquet in use with `Inventory.SetInUse`. Only Accept has an after-callback, `GiveFlowersAcceptAfterCallback`, which calls `SetNotInUse`. `Sim.GiveFlowers` has no `Cleanup` of its own. `InventoryItem.mInUse` is a saved field, and no reset path clears it (the only writers are `SetInUse`, `SetNotInUse`, `SetStackInUse` and the ctor). The effect:
- The rejected bouquet can never be dragged, sold or given again.
- The next Give Flowers skips it (`Find(..., checkInUse: true)`) and buys a new one, so it charges the cost again.
- A Give Flowers cancelled after the before-callback does the same.

**Fix.**
- A prefix on `SocialInteractionA.Cleanup`, for `Sim.GiveFlowers` only. A bouquet still in use in the giver's inventory is set not in use. After an Accept the bouquet has already left the giver's inventory, so nothing changes.
- At world load, an `IFlowers` in use in a Sim's inventory is freed when that Sim has no Give Flowers queued or running and no queued interaction targets the bouquet.

**Counters.** [2681] bouquets freed, [2682] errors.

## §4 uselistprune: objects "in use" by Sims who are gone

**Mechanism** [verified]. `GameObject.mActorsUsingMe` (behind `UseCount`, `IsActorUsingMe` and `OtherActor`; 243 readers) and `mReferenceList` are saved fields. EA wrote `GameObject.ValidateAllReferenceListsAndUseLists`, which drops entries whose script proxy is gone (`Simulator.GetProxyPreInit` -> `IC_Simulator_GetTaskImpl 0x76CA60`, a host lookup through vtable `+0x17C` -> `+0x18`). Nothing calls it (grep over all five script DLLs). Its loop also reads `reference.ObjectId` with no null check.

**Trigger** [inferred]: a Sim destroyed while still on a list, through move-out, travel or deletion. Use-list adds that sit outside an interaction's `StandardEntry`/`StandardExit` are not undone by `Cleanup`: `Sim.cs` 3549/3779, routing slots, `SimLine`.

**Fix.** EA's own pass, run at world load and made null-safe. It also drops null and destroyed entries, and removes them from the list directly, as EA's pass does, so there are no `RemoveFromUseList` side effects. The cost is O(objects), once per load. A live Sim is never removed.

**Counters.** [2683] entries dropped, [2684] errors.

## §5 chestuselist: a cut treasure-chest open keeps the Sim on the chest (docs/574 parked lead)

**Bug** [verified]. `TreasureChest+OpenWithAnimation.Cleanup` overrides `Cleanup` without calling `base.Cleanup()`. If the open is cut between `StandardEntry` and `StandardExit` by a reset or an exception, the base step `StandardExit` (which removes the Sim from the use list) never runs. Sim reset only runs the interactions' `Cleanup`, and no global use-list scrub exists. The list is saved, so the chest stays in use by that Sim. Re-reading the code settles the impact question docs/574 left open.

**Fix.** A postfix on that `Cleanup`: the Sim is taken off the chest's use list when still on it. After a normal open, `StandardExit` has already removed the Sim, so nothing changes. This does not overlap `sandscoins` (574 §4), which hooks `TreasureChestOpenedCallback`.

**Counters.** [2685] Sims taken off, [2686] errors.

## §6 Inventory and ownership: other paths read, not fixed

| Path | Finding | Status |
|---|---|---|
| `Inventory.MoveObjectsTo` (household merge `BinCommon.MoveInventoryInto`, moving split, fish tank, display cases, servo sale, horse to rider) | An item the target refuses is destroyed (`TryToAdd` false -> `Destroy`) | by design; the loss is real but rare (family inventory takes almost everything); parked |
| `Inventory.MoveItemsToManager` -> `IC_World_MoveObjectToObjectManager 0x789420` | Native: it takes a reference on the object, finds the target manager (the current lot at `[0x11ECBC4]+0xB0` +0x48/+0x4C fast path, else lot search `0xC60D30`), then the old manager vtable `+0x3C` (remove) and the new one `+0x30` (add). It does nothing when the target is missing, so the object stays with its old manager [verified: decompile] | no bug seen |
| `IC_World_RemoveObjectFromObjectManagerImpl 0x784E30` -> `0x798D40` | Owner `+0x30` vtable `+0x3C`, then object event `0xC`; the reference is dropped at the end [verified: decompile] | matches docs/50 |
| 53 `Inventory.SetInUse` call sites | Sampled: Bed read-to-sleep, Crib, Photography, Subway busking, PetBowl and Sarcophagus pair the call in `Cleanup` or a property; GiveFlowers does not (§3) | the rest is unscanned (lead for the next pass) |
| `TravelUtil.GetPartnershipBonuses` | `Array.IndexOf(kVacationWorldNames, CertificatesWorld)` into a 3-slot array; -1 would throw only for a certificate from a world outside the list | not reachable without mods |
| `GameObject.InUse` | Reads only the parent chain (held by a Sim), not the use list | API note |

## §7 Parked leads from docs/571-574 revisited for native knowledge

- **574 `TreasureChest.OpenWithAnimation.Cleanup`:** fixed in §5.
- **573 tuition on a reset van, and the failed van after a dropout:** managed only; no native knowledge changes the verdict.
- **574 `WorkInRabbitHole` never-true test:** managed only.
- **The rest of the 15 + 14 rows:** no native component found. They stay parked for the reasons in their docs.

## §8 busyflagreset: a self-cleaning litter box, a stage light or a chocolate fountain stays busy for good (added 2026-11-20)

Found by a scan of the scripts for a saved `bool` field that is set to true, then yields (animation, route, `Simulator.Sleep`), then is set back to false, with no `finally` and no reset in the class's `DoReset`, `Cleanup` or load code. The scan had 51 hits. Most were cleared elsewhere, saved on purpose (the invention workbench's Frankensim progress), or harmless. Three block an object for good:

- **`LitterBoxUber` (Pets, self-cleaning box)** [verified in the scripts]: after a cat's use, `UseLitterBox.UpdateMotivesAndDirtyLevel` starts `RunSelfCleanOneShotTask` as a one-shot with `OneShotFunctionDisposeFlag.OnReset`.
  - The task sets `mIsCleaning` and `mSelfCleanStateMachine`, plays the `SelfClean` state (`RequestState` yields), then clears both.
  - An object reset kills the one-shot: `GameObject.DoReset` calls `RemoveOneShotFunctionOnReset`, after `LitterBox.DoReset`'s own footprint work. A save taken during the animation stores `mIsCleaning = true`; nothing clears it at load.
  - After that, every `UseLitterBox` waits 5 sim minutes in `WaitForLitterbox` and fails. Only a finished use starts a new self-clean, and `RunSelfCleanOneShotTask` does nothing while `mSelfCleanStateMachine` is not null. So the box is never used again [verified in the scripts]. Where the cats go instead (the floor) is [inferred]: the box's own pee-on-floor push needs a dirty box, and a stuck box never gets dirtier.
  - The box material can also stay "InUse": the animation sets it at event 117 and back to "Default" at 118.
  - The cheap box is not affected: `IsUber` is false, so it never self-cleans.
- **`StageLightObject` (Showtime stage lights and FX towers)** [verified in the scripts]: `SetMotion` calls `StartSequence(Yield: true)`. It sets `mTransitioning`, yields in `RequestState`, then clears it.
  - A reset or a save in that window leaves it set; `StageLightObject.DoReset` does not clear it.
  - Then every light interaction greys out ("LightIsTransitioning"), and the next `StartSequence` (turn on, colour, auto lights) loops in `while (mTransitioning) Simulator.Sleep(1)` for good: that Sim's interaction never ends.

- **`ChocolateFountain` (store object)** [verified in the scripts]: `CleanOut.Run` sets `mBeingCleaned`, calls `StandardEntry`, animates "Clean", and clears the flag at the end.
  - A reset of the cleaning Sim also resets the fountain: a Sim reset adds its current target when the Sim is on its use list. But `ChocolateFountain.DoReset` only clears the table slots.
  - `CleanOut`'s Test then refuses for good, so the fountain can never be cleaned out again. The clean had already removed its spoilage alarm.

Read and left alone: `MoodLamp.mFairyUsingLamp` (a fairy reset mid-look also resets the lamp through the same use-list rule, and `InitializeLamp` clears it), `MinorPet.mBehaviorCallbackInProgress` (cleared at load and in `StopBehaviorSMC`), `WildHorsePool` (cleared in the catch).

**Fix (`busyflagreset`, `BusyFlagReset.cs`, default off):** after the object's `DoReset` (its own task is gone by then) and at world load (no task from before the save runs), the flag is cleared. For the litter box, the state machine is also disposed (after a reset; a saved one is only dropped) and the material set back to "Default". Objects only: no Sim is touched, and nothing changes while the task still runs. Counters [2714] litter boxes freed, [2715] stage lights freed, [2716] errors, [2717] fountains freed.

**Repro `busyflag-live`:** places a `LitterBoxUber` and a `StageLightObject` on the far lot. `BusyFlagResetCheck start` runs EA's own `RunSelfCleanOneShotTask` and `StartSequence(true)` as one-shots on them, as a cat's use and Set Motion do. `reset` resets both objects while the animation plays, and `state` reads the flags back. The off arm runs first, then the on arm. The fountain has no live step (it is a store object, and the trigger needs a Sim reset mid-clean); `result` counts fountains with the flag set. `result` also counts the busy boxes and lights in the world, so a load of an old save shows whether the state occurs naturally.

## §9 dollhouseslots: a dollhouse frees its play slot twice, and a cut smash greys it out (added 2026-11-20)

Found by a review agent reading the toys folder, then checked by hand [verified in the scripts].

- **Double free:** `DollHouse.Play.Run` ends with `Target.VacateSlot(mPlaySlot)`, and `Play.Cleanup` calls `VacateSlot(mPlaySlot)` again. `mPlaySlot` is never set back to `Slot.None`.
  - `VacateSlot` is a plain `Add` to the saved (`[Persistable]`) free-slot list `mAvailablePlaySlots`. Only `OnCreation` fills that list, with `RoutingSlot_2` and `RoutingSlot_1`.
  - So every finished play adds a duplicate, and the list grows for good. `IsFull` (`Count == 0`) stops working, so more than two children play at once, and two can take the same slot and stand on one spot.
  - `IsEmpty` (`Count >= 2`) is always true: Smash is allowed while children play, and the menu says "Play" instead of "Join".
  - A cut play is not double: Run never reaches its own free, so only Cleanup frees the slot.
- **Smash flag:** `Smash.Run` sets `mBeingSmashed` (`StartSmashing`). It clears it (`FinishSmashing`) only when the loop ends normally, or after animation event 301 has already changed the geometry.
  - A cancel before that event, or a reset, leaves it set. `IsAvailableToPlay` and `IsAvailableToSmash` then refuse, so the dollhouse is greyed out.
  - `Smash` has no `Cleanup`, and `DollHouse` has no `DoReset`. Whether the flag survives a save is [inferred]: unlike the slot list, it has no `[Persistable]`.

**Fix (`dollhouseslots`, `DollHouseSlots.cs`, default off):**
- `VacateSlot` does nothing when the slot is already free.
- After `Smash.Run` ends (normally or by a reset), the flag is cleared. Only one Sim can smash at a time.
- At world load, an idle dollhouse gets its two free slots back, with duplicates dropped, and its smash flag cleared.

Objects only: no Sim is touched. Counters [2110] duplicate slots skipped or dropped, [2111] smash flags cleared, [2112] errors.

**Repro `dollhouse-live`:** `state` first counts the dollhouses already in the save with more than 2 free slots, which carry the bug from earlier plays. Then a dollhouse is placed on the far lot, and `vacate` takes a play slot and frees it twice in Play.Run + Play.Cleanup order. With the switch off the list goes 2 -> 3, with it on 2 -> 2.

## Loop-logic pass (standing rule)

- `UlPrune` walks backwards with `RemoveAt`: no skipped entries and no copy.
- The world-load passes are O(objects) or O(Sims × inventory), once per load.
- `dollhouseslots`: one `List.Contains` over at most 2 entries per freed slot, and once per load over the dollhouses.
- `busyflagreset` runs once per litter box, stage light or chocolate fountain reset, and once per load over those three object types.
- No per-tick work, and no reflection in hot paths: `SetHandsetState` runs through `MethodInfo.Invoke` only on a cut call or at load.
- EA code read: `Inventory.MoveObjectsTo` re-reads `item.List[0]` until empty, and each pass removes one item, so it terminates. `InteractionQueue.OnReset` caps its cleanup at 3 passes. No new findings.

## Tests (combined run, off then on)

Pure checks: `handsetreset-check`, `flowersinuse-check`, `uselistprune-check`, `chestuselist-check`. Live: `busyflag-live` (§8), `dollhouse-live` (§9).

In game, with the switches on:
- Start a wall-phone call and reset the Sim (`resetsim`) mid-call. The handset should be back on the base; counter 2679.
- Give Flowers to a Sim who rejects it. The bouquet should be draggable afterwards; counter 2681.
- Load a save with old stuck items. Counters 2683 and 2681 show what was cleaned.
- In Egypt, reset a Sim while a treasure-chest open is running. The chest is usable afterwards; counter 2685.
- A cat uses a self-cleaning litter box; reset the box while it cleans itself. Cats use it again afterwards; counter 2714.
