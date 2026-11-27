# 348. Own-thread object classes and lot add / remove hub coverage (ST-8, ST-12) (2026-11-26)

Static only. Steam 1.67: decompiled script assemblies in `game-copy/decompiled` (local) and `game-copy/bin/TS3W.exe`.
Nothing built, nothing run. Packages ST-8 and ST-12 of docs/332 §2.2 / docs/340 §6. Ledger: docs/51 §617. Handoff:
`docs/handoff/st8-st12-1126.md`.

Tool note: the REA server did not answer during this pass (other lanes were using it), so the native part was read
from the raw bytes of the game-copy exe with a local Capstone script (call / jump rel32 targets and 4-byte immediates
over `.text` / `.rdata` / `.data`, function ranges from `game-copy/analysis/functions.csv`). "[verified]" on a native
row means: the instruction bytes at that address were disassembled and read. Indirect calls through registers are not
seen by this scan; where that matters the row says so.

Related, not repeated: docs/346 (alarm hold hook, `FarHold` owner table), docs/331 §4.9 and §5 E4 (own-thread objects
in the far-object model), docs/341 §1 (Sim tasks), docs/321 §8-§11 (lot hub CP-1 .. CP-4), docs/38 (world manager
functions), docs/28 §5 (lot streaming), docs/343 (sidecar).

## 0. Short answer

**ST-8.** EA script code has three ways to run on its own scheduler entry, and the full list is short:

- 11 classes return the threaded execute type, 7 return the task type, 7 classes derive from `ScriptTask`, 18 derive
  from `Task` [verified, greps over the non-UI assemblies].
- Per **object** and endless, so they keep costing while the lot is far: `Lot` itself, `BirdCage`, `MinorPet`
  subclasses, `MarineLifeBase` subclasses, `TreeOfProsperity` (one-shot with an endless loop), `CrowdMonsterUpdate`
  (one task per crowd monster), plus one global loop that ticks a per-object list every tick (`HoloDiscManager`).
- Of 205 one-shot creation sites outside UI, 148 distinct same-file targets were resolved; 11 have a loop with a wait,
  and only `TreeOfProsperity.SimulateTree` is endless. The other 10 end by themselves when no Sim uses the object, or
  are household / world flow.
- No timer source outside `AlarmManager` fires callbacks: `StopWatch` has no callback API in script code, every use
  is polled from a loop or an interaction [verified grep: 19 create sites, 0 callback setters].
- Pause hook needed (5): `BirdCage`, `MinorPet`, `TreeOfProsperity`, `CrowdMonsterUpdate`, `HoloDisc.Update` (the
  per-object call inside the global loop). `Lot` is covered by docs/346 Hook A plus FO-3. Everything else stays live
  (world-global, per-Sim, bounded, or household flow). The hooks ask the same `FarHold` parked-lot set as the alarm
  hold hook; no second table.

**ST-12.** The CP-1 hub (`0xABD3C0` / `0xABD3E0`) sees only camera streaming and edit-mode entry. It does **not** see a
lot being created or destroyed:

- The add fn `0xAC1130` has exactly two references: the function pointer in `Lot::SetActive` `0xAC20E0` (goes through
  the execute, hub sees it) and a **direct call** at `0xACE1E2` in `Lot_LoadObjectLayer` `0xACE0D0` (lot load: world
  load, Edit Town placement, houseboat create; hub does not see it) [verified].
- The remove fn `0xAB4C00` has exactly one reference, the pointer in `Lot::SetActive` [verified]. A bulldozed or
  unloaded lot never runs it, so `lhRemoves` does not move when a lot is destroyed.
- The real lot lifecycle points are the world events `0xD` (lot added) and `0xE` (lot removed), raised only from
  `0xC64230`, `0xC65700` (add) and `WorldManager_DetachLot` `0xC65830` (remove) through the one raiser `0x78F2B0`
  [verified]. Managed side: `LotManager.AddLot` / `LotManager.RemoveLot`, two callers each [verified].
- So I-591-1 (docs/51 §591) fails in part. The hub needs a second layer: managed patches on `LotManager.AddLot` /
  `RemoveLot` (lot enters / leaves), on the two per-lot object list writers, and on `Lot.MoveIn` / `MoveOut` (scope
  change); native consumers get two new kinds from detours on the three native functions. Design in §6.

