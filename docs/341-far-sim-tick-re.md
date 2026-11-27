# 341. Far-Sim tick map: what the sim thread does per tick for a Sim far from the household (2026-11-26)

Static only (phase R of docs/340: RX-1 Sim part, RX-5, parts of RX-6). Steam 1.67. Sources: the decompiled script
assemblies under `game-copy/decompiled` (local only; `file:line` below points there) and the existing local Ghidra
decompile of the scheduler seams (`game-copy/analysis/decomp/sched-seams.c`, `simulate.c`). Nothing built, nothing run.
Ledger: docs/51 §611 and §619 (round 2, §9). Handoff: `docs/handoff/far-sim-tick-re-1126.md`.

Builds on, and does not repeat: docs/322 (distance bands), docs/324 (far-Sim model, branch
`feature/far-sim-model-1122`), docs/331, docs/332, docs/340 §3, docs/308 (task seam addresses), docs/27 (RNG),
docs/630 (EA low-LOD layer), docs/143 / 144 (unseen throttles).

Markers: [verified] = read in the decompiled code or the Ghidra decompile named on the row; [inferred] = follows from
verified rows, not read directly; [earlier] = verified by the doc cited, not re-read here.

## 0. Short answer

- One instantiated Sim owns **two scheduler entries**: the Sim object itself (execute type Threaded, body
  `Sim.Simulate`) and one `SimUpdate` task. Everything else is shared: one `AutonomyManager` task for all Sims, the
  world lot's alarm manager for every Sim alarm, one aging alarm, one story progression alarm [verified].
- A far (EA "low LOD") Sim does not decay needs. EA already computes its needs in closed form:
  `value = AutoSatisfyCurve((MotiveTimeDelta + hour of day + 24) mod 24)`, with one random phase per motive drawn once
  [verified, `Motive.cs:902-938`]. The state machine can use the same formula and hand EA the same two fields at
  go-live, so the hand-over is a state EA itself produces.
- The per-Sim heartbeat (`Autonomy.Update`: motives, buffs, mood, skills, careers, relationship updates, opportunities,
  two events) runs for a far Sim only inside an autonomy decision. This is the static reason behind docs/243's
  "cost is per decision" [verified, `Autonomy.cs:990-1021`, `1070-1081`].
- `SimUpdate` of a low-LOD Sim sleeps 0 ticks: it runs every sim tick for every instantiated far Sim, for a handful of
  flag tests and one timer decrement [verified, `SimUpdate.cs:135-316`]. This is the one true per-tick per-Sim cost.
- EA's "fresh Sim" contract (`Sim.OnCreation`) is: new mood, buff and motive objects, empty interaction queue,
  standing, only travel buffs restored, pregnancy continued. A Sim rebuilt from its `SimDescription` therefore starts in
  a state EA reaches by itself; buffs, queue, posture and conversation are not carried by EA either [verified].
- The live EA simulation is **not deterministic by construction**: the threaded task walk stops on a
  `QueryPerformanceCounter` deadline, autonomy scoring yields on a 1 ms real-time stopwatch, the script RNG is one
  shared Mersenne Twister, equal-time alarms fire in binary-heap order and object-keyed dictionaries iterate in
  address-hash order [verified, §5]. The far machine can be replayable against itself; it cannot be bit-equal to a
  live EA run, and no test should ask for that.

## 1. Per-tick entry points (question 1)

### 1.1 Native loop into Mono

| Step | Address (Steam 1.67) | What | Marker |
|---|---|---|---|
| Host loop | `0xD81840` `MonoScriptHost::Simulate` | per iteration: `0xD80620`, `ScriptTick` `0xD80BC0`, TaskObjects `0xD7FC60`; in the tick branch also OneShotObjects `0xD74800`, ThreadedObjects `0xD7F8B0`, `0xD7FE80` | [earlier] docs/04 F17, docs/308; call order re-read in `simulate.c` |
| Run one task | `0xD7F000` `ScriptHost_RunTask` (retail `0xD7F490`) | resumes the task's Mono context; call sites `0xD7FA74`, `0xD7FB53` (Threaded), `0xD7FE01`, `0xD800FC` (Task) | [earlier] docs/308 |
| Threaded walk | `0xD7F8B0` | walks a hash map: bucket array host `+0x16C4`, bucket count `+0x16C8`, 64-bit object id key; resume cursor (next id) host `+0x1700/+0x1704`; ends after one full round **or when a `QueryPerformanceCounter` deadline passes**, then stores the cursor | [verified] `sched-seams.c` (Ghidra decompile, not the raw disassembly) |
| Managed entry | `ScriptProxy.Simulate` | `PreSimulate`, then `mTarget.Simulate()` in a retry loop; `ResetException` goes to `OnReset` | [verified] `ScriptProxy.cs:84-128` |
| Sleep | `Simulator_SleepImpl` `0x76CB00` | `Simulator.Sleep(n)` parks the calling task for n sim ticks | [earlier] docs/119 |
| Task lookup | `Simulator_GetTaskImpl` `0x76CA60` | `Simulator.GetTask` / `GetProxy` by object id. It is a lookup icall, **not** the scheduler's fetch (answers the "confirm" in docs/340 RX-1) | [earlier] docs/51 (I-122 block), [verified] managed declaration `ScriptCore/Simulator.cs:61` |

EA 1.69: the RunTask sites are unresolved there (docs/308); the managed side was not compared. Open question Q6.

Where sleeping tasks are kept and how their wake tick is ordered was not read (the list at host `+0x174C` is a
candidate) [inferred]. Open question Q1.

### 1.2 What one Sim owns

