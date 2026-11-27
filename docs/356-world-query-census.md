# 356. World-wide query census: who asks for everything in the world (RX-2a, 2026-11-26)

Status: static only. Nothing built, nothing run. Steam 1.67: decompiled script assemblies (local only) through
`tools/world-query-census.ts`, and `game-copy/bin/TS3W.exe` in raw disassembly with capstone. EA 1.69 not checked.
Package RX-2a of docs/340 §6.1. docs/344 §3 did the object half by type counts; this doc classifies every site and
adds the Sim, household, lot and description lists.
Ledger: docs/51 §625. Handoff: `docs/handoff/rx2a-world-query-census-1126.md`.
Data: `docs/data/world-query-census.csv` (1,270 rows: assembly, file, line, class, member, type, ask, frequency).

How to read the markers: counts and the CSV are [verified] as text matches. The **ask** and **frequency** columns
come from text rules (§1), so each is [inferred] unless a row in this doc says it was read.

## 0. Short answer

- **1,270 sites ask for a whole-world list** in EA's four script assemblies: 329 for objects by type, 75 for all
  Sim objects, 172 for all lots, 58 for all households, 3 for all descriptions, and 633 calls of
  `SimDescription.Find`, which is a whole-world scan in disguise (§4.3).
- **World-wide object queries are rare in steady play.** Of the 329 object sites, 6 sit in a task loop, 8 on a
  repeating alarm and 21 in an interaction test; 137 run only at load, from a cheat or from UI. 70 have a caller the
  tool could not trace; they are grouped by hand in §3.4.
- **There are two native choke points, not one.** docs/344 named the host method `0xD798F0` (get). The count
  calls go through a second host method, `0xD74C00`, that walks the map itself and never calls `0xD798F0`
  [verified, V-625-1]. A lazy facade on `0xD798F0` alone would give wrong counts at the 22 `CountObjects<T>()` sites.
- **One managed choke point covers both:** the static forwarders of `Sims3.SimIFace.Queries`. Every script caller,
  EA's or a mod's, goes through them (§2.2).
- **For lazy objects (L2):** 149 distinct types are asked for world-wide. 56 of them only at load, from a cheat or
  from UI; 5 more only as a count or an exists test. The other 88 types are the "materialise" list for docs/340
  LZ-4 (§3.3). Six sites ask for `GameObject` itself; all are load-time validators.
- **For far Sims without an object (T4):** 107 sites enumerate Sim **objects** (`LotManager.Actors` or
  `GetObjects<Sim>`), so they would not see a Sim that exists only as a description. 9 of them run in normal play
  and write to each Sim (§4.1). These join the reader list of docs/349.
- **Lists of lots, households and descriptions need nothing:** lots and descriptions stay in every stage.
- No question for Rodney. Six open points for builders (§7).

## 1. Method and limits

`node tools/world-query-census.ts --out docs/data/world-query-census.csv` reads the decompiled C# of
Sims3GameplaySystems, Sims3GameplayObjects, Sims3StoreObjects and UI and writes one row per call. It has a
self-check (`--self-check`, run by `tools/selftest-all.ts`). RX-2b runs the same tool over mod sources by passing
the folders as arguments.

| Column | Values | Rule |
|---|---|---|
| family | `objects-all`, `objects-count`, `objects-global`, `sims-all`, `lots-all`, `households-all`, `descriptions-all`, `description-by-id` | which call matched |
| type | the `T` of the call | text |
| ask | `exists`, `count`, `pick`, `find-first`, `nearest`, `iterate-read`, `iterate-write`, `wrapper`, `lookup`, `stored`, `discard` | what the statement and the loop after it do with the answer |
| freq | `tick-loop`, `interaction-test`, `alarm-repeating`, `interaction-run`, `alarm-once`, `event`, `ui`, `load`, `cheat`, `helper` | name and class of the member, an `AddAlarm*` in the same file that names it, else the hottest caller one hop away (column `via`) |