## 1. ST-8 method

Greps over `ScriptCore`, `SimIFace`, `Sims3GameplaySystems`, `Sims3GameplayObjects`, `Sims3StoreObjects` (UI assembly
left out: 619 of the 894 `Simulator.AddObject` lines are UI one-shots, never lot state):

1. every `GetScriptExecuteType` / `Init` that returns the threaded or the task type;
2. every class deriving from `Task` or `ScriptTask`, with the shape of its `Simulate` (loop, sleeps);
3. every one-shot creation (`OneShotFunction`, `OneShotFunctionTask`, `OneShotFunctionWithParams`,
   `OneShotFunctionThreaded`): the delegate target is looked up in the same file and its body tested for a loop plus
   a wait (`Sleep`, timed loop helpers);
4. `StopWatch` create sites and callback setters.

Limits: step 3 resolves targets in the same file only (148 distinct targets found; delegates defined in another file
or built from a variable are not followed, about a quarter of the sites) and a loop whose wait sits in a callee is
only caught when the callee name contains `Sleep` or a timed-loop helper. Mods are not scanned (§4).

## 2. ST-8 class list

Loop shape: P = period between wakes. "Far" = the object's lot is parked by the far-object model.

### 2.1 Per object, endless

| Class | How it runs | Loop shape [verified] | State it changes | Far verdict |
|---|---|---|---|---|
| `Lot` | threaded script object, added in `LotManager.AddLot` (`LotManager.cs:2329`) | `Lot.cs:8309-8355`: alarm manager, door privacy, detail priority, display level; reactions and zombie count only when `ShouldSimulate` (active lot, active Sim's lot, world lot, any commercial lot, `Lot.cs:1985-1994`) or when broadcasters with Sims exist; otherwise sleeps `kInactiveLotSleepTicks` (30, `Lot.cs:784`); always one extra 0-tick sleep | lot alarms, portal privacy, reactions | **covered**: alarm part by docs/346 Hook A; the rest is FO-3's lot skip. No new hook here. Note: every commercial lot loops every tick even when nobody is there, which is the per-lot cost FO-3 removes |
| `BirdCage` (EP5) | threaded script object | `BirdCage.cs:1025-1046`: P = 30 ticks; updates the thought-balloon manager when it asks for it | visual only | **pause hook** (skip the body; nothing to compute) |
| `MinorPet` and subclasses (EP5) | threaded script object | `MinorPet.cs:1454-1482`: P = `MinorPetTuning.BehaviorAlarmCallbackFrequency` sim-minutes; once per 24 sim-hours `DeathUpdate`; else `SimulateBehavior` (`MinorPet.cs:1682-1703`: escaped / in inventory / in terrarium / in the wild) | pet age and death, wild-pet movement and despawn | **pause hook** + computed age at release. `DeathUpdate` body not read here [inferred closed form: days elapsed]; a pet in a household Sim's inventory or on a household lot is exempt |
| `MarineLifeBase` subclasses (EP10) | threaded script object | `MarineLifeBase.cs:259-273`: P = 0 ticks (every tick); `TrySimulate` (`:309-320`) updates the state and runs a route | position, state toward a target Sim | **stays live**: sea creatures live on the world lot, which is never parked (docs/346 §3.6). If FO-1's probe finds one on a parkable lot, treat as E4 |
| `TreeOfProsperity` (Store) | one-shot started at init (`TreeOfProsperity.cs:758-763`) | `:767-857`: endless, P = 0.25 sim-minute; inventory check, fruit alarm re-arm, area-of-effect on Sims in range, hour-23 firefly / pixie VFX, fruit state machine | fruit type, AOE benefits on Sims | **pause hook** as docs/331 §4.9 (fruit index computed at release; AOE on no Sims; VFX skipped) |
| `CrowdMonsterUpdate` (EP6) | `ScriptTask`, one per crowd monster (`CrowdMonsterUpdate.cs:8-18`) | `:21-46`: when the monster is active, every tick for `kSimUpdateSleepTicks` (5) and calls `Update`; when not, sleeps 5 ticks | crowd visuals during a show | **pause hook** on the monster's `Update`; an active show on a far lot is skipped (visual). Idle cost today: one wake per 5 ticks per monster |
| `LiteDriver` in `CarNpcWithNoDriver` | `ScriptTask`, one per ambient NPC car | `CarNpcWithNoDriver.cs:21-73`: until the car should drive off: pick a random commercial lot, route to a street parking spot, sleep a random wait | ambient traffic | **stays live**: world traffic, not lot state; count is capped by `TrafficManager`. It reads `StreetParking` of commercial lots, parked ones included: FO must leave street parking data readable on a parked lot |

### 2.2 Global loops that tick per-object lists

| Class | Loop shape [verified] | Far verdict |
|---|---|---|
| `HoloDiscManager` (EP11), threaded singleton | `HoloDiscManager.cs:129-152`: P = 1 tick; 30 tick groups; calls `HoloDisc.Update` for each disc of the current group | **pause hook** on `HoloDisc.Update` (skip when the disc's lot is parked). The manager loop stays |
| `IceCreamTruckManager.UpdateIceCreamTruckTask` | `IceCreamTruckManager.cs:48-61`: P = 300 ticks, one singleton update | live (world) |
| `TrafficManager.UpdateTrafficTask` | `TrafficManager.cs:15-99`: one world loop | live (world) |
| `RoleManagerTask` | `RoleManagerTask.cs:51-164`: one world loop over roles | live; role Sims are FS's subject |
| `Services` (each service is a threaded script object, `Services.cs:54-88`) | one loop per service type | live; service NPCs are FS's subject |
| `SeasonsManager` | `SeasonsManager.cs:4506-4535` | live: docs/331 E5 |
| `AutonomyManager`, `SimUpdate`, `Sim`, `AgingQueue`, `LifeEventManagerTask`, `StoryProgressionTask`, `DynamicChallengeManager`, `SocialFeaturesEventSystem` | per Sim or one per world | docs/341 (far-Sim lane), not object state |
| `ReferenceWatchdog`, `FinalizerTask`, `LoadSaveManagerTask`, `Camera`, `UiMouseClickProcessorTask`, `Gameflow`, `StateMachine`, `ThumbnailManager`, `CASLogic`, `AutoSave`, `ObjectDesignTask`, `SceneReadyTask`, `CameraTestTask`, `SwapTask`, `ImmediateInteractionTask` | engine / UI / mode tasks | never held |

### 2.3 One-shots with a loop and a wait (11 of 148 resolved targets)

| Target | Ends when | Far verdict |
|---|---|---|
| `TreeOfProsperity.SimulateTree` (`TreeOfProsperity.cs:767`) | never | §2.1 |
| `NonAnimatedDoor.TryToClose` (`NonAnimatedDoor.cs:632-650`, P = 10 ticks) | the door closed, or 3 tries while not in use; waits while a Sim uses the door | bounded by Sims on the lot; **demotion rule**: a lot with a pending close task (`mTryToCloseOneShot` not null) is not parked this pass |
| `BlackjackProp.Deal`, `PokerProp.Deal`, `RouletteProp.Deal` (Store; `BlackjackTable.cs:1624`, `PokerTable.cs:1932`, `RouletteTable.cs:1423`) | no active players | bounded by Sims on the lot; same demotion rule (dealer task field not null) |
| `PerformingChildSituation.DestroyStereoOneShot` (`DancerServiceSituation.cs:279`) | stereo use count 0 | bounded by Sims |
| `DaycareChildTooltip.Update` (`DaycareWorkdaySituation.cs:110`, P = 30 ticks) | daycare day ends | household career: exempt, never held |
| `TimePortal.TrySpawnTimePortal` (`TimePortal.cs:2276`) | portal placed | targets the active household's home lot: exempt |
| `GameStates.EditOtherTownSwitchWorlds` (`GameStates.cs:1736`), `ShoppingRabbitHole` list callback, `ScoreScreenGrabHelper.Run` | flow ends | world / UI flow: never held |

Other bounded per-object tasks seen by name, no loop found: sculpture / statue builders (`SimBuilderTask`,
`SimBuilderTaskSculpture`), `SlotMachine` funds effect (`SlotMachine.cs:338`), photo booth, weather stone, front stage
lights. They are started by an interaction or an alarm on the lot, so a parked lot starts none [inferred from the call
sites, not each read].

### 2.4 Yielding alarms

`AlarmManager.InvokeYieldingAlarm` (`AlarmManager.cs:616-625`) starts one `AlarmOneShot` task per yielding alarm fire.
docs/346 §2 already rules that demotion waits until the owner has no entry in `mAlarmOneShotList`. Nothing added here.

## 3. ST-8 pause hook (same table as the alarm hold hook)

One rule for the five hooked classes; it adds no state of its own:

- **Predicate.** `FarHold` (docs/346 §3.1) answers "is this lot parked" with the lot exemption of docs/346 §3.6 (world
  lot, active lot, active household's home lot, a lot with a household or selected Sim on it: never parked). The pause
  hook reads the object's current lot and asks that. An object with no lot, in an inventory, or whose lot cannot be
  read is **not paused** (unknown scope = household).
- **Site.** A managed prefix on the loop **body** method, never on the scheduler: `SimulateBehavior` /
  `DeathUpdate` for minor pets, the thought-balloon update call for the bird cage (or a prefix on the cage's
  `Simulate` replaced by a guarded copy if the body cannot be reached as a method, decided at build), the tree's body
  per docs/331 §4.9, `CrowdMonsterBase.Update`, `HoloDisc.Update`. Paused = the prefix returns without running the
  body. The coroutine keeps its own sleep, so it stays valid and resets as in vanilla (docs/243's rule: no frozen
  coroutine).
- **Cost while paused.** One wake per period per object. That is 1 per 30 ticks for the cage, 1 per pet period, 1 per
  5 ticks per crowd monster, 1 per 30 ticks per disc. Only the tree (0.25 sim-minute) is worth a longer sleep, as
  docs/331 §4.9 already says; the longer sleep must end at release, so the tree hook sleeps at most one periodic-pass
  interval.
- **Release.** FO applies the computed state before `FarHold.Release(lotId)` (same order as docs/346 §3.4): pet age
  in days, tree fruit index. Classes with nothing to compute (cage, crowd monster, holo disc) just resume.
- **Switch.** One sub-switch per class under the far-object switch, default off; with the parked set empty the prefix
  is one static read.
- **Household.** A pet, cage, tree or disc owned by the active household, on its lot, or carried by a household or
  selected Sim is never paused (the lot exemption covers the first two, the "no lot / inventory = not paused" rule
  the third). `[Experimental] HouseholdToo=1` does not lift this: a pause is a skip.
- **No leak.** The hooks hold no rows. The parked set is `FarHold`'s and is cleared by its `ReleaseAll` on world
  change and quit (docs/346 §3.5).

Classes without a hook (anything a mod adds, §4) are docs/331 E4: they stay live, and FO must not park a lot that
holds an object whose class has an own-thread entry and no hook, unless FO-1's probe shows the class is harmless.

## 4. Mod object-tick census: not done

No decompiled mod sources exist under `game-copy` (the compat set is stored as packages), so the mod half of ST-8 is
open. The scan is the same three shapes (§1 steps 1-3) over each mod's script assembly; it can share the scan docs/346
Q6 asks for. Until it is run the rule is E4: an object whose class comes from a non-EA assembly and has its own
scheduler entry keeps its lot live. NRaas note [community-reported, not read here]: NRaas mods run their own alarm and
task wrappers on global tasks, not per lot object.

## 5. ST-12: what reaches the CP-1 hub and what does not

### 5.1 Native call sites [verified, bytes read]

| Function | References | Meaning |
|---|---|---|
| `AddLotObjectsToScene` `0xAC1130` | immediate at `0xAC210F` in `Lot::SetActive` `0xAC20E0` (posted through `0xABE9C0`, execute `0xABD3C0`); **direct call at `0xACE1E2`** in `Lot_LoadObjectLayer` `0xACE0D0` | the load path bypasses the execute |
| lot remove fn `0xAB4C00` | immediate at `0xAC2123` in `Lot::SetActive` (posted through `0xABEA50`, execute `0xABD3E0`); no other reference | runs only when a live lot is switched off |
| `Lot::SetActive` `0xAC20E0` | calls at `0xC6C7C3`, `0xC6C7CE` (lot scoring `0xC6C290`), `0xC6A0F9` (`0xC6A080`, called from scoring at `0xC6C2D8`), `0xAE61A7` (`0xAE6110`: sets the edit flags and forces its lot on; build / buy entry [inferred from the fields it writes]) | camera streaming and edit entry only. Early out when `[lot+0xC1]` already equals the argument or `[lot+0xC9]` is set |
| `Lot_LoadObjectLayer` `0xACE0D0` | call at `0xACF293` in `Lot_FinishLoad` `0xACF170`, called at `0xACF8D8` in `Lot_Load` `0xACF520` (vtable thunk `0xACF9D0`, table `0x1065280`) | every lot load |
| execute `0xABD3C0` / `0xABD3E0` | one reference each, vtables `0x10650C4` / `0x10650D8` slot `+0x10` | as docs/28 |
| world event raiser `0x78F2B0` (event index = second argument; callback table `0x11D6A58`, 36 bytes per event, filled by `World_RegisterCallbackMethodImpl` `0x796190` -> `0x796150`) | 76 call sites; index `0xD` at `0xC642D0` (fn `0xC64230`) and `0xC65797` (fn `0xC65700`); index `0xE` at `0xC658E5` (`WorldManager_DetachLot` `0xC65830`) only | the only lot added / removed raises |
| same raiser, other lot events | `0xF` bounds changed `0xAB6F28`; `0x10` type changed `0x792DE6`, `0xACF78B`, `0xD41A99`; `0x11` object lot update `0xC8A346`; `0x12` object placed in lot `0xD04C08`; `0x13` object pending destruction `0xC8A473`; `0x46` lot post-load `0x7921A7` | object and lot-property changes |
| world events | `5` load started `0xC6CFD6`, `9` / `10` finished world file / save `0xC6D402` / `0xC6D3E4`, `7` load finished `0x769993` and `0x7986E3`, `3` quit `0xEC74EB` | world lifecycle |

Callers of the lot add / remove functions (rel32 calls only):

- `0xC64230` (raise `0xD`, takes the lot): from `0xC64420` (`0xC644B3`), `0xC64630`, `0xC65030`, `WorldManager_CreateLotsFromLayer`
  `0xC65E00` (`0xC661B9`) and `0xC6631C`. `0xC64420` is called from `0xAAA190`, `0xAFF070` (houseboat create icall
  `0x792CA0` and `0xD41630`), `0xC65E00` (`0xC65FD2`), `0xD43360`, `0xD44670`.
- `0xC65700` (raise `0xD`, thiscall, one argument): one direct call (`0xAAA176`) and four immediates (`0x957920`,
  `0x9C1585`, `0x9C1704`, `0xF4C9D3`), i.e. it is also handed out as a callback. Its callers' thread is not known.
- `WorldManager_DetachLot` `0xC65830` (raise `0xE`): from `WorldManager_RemoveLot` `0xC65A20` (`0xC65AAF`), `0xAA9780`,
  `0xAA9830` (vtable entries at `0x1064204` / `0x1064200`), `0xAA9988`. `0xC65A20` is called from `0xB04AF0` (bulldoze
  icall `0x7868E0`, houseboat delete icall `0x788800`, `0xD42640`), `WorldManager_LoadLots` `0xC678D0` (`0xC67D82`),
  `WorldManager_Shutdown` `0xC6B780` (`0xC6B8D3`) and `0xAAA119`.
- `WorldManager_Shutdown` `0xC6B780` is called at the start of `WorldManager_LoadWorld` `0xC6CF80` (`0xC6CF92`, also
  `0xC6D282`, `0xC6D3A2`) and from the quit path (`0xEC6F70`, `0xEC7140`).

### 5.2 Managed side [verified]

| What | Where |
|---|---|
| Native lot added / removed arrive as `World.OnLotAdded` / `World.OnLotRemoved` | `SimIFace` `World.cs:1783-1797`; registered `World.cs:1582-1583` |
| `LotManager` subscribes in its init and routes both to `DispatchMessage` | `LotManager.cs:811-812`, `1826-1829`, `2485-2494` |
| `LotManager.AddLot`: creates the `Lot` (or the world lot), puts it in `sLots`, adds it to the simulator (this starts the lot's own loop). Callers: the dispatch (`:2488`) and init for the world lot (`:806`) | `LotManager.cs:2318-2332` |
| `LotManager.RemoveLot`: destroys the lot's simulator object, removes it from `sLots`. Callers: the dispatch (`:2493`) and the shutdown loop over every lot (`:1042`), which runs **after** `LotManager` unsubscribed from the world events (`:985-986`) | `LotManager.cs:2358-2366`, `981-1044` |
| Object enters / changes / leaves a lot: `UpdateObjectLot` (event `0x11`), `RemoveObjectFromLot` (event `0x13`, and from `UpdateObjectLot`) | `LotManager.cs:2399-2430`, `2464-2474` |
| Lot placed from the bin, post-load step: `World.OnAddLotPostLoadCallback` -> `LotManager.PostLoadLot`; Edit Town clears its bin caches on the same callback | `LotManager.cs:859`, `913`; `EditTownModel.cs:483`, `780-785` |
| Household enters / leaves a lot: `Lot.MoveIn(Household, bool)` sets `mHousehold` (moving the old one out first), `Lot.MoveOut` clears it; these are the two writers of the field in `Lot.cs` | `Lot.cs:5953-5982`, `6096-6109` |
| Story-progression move events `kFamilyMovedInToNewHouse` / `kFamilyMovedOutOfHouse` are sent only by story progression; `kLotChosenForActiveHousehold` only by the play flow | `MoveInHousehold.cs:196`, `MoveOutHousehold.cs:111`, `MoveToLargerHouse.cs:158`, `MoveToSmallerHouse.cs:142`; `PlayFlowModel.cs:569-607`, `Household.cs:2605` |
| World switch for travel and Edit Other Town runs as one-shot tasks | `GameStates.cs:1363`, `1483`, `1580` (`SwitchWorlds`); `1309`, `1736` |

### 5.3 Lifecycle paths against the hub

"Hub today" = CP-1 as built (docs/321 §8): `lhAdds` / `lhRemoves` on the two executes, `lhWorldGen` from the ts3fixes
world lines.

| Path | Native route | Managed lot object | Hub today |
|---|---|---|---|
| World load (new game, load save) | `0xC6CF80`: shutdown of the old world, event `5`, `0xC65E00` -> `0xC64420` -> `0xC64230` (event `0xD` per lot), `Lot_Load` -> direct `0xAC1130` | `AddLot` per lot | world gen yes; **per-lot add no** (direct call) |
| Camera streaming in live mode | `0xC6C290` -> `SetActive` -> execute -> `0xAC1130` / `0xAB4C00` | unchanged (the `Lot` stays; only scene nodes move) | yes |
| Build / buy entry on a lot | `0xAE6110` -> `SetActive(true)` | unchanged | yes (add only) |
| Edit Town: bulldoze | icall `0x7868E0` -> `0xB04AF0` -> `0xC65A20` -> `0xC65830` (event `0xE`) | `RemoveLot` | **no** (`0xAB4C00` is not run) |
| Edit Town: place a lot from the bin, replace a lot | `0xD41630` / `0xD43360` / `0xD44670` -> `0xC64420` -> `0xC64230` (event `0xD`), `Lot_Load` direct add, event `0x46` [call chain verified; that these are the Edit Town placement functions is inferred, docs/109 names `0xD41630` so] | `AddLot`, then `PostLoadLot` | **no** |
| Edit Town: move a household between lots, evict, merge | none | `Lot.MoveOut` / `Lot.MoveIn` | no (not a lot event) |
| Houseboat create / delete (EP10) | icalls `0x792CA0` / `0x788800`, same add / detach functions | `AddLot` / `RemoveLot` | **no** |
| Houseboat moving | event `0xF` bounds changed | `Lot.UpdateLotPosition` | no (lot identity unchanged; position-keyed rows must drop) |
| Travel (World Adventures, University, Into the Future), Edit Other Town | `SwitchWorlds` task, then a world load of the other world file [inferred: the body was not traced to the load icall]; `0xC6CF80` starts with `WorldManager_Shutdown` [verified] | `LotManager.Shutdown` (direct `RemoveLot` loop), then init + `AddLot` per lot | world gen yes; per-lot no |
| Quit to main menu | `0xEC6F70` / `0xEC7140` -> `0xC6B780` -> `0xC65A20` per lot; event `3` at `0xEC74EB` | `LotManager.Shutdown` | world gen yes |
| Family moves in / out (story progression, player move, moving-in together) | none | `Lot.MoveIn` / `MoveOut` | no |
| Active household switch | none | `Lot.OnBecameSelected` from `MoveIn`; `kHouseholdSelected` | no; docs/331 E3 (scope unknown = household) covers the window |
| Object placed, moved between lots, destroyed | events `0x12`, `0x11`, `0x13` | `UpdateObjectLot` / `RemoveObjectFromLot` | no |

Consequence for what is already built: CP-3 / CP-4 consumers that key rows by lot and rely on `LH_REMOVE` to drop them
keep rows for a bulldozed or deleted lot until the next world event. The number is bounded by lots destroyed in Edit
Town in one session, and `lhWorldGen` clears them; it is a small leak, and a correctness risk only if a new lot can
reuse the id of a destroyed one in the same session [inferred, not checked].

## 6. ST-12: the hub the far models need

One hub, two feeders, no second design: the managed `OnLotLoad` / `OnWorldChange` surface of CP-2 (docs/321 §9) gains
the missing events; the native hub of CP-1 gains two kinds.

### 6.1 Managed (ts3fixes), the far models' source of truth

| Hub event | Patch site | Why this site |
|---|---|---|
| `LotEnter(lot)` | postfix on `LotManager.AddLot` | both callers pass through it; the `Lot` exists and is in `sLots` when the postfix runs. Subscribing to `World.OnLotAddedEventHandler` instead would depend on subscriber order: `LotManager` re-subscribes on every world init, so a handler added once at startup runs before it from the second world on |
| `LotLeave(lot)` | prefix on `LotManager.RemoveLot` | covers the event path **and** the shutdown loop, which no world event reaches; the prefix still has the live `Lot` |
| `ObjectEnter(obj, lot)` / `ObjectLeave(obj, lot, destroyed)` | postfix on `LotManager.UpdateObjectLot`, prefix on `LotManager.RemoveObjectFromLot` | the two dispatch targets for events `0x11` / `0x13`. FO-1 should confirm with its probe that script-side creation (`Lot.AddObjectToLot` called from other code) always comes with event `0x11` [inferred] |
| `ScopeChange(lot)` | postfix on `Lot.MoveIn(Household, bool)` and on `Lot.MoveOut` | the only writers of `mHousehold` in `Lot.cs`; the events are sent by some movers only |
| `WorldReset` | the existing ts3fixes world load / quit hooks (CP-2 `OnWorldChange`) | already built |

Rules for consumers (far-Sim, far-object, lazy-instantiation indexes, `FarHold`):

- `LotLeave` and `ObjectLeave`: drop every row keyed by that lot or object **in the call**. A parked lot that leaves is
  released first (`FarHold.Release`), so its alarms and paused objects are EA's again before EA destroys them.
- `LotEnter`: the lot starts live (never parked at birth); far state for it starts empty.
- `ScopeChange`: the lot is promoted at once and re-judged on the next pass with the lot exemption of docs/346 §3.6.
  While a move is in progress the scope is unknown, so the lot counts as household.
- `WorldReset`: every index is cleared and `FarHold.ReleaseAll()` runs; the sidecar flush of docs/343 runs before it.
- Edit Town, build / buy, CAS and pause: the pause / CAS gating rule applies to the far models' passes; hub events
  still arrive there (they come from EA's own calls) and only drop or promote, never demote.
- Each index exposes a row counter; the queue check is "rows == 0 after quit to menu" and "rows for lot X == 0 after
  bulldoze" (repro: Edit Town bulldoze and place, one travel round trip).

All sites are plain managed methods on the sim thread [inferred for the Edit Town path: the bulldoze icall is a script
call, so its world event is raised inside a script call; not traced]. No household Sim is touched by any of these
patches: they observe and drop our own rows.

### 6.2 Native (CP-1 hub), for native consumers only

| New kind | Site | Bytes at entry [verified] | Convention [verified] |
|---|---|---|---|
| `LH_LOT_CREATE` | `0xC64230` and `0xC65700` | `83 EC 68 56 8B 74 24 70` / `83 EC 68 53 56 8B 74 24 74` | `0xC64230`: lot as the one stack argument, `ecx` not read, plain `ret`; `0xC65700`: thiscall, one argument, `ret 4` |
| `LH_LOT_DESTROY` | `WorldManager_DetachLot` `0xC65830` | `83 EC 48 53 55 56 57 8B 7C 24` | thiscall, one argument (the lot), `ret 4`; the lot id is read from `[lot+0x48..+0x4C]` before the raise |

- Both bump `lhLotGen`; `LH_LOT_DESTROY` calls subscribers **before** the game's function body so rows are dropped
  while the lot pointer is valid.
- Both add functions test the lot's vtable `+0x40` result against 1 before raising; the detour should fire only when
  the game raises (hook after the game's call and compare a raise counter, or repeat the same test) [design note].
- `node tools/hook-sites.ts --check` lists no hook of ours, Apex or S3SS on these three addresses [verified, tool
  output 2026-11-26]. docs/68 must get the rows at build.
- Thread: `0xC65700` is also passed as a callback pointer (four immediates), so its thread is not proven; the detour
  must not assume the sim thread (counter bump interlocked, subscriber call only when the caller is the sim thread,
  else generation bump only). `0xC65830`: docs/38 says "caller's thread".
- If native consumers can live with `lhLotGen` compare only, the cheaper option is no new detour: ts3fixes sends one
  `UserDiag` op from `LotEnter` / `LotLeave` and the native hub bumps `lhLotGen` (same pattern as the world lines).
  That is the recommended first step; the detours are only needed for a consumer that must see the lot pointer.

### 6.3 What stays as built

CP-1's execute hooks stay: they are the only source for "this lot's objects entered / left the scene" (camera
streaming), which lot-scoped caches (door generation, ghost sweep) want. They are not a lot lifecycle signal and
docs/321 §8 should say so.

## 7. Gates

| Package | Gate (docs/332) | Result |
|---|---|---|
| ST-8 | each class: pause hook or E4 | met for the shipped EA and Store script assemblies (§2); **open** for mods (§4) and for one-shot targets defined in another file (§1 limit) |
| ST-12 | each lifecycle event mapped; hook coverage [verified] | met: §5.3. Verdict: the two executes do **not** cover lot create / destroy; hub extension in §6 |

## 8. EA 1.69

Not looked at. The managed line numbers are Steam 1.67 decompiles; the native addresses need the usual EA mapping
before CP-1 is extended.

## 9. Open questions

- Q1: mod object-tick census (§4). Needs the compat-set script assemblies decompiled; shares the scan of docs/346 Q6.
- Q2: about a quarter of the one-shot creation sites pass a delegate that is not defined in the same file. A pass
  over the managed call graph index (`game-copy/analysis/callgraph/index.json`) would close that.
- Q3: `MinorPet.DeathUpdate` and the wild-pet despawn rules were not read; the "age in days" closed form is inferred.
- Q4: thread of `0xC65700` callers (callback pointer users `0x957910`, `0x9C12C0`, `0xF4C960`) and what `0xAAA190`,
  `0xD43360`, `0xD44670`, `0xAA9780`, `0xAA9830` are (Edit Town place / undo are the guess).
- Q5: can a lot id be reused in one session after a bulldoze? Decides whether the stale CP-3 / CP-4 rows are only a
  leak or also a wrong answer.
- Q6: does every script-side object creation on a lot raise event `0x11` (FO-1 probe), or does `ObjectEnter` need a
  patch on `Lot.AddObjectToLot` as well.
- Q7: `SwitchWorlds` body to the world-load icall, and whether University / Future travel take any path that keeps
  `LotManager` alive across the switch (would skip `WorldReset`).
- Q8: REA was not available; the native rows come from a linear rel32 / immediate scan. Calls through registers or
  vtables to `0xAC1130`, `0xAB4C00`, `0xC64230`, `0xC65830` would not show. The functions have no vtable or data
  references except the ones listed, so an indirect caller would need a computed pointer; a REA xref pass should
  confirm.

## 10. docs/51 entries

§617: V-617-1 .. V-617-8, I-617-1 .. I-617-6. I-591-1 (§591) gets its verdict from V-617-1 / V-617-2.
