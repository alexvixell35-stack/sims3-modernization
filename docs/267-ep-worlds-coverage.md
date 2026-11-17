# 267 EP worlds and EP/SP content: what our Mono, routing and harness data never covered

Static check (2026-11-15), no game runs. Rodney: "there are places we didn't check for mono and routing work like china, universities, future. Maybe some of our mono functions we ignored are for those places." Widened the same day to every EP and SP, including EP/SP content that runs in home worlds.

Markers: **[verified]** = read in `game-copy/decompiled`, in our code, or counted in our data; **[inferred]** = reasoning, still to confirm (docs/51 §487).

Data: `docs/data/ep-pack-types.csv` (1,957 script types with a pack, one row each: interaction definitions, repeating alarms, per-tick work, listeners, route calls, situation/service/career base, Sim spawning, a hot score, and which of our data sets ever saw the type).

## 1. Summary

- **Our runtime data comes from three home worlds only [verified].** Every managed sampler file (382 `*-managed.jsonl`/`*.txt` in `probe-out/profile`), JIT census, compile-candidate list, AutShadow row and autonomy cold-path row comes from Bridgeport, Sunset Valley or Isla Paradiso. No sampler or profile file exists for Champs Les Sims, Shang Simla, Al Simhara, Sims University or Oasis Landing. The destination worlds only ever ran as save-matrix travel cases (correctness, no profile). China, Egypt and Oasis Landing last ran on 2026-10-28 (`2026-10-28-queue-release-validate.md`), University on 2026-11-02 (`2026-11-02-queue-vanilla-matrix.md`). Every routing and Mono change since then (v0.2.8 and v0.2.10, KdUnseenForeignCap, KdUncutCeilMs, JIT lazy bodies, AutShadow go-live) is untested there.
- **The JIT never compiles a destination-world method [verified].** `JitListed` (`native/ts3native/monojit.cpp:138`, gate at `:2557`) compiles only methods on the game list, and `tools/jit-hot.ts` builds that list from BP/SV/IP samples. A method that is hot only in a tomb or in Oasis Landing stays interpreted: no gain, but no JIT risk either. Of the 174 `jit-hot.csv` rows, 10 are pack types (e.g. `HoloDiscManager.Simulate`, EP11, seen in SV at 0.37 ms/s; it yields, so it stays interpreted), and 7 of the 270 compile-candidate rows are.
- **JIT code region caveat [inferred].** docs/537 §20 shows the 2 MB region is full in every game run (841-1,142 bodies), and refusals depend on the order in which EA transforms methods. A destination world transforms other methods first, so the same list may lose different hot bodies there. Check `jitCodeKB` / `jitMethods` / refusals in the first destination-world job.
- **Most pack code was never seen in our managed data [verified count, pack mapping inferred]:** 1,239 of 1,615 EP types (77%). That includes 271 of 286 World Adventures types, 149 of 164 Into the Future types and 121 of 142 University Life types. Our 1.25 M Sim-activity samples (`*-activity.jsonl`) show that several packs do run in home worlds even where the sampler never caught their types. Showtime has 24,201 "Performance", 13,923 "Attend Show" and 9,126 "Perform For Tips" samples; Late Night has 19,450 "Follow Celebrity" and ~15k dance samples. Those interactions are cheap enough not to show up in the sampler. Never seen in managed data therefore does not mean never run. The real blind spots are the content that never ran at all: tombs, visas, horses ridden by NPCs, festivals, campus life, plumbots, hover vehicles, time travel.
- **SPs have almost no script code of their own [verified].** There are 6 `ProductVersion.SPn` references in all three gameplay assemblies: 3 SP1 object declarations (`PhoneTableHETech`, `VideoGameSystemHETech`, `VideoGameSystemHETech2`) and 3 CAS install checks (`CASLogic.cs:9781,9797,9809`). SP objects reuse base or EP classes, so SP risk follows the class they reuse (arcade machines: `ArcadeMachineBase`, seen; movie set: `MovieSet`, seen).

