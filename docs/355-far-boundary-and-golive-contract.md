# 355. Far boundary, hysteresis and the go-far / go-live contract (SD-3, 2026-11-26)

Status: design plus a small static check. Nothing built, nothing run. Steam 1.67 (`game-copy/bin/TS3W.exe`, raw
disassembly with capstone; decompiled script assemblies, local only). EA 1.69 not checked.
Package SD-3 of docs/340 §6.2. Builds on docs/345 (SD-1), docs/341 (tick RE), docs/346 (alarm hold), docs/350 (lot
machine) and docs/353 (save flush). It does not repeat them: it fixes the parts they left to SD-3.
Ledger: docs/51 §624. Handoff: `docs/handoff/sd3-boundary-contract-1126.md`.

Every number in this doc that is not cited is a first value [inferred]; FS-4 (shadow calibration) replaces it.

## 0. Short answer

- **Near is checked when a viewer moves, not on the 5-minute pass.** docs/345 §4.2 finds both crossings in the
  boundary pass (every 5 sim minutes, about 5 s of real time at speed 1). A camera at 50 m/s covers 250 m in that
  time, more than the whole gap between the radii on Sunset Valley. So the far side stays on the pass, and the near
  side gets its own cheap scan that runs when any viewer point has moved 20 m (§2.2).
- **The ring is a lead segment.** A machine-owned Sim is near when its place is within `R_live + RingMinM` (175 m)
  of the segment from a viewer point to where that point will be in `RingLeadS` (2 s). The segment length is capped
  so near can never reach within 50 m of the far radius (§2.3).
- **Flap cannot happen by construction** (§2.5): the two sets are 50 m apart, every go-live restarts the dwell
  clock, and the ini reader enforces `FarDwellMin >= FlapWindowMin + BoundaryPassMin`. The `flap` counter stays as a
  bug detector.
- **Parking, docs/345 Q4, answered: neither a nor b.** Mechanism c (§3.2): the Sim parks itself, on its own task, at
  the idle point of EA's loop, with EA's own sleep-forever value; `SimUpdate` is removed and re-added with the two
  calls EA uses; go-live wakes the Sim with `Simulator.Wake`. No native hook, no interaction in the queue, no patch
  of a hot loop. The wake half was [inferred] in docs/341 §9.1 and is now [verified, V-624-1].
- **Go-far commits on the Sim's own task** (§3.1). The boundary pass only arms `GoFarPending`. This removes the
  race between "safe point seen" and "Sim queued itself for autonomy" that the polled form of T4 has.
- **Go-live contract: 8 lines, each with a code check, a counter and one repro** (§4, §5). 8 contract repros,
  6 boundary repros and 5 go-far repros; 19 jobs, about 3 h of game time in one correctness queue [inferred].
- **No question for Rodney.** Seven open points, all for builders (§8).

## 1. What this doc changes in earlier docs

| Doc | Was | Now |
|---|---|---|
| docs/345 §4.2 rule 5 | one boundary pass finds far and near crossings | the pass finds far crossings; near crossings come from the near scan (§2.2) and are still logged as I7 before they apply |
| docs/345 §4.2 rule 2 | ring formula left open | §2.3 |
| docs/345 §5.1 T4 | "polled by the boundary pass and on the Sim's queue-empty event" | only at the park point on the Sim's own task (§3.1) |
| docs/345 §9.2, §16 Q4 | mechanism a or b | mechanism c (§3.2) |
| docs/345 §9.3 step 4 | "restore both scheduler entries" | re-add `SimUpdate`, then `Simulator.Wake`; T18 is logged when the Sim's task has resumed (§3.3) |
| docs/345 §4.2 rule 1 | dwell "continuously" | dwell clock defined (§2.4) |
| docs/340 §3.2 | "pre-expand ring sized by time-to-view" | time-to-view orders the queue (§2.6); the ring is the lead segment |
| docs/324 §6.3 | periodic pass every 5 s of real time | sim-time pass for far, movement-driven scan for near; no real-time period anywhere |

Nothing else in docs/345 changes. States, transitions, guards, reasons, seed and PRNG are untouched; model version 1
stays 1, because the changes above are in recorded inputs (I7, I8) and in how EA is driven, not in `F(t)`.

## 2. Boundary

### 2.1 Viewer points

