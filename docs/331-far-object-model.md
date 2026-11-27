# 331. Far-object model: computed state for every non-Sim system far from the household (2026-11-23)

Status: **design + static analysis only.** Nothing built, nothing queued. Rodney's request (2026-11-22): "Can we also make
this into a deterministic state machine similar to our plan", then "Plan this too". **Revised 2026-11-23 after his
decision on the first version: compute everything.** In his words: "we still compute those with timers instead of
letting them go off on their own. For everything that doesn't compute, we compute them because they will eventually add
up on unbounded worlds." So a parked lot holds **all** of its timers, no timer wakes a lot, and every class that has no
closed form gets a computed model with a seeded event log (§3, §4). Exceptions are only where computing is impossible
or unsafe (§5).

Companion of the far-Sim model (docs/324, branch `feature/far-sim-model-1122`), whose structure, tiers, one-owner rule,
sidecar and phase format this doc follows. Branch `feature/far-object-model-1123`. Ledger §585 (docs/51).

Inputs: docs/329 (non-Sim split, `[Diag] NonSimSplit`, branch `feature/nonsim-split-1122`; its queue ran 2026-11-22,
5/5 PASS; docs/329 §3 now holds the measured table), docs/160 (sim budget m1122), docs/326 (prior art, ideas #1-#12,
pitfalls P1-P10; branch `feature/unbounded-sim-research-1122`), docs/330 (our own loop costs; branch
`feature/ts3fixes-loop-cost-1122`), docs/320 (large worlds, §2 cost table, §7 region streaming), docs/321 (unbounded
sweep, lane A lifecycle hub), docs/243 (unseen-lots study: option D2 "lots suspended, fast-forward on seen"),
docs/328 (compat rules, runtime precedence; branch `feature/compat-resolve-1122`). EA script DLLs decompiled read-only
in `game-copy/decompiled/` (nothing quoted here). Tags: **[verified]** read in the decompile, our source or the run
data named; **[measured]** a run; **[inferred]** reasoning, still to confirm (docs/51 §585).

## 0. Short answer

- **Measured (nonsim-split-1122, 10 min seeded, 2026-11-22):** far (>= 150 m), non-household per-lot and per-object
  work is **27.7 / 33.9 / 11.8 ms/s (BP / IP / SV)** of class b, plus 8.9 / 10.0 / 10.5 ms/s of far lot alarm
  callbacks, plus **~39-40 ms/s per world** of world-lot alarms, yielding-alarm tasks and one-shot functions without a
  position, plus ambient traffic (BP 10.7) [measured]. `SimUpdate` (97-109 ms/s) is the far-Sim model's (docs/324).
- **The unit is the lot.** EA puts an object's alarms on its lot's `AlarmManager`; only Sims and lot-less objects use
  `AlarmManager.Global` [verified, `GameObject.AddAlarm`]. A **parked lot** runs none of its timers, no reaction
  pulses, no door privacy, no zombie pass and no own-thread object loops. Its state is a pure function of
  `(seed, version, park start, now, recorded world history)` and is evaluated only when read or at promotion.
- **Every timer is computed, none fires on its own.** Each held timer has a model (§4): closed form, seeded schedule,
  or (for callbacks without a dedicated model yet) a recorded fire list replayed through EA's own callback at
  promotion. Outcomes that need a real object or Sim (spawn, burn, destroy, create, Sim arrival) go into a
  **deterministic event log** and are materialised at promotion through EA's own code, in time order. Sim outcomes
  hand off to docs/324 (T3 / T4).
- **Exceptions (§5), each with a reason and a later path:** household-facing timers pass through at their real time
  (household rule), fire within reach of a household lot, Sim or object stays EA's, unknown scope parks nothing,
  own-thread classes from mods without a pause hook stay live, and the world-global Seasons loop keeps running (it is
  one loop for the world, not lot work; its lot effects are computed).
- **Household untouched**, unchanged from the first version: household lots, lots with a household Sim or its route
  target, the active and visited lot, objects a household Sim owns, uses or reserves never park; a stale household
  table parks nothing. Default off. Nothing new in the .sims3: before every save all lots materialise their logs and
  unpark, so EA writes EA state.
- **Gain [inferred]:** ceiling ~54-77 / 57-76 / 35-55 ms/s, realised **~35-65 / 35-65 / 20-45 ms/s** (BP / IP / SV;
  3.5-6.5% of the sim thread). On a 4x or streamed world all object work is near-only (§9).
- **Phases P0-P8: ~24-34 h agent time, ~50 game jobs** (§11).

## 1. Goal, rules, tiers

### 1.1 Goal