Limits:

- Text rules, not a compiler. `iterate-write` means the loop body calls a member whose name looks like a change
  (`Set`, `Add`, `Destroy`, ...) or assigns through the loop variable. `find-first` means the loop can leave early.
- `helper` means the caller was not traced: the member is called through an instance from another file, through a
  delegate, or it is a property. It does not mean "rare".
- One hop only. A member called from a task loop through two helpers shows as `helper`.
- Lot-scoped and range queries are not in this census: they name their lot or centre, so docs/344 §3.3 rule 1
  covers them.
- Not counted: reads of a cached list that was filled by one of these calls at load (the map tag manager and the
  role manager keep such lists).

## 2. Choke points

### 2.1 Native [verified, V-625-1, V-625-2]

| Script call | Entry | Host method (vtable `0x108B690`) | Reads |
|---|---|---|---|
| `Query_GetObjects` (world) | `0x749630` (table slot `0x1160710`) | slot `+0xC0` = `0xD798F0` | host maps `+0x1658`, `+0x1678` |
| `Query_GetObjectsOnLot`, `Query_GetObjectsInRange` | `0x749D30`, `0x7496E0` | `+0xC0` with an id list | id list, then id -> object `0xD726F0` |
| `Query_GetGlobalObjects` | `0x749DD0` | `+0xC0` with the id list `0x749B40` builds | list at `[0x11ECBC4] + 0xB0`; 3 EA sites (`Sim.PostWorldLoadInit`, the two `WorldLot` overrides) |
| `Query_CountObjects` (world) | `0x749610` (slot `0x11606F0`) | slot `+0xC4` = `0xD74C00` | host map `+0x1658` |
| `Query_CountObjectsOnLot`, `Query_CountObjectsInRange`, `Query_CountGlobalObjects` | `0x749BC0`, `0x749770`, `0x749C80` | `+0xC4` with an id list | as the get forms |
| `Query_GetSimsOverlappingFootprint`, `Query_GetObjectsOverlappingObject` | `0x74A0C0`, `0x749E60` | lot scoped, not through these two | - |

- `0xD74C00` has its own walk: 129 instructions, calls to the id lookup `0xD726F0` and the class test, no call to
  `0xD798F0`. So "count" and "get" are two entries into the same maps.
- Both are also read points of the autonomy shadow worker (docs/122, docs/131, `native/ts3native/autshadow.cpp`).
  Before any hook: `node tools/hook-sites.ts --check` and docs/68.
- `Simulator_GetAllSimulationObjectKeysImpl` (`0x76D3D0`, table slot `0x1161008`) is the only other script call that returns every
  simulation object. No gameplay or UI assembly calls it [verified by grep].

### 2.2 Managed

| Choke point | Covers | Sites |
|---|---|---|
| `Sims3.SimIFace.Queries` static forwarders (`GetObjects(Type)`, `CountObjects(Type)`, `GetGlobalObjects`, `CountGlobalObjects` and the lot and range forms) | every script caller; `Sims3.Gameplay.Queries` generic wrappers call them, 24 EA lines call them directly | all 329 object sites |
| `WorldLot.GetObjects<T>` / `CountObjects<T>` | `lot.GetObjects<T>()` on the world lot: it asks for the **global** objects and keeps Sims whose lot is the world lot [verified, V-625-3] | every lot-scoped site whose lot can be the world lot |
| `LotManager.sActorList` (read as `LotManager.Actors`) | all Sim objects; written at 4 places in `LotManager` (2 adds / removes, 2 clears) [verified] | 75 |
| `Household.sHouseholdList`, `LotManager.sLots` | households, lots | 58, 172 |
| `SimDescription.Find(ulong)` | id -> description | 633 |