| Scheduler entry | Created | Removed | Per Sim? | Marker |
|---|---|---|---|---|
| The Sim object, Threaded, `Sim.Simulate` | object creation (`SimDescription.Instantiate` -> `GlobalFunctions.CreateObjectWithOverrides`) | `Sim.Destroy` / `Dispose` | yes | [verified] `Sim.cs:53167`, `56647` |
| `SimUpdate` (`ScriptTask`) | `Sim.OnStartup` (`Simulator.AddObject`, id kept in `mSimUpdateId`); skipped for sculpture temp descriptions | `Sim.Dispose` (`Simulator.DestroyObject`) | yes | [verified] `Sim.cs:52973-52976`, `56481-56485` |
| `AutonomyManager` (Threaded) | `AutonomyManager.Startup` | `Shutdown` | **global, one** | [verified] `AutonomyManager.cs:138-160`, `208-225` |
| Sim alarms | `GameObject.AddAlarm*`: an owner that is a Sim always goes to `AlarmManager.Global` | `AlarmManager.OnDispose` / `OnReset` | global queue, per-Sim entries | [verified] `GameObject.cs:7502-7530`; `MoveAlarms` skips Sims (`AlarmManager.cs:737`) |
| Global alarm pump | the world lot's `Lot.Simulate` calls its alarm manager first; `AlarmManager.Global` is the manager whose lot id is the world lot | - | global | [verified] `Lot.cs:8309-8313`, `AlarmManager.cs:256-282` |
| Transients | `OneShotFunction` tasks (outfit build, overlays, yielding alarms through `AlarmOneShot`), reaction broadcasters | self | per event | [verified] sites listed by grep, not each read |

No other persistent per-Sim task was found in the actor systems, autonomy, socializing or routing namespaces (grep of
`Simulator.AddObject(`) [verified for those four namespaces; object scripts in Sims3GameplayObjects not swept].

### 1.3 `Sim.Simulate` -> `DoInteraction` (per Sim, Threaded)

`Sim.Simulate` loops `DoInteraction` forever [verified, `Sim.cs:56647-56657`]. One pass [verified, `Sim.cs:56141-56246`]:

1. Head interaction present and it must run (or is prioritized / a continuation): bookkeeping, then
   `InteractionQueue.ProcessOneInteraction()`, then `Simulator.Sleep(0)`. Routing, animation and the interaction's own
   loops run inside this call on the Sim's own task stack.
2. Otherwise the Sim idles: `Posture.OnInteractionQueueEmpty`, idle manager calls, `LoopIdle`, then:
   - autonomy off for this Sim: `Sleep(20)` (`kSimLoopSleepTicksWhenAutonomyDisabled`);
   - already in the autonomy queue: `Sleep(10)`;
   - not queued: it queues itself (`AutonomyManager.Add`) when it may run at once, or when the queue has been empty for
     the delay (normal 5 sim minutes, 2 after a user cancel, 7 while socializing, 10 mounted), **or always when the Sim
     has a service or an active role**; then `Sleep(2)`.

So an idle far Sim wakes every 2 or 10 sim ticks only to test whether it may queue for a decision.

### 1.4 `AutonomyManager.Simulate` (global)

One decision per pass: it takes the head of the high-LOD or the low-LOD queue (alternating, the other queue if one is
empty), calls `Autonomy.TryFindBestAction`, handles one queued reaction, then `Sleep(0)` [verified,
`AutonomyManager.cs:138-182`]. A Sim is in the high-LOD queue when `IsRunningLocalAutonomy` was true at enqueue.

`TryFindBestAction` -> `RunAutonomy(true)` [verified, `Autonomy.cs:791`, `1070-1145`]:

- low LOD only: first `Autonomy.Update()` (the heartbeat, §1.6);
- scoring: local autonomy only if `ShouldRunLocalAutonomy`; meta autonomy (venue / lot choice) when the queue is empty;
- choose by weighted random, queue the interaction;
- **nothing chosen, NPC, not in a service, not in a situation that rules it out: `Sim.MakeSimGoHome`.**

### 1.5 `SimUpdate.Simulate` (per Sim, every tick when far)

[verified, `SimUpdate.cs:118-316`]

- A static counter lets **two** high-LOD Sims per sim tick do the full update (`kMaxSimUpdates = 2`); those sleep
  `kSimUpdateSleepTicks` (5) scaled by the high-LOD population. Everyone else, and every low-LOD Sim, sleeps
  **0 ticks** (the local sleep count stays 0).
- High-LOD work: curfew, hair growth, fairy aura, `Autonomy.Update()`, look-ats and clothing reactions per sim minute,
  body shape per 30, environment score and buffs per 2.