Camera target, camera position and every household Sim, as docs/340 §3.2. They are read once per running sim tick
into a small array (`viewers`, at most about 12 entries). A household Sim inside a vehicle counts at the vehicle's
position. The selected Sim is a household Sim, so it needs no entry of its own.

Nothing is read while the game is paused or not live (docs/345 §11). The array is reset on quit to menu.

### 2.2 Two detectors

| | Far detector | Near detector |
|---|---|---|
| Finds | `Live` Sims that may go far (T1), exclusions (T20, T21) | machine-owned Sims that must go live (T15), `GoFarPending` Sims that must stay live (T2) |
| Runs | boundary pass, every `BoundaryPassMin` sim minutes (docs/345 rule 5), sliced by count | when any viewer point is `NearStepM` or more from its anchor, and once per boundary pass |
| Walks | the sorted id list of resident `SimDescription`s | the place grid only (below) |
| Distance | to the viewer points as sampled | to the lead segments (§2.3) |
| Cost | follows the resident count, spread over the pass | follows the number of machine-owned places inside the scan box |

Near scan steps:

1. For each viewer point `p_i`: if `|p_i - anchor_i| < NearStepM` for all `i`, stop. Otherwise set every anchor to
   its point.
2. Build the lead segment of each viewer point (§2.3).
3. Query the **place grid** for the box around each segment grown by `R_live + RingMinM`. The grid is a hash map
   from a 64 m cell to the machine-owned and `GoFarPending` records whose place is in that cell. It has an entry
   only for cells that hold a record, so it has no world-size constant and an empty town costs nothing.
4. For each record found, test the exact distance. A hit is logged as an I7 input (id, viewer index, sim tick) and
   then requests T15 or T2.

Rules that keep it exact and leak-free:

- A record is in exactly one cell. It moves when its place changes (T8, T11, T13, T22) and leaves when the record is
  dropped. Counter `farsimGridEntries` must equal the number of records in states 1-8; both are 0 at the main menu.
- The near scan never reads a clock for its result. Real time enters only through the viewer speed of §2.3, and
  the result is a recorded input. A replay (DT-1, DT-2) feeds the recorded I7 lines and never runs the scan, so
  H3 of docs/341 §5 holds.
- Household check EP1 / EP3 of docs/345 §10 is unchanged: the scan only ever requests a go-live, and a go-live is
  never refused.

### 2.3 Ring: the lead segment

For viewer point `i`, with `v_i` its velocity in the XZ plane (difference of the last two samples over real seconds,
smoothed over 4 samples):

```
lead_i  = min(|v_i| * RingLeadS, LeadMaxM)
q_i     = p_i + normalize(v_i) * lead_i
near(x) = exists i: dist(x, segment[p_i, q_i]) < R_live + RingMinM
LeadMaxM = max(0, R_far - GapM - R_live - RingMinM)
```

| Key (`[FarSim]`) | Default | Why |
|---|---|---|
| `RingMinM` | 25 | covers one near scan step (`NearStepM` 20) plus slack, so a slow viewer cannot step over the edge between scans |
| `RingLeadS` | 2.0 | docs/324 §6.3 said 1-2 s ahead; the restore queue needs about 0.25-0.5 s for 60 Sims (§2.6) |
| `NearStepM` | 20 | one scan per 20 m of viewer movement: about 2-5 scans per second while the camera pans, none while it rests |
| `GapM` | 50 | docs/345 rule 2: the radii stay at least 50 m apart |
| `JumpM` | 150 | a viewer step this long in one tick is a jump (§2.7), not a speed |

With the working radii: Bridgeport and Isla Paradiso (`R_far` 450) get `LeadMaxM` 225 m, Sunset Valley (300) gets
75 m. Near never reaches past `R_far - 50`.

Two caps on top:

- A Sim that went far through the parked rule (docs/345 rule 1, second half: beyond `HandoffRadiusM` 200 m and in a
  rabbit hole or on an unseen lot for `ParkedDwellMin`) uses `LeadMaxM = max(0, HandoffRadiusM - 25 - R_live -
  RingMinM)`, which is 0 with the defaults. Its near set (175 m) then stays 25 m inside its far set (200 m). It can
  be late by the lead, and that is not visible: such a Sim is inside a rabbit hole or hidden indoors by EA's own
  low-LOD hide (docs/341 §1.5).