Recommendation for LZ-4: put the facade on the managed forwarders, not on the two host methods. One patch point
per form, no conflict with S3PP or the shadow worker, and a mod that calls `Sims3.SimIFace.Queries` directly is
covered too. The native entries stay as the fallback if `Fixes.InlineShape` (docs/293) shows the forwarders are
inlined.

## 3. Object queries (329 sites, 149 types)

### 3.1 What they ask [inferred, text rules]

| Ask | Sites | Lazy objects (L2) need |
|---|---|---|
| `iterate-read` | 137 | materialise every record assignable to `T` |
| `iterate-write` | 83 | same |
| `find-first` | 32 | same (could stop early, but the array is built first) |
| `pick` (random or first element) | 28 | same |
| `exists` | 21 | answer from the record table, no materialise |
| `count` | 12 | answer from the record table |
| `wrapper` (returns the array) | 12 | follow the caller |
| `nearest` | 1 | materialise |
| `stored`, `discard` | 3 | materialise |

33 sites (10%) only need a number. The rest need the objects.

### 3.2 How often [inferred, text rules; rows marked "read" were checked in the code]

| Frequency | Sites | Note |
|---|---|---|
| `load` | 97 | world load, startup, fix-ups, validators, clean-ups: every object is live then |
| `helper` | 70 | caller not traced; grouped in §3.4 |
| `interaction-run` | 39 | when a Sim runs that interaction |
| `event` | 31 | listeners and callbacks |
| `cheat` | 26 | debug commands and debug interactions |
| `interaction-test` | 21 | pie menu build and autonomy scoring: can run many times a minute |
| `alarm-once` | 17 | |
| `ui` | 14 | map tags, pickers, panels |
| `alarm-repeating` | 8 | |
| `tick-loop` | 6 | |

The 35 sites that can run in steady play without the player (task loops, repeating alarms, interaction tests):

| Frequency | Member | Type asked | Ask | Note |
|---|---|---|---|---|
| tick-loop | `RoleManagerTask.FillRoleGiverObjects` | `IRoleGiver` | iterate | role manager task; sleeps 5-120 sim minutes between passes (read) |
| tick-loop | `FoodTruckManager.Update` | `FoodTruck` | iterate-write | from the traffic task, once per traffic check, hours apart (read); also walks all lots |
| tick-loop | `Coupon.Create` (3 sites) | `IBookstore`, `IGrocery`, `IDaySpa` | exists | inside the computer's "find coupons" loop |
| tick-loop | `SimDescription.Find` | `Urnstone` | find-first | §4.3 |
| alarm-repeating | `Car.TurnAllCarHeadlightsOn` / `Off` | `Vehicle` | iterate-write | twice a day (read: two day alarms) |
| alarm-repeating | `DaylightLight.OnUpdateLighting` | `DaylightLight` | iterate | repeating, period from tuning (read) |
| alarm-repeating | `AgingManager.DayPassedCallback` | `ISchoolRabbitHole` | exists | daily |
| alarm-repeating | `ImprovedProtestSituation.ScheduleTodaysprotest` | `ICityHall` | pick | daily |
| alarm-repeating | `KrakenManager.KrakenAttackEndAlarmCallback` (2) | `IBoat`, `UnchartedIslandMarker` | find-first, iterate | Island Paradise only |
| alarm-repeating | `Proprietor.GetListOfAllPossibleSimFestsOnAGivenDayOfWeek` | `IShowController` | iterate | Showtime, via the SimFest schedule |
| interaction-test | 21 sites: `Subway` (4), `ShoppingRabbitHole` (3), `Bookstore` (2), `CarOwnable` (2), one each for `RabbitHole`, `IHoverTrainStation`, `IDaySpa`, `Lot`, `Book`, `Urnstone`, `ScienceLab`, `IServoBotArena`, `Sim`, `Wildflower` (the last two are debug tests) | | exists 8, find-first 7, count 2, wrapper 2, iterate 1, nearest 1 | mostly "is there such a rabbit hole in town" |