## 2. Mono/JIT and autonomy coverage per pack

Method: each non-mimic type in `Sims3GameplayObjects`, `Sims3GameplaySystems` and `Sims3StoreObjects` is mapped to a pack. The mapping uses its own `ProductVersion` override (`declared`), else class-name keywords (`name`), else a type whose `ProductVersion.EPn` / `WorldName` / `IsFutureWorld` / `IsUniversityWorld` references are at least 70% one pack (`pv`). A spot check of 32 rows found 27 right (84%). Wrong ones were building shells as IP sea shells, `HouseParty` as EP9, and `IGraduationGnome` as EP7. Mapped types: 1,957 of 6,172 (the rest are base or mixed). "Seen" = named in a runtime data set: sampler stacks, compile-candidates, jit-hot, JIT exit census, JIT call edges, autonomy cold paths, autonomy entry list, sampled tier2 rows, results census files, or the 2 ScriptError XMLs. Static rows (route-plan callers, managed overrides, allow lists) do not count as seen. Hot score (0-5) = 1 each for: per-tick work or a repeating alarm; a situation, service, career or rabbit hole base, or Sim spawning; 3 or more interaction definitions; 3 or more route calls; 2 or more listeners. "Likely hot" = hot score 2 or more [inferred: a static proxy, not a measurement].

| Pack | Types | Interaction defs (seen / never) | Types seen | Never seen | Likely hot, never seen | Home-world activity evidence (samples) | Types named in ts3fixes | tier2 rows (sampled / not) |
|---|---|---|---|---|---|---|---|---|
| EP1 World Adventures | 286 | 12 / 290 | 15 | 271 | 14 | none of its own (only books) | 5 | 0 / 5 (2 blocked) |
| EP2 Ambitions | 163 | 35 / 158 | 18 | 145 | 18 | Investigate Strange Sim 333, Firewalk 132, ghost story 175 | 17 | 0 / 12 |
| EP3 Late Night | 125 | 164 / 47 | 32 | 93 | 4 | Follow Celebrity 19,450, dances ~15k, drinks ~6k | 10 | 5 / 3 |
| EP4 Generations | 87 | 76 / 62 | 17 | 70 | 7 | pillow fight 73, water balloon 80, Go to Prom 14 | 10 | 0 / 5 |
| EP5 Pets | 262 | 99 / 196 | 51 | 211 | 19 | Play Catch 1,082, Feed Treat 960, Route to Wild Horse 95, Mount 1 | 17 | 0 / 13 (1 blocked) |
| EP6 Showtime | 157 | 51 / 93 | 19 | 138 | 16 | Performance 24,201, Attend Show 13,923, Perform For Tips 9,126 | 6 | 0 / 5 |
| EP7 Supernatural | 160 | 152 / 117 | 25 | 135 | 14 | werewolf form 64, Fairy Frolic 42, zombie attack 17 | 11 | 3 / 4 (2 blocked) |
| EP8 Seasons | 130 | 81 / 100 | 25 | 105 | 11 | Use Umbrella 131, Skate 95, Make Snowman 61; no festival rows | 10 | 0 / 7 (1 blocked) |
| EP9 University Life | 142 | 57 / 97 | 21 | 121 | 16 | Protest 453; class rows 1 each (travel runs) | 14 | 2 / 3 |
| EP10 Island Paradise | 202 | 110 / 120 | 36 | 166 | 6 | swim ~5k, dive 316, Board Boat 55 (covered world) | 18 | 0 / 17 |
| EP11 Into the Future | 164 | 47 / 164 | 15 | 149 | 16 | Teleport Me Here 47; no hover, jetpack or plumbot rows | 9 | 2 / 3 |
| SP1 High-End Loft | 3 | 0 / 0 | 0 | 3 | 0 | - | 0 | - |
| SP2 Fast Lane, SP3 Outdoor Living, SP4 Town Life, SP5 Master Suite, SP7 Diesel | 0 | - | - | - | - | - | - | - |
| SP6 Katy Perry | 16 | 0 / 5 | 0 | 16 | 0 | - | 0 | - |
| SP8 70s 80s 90s | 21 | 6 / 10 | 1 | 20 | 1 | arcade machines 15 | 1 | - |
| SP9 Movie | 12 | 9 / 1 | 2 | 10 | 0 | Visit Blue Sky Movie Theater 29 | 1 | 1 / 0 |