- If the ini makes `R_live + RingMinM + GapM > R_far`, the reader lowers `RingMinM` to fit, logs one line and
  counts `farsimRadiiClamped`. If even `RingMinM = 0` does not fit, the feature stands down (`farsimStoodDown`).

The segment test replaces docs/324's "along the camera motion vector" with something that has one definition and
one test: point-to-segment distance.

### 2.4 Dwell clock

`dwellStart(sim)` = the latest of: the tick the Sim last crossed outward past `R_far` (or met the parked
condition), its last near trigger (docs/345 rule 3), its last T2, T3, T18 or T19, and the last scope or eligibility
change. Far (rule 1) holds when `now - dwellStart >= FarDwellMin` (or `ParkedDwellMin` for the parked rule) and the
Sim is still outside.

The ini reader enforces `FarDwellMin >= FlapWindowMin + BoundaryPassMin` and the same for `ParkedDwellMin`; a
smaller value is raised and logged.

The dwell entry is the "small dwell entry" docs/345 §3.1 already names. It is dropped when the Sim comes inside
`R_far`, so the dwell table holds only Sims that are outside right now. Counter `farsimDwellEntries`.

### 2.5 Why a Sim cannot flap

`flap` = a go-far (T4) within `FlapWindowMin` of that Sim's last go-live.

1. Space: a T15 from the near scan needs `d < R_far - GapM` (§2.3). A T1 needs `d > R_far`. No position satisfies
   both.
2. Time: every go-live, whatever its reason, sets `dwellStart` to its own tick (§2.4). The next T1 is then at least
   `FarDwellMin` later, and `FarDwellMin >= FlapWindowMin + BoundaryPassMin` is enforced. So even a go-live at any
   distance (observed, `eaPush`, flush, scope) cannot be followed by a go-far inside the window.
3. A T1 needs no trigger during the dwell, so a Sim that is observed again and again never goes far.

So `flap` > 0 is a bug in the dwell clock or the clamp, not a tuning matter. The cycle rate is bounded too: at most
one go-far per Sim per `FarDwellMin`, 48 per sim day at the default. The shadow build reports
`farsimCyclesPerSimDay` (mean and p99 per Sim); FS-4 raises `FarDwellMin` if the mean is above 4 [inferred target].

### 2.6 Order in the restore queue (time-to-view)

`TTV = max(0, dist(x, p_i) - R_live) / max(v_toward, 5 m/s)`, smallest first, ties by `SimDescriptionId`.
`observed`, `eaPush`, `scope`, `jump` and every forced reason are 0 (docs/345 §9.3). The order changes only when
EA gets a Sim back, never what the Sim's state is: `F(t)` is evaluated at the tick each restore runs.

Throughput [inferred]: `GoLiveOpsPerFrame` 4 gives 120-240 restores per second at 30-60 fps. A camera running
into a dense block (60 far Sims on a 100 m front) needs 0.25-0.5 s; `RingLeadS` 2.0 leaves a factor of 4. A stage
2a restore is a wake, one `AddObject`, a flag write and a move only when the place changed, so it should cost well
under 0.5 ms each [inferred]; the counter is `farsimRestoreUs`.

### 2.7 Jumps

A viewer step of `JumpM` or more in one tick is a jump: camera cut to a Sim or lot, map view pick, household Sim
teleport, vehicle exit far away. The ring cannot lead a jump. Rule:

- the jumping point's velocity is set to 0 for 4 samples (no lead from a jump);
- a near scan runs at once at the new point;
- its hits carry reason `jump`, priority 0, and restore step 3 uses the out-of-view placement of docs/345 §9.3
  (placed at the nearest point outside the frustum, then EA routes the Sim in);
- counter `farsimJump`, and `farsimLateOnScreen` for restores whose place was on screen at step 3.

`Camera.FocusOnSim` and `Camera.FollowSim` on a far Sim are already near triggers (docs/345 rule 3) and fire before
the camera moves; the jump rule covers the Sims around the target.

### 2.8 Lots

docs/350 owns the lot boundary. Three couplings are fixed here so both specs say the same thing:

1. A Sim goes far before its lot may park, and a lot goes live before a Sim goes live onto it (docs/350 §4.2 rule
   3, M12). The restore queue asks the lot machine first and waits for its answer inside the same restore step.
2. The lot's near test uses the same viewer array and the same near scan trigger, with its own radius.
3. A Sim's place is never a household lot and never a lot a household Sim is routing to (docs/345 T11 guard).