Rabbit holes, subways, city halls, schools and island markers are never lazy (docs/344 §3.2), so most of this
table needs nothing. What is left for lazy objects in steady play: `Vehicle`, `DaylightLight`, `FoodTruck`, `IBoat`,
`Book`, `CarOwnable`, `Urnstone`.

### 3.3 Types: the materialise list for LZ-4

For each of the 149 types, the hottest site that asks for it:

| Hottest site | Types | L2 rule |
|---|---|---|
| load, cheat or ui only | 56 | may be lazy: every such query runs when the objects are live, or it is a debug path. `GameObject` (6 sites) is here: all six are load-time validators |
| count or exists only (and not in the row above) | 5 | may be lazy if the record table answers counts by class: `ISpirit`, `IServoBotArena`, `MagicGnome`, `MarineLifeBase`, `MarineLifeSurfaceSpawner` |
| anything else | 88 | **not lazy in the first L2 build**, or a query for the type materialises all records assignable to it and pins them (thrash guard, docs/340 §3.6) |

The 93 types outside the first row by hottest site: tick-loop 6, interaction-test 12, alarm-repeating 7,
interaction-run 18, alarm-once 9, event 18, helper 23 (the lists are in the CSV; sort by `type`). `T` as a type parameter (8 sites) is the wrappers
themselves and the house-light helpers.

The rule is about the **type asked for**, and a query for an interface or a base class matches every class under
it. Turning the 88 names into a class allow-list needs the class hierarchy; that is open point O1.

### 3.4 The 70 sites with an untraced caller, grouped by hand [inferred from names; "read" where checked]

| Group | Sites | Members | Runs |
|---|---|---|---|
| Weather changes | 7 | `SeasonsManager` temperature, flag, thunder and rainbow updates; `FishingSpot.UpdateAllFishingSpots`; `PondManager.AllPondsClear` | at a weather or temperature change (read: 5 callers are in `SeasonsManager`) |
| Spawners | 8 | `MinorPet.CanSpawnMoreMinorPetsInWorld`, `MinorPetSpawner.SpawnObject`, `MarineLifeSpawner` (4), `SpiritMaker`, `ObjectSpawner.CheckAllObjectSpawnersPlacement` | each time a spawner spawns (read: three spawner classes call the minor-pet test). One world-wide walk per spawn, so the cost is spawners times objects |
| House lights and holograms | 9 | `SwitchLightsInHouse`, `SetColorHouse`, `ChangeFormHouse` and friends | a player interaction |
| Urns and description ids | 8 | `SimDescription` id and genealogy helpers (4), urn finders, kill helpers | a new description id, a death, load |
| School, career, situation | 6 | afterschool, prom, field trip, job spec, `Household.IsSomeActiveSimAging` | events |
| Collectibles, coupons, clubs | 10 | `Opportunity.CollectiblesForOpp` (4), coupons (4), club listing, theatre | an opportunity or interaction |
| Island and dive markers | 6 | `UnchartedIslandMarker` (5), `DiveWell` | Island Paradise events |
| World builder data and save metadata | 6 | `WorldBuilderData` (3), recipe check, task list, world metadata | load, save |
| Wrappers | 2 | `WorldLot.GetObjects`, `WorldLot.CountObjects` | §2.2 |
| Single cases | 8 | Eye of Horus (2), imaginary doll, pet toy crate, potion effect, seasonal lot marker, stylist, art pedestal | events |

## 4. Sims, households, lots, descriptions

### 4.1 All Sim objects: 107 sites (`LotManager.Actors` 75, `GetObjects<Sim>` and `<Sim.Placeholder>` 32)

These see only Sims that have a Sim object. A far Sim kept as a description only (T4, docs/324 §5.6) is not in
the list. EA already lives with that for homeless townies and for Sims that are not instantiated, so a missing
Sim is a state EA handles; what changes is how many are missing.