- Low-LOD work: if the Sim is a non-selectable NPC inside on a residential lot that is not the active household's home
  and it is not routing, it is faded to opacity 0 and flagged `WasHiddenForLowAutonomy` (this is EA's own "hidden and
  inert at home" state, docs/630).
- Both: `InteractionQueue.UpdateTimer(minutes)` (decrements the running interaction's timer and raises
  `ExitReason.Finished` at 0, `InteractionQueue.cs:1433`).

### 1.6 The heartbeat: `Autonomy.Update`

[verified, `Autonomy.cs:990-1021`] Skipped unless at least 1 sim minute passed (active Sim: any time). Then, in order:
`Motives.UpdateMotives`, `BuffManager.UpdateBuffs`, `MoodManager.UpdateMood`, `SkillManager.UpdateSkills`,
`CareerManager.UpdateCareers`, `SocialComponent.UpdateRelationships` (only the queued per-interaction increments,
`SocialComponent.cs:2818`), opportunities, thought balloons, map tags, events `kSimHeartbeat` and
`kDnPSynchronizeEvent`, dreams refresh.

Callers: `SimUpdate` (high LOD only) and `RunAutonomy` (low LOD only). A far Sim that never reaches a decision has
no heartbeat at all.

### 1.7 High LOD or low LOD

`Autonomy.IsSufficientlyOnScreenForHighLODSimulation`, re-evaluated every 60 sim ticks per Sim [verified,
`Autonomy.cs:472-534`]. High when: same household as the active Sim; current lot is the active lot; same non-world lot
as the active Sim; guest of a started hosted situation whose host is selectable; daycare child; resort worker; or
outside (room -1 / 0) **and on screen** (`World.IsObjectOnScreen`). The last term makes LOD depend on the camera.

### 1.8 Per Sim or global

| Work | Scope | Far Sim pays |
|---|---|---|
| `Sim.Simulate` idle wake | per Sim | every 2-20 sim ticks |
| `SimUpdate` | per Sim | **every sim tick** |
| Autonomy decision + heartbeat | global task, per Sim work | per decision (about every 5 sim minutes when idle; more for service / role Sims) |
| Interaction run (route, animation, loops) | per Sim, on the Sim's task | while an interaction runs |
| Sim alarms | global heap | per alarm fire |
| Aging | global daily alarm over SimDescriptions | per day |
| Story progression | global alarm + `StoryProgressionTask` | per action |
| Relationship decay, careers off shift | per SimDescription, global drivers | docs/324 §4-5 |

## 2. State a far Sim carries (question 2)

"Lives on" tells whether the state survives without a Sim object. Field lists come from a declaration scan of
`Sim.cs` (130 instance fields, 8 not persisted) and `SimDescription.cs` [verified by scan; not every field read].

| State | Lives on | Closed form today? | For the machine |
|---|---|---|---|
| Needs (motives) | Sim (`Autonomy.Motives`, `Dictionary<int, Motive>`) | **yes, by EA**: auto-satisfy curve of the hour plus `MotiveTimeDelta` per motive; first low-LOD update only draws the phase (`AutoUpdated`); a high-LOD Sim not updated for more than 1 h takes the same path | compute; at go-live write `MotiveTimeDelta`, `AutoUpdated` and the value through EA's setter. Motives without an auto-satisfy curve keep their value [verified `Motive.cs:902-915`] |
| Motive-driven buffs | Sim (`BuffManager`) | follows the needs value (`UpdateMotiveBuffs`, only with decay enabled) | derive at go-live from the computed needs through EA's own update |
| Timed buffs | Sim | no general form (per-buff timeout plus callbacks) | go-far only without timed buffs that have side effects, or hold their alarms; Q3 |
| Mood | Sim (`MoodManager`) | function of buffs | let EA recompute |
| Interaction queue, running interaction, timer | Sim (`InteractionQueue`, persisted) | no | must be empty at go-far (docs/340 §3.5); never forged at go-live |
| Posture, carried object, parent / container | Sim | no | standing and unparented at go-far |
| Position, lot, room | native object + Sim | place = function of state (home, rabbit hole, venue) | docs/324 §2.4; go-live through EA placement only |
| Route in progress | Sim routing component + native | no | not routing at go-far |
| Outfit shown | Sim (`mVisibleOutfitInfo`), outfits on SimDescription | function of state (sleep, work, weather) | let EA pick at go-live (`OnStartup` re-validates the outfit and destroys the Sim if it is invalid) |
| Alarms owned by the Sim | global heap, handles on the Sim (10 handle fields) and in systems | timers with a known due time | hold and re-arm (docs/331); trait idle alarms are re-drawn by `AddTraitAlarms` at startup |
| Conversation, short-term context, reaction memory | Sim (`SocialComponent`, `LookAtManager`) | no | none while far; cleared like EA's reset does |
| Relationships (LTR, bits) | `Relationship` table keyed by SimDescription | drift model docs/324 §2.7 | survives without a Sim |
| Skills, career, school, occult, traits, health, pregnancy object, celebrity, aging state, genealogy | SimDescription managers | per system; docs/324 §2.7, §3.4 | survive without a Sim; several need a Sim to advance (§3) |
| Situations the Sim is in | `Autonomy.SituationComponent` + global list | no | excluded while in one |
| Service / role assignment | SimDescription (`Service`, `AssignedRole`) | schedule driven | excluded while on duty (docs/324 §3.6) |
| Inventory | Sim (`InventoryComponent` added in `OnStartup`) | static while far | stays with the object in stage 2a; blocker to check for stage 2b (Q2) |
| Dreams, opportunities, map tags, thought balloons | Sim | UI or selectable-only | drop for NPCs |

## 3. What EA already does without a Sim object (question 3)

docs/324 §5.1 holds the main list (single funnel into `SimDescription.Instantiate`, homeless townies destroyed by
`GoToVirtualHomeInternal`, placeholders, service reuse, mini descriptions) [earlier]. Re-read and added here:

- **Claim "off-world Sims exist as SimDescription only": confirmed for the script side.** `SimDescription.mSim`
  (`CreatedSim`) is the only link; `Sim.Dispose` clears it and removes every buff first (`Sim.cs:56427-56440`).
  Households keep `SimDescription` lists, not Sims (`Household.cs:64-72`) [verified].
- `Instantiate(Vector3, ResourceKey, bool, bool)` returns the existing Sim if there is one, may wait in
  `AgingState.PreInstantiateSim`, creates the object with a `SimInitParameters` carrying the description, then:
  dynamic footprint, initial objects (humans), aging push, homework skill for teens, occult setup, future-world mods,
  event `kSimInstantiated`, mini-description relationship merge when travelling, health startup, roommate
  interactions [verified, `SimDescription.cs:4807-4860`].
- `Instantiate(Lot)`: a household without a virtual home goes to `InstantiateOffScreen` (random lot position from
  `Service.GetPositionInRandomLot`); apartment residents appear at their NPC door or an elevator, else near the lot
  corners (`SimDescription.cs:4709-4785`) [verified]. These are EA's own "appear" positions.
- `Sim.OnCreation`: new `MoodManager`, `BuffManager`, `Autonomy`; `Motives.CreateMotives` (each motive starts from
  `MotiveTuning.GetInitialValue`: the auto-satisfy curve at the current hour plus a random phase, or a random value in
  the tuned range); new `InteractionQueue`; `CareerManager.OnLoadFixup`; `BuffManager.LoadBuffsFromTravel`;
  `Pregnancy.Continue`; trait listeners [verified, `Sim.cs:51921-51980`, `MotiveTuning.cs:119-134`].
- Age-up without a Sim: `AgingState.AgeTransitionWhileUninstantiated` cancels the Sim's aging alarms and defers the
  outfit build to the next instantiation (`AgingState.cs:358`); the daily `AgingManager.DayPassedCallback` works on
  aging states, not Sims (`AgingManager.cs:1193`, `1682`) [verified].
- Story progression runs from one global alarm and a task, picks a random number of actions per run and yields between
  them (`StoryProgressionService.cs:325-445`) [verified]; what each action needs from a Sim object is docs/324 §4 and
  RX-6.
- `CreatedSim` is read in about 2,300 places in Sims3GameplaySystems (2,318 lines) (top namespaces: Skills 383, Careers 347,
  ActorSystems 312, Socializing 200) [verified count by grep; sites not classified]. The classification is RX-6.

## 4. Go-live and go-far in EA's own code (question 4)

| EA path | What it guarantees | Use | Marker |
|---|---|---|---|
| `SimDescription.Instantiate` + `Sim.OnCreation` + `OnStartup` | fresh managers, needs from the curve, empty queue, standing, valid outfit or the Sim is destroyed, `SimUpdate` added, lot knowledge and venue preferences rebuilt | stage 2b go-live | [verified] §3 |
| `Motives.SetMotivesToTimedDefaultsAndRemoveBuffs` | removes perma moodlets and sets the perma motives to the curve value for the current hour | EA's own "catch up after absence": called for the household after `ReturnHomeBehaviorInHomeWorld` and for townies in `GoToVirtualHomeInternal` | [verified] `Motives.cs:583-610`, `ReturnHomeBehaviorInHomeWorld.cs:66`, `Sim.cs:4447` |
| Travel import | `BuffManager.LoadBuffsFromTravel` restores only the buffs parked in `SimDescription.TravelBuffs` | shows the one buff carry-over EA has | [verified] `GameStates.cs:2163`, `2258`, `2455`, `BuffManager.cs:1734-1754` |
| `Sim.DoReset` | work / school finished, queue reset, overlays and look-ats stopped, carry disposed, posture unwound to standing, default pose, `AttemptToPutInSafeLocation`, outfit fixed if naked, autonomy and situations reset | **go-far safe-point normaliser and go-live fallback** | [verified] `Sim.cs:54629-54765` |
| `Sim.AttemptToPutInSafeLocation(sendHome)` | finds a routable spot on the current lot, or at home when `sendHome`; re-adds the object to the world, clears hidden flags, opacity 1; last resort is the lot position | answers docs/340 RX-1 "which EA entry safely sends an NPC home": this one teleports; `MakeSimGoHome` only queues a go-home interaction | [verified] `Sim.cs:54465-54510`, `48125-48160` |
| `Sim.MakeSimGoHome` | home lot: go home (by car or boat if owned); no home lot: `GoToVirtualHome` (fade and destroy); refuses non-playable ghosts and tomb mummies | live Sims only | [verified] |
| Lot load / save load | a saved Sim comes back with its persisted queue, motives and posture and runs `OnLoadFixup` paths (`Motives.OnLoadFixup`, `InteractionQueue.OnLoadFixup`) | not a model for go-live: it needs the saved object | [verified] entry points only |
| `Sim.Placeholder` / `Rematerialize` | EA's resident-without-a-Sim for NPC children | docs/324 §5.1 | [earlier] |

Minimum that must hold to hand a Sim to EA mid-play, from the rows above:

1. No second Sim for the description (`Instantiate` only checks `CreatedSim`; docs/324 §5.1).
2. Position from an EA placement call; hidden flags cleared and opacity 1 unless EA's low-LOD hide applies.
3. Standing posture, empty queue, no carry, no parent. Anything else has to be entered by EA's own interactions.
4. Needs set through EA's setters with `MotiveTimeDelta` and `AutoUpdated` consistent, so the next EA low-LOD update
   gives the same value and no jump.
5. Outfit valid for the category EA will ask for; an invalid outfit makes `OnStartup` destroy the Sim.
6. `SimUpdate` present, autonomy LOD flag correct, lot known (`Autonomy.AddLotKnownAbout`).
7. Career state consistent: `IsAtWork` false unless the Sim is placed in the rabbit hole by EA's go-to-work path.

## 5. Determinism hazards (question 5)

| # | Hazard | Evidence | Marker | Rule for the machine |
|---|---|---|---|---|
| H1 | One shared script RNG (Mersenne Twister at host `+0xC30`), seeded from `RDTSC` unless `[Config] SimulatorRandomSeed`; `RandomUtil` has about 2,990 call sites in Sims3GameplaySystems and 1,920 in Sims3GameplayObjects | docs/27; `RandomUtil.cs`, `ScriptCore/Random.cs` | [earlier] + [verified] counts | never call `RandomUtil` / `RandomGen`; own counter-based PRNG (docs/340 §3.4) |
| H2 | 51 `new Random(...)` sites; unseeded ones use the tick count; some seeds mix `SimClock` days, `ObjectId` or `string.GetHashCode` | docs/27 §2; e.g. `Career.cs:3432`, `ComeAndSeeComponent.cs:254`, `StocksManager.cs:139` | [verified] | not reachable from machine code; go-live calls into EA may draw from them, which is fine |
| H3 | Threaded task walk stops on a real-time deadline | `0xD7F8B0` reads `QueryPerformanceCounter` each step and leaves when past the deadline | [verified] Ghidra decompile | the due queue is drained by sim time and a **count** budget, never by a clock |
| H4 | Autonomy scoring and motive update yield on a 1 ms real-time stopwatch (`sUpdateTimer`, every 5 or 10 items) | `Autonomy.cs:162`, `2292-2336`; `Motives.cs:642-660` | [verified] | no time-based yields in the machine |
| H5 | `SimClock` is tick based (`CurrentTicks` = sim tick count + start + advanced) and deterministic, but many holders keep elapsed minutes as `float` (`mLastTime`) | `SimClock.cs:74`, `Autonomy.cs:990` | [verified] | keep sim time as integer ticks; convert at the edge |
| H6 | Alarm order: a binary heap keyed by due tick; `Timer.CompareTo` compares the date only, so equal-tick alarms fire in heap order, not insertion order; repeating alarms re-insert | `AlarmManager.cs:171`, `543-600`; `Sims3.Collections.PriorityQueue` | [verified] | order replayed fires by (due tick, stable id, sequence) |
| H7 | Dictionary order: this Mono's `Dictionary<,>` enumerates bucket by bucket, so order follows the key hash; `GameObject`, `SimDescription`, `Lot` do not override `GetHashCode`, so their hash is `Object.InternalGetHashCode` | `mscorlib` `Dictionary.cs:104-113`, `Object.cs:34-57`; 191 object-keyed dictionary declarations in the two gameplay assemblies | [verified] managed; that the native hash is address based is [inferred] | iterate only sorted lists keyed by `SimDescriptionId` / lot id; never an object-keyed dictionary or a `Hashtable` |
| H8 | LOD depends on the camera (`World.IsObjectOnScreen`) and on a 60-tick cache | §1.7 | [verified] | boundary inputs (camera target, household positions) are recorded inputs of the trace, with hysteresis (docs/340 §3.2) |
| H9 | Native RNGs (about 50 seed sites; routing, animation, effects) | docs/27 §1.4 | [earlier] | none reachable while far; go-live placement may differ between runs. The trace compares machine state, not final positions |
| H10 | `DateTime.Now` (27 sites in Sims3GameplaySystems), `Stopwatch` | grep | [verified] count; sites not classified | not allowed in machine code |
| H11 | Task run order across Sims follows the native hash map order over object ids, and object ids differ between sessions | H3 row; id stability is RX-4 | [inferred] | never depend on EA task order; process due transitions sorted by (due tick, `SimDescriptionId`) |

**Allowed inputs for SD-1** (output asked by RX-5): sim tick, world seed, `SimDescriptionId`, the go-far snapshot,
tuning tables read once at load (auto-satisfy curves, career schedules), the recorded world-history ring, and recorded
boundary events. Nothing else.

## 6. Proposed state set for far Sims

Refines docs/340 §3.3 with what §1-5 found. Names kept where they match.

### 6.1 States

| State | Enter | Leave | EA-side shape while in it |
|---|---|---|---|
| `Live` | start; go-live done | far for the hysteresis time and eligible -> `GoFarPending` | EA owns everything |
| `GoFarPending` (new) | boundary says far | safe point reached -> first far state; near again -> `Live`; timeout -> `Live` (`noSafePoint`) | EA still owns; the machine only watches for: queue empty, standing, not routing, no situation, no carry, no timed buff with side effects, autonomy not mid-decision |
| `FarHome` | safe point at home; arrival | schedule edge -> `FarTransit`; sleep time -> `FarSleep`; seeded pick -> `FarTransit` to a venue or `VisitProposed` | stage 2a: object kept, EA's own low-LOD hide (opacity 0, `WasHiddenForLowAutonomy`), both tasks parked. Stage 2b: no object |
| `FarSleep` | from `FarHome` at the seeded sleep time | wake time | same as `FarHome` |
| `FarTransit` | leaving a place | arrival tick (distance / speed table) | position = point on the stored or straight path at t |
| `FarWork` / `FarSchool` | arrival at the rabbit hole inside a shift | shift end | `IsAtWork` handled at go-live only; pay and performance by docs/324 §3.4 |
| `FarVenue` | arrival | seeded stay length; venue closes | - |
| `VisitProposed` | seeded pick with distance decay (docs/324 §3.9) | EA accepts -> `PendingLive`; lapse -> `FarHome` | - |
| `PendingLive` | near boundary crossed, observation, household-facing event, EA read that needs a Sim, save flush | build done -> `Live`; failure -> fallback -> `Live` | spread queue; EA calls only |
| `Excluded` | docs/324 §3.5-3.6 reasons; unknown scope; household or selected Sim | reason gone -> `Live` | EA owns; never far |

Needs, mood and motive buffs are not states: needs are `curve_m(hour(t) + delta_m)` read on demand, the same form EA
uses for low-LOD Sims (§2).

### 6.2 Inputs

- Clock: sim tick (integer). Day of week, hour, season from the tick and the world-history ring.
- Boundary events: near / far per Sim with hysteresis, from recorded viewer positions (camera target, household Sims).
- EA events that force `PendingLive` or `Excluded`: situation start, service assignment, pregnancy, age-up due,
  death, a household Sim's interaction targeting the Sim, phone / invite targeting, selection.
- Schedule tables: career and school hours, venue hours (read once; version in the model version).

### 6.3 Seeded RNG

`seed = H(world seed, SimDescriptionId, model version)`; value `= PRNG(seed, transition index, draw index)`
(docs/340 §3.4). Draws used: `delta_m` per motive (draw index = commodity id, transition index 0, so it is stable for
the whole far episode), sleep and wake offsets, venue choice, stay length, visit proposal, drift steps. No draw ever
comes from the script RNG (H1), so EA's stream is not shifted by the machine either.

### 6.4 Sidecar record (never in the .sims3)

- Header: world seed, model version, tuning hash, sim tick at write.
- Per far Sim: `SimDescriptionId`, state, `enteredAt`, `nextDue`, transition index, place (lot id or rabbit-hole id,
  or path id + start tick), go-far snapshot (needs values and `delta_m` at go-far, held alarm list with remaining
  ticks and owner, outfit category, skill / career / relationship baselines for drift).
- Event log: (id, transition index, from, to, sim tick) plus recorded inputs (boundary events, forced promotions).
- Before every save all far Sims are flushed through EA code (docs/340 §3.5), so a missing or stale sidecar loads as
  plain EA state.

### 6.5 Transition rules that follow from the RE

- Go-far normalises with EA's own state: the safe point is the state `Sim.DoReset` would leave (standing, empty queue,
  no carry), reached by waiting, not by calling the reset.
