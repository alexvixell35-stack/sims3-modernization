# 350. Far-object state machine: the spec builds implement (SD-2, 2026-11-26)

Design only: nothing built, nothing run. Static reads for this doc: the census CSV and five registration lines in the
decompiled sources (§6.7). Package SD-2 of docs/340 §6.2. Gate: every census class has a row or an exception.
Builders: FO-3 .. FO-12, DT-1 / DT-2 (replay). Handoff: `docs/handoff/sd2-far-object-fsm-1126.md`.

Builds on, and does not repeat: docs/331 (far-object model: classes, exceptions E1-E6, measured split), docs/345
(far-Sim machine, SD-1: this doc mirrors its structure, units, PRNG and trace), docs/346 (`FarHold` alarm hold hook and
the 1,339-row census), docs/348 (own-thread objects, lot hub), docs/343 (sidecar), docs/344 (lazy instantiation RE),
docs/347 (EA setters, Sim side), docs/340 §3 (architecture), docs/332 (FO package ids), docs/322 (bands).

Markers: [verified] = verified by the doc cited on the row, or by a source line cited here; [measured] = measured by
the doc cited; [inferred] = design choice or conclusion of this doc, to confirm at build; [pending X] = waits for X.

Data: `docs/data/far-object-class-machines.csv` (691 rows, 561 classes, 1,339 sites): every census class with its
route, machine and release action, produced from `docs/data/alarm-callback-census.csv` by the rule of §6.1.

## 0. Short answer

- The unit is the **lot**. 6 lot states: `Live`, `GoFarPending`, `FarLight`, `FarParked`, `PendingLive`, `Excluded`.
  15 transitions (§5), each with trigger, guard, inputs and action.
- `FarLight` is new: the lot's alarms keep running, only the per-lot pulse work is skipped (reaction pulses, zombie
  count, door privacy) while no EA-owned Sim is on or near the lot. It needs no model and no catch-up, so it covers
  the lots that cannot park yet (rabbit holes, lots with an unread "shows UI" timer, lots with an unhooked own-thread
  class) and can ship first.
- `FarParked`: the lot id is in `FarHold` (docs/346), so its alarm manager is not simulated and its own-thread objects
  are paused (docs/348). The state of the lot and of each modelled object at sim time t is a **pure function** of
  (seed, park snapshot, recorded inputs, t). Nothing is stepped while parked. Go-near evaluates once and applies the
  result through EA's handle API and setters, then releases the lot (§9.2).
- Every census site has a route (§6.1): 427 are far-Sim rows (SD-1), 299 stay EA's (system and world timers), 588 are
  lot rows, 14 are global walkers that get a filter, 11 are instance callbacks held by the lot of their target.
  Of the 588 lot rows: 47 named models, 328 long-tail rows under 6 generic machines, 93 rabbit-hole rows
  (`FarLight` only in model version 1), 120 rows that cannot be armed on an eligible lot.
- The global no-owner pulses of docs/346 Q4 (93 rows there, 92 after one census correction) are split into four
  handlings (§6.7). `Wildflower` is a filter: the
  hourly spawn for a parked lot becomes a pending event and is created by EA's own code at go-near.
- Random numbers: the SplitMix64 finaliser of docs/345 §8, keyed by (world seed, lot id, object id, model version,
  slot, nominal due tick). No running stream and no episode counter, so the model needs no carry-over line in the
  sidecar (§8, §12).
- Never far (§4.3): household home lot, active lot, any lot a household, selected or unknown-scope Sim is on, routes
  to or references through an object, the world lot, and more. `[Experimental] HouseholdToo` does not apply.
- Everything default off. Estimated gain [inferred, §15]: `FarLight` alone about 16-23 / 19-29 / 6-10 ms/s
  (BP / IP / SV); full model with far Sims on about 27-42 / 34-46 / 18-27 ms/s, traffic not counted.
- Open: 11 questions (§17), none for Rodney. Answered by Rodney 2026-11-26: Q1 full flush stays on EA worlds, the
  no-flush mode is Atlas `docs/007-paged-lot-save.md`; Q2 the counted replay cap of 32 is accepted; Q3 lots with an
  unmapped Sim hand-off repeat stay at `FarLight`.

## 1. What this doc changes in earlier docs

