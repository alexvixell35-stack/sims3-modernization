# 542. Where orphaned vehicles come from, and fixes at the source

Static research, 2026-11-10, no game run. **Status: S1-S5 and the census built 2026-11-10 (§7); first live run 2026-11-10 (SteamOS/Wine, 90 min stress + 7 world reloads, all on): PASS, S2 acted 5 times, S1 once, no household vehicle touched (PLAN.md). The off/on natural-play soak of §5 is still to do.** NRaas Overwatch's `CleanupVehicles` option tows vehicles that
nothing uses any more (docs/145 row 11). This doc uses what it finds as a map of what EA leaves behind, traces each kind
of leftover back to the EA code that makes it and fails to clean it up, and designs small ts3fixes switches (all off by
default) that fix those paths in EA's code. It also designs an agent census so a soak run can show the leak and the fix.

Sources: the 1.67 script DLLs decompiled with ilspycmd into `game-copy/decompiled/` (git-ignored; no code is reproduced
here, only class and method names); NRaas source at `game-copy/nraas-src` (shallow clone of Chain-Reaction/NRaas, read
only, no license: behaviour described in our words, cited by path).

Claim marks: **[verified]** = read in the decompiled game code or the NRaas source; **[inferred]** = our reading of how
often or when a path fires, not yet seen in game; **[community]** = NRaas behaviour that points at the same defect.

## 1. What CleanupVehicles finds (behaviour spec, our words)

`NRaasOverwatch/OverwatchSpace/Alarms/CleanupVehicles.cs` [verified]:

- **When.** In the nightly alarm (docs/145, `mCleanupVehicles`, on by default), and every sim-hour when
  `mCleanupVehiclesHourly` is on (an `AlarmTask` registered at world load, off by default). The same file also adds a
  `kEventSimMovedFromLot` listener at every world load that is **not** gated by either setting (see the last bullet).
- **Parking pass first.** Every `ParkingSpace` that holds no car but still has a reserved vehicle gets the reservation
  cleared.
- **Kept (never towed):** vehicles parked in a parking space, mooring post or bike rack on a residential lot; every
  resident Sim's reserved vehicle and preferred vehicle (`mPreferredVehicleGuid`); objects stored for absent Sims
  (boarding-school and passport inventories, jail inmate objects, `ParentsLeavingTownSituation` inventories); stolen
  objects; a vehicle whose live driver is on the vehicle's lot or is performing a service; UFOs, wind surfboards; magic
  brooms in an inventory or on a stand; ownable vehicles marked pending use; **player cars and boats** (a `CarOwnable` or
  `BoatOwnable` not marked `GeneratedOwnableForNpc`); other ownables (bikes, motorcycles) in an inventory; food trucks
  and service cars that are in the world; vehicles in use by a live Sim; cars in gallery or store-set parking spaces.
