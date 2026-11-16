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