- Stage 2a parks exactly two scheduler entries per Sim (§1.2) and removes the Sim from the `AutonomyManager` queues
  (`AutonomyManager.RemoveActor`). Held alarms are the Sim-owned entries of `AlarmManager.Global`.
- Go-live fallback is `AttemptToPutInSafeLocation(sendHome: true)` after the queue is confirmed empty; it is logged
  once (`goLiveFallback`).
- Household Sims, the selected Sim and Sims of unknown scope never leave `Excluded`. `[Experimental] HouseholdToo`
  does not apply: parking a Sim is a skip, not a realistic-only site.

## 7. Candidates noticed on the way (not built, not measured)

- `SimUpdate` runs every sim tick for every low-LOD Sim (§1.5). For an NPC with no running interaction the only effect
  is the hide test and a no-op timer call. A longer sleep for that case looks exact [inferred]; it needs the household
  exemption and a measurement first (docs/322 has the per-Sim cost, not this split). Check docs/143 / 144 before
  building; this may overlap an existing unseen throttle.
- High-LOD Sims beyond the two-per-tick budget also spin with `Sleep(0)`; household Sims are in that set, so no change
  is proposed there.

## 8. Open questions

Round 2 (§9) answers Q1-Q5 and Q7; Q6 stays open (no decrypted EA 1.69 image on the analysis machine).

