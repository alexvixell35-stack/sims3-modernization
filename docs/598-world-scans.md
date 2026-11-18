# 598. World and lot object scans in "hot" EA methods: frequency census (nothing built)

Written 2026-11-16 (UTC). Static only: decompiled EA script code, no game run. Input: a grep of 172
`GetObjects<T>()` calls (world or lot) inside methods with hot-sounding names (Update, Callback, Test, Run).
Goal: find more scans like `pondclearscan` (docs/594 §5: `PondManager.AllPondsClear`, a world
`GetObjects<LotPond>` on **every Seasons tick** while ponds try to melt, 0.57 ms per tick [measured]) and build
exact speedups for the frequent ones.

**Result: none of the 172 runs per tick or per Sim per tick.** All are edge-triggered, alarm-driven at
10 sim-minutes or slower, per route plan behind narrow conditions, per interaction start, or player / build /
load actions. No switch built, no counters reserved, no package (same as s65 / docs/595).

## 1. Leads named in the brief [verified, decompiled]

| Method | Scan | How often it runs | Verdict |
|---|---|---|---|
| `SeasonsManager.TemperatureManager.UpdateExtremeCold` (×2) | world `IReactToExtremeCold` | called every Seasons tick, but each scan only on the tick the temperature **crosses** `kExtremeColdThreshold` (prev vs current) | rare (a few per sim-day) |
| `SeasonsManager.WeatherManager.UpdateFlagState` | world `IFlagPole` | only when the wind intensity **changes** (`windIntensity != mPreviousWindIntensity`) | rare |
| `FoodTruckManager.Update` | world `FoodTruck` + per-lot `CountObjects` | `TrafficManager.UpdateTrafficTask` loop, sleeps `kTrafficCheckTime` **hours** | rare |
| `FishingSpot.UpdateAllFishingSpots` | world `FishingSpot` | `UpdatePondFreezing` pond-state changes only (Liquid ↔ FreezingMelting) | rare |
| `SimRoutingComponent.AboutToPlanRouteCallback` (4) | **lot** `CommonDoor` ×2, lot `Fire` ×2 | per route plan, but door scans only for a Sim not greeted on a non-owned residential lot or a closed venue; fire scans only for Daredevil / ImmuneToFire / Blizzard / on-fire / ghost / Grim Reaper | conditional per route; lot-scoped concrete-class query (tens of µs [inferred]); est. < 10 ms per sim-hour. Not in the managed hot-spot profile (docs/74 lists this callback only for `Lot.IsOpenVenue`) |
| `ResortManager.ThoughtBubbleFeedbackAlarmCallback` | lot `IResortStaffedObject`, `ScenarioRoach`, `TrashPile` | static alarm every 10-30 sim-min, per resort lot, returns at once unless `mOwnerLot.IsControllable` (player-owned resort) | rare |
| `ResortManager.ResortDegradationCallback` | lot `GameObject` ×2, roach, trash | every `kMaintenanceInterval` = 30 sim-min, owned resorts only | 2/sim-hour; the second `GameObject` scan repeats the first with no yield between (could reuse it, ~1-3 ms per sim-hour saved [inferred]): not worth a switch |
| `ResortManager.UpdateRating` / `UpdateAmentityObjectTotal` | lot `IGameObject`, roach, trash | daily profit alarm, rating dialog, `MakeReviews`, `RealEstateManager` | rare |
| `Lot.UpdateActiveTopics` | lot `RabbitHole` | only from `CalculateMetaAutonomyTypeAndConsiderAddingToPublicMetaObjects`: load, options change, edit town, build/buy exit | rare |
| `ChairSectional.UpdateHelper.Update` | lot `ChairSectional` | one 0 s alarm per lot after build/buy exit or import, deduped per lot | rare |

## 2. The rest of the list

- **`Run` (91 hits)**: once per interaction start (or per stage). Not per tick.
- **`Test` (23 hits)**: interaction tests; most are lot-scoped and behind type or actor gates. The world-scoped
  ones: `Subway` (×4, subway interactions on rabbit-hole lots), `Urnstone` (debug "Death By World Builder"
  only), `Sim.GoForWalkWithDog` (world `Lot`, after `ActorCanWalkTarget`: dog-owning households only),
  `Sim.NPCUseHoverTrain` (world `IHoverTrainStation`, after `!isAutonomous || a.IsSelectable`).
- **Other callbacks**: `CaregiverRoutingMonitor` (repeating alarm while a household needs a caregiver),
  `SharedFridgeInventory` (fridge inventory events), `Scarecrow.PanickingBirdCallback` (one alarm per panic),
  `GigOpportunity`, `School` graduation, `KrakenManager`, `FireStationAlarm`, burglar alarms, elevator / tube
  elevator portal updates, room markers, `UpdateCachedCost` (daily depreciation, houseboat parent),
  `UpdateVirtualResidentialSlots`, `BinCommon.UpdateImportedUrnstones`, `TheatreSandwichBoard.UpdateGraphic`:
  all event-, build- or alarm-driven.

