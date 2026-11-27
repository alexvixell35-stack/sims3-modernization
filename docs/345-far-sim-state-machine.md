# 345. Far-Sim state machine: the spec builds implement (SD-1, 2026-11-26)

Design only: nothing built, nothing run, no new REA. Package SD-1 of docs/340 §6.2. Gate: table complete; every
transition names its inputs; household rows explicit. Builders: FS-1 (machine core), DT-1 / DT-2 (replay), FS-2 .. FS-12.
Handoff: `docs/handoff/sd1-far-sim-fsm-1126.md`.

Builds on, and does not repeat: docs/341 (tick map, state set, hazards H1-H11), docs/340 §3 (architecture), docs/343
(sidecar), docs/324 (far-Sim model; branch `feature/far-sim-model-1122` until CP-0 merges it), docs/322 §4 (T3 edges),
docs/331 (held alarms), docs/332 (FS package ids), and the phase R verdicts that landed the same day: docs/342
(ST-1 / ST-5 read sites and UI hooks), docs/344 (lazy instantiation RE), docs/346 (ST-4 / ST-7a alarm hold),
docs/347 (ST-2 / ST-3 / ST-13 setters, visits, NRaas).

Markers: [verified] = verified by the doc cited on the row (nothing was re-read in the game code for this doc);
[measured] = measured by the doc cited; [inferred] = design choice or conclusion of this doc, to confirm at build;
[pending X] = waits for the verdict of lane or package X (§15).

## 0. Short answer

- 11 states (docs/341 §6.1 names): `Live`, `GoFarPending`, `FarHome`, `FarSleep`, `FarTransit`, `FarWork`,
  `FarSchool`, `FarVenue`, `VisitProposed`, `PendingLive`, `Excluded`. 24 transitions (§5), each with trigger, guard,
  inputs and action.
- The far state of a Sim at sim time t is a **pure function** of (seed, go-far snapshot, recorded inputs, t). The due
  queue only delivers the side effects of a state change (move the hidden object, propose a visit, write the trace).
  So catch-up never replays missed transitions: it evaluates the state at t once (§7.3).
- Needs are not stepped and not written at go-live. The machine reads them as EA's own low-LOD form
  `curve_m((hour(t) + delta_m + 24) mod 24)` [verified, docs/341 §0, §2]. At go-far it sets the phase `delta_m` once;
  at go-live EA's own motive update produces the value (§6.1). This replaces the decay integration of docs/324 §2.2.
- Random numbers: SplitMix64 finaliser, counter based, keyed by (world seed, `SimDescriptionId`, model version,
  episode, day, plan revision, slot). No running stream, so evaluation order cannot change a result (§8).
- Household Sims, the selected Sim and Sims of unknown scope sit in `Excluded` and never leave it while that holds.
  The check is repeated at each of the 9 entry points (§10). `[Experimental] HouseholdToo` does not apply.
- Go-live hands EA a Sim that is standing, idle, with an empty queue, placed by EA's own placement call, out of view.
  EA's autonomy picks the next action. Fallback: `Sim.AttemptToPutInSafeLocation(sendHome: true)` (§9.3).
- Everything is default off. One master switch, six per-feature switches, a per-state kill list and an automatic
  stand-down (§14).
- Open: 9 questions (§16), none of them for Rodney. Answered by Rodney 2026-11-26: Q1 no -40 clamp at go-live, the
  difference is counted in the shadow tests; Q2 no go-live while paused, far Sims at a venue the camera moved to
  appear at unpause; Q3 EA runs work shifts in the first build, `FarWork` is built behind `farsimwork`, default off.

## 1. What this doc changes in earlier docs

| Earlier text | Here | Why |
|---|---|---|
| docs/340 §3.3 state names (`HomeIdle`, `Sleep`, `Commute`, `Work`, `School`, `Venue`) | docs/341 §6.1 names (`FarHome`, `FarSleep`, `FarTransit`, `FarWork`, `FarSchool`, `FarVenue`) plus `GoFarPending` | one name set; the docs/343 §3.4 example line uses the old names and is only an example (`st` is written by name) |
| docs/324 §2.2: needs by decay integration with seeded meal, hygiene and bladder events | EA's auto-satisfy curve of the hour plus one phase per motive | EA already does this for low-LOD Sims [verified, docs/341 §2]; the hand-over is then a state EA produces itself |
| docs/340 §3.4: `PRNG(seed, transition index, draw index)` | `PRNG(seed, episode, day, revision, slot)` (§8) | a running transition count makes a result depend on how many transitions were replayed; catch-up must not replay |
| docs/324 §6.3: periodic pass every 5 s real time | pass every `BoundaryPassMin` sim minutes | no wall clock in the machine (docs/341 H3, H10) |
| docs/324 §2.2 partial-effect credits (`c` of the docs/343 `S` line) | no credits in model version 1 | needs do not depend on the last interaction any more; answers docs/343 open question 7 for `S.c`: `ep` and `rev` only (§12) |

## 2. Scope and units

- Covers stage 2a (docs/340 §3.6 L0: the Sim object is kept, hidden and inert). Stage 2b (no Sim object) reuses the
  same table; its extra guards and its go-live build are SD-4 and RX-9 (docs/344 §1 draft verdict: Sims are the
  first L2 target; RX-6 reader classification still open). Rows that differ are marked "2b".
- Boundary repros, the pre-expand ring size and the eight contract-line repros are SD-3. This doc fixes the guards
  SD-3 tests.
- Sim time is an integer tick count (docs/341 H5). Durations below are written in sim minutes and converted once with
  EA's own ticks-per-minute constant, in integer arithmetic. No float is stored or compared by the machine; distances
  are integer centimetres.
- "Viewer points" = camera target, camera position and every household Sim (docs/340 §3.2).

## 3. Record and inputs

### 3.1 Per-Sim record

One record per Sim that is not `Live`, in one table keyed and sorted by `SimDescriptionId` (never an object-keyed
dictionary, H7). A `Live` Sim has no record, except a small dwell entry while it is far but still inside the dwell
time (§4.2).

| Field | Type | Meaning |
|---|---|---|
| `id` | u64 | `SimDescriptionId` |
| `state` | u8 | ordinal of §4.1 |
| `enteredAt` | tick | nominal tick of the last transition |
| `nextDue` | tick | next scheduled edge, or none |
| `ti` | u32 | count of logged transitions of this Sim (trace order only, not a PRNG input) |
| `ep` | u32 | episode: number of go-fars of this Sim in this world |
| `rev` | u32 | plan revision: number of plan-changing inputs logged in this episode (§3.2 class P) |
| `place` | kind + id | home lot id, rabbit-hole object id, venue lot id, or (from, to, start tick, arrival tick) |
| `why` | u8 | reason of the last transition (§5.2) |
| `snap` | index | go-far snapshot (§9.1) |

Size: about 64 bytes plus the snapshot (about 100 bytes) [inferred]. Held timers live in the hold hook's side lists (docs/346), not here.

### 3.2 Inputs

The complete list (docs/341 §5 "allowed inputs"). Anything not on it must not reach a transition.

| # | Input | Source | Class |
|---|---|---|---|
| I1 | sim tick; from it day index, day of week, hour | `SimClock` ticks | clock |
| I2 | world seed, model version `fs` | sidecar `W` line (docs/343) | constant |
| I3 | `SimDescriptionId` | EA | constant |
| I4 | go-far snapshot | captured at T4 (§9.1) | recorded |
| I5 | tuning tables: auto-satisfy curves, career and school hours, venue hours, wake time rule | EA tuning, read once per world load; hash kept as `tuneHash` | constant per load |
| I6 | world-history ring: weather, season, holiday | docs/343 `H` lines | recorded |
| I7 | boundary event near / far per Sim | boundary pass (§4.2) | recorded, **R** |
| I8 | forced event per Sim: observation, household-facing event, EA or mod push, scope change, exclusion reason, switch off, flush, stand-down | hooks (FS-8, FS-9) | recorded, **R** |
| I9 | plan-changing event per Sim: fire or burglary on its home lot, changed job, home or school, an NPC invite | hooks; description compare at the daily edge | recorded, **P** (bumps `rev`) |
| I10 | answer of an EA predicate the plan asks (`ea_ok` of a visit, venue open now, `IsWorkHour` for careers with overrides) | EA method, called once, answer logged before use | recorded, **R** |