| Frequency | Sites |
|---|---|
| helper 38, cheat 20, load 15, interaction-run 10, ui 8, event 8, alarm-once 5, alarm-repeating 2, interaction-test 1 | 107 |

Sites that run in normal play and act on every Sim [inferred; none read beyond the loop header]:

| Member | Ask | Effect on a T4 Sim |
|---|---|---|
| `Autonomy.OnceADayCallback`, `Autonomy.OnceAnHourCallback` | iterate-write | daily and hourly per-Sim upkeep is skipped; the far model must cover it or the Sim gets it at go-live |
| `Sim.EnforceTeenCurfew` | find-first | far teens are never sent home (they are not out) |
| `HolidayManager.OnGiveHolidayBuff`, `ClearCurrentHoliday` | iterate-write | holiday buff missed; buffs are not model state (docs/345), so nothing to do |
| `Lot.RouteRandomNPCSimsHere`, `RabbitHole.RouteRandomNPCSimsHere` | iterate-write | EA picks only from live Sims; fewer candidates near a venue |
| Weather reactions (`MakeAllSimsReactToPrecipitation`, rainbow, thunder, `PutOutFires`) | iterate-read | visible reactions only; a far Sim is unseen |
| `SocialComponent.FindSimsOfRelationshipType` (13 sites), `Gossip.FindRandomSimToTalkAbout`, `MindMeld` | find-first, pick | candidates come from live Sims only; a household Sim's search does not find a far friend. **This one is household-facing**: see O3 |
| Consignment and stand sellers (4), `Stylist.GetSimToMakeover`, `Medical.GetSimToFollowUp`, `FieldTripSituation` | iterate-read | NPC candidate picks |
| Wish and challenge start functions (8) | iterate-read | "know a Sim", "know a celebrity": counted over live Sims |
| `Autonomy.GetDesiredNumSimsForMetaObject`, `LotManager.GetWorldMetaData` | count | population counts drop by the number of far Sims |

For stage 2a (object kept, parked) none of this changes: the Sim is still in the list. A parked Sim that one of
the `iterate-write` sites touches gets the write on a sleeping Sim; docs/345 Q11 (go-live first, or let the write
land) decides it, and the two autonomy callbacks above are the sites to read first.

### 4.2 Lots (172) and households (58)

Lots and households stay in every stage, so no site needs a facade. They matter for scaling: each is a loop over
the whole town.

- Task loops: `Camera.GetClosestVisibleLot` from the camera task, `TrafficManager`'s update task,
  `CarNpcWithNoDriver.LiteDriver`, `FoodTruckManager.Update`.
- Repeating alarms: venue open and close helpers (9 sites in `VenueFlowUtility` and `Bartending`),
  `OpportunityManager.GetLotTarget` (3), `ResortManager.DailyUpdate`, stray pets, wild horses, laundry, alien
  visits, `RealEstateManager.AlarmCallback`, `Autonomy.OnceADayCallback`, `AcademicCareer.RefreshProfessors` (2).
- Interaction tests over all lots: 8.

None is checked against docs/333 (loop-cost scaling) row by row; the CSV rows with `family` = `lots-all` and a
hot `freq` are the list to compare.

### 4.3 `SimDescription.Find` is a whole-town scan (633 call sites) [verified, V-625-4]

`SimDescription.Find(id)` has no index. It walks every household and every description in it; on a miss it asks
for every `Urnstone` in the world (one world-wide object query, which builds an array), then two small lists. So:

- a hit costs half the residents on average;
- a miss costs all residents plus a walk of the host object map;
- 633 sites call it: 24 on repeating alarms, 17 in interaction tests, 3 in task loops, 54 in interaction runs, 67
  in events, 65 in UI, 273 untraced.

This is the id -> resident lookup that the far model, the facade and docs/349's readers all lean on. It grows with
the town, so it belongs to the docs/333 list (candidate in §6). For lazy objects it also means `Urnstone` is asked
for world-wide from a very common path, so urns cannot be lazy unless misses are answered from a table.