## 3. The one lead left open [inferred]

`Sim.NPCUseHoverTrain` is a solo interaction on every Sim when EP11 is installed (`Sim.cs` AddSoloInteraction),
so its `Test` may run on every autonomous NPC autonomy pass in every world, each time a world interface query.
Whether it does depends on its ITUN advertisements (not extracted here). No exact speedup was found:
- a cached station count needs object create/destroy coverage that docs/125 §4.2 rejected as not airtight;
- `CountObjects` walks the same maps (no gain unless the array build dominates) [inferred];
- a `HoverTrainStation` class query differs once a mod adds another implementer.

Next step if wanted: a counter-only hook on the `Test` (calls per sim-hour) in one natural run; build only if it
fires > ~1,000 times per sim-hour. Estimated upper bound today: 40 NPCs × 12 passes per sim-hour × ~0.5 ms ≈
240 ms per sim-hour [inferred, unmeasured].

## 4. Saved per sim-hour

Nothing built, so 0 ms. The largest exact gains found (resort double scan, route-callback door scans) total well
under 10 ms per sim-hour, against pondclearscan's ~1 s per sim-hour while its condition holds.

## 5. Built: `hovertraintest` (2026-11-16, static, NOT RUN)

Asked for by Rodney (hover trains are rarely used). Switch `hovertraintest` (tuning `kHoverTrainTest`), default off;
`HoverTrain598.cs`; counters 2157-2162; repro `hovertraintest-check`; package r56.

**What EA does [verified, decompiled].** `Sim.NPCUseHoverTrain.Definition.Test`: `!isAutonomous || a.IsSelectable` → false,
else `Queries.GetObjects<IHoverTrainStation>().Length >= 2`. The query is the native per-class map walk (docs/125 §4.1).
EA keeps its own `HoverTrainStation.sHoverTrainStations` (added in `OnStartupRabbitHole`, removed in `Dispose`) but that
list is not the query's set (timing, other implementers), so it is not used.

**Design.** `AtLeastMemo` (game-independent) keeps the array EA's query returned last. Answer: fewer than 2 stored →
"no" (a removal only lowers the count); 2+ stored → "yes" if the first two still have a live proxy, else unknown. Unknown →
EA's query runs and is stored (not stored while build/buy or edit town is open). EA's gate runs first, unchanged, so
selectable / household Sims get EA's false before the memo is read; a null actor runs EA's own Test.

Where a station can enter the query's set, and what forgets the memo. **r59 fix (2026-11-16):** the first in-game check
(r56) returned UNEXPECTED, `members found=False (missing: a Simulator add hook)`: `Simulator.AddObject(object)` is already
compiled when the mod loads (applied log: `Simulator.AddObject: already compiled`), so that swap is always refused and the
memo never armed [verified, applied log]. The four Simulator swaps are replaced by one hook on the station itself:

| Add path | Hook |
|---|---|
| Every station start: native creation (lot / world load, buy mode, edit town: `ScriptProxy.CreateLogic`), save load (`ScriptProxy` post-load `Init`), script spawns (`Simulator.CreateObject*`, `AddObject*`, `CreateScriptLogic`: ScriptCore `Simulator.AddObject` → `SetLogic`) | `HoverTrainStation.OnStartupRabbitHole` swap [verified, decompiled: `SetLogic` → `GameObject.Init` (both arms) → `OnStartup` → sealed `RabbitHole.OnStartup` → `OnStartupRabbitHole`]: forget, and keep the station pending |
| The gap between `Init` and the native add (`AddObjectImpl` runs after `SetLogic`) | while a pending station has a proxy that `GetProxy` does not find yet, the memo answers unknown and stores nothing; resolved when registered or when init failed (proxy cleared) [inferred: registration makes `GetProxy` find it; the check compares live] |
| Other `IHoverTrainStation` classes (mods), or a subclass that declares its own `OnStartupRabbitHole` | loaded assemblies scanned at apply and each world load: any such class keeps the memo off (`members found` names it) |
| Buy mode, objects moved onto a lot, edit-town lot placement (belt and braces) | `World.OnObjectPlacedInLotEventHandler` (station or unknown object), `OnLotAdded` / `OnLotRemoved`, `LotManager.EnteringBuildBuyMode` / `ExitingBuildBuyMode`; nothing stored while build/buy or edit town is open |
| World load / travel | `OnWorldLoadStarted` / `OnWorldQuit` (also drop pending), `Finished`, `OnEnterNotInWorld` / `OnLeaveNotInWorld` |

