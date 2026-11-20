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

## Loop-logic pass (standing rule)

- `UlPrune` walks backwards with `RemoveAt`: no skipped entries and no copy.
- The world-load passes are O(objects) or O(Sims × inventory), once per load.
- No per-tick work, and no reflection in hot paths: `SetHandsetState` runs through `MethodInfo.Invoke` only on a cut call or at load.
- EA code read: `Inventory.MoveObjectsTo` re-reads `item.List[0]` until empty, and each pass removes one item, so it terminates. `InteractionQueue.OnReset` caps its cleanup at 3 passes. No new findings.

## Tests (combined run, off then on)

Pure checks: `handsetreset-check`, `flowersinuse-check`, `uselistprune-check`, `chestuselist-check`.

In game, with the switches on:
- Start a wall-phone call and reset the Sim (`resetsim`) mid-call. The handset should be back on the base; counter 2679.
- Give Flowers to a Sim who rejects it. The bouquet should be draggable afterwards; counter 2681.
- Load a save with old stuck items. Counters 2683 and 2681 show what was cleaned.
- In Egypt, reset a Sim while a treasure-chest open is running. The chest is usable afterwards; counter 2685.

## §10 Sand notices for every Sim in town: `sandboxtns` (tuning `kSandboxTns`, default off, `SandboxTns.cs`, counters 2174-2175, repro `sandboxtns-live`)

Player report (2026-11-20): "I'm getting notifications from other Sims playing in the sand and finding objects, not my household Sims."

- [verified, decompiled] `PlayInSandJig.FindSomething` (play in sand, the sandbox included) calls `NotificationSystem.ShowWithAutomaticGender(TNSNames.SandboxFoundObject, Actor, found)`. The sandbox's bury step calls it with `TNSNames.SandboxBuryObject`. Neither checks who the Sim is.
- [verified, decompiled] `NotificationSystem.Show` checks only that the notice exists and its pack is installed. So every Sim in town who finds or buries something in the sand pops a notice.
- Fix: a hook on `NotificationSystem.ShowWithAutomaticGender` drops only these two notices, and only when the Sim is not exempt (`Fixes.HouseholdExempt`: household, selected and unknown-scope Sims still get them). The find, the reward and the bury run as EA's.
- Counters [2174] notices dropped, [2175] errors. The slots were free in the source and in this registry (the `registry.ts` reservation read a stale main checkout and offered used slots, so the row was added by hand).
- Repro `sandboxtns-live`: `SandboxTnsCheck run` (pure, with controls), then `live`: EA's call for a Sim outside the household and for the active Sim, switch off then on. Expect off BROKEN / townie shown=True, on FIXED / townie shown=False, active Sim shown both times.