Likely hot and never seen (hot score, top per pack; full list in the CSV):

| Pack | Types | Where it runs |
|---|---|---|
| EP1 | `Sarcophagus` (17 defs, spawns the mummy), `TravelUtil` (23 route calls), `TombTrap` (16 defs), `NectarMaker`, `SnakeCharmingBasket`, `LogicGate` (repeating alarm), `MartialArts`, `PushableStatue`, `TreasureSpawnPoint`, `Relic`, `BoardBreaker` | tombs and vacation worlds; Nectar, martial arts, snake charming and photography also in home worlds |
| EP2 | `Stylist` and `ActiveFireFighter` careers, `FirefighterEmergencySituation` (13 route calls), `SculptingStation`, `InventionWorkbench`, `TimeMachine`, `DrillHole`, `PrivateEyeInteractions` (18 route calls), `ServoBotPedestal` | home worlds |
| EP3 | `DJTurntable` (14 defs, 23 route calls), `BarBrawlSituation` (16 route calls), `PoolBar`, `BarTray` | home worlds (BP) |
| EP4 | `BabySwing`, `Treehouse` (18 defs), `Clothesline`, `DaycareToddlerTransportSituation`, `DaycareChildManager`, `SocialWorkerAdoptionSituation` | home worlds |
| EP5 | `BoxStall` (24 defs, 43 route calls), `HorseJump` (17 route calls), `HitchingPost`, `EquestrianCenter`, `WaterTrough`, `PetFeedingStation`, `MinorPetTerrarium`, `PetBowl`, `PetBed`, `PetHouse` | home worlds; horses route a lot |
| EP6 | `SingerCareer`, `MagicianCareer` (15 route calls), `PerformanceArtistCareer`, `PerformerSituation`, `OccultGenie`, `GenieLamp`, `KaraokeMachine`, `StageLightObject` and `FXTower` (repeating alarms) | home worlds (BP venues) |
| EP7 | `BonehildaCoffin` (alarm, spawns Bonehilda), `OccultFrankenstein` (SimBot), `VoodooDoll`, `MagicCauldronRegency`, `AlchemyPotion` (12 route calls), `FortuneCookieMaker`, `CrystalBall`, `GypsyCaravan`, `WhackASupernatural` | home worlds |
| EP8 | `WeatherStone` (per tick), `Snowman`, `SkatingRink` (22 defs, 19 route calls), `CarvedPumpkin`, `ConcessionsStand`, `EatingContestStand`, `HauntedHouse`, `AppleBobbingContestTank`, `GiftPile` | home worlds (festival lot, weather) |
| EP9 | `CollegeGraduation` (per tick), `AcademicCourse`, `Whiteboard`, `GraffitiSurface`, `JuiceKeg` / `JuiceKeggerParty`, `TutorObject`, `OrientationSituation`, `MascotIntroSituation`, `UniversityMascotSituation`, `ForeignVisitorsSituation` | University world; keg, graffiti and whiteboard also at home |
| EP10 | `Shark` (14 route calls, per tick), `ResortFrontDesk` (35 defs), `ResortTower`, `ResortFoodStand`, `NPCMermaidManager` | IP (covered world, but these paths not seen) |
| EP11 | `TimePortal` (16 defs), `BotMakingStation`, `Teleporter`, `HoverTrainStation`, `MeteorShower` (repeating alarm, spawns), `FutureFoodSynthesizer` (20 defs), `SleepPodFuture`, `StellarObservatory`, `BotChargingStation`, `FutureTrashPile` | Oasis Landing; portal, synthesizer, bot station and teleporter also at home |