### 2.9 Per-world edge and calibration

`t3EdgeM` comes from the sidecar `K` line (docs/343). A change of the edge (FS-4 writes a calibrated value) applies
to new T1 decisions only. It never causes a T15: a Sim already far stays far until a near rule fires. So a
calibration write cannot produce a go-live burst.

### 2.10 Game speed

Radii are metres and dwell is sim minutes, so the far side behaves the same at every speed in sim time. In real
time the dwell is shorter at speed 3, and the near side matters more: the near scan is driven by movement, so its
rate follows the viewer, not the game speed. The restore budget is per frame, so restores per real second do not
drop at speed 3. Repro B6 checks this.

## 3. Go-far

### 3.1 Contract

T4 runs only at the **park point**: the idle branch of the Sim's own loop, after EA's idle call and before EA
tests the autonomy queue [verified order, V-624-3]. It runs on the Sim's own task, so no other script task can
run between the checks and the park (script tasks are cooperative, docs/341 §1.1).

| Line | Rule | Check in code | Counter if refused |
|---|---|---|---|
| GF1 | scope and eligibility again, on this tick (docs/345 EP2, §5.3) | shared predicate + scope known + `G-elig` | `farsimHhBlockedAtGoFar`, `farsimExcl.<name>` |
| GF2 | safe point (docs/345 §5.4 lines 1-6), read on the Sim's own task | queue empty, standing, no carry, no parent, not routing, no reservation | `farsimNotSafe.<line>` |
| GF3 | unseen: not on screen, outside `R_far` by the sampled viewers, not on a household lot | `World.IsObjectOnScreen` false + distance + lot test | `farsimSeenAtGoFar` |
| GF4 | snapshot first, writes after (docs/345 §9.1); the only EA write is the needs phase | snapshot object complete before the first write | `goFarAborted` |
| GF5 | park is all or nothing (§3.2). A step that throws undoes the earlier steps in reverse order, then T2 | try / undo list | `goFarAborted` |
| GF6 | no decision after T4: the Sim is in no `AutonomyManager` queue and its `SimUpdate` is gone before it sleeps | assert at the sleep call | `farsimDecisionsWhileFar` (must be 0) |
| GF7 | never forced: no cancel, no reset, no teleport to make a safe point; `GoFarTimeoutMin` then T3 | - | `noSafePoint` |

The boundary pass does only T1 (arm) and T3 (timeout). A `GoFarPending` Sim tests GF1-GF3 on each idle pass of its
own loop, which EA runs every 2 to 20 sim ticks (docs/341 §1.3). Only pending Sims pay for that test.

### 3.2 Parking: mechanism c

Facts used:

- EA's sleep call clamps the count to `0xFFFFFC17`, and a sleep entry at that value is never counted down: it is
  EA's own "sleep forever" [verified, docs/341 §9.1]. A sleeping task is out of the run map and costs one sleep-map
  visit per `ScriptTick`.
- `Simulator.Wake` is the script call at `0x76CB30`, which calls the host's wake (`0xD77BA0`, host vtable slot
  `+0x24`). When the id is in the sleep map (host `+0x1728`) it writes 0 into the entry's remaining count, so the
  next count-down pass wakes the task. When it is not, it looks the id up in the list at host `+0xBD8` and, if
  found, records a pending wake in the container at host `+0x1764` [verified bytes, V-624-1].
- EA calls `Simulator.Wake` at 12 sites in Sims3GameplaySystems, none of them in the interaction queue
  [verified by grep, V-624-2]. So an interaction that EA adds to a parked Sim does **not** wake it; the `eaPush`
  hook of docs/345 has to.
- The idle branch of the Sim's loop ends with: EA's idle call, then the autonomy test, then a sleep of 2, 10 or 20
  ticks. `AutonomyManager.RemoveActor` exists and is what EA uses to take a Sim out of the queues
  [verified, V-624-3].
- `SimUpdate` is added with `Simulator.AddObject` in `Sim.OnStartup` and removed with `Simulator.DestroyObject` in
  `Sim.Dispose`; its id is kept in one private field of the Sim [verified, docs/341 §1.2].

Park (inside the hook at the park point, after GF1-GF4 passed), in this order:

1. `AutonomyManager.RemoveActor(sim)`.
2. `Simulator.DestroyObject` on the Sim's `SimUpdate` id; set the field to invalid, as `Sim.Dispose` does.
3. Hide with EA's low-LOD flags, marked as ours in the ownership table (docs/345 §9.2).
4. `FarHold` add for the Sim's alarms (docs/346).
5. Record state = `F(t)`, grid entry added, T4 logged.
6. Loop: `Simulator.Sleep(max)`. When the call returns, read the record:
   - state `PendingLive` with the wake flag set: leave the loop (§3.3);
   - anything else: somebody else woke the Sim. Raise `eaPush` (reason `foreignWake`), run the restore right here,
     leave the loop. Counter `farsimForeignWake`.
7. Return into EA's loop. EA's own code then tests the autonomy queue and the Sim starts from idle, which is
   contract line 3 with nothing forged.

Why this meets docs/345 V3: the Sim's task is out of the run map, its `SimUpdate` does not exist, and it is in no
autonomy queue. Nothing of the Sim runs until a wake.

Why not a: no native park is needed, so there is no hook on the walk at `0xD7F8B0`, which S3PP detours and our
TaskSeam and TaskServe already retarget (docs/68). Why not b: a parked interaction sits in the queue, so "queue
empty" is false for a far Sim, the interaction would be written into a save, and `SimUpdate` would still run every
tick.

Cost while parked [inferred]: one sleep-map visit per Sim per `ScriptTick`, tens of nanoseconds; 2,000 parked Sims
are well under 0.1 ms/s. This is the only per-tick cost a far Sim has, and it is EA's loop, not ours. If it ever
shows in a profile, the fallback is to take the entry out of the sleep map natively; not planned.

Hook site: the builder (FS-7) picks the managed method at the park point and checks it with `Fixes.InlineShape`
(docs/293) before `Fixes.Patch`. The hook must test "is this Sim pending or owned" with one table lookup and return
at once for every other Sim, because every idle Sim in town passes through it every 2 to 20 ticks.

### 3.3 Unpark (go-live step 4)

On the restore queue's task, after steps 1-3 of docs/345 §9.3:

1. Clear our hide flags; opacity 1 unless EA's low-LOD hide applies at the spot.
2. `Simulator.AddObject(new SimUpdate(sim))`, id written to the Sim's field, as `Sim.OnStartup` does.
3. `FarHold.Release(id)` (docs/346 §3.4), so EA dispatches the held timers before the Sim's first live tick.
4. Drift apply (docs/345 §6.4).
5. Set the wake flag in the record, then `Simulator.Wake(sim.ObjectId, false)`.

The Sim's task resumes on a later `ScriptTick`, inside our park loop (§3.2 step 6), which sets `resumed`. T18 is
logged then, not at the wake call: a restore counts as done when EA is running the Sim, the same rule docs/324
§6.3 uses for builds. If `resumed` is not set within `WakeTimeoutTicks` (30 sim ticks), the restore failed: T19.

The record and its grid entry are dropped at T18 or T19, never before, so a second near hit in between finds
`PendingLive` and does nothing.

### 3.4 Flush, switch off, quit

- Save flush (docs/353, docs/345 §9.4): the Sim stays asleep. Its queue is empty and its posture is standing, so
  what EA writes is a plain idle Sim. `SimUpdate` is re-added for the save and removed after it only if open point
  P3 says EA persists it; otherwise it stays removed.
- Switch off, stand-down, quit, travel: unpark all, in id order, outside the budget. For quit and travel the wake
  is still issued, so no task is left asleep when EA tears the world down.
- After a load every Sim is `Live` (docs/345 §9.1). A sleep entry of ours cannot survive a load if EA does not
  persist task sleep state; that is open point P2 and repro G5 checks it.

## 4. Go-live contract

Lines 1-8 of docs/340 §3.5. "Check" is an assert in the restore, run before T18; a failed check goes to the
fallback (line 8) and counts under the line's counter. All counters must be 0 in the correctness queue except
where a repro forces them.