| Earlier text | Here | Why |
|---|---|---|
| docs/331 §1.3 tiers H / L1 / L2 / L3 | states `Excluded` / `Live` / `GoFarPending` / `FarParked`, plus `FarLight` | one naming scheme with docs/345; `FarLight` gives a gain where parking is blocked |
| docs/331 §4 seed `S(k) = H_v(lotId, objectId, timerKey, k, worldId)` | docs/345 §8 mix; key = (lot id, object id, version, slot, nominal due tick) | one PRNG for both machines; the due tick is unique per fire, so no index has to be stored |
| docs/331 §1.3, §6.1: periodic pass every 5 s real time | pass every `BoundaryPassMin` sim minutes, shared with docs/345 §4.2 | no wall clock in the machine |
| docs/331 §6.1: E1 timers on a parked lot run through our own minimal dispatch | a lot with an armed E1 timer is not parked; it takes `FarLight` | docs/346 Hook A offers no dispatch for a parked manager; such lots are bounded by the household's ties, not by world size |
| docs/331 §7: go-near budget 2 ms per sim frame | budget in operations per sim frame (§7.3) | a time budget makes the order of EA calls depend on the machine's speed (docs/345 §7.2, H3) |
| docs/331 §4.0 generic row: recorded fires replayed k times | kept, with a cap, a re-arm bracket and a rule per census hint (§6.3) | a callback that re-arms itself would add one timer per replayed call |
| docs/331 §6.4: sidecar keeps the park start per lot | nothing per lot in flush mode; the big-world save path is Atlas `docs/007-paged-lot-save.md` (§9.4) | every save flushes, so EA state is current |
| docs/346 §4.4: `Lot.RunTriggerVolumeCallbacks` listed as global, no owner | lot manager, no owner [verified, `Lot.cs:9668` calls the lot's own `AlarmManager`] | covered by the parked-lot case; the census CSV row was corrected with this doc (2026-11-26): it is now "lot manager (explicit)", so the CSV has 92 global no-owner rows in FO and World groups; the totals printed in docs/346 §4.2 and §4.3 still count it as global |
| docs/340 §3.3 lot table row "Traffic: seeded spawn rate by band" | unchanged, pointer only (§6.8) | FO-12 owns it; no new fact here |

## 2. Scope and units

- Covers docs/340 §3.6 stage L0: every object stays instantiated. Object L1 is a no-go and object L2 is a pilot on the
  WS-0 world only (docs/344 §1). The lot record of §3 holds ids only, so an L2 pilot can reuse it; SD-4 owns that.
- Sim outcomes are never produced here: they are proposals to the far-Sim machine (docs/345), which owns every Sim.
- Sim time is an integer tick count. Durations are written in sim minutes and converted once with EA's
  ticks-per-minute constant. EA tuning floats (hours, chances) are converted once per world load to ticks and to
  integer parts per 2^32; the machine stores and compares no float. Distances are integer centimetres.
- "Viewer points" = camera target, camera position and every household Sim (docs/340 §3.2). Lot distance = distance
  from the nearest viewer point to the lot's centre minus its half diagonal (docs/331 §1.3).

## 3. Record and inputs

### 3.1 Per-lot record

One record per lot that is not `Live`, in one table keyed and sorted by lot id (never an object-keyed dictionary).
A `Live` lot has no record, except a dwell entry while it is far but inside the dwell time.

| Field | Type | Meaning |
|---|---|---|
| `lot` | u64 | lot id |
| `state` | u8 | ordinal of §4.1 |
| `enteredAt` | tick | nominal tick of the last transition |
| `parkStart` | tick | tick of L3 (`FarParked` only) |
| `nextDue` | tick | next edge that has a side effect while parked (§7.2), or none |
| `ti` | u32 | count of logged transitions of this lot (trace order only) |
| `why` | u8 | reason of the last transition (§5.2) |
| `block` | u16 | bit set of the reasons that keep the lot in `FarLight` (§5.3) |
| `snap` | index | park snapshot (§9.1): timer rows, object rows, footprint grid |

Park snapshot rows:

| Row | Fields |
|---|---|
| timer row | handle id, callback id (declaring type + method, interned), owner object id or 0, due tick, repeat ticks or 0, yield flag, machine id (§6) |
| object row | object id, machine id, the fields that machine reads (§6.2), all integers |
| pending event | nominal tick, kind, object id or 0, argument |

Size: about 40 bytes per record, 32 per timer row, 48 per object row [inferred]; about 1 KB for a lot with 10 timers
and 10 modelled objects.

### 3.2 Inputs

The complete list. Anything not on it must not reach a transition or a model.

| # | Input | Source | Class |
|---|---|---|---|
| J1 | sim tick; from it day index, day of week, hour | `SimClock` ticks | clock |
| J2 | world seed, model version `fo` | sidecar `W` line (docs/343) | constant |
| J3 | lot id, object id | EA | constant |
| J4 | park snapshot | read from EA at L3 (§9.1) | recorded |
| J5 | tuning tables of the modelled classes; hash kept as `tuneHash` | EA tuning, read once per world load | constant per load |
| J6 | world-history ring: weather kind, intensity, temperature, frost, snow, wet, season, holiday | docs/343 `H` lines, shared with docs/345 I6 | recorded |
| J7 | boundary event near / far per lot | boundary pass (§4.2) | recorded, **R** |
| J8 | forced event per lot: hub events of docs/348 §6.1 (`LotLeave`, `ObjectEnter`, `ObjectLeave`, `ScopeChange`, `WorldReset`), EA-owned Sim arrives or routes to the lot, on-demand read, observation, exclusion reason, switch off, flush, stand-down | hooks (FO-4) | recorded, **R** |
| J9 | far-Sim input: a model-owned Sim's plan block on this lot, an accepted chore proposal and its result | docs/345 trace | recorded, **R** |
| J10 | pending event added by a global-walker filter (§6.7 G-F): tick, lot, kind | the filter prefix | recorded, **R** |
| J11 | answer of an EA predicate a model asks (plant sheltered from rain, object in a fridge, room of an object) | EA method, called once at park, stored in the object row | recorded (part of J4) |

Class R inputs are written to the trace (§13.1) before the machine acts on them. A replay feeds the recorded answer
and never calls EA.

## 4. States, boundary and hysteresis

### 4.1 States

| # | State | Owner | What runs on the lot |
|---|---|---|---|
| 0 | `Live` | EA | everything |
| 1 | `GoFarPending` | EA | everything; the dwell clock runs (docs/331 L2) |
| 2 | `FarLight` | EA, pulses skipped | alarm manager, detail priority and display level run; `UpdateReactions`, `UpdateNumZombiesOnLot` and `UpdateDoorPrivacy` return early |
| 3 | `FarParked` | machine | nothing of the lot's own: lot id in `FarHold`, alarm manager not simulated, own-thread objects paused, pulses skipped. Detail priority stays (docs/331 §2.1, render-facing) |
| 4 | `PendingLive` | machine, catch-up queued or running | as `FarParked` until the last catch-up step, then released in one step |
| 5 | `Excluded` | EA | everything; the lot is on the never-far list (§4.3) |

Invariants (each a counter that must be 0, §13.3): V1 one owner per lot; V2 a lot in `FarHold` is `FarParked` or
`PendingLive` and nothing else; V3 no EA-owned Sim is on a `FarParked` lot; V4 no timer of a `FarParked` lot fires
(`farobjFiresWhileParked`); V5 `F(t)` of a parked lot depends only on J1-J11.

### 4.2 Boundary and hysteresis

One boundary pass every `BoundaryPassMin` sim minutes (default 5), the same pass as docs/345 §4.2, lots after Sims,
in lot id order. It does not run while the game is frozen (§11).

- Far: lot distance above `R_park` (default 200 m, docs/331 decision 1; per-world key `parkEdgeM` in the sidecar `K`
  lines once FO-2 has measured it) for `ParkDwellMin` sim minutes (default 30) with no near trigger.
- Near: lot distance below `R_near` (default 150 m) plus the pre-expand ring of docs/345 §4.2 (sized by time to view,
  SD-3), or any near trigger. The two radii never touch, so a lot on the edge does not flap; `farobjFlap` counts a
  park within 10 sim minutes of a go-near.

Near triggers (each is a J7 or J8 input):

1. distance or frustum, with the pre-expand ring;
2. an EA-owned Sim is on the lot, starts a route to it, or comes within `R_pulse` of its bounds (`R_pulse` = the
   largest `PulseRadius` of the lot's broadcasters read at park, at least 10 m). A model-owned Sim whose docs/345
   `place` is this lot is **not** a trigger: it is a record, not a Sim on the lot;
3. a far Sim goes live onto this lot (docs/345 T15): the lot goes first, then the Sim (docs/331 §6.5);
4. `ObjectEnter` or `ObjectLeave` on the lot from any code that is not our catch-up (reason `extWrite`). This is the
   catch-all for EA or mod code that creates, moves or destroys an object on a parked lot;
5. `ScopeChange` (move in, move out), `LotLeave`, houseboat position change (`Lot.UpdateLotPosition`);
6. observation: map view selection of the lot, lot info or real estate UI, Edit Town, Build / Buy on the lot;
7. an on-demand read by a hooked read site (docs/331 §6.2);
8. a computed fire front within reach of a household lot, Sim or object (docs/331 E2);
9. save, travel, world change, quit, switch off, stand-down (§9.4).

**No timer promotes a lot** (docs/331 rule 2).

### 4.3 Never-far list (`Excluded`)

A lot is `Excluded` while any row holds. The first seven are household rows and are re-checked at every entry point
of §10.

| # | Never far | Check |
|---|---|---|
| X1 | the active household's home lot | `Household.ActiveHousehold.LotHome` |
| X2 | the active lot and the selected Sim's current lot | `LotManager.ActiveLot`, `LotCurrent` |
| X3 | any lot a household Sim, the selected Sim or a Sim of unknown scope is on | shared predicate `Fixes.HouseholdExempt` + "scope known" |
| X4 | the destination lot of a route, travel, rabbit-hole stay or career / school alarm of such a Sim | route destinations read only; career and school locations of the household |
| X5 | any lot holding an object a household Sim owns, uses, has reserved, routes to or stole from: household inventory objects placed in the world, the household's cars, owned-by component, `mReferenceList`, `mActorsUsingMe`, `GameObjectStolenFrom` | per object at park; one hit excludes the lot |
| X6 | lots the active household owns or runs: real estate, resorts, venues | `RealEstateManager`, `ResortManager` owner checks [pending FO-4 read] |
| X7 | a lot in a situation, party, date, opportunity or delivery with a household Sim; a lot with an armed E1 timer (docs/331 E1) takes `FarLight`, not `Excluded` | situation lists; pass-through set of docs/346 §3.3 |
| X8 | every lot while the household table is stale or a move or travel is in progress (docs/331 E3) | unknown scope = household |
| X9 | the world lot | docs/346 §3.6 |
| X10 | a lot with a live fire that did not start under the fire model | `FireManager` |
| X11 | a lot being observed (near trigger 6) or in Edit Town / Build / Buy | mode test |
| X12 | a lot a mod marked through a compat row, or a lot holding an object of a mod class with its own alarm or task and no census row (docs/331 E4, decision 4) | assembly of the object's type |
| X13 | a lot born in this pass (`LotEnter`): it starts `Live` and is judged on the next pass | docs/348 §6.1 |

Not on the list, but limited to `FarLight` in model version 1: **rabbit-hole lots** (§6.5), lots with an armed timer
of an unread "shows UI" class (§6.3 K5), lots holding an own-thread class without a pause hook (§6.6), lots with an
armed E1 timer (X7).

## 5. Transition table

### 5.1 Table

| # | From | To | Trigger | Guard | Inputs | Action |
|---|---|---|---|---|---|---|
| L1 | `Live` | `GoFarPending` | boundary pass: far | not `Excluded` | J1, J7 | dwell entry created |
| L2 | `GoFarPending` | `Live` | any near trigger | - | J7, J8 | dwell entry dropped |
| L3 | `GoFarPending` | `FarParked` | boundary pass: dwell met | `G-excl`, `G-sim`, `G-safe`, `G-model` | J1, J3, J4, J5, J11 | park (§9.1) |
| L4 | `GoFarPending` | `FarLight` | boundary pass: dwell met | `G-excl`, `G-sim`; `G-safe` or `G-model` fails | J1 | pulse skip armed; `block` = failed guards |
| L5 | `FarLight` | `FarParked` | boundary pass | all four guards hold now | as L3 | park (§9.1) |
| L6 | `FarLight` | `Live` | any near trigger | - | J7, J8 | pulse timers reset (`mTimeSinceLastSimulate`), record dropped. No catch-up |
| L7 | `FarParked` | `FarParked` | due edge (§7.2) | EP3 | J1, J4, J6, J9 | side effect only: chore proposal to docs/345, exposure row change, fire front check |
| L8 | `FarParked` | `PendingLive` | any near trigger | - | J7, J8 | catch-up queued, priority by time to view; reasons 2-5 and 7-9 are priority 0 (run in the same call) |
| L9 | `PendingLive` | `Live` | last catch-up step done | - | J1, J4, J5, J6, J9, J10 | `FarHold.Release(lot)`, pulse timers reset, record dropped (§9.2) |
| L10 | `PendingLive` | `Live` | a catch-up step threw or a guard failed mid-way | - | - | fallback (§9.3): remaining timers are left to EA, `farobjGoNearFallback` |
| L11 | any far state | `Excluded` | a row of §4.3 starts to hold | - | J8 | from `FarParked`: L8 at priority 0 first, then `Excluded` |
| L12 | `Excluded` | `Live` | no row of §4.3 holds | - | J8 | judged again on the next pass |
| L13 | any | (gone) | `LotLeave` (bulldoze, world shutdown) | - | J8 | `FarHold.Release(lot)` first, **no catch-up** (the lot is being destroyed), record and snapshot dropped in the call |
| L14 | every far state | `Live` | flush: save, travel, world change, quit, switch off, stand-down | - | J8 | L8 + L9 for every record, unthrottled (§9.4) |
| L15 | `FarParked` | `FarParked` | record more than one sim day behind on a read (exposure table, shadow compare) | - | J1 .. J11 | one evaluation of `F(t)`; no side effect |

### 5.2 Reasons (`why`)

`far`, `near`, `simArrive`, `simGoLive`, `extWrite`, `scope`, `lotLeave`, `observe`, `read`, `fire`, `save`,
`travel`, `quit`, `switchOff`, `standDown`, `fallback`, `blockGone`.

### 5.3 Guards

| Guard | Holds when | If it fails |
|---|---|---|
| `G-excl` | no row of §4.3 | L11 / stays `Excluded`; counter `farobjExcl.<row>` |
| `G-sim` | no EA-owned Sim on the lot, routing to it, inside a portal on its edge, or within `R_pulse`; every Sim whose home or `place` is this lot is model-owned or absent (docs/331 rule 3) | stays `GoFarPending`; `farobjBlock.sim` |
| `G-safe` | no running interaction targets an object of the lot; no reservation or use list holds an EA-owned Sim; no entry of the lot's objects in `mAlarmOneShotList` (docs/346 §2); no pending door close or dealer task (docs/348 §2.3); no live fire; not frozen (§11) | `FarLight`; `farobjBlock.safe` |
| `G-model` | every armed timer of the lot and every own-thread object on it has a machine that allows parking: not K5 unread, not E1, not a rabbit-hole class, not an unhooked own-thread class, not a repeating K4 timer without a chore mapping (Rodney 2026-11-26), not in `[FarObj] ClassesOff`; `farhold` on | `FarLight`; `farobjBlock.<ui / e1 / rabbitHole / ownThread / handoff / classOff / hold>` |

`G-safe` is what makes the 120 interaction- and situation-scoped census rows a non-issue (§6.4): with no running
interaction and no situation on the lot they are not armed. A timer of such a class found armed at park fails
`G-model` (`farobjBlock.scoped`) instead of being guessed at.

## 6. Object machines by census model hint

### 6.1 Rule: every census row has a route and a machine

First match, per row of the census CSV:

| Step | Census fact | Route | Machine | Sites |
|---|---|---|---|---|
| 1 | hold class "Global, Sim owner"; unresolved owner that is a Sim (14 rows, §6.9) | far Sim | docs/345 §6.5 | 427 |
| 2 | "Global, non-object owner"; "Global, no owner" in an FS group | EA | never held | 234 |
| 3 | "Global, no owner" in an FO or World group | §6.7 | G-W 65, G-F 14, G-T 11, interaction-scoped 2 | 92 |
| 4 | lot manager, explicit lot manager, "Global, object owner", unresolved owner that is an object or a lot (26 rows) | lot | steps 5-8 | 586 |
| 5 | class is a named class of docs/331 §4.0 | lot | named model (§6.2) | 47 |
| 6 | group "FO rabbit holes, doors, shells, base objects" and not a door, gate, elevator, stair, fence, shell or hidden-room class | lot | NF: `FarLight` only (§6.5) | 93 |
| 7 | declaring type is a nested class (interaction, posture, helper) or the group is an FS group | lot | X0: not armed on an eligible lot (§6.4) | 118 |
| 8 | else by model hint | lot | K1 closed form 134, K2 closed form + event 71, K3 seeded 53, K4 Sim hand-off 26, K5 shows UI 29, K6 generic 15 (§6.3) | 328 |

427 + 234 + 92 + 586 = 1,339. The per-class result is `docs/data/far-object-class-machines.csv`. Steps 6-8 use the
census heuristics (docs/346 §4.1) and are a starting table [inferred]: FO-6 reads a class before it gives it a
dedicated model, and a class that proves wrong moves to `[FarObj] ClassesOff` until fixed.

### 6.2 Named models (47 sites): states and transitions

A model is a pure function of the object row (J4), the tuning (J5), the history ring (J6) and t. "Edge" = a state
change the model computes; none of them fires anything while parked. Registration lines: docs/346 §4.4 [verified
there]. Formulas: docs/331 §4.4-§4.8 [inferred there, tuning names verified]. Go-near API names are owed by FO-6
(Q8).

| Class (hint) | States | Edges | Draws (§8.3) | Go-near |
|---|---|---|---|---|
| `Plant` (seeded) | growth stage x {dry, damp, wet} x {weeds, none} x {dormant, awake}, `Dead` | daily at 05:00 + offset: progress, stage up, water decay, rain from J6, frost dormancy, weed roll, death | `weed` per day; `autoGrow` per day on auto-grow lots | fields through EA setters, stage events once each in order, `Dead` through `SetDead`; daily timer Rearm |
| `HarvestPlant` (closed form + event) | `Growing`, `Harvest(n, quality)`, `Faded` | harvest entry from the plant model; fade after the seeded duration | `yield`, `quality`, `fadeDays` | net harvestables created by EA's code; faded ones never created (compaction); timers Remove or Rearm |
| `ObjectSpawner` (seeded) | multiset of items alive, capped | spawn roll chain, delete-untaken at capacity | `spawnDelay`, `spawnOk` per roll | net set created by EA's spawn code (EA picks the spot); alarm chain re-armed to the model's next roll. Never persisted by EA, re-created after load |
| `Wildflower` (closed form + event) | `Alive`, `Dead` | lifespan end; winter boundary from J6 | `lifeJitter` | `Dead`: EA's death path; else Rearm. The hourly spawn pulse is §6.7 |
| `PreparedFood` (closed form) | spoil stage 0 .. n | stage every base time x fridge multiplier (J11) | none | one `ForceSpoil` per missed stage, in order; chain Rearm |
| `ServingContainer*` (closed form) | `Clean`, `Dirty` | dirty after `kMinutesUntilDirty` | none | `AddDirtyCallback` once; Remove |
| `TrashPile`, `LitterPile` (closed form) | age class fresh / old / ancient, stink | one-shots | none | each missed one-shot once, in order |
| `FishingSpot` (closed form) | `Active`, `Inactive`, `Depleted` | by hour | none | state set; daily timers Rearm. Never persisted by EA |
| `FishTank`, `FishBowlBase`, fish (seeded) | dirt level, per fish {fed, hungry, dead} | dirt steps, hunger, kills | `dirtExtra` per fish per step, `unfedSkew` per fish, `victim` per kill | dirt through the setter, deaths through EA's kill callback in order |
| `PuddleManager` (closed form + seeded spread) | tiles alive | evaporate, spread | `spread` per step | evaporated tiles removed, spread tiles added through EA's code. Its one-shot has no owner on the lot manager [verified, docs/346 §4.4] |
| `Fire`, `Fireplace`, `FireManager` burnt-tile row (seeded, switch `farobjfire`) | `None`, `Smolder`, `Burning(front)`, `Out` | fireplace catch-fire check, spread, burn, go-out, rain from J6, firefighter arrival (service delay tuning) | `catch` per check, `spread` per tile per step, `goOut` per check | burn and destroy events in order; a fire still burning is created by EA's `AddFire` at the computed tiles; near trigger 8 (E2) promotes early. With `farobjfire` off, a lot with a fireplace whose catch-fire timer is armed fails `G-model` |
| `TreeOfProsperity` (own thread, §6.6) | fruit index | every `kHoursToTreeReset` | none | fruit index set, 4 h alarm Rearm |
| `MinorPet` (own thread, §6.6) | age in days, `Dead` | daily `DeathUpdate` | [pending docs/348 Q3] | age set; death through EA's path |

Each model has a no-gaps duty: FO-6 lists every writer of the fields the model computes (grep of the field names in
the three gameplay assemblies) and shows that each is an interaction (blocked by `G-safe`), a held timer, or a
filtered walker. An unlisted writer fails the class selftest, and the class goes to `ClassesOff`.

### 6.3 Long tail (328 sites): six generic machines

The long tail has no per-class state. Its state is the **fire list**: for each armed timer row, the nominal due ticks
inside (`parkStart`, t], computed from the timer's own due and repeat data. `k` = number of entries. Release actions
are defined in §9.2.

| Machine | Census hint | Sites (classes) | While parked | Release action at go-near |
|---|---|---|---|---|
| K1 `Elapsed` | closed form | 134 (82) | fire list | one-shot (88): **Leave**. Repeating, visual only, flags `vfx` or `vfx+alarm` (11), and `Lot.RunTriggerVolumeCallbacks` (1, §6.7): **Rearm**. Other repeating (34): **Replay(k)** |
| K2 `ElapsedEvent` | closed form + pending event | 71 (49) | fire list | one-shot (55): **Leave** (EA's callback creates or destroys once). Repeating (16): **Replay(k)** |
| K3 `SeededEa` | seeded schedule | 53 (41) | fire list | one-shot (28): **Leave**. Repeating (25): **Replay(k)**. The callback draws from EA's game RNG when it runs; the machine draws nothing and tracks no result |
| K4 `Handoff` | Sim hand-off | 26 (16) | fire list; a callback with a docs/345 chore mapping (`CheckMail`, `EmptyTrash`, `GardenOutside`, docs/331 §4.2) becomes a proposal at its nominal tick (L7) | one-shot (20): **Leave**. Repeating (6: `BabyMonitor` 2, `BonehildaCoffin`, `Glass`, `ShowStage`, `Sprinkler`): with a chore mapping, **Rearm** after the proposals; without one the timer **blocks parking while armed** (`G-model`, `farobjBlock.handoff`), so the lot stays at `FarLight` and EA fires it on time (Rodney 2026-11-26, §17 Q3). It is never replayed: a replayed push would land on a Sim that is not there, or wake a far Sim through `eaPush` (docs/345 §5). None of the six has a mapping today |
| K5 `UiCheck` | shows UI | 29 (14) | - | **blocks parking while armed** (`G-model`), until the row is read. Read result per row: UI only for the active household = the row moves to its second hint (K1 .. K4); UI for anyone = E1, stays a block |
| K6 `Generic` | generic (yielding or task, body not resolved) | 15 (8) | fire list | **Leave**: one fire through EA's own yielding path after release |

- **Leave** is exact in end state for a one-shot whose effect does not depend on its fire time: it fires once, after
  release and before the lot can be seen [inferred per class; FO-13's calibration checks it].
- **Replay(k)** keeps docs/331's generic rule. Cap `ReplayCap` (default 32) fires per timer; fires above the cap are
  not replayed and are counted per class (`farobjReplayCapped`). A class that hits the cap in the calibration run needs a
  dedicated model. Limits, stated plainly: a replayed callback reads the current clock, not its nominal tick, and
  K3 outcomes come from EA's RNG at go-near, so a K3 class is as random as in vanilla and not replayable; the
  **trace** stays deterministic because it holds fire lists only.
- K5 first reads, by sites: `CrystalBall` 8, `ChickenCoopClassic` 3, `IndustrialOven` 3, `MeteorShower` 3,
  `DogDigHole` 2, `DrillHole` 2, eight classes with 1 (CSV filter `machine` = K5). `Plant`'s UI flag is already read: household plants only
  (docs/346 §4.4).

### 6.4 X0: interaction- and situation-scoped rows (118 + 2 sites)

Registered inside an interaction, a posture or a situation (nested declaring type, or an FS group class that puts a
timer on an object's lot). They exist only while a Sim uses the object or a situation runs on the lot. `G-safe`
keeps such a lot out of `FarParked`, so no model is needed. Exception path: one found armed at park blocks the park
(§5.3). The two no-owner rows `KissingBooth+AttendingKissingBooth` and `SoccerGoal+ShootOut` are the same case on
the global manager.

### 6.5 Rabbit holes (93 sites, 25 classes): `FarLight` only in model version 1

Rabbit-hole lots do not park in version 1:

- EA careers, school, services, story progression and NRaas push Sims into them at any hour through direct calls;
  docs/345 keeps `FarWork` behind its own switch and lets EA run shifts first.
- Their timers are show, game and event schedules that household Sims attend and that map tags and tooltips read
  (11 "shows UI" rows; `Theatre` 15 sites, `Stadium` 18).
- They are few per world (about 15-25 [inferred]) and do not grow with residential lot count.

They do take `FarLight`: a rabbit-hole lot is a commercial lot, so `ShouldSimulate` makes it loop and pulse every
tick even when empty [verified, docs/348 §2.1]. That is the larger part of its cost and needs no model. Parking them
(a closed-form schedule per class) is Q6.

Doors, gates, elevators, stairs, fences, shells and hidden-room markers in the same census group are ordinary lot
objects and follow step 8.

### 6.6 Own-thread objects (docs/348)

| Class | While the lot is `FarParked` | At go-near | If the pause hook is off or missing |
|---|---|---|---|
| `BirdCage` | body skipped | resumes | `G-model` fails: `FarLight` |
| `MinorPet` and subclasses | body skipped | age in days set; death through EA's path | same |
| `TreeOfProsperity` | body skipped, sleep at most one boundary pass | fruit index set | same |
| `CrowdMonsterUpdate` | `Update` skipped | resumes | same |
| `HoloDisc.Update` | skipped | resumes | same |
| `MarineLifeBase`, `LiteDriver` | live: world lot and world traffic | - | - |
| any other class with its own task (mods) | - | - | X12 or `G-model` |

The hooks ask `FarHold` for the parked set (docs/348 §3); they hold no rows. In `FarLight` they are not paused:
`FarLight` skips pulses only. `LiteDriver` reads `StreetParking` of commercial lots, parked ones included: parking
changes no EA data, so it stays readable [verified by construction].

### 6.7 The global no-owner rows of docs/346 Q4 (93 there, 92 after the correction)

`AlarmManager.Global` with `ObjectRef` null: docs/346 never holds them. Static or instance was read from the callback
declaration for all 93: 59 static, 34 instance [verified by scan of the decompiled sources; mode per row inferred
from the name and the census flags unless a line is cited].

| Mode | Rows | Classes | Handling |
|---|---|---|---|
| **G-W** world or household singleton | 65 | `ResortManager` 12, `HolidayManager` 5, `Mailbox` 5 (bills, book club, blog: static alarms [verified, `Mailbox.cs:2380-2385`]; active household only by their names [inferred]), `LunarCycleManager` 4, `TimePortal` 4, gnome spawners 11, `RealEstateManager` 3, `LotManager` 3, `Meteor` / `MeteorShower` 3, phone 3, `CarpoolManager` 2, `TutorialNpcManager` 2, `Commands` 2, anniversaries 2, `StocksManager`, `KrakenManager`, `SeasonsManager.DoTutorial`, `BuyModeSuite` | stays EA's: one timer per world or per household, not lot work (docs/331 E5 reasoning). If its callback creates, moves or destroys an object on a parked lot, near trigger 4 (`extWrite`) promotes that lot before the object lands. Rows to read for that: `LotManager.CleanUpLotsAndInventories`, meteor strikes, gnome spawns, `LunarCycleManager.PossiblySpawnZombie`, trick-or-treaters (Q9) |
| **G-F** global walker | 14 | `Wildflower.OnGrowWildflowersOnLot`, `DaylightLight.OnUpdateLighting`, `Car.TurnAllCarHeadlightsOn` / `Off`, `GameObject.AppreciationDepreciationCallback`, `HolidayHouseLights.TrickleOnHouseLights`, `LaundryManager.ClearAllGeneratedClothingPileCounts`, shop registers and `BakeSaleTable` (7) | a prefix on the per-lot or per-object step skips parked lots; the model computes the missed effect and applies it at go-near. Switch `farobjwalkers`. Until a walker's filter is built, its effect lands on parked lots as EA writes it and its class is not model-owned (one writer per class per lot) |
| **G-T** instance callback | 11 | `FruitVeggieStand` 3, `CityHall+ScheduleFireworkShow` 3, `RelicStoreRegister`, `EggHuntController`, `Sprinkler.ProcessSprinklerCallback`, `Cabinet`, `MarineLifeSurfaceSpawner` | target-owner rule, a proposed extension of docs/346 §3.3: when `ObjectRef` is null **and** the callback's delegate target is a `GameObject` whose lot is parked and passes docs/346 §3.6, the timer is held on the global manager's side list like an object-owner row. A null or non-object target is never held. Switch `farobjtarget`, default off. `CityHall` is a rabbit hole and `MarineLifeSurfaceSpawner` sits on the world lot: never parked, so the rule never applies to them |
| lot manager (corrected census row) | 1 | `Lot.RunTriggerVolumeCallbacks` (0.1 sim-minute repeat per lot with trigger volumes) | registered on the lot's own manager [verified, `Lot.cs:9668`]: not simulated while parked. With no Sim on the lot it has nothing to do; **Rearm** at go-near (K1 in the class table) |
| X0 | 2 | §6.4 | - |

G-F walkers in detail:

| Walker | Period | Effect | Model for a parked lot |
|---|---|---|---|
| `Wildflower.OnGrowWildflowersOnLot` | 1 h in spring | pops the first lot id of `sLotsToGetWildflowers`, creates one random wildflower and places it on that lot; the active household's home lot is put first [verified, `Wildflower.cs:434-460`] | prefix: if the first id is a parked lot, pop it as EA would and log a pending event (J10: tick, lot, `wildflowerSpawn`). At go-near each pending event runs EA's own create and place code once, in time order; an event whose flower would already be dead (lifespan, winter) is dropped (compaction). The flower type is EA's draw at go-near |
| `DaylightLight.OnUpdateLighting` | `kHoursBetweenLightUpdates` | brightness from the hour, written to every daylight light [verified, `DaylightLight.cs:44-58`] | closed form of the hour: set once at go-near |
| `Car.TurnAllCarHeadlightsOn` / `Off` | daily | headlights of every car | closed form of the hour |
| `GameObject.AppreciationDepreciationCallback` | daily at 0:00 [verified, `GameObject.cs:3363`] | value change of objects | closed form of days elapsed [pending FO-6 read of the body] |
| `HolidayHouseLights.TrickleOnHouseLights` | 1 h | turns house lights on, lot by lot | seeded: `houseLights` draw per lot per holiday |
| `LaundryManager.ClearAllGeneratedClothingPileCounts` | daily | resets a per-lot or per-Sim count | closed form: 0 after any midnight |
| registers, `BakeSaleTable` | daily | restock, demand | seeded restock at go-near, or leave to EA's next daily fire (Q10) |

### 6.8 Lot-level work and the classes with their own package

- **Reaction pulses** (`FarLight` and `FarParked`): skipped. With no EA-owned Sim within `R_pulse` a pulse has no one to
  act on [verified shape, docs/331 §3]. Model-owned Sims get their exposure from the table of docs/331 §4.1, built
  from the object models' timelines and read by docs/345 at a Sim's go-live. Pulse timers are reset at L6 / L9, so
  there is no burst.
- **Door privacy**: follows the Sims on the lot; EA's next pass recomputes it [inferred, I-585-3].
- **Zombies**: `ZombieDanceTimeOut` decrements by elapsed time (closed form); the dance stereo is a pending event,
  created through `AddZombieDanceStereo` at go-near if the condition still holds (docs/331 §4.10).
- **Fire** (FO-11, `farobjfire`), **traffic** (FO-12, `farobjtraffic`), **seasons lot effects** (FO-12; the world
  loop stays, docs/331 E5): as docs/331 §4.6-§4.8 with the seed of §8. No new fact here.

### 6.9 The 40 rows the scan could not type

Typed here from the owner expression in the CSV [inferred from the name; FO-6 confirms on first use]: 14 Sim owners
(`shark`, `child`, `mom` x2, `guestOfHonor`, `createdSim2`, `simFromDescriptionId`, `instanceActor`, `activeActor`,
`worker`, `host` x4) go to docs/345; 26 object or lot owners (`this`, `target`, `Owner`, `mObj`, `Container`, jigs,
`urnstone`, `lot`, `lotCurrent`, `lotHome`, `hole`, `item`) follow steps 5-8 of §6.1.

## 7. Evaluation, due queue and catch-up

### 7.1 `F(t)`: state as a pure function

`F(seed, snap, inputs, t)` = for each object row its named model evaluated at t, for each long-tail timer row its
fire list up to t, plus the pending events with a nominal tick <= t, compacted. No other data is read. An object
model that depends on its own history (the plant's weed feedback) runs its day loop from `parkStart`; the loop is
part of the function, not stored state.

### 7.2 Due queue

A parked lot needs a scheduled edge only for effects outside the lot: a chore proposal to docs/345 (K4 with a
mapping), a fire front check (E2), and an exposure row that a model-owned Sim's block will read. Lots without any of
these have `nextDue` = none and cost nothing until go-near.

- One binary heap of (`nextDue`, lot id), the heap type of docs/345 §7.2, drained once per sim tick on the sim
  thread, at most `DueBudget` (default 8) per tick, a count and never a time.
- A transition is logged with its nominal due tick.

### 7.3 Catch-up bound

The machine never replays its own missed edges. Worst case per lot at go-near, whatever the far time:

| Step | Bound |
|---|---|
| named models | one evaluation per object row; plant and tank loops at most `CatchUpDaysMax` (default 28) day steps, older spans use the last 28 days from a steady state [inferred; Q11] |
| long-tail fire lists | arithmetic per timer row; EA callbacks at most `ReplayCap` per timer |
| pending events | at most `EventsPerLotMax` (default 64) after compaction; creates at most `CreatesPerFrame` (default 4) per sim frame |
| timer handle calls | one `UpdateAlarmTime` or `RemoveAlarm` per timer row |

Go-near work runs in the spread queue at `GoNearOpsPerFrame` (default 16) operations per sim frame, none in a frame
already over 25 ms; one lot is the unit of priority, one operation the unit of work; a lot stays `PendingLive` until
its last operation, then L9 in one step. Priority-0 go-nears and the save flush are unthrottled. FO-4's selftest
reports microseconds per `F(t)`; FO-8 reports ms per go-near by lot size.

## 8. Seed and PRNG

### 8.1 Mix function

`M` of docs/345 §8.1 (SplitMix64 finaliser), unchanged. `M(0)` = `e220a8397b1dcdaf`.

### 8.2 Seed and draw

```
seedObj   = M( M( M( M(worldSeed) xor lotId ) xor objectId ) xor modelVersion )
draw      = M( M( M( seedObj xor slot ) xor dueTick ) xor k )
range(n)  = ((draw >> 32) * n) >> 32          integer in 0 .. n-1, n < 2^32
```

- `worldSeed`: the docs/343 `W.seed`, shared with docs/345. `modelVersion` = `mv.fo`.
- `objectId` = 0 for lot-level draws (house lights, zombie stereo).
- `dueTick` = the **nominal** due tick of the edge the draw belongs to. It is unique per fire of a timer, so no
  running index and no park episode counter exist; a lot parked twice draws different numbers because its ticks
  differ, and nothing has to be carried across a save.
- `k` = index of the draw inside one edge (tile index of a fire spread, fish index of a dirt step); 0 otherwise.
- No draw comes from `RandomUtil`, `RandomGen` or `System.Random`; a chance is compared as an integer against
  `draw >> 32`. EA's own draws happen only inside EA callbacks at go-near (K3, spawner spots, flower type).

Test vector (FO-4 selftest must reproduce it): `worldSeed` = `9c41d2e07b53a611`, `lotId` = 6493412208435789842,
`objectId` = 1111198233795652, `modelVersion` = 1: `seedObj` = `3fabc6462c6bae00`; `draw(slot 0x21, dueTick 1834200,
k 0)` = `3ca939b5cab4eeb8`; `range(100)` = 23. Lot-level seed (`objectId` 0) = `18be7f66530778c6`.

### 8.3 Slots

| Slot | Name | Used by |
|---|---|---|
| 0x10, 0x11 | `weed`, `autoGrow` | `Plant` |
| 0x12, 0x13, 0x14 | `yield`, `quality`, `fadeDays` | `HarvestPlant` |
| 0x20, 0x21 | `spawnDelay`, `spawnOk` | `ObjectSpawner` |
| 0x28 | `lifeJitter` | `Wildflower` |
| 0x30, 0x31, 0x32 | `dirtExtra`, `unfedSkew`, `victim` | fish tanks |
| 0x38 | `spread` | `PuddleManager` |
| 0x40, 0x41, 0x42 | `catch`, `spread`, `goOut` | fire |
| 0x50 | `houseLights` | `HolidayHouseLights` |
| 0x58 | `restock` | registers |
| 0x60 .. 0x6F | traffic (FO-12) | reserved |

A new slot or a changed model bumps `mv.fo` (docs/343 §4): the seed changes. Nothing else has to be discarded,
because the model keeps no carry-over.

## 9. Park and go-near

### 9.1 Park (L3, L5)

On the boundary pass, on the sim thread, after all four guards hold:

1. EP2 check (§10).
2. Read the lot's timers from its alarm manager, read only (the reflection path of `AlarmKillLog.cs`, docs/346 §6),
   and the global manager's timers whose owner is an object of this lot: one timer row each, machine id by §6.1.
   A row with no machine, a K5 row, an E1 row or an X0 row fails `G-model`: L4 instead.
3. Read the object rows of the named classes (§6.2) and the J11 predicate answers; read the footprint grid when
   `farobjfire` is on; read `R_pulse`.
4. `FarHold.Park(lot)` (docs/346 Q1 name). From here the lot's manager is not simulated, object-owner timers on the
   global manager are taken as they come due, and the pause hooks see the lot as parked.
5. Record `parkStart`, compute `nextDue`, log the trace line.

**Nothing in EA's state changes at park**: timers stay in EA's queue with their due times, objects are untouched.

### 9.2 Go-near catch-up (L8, L9): compute once, apply through EA

Order inside one lot. Every step is one or more spread operations (§7.3).

1. EP3 check. Evaluate `F(now)` once (§7.1).
2. **Fields**: write the computed fields of the named models through EA's setters (plant stage and water, food stage,
   dirt, fruit index, pet age, light state).
3. **Events**: run the compacted pending events in (nominal tick, object id) order through EA's own code: creates
   (harvestables, spawner items, wildflowers, puddle tiles, zombie stereo), destroys, `ForceSpoil` per stage, kills,
   burns. A create and its own later destroy inside the parked span cancel out and never run.
4. **Timers**: for each timer row that still exists (handle still valid), in (due tick, handle id) order, one of:

   | Action | EA call | Used when |
   |---|---|---|
   | **Leave** | none | EA fires it once on the lot's next `Simulate` (EA's late-repeat rule, docs/346 §3.4) |
   | **Rearm** | `UpdateAlarmTime(handle, next period boundary after now)` | the model produced the effect, or the effect is visual only, or it is a K4 repeat with a chore mapping |
   | **Remove** | `RemoveAlarm(handle)` | the model produced a one-shot's effect (dirty, spoil, fade, death) |
   | **Replay(k)** | the timer's own callback, min(k, `ReplayCap`) times, then **Rearm** | K1 / K2 / K3 repeating rows |

   Replays of all timers of the lot are merged into one time order. **Re-arm bracket**: before each replayed call the
   lot manager's handle set for the callback's owner is recorded; a handle added by the call is removed again unless
   the call is the last replay of that timer. A long-held short-period timer is always re-armed (I-614-4,
   docs/346 §3.4), never left to EA's one-period-at-a-time loop.
5. **Own-thread objects**: computed state applied (§6.6).
6. Pulse timers reset. `FarHold.Release(lot)`. Record, snapshot and due entry dropped. Trace line. The lot is `Live`
   and parks again only after a full dwell.

Seen-in-view guard (docs/331 §6.3): near trigger 1 fires at the pre-expand ring, so steps 2-5 normally finish out of
view. A lot that is already in view (camera jump, near triggers 2-7) runs them at once at priority 0 and uses EA's own fades
where EA has them; `farobjCatchupInView` counts these. Unlike docs/331 there is no real-time deferral: the lot is
never shown stale after its first running tick. During a pause nothing runs (§11).

### 9.3 Fallback (L10)

If a step throws or an EP check fails mid-way: stop the lot's remaining model steps, **Leave** every remaining
timer, release the lot. EA then fires each overdue timer once: vanilla lag semantics, the state EA itself produces
after any long stall. One log line, `farobjGoNearFallback`. Above `FallbackMaxPerDay` (default 5): stand-down (§14).
The class that threw goes to the session's `ClassesOff`.

### 9.4 Save flush, and the big-world save path

- **Flush mode** (`[FarObj] FlushAtSave=1`, default, every EA world): before `OptionsModel.SaveGame`, from the
  shared flush hub (docs/343 §6, `DisposeSaveHook` precedent), L14 for every record, unthrottled behind the save
  screen. The `.sims3` holds current EA state. `FarHold`'s Hook B (docs/346 §3.5) still brackets `Write` for the
  object-owner timers on the global manager's side list. After the save every lot is `Live` and parks again after a
  dwell. Mod save paths that skip the hub: parked lots write untouched, overdue EA timers (docs/331 I-585-5); logged,
  `farobjSaveWhileParked`.
- **Cost of the flush grows with the number of parked lots**, so it does not meet the unbounded-world rule. The
  no-flush mode is not designed here. Rodney 2026-11-26 (Q1): the full flush stays on EA worlds, and big worlds use
  the paged-lot save of Boundless Atlas `docs/007-paged-lot-save.md` (Atlas commits 6b4c6f3 and ca1ec5c; not read
  for this doc, cited as relayed): pages live outside `Saves`, under the docs/343 sidecar root `ts3native\lw\<key>\`,
  and far lots that load unfurnished when the mod is missing are accepted. What this machine gives that path: a
  parked lot's EA state is untouched from `parkStart` on, and the lot record holds ids and `parkStart` only.
  `FlushAtSave=0` stays reserved for it and is refused by this build.

## 10. Household, selected Sim and unknown scope: every entry point

The test is the shared predicate (`Fixes.HouseholdExempt` in ts3fixes, `HhExempt` in ts3native) plus "scope known",
applied to the Sims and objects of §4.3 rows X1-X8. `[Experimental] HouseholdToo=1` is **not** read at any of these
sites: a park is a skip, a replay or a pending event is a fake (docs/331 rule 1, docs/346 §3.6). A stale household
table makes every lot `Excluded` (X8).

| # | Entry point | Check | If it fails |
|---|---|---|---|
| EP1 | boundary pass, before L1 | §4.3 X1-X8 | L11; `farobjExcl.<row>` |
| EP2 | L3 / L4 / L5, on the tick of the park or pulse skip | X1-X8 again, per object for X5 | L2; `farobjHhBlockedAtPark` |
| EP3 | due-queue drain (L7, L15) and every catch-up step (§9.2) | X1-X8 | L8 at priority 0, reason `scope`; `farobjHhCaughtParked` (must be 0: it means EP6 missed an event) |
| EP4 | pulse skip, per call of the three lot hooks | lot is not X1-X3, re-read on the call | body runs; lot leaves `FarLight`; `farobjHhPulseSkip` (must be 0) |
| EP5 | `FarHold` Hook A, per call | docs/346 §3.6: world lot, active lot, home lot re-checked by the hook | manager simulated; the lot is removed from the parked set and logged |
| EP6 | scope hooks: `ScopeChange`, household change, selection change, active household switch, travel start, a household Sim starts a route (destination lot), `ObjectEnter` of a household-owned object | any change touching a recorded lot | L8 at priority 0, then `Excluded` |
| EP7 | own-thread pause hooks and the G-T target-owner rule | object's lot parked and not X1-X5; no lot, in an inventory, or lot unreadable = not paused, not held | body runs / timer fires on time |
| EP8 | G-F walker filters | skip only for a lot in the parked set that passes EP5's test | walker runs as EA wrote it |
| EP9 | chore proposals and exposure rows handed to docs/345 | the Sim is model-owned there (its own EP3) | proposal dropped |
| EP10 | sidecar writer and reader | docs/343 §8 | line refused; `farSidecarHouseholdRefused` (must be 0) |

Household-facing timers on a non-household lot (docs/331 E1) never reach a parked manager: an armed E1 timer fails
`G-model`, so the lot is at most `FarLight` and the timer fires at its real time through EA.

## 11. Pause, CAS, Build / Buy, Edit Town, load, travel, quit

Shared helper `SimFrozenOrNotLive()`, as docs/345 §11.

| Game mode | Boundary pass | Due queue | Park / go-near | Allowed |
|---|---|---|---|---|
| live, running | runs | runs | runs | everything |
| paused | no | no | no | hooks queue J7 / J8 by lot id; drained in (tick, lot id) order on the first running tick; `farobjSkippedPaused` |
| CAS, Build / Buy, Edit Town | no | no | no | as paused. Entering Build / Buy or Edit Town on a lot is near trigger 6, queued. Hub events (`LotLeave`, `ObjectLeave`) still arrive and only **drop or release**, never park (docs/348 §6.1) |
| loading screen, world load | no | no | no | tables reset; sidecar read; `CleanOnLoad` (docs/343 §7): no lot may be in `FarHold`, count-only mode first |
| save in progress | no | no | flush only (§9.4) | flush |
| travel start, quit to menu, world shutdown | no | no | L14 reason `travel` / `quit`, then `FarHold.ReleaseAll()`, then table reset | release and reset only |
| main menu | no | no | no | nothing; tables empty (`farobjRecords` = 0) |

**No go-near while paused, no exception** (the docs/345 §11 rule, Rodney 2026-11-26). A camera move while paused can
bring a parked lot into view before its catch-up ran: the lot shows its park-time state (food not yet spoiled, plants
not yet grown, no wildflowers) until the first running tick, when the priority-0 catch-up runs.
`farobjLateAtUnpause` counts lots whose catch-up ran at unpause while on screen. EA's pause shows the same kind of
frozen picture, so nothing moves while paused in either case. A `FarLight` lot shows nothing different: its state is
EA's.

## 12. Sidecar fields (docs/343)

| docs/343 line | What the far-object machine writes | Read back in game |
|---|---|---|
| `W` | `mv.fo` = 1; `seed` is shared with docs/345 | yes |
| `K` | `parkEdgeM` (per-world park radius, `src` = FO-2's queue id) | yes |
| `H` | history ring entries the models read (J6): weather, frost, season, holiday | yes; only needed for spans that are still parked, so in flush mode a load starts with an empty need |
| `E` | one line per logged lot transition: `ticks` (nominal), `id` = lot id, `ti`, `from`, `to`, `lot` = same id | never (DT-1, diagnosis) |
| `S`, `D` | nothing | - |
| `A` (repair file) | only if Hook B could not put a held object-owner timer back before `AlarmManager.Write` (docs/346 §3.5) | repair pass only |
| `P` (repair file) | nothing at L0 | - |

- No per-lot or per-object line in flush mode: the save holds current EA state and the PRNG needs no position (§8.2).
- No new line type is proposed: the no-flush save path and its pages are Atlas `docs/007-paged-lot-save.md` (§9.4).
- Recorded inputs J7-J10 are **not** in the sidecar. They go to the `[Diag] FarTrace` file (§13.1).
- Snapshot, fire lists, pending events, due queue and ownership table are never written.

## 13. Determinism tests, counters, leaks

### 13.1 Trace and hash

Trace line = (nominal tick, lot id, object id or 0, from, to, why). Lot transitions use the state ordinals of §4.1
with object id 0; model edges of named classes (stage up, spoil stage, death, spawn, burn) are logged at go-near with
their nominal ticks, object id set and class-local state ordinals. Hash as docs/345 §13.1 over the first five fields in
(tick, lot id, object id) order. Example: one line (1834200, 6493412208435789842, 1111198233795652, 3, 2) gives
`816e21d7c4a13fbf`. `[Diag] FarTrace` (shared, default 0) writes trace and input lines and the hash once per sim hour.

### 13.2 Tests

No test compares against a live EA run draw for draw (docs/331 §4: vanilla is not repeatable either). Distributions
are judged by FO-13's shadow calibration.

| # | Test | Where | Pass |
|---|---|---|---|
| D1 | PRNG test vector of §8.2 | FO-4 selftest | equal |
| D2 | fixed seed, fixed input file, run twice | FO-6 selftest | same trace hash |
| D3 | lots evaluated in shuffled order; `DueBudget` 1, 8, unlimited; `GoNearOpsPerFrame` 1, 16, unlimited | DT-1 | same hash |
| D4 | lazy against stepped: `F(t)` once at t against the same model advanced edge by edge | DT-1 | same object rows for 1000 seeded lots over 30 sim days |
| D5 | park, block the drain for 1, 3 and 30 sim days, go near | FO-6 selftest | state equals D4's stepped state at t |
| D6 | save round trip in flush mode | FO-5 + FO-8 | `.sims3` of the on arm after a flush equals the off arm's in object count, ids and script record count (docs/344 kill criterion 3 used as a check); alarm count per lot equal |
| D7 | one rewind snapshot run twice with recorded inputs replayed | DT-2 (in game) | trace hash per sim hour equal |
| D8 | model version bump | FO-4 selftest | seed differs; K and world seed kept |
| D9 | static check: model source has no `RandomUtil`, `RandomGen`, `new Random`, `DateTime`, `Stopwatch`, `Dictionary<` keyed by an object type, `Hashtable`, `float` / `double` field | selftest script with regex cases | 0 hits |
| D10 | per named class: writer list of the computed fields (§6.2 no-gaps duty) | FO-6 selftest per class | every writer classified; a new unlisted writer fails |
| D11 | census coverage: every row of the census CSV maps to exactly one row of the class-machine CSV | FO-6 selftest | 1,339 = sum of `sites` |
| D12 | replay bracket: a callback that re-arms itself, replayed 5 times | FO-7 selftest | exactly one timer of that callback on the lot after release |

### 13.3 Counters and leak rules

Slots are reserved at build (`node tools/registry.ts reserve fixes ...`). Names:

| Counter | Meaning | Queue check |
|---|---|---|
| `farobjRecords`, `farobjRecordsHigh` | lot table size, high-water | <= lots in the world; flat across travel and save-reload; 0 at main menu |
| `farobjByState[6]` | lots per state | sum of the far states = `farobjRecords` |
| `farobjTimerRows`, `farobjObjectRows`, `farobjPendingEvents`, `farobjSnapBytes`, `farobjDueSize` | sizes | bounded by `farobjRecords` x per-lot caps; 0 at main menu |
| `farobjPark`, `farobjLight`, `farobjGoNear`, `farobjGoNearFallback`, `farobjFlap`, `farobjCatchUp`, `farobjGone` | events | `farobjFlap` 0; fallback below a few per sim day |
| `farobjBlock.<reason>`, `farobjExcl.<row>` | why lots did not park | read; `block.ui` and `block.ownThread` rank the next reads |
| `farobjLeft`, `farobjRearmed`, `farobjRemoved`, `farobjReplayed`, `farobjReplayCapped`, `farobjEventsRun`, `farobjEventsCompacted` | release actions, per machine | `ReplayCapped` reported per class by FO-13 (the cap itself is accepted, §17 Q2) |
| `farobjParkedHousehold`, `farobjHhCaughtParked`, `farobjHhPulseSkip`, `farSidecarHouseholdRefused`, `fireNearHousehold` | household violations | all 0, hard fail |
| `farobjHhBlockedAtPark` | EP2 refusals | read |
| `farobjOwnerConflict` | V1 / V2 broken | 0 |
| `farobjFiresWhileParked` | V4: a callback of a parked lot's timer ran outside our replay | 0 |
| `farobjSimOnParked` | V3 | 0 |
| `farobjExtWrite` | near trigger 4 by calling site | read; a frequent site is a walker that needs a G-F filter |
| `farobjCatchupInView`, `farobjLateAtUnpause` | visible catch-ups | near 0 in natural runs; M19 expects `LateAtUnpause` above 0 |
| `farobjSaveWhileParked` | a save path that skipped the flush | 0 |
| `farobjSkippedPaused`, `farobjQueuedWhilePaused` | pause gating | flat while frozen, drained after |
| `farobjStoodDown` | automatic stand-down fired | 0 |

`FarHold`'s own counters (held timers, takes, releases, evictions, largest side list) are docs/346 §3.7's.

Leak rules (memory note "no leaks in new builds"):

- A record, its snapshot rows, its pending events and its due entry are dropped together on L2, L6, L9, L10, L13 and
  L14, by one drop function, which also calls `FarHold.Release(lot)`. No other code removes a part.
- Hub events of docs/348 §6.1: `LotLeave` drops the lot's record in the call; `ObjectLeave` drops that object's rows
  and its timer rows in the call (and is near trigger 4 unless it comes from our catch-up); `WorldReset` clears
  every table after `FarHold.ReleaseAll()`.
- Rows hold ids and interned callback ids only: no reference to a lot, an object, a timer or a delegate. A handle
  that is no longer valid at go-near is skipped and counted.
- Caps: `EventsPerLotMax`, `TimerRowsPerLotMax` (default 512; a lot above it does not park), history ring 512
  entries (docs/343 §10).
- Selftests: park / go-near churn of 10,000 lots, 100 load / quit cycles, Edit Town bulldoze and place, sizes back
  to 0.

## 14. Switches and kill paths

All default off. Names follow docs/331 rule 5; registry rows and ini lines are added at build.

| Switch | Off means |
|---|---|
| ts3fixes `farobj` (master) | no table, no pass, no hooks armed. Off at runtime: L14 reason `switchOff` for every record, then reset |
| `farobjlight` | no `FarLight`: L4 never fires, a lot that cannot park stays `GoFarPending` |
| `farobjlot` | no `FarParked`: L3 and L5 never fire; with `farobjlight` on, eligible lots take `FarLight` |
| `farhold` (shared with docs/345) | no lot can park (`G-model`) |
| `farobjtarget` | G-T rows are never held (§6.7) |
| `farobjwalkers` | no G-F filter; walkers write to parked lots as EA does and their classes are not model-owned |
| `farobjfire`, `farobjtraffic`, `farobjworld` | docs/331 rule 5; with `farobjworld` off, object-owner timers on the global manager are not held and their owners' lots fail `G-model` only if the class is model-owned |
| `farobjpet`, `farobjcage`, `farobjtree`, `farobjcrowd`, `farobjholo` | that class has no pause hook: a lot holding one takes `FarLight` |
| `[Diag] FarObjShadow` (FO-5) | on = compute and compare only: records and snapshots exist, nothing is parked, skipped or written; `farobjlot` and `farobjlight` are ignored |
| `[Diag] FarObjProbe` (FO-1), `[Diag] FarTrace` | no probe lines, no trace file |
| `[FarObj] ClassesOff=<list>` | a lot with an armed timer or an own-thread object of a listed class fails `G-model`. This is the "drop that class" step of docs/340 §9 |
| `[FarObj] FlushAtSave` (default 1) | 0 is reserved for the Atlas save path and refused by this build (§9.4) |
| `[FarObj]` keys | `ParkEdgeM` 200, `NearEdgeM` 150, `ParkDwellMin` 30, `ReplayCap` 32, `EventsPerLotMax` 64, `CreatesPerFrame` 4, `GoNearOpsPerFrame` 16, `DueBudget` 8, `CatchUpDaysMax` 28 |

Automatic stand-down (sets `farobjStoodDown`, logs once, L14 for everything, no new park until the next world load):
any household counter above 0; `farobjOwnerConflict`, `farobjFiresWhileParked` or `farobjSimOnParked` above 0;
`farobjGoNearFallback` above `FallbackMaxPerDay` in one sim day; `farFlushFailed` above 0; `tuneHash` changed
mid-session. Stand-down by compat rule: a mod assembly that ticks a model-owned class with its own timers
(docs/331 decision 4, `CompatResolve.cs` pattern) puts that class in `ClassesOff`; a mod that walks every
`GameObject` on a timer and writes (NRaas ErrorTrap, Overwatch clean-ups) is a compat row to check before
`farobjlot` is timed (Q12).

## 15. Gain estimate [inferred]

Rough numbers from the measured split of docs/331 §2.1 (nonsim-split-1122, sim-thread ms per wall second, far =
beyond 150 m, household rows apart), scaled by the census counts. docs/322 gives the Sim side: beyond 150 m NPCs own
85-89% of NPC time, so with far Sims off most residential lots keep an EA-owned Sim and cannot park.

| Part | BP | IP | SV | Basis |
|---|---:|---:|---:|---|
| far pulses + zombies / door privacy [measured] | 26.1 | 31.8 | 10.6 | docs/331 §2.1 rows 3 and 5 |
| far lot alarm callbacks + object-state alarms [measured] | 9.9 | 11.9 | 11.5 | rows 6 and 8 |
| **`FarLight` alone** (`farobjlight`): pulses row x 60-90% (needs only `G-sim`; rabbit holes included) | 16-23 | 19-29 | 6-10 | pulses come mostly from commercial lots, which hold few EA-owned Sims at a given time |
| `FarParked`, far Sims off: alarm rows x 40-60% | 4-6 | 5-7 | 5-7 | lots with a resident at home stay live |
| `FarParked`, far Sims on (docs/345 `farsimt3`): alarm rows x 70-90%, minus rabbit-hole and K5 lots | 7-9 | 8-11 | 8-10 | 93 + 29 + 6 of 588 lot sites are blocked in version 1 |
| global rows: `WorldLot` 20.0 / 27.5 / 24.1 + `AlarmOneShot` 7.8 / 9.7 / 12.5, x 10-20% | 3-6 | 4-7 | 4-7 | by census sites, 47 object-owner + 26 G-F / G-T of 535 global sites = 14%; 77% of global sites have a Sim owner and are docs/345's. docs/331 §10 assumed 30-80% |
| own-thread hooks: `OneShotFunction` 10.9 / 2.2 / 3.1, x 0-50% | 0-5 | 0-1 | 0-1.5 | owners unmeasured (FO-1) |
| go-near cost | -1 | -1 | -1 | docs/331 §10 |
| **full model, far Sims on, no traffic** | **27-42** | **34-46** | **18-27** | 2.7-4.6% of the sim thread |
| traffic (`farobjtraffic`, FO-12) | +5-10 | +~1 | +~1 | docs/331 §10 |

- This is below docs/331's realised 35-65 / 35-65 / 20-45 at the top end, because the census moves most of the
  no-position alarm time to Sim owners.
- Site counts are not time shares: FO-1's probe and FO-2's split replace every factor in this table.
- 4x or streamed world: far lots grow about 4x and nearly all are parkable residential lots, so the removable part
  grows with the world while `FarParked` costs nothing per lot without a due edge (§7.2). The flush at save is the
  one cost that still grows with the world (§9.4; Atlas docs/007 for big worlds). By the verdict rule of docs/340 §7, a gain shown statically to
  scale with world size may go ON without a gain on today's worlds.

## 16. Dependencies

| Lane / package | Taken | Still open for this spec | Until then |
|---|---|---|---|
| ST-4 / ST-7a (docs/346) | Hook A / B, `FarHold`, release semantics, the census | API names (ST-15, Q1 there); the target-owner extension of §6.7 is a proposal to ST-15 | G-T rows stay live |
| ST-15 (docs/351, written the same day, read for names only) | final API names: `FarHold.Park(lot)` here reads as `FarParkLot(lotId, owner)`, `FarHold.Release(lot)` as `FarReleaseLot(lotId)`, `FarHold.ReleaseAll()` as `FarReleaseAll(reason)` from `FarHub`, "is this lot parked" as `FarIsLotParked(lotId)` | docs/351 has no target-owner rule (§6.7 G-T) | G-T rows stay live |
| ST-8 / ST-12 (docs/348) | five pause hooks, hub events as J8 inputs | Q3 (`MinorPet.DeathUpdate`), Q5 (lot id reuse in one session: a reused id must not inherit a record; L13 drops in the call, so only a missed `LotLeave` could), Q6 (`ObjectEnter` coverage, which near trigger 4 depends on) | pets block parking; FO-1's probe checks Q6 before `farobjlot` is timed |
| ST-14 (docs/343) | `W`, `K`, `H`, `E` lines | - | - |
| docs/344 | L0 only; object ids stable over save and load | - | - |
| docs/347 | nothing object-side: its setters are for Sim drift | object setter table per named class (Q8) | each named class is shadow-only until FO-6 has read its setters |
| SD-1 (docs/345) | PRNG, trace, pause rule, boundary pass, heap, chore proposals, exposure reads | `eaPush` on a K4 replay (why K4 repeats are never replayed) | - |
| SD-3 | - | pre-expand ring size | 150 m plus the docs/345 working value |
| FO-1 / FO-2 | - | measured shares for §15, `R_pulse` values, how many G-W callbacks write onto far lots | estimates |
| ST-7b (mod census) | - | mod classes | X12 |
| EA 1.69 | docs/346 §5, docs/348 §8 | the five source lines cited here not checked on 1.69 | Steam first |

## 17. Open questions

| # | Question | Who |
|---|---|---|
| Q1 | **Closed (Rodney 2026-11-26).** The full flush stays on EA worlds. The no-flush mode for big worlds is the paged-lot save of Boundless Atlas `docs/007-paged-lot-save.md` (Atlas commits 6b4c6f3, ca1ec5c): pages outside `Saves` under the docs/343 sidecar root `ts3native\lw\<key>\`; unfurnished far lots when the mod is missing are accepted (§9.4) | - |
| Q2 | **Closed (Rodney 2026-11-26).** Accepted: long-tail repeating timers are replayed up to the counted cap `ReplayCap` 32 (§6.3) | - |
| Q3 | **Closed (Rodney 2026-11-26).** A lot with an armed repeating Sim hand-off timer that has no chore mapping (6 sites) stays at `FarLight`; the timer is not re-armed with no effect (§5.3 `G-model`, §6.3 K4) | - |
| Q4 | Target-owner rule for G-T rows (§6.7): does `Timer.CallBack.Target` read cleanly through the reflection path, and is a null `ObjectRef` with an object target ever household-facing? | ST-15, FO-3 |
| Q5 | Does a pulse ever act on something other than a Sim in radius (object-to-object reactions)? `FarLight` is exact only if not | FO-4 brief, before `farobjlight` is timed |
| Q6 | Rabbit holes: closed-form schedule per class (25 classes, 93 sites) so their lots can park, or stay at `FarLight` for good? Decide from FO-2's share of rabbit-hole alarm time | FO-13 |
| Q7 | K5: read the 29 "shows UI" lot rows (14 classes) | FO-6 brief |
| Q8 | EA setter and event API per named class (§6.2 go-near column), and the writer lists of D10 | FO-6, one class at a time |
| Q9 | Which G-W callbacks create or destroy objects on arbitrary lots (candidates in §6.7), and does each pass through `ObjectEnter` / `ObjectLeave` (docs/348 Q6)? | FO-1 probe (`farobjExtWrite` by site) |
| Q10 | Registers and `BakeSaleTable` (7 static daily rows): model the restock or leave it to EA's next daily fire after go-near? | FO-9 |
| Q11 | `CatchUpDaysMax` 28 and the steady-state start for longer spans: acceptable error for plants and tanks? | FO-13 calibration |
| Q12 | Mods that walk all objects on a timer and write (NRaas ErrorTrap, Overwatch): do they touch parked lots' objects in a way a model must see? | compat lane, with ST-7b |
| Q13 | `Lot.RunTriggerVolumeCallbacks`: cost per lot today (0.1 sim-minute repeat) and how many lots have it armed | FO-1 probe |
| Q14 | First values of the `[FarObj]` keys | FO-13 |

## 18. Test matrix (first cut; FO-8 turns it into queue jobs)

| # | Case | Setup | Expect |
|---|---|---|---|
| M1 | household lot far from the camera | camera 600 m from the home lot for 2 sim hours | home lot `Excluded`; household counters 0 |
| M2 | household Sim visits a far lot | send a household Sim to a parked lot | go-near at route start (X4), lot `Live` before the Sim arrives; `farobjHhCaughtParked` 0 |
| M3 | household-owned object on another lot | household car or placed inventory object on an NPC lot | that lot never parks (`farobjExcl.X5`) |
| M4 | unknown scope | force a stale household table | every lot `Excluded`; every record promoted |
| M5 | plain park and go-near | empty community lot, camera away past the dwell, then back | L1, L3, L8, L9; timers on the lot equal in count to the off arm; script errors 0 |
| M6 | `FarLight` | rabbit-hole lot, camera away | L4; pulses 0 on the lot, alarms still fire; L6 on return with no burst of pulses |
| M7 | edge walk | camera oscillates across `R_park` every 5 sim minutes for 1 sim hour | `farobjFlap` 0 |
| M8 | named classes, one job each: plant, food, dishes, trash, fish tank, spawner, wildflower, puddle | repro lot with the object, park 1 and 3 sim days, go near | state equals the model's `F(t)`; events ran in order; one timer per chain after release |
| M9 | replay bracket and cap | K1 repeat with a 1-minute period parked 1 sim day | `ReplayCap` calls, `farobjReplayCapped` set, exactly one timer after release |
| M10 | wildflower pulse | spring, parked lot first in the spawn list | no object appears on the parked lot; pending event logged; flower created at go-near; none if winter came first |
| M11 | external write | cheat or mod call that creates an object on a parked lot | go-near before the object lands (`extWrite`); `farobjExtWrite` names the site |
| M12 | far Sim goes live onto a parked lot | docs/345 M4 with the Sim's home lot parked | lot first, then the Sim; `farobjSimOnParked` 0 |
| M13 | pause, CAS, Build / Buy | 60 s pause, CAS and Build / Buy with lots parked and one due | counters flat while frozen; queued requests drained after |
| M14 | save round trip | save with 20 parked lots, reload | D6; after load all `Live`, then parked again; `farobjSaveWhileParked` 0 |
| M15 | bulldoze a parked lot, place a lot | Edit Town | L13; rows for that lot 0; the new lot starts `Live` |
| M16 | travel round trip, quit to menu 5 times | - | `farobjRecords` 0 at menu; `FarHold` empty; high-water flat |
| M17 | switch off at runtime | turn `farobj` off with 20 parked lots | all go near in one tick; tables 0 |
| M18 | forced fallback | make a catch-up step throw (test hook) | L10; lot `Live`; each remaining timer fires once; `farobjGoNearFallback` 1 |
| M19 | camera moved while paused | park a lot with food on a table; pause; move the camera onto it; wait 30 s; unpause | while paused no transition; at the first running tick the catch-up runs; `farobjLateAtUnpause` 1 |
| M20 | own-thread classes | bird cage, minor pet, Tree of Prosperity, holo disc on a parked lot | bodies skipped while parked; pet age and fruit index right at go-near |
| M21 | stand-down | raise a household counter through a test hook | `farobjStoodDown` 1; everything `Live`; no new park |
| M22 | replay | DT-1 (D2-D5) and DT-2 (D7) | hashes equal |
| M23 | timing A/B, three arms per world | off; `farobjlight` only; `farobjlot` + `farobjlight` + `farhold`, each with docs/345 far Sims off and on; in-load runtime switch, ABBA, 10 min per arm, BP / IP / SV; smoke job first (per-arm split, switch values read back) | sim-thread ms/s from `[Diag] NonSimSplit` by band, off against on, plus hitch p99 / p99.9 / max and a forced go-near burst job (camera sweep). Household exempt in every arm |

## 19. What the FO packages build from this

- **FO-3** (`FarHold`): docs/346 as written. The E1 own dispatch of docs/332's FO-3 row is not needed (§1). Optional
  target-owner rule behind `farobjtarget` (Q4).
- **FO-4** (`FarObjModel.cs`, no EA writes): the lot record, the transition table of §5 as data, guards behind one
  interface, `F(t)` with the fire-list machines K1-K6, seed / draw / range (§8), the due queue, the trace, the drop
  function, the three pulse-skip hooks for `FarLight`, selftests D1, D8, D9, D11 and the leak churn.
- **FO-5**: history ring, sidecar lines of §12, flush, `[Diag] FarObjShadow`.
- **FO-6**: named models of §6.2 as pure functions with D2, D5, D10, one class at a time; the class-machine CSV is
  its work list.
- **FO-7**: go-near (§9.2) with the four release actions, the re-arm bracket (D12), compaction, fallback.
- **FO-9**: spawners, harvests, exposure tables, G-F filters (`Wildflower` first). **FO-10**: pause hooks, zombies.
  **FO-11 / FO-12**: fire, traffic, seasons, unchanged.
