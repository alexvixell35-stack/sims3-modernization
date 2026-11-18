# 286. Unseen NPC animation and locomotion LOD: simplify off camera, snap back when seen (design, no build)

Written 2026-11-18. Rodney: can Sims that are off camera animate and move more cheaply, and snap back when they come into view?
Static design on existing code, docs and run data only (no build, no game run). Inputs: `probe-out/anim-split-sv-1118.md`
(SV + BP + IP, 10 min each, `[AnimPhase] Split=1`, `[UnseenProbe]` on; local), the `-native.json` files of queues
`anim-split-envreorder-1118` and `-rerun1-1118`, docs/22 §8 (ledger §511), docs/143 (UnseenThrottle), docs/228
(UnseenRootMotion), docs/249 (household audit). Marks: **[verified]** read in code / disassembly / generated address map,
**[measured]** counters of those runs, **[inferred]** our reading. Ledger: docs/51 §514.

## 0. Short answer

Yes, and most of the machinery already exists. Ranked:

| Rank | Option | Gain (BP / IP / SV, ms/s) | Risk | Effort | Status |
|---|---|---|---|---|---|
| 1 | **(a) Rate=1 revival**: unseen NPC agent update every 4th pass with summed dt (docs/143 §8.4, built 0.36.15) plus an explicit held-table exemption | ~10-20 (upper bound 29 / 26 / 30) [inferred] | low-medium | ~45 min agent + 1 correctness job + 1 timing job | built, never measured alone after the wait fixes |
| 2 | (d) the "untimed" 31-43 ms/s: mostly our own timers and classifiers, not EA work | 0 by itself; release build likely already cheaper; residual EA walk cost unknown | none (counters) | ~30 min agent + 1 diag job | measure |
| 3 | (c) `[UnseenAnim]` Every 4 -> 8 for unseen NPC pose | ~3-5 [inferred] | medium (clip events up to 7 passes late) | ini + existing `--ab unseenanim`, ~15 min + 1 timing job | built (Mode 1 ships Every=4) |
| 4 | (b) own cheap path follower for unseen NPCs | unknown, at most the share of the 116-150 us update that is not EA's own cheap follower | high | days of RE + build | **not recommended**: EA already does it (§2) |
| - | unseen non-Sim agents (cars, boats): 17.3 / 12.7 / 2.0 ms/s [measured `unAgentUs_1_1`] | not in Rodney's numbers | medium (docs/04: an earlier car shortcut was dropped for stuck Sims) | - | measure, later |