- Q1. Where the native scheduler keeps sleeping tasks and in what order equal wake ticks resume (not read; needs the
  disassembly of the sleep path behind `0x76CB00` and of `0xD80620`).
- Q2. Inventory of a Sim without an object (stage 2b): where EA keeps it when a resident's Sim is destroyed, if at all.
- Q3. Which timed buffs have side effects at timeout (callbacks, alarms). Needed for the go-far eligibility rule.
- Q4. `GetInitialValue` versus `AutoSatisfyMotive` use slightly different phase handling (the first adds the phase to
  the hour without the mod-24 wrap). The machine should follow `AutoSatisfyMotive`; confirm the curve is periodic.
- Q5. That `Object.InternalGetHashCode` is address based in this Mono build (H7) was not read in the native code.
- Q6. EA 1.69: scheduler addresses unresolved (docs/308); managed diff of the methods cited here not done.
- Q7. The `0xD7F8B0` rows were read in the Ghidra decompile, not in the raw disassembly; a byte-level check is due
  before any hook lands there.

## 9. Round 2 (2026-11-26): open questions closed

Static only. Native rows were read in the raw bytes of the Steam 1.67 exe copy with capstone (REA was busy; the
Ghidra decompile under `game-copy/analysis/decomp` was used as a map only). Managed rows: `game-copy/decompiled`.
Ledger: docs/51 §619. Markers as in the header; [verified bytes] = read in the disassembly.