| # | Line | Check in code | Counter | Repro |
|---|---|---|---|---|
| 1 | One instance per `SimDescription`, one owner | `CreatedSim` is the parked object and no second Sim object carries the description; the ownership table says machine until T18, EA after | `farsimDupSim`, `farsimTwoOwners` | C1 |
| 2 | Position from EA placement | the spot came from an EA placement call for the place of `F(t)`; route check passed; not inside the frustum, or reason is `jump` / `observed` and the out-of-view rule was used; the lot is live first (§2.8) | `farsimBadSpot`, `farsimLateOnScreen` | C2 |
| 3 | Motives in range, EA setters, no forged interaction | every motive inside EA's range; no motive write at go-live at all (docs/345 §6.1: the curve is EA's); queue empty at wake; the only push ever made is `VisitLot` for reason `visit`, after T18 | `farsimMotiveOutOfRange`, `farsimForgedPush` | C3 |
| 4 | Alarms | `FarHold.Release` ran before the wake; held timers that came due were dispatched by EA in due order; no pass-through timer was ever held (docs/346 §3.3) | `farholdLateFire`, `farholdOrderError`, `farholdPassThroughHeld` | C4 |
| 5 | Drift through EA setters, never a household relationship | both ids of a pair pass the predicate (EP7); values written through the setters of docs/347 §1 | `driftHousehold`, `driftDirectWrite` | C5 |
| 6 | Outfit, age and career valid, no dangling reference | outfit category valid for the place; age state not in transition; `IsAtWork` false unless EA's own path set it; posture standing, no carry, no parent (docs/341 §4 lines 3, 5, 7) | `farsimBadOutfit`, `farsimBadCareer`, `farsimBadPosture` | C6 |
| 7 | Budget | at most `GoLiveOpsPerFrame` budgeted restores per sim frame; order by TTV (§2.6); forced reasons outside the budget | `farsimBudgetOver`, `farsimRestoreUs` | C7 |
| 8 | Failure: home through EA, logged once, never half built | on any failed check: queue confirmed empty, `Sim.AttemptToPutInSafeLocation(sendHome: true)`, unpark, release; if that fails, EA's reset; one log line | `goLiveFallback`, `goLiveReset`, `farsimHalfBuilt` (must be 0) | C8 |

`farsimHalfBuilt` is checked by the queue after every job: a Sim that is in no ownership state and still carries
one of our marks (hide flag of ours, held alarm, missing `SimUpdate`, sleep entry of ours).

Household: none of the eight lines can touch a household Sim, because only machine-owned Sims are restored and a
household Sim is never machine-owned (docs/345 V2). Each repro below also reads the four household counters of
docs/345 §10 and fails on any non-zero value.

## 5. Repros

Format for FS-10: one entry per row in `tools/harness/repros.json` plus the queue job; setup through
`setup_sims` (never wait for Sims to arrive, AGENTS.md "Repro Sim setup"), camera through `camera` /
`camera_pan`, switches through `patches` / `native_set`, `"rewind": "auto"` at queue level. Saves: Baseline-BP
unless stated. All jobs run with `farsim`, `farsimt3` and `farhold` on, shadow and trace on, and the off arm is
the same job with `farsim` off (it must show the far counters at 0, which proves the job measures the feature).

Test hooks the builders must provide (ts3fixes debug commands, test builds only): `far_force_far <sim>` (skip the
dwell, still needs GF1-GF3), `far_fail <step>` (make restore step N fail once), `far_state <sim>` (print the
record), `far_hold_add <sim> <minutes>` (add a test alarm that falls due while far).

### 5.1 Contract lines (C1-C8)