Inventory placement: rabbit holes cannot go to an inventory [inferred], and a placed object is already a started script
object (covered by the placement event anyway).
Removals (`kSoldObject`, `kEventObjectDisposed`, `OnObjectPendingDestruction`, bulldoze: all end in
`Simulator.DestroyObject`) need no event: a stored station without a proxy makes the memo unknown. [inferred: a live proxy
means the object is in the per-class map; the check compares the memo with EA's query live.] If the station hook did not go
in, the memo is never used. `TickMemo` / `ExactMemos` were checked: they are per-tick (`TickStamp`) or keyed generation memos,
none event-forgotten, so not reused.

**Run [verified, decompiled].** EA's Run lists the stations again (kept: once per trip, and the list order feeds the random
pick). Defects: `GetClosestObject` skips charred objects and returns null when every station is charred, and Run
dereferences it; the travel definition is dereferenced unchecked. With the switch on both answer EA's "no station" false
(counter 2161), after the same calls in the same order (one `RandomUtil` draw). The `destination == origin` step is one
increment with wrap, not a loop: it always exits and the list has no duplicates. Kept as EA: the bias toward the station
after the closest one (it gets two of the N draws).

**Proof.** `HoverTrainTestCheck run`: the memo's answer (stored by NPC tests before the check) against EA's query in the
same tick; a difference counts 1974 (must be 0). Synthetic controls on a private memo: 0, 1, 2, 3 stations, a placement
(forget → recount lists one more), a removal (stored station gone → EA asked again), one going while fewer than 2 stored.
`result`: counters and a `timing:` line (EA query µs vs memo answer µs, stations, world object count). The repro runs it off,
on after 60 s of ultra speed, in buy mode, and after a placement.

Counters: [2157] tests answered from the memo, [2158] tests that ran EA's query (switch off: every gated test, so the off
arm measures the call rate this doc asked for), [2159] forgets, [2160] check mismatches, [2161] Run throws avoided,
[2162] errors. Gain: up to the §3 estimate (~240 ms per sim-hour [inferred]); the off arm's 1972 and the timing line give the
real figure.

## 6. Second sweep: Test-like methods with world queries; built `newcartest` (2026-11-16, static, NOT RUN)

Input: the cheap scan of Test-like methods with `GetObjects` / `CountObjects` / `GetRabbitHolesOfType` / `AllLots`
(brief s70). Static only (decompiled EA code, `OriginalSocialDataEP4.xml` for the social wiring).

### 6.1 Classification [verified, decompiled]