Class R and P inputs are written to the trace (§13.1) before the machine acts on them. A replay feeds the recorded
answer and never calls EA.

## 4. States, boundary and hysteresis

### 4.1 States

| # | State | Owner | Meaning | EA-side shape (stage 2a) |
|---|---|---|---|---|
| 0 | `Live` | EA | normal Sim | untouched |
| 1 | `GoFarPending` | EA | far and eligible; waiting for a safe point | untouched; the machine only watches |
| 2 | `FarHome` | machine | awake at home | object on the home lot, hidden the way EA's low-LOD hide does it (opacity 0, `WasHiddenForLowAutonomy`, docs/341 §1.5), parked (§9.2) |
| 3 | `FarSleep` | machine | asleep at home | as `FarHome` |
| 4 | `FarTransit` | machine | between two places | object stays hidden at the place it left until arrival (it is never put on a road) |
| 5 | `FarWork` | machine | inside the career rabbit hole during a shift | object hidden at the rabbit hole's hide position; only with `farsimwork` on |
| 6 | `FarSchool` | machine | as `FarWork` for the school | as `FarWork` |
| 7 | `FarVenue` | machine | on a community lot or in a non-work rabbit hole | object hidden on that lot |
| 8 | `VisitProposed` | machine | at home with a visit to a household lot drawn for today | as `FarHome` |
| 9 | `PendingLive` | machine until done | go-live requested; waiting in the spread queue or being restored | being restored through EA calls only |
| 10 | `Excluded` | EA | never far while the reason holds | untouched |

Invariants (selftest and queue checks):

- V1. One owner: a Sim is owned by EA (0, 1, 10) or by the machine (2-9), never both (docs/324 §3.1).
- V2. A household Sim, the selected Sim or a Sim of unknown scope is only ever in state 10 (or 0 for the tick before
  the first pass sees it). Counter `farsimHouseholdInFar` must be 0.
- V3. A machine-owned Sim makes no autonomy decision, has no heartbeat and runs no `SimUpdate` step (§9.2).
- V4. The state stored equals `F(seed, snap, inputs, enteredAt)` (§7.1) for states 2-8. The shadow build asserts it.
- V5. No record for a Sim whose `SimDescription` is gone (§13.3).

### 4.2 Boundary and hysteresis

`d` = distance from the Sim (or from its `place` when machine-owned) to the nearest viewer point, in the XZ plane.

| Key (`[FarSim]`, metres or sim minutes) | Default | Source |
|---|---|---|
| `LiveRadiusM` | 150 | docs/340 §3.2 T1 |
| `HandoffRadiusM` | 200 | docs/324 §1.3 (T1 to T2) |
| `FarRadiusM` | 450 | docs/340 §3.2 T3 |
| per-world edge, sidecar `K` line `t3EdgeM` | Bridgeport 450, Isla Paradiso 450, Sunset Valley 300 | working values [inferred, docs/322 §4: the band rerun data was lost; a rerun replaces them]; other worlds use `FarRadiusM` |
| `FarDwellMin` | 30 | docs/340 §3.2 |
| `ParkedDwellMin` | 60 | docs/324 §1.3 (in a rabbit hole or on an unseen lot for 1 sim-hour) |
| `BoundaryPassMin` | 5 | this doc [inferred] |
| `GoFarTimeoutMin` | 120 | this doc [inferred] |
| `GoFarRetryMin` | 60 | this doc [inferred] |
| `FlapWindowMin` | 10 | docs/340 §3.2 |

Rules:

1. **Far** (go-far may start): `d > R_far` continuously for `FarDwellMin`, or (`d > HandoffRadiusM` and the Sim has
   been inside a rabbit hole or on a lot no viewer point is on for `ParkedDwellMin`). `R_far` = the world's
   `t3EdgeM`, else `FarRadiusM`. In both cases no near trigger (rule 3) during the dwell.
2. **Near** (go-live must start): `d < R_live + ring`. `R_live` = `LiveRadiusM`. `ring` = the pre-expand ring
   (time-to-view, docs/324 §6.2-6.3); its formula is SD-3 / FS-11. The machine clamps it so that
   `R_live + ring <= R_far - 50 m`. The two radii therefore never touch: with the working values the gap is at least
   50 m and normally 150 m (Sunset Valley) or 300 m.
3. **Near triggers** at any distance (docs/340 §3.2, docs/324 §3.6-3.7): the Sim's place is a household lot, a lot a
   household Sim is on, or the destination of a household Sim's route or travel; a household Sim starts an
   interaction with it or picks it in a picker; it is the target of a call, invite or opportunity; it is scheduled as
   a visitor or service for the household; one of the 10 EA read paths of docs/342 §2 that build the Sim or queue an
   interaction on it runs; it is observed. Observation hooks [verified sites, docs/342 §3-4]: `Camera.FocusOnSim`
   and `Camera.FollowSim` (every camera jump to a Sim), `NPCSimMapTag.ClickedOn` / `MapTag.ClickedOn`, forced
   selection, and as lists the `HudModel.CurrentRelationships` getter and the Sim pickers. EA has no Sim info panel
   for an NPC, so that path of docs/324 decision 8 needs no hook (docs/342 Q1).
4. **Tie rule** (docs/324 §1.3): a Sim with a family, romance, best-friend, coworker or classmate tie to a household
   Sim, or a pending opportunity or call, is not eligible for go-far (it stays in the hand-off tier). It is in state
   `Excluded` with reason `tie`.
5. The boundary pass runs every `BoundaryPassMin` sim minutes of sim time, over a sorted id list, sliced with a
   count budget (H3: never a clock). Viewer points are sampled once at the start of a pass. Each crossing it finds is
   logged as an I7 input and only then applied.
6. Near triggers and forced events do not wait for the pass: their hook logs an I8 input and requests the
   transition at once.
7. `flap` counts a go-far (T4) within `FlapWindowMin` of that Sim's last go-live. It must be 0 in the SD-3 sweep job.
8. While the game is paused or not live, the pass does not run and requests are only queued (§11).

## 5. Transition table

### 5.1 Table

Guards: `G-hh` = not household, not selected, scope known (§10); `G-elig` = eligible (§5.3); `G-safe` = safe point
(§5.4); `G-run` = live, unpaused, master switch on, not stood down (§11, §14). Every row needs `G-run`, except T15, T18
and T19 with reason `flush`, `switchOff` or `quit`, which must run to give EA its Sim back. "Draws" names the slots of §8.3.