- **Towed (destroyed), everything else:** cars and boats the game generated for NPCs (in an NPC's inventory or loose),
  taxis and limos (`CarNpcWithNpcDriver`), traffic cars (`LightweightTaxi`, destroyed but not counted in the notice),
  out-of-world service cars and food trucks, loose owned bikes and brooms, and any vehicle whose driver is destroyed or
  on another lot. The vehicle is unparented, unreserved from its space or post, and destroyed.
- **Sim leaves a community lot (listener):** a non-generated car or boat reserved in a space or post there by that Sim
  (its reserved or preferred vehicle, or it is the driver) is moved into the Sim's inventory and the spot is
  unreserved. This one touches household cars, so it is outside anything we would change (CLAUDE.md household rule).

ErrorTrap, at load, clears the same stale references from the other side [community]:
`NRaasErrorTrap/ErrorTrapSpace/Dereferences/Performers/DerefParkingSpace.cs` and `DerefMooringPost.cs` (a reservation to
a destroyed vehicle), `DerefVehicle.cs` (`Driver` pointing at a destroyed Sim), `DerefCar.cs` (passengers),
`DerefCarpoolData.cs` (carpool car and driver), and `Checks/CheckVehicle.cs` (a vehicle loaded without a
`CarRoutingComponent` gets one).

## 2. Prior work (ours and upstream)

| Item | What it covers | Overlap with this doc |
|---|---|---|
| `carnoroute` (docs/513, PR #181 closed, merged in the sync commit for #176-#186, now on by default in `Instantiator.cs`) | `CarNpcManager.DriveFromPointToMatrix` with a car that has no car routing component returns false instead of throwing | Prevents the stray teleport helper and alarm the throw leaves, and a service Sim stuck in an off-screen car; it does not destroy or create vehicles. No new work. |
| `npcdriverpool` (docs/514, PR #184 closed, merged the same way, on by default) and `ageupabort` (docs/512) | A taxi, limo, bus or fire-truck driver is no longer built from a dead pooled description, or lost to an aborted age-up | **Already closes one orphan path:** `CreateNpcCarWithDriver` makes the car first and the driver second, so a throw in `CreateDriver` leaves an out-of-world car nobody holds [verified]. No new work. |
| `deadrefsweep` (docs/205, ts3fixes `DeadRefs.cs`) | Hourly sliced sweep: `Sim.ReservedVehicle` to a destroyed vehicle cleared (phase 5), car and boat previous passengers (phase 7) | Clears references *from Sims to dead vehicles*. Nothing clears references *from parking spaces and mooring posts to dead vehicles*, or `Vehicle.Driver` to a dead Sim; §4 S1 adds that as a phase. |
| #166 rows 36 and 44 (the player's patched core) | A vehicle that goes away releases `Sim.ReservedVehicle` | Same as `deadrefsweep` phase 5; done. |
| `unseenboat`, `unseenboatstart` (docs/248), branch `bugfix/unseenboat-goal-in-water` | Unseen boat trips skip the boat transport | Changes how a trip runs, never creates or destroys a boat outside EA's own path. No overlap. |
| `kUnseenCarThrottle` / `kUnseenCarEvery` (docs/143 §8.3) | Unseen traffic cars plan fewer drives | Traffic cars only, no lifetime change. No overlap. |
| `kUnseenPark*`, `kWakePark*`, `kWp*`, `kPark*` (`UnseenThrottleCore.cs`) | "Park" = an unseen Sim's autonomy or SimUpdate task sleeping, not vehicle parking | No overlap (name only). |
| `kHouseboat`, `kTraffic` (`RouteSourcesCore.cs`, `UnseenFallbackCore.cs`) | Route-source bucket ids | No overlap (name only). |
| #11 (closed, default on) | Route adaptor leak on ocean and houseboat portals | Route objects, not vehicles. No overlap. |
| #22 (open) | Houseboat gangway replans | Plan cost, not vehicles. No overlap. |
| #33, #35, #140 (save cleanup) | Snapshots, weak relationships, MiniSims, life events | None of them counts or removes vehicles; a vehicle census (§5) would be a new row for #35. |
| #21 (closed) | Background only: an earlier case where we fixed the cause of what an Overwatch check found | — |
| Branch `sync/2026-11-03-m2park` | Mode 2 round 2 result ("parked" = shelved) | Not vehicle related. |

GitHub searches for vehicle, car, Overwatch found nothing else on vehicle lifetimes (#86 is service-Sim pools, #98 a
crash after a free-vacation pickup, unrelated to car lifetime as far as its text says).

**What is new here:** the leak paths in §3 (L1-L6), the parking reservation leak, and the census. None of them is
covered by a built switch.

## 3. Where the vehicles come from and where EA fails to clean up

How EA's vehicles normally end [verified]:

- **NPC car** (`CarOwnable` with `GeneratedOwnableForNpc`): `Sim.GetNpcVehicle` makes it out of the world when an NPC
  with no usable vehicle routes by car (`GetVehicle` <- `GetInVehicleRouteAction.PerformAction`), sets `LotHome` to the
  NPC's home. `OnCarEntered` makes it the driver's `ReservedVehicle`. On arrival `CarOwnable.Park`: on a community lot it
  goes into a free parking space, otherwise into the driver's inventory. Ends: the NPC arrives home (`Sim` lot-change
  handler parks the reserved car into the inventory); the NPC is disposed (`OnReservingSimDisposed`: destroyed for a
  homeless NPC, else parked at the NPC's home or sold); the inventory is destroyed with its Sim
  (`InventoryComponent.Dispose` -> `Inventory.DestroyItems`). The same for NPC boats (`GetNpcBoat`, `BoatOwnable`).
- **Taxi and limo** (`CarNpcWithNpcDriver`): made by `CreateTaxi` (with an NPC driver, for a Sim that is not an NPC) or
  `CreateNpcCar` (no driver; the NPC drives it). Ends on the last get-out: `ShouldRelease` -> `Release` -> destroy, or the
  NPC driver drives it off screen (`ReleaseNpcCar` -> `DriveOffscreenAndRelease`, car and pooled driver destroyed).
- **Taxi bike, taxi scooter** (`AdultTaxiBicycle`, `ChildTaxiBicycle`, `ScooterTaxi`): `Release` destroys. **Taxi boat**
  (`CreateTaxiBoat`): `DestroyOnRelease`, destroyed on release.
- **Traffic car** (`LightweightTaxi`, `TrafficManager`): its `LiteDriver` task drives it between street spots, and an
  `AlwaysPersisted` alarm ends it (drive off screen, destroy). `OnStartup` runs on load as well as on creation
  (`GameObject` startup calls `OnLoad` then `OnStartup`), so the task restarts after a load; every exit of the task
  destroys the car. **No leak found.**
- **Service and police cars** (`CarService`, `ShouldRelease` false): owned by a situation. `ServiceSituation.CleanUp`
  destroys `Car` (`mDestroyCarOnExit`, default true); `PoliceSituation`, `SimArrestSituation`, `CaughtSituation`,
  `BarBrawlSituation` and the party break-ups destroy their cop car in `CleanUp`; `DriveAwayInServiceCar` destroys the car
  in its `finally`. Pizza delivery stores its car as `Car` too. **No leak found** except L5.
- **Carpools and school buses** (`CarpoolWithDriver`): `CarpoolData.NullCarAndDriver` destroys car and driver, and the
  class carries `IDeletedIfOutOfWorldOnLoad`, so `LotManager`'s post-load lot pass destroys one found out of the world.
  **No leak found.** Of all vehicles only carpools carry that interface [verified].
- **Food and ice cream trucks**: `FoodTruckManager.Update` and `IceCreamTruckManager` cap the count and send extras away
  (`FadeOut` + destroy). **No leak found** in the parts read; the truck's route runs in a `OneShotFunction` that a save
  may cut [inferred, not checked].
- **Owned bikes and brooms**: `Park` puts them in a rack or stand, or the rider's inventory. On an object reset
  (`DoReset`) they park only if the driver is still set, which the Sim's own reset has usually cleared, so a reset
  leaves them on the ground [verified]. These are household or NPC property; see L6.

### Leak paths

| # | Path | Vehicles left | Status |
|---|---|---|---|
| L1 | **Get-in interrupted.** `GetInVehicleRouteAction.PerformAction` gets or makes the vehicle, takes it out of the inventory, places it at the route start (`PlaceAt`), then yields in `FadeOut(yield)` before `PutInDriver`. A reset (`ResetException`) or any throw there runs the catch, which destroys only `DestroyOnRelease` vehicles (taxi boats). | NPC car placed in the street with no driver and **not reserved** (`OnCarEntered` never ran), so neither the arrive-home handler nor `OnReservingSimDisposed` will ever find it; a taxi or limo with its NPC driver sitting in it forever (nothing calls `ShouldRelease`); a taxi bike or scooter on the ground. | code [verified]; frequency [inferred]: every Sim reset in the 0.5 s get-in window (Overwatch and ErrorTrap resets, our watchdogs, NPC resets on load). |
| L1b | **Boat get-in gives up.** `GetInBoatRouteAction.PerformAction` places the boat, then `Boat.HaveSimWaitBeforeGettingIn` returns false after ~30 waits when another Sim is still getting in, and the action returns Terminate with the boat in the water. Same catch as L1 for throws. | NPC boat taken from the inventory and left in the water, unreserved; taxi boats survive only the throw path's destroy, not the Terminate. | code [verified]; frequency [inferred] low (group boat trips). |
| L2 | **Sim destroyed while seated.** `Sim.Destroy` unparents the Sim and `Sim.Dispose` calls `Vehicle.ReleaseSimRef` for `SittingInVehicle` only; neither `SittingInVehicle` nor `SittingInBoat` overrides `OnSimDestroy` / `OnSimDisposed`, so no get-out runs and `ShouldRelease` / `Release` never run. For `SittingInBoat` even the `Driver` field keeps the dead Sim (ErrorTrap's `DerefVehicle` clears it at load). | Taxi or limo an NPC drove itself, taxi bike or scooter, taxi boat (not reserved, so `OnReservingSimDisposed` does not help). NPC cars and boats are reserved, so they are handled. | code [verified]; who destroys a seated Sim [inferred]: a household moved out or deleted by story progression or a mod, `DestroyMyselfAndSleep` / `DestroyMyself`, NPC culling. |
| L3 | **Parking and mooring reservations to dead vehicles.** `ParkingSpace.ParkVehicle` sets `mReservedVehicle`; driving away does not clear it (by design, the car comes back to its own space), and nothing clears it when the vehicle is destroyed or ends up in an inventory. `Vehicle.GetClosestUnreservedParkingSpace` skips any space with a reservation, and `ParkingSpace.PostWorldLoadFixup` only overwrites a reservation when a car is parked there. | No vehicle, but the space: an NPC car that parked on a community lot and later went into an inventory or was destroyed can keep "its" space reserved for good (but see the correction below the table). The lot's spaces run out, later NPC cars go to inventories instead, players see empty spaces nobody uses. Same for `MooringPost`. | code [verified]; growth [inferred]: one dead reservation per community-lot space ever used by a now-gone NPC car. |
| L4 | **Frankensim intro taxi.** `OccultFrankenstein.MyBestFriendIntroduction.Run` makes a taxi with an NPC driver (`CreateTaxi`) and sets it as the Frankensim's reserved vehicle; `Cleanup` only clears the reservation. When the route to the lot does not use that taxi (walk, plan failure, `PlaceAtGoodLocation` fallback), it stays out of the world with its driver. | One taxi and one NPC driver Sim per such intro. | code [verified]; rare. |
| L5 | **Protest break-up cop cars.** `ImprovedProtestSituation.ProtestOutcomeBrokenUpByPolice` makes a service car per cop in `CopCars` and relies on the queued `DriveAwayInServiceCar` to destroy it; the situation's `CleanUp` does not. `Situation.Exit` leaves queued interactions in place (only clears callbacks), so the normal path works; a cop whose queue is cleared before that interaction runs (reset, cancel) leaves the car out of the world. | Out-of-world police `CarService`. | code [verified]; rare (EP9 protests). |
| L6 | **Owned bike or broom after a reset; NPC car reset in the street.** Object `DoReset` parks only when `Driver` is still set. For an NPC car with a home (`LotHome` set) the Sim's `SittingInVehicle.OnReset` gets it out but does not park it (only homeless NPC cars go to the inventory there); the car stays reserved, so the arrive-home handler or disposal parks it later. | Owned bikes and brooms: player or NPC property, on the ground until used. NPC cars: self-heal. | code [verified]; NPC car self-heal [inferred from the handlers, not traced end to end]. Household items: **no fix** (household rule). |

**Correction to L3 (2026-11-10, while building S1) [verified]:** `ParkingSpace.ParkVehicle` with a fade adds an alarm (`FadeCarIn`) that clears the reservation again when the car is not a `CarOwnable` whose `LotHome` is that lot, and `MooringPost.FadeBoatIn` does the same for boats. So an NPC car that parks on a community lot through EA's normal `Park` (fade) does not keep the space; the reservation stays only for (a) a car parked on its own home lot, (b) a park with no fade (`TryParkVehicle`, `ParkVehicle(v, false)`, `OnHandToolChildSlotted`), or (c) a fade alarm that never ran (destroyed or reset in the fade window). L3's growth claim is therefore weaker than written above [inferred]; the S1 counters (seen with the switch off) say how often it happens.

Not a leak, for the record: NPC cars parked in community spaces while their NPC is still in town. They are reserved and
come back to the NPC when it arrives home or is disposed [verified handlers; the "arrives home by another mode" case
inferred]. Overwatch tows them anyway (it keeps only residential-lot parking); EA would not lose them.

## 4. Fix designs (ts3fixes, each off by default)

Common rules for every switch:

- **Household exemption first.** Never touch: a vehicle whose driver or any passenger is a household or selected Sim; a
  `CarOwnable` / `BoatOwnable` not marked `GeneratedOwnableForNpc`; a bike, motorcycle or broom (owned property); any
  vehicle reserved or preferred by a household Sim; anything in a household Sim's or household inventory. A vehicle
  with any unknown holder counts as kept.
- **Only throwaway kinds are destroyed:** `CarNpcWithNpcDriver` except `CarpoolWithDriver`, `AdultTaxiBicycle`,
  `ChildTaxiBicycle`, `ScooterTaxi`, `BoatTaxi`. NPC-generated cars and boats are never destroyed by us; they are given
  back to their NPC the way EA's own `Park` does (into the NPC's inventory), or left alone when the NPC is gone (EA's
  disposal handler then deals with them).
- **Never destroy a vehicle with a live Sim in it or using it** (`Driver` alive, any live passenger, `ActorsUsingMe`).
  A taxi whose only occupant is its pooled NPC driver (`NpcDriversManager.IsNpcDriver`) is released the way EA does in
  `DriveOffscreenAndRelease`: release the driver to the pool, destroy driver and car.
- Destroys run in a `OneShotFunctionTask` on the next tick, never inside the hooked frame, and re-check everything first.
- Counters count the cases also with the switch off (project style), so the off arm of a soak shows how often each
  path fires. Counter slots: reserve with `node tools/registry.ts reserve fixes` when built.

| Switch | Fixes | Hook | What it does | Risks |
|---|---|---|---|---|
| `vehparkreserve` (S1) | L3 | Swap `Vehicle.GetClosestUnreservedParkingSpace` (static, the single place that picks a free space) and add a `deadrefsweep` phase | Before EA's loop, a space whose reserved vehicle is destroyed (`HasBeenDestroyed` / invalid object id) or is an NPC-generated car now in an inventory gets the reservation cleared; the phase does the same for every `ParkingSpace` and `MooringPost` hourly in the existing slices. A household car's reservation is kept even when the car is in a household inventory (players expect it back in its space). | Low: it only clears a field EA already treats as optional. Must not clear a reservation whose car is parked there (`GetParkedVehicle` non-null). |
| `vehgetinleak` (S2) | L1, L1b | Swap `GetInVehicleRouteAction.PerformAction` and `GetInBoatRouteAction.PerformAction` (internal classes; same swap as other route-action hooks). The vehicle is a local, so the wrapper reads the Sim's state before and after: the hook on `Sim.GetVehicle` / `GetBoatForGetInBoatRouteAction` (a thin swap) remembers the vehicle it returned for that Sim | On a throw (rethrown unchanged) or a Terminate return, when the remembered vehicle is placed in the world and the Sim is not seated in it: a throwaway kind is released (EA's own rule for `DestroyOnRelease` extended to the other throwaway kinds); an NPC-generated car or boat goes back into its NPC's inventory (`Inventory.TryToAdd`, as `CarOwnable.Park` does for NPCs). Household vehicles: counted, untouched. | The get-in also runs for household Sims calling a taxi; the taxi is not household property, but the household Sim must not be in it (checked). A reset can arrive mid-`PutInDriver`; the seated check covers it. |
| `vehseatdestroy` (S3) | L2 | `EventTracker` listener on `kSimDestroyed` (sent at the top of `Sim.Destroy`, posture still set) | If the destroyed Sim sits in a vehicle or boat, note the vehicle; on the next tick, when it has no live driver, no live passenger and is not in use, clear a dead `Driver` / passenger reference and release a throwaway kind (as in S2). NPC-generated cars: nothing (their reservation handler runs at dispose). | A household Sim deleted while driving its own car: the car is household property, skipped. A Sim destroyed during travel: run only in live mode (`GameStates.IsLiveState`). |
| `vehfrankentaxi` (S4) | L4 | Swap `OccultFrankenstein.MyBestFriendIntroduction.Cleanup` | Before EA's cleanup, if the reserved vehicle is still that throwaway taxi and nobody but its pooled driver is in it, release it. | Rare path; low. |
| `vehprotestcar` (S5) | L5 | Swap `ProtestOutcomeBrokenUpByPolice.CleanUp` (or a check when the protest situation ends) | Cop cars in `CopCars` that are out of the world, with no driver and not in use, are destroyed one sim-hour after the outcome ends (the queued drive-away has had its chance). | Rare path; low. Could be dropped if the census never shows it. |

Not proposed: a general "tow everything with no holder" sweep. It needs the full holder list (situations, carpools,
interactions with a selected vehicle, stored inventories), which is what makes Overwatch's pass broad; the source fixes
above keep the leftovers from being made. If the census (§5) still shows unexplained orphans of a throwaway kind after
S1-S3, the next step is a load-time pass in EA's own style: give the throwaway kinds the `IDeletedIfOutOfWorldOnLoad`
treatment (out of the world, not in an inventory, no driver -> destroyed in `LotManager`'s post-load lot pass), which EA
already applies to carpools.

Order and effort (wall clock, Claude with agents): S1 ~40 min build + self-test, S2 ~1 h (two swaps + the remember
hook), S3 ~40 min, S4/S5 ~20 min each; each needs one repro run (~10-15 min) per AGENTS.md "repro per fix". The census
command ~40 min. Smallest useful first step: the census plus S1, since L3 grows on every save with community lots.

Repros (new entries for `tools/harness/repros.json`, triggered by the agent, never waiting for play):

- `veh-park-reserve-dead`: on a community lot, make an NPC car (`CreateObjectOutOfWorld` of an NPC model, marked
  generated), park it with `TryParkCarInSpace`, destroy it, wait 5 s; off: the space stays reserved and
  `GetClosestUnreservedParkingSpace` skips it; on: it returns that space.
- `veh-getin-reset`: an NPC with no vehicle (`setup_sims`, autonomy off) is sent to another lot by car (`sim_visit_lot`
  with a car route); the step polls each tick until the Sim's current route action is the get-in action, then resets the
  Sim. Off: census orphan count +1 (NPC car or taxi in the street); on: +0, the NPC car is in the NPC's inventory.
- `veh-seat-destroy`: an NPC drives itself in an NPC taxi (`CreateNpcCar` path), the step destroys the Sim once its
  posture is `SittingInVehicle`. Off: one driverless taxi left; on: gone one tick later.

## 5. Measuring it in game

The agent has no command that sees vehicles outside the world: `list_objects` lists in-world objects of one lot,
`spawn_traffic` and the `RareVehicleHandlers` commands make vehicles, `world_census` counts Sims and households. **A new
command is needed.**

`vehicle_census {samples?: 10}` (sketch, read only, one pass over `Sims3.Gameplay.Queries.GetObjects<Vehicle>()`, which
includes inventory and out-of-world objects):

- **kind:** `player_car`, `npc_car` (generated), `taxi` (`CarNpcWithNpcDriver` not carpool), `carpool`, `service`,
  `traffic`, `food_truck`, `ice_cream`, `player_boat`, `npc_boat`, `taxi_boat`, `bike` (owned), `taxi_bike`
  (incl. scooter taxi), `broom`, `other`.
- **where:** `inventory_household`, `inventory_npc`, `inventory_other`, `parked_residential`, `parked_community`
  (space, post, rack, stand), `in_world_loose`, `out_of_world`.
- **driver:** `none`, `live_household`, `live_npc`, `pooled_driver`, `destroyed`.
- **held:** reserved by a live Sim (`Sim.ReservedVehicle` over all Sims), preferred by a resident, in a carpool, or
  held by a situation (fields of type `Vehicle` on `Situation.sAllSituations`, by reflection, cached per type).
- **orphan** = not held, no live driver or passenger, not in use, not in an inventory, not parked on a residential lot;
  counts per kind, plus `samples` examples (type, id, position, lot, first seen sim time from a static dictionary
  the command keeps between calls, so age shows).
- **parking:** spaces and posts total, reserved with a car, reserved to a destroyed vehicle, reserved to a vehicle not
  in the space, per lot type.
- The new switches' counters in the same reply.

Soak plan: Baseline-BP and Baseline-SV, 3 sim-days at speed 3, the census every sim-hour as a queue step, `[CrashForensics]
Light=1`. Arms: all vehicle switches off vs on, in-load A/B with the runtime switches. In a profile with Overwatch, its
nightly pass destroys the orphans and clears dead reservations at the alarm hour, so for this measurement only set its
CleanupVehicles options off in the test profile, or read growth per day between two nightly passes. Expected off arm
[inferred]: dead reservations rise on community lots day over day; orphan `npc_car` / `taxi` / `taxi_bike` rise with
Sim resets (the census can be paired with the watchdog and reset counters). Pass for the on arm: those rows flat, no
change to household rows (`player_car`, `player_boat`, `bike`, household inventories), no new script errors.

## 6. Open checks

- How often L1 fires in natural play: the S2 counter with the switch off answers it.
- Whether `FoodTruckBase`'s route `OneShotFunction` survives a save (food truck left mid-route after a load) [inferred
  open].
- The "NPC car comes back" chain for an NPC that arrives home by taxi or walking was read handler by handler, not traced
  in game [inferred]; the census's `parked_community` + `held` rows check it.

## 7. What was built (2026-11-10)

Code: `mods/ts3fixes/src/VehicleLeaks.cs` (all five switches and the agent check `Ts3Fixes.Fixes.VehicleLeakCheck`),
`mods/ts3agent/src/plugin/Handlers/VehicleCensusHandlers.cs` (`vehicle_census`). Built with Roslyn on Linux (no errors, no
warnings); not run in game. Tunings in `Instantiator.cs` and `TUNING_XML` are off (AGENTS.md "every new switch defaults
off"); a local test package with all five on is `node tools/setup/build-fixes.ts --on
kVehParkReserve,kVehGetInLeak,kVehSeatDestroy,kVehFrankenTaxi,kVehProtestCar` (writes
`build/ts3fixes-on-kVehParkReserve-kVehGetInLeak-kVehSeatDestroy-kVehFrankenTaxi-kVehProtestCar.package`; install it in
place of `ts3fixes.package`, never both). Runtime A/B: agent `patches {vehparkreserve: false, ...}`. All hooks are
patched at startup and gated per call, so a runtime switch takes effect at once.

| Switch (tuning) | Hooks | What it does as built | Counters (2200-2216) |
|---|---|---|---|
| `vehparkreserve` (`kVehParkReserve`) | `Vehicle.GetClosestUnreservedParkingSpace` (static swap); hourly pass over every `ParkingSpace` and `MooringPost` | a reservation with no vehicle parked there, whose vehicle is destroyed, or is an NPC-generated car/boat in an inventory whose home is another lot, is cleared with EA's `UnReserveSpot`. Kept: spaces on the active household's lot, a player car or boat that still exists | 2200 seen at a pick, 2201 cleared at a pick, 2202 seen hourly, 2203 cleared hourly, 2204 kept (household); a space/vehicle pair counts once |
| `vehgetinleak` (`kVehGetInLeak`) | `GetInVehicleRouteAction.PerformAction`, `GetInBoatRouteAction.PerformAction`, `Sim.GetVehicle` (remembers the vehicle for a Sim inside a car get-in; the boat is read from `mLastFoundBoatId`) | after a throw (rethrown unchanged) or any non-success return, a vehicle in no inventory, on no parent and not holding the Sim: next tick, a throwaway kind is released (pooled driver back to the pool and destroyed, vehicle destroyed: `DriveOffscreenAndRelease` without the drive), an NPC-generated car/boat goes into the routing NPC's inventory (`PendingUse` cleared, as `CarOwnable.Park`). Kept: a household / selected / unknown routing Sim, any household or other live Sim in or using it, player vehicles | 2205 seen, 2206 released, 2207 to inventory, 2208 kept |
| `vehseatdestroy` (`kVehSeatDestroy`) | `kSimDestroyed` listener (posture still set) | next tick, if the vehicle still exists (EA's own releases destroy taxis and carpools in the same frame, so those are not counted): dead driver / passenger references cleared with `ReleaseSimRef`, a throwaway kind with no live non-pooled occupant released. Skipped: a household-scope destroyed Sim, player vehicles, bikes and brooms, live mode only | 2209 seen, 2210 refs cleared, 2211 released |
| `vehfrankentaxi` (`kVehFrankenTaxi`) | `OccultFrankenstein.MyBestFriendIntroduction.Cleanup` (before EA's) | the actor's reserved throwaway taxi that never entered the world is released next tick if still out of the world, not reserved again and nobody else is in it | 2212 seen, 2213 released |
| `vehprotestcar` (`kVehProtestCar`) | `ImprovedProtestSituation.ProtestOutcomeBrokenUpByPolice.Init` (records `CopCars`); hourly check | a recorded cop car one sim-hour or more old with no driver, no occupant or user, and no `PoliceGoToProtesttLocation` / `DriveAwayInServiceCar` for it in the cop's queue is destroyed (in or out of the world; the doc design said out of the world only, but after `GetOut` the car is in the world). Tracking is not saved; dropped after 48 sim-hours | 2214 tracked, 2215 left, 2216 destroyed |

Throwaway kinds as built: `CarNpcWithNpcDriver` except `CarpoolWithDriver` (so `CarLimo` / `HoverCarLimo`, which are
carpool classes, are not touched) and `CarMovingVanWithDriver`; `AdultTaxiBicycle` and `ChildTaxiBicycle` with their
scooter subclasses; `BoatTaxi`. Taxis a household Sim called (S2 with a household routing Sim) are left as EA leaves them.

Log lines (ts3native.log via `[UserDiag]`, prefix `ts3fixes: `; also the agent's `recent` list; at most 200 per switch
per session):

- `vehparkreserve: <pick|hourly>: cleared <ParkingSpace|MooringPost> <id> reservation to <Type> <id> (<destroyed|npc vehicle in an inventory>) on <lot>; seen pick N hourly N, cleared pick N hourly N, kept household N`
- `vehgetinleak: <Type> <id> left by <Sim>'s get-in (<threw ResetException|Terminate|...>): <released|put in the NPC's inventory|kept (...)>; seen N, released N, to inventory N, kept N`
- `vehseatdestroy: <Sim> destroyed while seated in <Type> <id>: N dead reference(s) cleared[, released]; seen N, refs cleared N, released N`
- `vehfrankentaxi: <Type> <id> made for <Sim>'s intro never entered the world: released; seen N, released N`
- `vehprotestcar: protest break-up <Type> <id> (in|out of the world) with no driver and no drive-away queued after N sim-hour(s): destroyed; tracked N, left N, destroyed N`

A hook that throws inside our code logs once to the applied list (`patches` -> `applied`) as `<switch>: pass threw ...`.

Census: `node tools/harness/ag.mjs vehicle_census '{"samples":10}'` (`"list": true` adds every vehicle). Reply: `total`,
`by_kind`, `by_where`, `by_driver`, `by_held`, `kind_where`, `orphans_total`, `orphans` (per kind), `orphan_samples` (per
kind: type, id, kind, where, driver, held, pos, lot, `age_sim_hours`), `orphan_oldest_sim_hours`, `parking`
(`spaces_residential` / `spaces_community` / `posts_*`: total, free, reserved_with_car, car_not_reserved,
reserved_destroyed, reserved_car_elsewhere), `switches`, `counters` (the 17 slots by name). Kinds add `limo`,
`moving_van`, `ufo` and `in_world_parented` to the §5 lists. The counters also appear in `patches`.

Repros (`tools/harness/repros.json`, all UNVERIFIED, never run): `veh-park-reserve-dead` (forced, off/on), `veh-seat-destroy`
(forced, off/on, a fresh NPC made from the driver pool, no save Sim touched), `veh-getin-reset` (forced trigger: `getin arm`
resets the next NPC get-in in its fade; N/A when the townies walk), `veh-leak-soak` (natural soak, the only coverage for
S4/S5).

Open: no game run yet; whether `Sim.GetVehicle` and the two route actions are still uncompiled when ts3fixes patches at
startup (the `applied` list says `ok` or `already compiled`); S2 on a household Sim's taxi is deliberately not done.