| Rank | Method | Scope | How often | Verdict |
|---|---|---|---|---|
| 1 | `SocialTest.TestHasNewCar` (ProcTest of Enthuse About New Car / Fast Car / Technologically Advanced Car), `TestHasExpensiveNewCar` (Enthuse About Expensive Car) | **world** `GetObjects<CarOwnable>()` + a loop over every car (`GetOwnerLot() == actor.LotHome && IsNewCar`), no gate inside | all four `Available auto="True"`, ages T-E, actor not Grumpy (New Car: no trait needed; the others Daredevil / ComputerWhiz / Snob). `ActionData.Test` runs the ProcTest last, for every actor / target pair that passes, from `SocialInteractionA.Definition.Test` and `GetSocialsForSTC` (296 µs per call): per pair per social build | **built** (§6.2) |
| 2 | `Sim.GoForWalkWithDog` Test | world `GetObjects<Lot>()` + per-lot test | only after `ActorCanWalkTarget` (target an adult dog of the actor's household, awake) | dog households only; not built |
| 3 | `SocialTest.TestCanBeScouted` | world `CountObjects<IServoBotArena>` | only for a BotArena career actor with the scout unlock | rare |
| - | `Sim` trait interviews (Snob / BookWorm / Virtuoso: `GetRabbitHolesOfType`, 1.29 ms), `Sim` TraitDesire (`CountObjects` of a type) | world | `isAutonomous` → false first | player menu only |
| - | `Phone.CallInviteOverForeignVisitors`, `Phone.CallThrowParty` (`LotManager.AllLots`) | managed lot list loop | behind base Call tests, party / grounding / date gates | cheap managed loop; not built |
| - | `Subway` ×4 | world `GetObjects<Subway>` | subway interactions on the rabbit hole only (§2) | not per Sim |
| - | `SocialTest.TestBoastAboutCar` / `Bicycle` / `Pool`, `TestWooHoo` / `Spicy` / `Juiced` / `PetWooHoo` / `TryForBaby`, `TestTrainSim`, `TestComplainAbout*`, `TestEnthuseAboutFishing`, ... | **lot** `CountObjects<T>()` of the actor's home or current lot | per pair, mostly behind romance / trait gates | lot-scoped per-class count (tens of µs [inferred]); not built |
| - | `Sim` costume chest, bed, DJ turntable, dare, pet food; `Phone` chess / soccer / show; `ServingContainer`, `WashingMachine`, `FutureFoodSynthesizer`, `Fish`, `Door`, `Terrain` igloo | lot | per object or lot-scoped | not world |
| - | `Terrain` genius contemplate / tile test, `TraitFunctions` pyromaniac, `PicnicBasket` ×3 | radius queries around the actor | per Test, small radius | not a world scan |
| - | `BabyDragon` ×2 (`AllLots`) | managed lot loop | store pet only | rare |

Only rank 1 clearly runs per Sim pair on many autonomy passes in every world with cars.

### 6.2 `newcartest` (tuning `kNewCarTest`, `NewCar598.cs`, counters 2747-2752, repro `newcartest-check`, package r60)

**Exactness [verified, decompiled].** `IsNewCar` = `ElapsedTime(Days, TimeOfPurchase) <= BuffNewCarSmell.DaysOfNewCarSmell`.
`TimeOfPurchase` is written only in `CarOwnable.OnCreation` (= now, the newest value possible; NRaas source has no writer),
so a car that is not new stays not new while the clock runs forward and the tuning is unchanged. `FadingSubsetMemo`
(game-independent) keeps the cars of EA's last query that were new then, with the clock ticks and the tuning value. Answer:
any kept car that is still registered (proxy live, checked first, so EA's per-car calls are never made on a gone car) and
matches EA's per-car test live (owner lot, `IsNewCar`, and `Value` for the expensive test, same order). A clock that went
back or a changed tuning value: unknown → EA's query runs and is stored.

**Additions forget.** Every car start goes through `CarOwnable.OnStartup` (`GameObject.Init`, both arms; `OnCreation` just
before it). The six EA overrides (PriusC, CarTwizy, CarUFO, SirenEnabledVehicle, FireEngine, HoverCarExpensive) call
`base.OnStartup` unconditionally [verified, decompiled]. Hooked like the hovertraintest station start: forget, and keep the
car pending (nothing stored) until `GetProxy` finds it. `CarOwnable.ImportContent` forgets too. Any other `CarOwnable`
subclass that declares `OnStartup` (a mod) keeps the memo off. Belt and braces: build/buy enter / exit, world load / quit,
not-in-world; nothing stored in build/buy or edit town. [inferred: the query lists registered car script objects, inventory
cars included, so inventory moves change nothing; the check compares memo vs EA for every Sim live.]

**Household rule.** Household, selected and unknown-scope actors (`Fixes.HouseholdExempt`) and a null actor always run EA's test.

**Check.** `NewCarTestCheck run`: for every Sim in the world, both tests from the memo against EA's own test (the stubs) in
the same tick; differences count 2750 (must be 0). Synthetic controls: no / old / new / aged-out / gone / added car, clock
back, tuning changed. `result`: counters and a `timing:` line (EA test µs vs memo µs, car count, new cars kept). Live assert:
`CheckLiveMap` `newcartest` (2 car swaps, 2 test swaps).

Counters: [2747] answered from the memo, [2748] EA tests (switch off: every call, the call rate), [2749] forgets,
[2750] check mismatches, [2751] errors, [2752] ImportContent forgets. Gain [inferred, unmeasured]: one world car query
(~0.3-0.5 ms, cf. the 487 µs station query in §5) per passing actor / target pair per social build; the off arm's 2748 and
the timing line give the real figure. NPC car spawns (GeneratedOwnableForNpc) forget the memo, so the hit rate in busy
towns is what the run must show.

**r61 fix (2026-11-16).** r60 on Bridgeport: off BROKEN, on UNEXPECTED: `pending 3`, memo never armed (answers from the memo 0,
counters 0); timing EA test 599 µs vs memo 8 µs (50 cars, all 50 new, EA true for 98 of 100 Sims). Cause [verified, decompiled
ScriptCore]: `ScriptProxy.Dispose` clears the proxy's `Target` and `ObjectId` but leaves the object's `Proxy` set, so a car
that started and was destroyed before registering or before the next pending pass (NPC-generated cars come and go) never
resolved: `GetProxy(bad id)` stays null. Now `StartResolved` (shared with hovertraintest, same latent bug): resolved when the
proxy is gone, its `Target` is no longer the object (destroyed / init failed), or `GetProxy` finds it; still pending only while
the proxy targets the object and is unregistered (the start-to-`AddObjectImpl` gap). A destroyed car is not in EA's query, and
a kept one is skipped by the liveness test, so the answer stays exact. The check's live line now lists why each pending car
is pending (target, id, GetProxy / PreInit, in world, in inventory). With EA true for nearly everyone the memo answers "yes" at
the first live kept car of the actor's home lot; the liveness lookup now runs only after the owner-lot field matches.