Cut the sim-thread cost of every lot and object the player cannot see and no household Sim touches, so that non-Sim
cost follows the lots near the viewers, not the lot or object count of the world (AGENTS.md "Build for unbounded
worlds"). The player's lots and Sims see no difference.

### 1.2 Hard rules (each one a code-level check)

1. **Household untouched.** Never parked (tier H, §1.3): the active household's home lot; any lot holding a household
   Sim, the selected Sim, or a Sim of unknown scope; the destination lot of any of their routes, travel, rabbit-hole
   or career alarm; the active lot (`LotManager.ActiveLot`), the selected Sim's `LotCurrent`; any object a household Sim
   owns (household inventory, `CarOwnable` of the household, owned-by component), is using, has reserved or is routing
   to. No fresh household table = no lot parks (unknown scope counts as household). Use the shared predicates
   (`Fixes.HouseholdExempt`, `mods/ts3fixes/src/PlayerSims.cs`; native `HhExempt`). `[Experimental] HouseholdToo=1`
   does **not** apply: parking is a skip, the event log a fake (docs/271).
2. **Compute, never fire.** A parked lot's timers never run on their own and never wake the lot. Promotion comes only
   from viewers, household triggers, Sim arrivals, on-demand reads, save, travel and switch-off (§6.2). The only timers
   that run at their real time are household-facing ones (exception E1, §5), and they run alone through our dispatch,
   without unparking the lot.
3. **One owner per lot** (docs/326 #2): EA or the model. A lot parks only when every Sim on it, or routing to it, is
   model-owned (docs/324 T3 / T4) or there is none.
4. **Event log, materialised exactly.** Outcomes needing a real object or Sim are pending events with a computed time,
   kind, subject and seed; at promotion they run through EA's own code in time order (§6.3). Sim outcomes are handed to
   docs/324, which owns every Sim.
5. **Default off.** ts3fixes `farobj` (master) + `farobjlot` (lot parking) + `farobjworld` (world-lot objects) +
   `farobjfire` (fire model) + `farobjtraffic` (traffic model). Diagnostics separate and off (`[Diag] FarObjProbe`,
   §8).
6. **Sidecar only, deltas only.** Nothing new in the .sims3. Before every save the model materialises every log and
   unparks every lot (§6.4). `<Save>.farobj.json` holds the generator version and, per parked lot, the park start and
   the weather-history range it read, so a crash between park and save can be repaired (rule 8). Missing or mismatched
   sidecar = ignored and logged.
7. **Never cull.** The model deletes nothing on its own. A destroy happens only as a materialised EA event (a burn, a
   spawner's delete-untaken, a harvestable's fade), through EA's code.
8. **Clean up on load.** Parking writes nothing into EA state (timers stay in EA's queue, objects untouched until
   materialisation). A load with the switch off, or after a crash, finds overdue timers that EA fires once on its first
   pass: vanilla lag semantics. With the switch on, the sidecar lets the model compute and materialise those lots on
   the first pass instead.

### 1.3 Tier ladder (lots)

Viewer points as docs/324 §1.3: camera target, camera position, every household Sim. Lot distance = distance from the
nearest viewer point to the lot's centre minus its half diagonal (docs/329 lot table, `Lot.GetCenterPosition`,
`GetClosestCornerToPoint` [verified there]). Radii in metres from the ini, never fractions of the world.

| Tier | Which lots | Enter | Leave | What runs |
|---|---|---|---|---|
| **H** | rule 1 | always | never | full EA |
| **L1 Live** | lot distance < 150 m; in the camera frustum; holds an EA-owned Sim or one routes to it; a fire within reach of a household lot, Sim or object (E2); in a situation or party with a household Sim; observed (map selection, Edit Town, lot info) | at once | to L2 when no trigger holds | full EA |
| **L2 Ready** | 150-200 m, or past 200 m for less than `ParkDwellMin` (30 sim-min) | from L1 | to L3 at a periodic pass (every 5 s real) past 200 m after the dwell | full EA (hysteresis band; materialisation finishes here on the way back) |
| **L3 Parked** | past 200 m, dwell met, eligible (§6.1) | from L2 | promotion (§6.2) | nothing of the lot's own: state computed, events logged |

200 m is Rodney-approved as a recommendation (decision 1, §12) and stays unless it conflicts with docs/324: a lot with
an EA-owned Sim (T1 / T2, < 450 m) never parks anyway (rule 3), so an NPC-occupied lot parks only when its Sims reach
T3.

**Lot level and object level.** Lot-level parking is the default: EA's driver is the lot (§3.1) and one check per lot
per pass covers its objects. Object-level holds cover the rest: lot-less objects on the world lot (`AlarmManager.Global`
by `Timer.ObjectRef`) and objects with their own script thread (§4.9), both keyed by the object's position band.

## 2. Inventory: what scales with world size or object count

### 2.1 Measured split (nonsim-split-1122, 2026-11-22, all jobs PASS) [measured]

`node tools/harness/nonsim-split-report.ts` in the `nonsim-split-1122` worktree; the same table is now in docs/329 §3.
ms per wall second over a 698-699 s dump, seed 12345, `-disablestoryprogression`, heavy probes on (self cost ~0.3 us
per run). "far" = bands >= 150 m, household rows apart; "-" = no position (global row).

| Owner (docs/329 class) | BP total / far | IP total / far | SV total / far | Model here |
|---|---:|---:|---:|---|
| `SimUpdate` (b, Sim-driven) | 101.4 / 28.0 | 97.4 / 26.5 | 108.8 / 34.6 | docs/324 (far part); household 60-67 stays |
| `StateMachine` tasks (c, global) | 34.4 / - | 35.4 / - | 34.3 / - | owners unknown; P0 places them, then §4 by owner |
| `Lot`: reaction broadcaster pulses (b) | 26.1 / 19.4 | 22.4 / 18.7 | 7.6 / 5.9 | §4.1 |
| `WorldLot` (b, global: world alarm manager + world-lot pulses) | 20.0 / - | 27.5 / - | 24.1 / - | §4.3 (objects); Sims' alarms docs/324 |
| `Lot`: zombies, door privacy, rest (b) | 8.9 / 6.7 | 15.7 / 13.1 | 6.1 / 4.7 | §4.10 |
| `Lot`: alarms, other callbacks (c) | 11.9 / 8.9 | 12.0 / 10.0 | 13.7 / 10.5 | §4.2 |
| `Lot`: detail priority (c) | 9.7 / 7.2 | 11.2 / 9.3 | 6.1 / 4.7 | kept (engine LOD request, render-facing) |
| `Lot`: alarms, object state (b) | < 3 / - | 2.2 / 1.9 | < 3 / - | §4.4, §4.5 |
| `AlarmOneShot` (b, global: yielding alarms) | 7.8 / - | 9.7 / - | 12.5 / - | §4.3 |
| `OneShotFunction` (b, global: own-thread objects and functions) | 10.9 / - | 2.2 / - | 3.1 / - | §4.9 |
| `Services` (b, global) | 10.8 | 13.7 | 10.7 | service Sims: docs/324 |
| `UpdateTrafficTask` (b, global) | 10.7 | (< 2) | (< 2) | §4.6 |
| `SeasonsManager` (b, global) | 5.8 | 9.4 | 7.9 | world loop stays (E5); lot effects §4.7 |
| our loops: `DeadRefSweepRun`, viewer push, `StuckWatchdog`, `OdSweep` (c) | 36.2 | 35.9 | 41.4 | docs/330 |
| `SimBuilderTask` (c) | 6.7 | 16.7 | 15.0 | Sim outfits: docs/324 |
| engine (d) | 304.7 | 288.1 | 300.3 | no |

### 2.2 Unmeasured

Not in the 10-min window or without a position: daily plant bursts (~05:00), spawner rolls, wildflower lifecycles,
Seasons-era dormancy, story progression's object side (off in the queue), pets' objects, traffic by band,
`StateMachine` owners. Daily alarm bursts grow with the lot count: docs/320 §2 row 7 measured a 956 ms world lot alarm
pass [measured there]. P0 (§8) measures all of them.

## 3. How EA advances non-Sim state [verified]

- `GameObject.AddAlarm*` puts an alarm on `mLotCurrent.AlarmManager`; only a Sim or an object with no lot uses
  `AlarmManager.Global` (the world lot's manager).
- `Lot.Simulate` is a threaded script loop per lot: its `AlarmManager.Simulate()`, `UpdateDoorPrivacy()`,
  `UpdateDetailPriority()`, then `UpdateReactions` (+ `UpdateNumZombiesOnLot` with EP7) when `ShouldSimulate` (active
  lot, world lot, active Sim's lot or any commercial lot) or when `mBroadcastersWithSims` is not empty; otherwise it
  sleeps `kInactiveLotSleepTicks` (30). An inactive far lot still runs its alarm pass every ~30 ticks.
- `AlarmManager.Simulate` pops every due timer in time order. A late repeating timer is rescheduled past "now" and
  **fires once**; missed periods are skipped. A timer whose callback target has no proxy is dropped. A yielding timer
  becomes an `AlarmOneShot` task (`InvokeYieldingAlarm` -> `Simulator.AddObject`); a throwing repeating timer is
  removed for the session (`AlarmKillLog.cs` covers this).
- Each `Timer` carries `CallBack`, `ObjectRef` (`IAlarmOwner`), due time, repeat length and unit, yield flag.
- `ReactionBroadcaster` registers on its owner's lot (lot-less ones on the world lot); a pulse at most about once per
  sim-minute does one `Queries.GetObjects<Sim>(pos, PulseRadius)` and processes up to `MaxSimsToProcessPerTick` Sims.
- `UpdateNumZombiesOnLot` counts zombies on the lot every `kSearchForZombieInterval`; at `kNumZombiesToDance` with no
  stereo and the lot's `ZombieDanceTimeOut` at 0 it adds a zombie dance stereo; it decrements the lot's timeout by
  elapsed time.

## 4. Computed models, every class

Notation: **seed** `S(k) = H_v(lotId, objectId, timerKey, k, worldId)` (fixed 64-bit mix, generator version v, k = the
period or roll index; docs/324 §2.1 style). Same save + same version = same log on every machine and reload. EA's own
rolls use the shared game RNG, so no computed run matches an unparked run draw for draw; vanilla is not repeatable
draw for draw either. Models are judged by distributions in P5 shadow calibration (§11). **World history**: a ring
buffer, recorded while the game runs, of `(time, weather kind, intensity, temperature, FrostLevel, snow, wet,
season)` at every weather change (from `SeasonsManager`), so lot models read the real weather, not a guess.

### 4.0 Timer table: every held timer and how it is computed

At park the lot's timers are read from EA's queue (read only) into the lot's census. Each is matched by callback
declaring type + method to a model:

| Timer class (callback) | Model | Computed result | Pending events |
|---|---|---|---|
| `Plant` daily `EvaluatePlantGrowth` | seeded schedule (§4.5) | growth stage, progress, water, weeds, fertilizer days, dormancy | death (`SetDead`), harvest-state entry, state-change events |
| `HarvestPlant` fade / delete, `PostGrowthEvaluation` | event log (§4.4) | harvestables alive at now | create / delete harvestables (compacted) |
| `ObjectSpawner` spawn / delete-untaken | seeded schedule (§4.4) | items alive at now, capped by `MaxSpawnCapacity` | spawn (spot by EA at materialisation), delete oldest |
| `Wildflower` lifespan | closed form | alive iff now < spawn + `kBaseLifespan` + jitter(seed); winter kills | death |
| `PreparedFood` `mSpoilageAlarm` | closed form | stage n at n x `kBaseTimeToNextFoodSpoilageStage` x fridge multiplier | one `ForceSpoil` per missed stage (EA broadcaster, flies, fridge callback) |
| `ServingContainer*` `mDirtyAlarm` | closed form | dirty iff now > eaten + `kMinutesUntilDirty` | `AddDirtyCallback` once |
| `TrashPile` old / ancient, `LitterPile` stink | closed form | age class | each missed one-shot once, in order |
| `FishingSpot` active / deplete / inactive | closed form | active iff hour in [`HourActive`, `HourInactive`) | none (VFX at materialisation) |
| `FishTank` dirt, filthy kill, hunger kill, per-fish unfed | seeded schedule (§4.5) | dirt level, hungry flags | fish deaths in order |
| `PuddleManager` evaporate / spread | closed form (static) + seeded spread (§4.7) | puddle tiles alive at now | spread tiles, evaporations |
| `Fireplace` catch-fire check, every `Fire` alarm | fire model (§4.8) | fire start / spread / out timeline | burn states, destroys, fire end |
| neighbour chores (`CheckMail`, `EmptyTrash`, `GardenOutside`), pet, meta-autonomy alarms on the lot | Sim hand-off (§4.2) | none on the lot | Sim activity proposals to docs/324 |
| opportunity, phone, invitation, bill / repo alarms of the household | **E1 pass-through** (§5) | - | - |
| our own ts3fixes alarms on a lot | per alarm: ours declares a model (most are sweeps: run once at promotion) | - | - |
| any other callback (packs, mods) | generic recorded fires (§4.2) | fire times list (repeat: every period, not once) | replay through EA's callback at promotion, k times in order, each with its recorded due time |

The generic row is the fallback that keeps rule 2 for callbacks without a dedicated model: the timer still never fires
on its own; its fires are computed (times from the timer's own repeat data) and replayed at promotion. P0's census
lists every callback in the shipped packs and the top-100 mods, and each gets a dedicated model over time.

### 4.1 Lot reaction pulses, commercial lots included

A pulse finds Sims in radius and gives them a reaction (buff, autonomy push). On a parked lot no EA-owned Sim exists,
so the only consumers are model-owned Sims whose computed plan (docs/324 §2.4) puts them on the lot.

Model: **exposure table per lot**. From the lot's computed object timeline (spoiled food, dirty dishes, trash piles,
broken objects: each a broadcaster with a start and end time) the model builds `(broadcaster kind, room, start, end)`
rows. docs/324 reads it at a Sim's snap: for each plan block on this lot, overlapping rows give the buffs EA would have
given (kind -> buff from the broadcaster's tuning), applied through `BuffManager` at the snap. Commercial lots, which
EA pulses every pass even when empty [verified, `ShouldSimulate`], cost nothing while parked and resume at promotion
with fresh pulse timers (`mTimeSinceLastSimulate` reset, so no burst of catch-up pulses).

### 4.2 Other lot alarm callbacks

- **Sim-driving callbacks** (neighbour chores, pets' alarms, meta autonomy): their effect is a Sim doing something on
  the lot. The model converts each computed fire into an activity proposal for docs/324 (the Sim's plan gets a block,
  e.g. "empty trash at 07:10"); docs/324 decides, and its result on the lot (trash can emptied) is written back as a
  pending lot event. With docs/324 off, these Sims are EA-owned, and a lot with an EA-owned resident at home does not
  park (rule 3); EA-owned residents elsewhere fire the chore alarm on their own return, as in vanilla.
- **Generic recorded fires**: as in §4.0.
- **Household-facing**: E1.

### 4.3 World-lot alarms and yielding-alarm tasks

- World-lot timers whose `ObjectRef` is a lot-less object (`farobjworld`): held per object by the object's band,
  computed with the same table (§4.0). Timers whose owner is a Sim are docs/324 §5.2's (same hold hook). Owner-less
  global timers (managers, services, story progression, situations) are not object work: they run as EA's unless their
  manager has a model (traffic §4.6, seasons E5) or they act on a parked lot's object (then the action is routed to that
  lot's log through the on-demand guard, §6.2).
- **Yielding alarms**: the hold sits before `InvokeYieldingAlarm`, so a held yielding timer never creates an
  `AlarmOneShot` task. At materialisation, EA's own path runs it (it becomes a task then, exactly as in vanilla).
- **World-lot pulses** (lot-less broadcasters): exposure rows as §4.1, keyed by the broadcaster's position band.

### 4.4 Spawners and harvests

- **Spawner** seeded schedule: roll times from the alarm chain (`SpawnFrequency[0..1]` hours, seeded uniform; x
  `GathererTraitSpawnModifier` only with a Gatherer Sim on the lot, never on a parked lot), success if `S(k)` <
  `SpawnChance` + `PercentChanceBoost`, capacity `MaxSpawnCapacity`, delete-untaken every
  `DeleteUntakenObjectFrequency` hours at capacity. Output: the multiset alive at now plus the spawn and delete events.
- **Harvest** event log: the plant model (§4.5) gives the day it enters Harvest; yield = round(Gauss(seeded,
  `NumHarvestablesMin`, `NumHarvestablesMax`)) capped by `NumLifetimeHarvestables`; quality seeded
  (`kHarvestableQualityRandomDistributionRange`); fade after seeded `HarvestStateDurationMin..Max` days.
- **Compaction**: a spawn followed by its delete before promotion cancels out and is never materialised; only the net
  set at now is created. Side effects of the cancelled pair (opportunity hooks) are none for NPC lots [inferred,
  I-585-8].
- **Materialisation**: EA's own spawn code places each item (its random spot search, `CreateObjectOutOfWorld` +
  placement), so spots are EA's; EA's create / delete callbacks run in time order.

### 4.5 Plants and fish tanks

- **Plant** day-by-day replay, pure arithmetic: perf = `kGrowthPerformanceBase` + `PerformanceBonusFromWater[dampness]`
  + `kGrowthPerformanceFertilizer` x `mFertilizerValue`, x (1 - `kGrowthPerformancePenaltyWeeds`) with weeds, clamped
  [`mPlantSpecificMinPerformence`, 100]; progress += perf, >= 100 = next stage; water -= `WaterDecay` x hours, full when
  the world history shows rain over an unsheltered plant; dampness thresholds `kSoilWetWaterLevel` /
  `kSoilDryWaterLevel`; fertilizer days -1 per day; dormant when the history's `FrostLevel` > `kDormantFrostThreshold`;
  weed roll per day `S(day)` <= `WeedProblem` + water and fertilizer increments (x master-planter reduction). The weed
  feedback is path-dependent, which a day loop handles exactly. Auto-grow lots: seeded `kNextGrowthStateChanceForAutoGrowLots`.
  Output fields are written through EA's setters at materialisation; stage changes raise EA's events once each, in
  order; death and harvest entry are events.
- **Fish tank** seeded schedule: dirt steps every `kIntervalToIncreaseDirtiness` (+ seeded
  `kChanceForAdditionalDirtinessIncrease` per fish), hunger after seeded skewed `kTimeForFishToBecomeUnfed`
  (`kTimeSkewFactor`), kills at `kIntervalToKillFishWhenFilthy` / `kIntervalToKillHungryFish` with seeded victim choice;
  woohoo spawns need fish interactions with no EA Sim present: none. Deaths are events (EA's kill callback).

### 4.6 Traffic

`TrafficManager.UpdateTrafficTask` sleeps a random 0.5-1 h, then per lot spawns `CarTaxiLightweight` up to a random
count; each car's `LiteDriver` drives to random commercial lots, waits 5-10 min and destroys itself after 0.5-1 h
[verified]. Model (`farobjtraffic`): for parked lots the per-lot spawn step is skipped (hook in the per-lot loop) and a
seeded schedule records the cars that lot would have spawned, each with its route (seeded commercial lot sequence) and
lifetime. At a lot's promotion, or when a recorded car's route passes a live lot or the frustum ring, only cars alive
at now are materialised at their computed point on the route, out of view, through EA's spawn and `LiteDriver` start;
dead ones never exist. Cost is then the cars near the viewers only.

### 4.7 Seasons on lots

The world `SeasonsManager` loop stays (E5). Its lot-side effects are computed from the world history: rain waters
plants (§4.5) and puts out fires (§4.8), frost sets dormancy, snow and leaf sliders are global (no lot state), puddles
from rain and broken objects follow `PuddleManager` lifetimes (`kPuddleTerrainLifetime`, floor-tile lifetime) with a
seeded spread for flowing sources (`ScoreTiles` radius <= `kMaxPuddleRadius`), season-change lot timers (`Lot.cs`
season alarms) use the history's season boundaries, and wildflowers die at winter's boundary. Snowmen, leaf piles and
other season objects: leaf piles have no timer [verified]; others get rows in the §4.0 table from the P0 census.

### 4.8 Fire

Far NPC lots rarely start fires: `FireManager.AddFire` by default allows only lots the active household owns or lots
already in a fire emergency [verified]. Sources on a parked lot are timer-driven ones (fireplace catch-fire checks
with `kChanceCatchFireWhenNoSimsInRoom`) and lot-less events. Model (`farobjfire`), seeded:
- start: per fireplace check `S(k)` < the no-Sims chance -> fire at the fireplace;
- smolder `kSmolderTime` -> flame; spread every `kSpreadFrequency` with the floor / object chances to adjacent tiles
  and objects (the grid from the lot's footprint at park time); objects burn after `kTimeTillObjectBurns` (semi-burn
  12); go-out checks from `kFireLifetime` every 5 min with `kChanceFireGoesOut`; rain from the world history puts it
  out;
- firefighters: EA's emergency situation would send a service Sim; the model computes its arrival from EA's service
  delay tuning and ends the fire then, and hands the visit to docs/324 as a service-Sim block (no Sim is spawned for a
  parked lot);
- output: burned / destroyed objects and burn marks as events, fire ended or still burning at now. A fire still
  burning at promotion is materialised as live EA fire at its computed tiles (EA's `AddFire` with the emergency flag),
  then EA runs it.
- Exception E2: if the computed spread front comes within reach (spread radius x remaining lifetime) of a household
  lot, household Sim or household-owned object, the lot promotes and EA runs the fire live from that point.

### 4.9 Objects with their own script thread

`TreeOfProsperity` runs a `OneShotFunction` loop every 0.25 sim-min plus a 4 h fruit alarm [verified]; other
own-thread objects come from the P0 census (`OneShotFunction` row 2-11 ms/s). Freezing a coroutine is what docs/243
rejected, so the model **pauses** instead: a per-class hook on the loop body (for the tree, `SimulateTree`) sleeps long
(`Simulator.Sleep` with the next periodic-pass interval) while the object's lot is parked; the coroutine stays valid,
holds no waits on Sims (none is present). Computed state: fruit index = start + floor(dt / `kHoursToTreeReset`), AOE
effects on Sims none (no EA Sims), the hour-23 VFX shows skipped (visual only). Classes without a hook: E4.

### 4.10 Door privacy and zombies

- **Door privacy**: state follows the Sims on the lot; with none, nothing changes. At promotion EA's next pass
  recomputes it from the Sims present [inferred, I-585-3].
- **Zombies** (EP7, only on `ShouldSimulate` lots: commercial while parked): zombie count on the lot = the zombies
  docs/324 places there by plan; the lot's `ZombieDanceTimeOut` decrements by elapsed time (closed form); when the
  count reaches `kNumZombiesToDance` with the timeout at 0, a pending "dance stereo" event is logged at that time and
  materialised through `AddZombieDanceStereo` at promotion if the condition still holds at now.

## 5. Exceptions (only where computing is impossible or unsafe)

| # | What stays EA's | Reason | How it could be computed later |
|---|---|---|---|
| E1 | Household-facing timers on a parked lot (owner or target is a household Sim or object; opportunity activation on the lot [verified, `Lot.cs`], phone, invitation, household bills and repo) | household rule: results for controlled Sims equal vanilla, at their real time | none needed: they run alone at their due time through our dispatch, the lot stays parked |
| E2 | Fire whose computed front can reach a household lot, Sim or owned object | unsafe: household Sims and property must see EA's fire | compute the front with a tighter bound (room walls, fire-blocking floors) so fewer lots promote; promote later |
| E3 | Any lot while household scope is unknown (stale table, travel, household change) | household rule | none: the rule stays |
| E4 | Own-thread object classes without a pause hook (mods' coroutines, census leftovers) | a foreign coroutine cannot be paused safely without knowing its loop (docs/243) | P0 census lists them; add a per-class pause hook (the §4.9 pattern), one class at a time |
| E5 | The world `SeasonsManager` loop | one loop for the whole world, visible weather everywhere, cost does not grow with lots | its weather queue is seeded and could run as a closed form between viewer reads on a streamed world; lot effects are already computed (§4.7) |
| E6 | Fire materialised while the lot is in view | a running fire cannot be placed unseen and must not pop in | none: an in-view lot is L1, so it never parks; listed for completeness |

## 6. Ownership, park, promotion and materialisation

### 6.1 Park (EA -> model) at a safe point

A lot parks at the periodic pass (every 5 s real) when it is in L2 past 200 m, the dwell is met, and:
- no EA-owned Sim on it or routing to it (route destinations read only); no Sim inside a portal on its edge;
- no running interaction targets one of its objects; no reservation or use list holds an EA-owned Sim;
- no live fire (a fire under the fire model is fine only when it started while parked);
- not saving, travelling, in Edit Town, build/buy or CAS; the household table is fresh (rule 1).

On park: record `parkStart`, the timer census (§4.0, handle, callback, due, repeat, owner), the lot's footprint grid
for the fire model, and the object rows the models need (plant fields, spawner sets, food stages); set the owner flag.
**Nothing in EA's state changes.** Our hook around `AlarmManager.Simulate` (`Patch` with orig, the `AlarmKillLog.cs`
pattern [verified our source]) does not call orig for a parked lot; for E1 timers due now it runs our own minimal
dispatch (pop that timer, reschedule or remove as EA does, invoke). Hooks on `UpdateDoorPrivacy` (already hooked by
`Batch591Logic.cs`; chain inside the same hook), `UpdateNumZombiesOnLot`, `UpdateReactions`, the traffic per-lot step
and own-thread loop bodies return early for parked lots.

### 6.2 Promotion (model -> EA)

Triggers (each queues a promotion, priority by time-to-view as docs/324 §6.2): the lot enters L1 (distance, frustum,
pre-expand ring, observation); a Sim is promoted onto the lot or starts a route to it (docs/324, any household route);
an EA or mod call reads a parked object (on-demand guard: the read site hooks where they exist, e.g. harvest helpers;
the read waits for the lot's materialisation); E2; save, travel, world change, Edit Town, switch off. **No timer
promotes a lot** (rule 2).

### 6.3 Materialisation at promotion

1. Evaluate every model at `now` (pure, cheap).
2. Write computed fields through EA's setters (plant stage, water, food stage, dirt).
3. Run the event log in time order through EA's code: create / delete, `ForceSpoil` per stage, kill callbacks, burns,
   generic recorded fires (EA's callback, k times), with the yield path for yielding timers. Compacted pairs are
   skipped.
4. Hand Sim outcomes to docs/324 (proposals already consumed; service visits, zombie counts).
5. Reset the lot's EA timers to their next real due time after now (`UpdateAlarmTime`, EA's API [verified], computed
   from each timer's repeat data), so EA's next pass does not fire the held timers a second time.
6. Reset pulse timers; clear the owner flag. The lot is L2; it re-parks only after a full dwell.

**Seen-in-view guard** (docs/326 P1): visible changes (model swaps, spawns, deaths, burn marks) never run while the lot
is in the frustum; a lot promoted while already in view (camera jump) defers visible steps until the camera turns away
or at most 1 real second, then runs them with EA's own fades [inferred]. Counter `catchupInView` near 0.

### 6.4 Save flush

Before `OptionsModel.SaveGame` (the `UnseenOutfitDefer` `DisposeSaveHook` precedent, docs/586 [verified our source]):
materialise and unpark every lot, unthrottled behind the save screen. The .sims3 holds plain EA state. Mod save paths
that skip `OptionsModel.SaveGame` write overdue EA timers only (vanilla lag semantics on load), logged (I-585-5).

### 6.5 One owner and the far-Sim model

Owner flag per lot in ts3fixes. Promotion order: lot first, then its Sims; demotion: Sims first (to T3), then the lot.
Shared with docs/324: viewer points, TTV priority, periodic pass, spread task, save flush, sidecar folder, and the
alarm hold hook (§5.2 there; one owner table with Sim ids and lot ids). A model-owned Sim never uses, reserves, dirties
or breaks an object (docs/324 §3.10); its effects on a lot come only through §4.2 proposals the far-Sim model accepts.

## 7. Spread and budget

Materialisation runs in a sim-thread task with the EaSpread pattern (`EaSpreadCore.SliceFull`, `SlicedPass<T>`;
docs/146, 147), as docs/324 §6.1: **2 ms per sim frame**, none in a frame already over 25 ms; one lot is the unit of
priority, one event the unit of work; a lot stays parked until its last event ran, then the flag clears in one step;
object creates are capped per frame (start: 4) because small-object `CreateObject` cost is unmeasured [inferred];
traffic cars use the same cap; save flush unthrottled. A lot with 20 plants x 3 sim-days is 60 arithmetic steps + 20
field writes [inferred: well under 2 ms]; spawners, harvests and fires carry the create / destroy cost.

Deviation as built (docs/358, 2026-11-26; `FarHubCore.Frame`): a game that runs above 25 ms all the time would never get a boundary pass, a switch-off drop or a go-near, so after 7 skipped long frames in a row the 8th steps with a 0.5 ms budget (`kOverRunEvery` 8, `kForcedBudgetUs` 500). The run of long frames restarts on a normal frame, on pause and on world quit.

## 8. Final split measurement (P0, design only, not queued)

Gaps after the 11-22 run: (1) no-position owners (`WorldLot`, `AlarmOneShot`, `OneShotFunction`, `StateMachine`);
their timers carry `Timer.ObjectRef` [verified field], and world-lot broadcasters have positions; (2) lot sub-owners
are a sample mix (I-582-5); (3) 10 minutes cover less than one sim-day; (4) story progression off. Closing them needs a
managed hook on EA's alarm dispatch, more than a small diag, so it is designed here and not queued.

**Probe `[Diag] FarObjProbe`** (ts3fixes, default off; agent ~2-3 h): hook around `AlarmManager.Simulate`: read the
due timers (read only), time the orig call, split by count across them (exact with one due timer) [inferred share
otherwise]; key = callback type + method, `ObjectRef` kind and band, lot kind; count `AlarmOneShot` creations by the
same key; per lot per periodic pass a "would park" census (tier, eligibility reasons, timer counts per §4.0 model,
E1 count); `StateMachine` creator class; traffic spawns by lot band; dumps every 60 s via the TaskSeam writer
(`farobj:` lines), report `nonsim-split-report.ts --farobj`. Also the static **callback census**: every `AddAlarm*`
callback in the shipped packs and top-100 mods, classified into the §4.0 rows.

**Queue when built** (`far-object-split-<mmdd>.json`, Bin state `apex`, saves and seed as nonsim-split-1122): smoke 3
min; `bp-` / `ip-` / `sv-farobj-split` 10 min with `[UnseenProbe] Bands=150,200,450,1000`, TaskSeam, NonSimSplit,
FarObjProbe; `bp-` / `sv-farobj-day` 25 min at speed 3 across 05:00, story progression ON. Correctness only. Placement:
after every chain listed by `node tools/harness/track.ts status`, waiter without the `chain-` prefix, never
preempting. Est ~90 min.

## 9. 4x and streamed worlds

- Every object system is now near-only: alarms, pulses, own-thread loops, traffic spawns and lot passes run for H, L1
  and L2 lots; parked lots cost one periodic check each (a distance test from the lot table) and nothing per tick.
  Lot count grows ~4x with area (BP 83, IP 120 lots in the run [measured]); today every far lot's work grows with it
  [inferred: ~linear]; with the model, object cost follows the lots near the viewers and the household.
- The remaining per-lot term is the periodic distance check (5 s real, a few us per lot) and the world history ring
  (one row per weather change). Both are negligible at 4x [inferred].
- Keys are lot id, object id and region id; radii are metres; the lot table grows with the lot count (docs/329, no
  cap). The event log is per lot and bounded by compaction (only the net state at now is materialised).
- A parked lot is the natural page-out candidate of docs/320 §7.5 (quiesced: no Sim, no route, no alarm running).
  Page-out = park + serialize, with the lot's census and seeds in `<Save>.lw/`; page-in = deserialize + materialise.
  The models here are the fast-forward a streamed world needs, so a lot paged out for days catches up in one pass.
- Needs docs/321 lane A (lot load / unload hub, `lotstream.cpp`) for streamed lots.

## 10. Gain estimates [inferred]

| | BP | IP | SV | Basis |
|---|---:|---:|---:|---|
| far class b, household excluded | 27.7 | 33.9 | 11.8 | §2.1 [measured] |
| far lot alarms, other callbacks | 8.9 | 10.0 | 10.5 | §2.1 [measured]; now held too |
| far share of no-position rows (`WorldLot` + `AlarmOneShot` + `OneShotFunction`, minus Sims' alarms) at 30-80% | 11.6-31.0 | 11.8-31.5 | 11.9-31.8 | [inferred]; P0 measures |
| traffic of far lots at 50-90% | 5.4-9.6 | ~1 | ~1 | [inferred] |
| **ceiling** | **~54-77** | **~57-76** | **~35-55** | |
| minus exceptions (E1-E6), lots with EA-owned Sims, materialisation cost; residential pulse overlap with docs/324 counted once | -15 to -40% | | | P0 census |
| **realised** | **~35-65** | **~35-65** | **~20-45** | 3.5-6.5% of the sim thread |
| hitch | daily alarm bursts confined to near lots | | | docs/320 §2 row 7 (956 ms worst) |

Materialisation cost: arithmetic per period plus EA creates / destroys, spread at 2 ms per frame; at a few promotions
per minute, ~1 ms/s [inferred]. On a 4x world the removable part scales with the far lots (~4x), the cost stays
near-only.

## 11. Phased plan and tests

Agent wall-clock time (CLAUDE.md effort rule); game runs separate. Every switch default off, household exemption in
code, one repro per behaviour (AGENTS.md).

| Phase | Work | Agent time | Game runs |
|---|---|---|---|
| P0 | `[Diag] FarObjProbe` + report (§8); static callback census of packs and top-100 mods into §4.0 rows; own-thread class census; NRaas / mod object-tick census (docs/328) | ~3-4 h | smoke + 3 x 10 min + 2 x 25 min |
| P1 | Shared hold hook with docs/324 (lot skip, E1 own dispatch, world-lot holds by owner), park / promote / owner flag, periodic pass, eligibility, timer census, world history ring, sidecar, save flush; **shadow mode** (census + would-park only) | ~4-5 h | 1 correctness + 3 x 10 min shadow |
| P2 | Closed-form and seeded models: plants, food spoil, dishes, trash / litter piles, fishing spots, fish tanks, puddles, wildflowers; generic recorded fires; event log + compaction + ordered materialisation; spread task; seen-in-view guard; `UpdateAlarmTime` reset | ~5-7 h | repros 1-7, 13 |
| P3 | Spawners and harvests (seeded schedule, EA spawn at materialisation); exposure tables for pulses (commercial included) and the docs/324 read; door privacy and zombies; own-thread pause hooks (Tree of Prosperity first) | ~4-5 h | repros 14, 16, 18, 19 |
| P4 | Fire model (`farobjfire`) with E2 promotion; traffic model (`farobjtraffic`); seasons lot effects from the history | ~4-6 h | repros 8, 15, 17, 20 |
| P5 | **Shadow calibration** (docs/326 #9): park in shadow, EA keeps running, compare model vs EA per class at the would-be promotion over 2 sim-days (stage, water, spawned count, spoil stage, dirt, fire outcome on a seeded fire lot, traffic count near viewers) | ~2 h | 2 x 25 min (BP, IP) + 1 x 25 min SV |
| P6 | Timing: in-load A/B, `farobjlot` off / on, BP / IP / SV with docs/324 off in both arms; then with docs/324 T3 on in both arms (overlap); then day runs | ~30 min | smoke + 3 x 2 arms x 10 min + 2 arms x 2 x 25 min |
| P7 | `farobjworld`: lot-less objects on `AlarmManager.Global` by `ObjectRef` band; world-lot pulses | ~2-3 h | 3 repros + 1 timing pair |
| P8 | Streamed-world hand-off: park as page-out precondition, materialise on page-in (with docs/320 §7.5 and docs/321 lane A); 4x proof with `tools/world-gen.ts` | ~1-2 h | 3 jobs |
| **Total** | | **~24-34 h** | **~50 jobs** (timing on a quiet machine) |

**Correctness repros** (each a `tools/harness/repros.json` entry with off / on arms, rewind on):

1. `farobj-household-never`: 10-min soak, household over town (home, work, community lot, friend's lot); counter
   `parkedHousehold` = 0; every lot with a household Sim or route destination stays L1.
2. `farobj-pan-promote`: camera pans to a parked lot with plants mid-growth; materialised before the frustum,
   `catchupInView` = 0, stage equals the shadow prediction.
3. `farobj-save-parked`: save with parked lots, reload with the switch off; .sims3 holds only EA state, 0 script errors
   (save matrix).
4. `farobj-travel`: household travels to a parked community lot; materialised before arrival.
5. `farobj-harvest`: far NPC garden parked 3 sim-days, household visits and harvests; harvestables exist, counts in
   EA's range, no duplicates, fade alarms correct.
6. `farobj-spoil`: food on a far counter, parked past its spoil time; one `ForceSpoil` per stage, broadcaster and flies
   as the off arm (with `spoilbroadcast`).
7. `farobj-no-timer-fire`: a parked lot with 20 timers due over 2 sim-days: counter `heldTimerFired` = 0 while parked;
   at promotion each fires per its model, none twice (`UpdateAlarmTime` reset checked).
8. `farobj-fire-never-near-household`: a seeded fire on a far NPC lot (fireplace roll forced): the model owns it;
   then the same with the household 30 m away: the lot promotes before the front can reach, EA runs it, counter
   `fireNearHousehold` = 0 under the model.
9. `farobj-e1-passthrough`: a household opportunity alarm on a parked lot fires at its real time (log time vs the off
   arm) and the lot stays parked.
10. `farobj-farsim-order`: docs/324 T3 on, a far NPC promoted onto its parked home lot: lot materialised first,
    `ownerConflict` = 0.
11. `farobj-nraas`: Overwatch + StoryProgression installed; a mod harvest on a parked lot waits for materialisation;
    no double catch-up.
12. `farobj-crash-parked`: harness kill while lots are parked; reload on (sidecar materialises) and off (EA fires
    once); 0 script errors.
13. `farobj-generic-replay`: a mod timer with no model (test mod, 15-min repeat) on a parked lot for 2 sim-hours: 8
    recorded fires replayed in order at promotion, each callback sees its recorded index.
14. `farobj-spawn-at-promotion`: spawner lot parked 2 sim-days; spawns appear at promotion through EA's spot search,
    count <= `MaxSpawnCapacity`, compacted pairs never created (counter), distribution vs the off arm over 5 runs.
15. `farobj-seasons-catchup`: Seasons world, a parked garden across a rain day and a frost day; water refilled by the
    recorded rain, dormancy from the recorded frost, equal to the shadow run's EA state.
16. `farobj-commercial-pulse-resume`: a far commercial lot parked, then a Sim arrives: pulses resume on the first live
    pass, no catch-up burst (pulse count per sim-minute <= the off arm's), no missed reaction for the arriving Sim.
17. `farobj-traffic-near-only`: camera tour; taxis exist only near the viewers, a car whose computed route crosses the
    ring appears out of view, `trafficSpawnFar` = 0.
18. `farobj-tree-pause`: Tree of Prosperity on a parked lot: its loop sleeps (run count ~0 while parked), fruit index
    at promotion equals the closed form.
19. `farobj-zombie-dance`: EP7 full moon, zombies placed by docs/324 on a parked commercial lot: dance stereo
    materialised once if the condition holds at promotion.
20. `farobj-firefighter-handoff`: a model-owned fire ends at the computed firefighter arrival; docs/324 gets the
    service block; no service Sim spawned for the parked lot.

**Timing:** in-load A/B (`profile.ab`), 10 min per arm and 25-min day runs, Bin state `apex`, docs/322 band probe and
NonSimSplit on for per-class rows per arm; ABBA order; judge by sim ms/s by band, hitch p99 / p99.9 / max, sim it/s;
**needs quiet machine**. queue.ts runs the measurement smoke first. No timing before P5 passes.

## 12. Decisions and open questions

Rodney decided on the first version (2026-11-23):

| # | Question | Decision | Where |
|---|---|---|---|
| D0 | Allow-list and wake-on-timer vs compute everything | **compute everything**: parked lots hold all timers, nothing fires or wakes on its own, every class has a computed model; exceptions only where impossible or unsafe | §1.2 rule 2, §4, §5 |
| 1 | Park radius 200 m (lots) vs 450 m (Sims) | 200 m stays as the recommendation; no conflict found with docs/324 (rule 3 keeps NPC-occupied lots live until T3) | §1.3 |
| 2 | Fidelity of catch-up | compute everything with seeded models; EA callbacks only to **materialise** events at promotion (creates, destroys, spoil stages, kills, generic recorded fires) | §4, §6.3 |

Open (recommendations):
3. **World-lot objects (P7).** Recommend after P6: the lot-level gain is measurable alone, and P7 shares the dispatch
   hook with docs/324 T3, which should land first.
4. **Mods that tick held classes with their own timers.** Recommend: route their reads through the on-demand guard;
   where a mod writes state the model also computes, the mod's class model goes into the §4.0 table from the census,
   and until then the class's lots do not park while that assembly is loaded (stand-down by assembly name, the
   `CompatResolve.cs` pattern), logged.
5. **Build the P0 probe now or wait for docs/324 P1?** Recommend now (~3 h, measurement only): it places the ~40 ms/s
   no-position rows and the daily bursts and fills the §4.0 census.
6. **Ship order.** Recommend: docs/330 loop fixes first, docs/324 T3 second (bigger gain, owns every Sim hand-off this
   model needs), this model third.

## 13. docs/51 entries

docs/51 §585 (I-585-1 .. I-585-9).