### 9.1 Q1: sleep list and wake order

| Step | Address | What | Marker |
|---|---|---|---|
| Sleep icall | `0x76CB00` | clamps the tick count to `0xFFFFFC17`, then app singleton vfunc `+0x17C` -> script host vfunc `+0x20` | [verified bytes] |
| Host sleep | `0xD77B20` (host vtable `0x108B818` slot `+0x20`) | count 0: no sleep entry, the task only yields and stays in its run map. Count > 0: writes the count into the **sleep map** (hash map object at host `+0x1728`, buckets `+0x172C`, bucket count `+0x1730`, key = running task id at host `+0xBD0`) and inserts the id into an ordered tree at host `+0x1748` (head `+0x174C`). Both cases end in the yield call (host `+8` interface `+0x11C`) | [verified bytes] |
| Park | end of each walk (`0xD7F8B0` at `0xD7FC16..0xD7FC3B`; the Task and OneShot walks have the same tail in the decompile) -> `0xD73640` | every id in the `+0x1748` tree is erased from the run map (`+0x16C0` for Threaded); if the resume cursor `+0x1700/+0x1704` named that id it moves to the next node or is zeroed. The tree is cleared at the start of the next walk | [verified bytes] for the Threaded walk and `0xD73640` |
| Count down and wake | tail of `ScriptTick` `0xD80BC0` | walks the whole sleep map in bucket order every call. Remaining 0 or below the elapsed ticks: wake. Else remaining minus elapsed, **unless remaining >= `0xFFFFFC17`, which is never counted down** (EA's own "sleep forever") | [verified] Ghidra decompile `script-tick.c`, not re-read in bytes |
| Wake | `0xD74A10` | reads the object's execute type through a managed call and inserts the id into that type's run map: Threaded `+0x16C0`, Task `+0x16E0`, the third type `+0x16A0` | [verified bytes] |
| Forced wake | map at host `+0x1768`, drained in `ScriptTick` before the count-down loop | sets the sleep entry's remaining count to 0, so the loop right after wakes it | [verified] decompile; that `Simulator.Wake` fills this map is [inferred] |
| `0xD80620` | - | **not part of sleep**: when host `+0xA34` < 1 it walks the task-context map (`+0xB88`) and tears down entries flagged at `+0xD` (the flag `RunTask` turns into a `ResetException`) that are not finished | [verified] decompile `sched-seams.c` |

Run-map lookup `0xD726F0`: bucket = low 32 bits of the 64-bit object id modulo the bucket count, chain link at node
`+0x10` [verified bytes].

**Wake order answer.** There is no wake queue and no order by wake tick. A woken task goes back into a hash map and
runs when the walk reaches its bucket. Run order within a tick = bucket order of (low 32 bits of the object id mod
bucket count), starting at the rotating resume cursor, chain order inside a bucket. It is repeatable only if object
ids, the bucket count (grows with the task count) and the cursor are the same, and the cursor depends on the
real-time deadline (§9.7). So H11 is now [verified] and the rule stands: the machine never depends on EA task order.
This replaces the round-1 guess (docs/51 I-611-3).

For stage 2a: a parked task is a sleep-map entry with the clamp value. It leaves the run map, costs one sleep-map
visit per `ScriptTick`, and wakes through the forced-wake map. No hook is needed to park: a sleep with the maximum
count inside the task and `Simulator.Wake` from outside are EA's own calls [inferred for the Wake half].

### 9.2 Q2: inventory of a Sim without an object

- The inventory exists only on the Sim object: `InventoryComponent` is added in `Sim.OnStartup` (`Sim.cs:52946`).
  When the component is disposed it disposes the inventory, and that **destroys every item**
  (`InventoryComponent.cs:72-82`, `Inventory.cs:138-141`) [verified].
- `SimDescription` has one inventory holder: `mInventoryItemsWhileInPassport` (`SimDescription.cs:771`). The Passport
  system empties the live inventory into that list without destroying the objects
  (`Inventory.DestroyInventoryAndStoreInList`, `Inventory.cs:1438`) and puts them back with
  `RestoreInventoryFromList` (`Inventory.cs:1458`; an item that does not fit is destroyed)
  (`Passport.cs:128-142`) [verified]. This is EA's only "inventory without a Sim object" path.
- EA moves an inventory to another Sim only on death and on a household merge
  (`Sim.MoveInventoryItemsToAFamilyMember`, `Sim.cs:60982`; callers `Urnstone.cs:115`, `3659`,
  `GrimReaperSituation.cs:1263`, `GameplayMovingModel.cs:1120`) [verified]. The fade-out-and-destroy in the
  go-to-virtual-home path (`Sim.cs:4436`) therefore destroys the inventory with the object [verified chain; the
  branch condition was not re-read].
- Stage 2b rule: go-far to "no object" only with an empty inventory, or after the store-in-list call into a
  mod-side list (never the EA field: it is saved), restored at go-live and at the pre-save flush. Items are live
  script objects while stored, so they keep their own alarms [inferred]. The phone and other start items are made
  again by EA at instantiation (§3).

### 9.3 Q3: timed buffs with an effect at timeout

Buff time-outs are not alarms. `BuffManager.UpdateBuffs(simMinutesPassed)` counts each instance down and calls the
buff's `OnTimeout` (`BuffManager.cs:1563-1625`) [verified]; for a low-LOD Sim that runs only inside a decision
(§1.6). 46 classes override `OnTimeout` in the three gameplay assemblies; 6 bodies are empty or only call the base
(`BrainDead`, `CoffeeBreath`, `GeniusIQ`, `NewIdentity`, `OrganicSlappucinoBuzz`, `HiveMind`). The other 40
[verified by reading each body, cut at 330 characters; file = `Buff<Name>.cs`]:

| Effect at timeout | Buffs |
|---|---|
| Death or a death interaction | `Fatigued` (drowns if swimming), `GaspingForAir` (scuba), `MermaidDehydrated`, `DoIEvenExist`, `OnFire`, `Drowning` (pool only) |
| Occult, trait or pregnancy change | `AdaptingToLand` (mermaid removed), `MummysCurse` (trait removed), `Poisoned` (plant Sim chance), `ZombieFreeze` (perma zombie chance), `Abducted` (pregnancy chance), `Delusional` (stored traits) |
| Next buff in a chain | `BreathingComfortably` -> `BreathingShallowly` -> `GaspingForAir`; `DivingDeficiency` -> `AdaptingToLand`; `TimeParadoxSickness` -> `BlinkingInAndOutOfExistence` -> `DoIEvenExist`; `FeelingOutOfSorts` -> `ImpendingEpisode` -> `Delusional`; `HeatingUp` -> knocked out + too much sun; `Buzzed`, `GoingWild`, `OrganicCatCreationBuzz`, `OrganicKonaLiftBuzz`, `OrganicMidnightMudBuzz`, `OrganicOldJoeBuzz` -> buzz crash; `LiquidEnergy` -> crash and energy drop; `Misunderstood` -> need to brood; `RadioactiveBugBite` -> one of two buffs (script RNG) |
| Queues an interaction | `Nauseous`, `Spoiled`, `StrayPet` (go home), `Torched` (pass out chance), `Poisoned` |
| Restores a counter | `GenieMagicPower`, `UnicornMagicPower`, `Hairball`, `ShortOut` |
| Effects only | `Cloudy`, `NotSoHealthyGlow`, `BeeAttack` |

Rule: none of these is emulated. A Sim holding any of the 40 is not eligible for go-far (`GoFarPending` waits or
times out). All are rare states (scuba, time travel, fire, occult). For the rest: stage 2b loses the buffs the way EA
does (`Sim.Dispose`); stage 2a freezes the counters, and one EA `UpdateBuffs` call with the far minutes at go-live
lets EA time them out itself [inferred, not tested]. 199 classes override `OnRemoval`; they run under EA's own
removal in both stages, so they need no model.

### 9.4 Q4: is the auto-satisfy curve periodic

Yes by construction. The auto-satisfy curve is built with `Loops = true` (`MotiveTuning.cs:281`), and `Curve.Fx`
wraps any x outside the tuned range by the span MaxX - MinX (`Curve.cs:225-243`) [verified]. So
`GetInitialValue` (hour + phase, no mod) and `AutoSatisfyMotive` (mod 24 first) give the same value for the same
phase when the tuned span is 24 h. That the shipped tuning spans exactly 0..24 with equal end values is [inferred]:
the tuning XML was not read. Differences that remain: the phase range (`timeRandomness` per motive in the first,
+/- 1 h constant in the second) and the clamp to Min / Max in the first only.
Machine rule: call EA's curve object, do not re-implement it; then the period question cannot bite.

### 9.5 Q5: `Object.InternalGetHashCode`

`0xE9F850` (retail 1.67 `0xE9FB40`): the object address shifted right by 3, times `0x9E3779B1`. Found through the
Mono icall name table (name slot `0x10B52C0`, function array from `0x10B5838`, index 264; the neighbours resolve to
the type getter, the clone and the address getter, which confirms the index) [verified bytes]. H7 is now fully
[verified]: object-keyed dictionaries iterate in address order, different between sessions. Stable inside one
session as long as the collector does not move objects [inferred, collector not re-read here].

### 9.6 Q6: EA 1.69

Not resolvable on this machine. Both EA files in `game-copy/ea` are still encrypted (`.text` entropy 8.0, `.ooa`
section); none of seven Steam signatures matches. The decrypted image used by docs/230 is not in `probe-out`.
What exists [earlier, `docs/data/sigmap-ea169.json`, unique matches]: Threaded walk `0xD7F8B0` -> `0xD7FC70`, host
loop `0xD81840` -> `0xD81C00` (block shift +0x3C0). RunTask, the sleep vfunc and the four RunTask call sites have no
row; +0x3C0 would put RunTask at `0xD7F3C0` [inferred, unverified]. Needs `tools/sigmap.ts` on the decrypted image.
Retail 1.67 by signature in this round [verified bytes, unique]: RunTask `0xD7F490`, Threaded walk `0xD7FD40`, host
sleep `0xD77D40`, sleep icall `0x76CBC0`, `0xD80AB0` (Steam `0xD80620`), park `0xD73820`.

### 9.7 Q7: byte-level check of the `0xD7F8B0` rows

All round-1 rows hold in the disassembly (`0xD7F8B0..0xD7FC5D`) [verified bytes]:

- Entry bytes are the nine S3PP expects (docs/68, detour table row for `0xD7F8B0`). Host in `edi`.
- Run map object at host `+0x16C0`: buckets `+0x16C4`, bucket count `+0x16C8`, element count `+0x16CC` (zero and
  no cursor: early return). Cursor at `+0x1700/+0x1704`.
- Budget: max(1.0, the float at `0x11CDDB0`) times 5000.0, set as a deadline on a stopwatch object on the stack.
- RunTask call sites `0xD7FA74` (task context found in the map at `+0xB88`) and `0xD7FB53` (context created first):
  both 5-byte relative calls to `0xD7F000` with three pushed arguments (context pointer, 1, 1).
- Loop tail `0xD7FB5C..0xD7FC01`: next node (chain link `+0x10`, else next non-empty bucket, wrap at the end
  bucket); equal to the start id -> done; else `QueryPerformanceCounter` (import slot `0xF953B8`) at `0xD7FBCA`
  and a signed 64-bit compare with the deadline; past it -> store the cursor and leave. The walk also leaves when
  host `+0xC10` is not 1.
- Other hooks on these sites (docs/68): TaskSeam / TaskServe retarget the two call sites; S3PP detours the entry
  (conflict row 1). `node tools/hook-sites.ts --check` ran; its full output was not read line by line.

### 9.8 Q8: other per-Sim tasks and alarms

- `Simulator.AddObject(` has 23 sites in Sims3GameplayObjects, 6 in Sims3StoreObjects and 236 in
  Sims3GameplaySystems (155 of them one-shot helpers). **`SimUpdate` (`Sim.cs:52975`) is the only persistent task
  created per Sim** [verified by listing every site; the bodies of the two interaction-scoped helpers
  `EnterSleeping.cs:203` and `BarAdvanced.cs:337` were not read].
- New in this sweep, per household not per Sim: the `Household` itself is a scheduler object
  (`Household.cs:1897`), and so are its shared family inventory and shared fridge inventory
  (`SharedFamilyInventory.cs:112`, `SharedFridgeInventory.cs:68`). A far household keeps these three entries.
- Alarms (call lines by a regex sweep): 409 in Sims3GameplayObjects, 100 in Sims3StoreObjects, 792 in
  Sims3GameplaySystems. Explicit lot-manager alarms: 12 sites, none owned by a Sim (owners are objects, a service or
  null). The bare `AlarmManager` used by `LookAtManager` and `Situation` is the global one
  (`LookAtManager.cs:177`, `Situation.cs:71`). So §1.2 holds without the round-1 caveat: every Sim-owned alarm is in
  the global heap [verified for the swept patterns].

### 9.9 Q9: the `CreatedSim` reader lines in buckets

Method: every line of Sims3GameplaySystems that names `CreatedSim` (2,317 lines in 404 files; the property itself
excluded) classified by a script on the line, the 6 lines above and the 4 lines below; 10 random lines of the
unguarded buckets were then read by hand. Counts are a heuristic, not a per-site proof; the per-site census of the unguarded
lines is docs/349 (another lane, in progress when this was written).

| Bucket | Lines | Share | Top namespaces | Examples |
|---|---|---|---|---|
| Null-safe already (null test on the line, in the 6 lines above, or on the local copy within 4 lines) | 1,168 | 50% | Careers 188, ActorSystems 171, Skills 141, Socializing 119 | `Medical.cs:565`, `Career.cs:4261`, `GameStates.cs:2576` |
| Passed on as an argument (the callee decides) | 503 | 22% | Skills 143, Careers 85, Socializing 55 | `RidingSkill.cs:760`, `Medical.cs:1069` |
| Unguarded state read (needs a proxy or go-live) | 130 | 6% | Skills 47, Careers 23, ActorSystems 16 | `Handiness.cs:462`, `Science.cs:196`; top members: gender 18, object id 14, has-buff 13, has-skill 10, inventory find 6 |
| Unguarded action on the Sim (needs go-live) | 139 | 6% | Skills 37, Careers 26, ActorSystems 20 | `SimDescription.cs:3848`, `GhostHunter.cs:240`; top members: notification 21, add / remove buff 24, inventory add 9, alarms 15 |
| Local copy with no null test near it (unknown) | 170 | 7% | ActorSystems 41, active careers 27, Careers 19 | `Medical.cs:803`, `OccultWerewolf.cs:1267` |
| UI-only (UI, Visa, CAS UI, map tags, notifications) | 57 | 2% | UI 20, Visa 19 | `Notifications.cs:182`, `Visa.cs:158` |
| Other (returns, compares, lambdas) | 150 | 6% | ActorSystems 24, Services 21, Roles 14 | `SimDescription.cs:4811` |

Reading: the hand-read sample of the unguarded lines sits mostly in code that runs in the owner Sim's own context
(a skill during its interaction, a career event of a working Sim), where EA already assumes a live Sim. The upper
bound of lines that could fault for a Sim without an object is 439 (the three unguarded rows); EA has the same
exposure today for its own uninstantiated Sims. Stage 2a keeps the object, so none of this applies there. For stage
2b the proxy list is short: gender, object id, has-buff, has-skill, has-trait, inventory find; gender, skills and
traits are already on the description.

### 9.10 What changes for the state set (§6, docs/345)

1. `GoFarPending` eligibility: "no timed buff with side effects" becomes the 40-buff table of §9.3 (a fixed id set).
2. Stage 2b adds the predicate "inventory empty or stored" (§9.2); stage 2a is not affected.
3. Parking in stage 2a can use EA's own sleep-forever value and forced wake (§9.1); no state is added.
4. A far household keeps three scheduler entries of its own (§9.8); they are outside the per-Sim machine.
5. Needs: evaluate through EA's curve object (§9.4). Order: sorted by (due tick, `SimDescriptionId`), confirmed by
   §9.1 and §9.5.
6. No new state and no removed state.

Still open: Q6 (EA 1.69, needs the decrypted image); the tuning XML span for §9.4; that `Simulator.Wake` feeds the
`+0x1768` map; the count-down loop of `ScriptTick` and the Task / OneShot walk tails in raw bytes before any hook
there.