| T | From | To | Trigger | Guard | Inputs | Action | Draws |
|---|---|---|---|---|---|---|---|
| T1 | `Live` | `GoFarPending` | boundary pass: far (rule 1) | G-hh, G-elig, not within `GoFarRetryMin` of a T3 | I1, I7 | add record; `nextDue` = now + `GoFarTimeoutMin` | - |
| T2 | `GoFarPending` | `Live` | near (rule 2), near trigger, or G-elig lost | - | I7 / I8 | drop record | - |
| T3 | `GoFarPending` | `Live` | timeout | - | I1 | drop record; count `noSafePoint`; remember retry time | - |
| T4 | `GoFarPending` | `F(t)`: one of 2-7 | safe point seen (polled by the boundary pass and on the Sim's queue-empty event) | G-hh and G-elig **re-checked**, G-safe, not visible to the camera, not on a household lot | I1, I4 | `ep`++; `rev` = 0; snapshot (§9.1); set phase (§6.1); park (§9.2); schedule next edge | phase, plan |
| T5 | `FarHome` | `FarSleep` | due: bed time `W - 8 h - j` | - | I1, I5 | none (place unchanged) | sleepJitter |
| T6 | `FarSleep` | `FarHome` | due: wake time `W` | - | I1, I5 | none | - |
| T7 | `FarHome` | `FarTransit` (to work or school) | due: shift start minus travel time | `farsimwork` on; `IsWorkHour` answer for that day (I10) | I1, I5, I10 | set arrival tick | - |
| T8 | `FarTransit` | `FarWork` / `FarSchool` | due: arrival | shift still running | I1, I5 | move hidden object to the rabbit hole's hide position | - |
| T9 | `FarWork` / `FarSchool` | `FarTransit` (to home) | due: shift end | - | I1, I5 | work-day effects through the EA setters of §6.2; set arrival tick | - |
| T10 | `FarHome` | `FarTransit` (to venue) | due: start of a free block of at least 2 h | `farsimvenue` on; venue from the open-lot list; venue open (I10) | I1, I5, I6, I10 | set arrival tick | venuePick, stayLen |
| T11 | `FarTransit` | `FarVenue` | due: arrival | venue still open (I10), no household Sim on it (else T15) | I1, I10 | move hidden object to the venue lot | - |
| T12 | `FarVenue` | `FarTransit` (to home) | due: stay end, venue closing time, or a bed, shift or visit edge | - | I1, I5 | set arrival tick | - |
| T13 | `FarTransit` | `FarHome` | due: arrival | - | I1 | move hidden object to the home lot | - |
| T14 | `FarHome` | `VisitProposed` | due: daily visit draw says yes | `farsimvisit` on; candidate rule of docs/324 §3.9; `ea_ok` (I10) | I1, I10 | `nextDue` = visit time minus `PrebuildHours` | visitDraw, visitHour |
| T15 | 2-8 | `PendingLive` | near (rule 2); near trigger; forced event (I8) | - (never refused) | I7 / I8 | enqueue go-live with priority (§9.3) | - |
| T16 | `VisitProposed` | `PendingLive` | due: visit time minus `PrebuildHours` | `ea_ok` re-asked (I10) | I1, I10 | enqueue go-live, reason `visit`; EA's `VisitLot` is pushed after T18 (docs/324 §3.9) | - |
| T17 | `VisitProposed` | `FarHome` | `ea_ok` false at T16, or `farsimvisit` turned off | - | I10 / I8 | none | - |
| T18 | `PendingLive` | `Live` | restore finished (§9.3) | all contract checks pass | - | drop record; count `goLive` | - |
| T19 | `PendingLive` | `Live` | a restore step failed | - | - | fallback (§9.3); count `goLiveFallback`; drop record | - |
| T20 | `Live` | `Excluded` | exclusion reason found (first pass, or I8) | - | I8 | add a small record with the reason | - |
| T21 | `Excluded` | `Live` | reason gone, seen by the boundary pass | G-hh (a household Sim never leaves) | I8 | drop record | - |
| T22 | 2-8 | `F(t)` | catch-up: `nextDue` is older than one sim day, or first drain after a time jump | - | I1 | evaluate `F(t)` once; move the hidden object once; schedule next edge | plan (day of t) |
| T23 | 2-8 | same state | plan-changing event (I9) | - | I9 | `rev`++; re-evaluate `F(t)`; if the state differs, apply as T22 | plan |
| T24 | any | (record dropped) | the `SimDescription` or the Sim object is destroyed by EA or a mod | - | I8 | drop record and snapshot; `FarHold.Release(id)`; count `gone` | - |

Not transitions: a save flush (§9.4) leaves the state as it is; needs, mood and buffs are never states.

An exclusion reason that appears while a Sim is machine-owned is a forced event: T15 with that reason, then T18, then
T20 on the same tick. A Sim never goes from a far state to `Excluded` directly, because `Excluded` means EA owns it.

### 5.2 Reasons (`why`)

`far`, `near`, `ring`, `trigger`, `observed`, `eaPush` (an `InteractionQueue.Add`, a position set or a cancel on a
machine-owned Sim, docs/324 §5.5), `visit`, `scope` (household, selected or unknown), `excl:<name>`, `tie`,
`noSafePoint`, `flush`, `switchOff`, `standDown`, `quit`, `catchUp`, `plan`, `gone`, `fallback`. Written by name.

### 5.3 Eligibility (`G-elig`) and exclusion reasons

A Sim is eligible only when none of these holds. Each is an `Excluded` reason with its own counter
`farsimExcl.<name>`.

| Reason | Rule | Source |
|---|---|---|
| `household` | member of the active household | CLAUDE.md hard rule |
| `selected` | the selected Sim | same |
| `unknownScope` | no fresh household table, id not resolvable, mid-travel, mid-household-change | docs/324 §1.2 |
| `tie` | §4.2 rule 4 | docs/324 §1.3 |
| `noHome` | no `LotHome` (homeless townie, service NPC): EA's own off-world handling stays | docs/324 §2.3 item 5 |
| `situation` | member of any situation | docs/341 §2 |
| `service` | service or active role, on duty or assigned | docs/341 §1.3, docs/324 §3.6 |
| `pregnant` | pregnant or in labour (2a too: `Pregnancy.Continue` and its alarms are household-facing in effect) | docs/340 §3.3 |
| `ageUp` | age transition due within `PrebuildHours` or in progress | docs/324 §3.5 |
| `dying` | dying, ghost in transition, non-playable ghost, tomb mummy | docs/324 §3.5, docs/341 §4 |
| `pendingEvent` | a known household-facing event within `PrebuildHours` | docs/324 §3.5 |
| `placeholder` | `Sim.Placeholder` child | docs/324 §3.5 |
| `pet` | pet of the household | docs/324 §3.5 |
| `timedBuff` | has a timed buff that is not motive driven. Strict first rule; relaxed to "a timed buff on the side-effect list" when docs/341 Q3 is answered | [pending docs/341 Q3] |
| `heldClass` | owns a timer whose callback is in the pass-through set of docs/346 §3.3 (must fire on time, docs/331 E1). The set ships empty; until the 45 "shows UI" callbacks on Sim owners are read (docs/346 Q7, §16 Q10) every one of them counts as pass-through, so its owner stays live | docs/346 §3.3, §4.3 |
| `modOwned` | a compat row says another mod owns this Sim (NRaas StoryProgression precedence is FS-14; sites in docs/347 §3) | docs/347 §3 |
| `readerNeedsSim` | (2b only) one of the five reads that are wrong without a Sim object is active for it (docs/342 §2.8), or an unclassified reader | docs/342 §2.8; [pending RX-6] |

### 5.4 Safe point (`G-safe`)

All must hold on the tick of T4 (docs/341 §6.1, §6.5; docs/324 §3.2). It is the state `Sim.DoReset` would leave,
reached by waiting, never by calling the reset.

1. Interaction queue empty and no running interaction; not inside an autonomy decision (not at the head of an
   `AutonomyManager` queue being scored).
2. Standing posture, no carried object or child, no parent or container, not in a vehicle.
3. Not routing, not inside a portal.
4. Not on a household lot; not on screen (`World.IsObjectOnScreen` false) and outside `R_far` by rule 1.
5. Not saving, travelling, in Edit Town, Build / Buy or CAS (`G-run`).
6. No reservation held by or on the Sim; not in a household-facing queue (phone, invite, guest list).

The machine never cancels an interaction to make a safe point (docs/324 §3.2 allowed cancelling low-priority
autonomous ones; dropped here: waiting costs nothing and a cancel is a behaviour change). `noSafePoint` shows the
cost of that choice in the shadow build.

## 6. Computed effects per state

### 6.1 Needs (every far state)

- Read: `need_m(t) = curve_m((hour(t) + delta_m + 24) mod 24)` for a motive with an auto-satisfy curve; a motive
  without one keeps its snapshot value [verified form, docs/341 §2; `Motive.cs:902-938` cited there]. The state does
  not enter the formula.
- `delta_m` at T4: if EA's motive already carries a phase (`AutoUpdated` set), the snapshot takes that value and no
  draw is made, so the curve continues without a step. Otherwise `delta_m` = draw slot `phase + commodity id`
  mapped to EA's phase range, and the machine writes `MotiveTimeDelta` and `AutoUpdated` on the Sim's motive
  [inferred; not answered by docs/342; §16 Q6]. That is the only needs write.
- Go-live: nothing is written. EA's own `Motives.UpdateMotives` takes the auto-satisfy path for a low-LOD Sim and for
  a high-LOD Sim not updated for more than 1 h [verified, docs/341 §2], and gives the same value the machine read. A
  far episode shorter than 1 h falls into EA's normal decay over the elapsed time, which is EA's own result as well.
- Follow `AutoSatisfyMotive`, not `GetInitialValue` (docs/341 Q4).
- **No clamp at go-live** (Rodney 2026-11-26, §16 Q1; this replaces docs/324 decision 2, "never go live below
  -40"). The value is EA's own low-LOD value, and a clamp written by us would jump back on EA's next update. The
  difference is counted instead, in the shadow build (FS-2) and after it: `farsimCurveBelowClamp` = motives whose
  curve minimum is below -40 (once per tuning load); `farsimGoLiveBelow40` = go-lives (shadow: would-be go-lives)
  where at least one need reads below -40, with the lowest value seen in `farsimGoLiveNeedMin`;
  `farsimGoLiveDistress` = of those, Sims that enter motive distress within 10 sim minutes of go-live (docs/347
  I-616-2). FS-4 reports the three per world next to the same counts for EA's own low-LOD Sims coming near.
- Motive buffs and mood: never computed. EA derives them on its first heartbeat after go-live (docs/341 §2).
- Timed buffs: not advanced while far (no heartbeat, V3). EA expires overdue ones on its first heartbeat after
  go-live. EA does the same to a low-LOD Sim that reaches no decision [verified, docs/341 §1.6]. `timedBuff` (§5.3)
  keeps Sims with such buffs live until Q3 says which ones are safe.

### 6.2 Day plan and schedules

`Plan(day)` is a sorted list of at most 12 blocks (start tick, state, place), built lazily and cached per
(`id`, day, `ep`, `rev`). Rules from docs/324 §2.3, reduced to what the states need:

| Block | Start | End | Draw slots |
|---|---|---|---|
| sleep | `W - 8 h - j`, j in 0..90 min | `W` (`Occupation.WakeupTime`, else 6:00) | `sleepJitter` |
| work / school (only `farsimwork` on) | shift start minus travel time | shift end plus travel time | - |
| venue (only `farsimvenue` on) | start of a free block of at least 2 h, if the draw picks a venue | seeded stay of 60..180 min, cut at closing time and at the next sleep, shift or visit edge | `venuePick`, `stayLen` |
| visit (only `farsimvisit` on) | drawn hour inside a free block | hand-over to EA at T16 | `visitDraw`, `visitHour` |
| home | everything else | | - |

- Travel time between two places = straight-line distance times 1.3, divided by a speed from a three-row table
  (car, bike, walk; from the description), rounded up to whole sim minutes, at least 20 (docs/324 §2.4, §3.9).
- Career hours, days and overrides: the answer of EA's own `IsWorkHour` / `ShouldBeAtWork` is taken once per day per
  Sim as an I10 input, not copied from tuning (docs/324 §2.3).
- Meals, hygiene and bladder events of docs/324 §2.2 are gone (§1).
- **Work shifts, `farsimwork` off (first build):** the machine has no work block. EA's work-day alarm pushes the work
  interaction to the hidden Sim because the object exists [verified, docs/324 §3.4]; that push is an `eaPush` forced
  event (T15). EA then runs the shift, pay and performance itself. After the shift the Sim goes far again by T1 / T4.
  `FarWork` and `FarSchool` are unreachable in this mode.
- **`farsimwork` on:** the shift is a `FarWork` block. Pay and performance are applied at T9 through
  `Household.ModifyFamilyFunds` and `Career.AddPerformance` [verified setters, docs/347 §1.1]. The work-day alarm
  only reads the Sim and is not held (docs/324 §5.2, docs/346 §3.3): its push needs its own guard, not designed
  here. The T3 facade then also needs `RabbitHoleCurrent` and the mood read of the career mood metric
  (docs/342 §2.5, §2.7).
- **Decision (Rodney 2026-11-26, §16 Q3):** the first build ships with `farsimwork` off, so EA runs the shifts and
  their load can be measured. `FarWork` / `FarSchool` are built as well, behind `farsimwork` (default 0), so an
  A/B can show the gain (§17 M20). The switch is read at runtime, per shift edge: a Sim already inside a shift
  finishes it under the mode it started in.

### 6.3 Position

- `Place(t)` = the block's place. In `FarTransit` the machine keeps (from, to, start, arrival); a facade read that
  wants a point gets the linear point between the two lot centres at t (integer arithmetic). The hidden object is
  **not** moved along the way; it moves once at arrival (T8, T11, T13), to a spot from EA's placement on that lot.
- That move is a position set on a hidden, unseen, non-household NPC. It is a teleport by the project rules, so it is
  never done for household, selected or unknown-scope Sims (they are never far) and never while the Sim is on screen
  (a near Sim is already `PendingLive`).

### 6.4 Drift (relationships, skills, career)

Only with `farsimdrift` on. Rules of docs/324 §2.7 stand: plan-driven, bounded per day and per far stretch, below
every life-event threshold, never a pair with a household, selected or unknown-scope Sim on either side (shared
predicate on both ids; counter `driftHousehold` must be 0).

- Evaluated lazily at go-live and at the save flush, for the elapsed days of the episode, at most `DriftDaysMax`
  (default 14) days: the per-stretch caps of docs/324 §2.7 are reached long before that.
- Applied through EA's setters, one per kind, none needs a Sim object: `LongTermRelationship.SetLiking`,
  `Skill.AddPoints`, `Career.AddPerformance` [verified, docs/347 §1.1]. A relationship write that stays inside the
  current state shows nothing, and a skill level-up is silent for a non-selectable Sim [verified for the base
  class, docs/347 §1.2-1.3]. Skill subclasses that override `AddPoints` are not read yet (docs/347 I-616-3): until
  then skill drift is shadow only.
- NRaas StoryProgression writes career performance only for Sims with a body (docs/347 §3), so career drift is not
  counted twice in 2b; in 2a career drift stays off unless `farsimwork` is on.
- Ledger = docs/343 `D` lines (§12).

### 6.5 Alarms the Sim owns

Sim alarms sit on the global alarm heap, owner = `Timer.ObjectRef`, one dispatch site `AlarmManager.Simulate`
[verified, docs/341 §1.2, docs/346 §1]. The machine uses the shared hold hook of docs/346 §3 and adds nothing to it:

- T4 adds the Sim's id to the `FarHold` held-Sim set. A due timer of a held owner is taken out of the heap into a
  side list; it stays removable and updatable through EA's handle API [verified, docs/346 §0].
- Go-live calls `FarHold.Release(id)`: timers are put back and EA dispatches them on its next global `Simulate`,
  one fire per timer however long the hold was (EA's own late-repeat rule), in the manager's own order. The machine
  fires nothing itself. Order inside one tick is EA's heap order (H6); the trace does not record alarm fires.
- Timers whose owner is a `SimDescription`, `Household`, situation, service or role are never held (docs/346 §3.3).
  They may fire for a far Sim. One that pushes onto the Sim arrives as `eaPush` (T15); §16 Q8 is the gap check.
- The hook's own household test (docs/346 §3.6) is EP8 of §10. Switch `farhold` (docs/346). `farsimt3` needs it
  on: nearly every Sim owns timers (413 registration sites with a Sim owner, docs/346 §4.2), so without the hold a
  parked Sim would be run by its own alarms. With `farhold` off there is no T4.

## 7. Evaluation, due queue and catch-up

### 7.1 `F(t)`: state as a pure function

`F(seed, snap, inputs, t)` = the state and place of the block of `Plan(day(t))` that contains t, where the plan is
built from I1-I6, the recorded I9 / I10 answers and the draws of §8. If the block's place differs from the previous
block's place and t is inside the travel time, the state is `FarTransit`. No other data is read.

### 7.2 Due queue

- One binary heap of (`nextDue`, `id`), ordered by due tick then id (H11). Entries are records, not Sims.
- Drained once per sim tick on the sim thread: all entries with `nextDue <= now`, at most `DueBudget` (default 8)
  per tick. The rest stay for the next tick. The budget is a count, never a time (H3).
- A transition is logged with its **nominal** due tick, not the tick it was drained on. A full budget therefore
  delays side effects by ticks but does not change the trace.
- Cost follows transitions due now: about 4-8 per far Sim per sim day [inferred, docs/324 §5.4], not population times
  tick rate.

### 7.3 Catch-up bound

The machine never replays. When a record is more than one sim day behind, or at go-live, flush or load, it evaluates
`F(t)` once (T22). Worst case per Sim, independent of how long it was far:

| Step | Bound |
|---|---|
| plan for day(t) | 1 plan, at most 12 blocks, at most 6 draws |
| needs | one curve lookup per motive (about 10) and only when something reads them |
| drift | at most `DriftDaysMax` (14) plan evaluations, only with `farsimdrift` on |
| held alarms | none of ours: `FarHold.Release` puts timers back, EA fires each once (docs/346 §3.4) |
| object move | at most 1 |

All integer arithmetic on a few hundred bytes. FS-1's selftest reports microseconds per `F(t)` and per go-live
evaluation; the number goes into FS-11's spread budget [inferred until then].

## 8. Seed and PRNG

### 8.1 Mix function

SplitMix64 finaliser, all arithmetic modulo 2^64:

```
M(z):  z = z + 0x9E3779B97F4A7C15
       z = (z xor (z >> 30)) * 0xBF58476D1CE4E5B9
       z = (z xor (z >> 27)) * 0x94D049BB133111EB
       return z xor (z >> 31)
```

### 8.2 Seed and draw

```
seed      = M( M( M(worldSeed) xor SimDescriptionId ) xor modelVersion )
draw      = M( M( M( M(seed xor ep) xor day ) xor rev ) xor slot )
range(n)  = ((draw >> 32) * n) >> 32          integer in 0 .. n-1, n < 2^32
```

- `worldSeed`: 64 bits, created once per world from the OS random source, kept in the docs/343 `W` line. It is the
  only value that does not come from the save or the tick. No sidecar = a new seed (docs/343 §0).
- `day` = day index of the block's start; `ep`, `rev` from the record. Episode-wide draws use `day = 0xFFFFFFFF`.
- No draw comes from `RandomUtil`, `RandomGen` or `System.Random` (H1, H2), so EA's stream is not shifted either.
- Results never pass through a float. Minutes, metres and weights are integers; a weighted pick uses integer
  cumulative weights and `range(total)`.

Test vector (FS-1 selftest must reproduce it): `M(0)` = `e220a8397b1dcdaf`; with `worldSeed` = `9c41d2e07b53a611`,
`SimDescriptionId` = 7215113988226514, `modelVersion` = 1: `seed` = `2aebc8589dd9f080`; `draw(ep 0, day 12, rev 0,
slot 3)` = `a0fea6b83f171b02`; `range(90)` = 56.

### 8.3 Slots

| Slot | Name | Key day | Used by |
|---|---|---|---|
| 0x100 + commodity id | `phase` | episode | §6.1 |
| 1 | `sleepJitter` | day | T5 |
| 2 | `venueGo` (venue or stay home) | day, plus block index in bits 8-15 | T10 |
| 3 | `venuePick` | same | T10 |
| 4 | `stayLen` | same | T10 |
| 5 | `visitDraw` | day | T14 |
| 6 | `visitHour` | day | T14 |
| 0x200 + ledger kind, mixed with the other id by one more `M` | `drift` | day | §6.4 |

A new slot or a changed table bumps the model version `fs` (docs/343 §4): the seed changes and old `S` / `D` / `E`
lines of that owner are discarded.

## 9. Go-far and go-live

### 9.1 Go-far snapshot (T4)

| Field | Use |
|---|---|
| tick of go-far, lot id and position | `enteredAt`; fallback position; trace |
| per motive: value, `delta_m`, had-phase flag | §6.1 |
| `tuneHash`, model version | detect a tuning change inside an episode (then T15, reason `standDown`, for that Sim) |
| occupation id and level, home lot id, school id | plan inputs; compared at each daily edge to raise I9 |
| drift baselines (relationship, skill, career values the ledger starts from) | §6.4, only with `farsimdrift` on |
| outfit category shown | check at go-live (contract line 6) |

The snapshot is memory only. After a load every Sim is `Live` and goes far again by the normal rules with the loaded
EA state as the new snapshot (docs/324 §2.6), so no snapshot is ever saved.

### 9.2 Parking (stage 2a)

Required effect (V3): neither of the Sim's two scheduler entries runs, and the Sim is in no `AutonomyManager` queue
[verified that these are all, docs/341 §1.2; object scripts not swept]. Candidate mechanisms, chosen by FS-7 with
SD-3:

- a. docs/341 §6.5: park both entries natively and call `AutonomyManager.RemoveActor`. Needs the sleep-list RE
  (docs/341 Q1) and a byte-level read of the walk (Q7).
- b. docs/324 §5.2: one parked interaction that yields until go-live, autonomy off for the Sim. Managed only, but
  `SimUpdate` still runs every tick (docs/341 §1.5), so it does not meet V3 fully.

Either way the hide uses the flags EA's low-LOD hide uses (docs/341 §1.5), set and cleared by us only on Sims in the
ownership table, so the `CleanOnLoad` scan (docs/343 §7) can tell ours from EA's.

The object stays in the world (`InWorld` true); this answers docs/347 open question 5. NRaas Overwatch's
`StuckCheck` and `ResetCheck` (both default on) will therefore reset a parked Sim that does not move
[verified, docs/347 §3.2]. Until FS-17 ships the exemption of docs/347 §3.3, `farsimt3` stands down when Overwatch
is loaded (shadow still runs), and a reset of a machine-owned Sim by any mod is an `eaPush` forced event.

### 9.3 Go-live restore (T15 / T16 to T18)

Runs through the spread queue (FS-11): priority = time-to-view, `observed` and `eaPush` at 0; at most
`GoLiveOpsPerFrame` (default 4) stage 2a restores per sim frame. `eaPush`, `flush`, `switchOff` and `quit` run at
once, outside the budget (docs/324 §5.5).

Steps, in order. Any failure goes to the fallback.

1. Re-check one owner and one instance (FS-5 guard). 2b: `SimDescription.Instantiate` builds the Sim; EA's
   `Sim.OnCreation` then gives fresh managers, needs from the curve, an empty queue and a standing Sim
   [verified, docs/341 §3-4]; the rest of 2b is SD-4.
2. Evaluate `F(t)` (catch-up, §7.3).
3. Place: a spot from EA's placement for the place in `F(t)` (home lot spawn, rabbit-hole slot, venue arrival point),
   route-checked, never inside the camera frustum; a Sim that must appear while its place is in view is put at the
   nearest out-of-view point and routes in (docs/324 §3.3, §6.4). `FarTransit` uses the place it left.
4. Unpark: restore both scheduler entries, clear our hide flags, opacity 1 unless EA's own low-LOD hide applies at
   that spot (docs/341 §4 line 2).
5. `FarHold.Release(id)` (§6.5), ordered so that EA dispatches the timers before the Sim's first live tick
   (docs/346 §3.4).
6. Apply drift (§6.4).
7. Check contract lines 3, 6 and 7 of docs/341 §4: standing, empty queue, no carry; outfit valid; `IsAtWork` false.
8. **Start from idle.** No interaction is pushed (docs/324 §3.3 allowed a push of the implied activity; not in model
   version 1). EA's `Sim.Simulate` idle loop queues the Sim for autonomy and EA picks the next action
   [verified loop, docs/341 §1.3]. The only push the machine ever makes is EA's `VisitLot` after a `visit` go-live
   (docs/324 §3.9).
9. Log T18.

**Fallback (T19):** confirm the queue is empty, then `Sim.AttemptToPutInSafeLocation(sendHome: true)`
[verified, docs/341 §4: EA's own teleport to a routable spot at home; re-adds the object, clears hidden flags,
opacity 1], unpark, release held alarms, log once, count `goLiveFallback`. If that call fails too: `Sim.DoReset`
path through EA's reset, counted as `goLiveReset`. The Sim is never left half restored.

Go-live applies only to Sims the machine owns, and those are unseen non-household Sims by V2 and §5.4 rule 4.

### 9.4 Save flush

Hooked before EA's save (docs/343 §6; open question 3 there for mod save paths). For every machine-owned Sim, in id
order, with no budget:

1. Evaluate `F(t)`; make sure the hidden object is at the place of `F(t)`.
2. Fire nothing and re-arm nothing: the hold hook's own bracket around `AlarmManager.Write` puts held timers back
   for the save and takes them out again after it (docs/346 §3.5, Hook B), so EA saves them as its own with their
   original due times.
3. Apply drift so far and reset the ledger baselines.
4. Clear our hide and park marks for the duration of the save so the saved object is a plain EA Sim [inferred;
   which of the flags EA persists is §16 Q6]; set them again after the save through the spread queue.
5. Write `S`, `D`, `E` lines (§12) after the `.sims3` succeeded.

The state does not change. A step that fails counts `farFlushFailed` (docs/343 §10) and promotes that Sim (T15,
reason `flush`).

## 10. Household, selected Sim and unknown scope: every entry point

The test is the shared predicate (`Fixes.HouseholdExempt` in ts3fixes, `HhExempt` in ts3native) plus "scope known".
`[Experimental] HouseholdToo=1` is **not** read at any of these sites: parking is a skip and the object move is a
teleport (docs/340 §1.3, docs/341 §6.5). A stale household table makes every Sim `unknownScope`.

| # | Entry point | Check | If it fails |
|---|---|---|---|
| EP1 | boundary pass, before T1 | predicate + scope | T20 (`Excluded`), counter `farsimExcl.household` / `selected` / `unknownScope` |
| EP2 | T4, on the tick of go-far | predicate + scope again | T2; `farsimHhBlockedAtGoFar` |
| EP3 | due-queue drain, before each T5-T14, T22, T23 | predicate + scope | T15 reason `scope`; `farsimHhCaughtInFar` (must be 0: it means EP6 missed an event) |
| EP4 | object move (T8, T11, T13, T22, flush step 1) | predicate + scope + not on screen | no move; T15 reason `scope` |
| EP5 | needs phase write at T4 | predicate + scope | no write (the T4 guard has already refused) |
| EP6 | scope-change hooks: household change, move-in, marriage, selection change, switch of active household, travel start | any change touching a recorded Sim | T15 reason `scope` at priority 0, then T20 |
| EP7 | drift apply and ledger write, for both ids of a pair | predicate + scope on both | pair skipped; `driftHousehold` (must be 0) |
| EP8 | alarm hold (`IsHeld`, docs/346 §3.3, §3.6) | owner is in the held set **and** `Fixes.HouseholdExempt` is false; no owner or a non-object owner is never held; callback not in the pass-through set | alarm runs at its real time (E1); a household Sim found in the set is removed and logged |
| EP9 | sidecar writer and reader | docs/343 §8 | line refused; `farSidecarHouseholdRefused` (must be 0) |

Also: visit proposals touch the household only by pushing EA's own `VisitLot` on a live, EA-owned Sim (docs/324
§3.9); a household lot is never a `place` of a machine-owned Sim (T11 guard, near trigger rule 3).

## 11. Pause, CAS, Build / Buy, Edit Town, load, travel, quit

Shared helper: `SimFrozenOrNotLive()` (`mods/ts3fixes/src/SimFrozen.cs`) [verified that it exists; FS-1 confirms it
covers each row].

| Game mode | Boundary pass | Due queue | Go-live / go-far | Allowed |
|---|---|---|---|---|
| live, running | runs | runs | runs | everything |
| paused | no | no (sim tick does not advance, nothing comes due) | no | hooks queue I7 / I8 requests by id; drained in (tick, id) order on the first running tick; counter `farsimSkippedPaused` |
| CAS, Build / Buy, Edit Town | no | no | no | as paused; entering Build / Buy or Edit Town on a lot is a near trigger for Sims placed on it, queued |
| loading screen, world load | no | no | no | table reset, sidecar read, `CleanOnLoad` (docs/343 §7) |
| save in progress | no | no | flush only (§9.4) | flush |
| travel start, quit to menu, world shutdown | no | no | T15 reason `quit` for all, at once, then table reset | restore and reset only |
| main menu | no | no | no | nothing; tables empty (`farsimRecords` = 0) |

**No go-live while paused, no exception** (Rodney 2026-11-26, §16 Q2). A camera move while paused can bring a far
Sim's place into view before it is live. The near event is queued and the Sim appears on the first running tick
after unpause, through the normal restore (§9.3 step 3: placed out of view, then routes in). Sims in `FarHome` and
`FarSleep` are hidden indoors exactly as EA's own low-LOD hide shows them [verified, docs/341 §1.5], so only
`FarVenue` Sims are missing from the picture while paused. The same holds for the modal Sim pickers, which pause
the simulator (docs/342 Q2): listed Sims are queued, a picked Sim goes live at unpause before the pick's
interaction is pushed. Counter `farsimLateAtUnpause` = go-lives drained at unpause whose place was on screen.

## 12. Sidecar fields (docs/343)

| docs/343 line | What the far-Sim machine writes | Read back in game |
|---|---|---|
| `W` | `seed` (world seed), `mv.fs` = 1 | yes |
| `K` | `t3EdgeM` (per-world far radius, `src` = `ME-4` or the rerun's id) | yes |
| `S` | `id`; `st` = state name of §4.1; `ti`; `c` = `{"ep":n,"rev":n}` | `ep`, `rev`, `ti` only, so draws and the trace continue; `st` is for diagnosis, the state is derived again after load |
| `D` | `id`, optional `to`, `k` in `relDrift` / `skillDrift:<skill>` / `careerDrift`, `v` = amount already applied in this far stretch | yes (keeps the per-stretch cap across a save) |
| `H` | world-history ring entries the plan read (I6) | yes |
| `E` | one line per logged transition: `ticks` (nominal), `id`, `ti`, `from`, `to`, optional `lot` | never (DT-1, diagnosis) |
| `A` (repair file) | only when the hold hook could not put a timer back before `AlarmManager.Write` (docs/346 §3.5, docs/343 §7) | repair pass only |

- Proposal to the docs/343 owner, no version change needed (unknown fields are skipped): optional `why` on `E`.
- Recorded inputs I7-I10 are **not** in the sidecar. They go to the `[Diag] FarTrace` file (§13.1).
- Snapshot, plan cache, due queue and ownership table are never written.

## 13. Determinism tests, counters, leaks

### 13.1 Trace and hash

Trace line = (nominal tick, `id`, `ti`, from, to, why). Input line = (tick, `id`, kind, value) for I7-I10. Trace hash:
`h = 0`; for each trace line in (tick, `id`, `ti`) order, for each of its first five fields as a 64-bit integer (states by
the ordinals of §4.1; `why` is not hashed, the inputs imply it): `h = M(h xor field)`. Example: one line
(1834100, 7215113988226514, 40, 5, 2) gives `5b19bf2ce8828ce4`.

`[Diag] FarTrace` (default 0) writes both line kinds to a log file and the hash once per sim hour.

### 13.2 Tests

No test compares against a live EA run: EA's own run is timing dependent (docs/341 §0, §5).

| # | Test | Where | Pass |
|---|---|---|---|
| D1 | PRNG test vector of §8.2 | FS-1 selftest | equal |
| D2 | fixed seed, fixed input file, run twice | FS-1 selftest | same trace hash |
| D3 | same, Sims evaluated in shuffled order and with `DueBudget` 1, 8 and unlimited | DT-1 | same hash in all runs |
| D4 | lazy against stepped: `F(t)` evaluated once at t against the same inputs drained edge by edge | DT-1 | same state and place for 1000 seeded Sims over 30 sim days |
| D5 | catch-up: drain blocked for 1, 3 and 30 sim days, then T22 | FS-1 selftest | state equals D4's stepped state at t |
| D6 | save round trip: flush, write `S` / `D`, reset, read, go far again | FS-1 selftest + FS-10 | `ep`, `rev`, ledgers equal; `.sims3` byte-equal to a run with the switch off is FS-10's save check |
| D7 | one rewind snapshot run twice with recorded inputs replayed | DT-2 (in game) | trace hash per sim hour equal |
| D8 | model version bump | FS-1 selftest | seed differs; old `S` / `D` / `E` discarded; K and world seed kept |
| D9 | static check: machine source has no `RandomUtil`, `RandomGen`, `new Random`, `DateTime`, `Stopwatch`, `Dictionary<` keyed by an object type, `Hashtable`, `float` / `double` field | selftest script with regex cases | 0 hits |

### 13.3 Counters and leak rules

Slots are reserved at build (`node tools/registry.ts reserve fixes ...`). Names:

| Counter | Meaning | Queue check |
|---|---|---|
| `farsimRecords`, `farsimRecordsHigh` | table size, high-water | <= non-household Sims; flat across travel and save-reload; 0 at main menu |
| `farsimSnapBytes`, `farsimPlanCache`, `farsimDueSize`, `farsimHeldAlarms` | sizes | bounded by `farsimRecords`; held timers are counted by the hold hook (docs/346) |
| `farsimByState[11]` | Sims per state | sum = `farsimRecords` |
| `goFar`, `goLive`, `goLiveFallback`, `goLiveReset`, `noSafePoint`, `flap`, `catchUp`, `gone` | events | `flap` 0; `goLiveReset` 0; `goLiveFallback` below a few per sim day (docs/340 §8 risk row) |
| `farsimExcl.<reason>` | exclusions by reason | read, not gated |
| `farsimHouseholdInFar`, `farsimHhCaughtInFar`, `driftHousehold`, `farSidecarHouseholdRefused` | household violations | all 0, hard fail |
| `farsimHhBlockedAtGoFar` | EP2 refusals | read |
| `ownerConflict` | V1 broken | 0 |
| `farsimFViolations` | V4 broken (shadow) | 0 |
| `farsimSkippedPaused`, `farsimQueuedWhilePaused` | pause gating | pause / CAS / Build step: transitions flat while paused, queue drained after |
| `farsimDecisionsWhileFar` | autonomy decisions or `SimUpdate` steps seen for a machine-owned Sim (V3) | 0 |
| `farsimCurveBelowClamp`, `farsimGoLiveBelow40`, `farsimGoLiveNeedMin`, `farsimGoLiveDistress` | §6.1: what the dropped -40 clamp would have changed | read in every shadow and correctness job, reported per world; not gated |
| `farsimLateAtUnpause` | §11: far Sims whose place was on screen when the game was unpaused | read; M21 expects it above 0 and go-lives while paused 0 |
| `farsimStoodDown` | automatic stand-down fired | 0 |

Leak rules (memory note "no leaks in new builds"):

- A record, its snapshot, its plan cache entry, its due-queue entry and its `FarHold` set entry are dropped together
  on T2, T3, T18, T19, T21 and T24. One drop function; no other code removes a part.
- T24 hooks `Sim.Dispose` and the description's removal, so a Sim destroyed by EA or by another mod while far leaves
  nothing behind. Records hold ids only, never a reference to a Sim, a description or a lot.
- Reset of every table on quit to menu, travel and world load, on the quit handler the played store uses.
- Plan cache: at most 2 days per record. Dwell entries for `Live` Sims (§3.1): dropped when the Sim is near or gone.
- Selftests: create / destroy churn of 10,000 records, 100 load / quit cycles, sizes back to 0.

## 14. Switches and kill paths

All default off. Names follow docs/324 §1.2 and docs/332; registry rows and ini lines are added at build.

| Switch | Off means |
|---|---|
| ts3fixes `farsim` (master), native `[FarSim] Enabled` | no table, no pass, no hooks armed. Turning it off at runtime: T15 reason `switchOff` for every record at once, then reset |
| `[Diag] FarSimShadow` (FS-2) | on = compute and compare only: records exist, nothing is parked, moved, held or written; `farsimt3` is ignored |
| `farsimt3` | no ownership: no T4. `Live`, `GoFarPending` (as a count only) and `Excluded` still tracked for the counters |
| `farsimvenue` | T10 never fires; Sims stay `FarHome` / `FarSleep` |
| `farsimwork` (own switch for `FarWork` / `FarSchool`; default 0 in the first build and after) | EA runs shifts through `eaPush` (§6.2). On = computed shifts (T7-T9). Goes on by default only after M20 shows a clear gain and the correctness queue passes |
| `farsimvisit` | T14 never fires; a pending `VisitProposed` takes T17 |
| `farsimdrift` | no drift computed or applied |
| `farhold` (shared with the far-object model, docs/346) | no alarm is held, so no T4 (§6.5) |
| `farsimt4` | stage 2b (SD-4); stays off even with `farsim` on (docs/324 decision 5) |
| `[Diag] FarTrace` (DT-2) | no trace file |
| `[FarSim] StatesOff=<list>` | each named far state is unreachable; a Sim whose `F(t)` is that state is promoted (T15 reason `standDown`) and stays live while it holds. This is the "drop that state" step of docs/340 §8 |

Automatic stand-down (sets `farsimStoodDown`, logs once, promotes everything, no new go-far until the next world
load): any household counter above 0; `ownerConflict` above 0; `goLiveReset` above 0; `goLiveFallback` above
`FallbackMaxPerDay` (default 5) in one sim day; `farFlushFailed` above 0; `tuneHash` changed mid-session. Stand-down by compat rule (no counter, logged once):
NRaas Overwatch loaded before FS-17 (§9.2); NRaas StoryProgression loaded before FS-14.

With ts3native missing or old, or the sidecar refused, the machine runs with a per-session seed and writes nothing
(docs/343 §9).

## 15. Dependencies on the other phase R lanes

The lanes below had written a handoff by the time this doc was finished (`docs/handoff/`: `st1-st5-1126.md`,
`st4-st7a-1126.md`, `st2-st3-st13-1126.md`, `lazy-inst-re-1126.md`, `st14-sidecar-1126.md`; `docs/handoff/`:
`far-sim-tick-re-1126.md`). ST-8 / ST-12 (docs/348) was still being written and was not read. What this spec took, and what is still open:

| Lane / package | Taken from its verdict | Still open for this spec | Until then |
|---|---|---|---|
| ST-1 / ST-5 (docs/342) | go-live read paths and observation hooks (§4.2 rule 3); T4-only wrong reads (§5.3); two extra T3 facade rows (§6.2) | write access to `MotiveTimeDelta` / `AutoUpdated` and which hide flags EA persists were not in its scope (Q6); pickers while paused (docs/342 Q2: answered by §11, queue and go live at unpause); buff writes on a far Sim (docs/342 Q4, here Q11) | needs stay read-only in shadow (FS-2) |
| ST-3 (docs/347 §1) | drift setters (§6.4), pay and performance setters (§6.2) | skill subclass overrides (I-616-3) | skill drift shadow only |
| ST-13 (docs/347 §2) | EA's visit score has no distance and no liking term; cooldowns 3 days per visitor, 1 day after any visit, 24 h per active-household lot, no visits 24:00-05:00. `ea_ok` (T14, T16) must include these cooldowns; the `decay(T)` weight of docs/324 §3.9 is our addition | `MetaAutonomy` visit tuning rows; ME-10 baseline | `farsimvisit` not built |
| ST-2 (docs/347 §3) | Overwatch `StuckCheck` / `ResetCheck` against parked Sims (§9.2); NRaas career writer (§6.4) | FS-14, FS-17 | stand-down with those mods loaded (§14) |
| ST-4 / ST-7a (docs/346) | the whole hold design (§6.5, §9.3 step 5, §9.4 step 2, EP8) | pass-through set: 45 "shows UI" rows unread (Q10); API names (ST-15) | those owners stay live |
| ST-8 / ST-12 (docs/348: own-thread objects, lot hub coverage) | not read | nothing in this spec depends on them; SD-2 owns it | none |
| Lazy-instantiation RE (docs/344) | ids stable over save and load (the `S`, `D`, `E` ids hold); L1 has nothing to strip; Sims are the first L2 target | RX-6 reader classification, RX-9 final verdict, inventory without an object (docs/341 Q2) | `farsimt4` not specified here (SD-4) |
| docs/341 Q1, Q7 (sleep list, byte-level read of `0xD7F8B0`) | - | parking mechanism a of §9.2 | mechanism b, or shadow only |
| docs/341 Q3 (timed buffs with side effects) | - | relax `timedBuff` | strict rule |
| docs/322 §4 band rerun | working values | measured `t3EdgeM` per world | 450 / 450 / 300 |
| CP-0 | - | docs/324 on master | cited from the branch |

## 16. Open questions

| # | Question | Who |
|---|---|---|
| Q1 | **Closed (Rodney 2026-11-26).** No -40 clamp at go-live. The difference is counted in the shadow tests (§6.1 counters, M22) | - |
| Q2 | **Closed (Rodney 2026-11-26).** Far Sims at a venue the camera moved to while paused appear at unpause. No go-live while paused; the pause gating rule has no exception (§11, M21) | - |
| Q3 | **Closed (Rodney 2026-11-26).** The first build lets EA run work shifts (`farsimwork` off) so their load is measured. `FarWork` is also built, behind its own switch `farsimwork` (default off), and an A/B shows its gain (§6.2, §17 M20) | - |
| Q4 | Parking mechanism a or b (§9.2) | FS-7 with SD-3, after docs/341 Q1 / Q7 |
| Q5 | Does `SimFrozenOrNotLive()` cover CAS, Build / Buy and Edit Town on both editions, or does the machine need its own mode test? | FS-1 |
| Q6 | Can script code write `MotiveTimeDelta` and `AutoUpdated`, is the phase persisted by EA, what is EA's phase range, and which of the low-LOD hide flags does EA persist? (docs/342 did not cover these) | FS-2 brief (read before the shadow build) |
| Q7 | `BoundaryPassMin` 5, `GoFarTimeoutMin` 120, `DueBudget` 8, `GoLiveOpsPerFrame` 4, stay 60..180 min: first values, tuned by the shadow calibration (FS-4) | FS-4 |
| Q8 | Does a hidden Sim that EA's work-day alarm pushes always arrive through `InteractionQueue.Add` (so `eaPush` catches it), or are there direct writers? No-gaps rule: every writer hooked or the site stays live | FS-8, ST-4 |
| Q9 | EA 1.69: managed methods cited by docs/341, 342, 346 and 347 not diffed | RX lane |
| Q10 | The 45 "shows UI" alarm callbacks on Sim owners (docs/346 Q7, `docs/data/alarm-callback-census.csv`): which are conditioned on the active household and can leave the pass-through set? Each one left in keeps its owners live | FS-8 brief, before `farsimt3` is timed |
| Q11 | EA writes onto a far Sim that are not an interaction push (buff add from a prank call or graduation, docs/342 Q4): go-live first (no-gaps default), or let the write land on the parked Sim? | FS-8 |
| Q12 | docs/347 I-616-2: a Sim whose curve value at go-live is low enters distress on its first tick. Not clamped (Q1); `farsimGoLiveDistress` measures how often, against EA's own low-LOD Sims | FS-4 report, FS-7 |

## 17. Test matrix (first cut; FS-10 turns it into queue jobs, SD-3 adds one repro per contract line)

| # | Case | Setup | Expect |
|---|---|---|---|
| M1 | household Sim far from the camera | camera 600 m from the household for 2 sim hours | all household Sims `Excluded`; household counters 0 |
| M2 | selected Sim changes to an NPC that is far (testing cheat or household switch) | switch active household to a far Sim's household | T15 `scope` at once, then `Excluded`; `farsimHhCaughtInFar` 0 |
| M3 | unknown scope | force a stale household table | no T1 anywhere; every record promoted |
| M4 | plain go-far and go-live | NPC at home, camera away past `FarDwellMin`, then back | T1, T4, T15, T18; Sim standing, empty queue, EA picks an action; `goLiveFallback` 0; script errors 0 |
| M5 | edge walk | camera oscillates across `R_far` every 5 sim minutes for 1 sim hour | `flap` 0 |
| M6 | no safe point | NPC kept busy by a looping interaction | T3 after `GoFarTimeoutMin`; `noSafePoint` 1; retry after `GoFarRetryMin` |
| M7 | pause, CAS, Build / Buy | 60 s pause, enter and leave CAS and Build / Buy while Sims are far and one is due | counters flat while frozen; no go-live while frozen (M21 covers the camera case); queued requests drained after; no transition logged with a frozen tick |
| M8 | save round trip | save with 20 far Sims, reload | flush counters 0 failed; `.sims3` equals the switch-off save (FS-10 save check); after load all `Live`, then far again; `ep` continues |
| M9 | sidecar missing, damaged, other version | delete or edit the file, load | vanilla load, new seed, status counter set, no error |
| M10 | EA push to a far Sim | force an interaction onto a far NPC (repro action) | synchronous go-live before the push proceeds; one owner |
| M11 | observation | open the relationship panel listing far Sims | listed Sims go live in list order |
| M12 | household-facing event | household Sim phones a far Sim | Sim live before the call resolves; relationship change comes from the real interaction |
| M13 | destroyed while far | story progression or a cheat removes a far Sim | T24; sizes drop; no reference left |
| M14 | switch off at runtime | turn `farsim` off with 20 far Sims | all go live in one tick; tables 0 |
| M15 | quit to menu and reload, 5 times | - | `farsimRecords` 0 at menu each time; high-water flat |
| M16 | replay | DT-1 (D2-D5) and DT-2 (D7) | hashes equal |
| M17 | work shift, `farsimwork` off | employed far NPC at shift start | `eaPush` go-live; EA runs the shift; far again after it |
| M18 | forced fallback | make step 3 fail (test hook) | T19; Sim at home, standing; `goLiveFallback` 1; `goLiveReset` 0 |
| M19 | stand-down | raise a household counter through a test hook | `farsimStoodDown` 1; everything live; no new go-far |
| M21 | camera moved while paused | far NPCs in `FarVenue`; pause; move the camera onto the venue; wait 30 s; unpause | while paused: no transition, no go-live, counters flat, near events queued; first running tick: the Sims go live, placed out of view and route in; `farsimLateAtUnpause` equals the number of those Sims; script errors 0 |
| M22 | no clamp, difference counted | shadow job per world (FS-4) and one forced go-live at the hour of a curve minimum | no need value is written at go-live; `farsimGoLiveBelow40`, `farsimGoLiveNeedMin` and `farsimGoLiveDistress` are reported beside the same counts for EA's own low-LOD Sims; the need read after EA's first update equals the machine's read |
| M20 | timing A/B: EA shifts against `FarWork` | `farsim`, `farsimt3`, `farhold` on in both arms; arm A `farsimwork` 0, arm B `farsimwork` 1; in-load runtime switch, ABBA, 10 min per arm, window covering a weekday shift start and shift hours, BP / IP / SV; smoke job first (per-arm split, switch value read back) | verdict in sim-thread ms/s per band (`[UnseenProbe] Bands=150,200,450,1000`), off against on, per world, plus hitch p99 / p99.9 / max at shift start and shift end. Household exempt in both arms: employed household Sims work through EA in A and B, household counters 0, their pay and performance equal between arms. Arm B also reads `farsimDecisionsWhileFar` 0 and NPC pay / performance per shift inside the shadow band |

## 18. What FS-1 builds from this

`FarSimModel.cs` (docs/332 FS-1), no hooks: the record, `M` / seed / draw / range (§8), slots, `Plan(day)`, `F(t)`,
the transition table of §5 as data (from, to, trigger, guard mask, action id), the due queue with nominal ticks, the
trace and its hash, the drop function, and the selftests D1, D2, D5, D6 (format part), D8, D9 and the leak churn.
Inputs arrive through one interface so DT-1 can feed a recorded file. Everything that touches EA (guards that read a
Sim, parking, placement, alarms, drift apply) is behind that interface and belongs to FS-2 .. FS-9.