**Recommended first build: (a)**, after the one-job measurement in §5. The reason it was parked (docs/143 §8.15: "the wait is
conserved", the update time was the route job's `+0x918` hold) no longer holds: in the 1118 runs the keyed waits inside
unseen Sim agent updates are 0.4 / 4.2 / 1.6 ms/s (BP / IP / SV, `unWaitUs[1][*]`) of 39.5 / 35.0 / 39.8 ms/s of update time
(`unAgentUs[1][0]`) [measured]. The update is now CPU, so fewer updates should save CPU.

## 1. Where the time is [measured, 1118 runs]

| Item (ms/s) | BP | IP | SV |
|---|---|---|---|
| unseen NPC locomotion (agent updates `0x957210`) | 38.9 (260/s, 150 us) | 32.3 (246/s, 131 us) | 39.2 (337/s, 116 us) |
| of it keyed `+0x918` waits | 0.4 | 4.2 | 1.6 |
| unseen NPC pose (rig pose `0x940430`, after `[UnseenAnim]` Mode 1) | 11.4 (Advance 7.5) | 11.3 (8.5) | 10.6 (8.4) |
| household pose + loco (untouchable) | 13.7 | 12.2 | 13.8 |
| unseen non-Sim agents (cars, boats) | 17.3 | 12.7 | 2.0 |
| animation pass `0x571B90` not under a timer | 42.8 | 31.3 | 29.8 |

## 2. What the agent update owns (for a, b) [verified unless marked; docs/143 §2]

`Locomotion_Update 0x83CED0` calls `Locomotion_UpdateAgent 0x957210` once per enabled agent per tick at `0x83CF52`
(no LOD in the walk). Per agent: EA's on-screen byte `+0xC8` (sphere x3 vs frustum, `0x954240`); the behaviour update
`0x839F70` (brain behaviours + body); the agent timer (`+0xE0` period, `+0xE4` accumulator, vtable `+0x68` per period);
the steering obstacle list `0x9573B7` -> `0x85FFE0` for walking Sims. When `+0xC8` flips, the brain is rebuilt:
**an off-screen walking Sim already runs `OffScreenSimBrain` + `SimpleBody`** (behaviours: avoidance + behaviour B,
the plain path follower `0x847830`, which clamps its dt to [1/30 `[0x10434C4]`, 0.5 s `[0xF9A5AC]`]) [verified
selection and clamp; "plain follower" inferred]. So option (b) is EA's own design already; replacing B saves only
what B costs over a hand-written follower, and would have to reproduce portals, doors, sub-path ends and slot snaps.

Script side: no wait depends on wall-clock animation or locomotion time; route completion and SACS events wake script
tasks; time-outs are sim ticks (I-511-7, `StateMachineClient.cs:706-930`) [verified]. A late update shifts those wakes
by passes, never blocks.

## 3. Options in detail

### 3.1 (a) Rate=1: unseen agent update at 1 pass in 4 with summed dt

- **Code** [verified, `native/ts3native/unseen.cpp:775-830`]: `UtRateWhy` / `UtRateStep` in the `0x83CF52` thunk. A skipped
  pass does not call the game; its `max(dt, 1/30)` joins the agent's debt; the run passes `dt + debt`, never over 0.5 s
  (cap and spike split). Runs every pass when: visible (our class, every pass, so the first visible pass runs with the debt),
  EA byte `+0xC8` set, not a Sim, state not 1, group link or rider, agent timer under 1 s, no path, new path, within
  `RateNearM` 3 m of the goal or `PortalM` 3 m of a sub-path end, near a viewer (`NearM` 15 m of any household Sim),
  stale viewer table (`NeedViewers=1`).
- **Exactness of summed dt** [inferred]: position along the route is distance = speed x summed time on the same polyline,
  so equal outside speed ramps; corners inside the skipped window are not cut because B follows the path, not a straight
  line. Arrival, door, portal and route-complete callbacks fire every pass inside the 3 m bands, so script waits on
  route end keep their tick. Outside the bands: avoidance and obstacle notices come up to 3 passes late (docs/143 §8.15
  finding 3: late avoidance alone cost more than it saved; Rate keeps avoidance and the body on the same cadence, so
  the agent never moves without its avoidance step) [inferred].
- **Household exemption** [verified gap]: Rate has no id test; a household Sim is held only because its own pushed
  position is a viewer (distance 0 < 15 m) and a stale table holds everyone. Per docs/249 add the id test first in
  `UtRateWhy`, as `AaPoseThunk` does (`UtHeldObj(owner)`, unseenanim.cpp ~line 320; unknown = no fresh table = held,
  `AnxHousehold`). Selected Sim is in the active household, so covered. HouseholdToo never lifts it (a skip).
- **Hook site**: `0x83CF52` (Steam 1.67) = `0x83BBC2` (EA 1.69) = `0x83C6A2` (retail 1.67); clamp constants `0x10434C4` /
  `0xF9A5AC` = `0x100C7EC` / `0xFE46FC` (EA) = `0xFC2B9C` / `0xF9B5B4` (retail) [verified, `ts3addr-ea169.h`,
  `ts3addr-retail167.h`]. No new site; no S3SS / S3PP window (docs/68 `[UnseenProbe]` / `[UnseenThrottle]` rows). Sim thread only.
- **Snap-back**: position: the debt run on the first visible pass (same pass the class flips) [verified, code + selftest
  `TestUnseenThrottle2`]. Pose: untouched by Rate (anim pass is separate). Carried objects / riders: parented, held
  (`UTW_LINKED`). Save: debt lives in a native table only, so a save mid-skip stores the Sim up to 3 passes behind
  (< 0.1 s of walk) [inferred harmless; no sidecar needed].
- **Gain** [inferred]: skipped share x 3/4 x unseen update time, minus the summed run's extra. Eligible share unknown
  (bands, new paths, idle agents with no path are counted as "run"); at 50-70% eligible: ~12-20 ms/s on BP / SV,
  ~10-17 on IP. Upper bound 3/4 of the update: 29 / 24 / 29.
- **Prior correctness** [measured, docs/143 §8.9-8.17]: snap-back, stuck, water and houseboat checks run with Rate on
  in the bundle; failures were traced to the checks, not the throttle.

### 3.2 (d) The untimed part of `0x571B90` (31-43 ms/s)

- **Timer truncation** [verified]: `UsSince` (`ts3native.cpp:3355-3358`) floors each call to whole microseconds. The time
  step `0x942C00` runs ~15 500 / ~12 700 calls/s (BP / SV) and reads 0.00-0.01 ms/s, i.e. under 1 us per call, each
  counted as 0. Object pose calls (~8 700-12 400/s, 0.6-0.9 us) lose up to 1 us each too. Up to ~25 ms/s of real work
  can hide in floor loss.
- **Our own work inside the pass, outside every timer** [verified code]: per time-step call `AnxClassBody` (two component
  lookups, a frustum test, `UnClassify`); per Sim pose call `AaPoseThunk` (`UnObjClass`, `UtHeldObj`, slot, `AaHoldWhy`);
  per object `AaObj`; the pass-start `AoWalk` / `UnRefreshCamera`. With Split=1 that is ~30 000 classifier calls/s.
- **EA's own walk** [verified shape, docs/228 §1.1]: refcount `lock xadd` x2 per actor per walk, `0x93E190` -> `0x582830`
  track scan per actor per walk; docs/228 §10.1 sampled a whole sub-step walk at ~17.5 us (~4.5 ms/s).
- **Reading** [inferred]: most of the "untimed" is measurement cost and floor loss, so the release build (Split off) is
  cheaper than the split run says, and there is no 30-40 ms/s EA target here. **Fix the measurement first**: accumulate
  QPC ticks (64-bit) instead of floored us for every per-call anx/aa timer, time the classifier calls themselves, and
  read `aaPassUs` with Split=0 vs Split=1 in one job.

### 3.3 (c) Unseen pose at a lower rate

Already shipped: `[UnseenAnim]` Mode 1, Every 4, owed dt paid on the next run or on snap (`aaSnap`), CapPasses 4,
held table first [verified, unseenanim.cpp:305-360]. Remaining unseen NPC pose 10.6-11.4 ms/s runs at ~1 of 3 calls
(`aaSkipped` 1 929-2 536/s vs ~1 300/s run). Every=8 halves the run calls that are not held; Advance cost grows with the
clip events crossed by a bigger dt, so the saving is below linear: ~3-5 ms/s [inferred]. Cost: clip events (props, SACS
states 3/5/11, sound) land up to 7 passes late for unseen NPCs, and script tasks waiting in `RequestState` wake later in
sim ticks [inferred]. Hook sites unchanged (`0xD81C73`, `0x571C3C`; EA `0x57156C`, retail `0x571D3C`). Snap-back: owed time
paid on the first visible pass, track times exact again (docs/22 §8 §5); drawn pose is rebuilt by the main thread from
the current records, EA culls it off camera already (I-511-3).

### 3.4 (b) Own path follower

Rejected for now: EA's off-screen brain is the cheap follower (§2). First learn what the 116-150 us per update is
(§5 sub-timers); if B itself is most of it, (a) already cuts it by 3/4 outside the bands.

## 4. Visibility and exemption, shared by all options

Unseen = the `[UnseenProbe]` class (off every camera frustum and beyond `max(80 m, 2 x camera height)` of the camera and
its target) **and** no household viewer within `NearM` **and** EA's byte off **and** not in the fresh held table
(ts3fixes `UnseenViewers.PushHeld`; no fresh table = household). Household, selected and unknown-scope Sims always run
every pass; HouseholdToo does not apply.

## 5. Measurement before build (measure-before-build)

One job set, BP + IP + SV, 10 min each, release ini, `[UnseenProbe]` on, `[AnimPhase] Split=0`:
1. `--ab unseenrate` (ABBA, 2.5-min slices) with `[UnseenThrottle] Enabled=1 Rate=1`: per arm `unAgentUs[1][0]`,
   `unAgents[1][0]`, `utRateSkipped / utRateCalls`, `utRateWhy[*]`, `utRateDebtMaxUs`, `utRateDropped`, `anxLocoPassUs`.
   Build gate: skipped share >= 40% and on-arm `unAgentUs[1][0]` drop >= 8 ms/s on 2 of 3 worlds.
2. Locomotion sub-timers (new, in the `0x83CF52` thunk's class split): behaviour update `0x839F70`, obstacle list
   `0x9573B7`, footprint body `0x833560`, agent timer vtable `+0x68`, for unseen Sims only (one QPC pair each, 64-bit tick
   sums). Decides whether B or the footprint dominates the 116-150 us.
3. Animation pass: 64-bit tick timers (§3.2), classifier time, `0x93E190` time, aaPassUs with Split 0 vs 1.
Correctness for (a) after the gate: repro `unseen-rm-snapback` / `unseen_snapback` (`utSnap`, `utSnapMaxMs`), stuck
check pair, `unseen-water-cause`. Effort: one agent ~45 min for the held-table test and the counters; jobs ~3 x 12 min.

## 6. Open

- I-514-3: share of unseen updates that Rate may skip (bands, idle agents) on today's build.
- I-514-4: how much of the untimed `0x571B90` time disappears with 64-bit tick timers and Split off.
- Unseen cars and boats (12.7-17.3 ms/s on BP / IP): who updates them and whether a car-only rate is safe (docs/141).