### 4.4 All descriptions (3 sites)

`SimDescription.GetAll`, `GetSimDescriptionsInWorld`, `GetHomelessSimDescriptionsFromUrnstones`: two at load, one
wrapper. Most code reaches descriptions through the household list (58 sites) instead.

## 5. What RX-9 and LZ-4 take from this

1. Facade rows: get (world), count (world), get global, count global, plus the `WorldLot` overrides. The lot and
   range forms stay as docs/344 has them.
2. Count and exists (33 sites) are answered from the record table without materialising. This needs a class ->
   count map per recorded class and its base types and interfaces.
3. Materialise list: the 88 types of §3.3, until O1 turns them into classes.
4. Unknown-site default: a world-wide query for a type that is on no list materialises everything assignable to
   it, logs the site once and pins those records. This is the "unknown-site default" SD-4 has to write
   (docs/340 §6.2).
5. Sims: 107 enumeration sites join docs/349's reader table as "enumeration readers"; the 9 rows of §4.1 are
   the ones a T4 build has to decide on.
6. RX-2b (mods) runs the same tool; the NRaas world-wide walks (docs/344 §7 item 6) will show up as
   `objects-all` with type `GameObject` or `Sim`.

## 6. Candidates noticed on the way (not built, not measured)

| Candidate | What | Why it might pay | First step |
|---|---|---|---|
| Description index (**built 2026-11-26, docs/359, default off, not run**) | id -> `SimDescription` table kept exact by hooking the writers of the household lists, falling through to EA's scan on a miss | §4.3: every lookup is O(residents); the cost grows with the town and the lookup count grows with it too | count `SimDescription.Find` calls per second and misses per second in a bundle run (measure before build); check docs/333 for an overlap |
| Spawner world walks | the per-spawn "may I spawn more" tests walk the whole world (§3.4) | spawners times objects; the far-object machine already schedules spawners (docs/350), so far spawners stop calling it | read the three spawner callers; no build until a profile shows it |
| `Camera.GetClosestVisibleLot` | all lots from the camera task | loop over every lot per camera update | check the cadence in the code; compare with docs/333 |

## 7. Open points (none for Rodney)

| # | Point | Who |
|---|---|---|
| O1 | Class hierarchy: which concrete classes are assignable to each of the 88 types of §3.3 (interfaces and base classes), to turn the type list into the L2 class allow-list | LZ-1 / LZ-4, with static .NET metadata |
| O2 | The 70 `helper` rows are grouped from names; a second hop in the tool, or a hand read, would give each a traced caller | RX-9 if a row decides a stage |
| O3 | `SocialComponent.FindSimsOfRelationshipType` (13 sites) picks from live Sims: when a household Sim asks, a far friend is missing from the candidates. Is that inside "household untouched"? The tie rule of docs/345 §4.2 already keeps friends of the household live, so it should not occur; the 13 sites need a read to confirm | FS-16 / RX-9 |
| O4 | `0xD74C00` walks only the map at `+0x1658` while `0xD798F0` reads `+0x1658` and `+0x1678`: what the second map holds, and whether count and get can disagree today | only if a native facade is built |
| O5 | Real call rates: nothing here is measured. The shadow build should count calls per choke point per sim hour | LZ-1 |
| O6 | EA 1.69 addresses; the managed census on the 1.69 assemblies | RX lane |

## 8. 4x and streamed worlds

- Every site in this census is a loop over the whole world: its cost grows with the world, and the sites that run
  on timers grow in number too (more venues, more spawners). The census is the list of places where a 4x world
  costs more than 4x.
- A lazy-object facade shrinks the object walks only for types on the lazy list; the Sim, lot and household walks
  are untouched by it.
- `SimDescription.Find` (§4.3) is the clearest case: it is per call O(residents), so a town with 4x residents and
  4x callers pays 16x.