| Job | Line | Setup and steps | Pass |
|---|---|---|---|
| C1 `farsim-c1-one-instance` | 1 | 10 NPCs far. Then, for each: `reflect_call` EA's instantiate for the same description while it is far; also open the relationship panel and push an interaction in the same tick | every call returns the parked Sim or goes live first; `farsimDupSim` 0, `farsimTwoOwners` 0; Sim object count per description is 1 before and after |
| C2 `farsim-c2-placement` | 2 | 20 NPCs far, in `FarHome`, `FarVenue` and (with `farsimwork`) `FarWork`. Camera pans to each place at 30 m/s, then one camera jump onto a venue | every restore spot is routable and outside the frustum at the restore tick; `farsimBadSpot` 0; for the jump, `farsimLateOnScreen` equals the Sims on that lot and each routes in; no Sim inside geometry (stuck watch 1 sim hour) |
| C3 `farsim-c3-motives-idle` | 3 | 20 NPCs far for 6 sim hours at speed 3, restores at the hour of a curve minimum and of a maximum | no motive write at go-live; all motives in range; queue empty at wake; first interaction after wake is chosen by EA's autonomy (log line names the chooser); `farsimForgedPush` 0 |
| C4 `farsim-c4-alarms` | 4 | 5 NPCs far; `far_hold_add` three alarms each: due while far (2), due after (1). Go-live after the first two are due | the two fire in due order before the Sim's first live tick, the third fires at its original due time; `farholdLateFire` 0, `farholdOrderError` 0; with a pass-through class: the alarm fires on time while the Sim stays live (never held) |
| C5 `farsim-c5-drift` | 5 | `farsimdrift` on; 10 NPCs far for 1 sim day, 3 of them friends of a household Sim (these must be `Excluded` by the tie rule; one is forced in with a test hook to exercise EP7) | drift written only through EA setters; household relationship values equal to the off arm bit for bit; `driftHousehold` 0, forced pair skipped and logged |
| C6 `farsim-c6-outfit-career` | 6 | NPCs far across a shift start (with `farsimwork` on and off), across a day change and across a season outfit change | outfit valid after restore; `IsAtWork` false unless EA placed the Sim; no age-up missed (the `ageUp` exclusion fired first); script errors 0 |
| C7 `farsim-c7-budget` | 7 | 80 NPCs far on one block (`spawn_sims` + `far_force_far`); camera runs at them at 60 m/s; second pass at speed 3 | budgeted restores per frame never above `GoLiveOpsPerFrame`; all live before `d < R_live`; frame time p99 during the burst reported; `farsimBudgetOver` 0 |
| C8 `farsim-c8-fallback` | 8 | `far_fail` on each of steps 1-7 in turn, one Sim each; then a failure of the fallback call itself | Sim at home, standing, empty queue; `goLiveFallback` 7, `goLiveReset` 1; one log line each; `farsimHalfBuilt` 0; held alarms released |

### 5.2 Boundary (B1-B6)

| Job | Steps | Pass |
|---|---|---|
| B1 `farsim-b1-edge-walk` (docs/345 M5) | camera oscillates across `R_far`, then across `R_live + RingMinM`, every 5 sim minutes for 2 sim hours; repeated with a household Sim walking the same line | `flap` 0; cycles per Sim at most 1 per `FarDwellMin` |
| B2 `farsim-b2-fast-pan` | camera crosses the town at 30, 60 and 120 m/s | every restore finishes before `d < R_live`; `farsimLateOnScreen` 0; near scans per second reported |
| B3 `farsim-b3-jump` | camera cut onto a far venue; map-view pick of a far lot; household Sim teleported 500 m | `farsimJump` equals the cuts; Sims appear out of view and route in; no pop in view (frames kept) |
| B4 `farsim-b4-observed-far` | relationship panel opened every 5 sim minutes on Sims 600 m away, for 2 sim hours | those Sims go live once and never go far while observed; `flap` 0 |
| B5 `farsim-b5-clamp` | ini with `LiveRadiusM` 280, `FarRadiusM` 300; then 300 / 300 | first: `RingMinM` lowered, one log line, `farsimRadiiClamped` 1, feature runs; second: stand-down, everything live |
| B6 `farsim-b6-speed3` | B2 at 60 m/s with game speed 3; pause in the middle of the pan for 30 s | same pass rule as B2; while paused no transition (docs/345 M21) |

### 5.3 Go-far (G1-G5)

| Job | Steps | Pass |
|---|---|---|
| G1 `farsim-g1-own-task` | 20 NPCs made eligible; trace on | every T4 is logged from the Sim's own task id; `farsimDecisionsWhileFar` 0 over 6 sim hours; no `SimUpdate` step for a parked Sim (shadow counter) |
| G2 `farsim-g2-abort` | `far_fail` on park steps 1-5 in turn | Sim stays `Live`, fully normal (autonomy queue membership, `SimUpdate` present, not hidden, no held alarm); `goFarAborted` 5; `farsimHalfBuilt` 0 |
| G3 `farsim-g3-foreign-wake` | `reflect_call` `Simulator.Wake` on a parked Sim; push an interaction on another (docs/345 M10) | both go live at once and whole; `farsimForeignWake` 1; the pushed interaction runs after the restore |
| G4 `farsim-g4-reset` | `reflect_call` EA's reset on a parked Sim; then destroy another (docs/345 M13) | reset: go-live or T24 with no error, no sleep entry left; destroy: T24, sizes drop |
| G5 `farsim-g5-save-load` | save with 20 parked Sims, reload, 3 times; then quit to menu | the `.sims3` equals the off-arm save (FS-10 save check); after load every Sim runs (each logs an idle pass within 30 sim ticks); `farsimRecords`, `farsimGridEntries`, `farsimDwellEntries` 0 at the menu |