Seen pack code that matters [verified]: `Bartending.TryGetHoursOfOperationExtended` (EP3) at 2.6-3.8 ms/s in all three worlds (docs/74 venue path), `OccultManager.GetSimOccultInfo` / `HasOccultType` (EP7, 0.9-2.1 ms/s), `Cane.GetActiveCaneForActor` (EP4, 2.6 ms/s, docs/74), `HoloDiscManager.Simulate` (EP11, SV 1.1 ms/s). All three home worlds already exercise these.

Destination-world hot paths that our data never saw, by world [inferred from the code, unmeasured]:
- **WA worlds:** tomb rooms (`TombRoomManager`, `TombTrap`, `PushableStatue`, `LogicGate` alarms), `Sarcophagus` mummy spawns, `TreasureSpawnPoint` / `Relic` spawners, `TravelUtil` vacation timers and visa checks, local and tourist NPC situations.
- **University:** `AcademicCourse` and class rabbit holes, `CollegeGraduation` per tick, `OrientationSituation` / `MascotIntroSituation` / `ForeignVisitorsSituation`, `JuiceKeggerParty`, social-group influence, a campus crowd of student NPCs (docs/19 §2 #1: Student Union and Library door pile-ups).
- **Oasis Landing:** `HoverTrainStation` plus `SimRoutingComponent.CheckAndUpdateRouteForStations` after every cross-lot plan (docs/10 V2: up to 3 extra plans), plumbot NPCs (`IsEP11Bot`), hover cars (`HoverCarExpensive` and the like), `FutureFoodSynthesizer`, `MeteorShower`, `BuffWonderland`.

## 3. Routing features vs pack-specific routing paths

No routing native has a world name, world id, lot id or fixed lot address (plandeadline, a10, cellmemo, slotro, avoidro, gvro, footprint, routegate) [verified grep; the only world name in native code is the Bridgeport string in `memdiag-test.h:67`]. Per-world state is live routing state or per-lot and per-plan data (CellMemo lives for one plan; the footprint index is per lot and rebuilt each save, `footprint.cpp:16`).

| EA path (pack) | How EA moves the Sim | Our features | Verdict |
|---|---|---|---|
| Tomb traps, pushable statues, dig sites (EP1) | route to the object, then `SetPosition` (`PushableStatue.cs:479,485`) | unseen paths refuse a start in a tomb (`TownieUnseen`, `mods/ts3fixes/src/Fixes.cs:1139`; `UnseenFallbackFinish.cs:110`); travellers are household (`PlayerSims.cs:49-50`) | stand down [verified] |
| A route **goal** in a tomb or hidden room (EP1, EP3) | - | `UfTeleport` re-check (`UnseenFallback.cs:1320-1325`) tests why, `TownieUnseen`, goal unseen and routable only; no tomb, hidden-room, indoor or lot check (the pre-plan path has them, `:1020-1021`) | **gap**, untested (§6 F1) [verified code; reachability inferred] |
| Tomb boulders, hidden doors (EP1) | `PortalComponent` (`GialtBoulderPathBlocking.cs:11`) | door answers come per plan from the game's own query (`native/ts3native/p4.cpp:418`), nothing reused across plans | handled [inferred] |
| Hoverboard, jetpack (EP11) | posture + walk style, normal route (`Hoverboard.cs:79`, `Jetpack.cs:1251`) | `TownieUnseen` needs Standing (`Fixes.cs:1138`), `UfLeave` too (`UnseenFallback.cs:1272`) | stand down [verified] |
| Bikes, scooters (EP9), brooms (EP7) | `Vehicle` subclasses (`Bicycle.cs:17`, `MagicBroom.cs:19`) | `UfHeld` = held when `Parent is Vehicle` (`UnseenFallback.cs:292`); native deadlines can still cut an NPC-only vehicle (`UfVehicleNpcOnly` `:308-316`, `:869`) | unseen work stands down; native cuts **untested** [verified] |
| Hover cars, hover train and station (EP11), subway (EP3) | `HoverTrain : Car`; station has a `PortalComponent` (`HoverTrainStation.cs:27`) | fast legs refused (`UfFastLegs` / `UfCarOrBoat`, `UnseenFallback.cs:1165-1172`); ownable or household-occupied vehicles = household (`:87-91`) | stand down [verified]; `CheckAndUpdateRouteForStations` cost unmeasured (docs/10 V2) |
| Tube elevator (EP11), elevators (EP3) | `PortalComponent` (`TubeElevator.cs:24`) | `UfOutdoorGround` needs room 0 and ground level (`:447-450`), but `UfTeleport` does not call it | partly; teleport untested [verified] |
| Time portal, teleporter (EP11) | EA's own `SetPosition` / `TeleportTimeTravelerToSim` (`TimePortal.cs:1058`, `Teleporter.cs:706`) | no route of ours | stand down [inferred] |
| Horse riding, jumps, box stall (EP5) | `RidingPosture` / `BeingRiddenPosture` | riding = held (`:292`), pets skip unseen work (`:875`), household horses exempt; wild-horse routes can still get native cuts | stand down; wild horses **untested** [verified] |
| Mermaids, sharks (EP10) | swimming posture; `Shark` is `MarineLifeBase`, not a Sim | swimming refused (`UnseenBoat.cs:144`); no driver and no vehicle = household (`UnseenFallback.cs:87`) | stand down [verified] |
| Boats, houseboats (EP10) | boat routes | `UnseenBoat.cs:154-162`, `HouseboatOk` (`UnseenFallback.cs:382,429`) | handled [verified]; moving houseboat vs AvoidRO untested |
| Vacation tourists and locals (EP1), future descendants (EP11) | own households (`Household.cs:804`, `FutureDescendantService.cs:72-78`) | NPCs, so unseen work and KD cuts act on them; travel itself = not live (`UnseenFallback.cs:130`) | follows the rule; **untested** in those worlds [verified code] |
| RouteBackoff, stuck check async | - | `BackoffEligible` uses `TownieUnseen` (`RouteBackoff.cs:509`); stuck check skips while travelling (`FewerPlans.cs:82,240`) | handled [verified] |
| KindDeadlines, KdUnseenForeignCap, A10, JobYield | scope from ts3fixes (`KdScopeOp`, `UnseenFallbackCore.cs:34`); unknown scope = household (`plandeadline.cpp:110-114`) | nothing world-specific | handled [verified] |
| `KdUncutCeilMs` 30 s (all scopes, docs/264) | `plandeadline.cpp:218-228` | a long hover-train, tube or tomb plan over 30 s fails as an ordinary route failure, household too (by design) | **untested** in Oasis Landing and tombs [verified code] |
| GoalValidRO, SlotRO, AvoidRO, CellMemo, footprint index | read live state | no per-world tables | handled [inferred] |
| AutOffload | tomb, elevator and vehicle checks (`AutOffload.cs:700-702,1101,1111`) | test-only, off (`Fixes.cs:523-527`) | stand down [verified] |

Household rule in those worlds [verified, decompiled]: vacation travellers are the selected Sim's household (`h == act`, `PlayerSims.cs:49-50`). University roommates and dorm mates go through `AddRoommate`, which adds them to the active household with `IsNeverSelectable` (`RoommateManager.cs:692-697,792-800`), so they are exempt. That is stricter than needed, and correct. Household horses and plumbots are household members. Plumbot NPCs pass `IsHuman` (`Sim.cs:47012`) [species value not checked]. Future descendants and WA tourists are NPCs.

## 4. Harness reach [verified unless marked]

- **Travel exists for all five destinations.** `travel_vacation` (france/china/egypt, `mods/ts3agent/src/plugin/Handlers/TravelHandlers.cs:104-106`; it targets the world lot, so it skips the walk and the visa/duration planner, `:112-152`), `travel_university` (`:200-338`, fast enrol or `dialog:true`, `direct` skips the van), `uni_move_in` (`:591`), `travel_future` (`:471`; EP11, a placed time portal, `prepare:true` runs `portal_prepare`, `:380-418`) and `travel_home` (`:637`, per world type). All are live-mode only (`GateHandlers.cs:50-52`).
- **Harness wiring.** `save-matrix.ts` has the opt-in `travel` case (`--to`, `--direct`, `--stay`; `tools/harness/save-matrix.ts:904-955`). `profile-world.ts --travel WORLD` runs it with `--stay`, then profiles in the destination (`profile-world.ts:139-143`). `queue.ts` accepts `run.profile.travel` = university/future/france/china/egypt (`queue.ts:439`), but `profile.repros` refuses travel (`:498`), so one repro per travel job. `travel-trip.ts` does N round trips from a home world (`:157,163-176`).
- **Never used for a profile.** No `probe-out/profile` file comes from a destination world. Every travel run starts from Baseline-BP; no travel jobs from SV or IP exist.
- **Gaps.** No save that starts in a destination world. Only `rare-world-walk` (`tools/harness/repros.json:7874`, "UNVERIFIED, never run") targets another world. `test_env` has no seasons while away (`HarnessHandlers.cs:773-776`). There is no horse spawn (`pet_adopt` only does cat, dog or any; `unseen_horse_ride` is the only horse step, `RareHandlers.cs:31`), and no generic command to force an EP service NPC. Destination `.world` files are not in `game-copy/worlds` (only BP, IP, SV); they load from the installed EPs [inferred].
- **Home worlds can force most pack content:** `setup_sims` / `place_sim` (teleport, motives, cancel queues; `ReproHandlers.cs:22-23`), `buy_object` / `place_object` (`ControlHandlers.cs:46,50`), `do_interaction` by name (`ControlHandlers.cs:38`), `social` (`StoryHandlers.cs:42`), `make_occult` (`StoryHandlers.cs:35`), `pet_adopt` (`PetHandlers.cs:23`), `test_env {season, weather}` (`HarnessHandlers.cs:782-792`), `spawn_sims`, `spawn_traffic`, `sim_portal_trip` (IP boats).

## 5. Run plan (smallest first)

Rules: repros teleport and force (docs/TESTING.md:33); diag on; ON bundle = release ini; correctness before timing; the queue.ts smoke runs first. Times are wall clock at the measured pace (a live job is ~8-15 min).

**Step 1: destination-world correctness, 5 jobs, ~1 h 15 min total.** One `profile.travel` job per world from Baseline-BP with the current release bundle: travel with `--stay`, read switches back (`patches`), 10 min at speed 3 with the world's repro (below), `travel_home`, save. Pass = no crash, 0 script errors (ErrorTrap), switch values read back, household audit unchanged (household Sims never cut, skipped or teleported: `kdScope` household/selected counts 0 in the cut lines), plus `jitCodeKB` / `jitMethods` / `refused` logged.
- France / China / Egypt: walk the household into the nearest tomb lot, `do_interaction` on a `TombTrap`, a `PushableStatue` and a `Sarcophagus`; 6 NPCs `setup_sims` onto the market lot.
- University: `travel_university direct`, `uni_move_in`, `do_interaction` attend class; 10 student NPCs onto the Student Union lot (door pile-up path).
- Oasis Landing: `travel_future prepare:true`, then 6 NPCs and the household routed across lots (hover-train station path), `do_interaction` on a `FutureFoodSynthesizer`.

**Step 2: one profile per destination world, 5 jobs x 2 arms x 10 min, ~2 h 30 min (a night).** Bundle ON vs OFF, per-arm ms/s split, light tools only. Watch `KdUnseenForeignCap` / `KdUncutCeilMs` cuts (scope tags) and AutShadow `row` aborts on rows no home world has.

**Step 3: home-world pack repros, 3 correctness jobs (BP, SV, IP), ~40 min, then fold the passing ones into the next bundle timing run.** Each step forces the hot never-seen content for 2 NPCs and 1 household Sim (household must stay vanilla):
- BP: `DJTurntable` and a bar brawl (EP3), `KaraokeMachine` + `do_interaction` on a stage (EP6), `SculptingStation` / `InventionWorkbench` (EP2), `make_occult genie` + `GenieLamp` (EP6), `AlchemyPotion` + `MagicCauldronRegency` (EP7).
- SV: `test_env {season: winter, weather: snow}` with `Snowman`, `SkatingRink`, `WeatherStone` (EP8); `Treehouse`, `Clothesline`, `BabySwing` (EP4); `NectarMaker`, `BoardBreaker`, `SnakeCharmingBasket` (EP1 home-world objects); `BonehildaCoffin` (EP7).
- IP or a home world with a stable: `BoxStall`, `HorseJump`, `HitchingPost` (EP5). This needs a horse spawn step first (missing, §4). Also `TimePortal`, `BotMakingStation`, `Teleporter`, `FutureFoodSynthesizer` (EP11 home-world objects), `JuiceKeg`, `GraffitiSurface`, `Whiteboard` (EP9 home-world objects), `Shark` near a dive spot (EP10).

Sizes: step 1 ≈ 5 live jobs, step 2 ≈ 10 arms (one night), step 3 ≈ 3 live jobs. Harness work before step 3: a `spawn_horse` step (~30 min agent build). Before step 1: nothing (travel exists), but add the per-world repro steps to `repros.json` (~30 min).

**Built 2026-11-15 (lane epruns, branch `feature/ep-world-runs-1115`, NOT RUN):** repros `ep-wa-tomb` / `ep-uni-campus` / `ep-future-oasis` (step 1 and 2 worlds) and `ep-home-bp` / `ep-home-sv` / `ep-home-ip` (step 3) in `tools/harness/repros.json`; queues `ep-worlds-correctness-1115.json`, `ep-worlds-profile-1115.json` (per world an OFF arm with ourMods false and an ON arm with the release bundle; natural play, since ourMods false jobs refuse a repro), `ep-home-packs-1115.json`. Agent targets for them: `@type:<class>`, `@picked:N`, `@far`, `def:<class part>` interaction match, `buy_object {type}`, `spawn_horse` (F3). Not forced: festivals, orientation / mascot / foreign-visitor situations, graduation, sharks (natural events or no spawn step).

## 6. Static fix candidates

- **F1 `UfTeleport` goal check (on by default, `unseenfbteleport`).** A cut own `Route.Plan` (`UnseenFallback.cs:921-926`) places the unseen NPC at the plan's goal. Its last re-check (`:1324`) has no tomb, hidden-room, indoor or lot test, so the NPC can land in a WA tomb room, a Late Night hidden room, a locked room or an upper floor that the cut plan never proved reachable [verified code; reachability inferred, I-487-4]. Fix: refuse when `TombRoomManager.IsPointInATombOrHiddenRoom(dest)` (`TombRoomManager.cs:1133`) or when the goal is neither `UfOutdoorGround(dest)` nor an open lot. This mirrors the pre-plan goal checks (`:1020-1021`). Size: ~10 lines + a counter slot + a repro (hidden room in BP, tomb in a WA world); ~30 min agent build.
- **F2 `TownieUnseen` ignores hidden rooms.** `IsObjectInATombRoom` (`Fixes.cs:1139`) leaves out Late Night hidden rooms (`TombRoomManager.cs:1078-1108`, `allowHidden: false`). Switching to `IsObjectInATombOrHiddenRoom` touches every `TownieUnseen` caller (backoff, unseen fallback, sites), so grep them first and give it its own switch. `hiddenroomnpc` is off (`Instantiator.cs:239`), so only the stuck watchdog rescues a trapped NPC, about an hour later (docs/552) [verified].
- **F3 harness: `spawn_horse`.** No command creates a horse (`pet_adopt` = cat/dog/any), so no repro can force `BoxStall` / `HorseJump` / riding routes (EP5) [verified].
- No JIT fix: the game list cannot gain destination-world methods without samples there (step 2 gives them).