Game time [inferred]: 19 jobs of 6-12 minutes each with rewind, about 3 h in one correctness queue. No timing
job is in this list; timing is FS-13.

## 6. Counters and leak rules added by this doc

`farsimGridEntries`, `farsimDwellEntries`, `farsimJump`, `farsimLateOnScreen`, `farsimRadiiClamped`,
`farsimCyclesPerSimDay`, `farsimRestoreUs`, `farsimForeignWake`, `farsimSeenAtGoFar`, `farsimNotSafe.<line>`,
`goFarAborted`, `farsimHalfBuilt`, `farsimDupSim`, `farsimTwoOwners`, `farsimBadSpot`, `farsimForgedPush`,
`farsimMotiveOutOfRange`, `farsimBadOutfit`, `farsimBadCareer`, `farsimBadPosture`, `farsimBudgetOver`. Slots are
reserved by the builders with `tools/registry.ts`.

Leak rules (no-leaks rule of docs/340 §1.3): the place grid, the dwell table and the viewer array each drop an
entry with its record or Sim, reset on quit to menu, have a size counter above and a queue check (G5).

## 7. Who builds what

| Part | Package |
|---|---|
| Dwell clock, far detector and clamps (§2.4, §2.5, §2.9), ownership table | FS-6 |
| Park point hook, mechanism c, unpark, wake timeout, go-far lines GF1-GF7 (§3); restore checks of lines 1-6 and 8 (§4) | FS-7 |
| Line 4 with `FarHold`, `eaPush` and `foreignWake` | FS-8 |
| Test hooks and the 19 jobs (§5) | FS-10 |
| Viewer array, near scan, place grid, lead segment, jumps, TTV order, line 7 (§2.1-2.3, §2.6, §2.7) | FS-11 |
| First values of `RingMinM`, `RingLeadS`, `NearStepM`, `FarDwellMin` | FS-4 (shadow) |

FS-7 comes before FS-11 in docs/340 §6.3. Until FS-11 lands, FS-7 runs the near test inside the boundary pass
with no lead (`RingLeadS` 0) and a shorter `BoundaryPassMin` of 1; the B2, B3 and B6 repros belong to FS-11.

## 8. Open points (none for Rodney)

| # | Point | Who |
|---|---|---|
| P1 | The managed method to patch at the park point, and that EA's transform does not inline it (`Fixes.InlineShape`) | FS-7 |
| P2 | Is a task's sleep state written into a save? If yes, the flush must wake and re-park around the save | FS-7, static read of the task save path; repro G5 |
| P3 | Is `SimUpdate` persisted, or always rebuilt by `Sim.OnStartup` after a load? | FS-7; repro G5 |
| P4 | What does EA's reset do to a task that sleeps with the forever value? The teardown at `0xD80620` handles flagged task contexts (docs/341 §9.1); the sleep-map entry of a torn-down task was not followed | FS-7; repro G4 |
| P5 | The list at host `+0xBD8` and the container at `+0x1764` that `Simulator.Wake` uses for a task that is not asleep: named from the code shape only | only if a builder needs the not-asleep path |
| P6 | Real camera speeds (edge scroll, keyboard, zoomed out) to check `RingLeadS`; no measurement exists | FS-4, from the shadow build's viewer log |
| P7 | EA 1.69: `0x76CB30` and `0xD77BA0` not located there | RX lane (docs/345 Q9) |

## 9. 4x and streamed worlds

- The near detector's cost follows the machine-owned places inside the scan box, not the town: a 4x world with
  the same density near the viewers costs the same.
- The far detector walks the resident list in slices; its cost per pass follows the resident count, spread over
  5 sim minutes. At 4x residents the slice budget is unchanged and a full pass takes longer, which only delays
  go-far. If a pass takes longer than `FarDwellMin`, the pass period is the limit and is logged
  (`farsimPassOverrun`).
- No radius, cell size or budget is tied to world size. The grid is a hash map keyed by cell, so a streamed world
  needs no bounds.
- A parked Sim's only standing cost is one sleep-map visit per `ScriptTick` (§3.2); 4x Sims is 4x of a number
  that is far below the noise.
