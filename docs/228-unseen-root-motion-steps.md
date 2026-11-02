# 228. Root-motion sub-steps for unseen Sims: fewer, bigger steps with exact zones (design + probe plan)

Status 2026-11-02 (latest): **Mode 2 (coarse unseen Sims) written in 0.36.161, counters v154 `rm2*`, built, untested** (§11;
not compiled, not run). Before: **Mode 0 probe and Mode 1 shortlist written, not built, not run** (ts3native 0.36.157,
`unseenrm.cpp`, counters v151 `rm*` 0x5830-0x594F, `[UnseenRootMotion]` all off, queue
`tools/harness/queues/unseen-root-motion.json`; §9). Mode 2 was a stub until §11. Earlier:
static analysis and design only (Ghidra headless read-only on the Steam 1.67 project, capstone on
`game-copy/Bin/TS3W.exe`; no build, no game run). Goal (Rodney, 2026-11-02): "root motion with all the checks" for unseen
Sims. Follows docs/144 §3.4-3.6 (`[UnseenAnim]`, which never touches the root-motion sub-steps) and closes I-418-3's
"read the loop first". Ledger docs/51 §420. Marks as in docs/04: **[verified]** = read in the disassembly by us,
**[measured]** = counters from a run, **[inferred]** = our reading, to be checked by the probe below.

## Summary

- **The loop [verified].** `Animation_UpdateActors 0x571B90` (thiscall(list; dt), `ret 4`, one caller `0xD81C73`) walks
  every actor once (time step `0x942C00`, root-motion test `0x93E190`, non-root-motion pose `0x571C3C`); an actor with
  root motion only sets a flag (call `0x571C4C` -> `0xC0FD60`, a bare `ret`). If any actor had root motion it then runs
  `n = floor(dt / (1/30))` sub-steps, **each a full walk of the whole actor list** that re-tests `0x93E190` for every
  actor and calls `0x940430(1/30)` (`0x571D20`) for those with root motion; then one more full walk with the remainder
  (`0x571DDC`) if it is `>= 1/60`; a smaller remainder is dropped.
- **dt is whole ticks [verified].** The caller passes `dt = k x [0x108B1A0]` (1/30 s) with `k` = sim ticks this
  iteration, clamped to 10 (`MonoScriptHost_Simulate`, `0x571B90` call at `0xD81C73`). So the sub-step count per pass is
  exactly `k`, the remainder is float noise, and EA never advances a root-motion actor by more than 1/30 s per call.
- **Per-step vs per-pass cost.** Fixed per pass: walk 1 (every actor: refcount pair, `0x942C00`, `0x93E190`, plus the
  non-root-motion pose already timed as `aaUs`) and the post-walk drains (`0x577060`, `0x571A50`). Per sub-step: one
  full list walk (every actor: refcount pair and `0x93E190` -> `0x582830` track-list scan with a virtual call per track),
  plus `0x940430(1/30)` per root-motion actor (track advance, locomotion notify, Evaluate + root delta, transform write
  and its event `0x0E` fan-out). Bigger steps for unseen Sims only remove the per-actor `0x940430` calls; the walks stay
  while any actor still needs the step.
- **Gain estimate (updated, inferred).** BP pass 58-62 ms/s, 36.6-38.1 passes/s, non-root-motion pose calls 18.1-18.6 ms/s
  [measured, bp-ua-ab / -onfirst / -r2, mode on and off pooled, §2.3]. The other ~41 ms/s is walk 1 + sub-step walks +
  root-motion pose calls; the split is not known. With ~500 actors per pass and ~1.25 sub-steps per pass (wrong: the v151 smoke measured ~10, §10), the walks
  alone plausibly take 10-30 ms/s, leaving 10-30 ms/s of root-motion pose calls. Rodney was told 6-15 ms/s; the
  honest range is **~0-12 ms/s on BP**, most likely 2-8: it shrinks with the share of root-motion time spent in exact
  zones (doors, slots, object entry, near goal), which is not known and may be most of it (§3). A second,
  behaviour-identical cut (a root-motion shortlist so the sub-steps stop walking every actor, §5.2) is worth about
  the sub-step walk cost, plausibly 2-10 ms/s, on every Sim, seen or not. **The counter-only probe (§6) decides both
  before any behaviour change.**
- **Camera (§4).** The anim pass runs on the sim thread after the locomotion pass and before the scripts of the same
  step; the main thread draws interpolated published frames. A snap cannot land before the first frame drawn with a new
  camera: it lands in the next sim publish, 1-2 frames later. Lerped camera moves (portrait focus, lot focus) hide that;
  an instant jump (`SetPositionAndTarget`, map view exit) can show 1-2 frames of a stale pose. Hooking the script camera
  ICs on the sim thread gets the exact switch in one tick earlier than the camera-position test does.

## 1. The sub-step loop `0x571B90` [verified, disassembly 0x571B90..0x571E24]

### 1.1 Shape

| Range | What | Per |
|---|---|---|
| `0x571B99` | list `+0x28 |= 8` (iterating); cursor at list `+0x10` (entry = link - 0x18, next link at entry `+0x18`), so the step may unlink entries (docs/22 §1) | pass |
| `0x571BC0..0x571C7D` | **walk 1**: owner `[entry+4]` refcount `lock xadd` +1; `0x942C00(dt)` (time step, `-> 0x935810`); `has rig = [entry+0x24] && [[entry+0x24]+0xB0] && dt > 0` (`[0xF98964]` = 0.0); `0x93E190`; no root motion and rig: `0x571C3C call 0x940430(dt)`; root motion and rig: `0x571C4C call 0xC0FD60` (a `ret`) and flag `[esp+0x13] = 1`; refcount -1 (release via vtable `+0` when it hits 0) | actor |
| `0x571C83` | no flag: skip to the drains | pass |
| `0x571C8E..0x571D5F` | **sub-steps**: while `dt >= [0x10434C4]` (1/30): `dt -= 1/30`, then a full walk: refcount +1, `0x93E190`, root motion and rig: `0x571D20 call 0x940430(1/30)`, refcount -1 | sub-step x actor |
| `0x571D65..0x571E08` | **remainder**: if `dt >= [0xFC25C4]` (1/60): one full walk, root-motion actors `0x571DDC call 0x940430(dt)` | pass x actor |
| `0x571E0C..0x571E1A` | cursor reset, `0x577060` (finished/removed tracks hash, callbacks), `+0x28 &= ~8`, `0x571A50` (deferred object-id list) | pass |

`0x93E190(actor)` (fastcall) returns 1 when bit 15 of `[actor+0x2C]` is set (a forced root-motion flag; setter not
looked up), else, with a rig, tail-jumps to `0x582830(TrackManager)`: 1 when any track's vtable `+0x2C` returns true
(ClipController `0x5681C0` = clip byte `+0x5FB`, the clip's root-motion bit, docs/22 §5.1a). The test is re-run in
every sub-step walk, so an actor whose root-motion track ends mid-pass drops out of the remaining sub-steps.

`0x942C50` (the SACS pass `0x571AD0`, docs/22 §1) does the same slicing per actor (`while dt >= 1/30: 0x940430(1/30)`;
remainder only if `>= 1/60`). EA cuts root motion into 1/30 s steps on both passes and never feeds it a bigger step.

### 1.2 How dt and the step count are derived [verified, `MonoScriptHost_Simulate 0xD81840`]

The step body computes `fStack_44 = k x [0x108B1A0]` (= 1/30) where `k` is the tick count of this iteration (0 when
`+0xC08 != 1`, i.e. not running; clamped to 10 by the catch-up logic) and passes it to `0x7FD520`, the locomotion pass
`0x83CED0` and `Animation_UpdateActors 0x571B90` (`0xD81C73`), in that order. Hence:

- sub-steps per pass = `k` exactly; the remainder walk runs only on float noise (`k/30 - k x 1/30` rounding), so it
  is rare [inferred: never observed, the probe counts it];
- the largest root-motion step EA ever takes is 1/30 s; the largest non-root-motion step (`0x571C3C`) is 10/30 s;
- the "~1.25 sub-steps per pass" in the brief equals a mean `k` of ~1.25 (not counted by v147-v150; §6 adds it).
  **Refuted 2026-11-02 by the v151 smoke (§10.1):** `rmK_7` (the `k >= 10` bucket, the catch-up cap) = 8,651 of 8,970
  passes and 9.9 walks per root-motion pass, so `k` is ~10 on almost every pass; docs/144 §3.5's large-dt reading
  (I-418-2) was right.

**Cross-check with I-418-2.** docs/144 §3.5 reads the `[UnseenAnim]` debt cap as "one pass already carries > 250 ms",
i.e. `k >= 8` on most passes. A mean `k` of ~1.25 contradicts that; if both hold, the cap firing has another cause
(for example the dt units in the cap test). The probe's `k` histogram settles it; I-418-2 should not be relied on until
then.

### 1.3 What one `0x940430` call does for a root-motion actor (docs/22 §2, re-read here) [verified]

1. Idle exit (no active track and flag bit 31 clear): idle counter `+0xB4`, rest after `[0x117B7E8]` steps; returns.
2. `0x959170(1)` on the transform component (`[0x11E9158]`) unless flag bit 6.
3. **Track advance** `0x583D00(dt, &rootMotion, &changed)`: every track's Advance(dt), collects "has root motion"
   (vtable `+0x2C`) and "changed"; finished tracks go to the removal scratch list (clip-end events fire here).
4. **Locomotion notify** `0x947590(footprint comp, active = !rootMotion, 0)` (component `[0x11E8FA8]`, only when its
   `+0x2C` is set): returns at once unless the state flips; on a flip it sets `C+0x40` bit 0 and calls
   `UpdateSimFootprint 0x833560` (docs/89). So **a Sim's routing footprint is switched off while a root-motion clip
   runs and back on when it ends**: a per-flip cost, not per step.
5. Transform read (`0x7F1620`, `0x580430`) into rig state `+0x0..+0x1C` (position + rotation).
6. **Evaluate** (only when the advance reported root motion and flag bit 14 is clear; bit 14 is a one-shot skip,
   cleared on every call): `0x54C720`, `0x5833C0` (blend each track's Evaluate), graph root vtable `+8` via `0x54C570`
   into a pooled RigPose (`0x5748A0`, 0x19B30 bytes, built by `0x574F50`), root delta into rig state `+0x60..+0x7C`,
   pose released `0x55CFA0`. This is the expensive part [inferred: it scales with the bone count; docs/22 §6 never timed it].
7. Root-motion math (slerp, normalise) into rig state `+0x30..+0x5C`; new position/rotation = old + delta.
8. Placement checks: `0x5826B0(owner)`, then `0x5EB7C0` / `0x5EC9E0(+0x20 +8)` / `0x55DA60(mask & 0xCF00)` (the
   route option word's boat/water bits, docs/100) and component `[0x11E90FC]` `+0x30 & 3`: these pick between
   writing the integrated transform as is and an adjusted one, just before `0x94118B` [verified branches, meaning inferred:
   water/boat height and a parent/slot-relative case].
9. **Transform write** `0x959670(transform comp, posquat)`: returns when position and rotation changed less than
   `[0x1044804]` (scaled by `[0xFAA210]`); else stores them at `+0x10..+0x2C`, dirties children (`0x958430` when
   `+0x34 & 3`), builds the matrix (`0x958EE0`) and fires the global handler chain `0x936C90(0x0E, owner, matrix)` (event
   0x0E "transform published": footprint `0x947660`, physics `0x94DE50` and others, docs/109). Skipped when `+0x34 & 4`.
10. Epilogue: `+0x28` extra `0x5813C0(dt)`; clears the per-step rig fields `+0x60..+0xAC`.

So every root-motion sub-step of a moving actor pays a full Evaluate and an event-0x0E fan-out.

## 2. Where root motion moves the Sim, and where a position override is safe

### 2.1 Two writers of a Sim's world transform [verified call graph, roles inferred]

- **Animation root motion**: `0x940430` step 9 -> `0x959670` (callers of `0x959670`: only `0x940430`, `0x959720`
  twice, `0xB03960`).
- **The locomotion agent** (route follower): the vtable slot at `0x10445C0` -> `0x954580(this, quat*, pos*)` ->
  `0x959720` -> `0x959670` on the owner's transform component, plus the component `[0x11DC484]` vtable `+0xC(pos)`.
  `this - 4` is the owner pointer, so `0x954580` is a sub-object of a per-object component [inferred: the agent's
  "set my object's transform" callback; I-420-3]. `Locomotion_UpdateAgent 0x957210` runs in the locomotion pass
  `0x83CED0`, **before** the anim pass in the same sim step (§1.2).

What decides which writer moves a walking Sim is not read. Two readings:
(a) walking is body-driven (agent writes the transform; walk clips carry no root-motion bit), and root motion is used
for animation-driven moves: object approach and use, entering and leaving objects, stairs/ladders/doors as portal
clips, social jigs; (b) walk clips carry root motion and the body only steers. EA's footprint switch (§1.3 step 4: off
while root motion runs) fits (a): a Sim walking a route keeps its footprint for other Sims' plans [inferred, I-420-2].
Under (a), most root-motion time sits exactly in the zones Rodney wants exact, and the coarse-step gain is small; under
(b) it is the bulk of unseen walking. **The probe's per-state split (§6, `rmCallsBy*`) answers this first.**

### 2.2 The "route-expected" position

The route follower's state is the agent's path: `cur` = agent part vtable `+0x10`, remaining distance
`0x8469F0(path, cur)`, sub-path rest = path vtable `+0x12C` minus `+0xA4(cur)` (docs/143 §6.5 rows 10-11, I-207-2).
A "position at progress p" function is not mapped. Practical sources, in order of preference:

1. **The agent's own transform write** (reading a): if the agent keeps writing the transform while root motion runs,
   the route position is already applied each locomotion pass and nothing needs overriding; the anim step then only
   adds a delta on top. The probe measures the per-step distance between the transform after `0x940430` and before it
   for coarse candidates (`rmDeltaCm`), and how often the agent wrote this tick.
2. **The agent's steering position** (the avoidance record `0x85D760` gets the agent's position and radius each tick,
   docs/143 §8.2) [inferred field; not mapped].
3. **Project onto the path** (needs the path point function; not mapped, ~1 h of static work if needed).

### 2.3 Where an override can go

Safe point: **right after our coarse `0x940430` call returns at `0x571D20` / `0x571DDC`**, still inside the walk with
EA's owner refcount held, before the next actor. Write through the same path EA uses (`0x959720` on the owner's
transform component, i.e. `0x959670` + event 0x0E), never by poking `+0x10..+0x2C`, so footprint, physics and the
render message follow. Not safe: from the locomotion pass (the anim step would overwrite it), from the script phase
(a task may be mid-interaction), or outside the sim thread. A Sim that is parented (`[transform comp+0x30] != 0`,
docs/143 §8 "world position of a rider") or whose `0x940430` took the adjusted-placement branch (step 8) is never
overridden.

## 3. Cheap native signals for the exact zones

All per Sim per pass, from data the classifiers already read; the agent comes from `[UnseenTable]`'s walk of the
locomotion agent list (owner `[agent+4]`, I-418-4, I-419-2), so adding the agent pointer to `UtbRow` gives O(1) access.

| Zone | Signal | Cost | Basis |
|---|---|---|---|
| In a slot, vehicle, carried, riding | transform component `+0x30 != 0` (parent) | 2 loads | [verified] docs/143 §8, `UTB_PARENT` |
| Route goal (last few m) | `0x8469F0(path, cur) <= GoalM` (default 3 m; 15 m = EA's avoidance band) | one call per would-coarse step | [inferred] I-207-2 |
| Door, gate, stairs, any portal | sub-path rest or progress `<= PortalM` (3 m) | 2 vcalls | [inferred] docs/143 row 11 |
| No route (object use, jig, social approach) | agent has no path, or agent state not moving | 1-2 loads | [inferred] docs/143 row 12 |
| Entering / leaving an object, multi-actor animation | the owner is an actor in a running SACS state machine: component `[0x11E90F0]` vector at `+8/+0xC` non-empty | 3 loads | [inferred] I-419-1 |
| Root motion just started or ended | `0x93E190` flipped vs last pass for this actor, or footprint `C+0x40` bit 0 flipped | slot compare | [verified] flag paths, §1.3 |
| Next to someone the player sees | within `NearVisM` (10 m) of a visible Sim (`[UnseenAnim]`'s list) | already built | docs/144 §3.4 |
| Active household / camera reach | `UTB_VIEWER`, `UTB_REACH` bits | already built | docs/227 §6 |

Rule: a Sim is **coarse** only when unseen with margin (§4.2), has a route, is beyond `GoalM` of its goal and
`PortalM` of both ends of its sub-path, is not parented, is in no multi-actor state machine, and has had root motion on
the previous pass too. Everything else is exact, EA's 1/30 s.

## 4. Camera changes vs the frame [verified frame order docs/41; script camera ICs from the symbol table]

### 4.1 Order

- Sim thread, one step: `0x7FD520` -> locomotion `0x83CED0` -> **anim `0x571B90`** -> `0x59ED70` -> ... -> script
  tasks (`0xD80620` / `0xD80BC0` / `0xD7F8B0` family) -> next step; one publish per iteration (`0x76C8A0` at `0xD81DF5`,
  docs/162 §2).
- Main thread: consume and interpolate between the two oldest published sim frames (`FrameDb_ConsumeAndInterpolate
  0x693780`), then render and Present (docs/41 §3). The camera itself lives on the main thread.
- So a pose computed in the anim pass reaches the screen after that iteration's publish and the render clock catching
  up: at least one published frame after the camera already moved. **A snap cannot land before the first frame drawn
  with a new camera position.**

### 4.2 What this means for the design

- **Continuous pans and zooms**: covered by the margin: view frustum widened by `MarginPct` (25%) and the sphere grown
  by `camera speed x LeadS` (0.5 s), with hysteresis (coarse only after `HystPasses` = 8 unseen passes in a row; exact at
  once). With the margin, a Sim turns exact while it is still off screen.
- **Script-driven jumps** (portrait click, lot focus, map view): the script side calls `IC_Camera_RequestLerpToTarget`,
  `IC_Camera_RequestLerpToPositionAndTarget`, `IC_Camera_SetPositionAndTarget`, `IC_Camera_FocusOnLot`,
  `IC_Camera_EnableMapViewMode` / `DisableMapViewMode`, `IC_Camera_EnableObjectFollow`, `IC_Camera_TetherToObject` /
  `TetherToLot` on the sim thread [inferred thread: internal calls run in the Mono script phase]. A hook on these sets
  "all exact" for `CamIcPasses` (30, ~1 s) at the call, one sim step before the camera can move. Lerps take longer
  than that, so lerped jumps are covered; an instant jump may show 1-2 frames of a pose one coarse step old.
- **Main-thread-only moves** (mouse edge scroll, keyboard, `[UnseenAnim]`'s camera-position test): the U-A1 wake on view
  change already covers them in the next pass.
- Because coarse Sims keep their route position (§2) and owe at most `MergeSteps - 1` steps, the worst visible error is
  one stale animation phase, not a position jump.

## 5. Hook plan (nothing built; all off by default)

### 5.1 Sites [verified bytes, Steam 1.67, `TS3W.exe` 14,510,400 bytes]

| Site | Bytes | Use |
|---|---|---|
| `0x571BF3` `call 0x942C00` | `E8 08 10 3D 00` | time the time step (probe) |
| `0x571C3C` `call 0x940430` | `E8 EF E7 3C 00` | owned by `[UnseenAnim]` (`AaPoseThunk`); keep |
| `0x571C4C` `call 0xC0FD60` (a `ret`) | `E8 0F E1 69 00` | **per root-motion actor in walk 1, `ecx` = actor**: classify, build the shortlist |
| `0x571D20` `call 0x940430(1/30)` | `E8 0B E7 3C 00` | `RmStepThunk`: exact = pass through; coarse = owe the step or run the owed sum |
| `0x571DDC` `call 0x940430(rem)` | `E8 4F E6 3C 00` | same thunk, remainder |
| `0xD81C73` `call 0x571B90` | owned by `[UnseenAnim]` (`AaPassThunk`) | pass start/end; flush on pass end |
| `0x571C94` / `0x571D4E` `movss xmm1,[0x10434C4]`, `0x571D14` `fld [0x10434C4]` | `F3 0F 10 0D C4 34 04 01`, `D9 05 C4 34 04 01` | not patched (changing the step for everyone is out of scope) |

All rel32 call rewrites go through `PatchCalls` (refuse unless `E8` to the expected target) and the `tools/sigmap.ts`
byte check; `[UnseenAnim]` must be loaded first (same file, shared pass counter). Conflict check: S3SS, S3PP 1.1 and
Apex hook none of these (docs/144 §3.4, docs/68).

### 5.2 Modes (`[UnseenRootMotion]`, runtime switch `rmMode`, `--ab unseenrm`)

- **Mode 0 (probe, counters only)**: thunks time and count; behaviour identical.
- **Mode 1 (shortlist, exact for everyone)**: replace the sub-step walks with a loop over the walk-1 root-motion
  shortlist, keeping per entry EA's refcount pair and the `0x93E190` re-test. Guards: an entry is skipped if it is no
  longer linked (`[[entry+0x1C]] == entry+0x18`, prev at `+0x1C` [inferred, I-420-5]); if the list head or count
  changed during the loop (an actor linked or unlinked by a clip event), fall back to EA's walk for the rest of the
  pass. Saves the sub-step walk cost for all actors; needs our own loop (a jump patch at `0x571C83`), so it is the riskier
  patch; build only if the probe shows walk cost >= ~3 ms/s.
- **Mode 2 (coarse unseen Sims)**: at `0x571D20` / `0x571DDC`, a coarse Sim owes the step instead of running it, and
  runs the owed sum when `owed >= MergeSteps x 1/30` or at its first exact pass (snap). Time is conserved, never
  dropped. Cross-pass debt is needed because a mean `k` of ~1.25 leaves nothing to merge inside one pass (`k` is ~10
  in fact, §10.1; Mode 2 is not built, §10.4). After a coarse
  call, the §2.3 override when the probe has shown it is needed (`Place=1`).
- Holds (exact): every §3 zone, visible or within the margin, household / camera reach, no fresh unseen table, actor
  not a Sim (objects stay with `[UnseenAnim] Objects`), first pass after any hold, debt slot taken.
- ini: `[UnseenRootMotion] Enabled=0, Mode=0, MergeSteps=4 (2-8), GoalM=3, PortalM=3, NearVisM=10, MarginPct=25,
  LeadS=0.5, HystPasses=8, CamIcPasses=30, Place=0, Shortlist=0`.
- Counters (reserve with `node tools/registry.ts reserve native <n>` at build time): `rmPasses`, `rmK_0..7` (k histogram:
  0,1,2,3,4-5,6-7,8-9,10), `rmWalks`, `rmWalkUs` (sub-step walk time minus the timed calls), `rmTsUs` (`0x942C00`),
  `rmCalls_c`/`rmUs_c` per class (0 visible Sim, 1 unseen Sim, 2 non-Sim), `rmActorsMax`, `rmByZone_z` (calls of unseen
  Sims per first zone that holds, §3 order), `rmCoarse`, `rmOwedRuns`, `rmSnap`, `rmDeltaCm` (sum and max of
  per-call transform moves), `rmFlips` (root-motion start/end), `rmFaults`.

### 5.3 Risks

- **Bigger root-motion steps are outside anything EA does** (both passes slice to 1/30). A clip ending inside a big
  step loses the rest of the step's root motion (the next clip starts later, from script), and its end events come up to
  `MergeSteps - 1` steps late. Contained by the exact zones and the route placement; measured by `rmDeltaCm` and the drift
  check (§7).
- Reading (a) of §2.1 makes Mode 2 nearly useless (the root-motion time is in the exact zones). That is why the probe
  runs first.
- Shortlist (Mode 1): list mutation inside a sub-step (docs/22 §1 says the engine expects it); the guards fall back to
  EA's walk, and the probe counts how often the list changes (`rmListChanged`).
- Footprint flips (`0x833560`, `+0x918` lock key `0x833593`) move later by up to `MergeSteps - 1` steps for a coarse Sim
  whose root motion ends; same class of delay `[UnseenThrottle] Footprint=1` accepted.
- Hook overhead: 2 QPC reads per timed call; Mode 0 times every call in both arms, so compare shares and arm deltas,
  never a probe run's it/s against a run without it (docs/143 §6.5 overhead rule).

## 6. Probe plan (first build: Mode 0, counters only)

One build (~30-45 min of agent time, after the current queue), then one BP + one IP smoke job (4 min each, `--ab
unseenrm` 1-min slices with `[UnseenAnim]` on in both arms for the class split) and a 10-min BP run with Mode 0 on.
Questions it answers, with the stop lines:

1. Mean `k` and its histogram (settles §1.2 and the I-418-2 conflict).
2. Split of the ~41 ms/s: `rmTsUs` (time steps), `rmWalkUs` (sub-step walks), `rmUs_c` (root-motion pose by class).
3. Unseen root-motion share: `rmUs_1 / sum(rmUs_c)`, and of it the share outside every exact zone (`rmByZone`).
   **Mode 2 is built only if that coarse-eligible time is >= ~3 ms/s on BP or IP**; Mode 1 only if `rmWalkUs` >= ~3 ms/s.
4. Per-call `rmDeltaCm` of eligible Sims (how far root motion moves them per step; tells reading a vs b of §2.1).

Gain then = `coarse-eligible rmUs x (1 - 1/MergeSteps)` (Mode 2) and `~rmWalkUs x (1 - shortlist / actors)` (Mode 1).

## 7. Test plan (after Mode 2 or Mode 1 is built)

- **Smoke** (BP 4 min): arm split, `rmMode` read back, `rmCoarse` > 0 only in on slices, no `rmFaults`.
- **Position drift vs exact**: agent command samples the positions of coarse Sims at every snap and every route end
  (`rmSnapDistCm`: transform vs the route position at snap; route-end error vs the goal), both arms; pass = snap jump
  under 30 cm at p99 and route-end error not above off.
- **Snap-back repro**: the existing `unseen-snapback` agent command (moves the camera onto unseen Sims, samples the next
  frames) with Mode 2 on the whole run, plus a portrait-click and a map-view jump variant (new `repros.json` entries,
  memory "Repro actions"); pass = `rmSnap` > 0, no visible pose freeze in the screenshots, jump bound as docs/143.
- **Stuck check**: the off/on 15-min pair (docs/TESTING.md); stuck Sims not above off. Door-heavy repro
  `rare-big-crowd` for the portal zone.
- **A/B**: 10-min in-load A/B on BP and IP, both orders, judged by pose ms/s (`aaPassUs`, `rmUs_*`, `rmWalkUs`) per arm
  and hitch counts per threshold, not it/s.

## 8. Open items

- The root-motion share by state (§2.1 reading a vs b): probe question 3-4.
- Path point function for §2.2 option 3 (only if `rmDeltaCm` shows coarse Sims leave their route).
- Setter of `[actor+0x2C]` bit 15 (forced root-motion) and bit 14 (one-shot Evaluate skip): not looked up. Not needed
  for Mode 1 since v153: the walk reads bit 15 of every entry directly (§10.3).
- Whether the camera ICs run on the sim thread (I-420-6): one log line per call with the thread id in the probe build.

## 9. Build 2026-11-02 (ts3native 0.36.157, written; not compiled while a game queue ran)

- **Sites.** `PatchCalls` at `0x571BF3` (`0x942C00`), `0x571C4C` (`0xC0FD60`), `0x571D20` / `0x571DDC` (`0x940430`); all
  four or none. `Shortlist=1` adds an 11-byte `jmp` at `0x571C83` (bytes `80 7C 24 13 00 0F 84 7C 01 00 00` checked) to a
  naked stub: flag byte `[esp+0x13]` clear = `0x571E0A` as EA; else `RmSubSteps(edi = list, &[esp+0x1C] = dt)`, 1 = done
  (`0x571E0A`), 0 = EA's code at `0x571C8E` runs whatever dt is left. `0x942C00` ends `ret 4` [verified]; `0xC0FD60` is a
  bare `ret`, no stack argument, so the flag thunk is `__fastcall(actor)` and calls nothing. Pass start/end come from
  `[UnseenAnim]`'s `AaPassThunk` (its Enabled=1 is required; Apply refuses otherwise).
- **Mode 0 (probe).** Every pass: k histogram from EA's own float loop on the pass dt (§1.2), remainder passes, walk-1
  actors (time-step calls) and root-motion actors (flag calls, deduplicated in a 1024-slot set), walks, pose calls and
  `rmLateRm` (a pose call for an actor not flagged in walk 1 = root motion started mid-pass, the one case Mode 1 does not
  step), flips vs the last pass. 1 pass in 16: QPC around each time step and each root-motion pose call (sub-us remainders
  kept per counter), walk 1 = pass start to the last time-step return, tail = last time-step return to pass end; `rmWalkUs`
  = tail minus pose calls minus the probe's own class/position reads (measured); drains alone = the tail of passes with no
  root motion (`rmTailUs_0 / rmTailN_0`). Class and zones per call for Sims via `UnObjClass`; zones (first that holds):
  parented, SACS state machine, root motion started this pass, near a Sim visible last pass, household / no household
  table, owner not a locomotion agent, none, no agent grid. **Goal and portal bands (§3) are not mapped, so zone "none" is
  an upper bound for Mode 2.** `rmDeltaCm` = position change across the call (owner transform before/after). Floor: 4 bare
  QPC pairs per sampled pass (`rmFloorNs`).
- **Mode 1 (shortlist).** Design changed from §5.2's "loop over the shortlist with a linked check": each walk follows EA's
  live cursor protocol over the real list (cursor at list `+0x10` = next entry before any call, so unlinks behave as in
  EA) and runs the refcount pair, `0x93E190` and the pose only for actors in the walk-1 set. No stored pointer is ever
  dereferenced, so a freed actor cannot be touched (I-420-5 is no longer needed). Guards: shortlist full (> 512), or the
  list's node count / address XOR differs from walk 1's before the first step = whole pass to EA; after each walk the
  same check plus "every flagged actor seen", else the remaining steps go to EA with the remaining dt (never mid-walk, so
  no actor is stepped twice). Saved per non-root-motion actor and walk: two locked refcount ops and `0x93E190` (track
  scan with a vcall per track); left: two loads and a hash probe. Not identical to EA only for an actor whose root motion
  starts after its walk-1 test (counted by Mode 0's `rmLateRm`; the queue's probe jobs say whether it is 0).
- **Mode 2.** Ini keys `MergeSteps, GoalM, PortalM, NearVisM, MarginPct, LeadMs` (ms instead of `LeadS`, the ini reader
  is integer), `HystPasses, CamIcPasses, Place` read and logged; `Mode=2` runs as 0. Built only after the probe shows
  coarse-eligible time >= ~3 ms/s (§6).
- **Self-test** `unseenrm-test.h` (`TestUnseenRootMotion`, fakes only): k from `k x 1/30`, shortlist walks = EA's pose
  calls in order and dt (reference loop of `0x571C8E..0x571E08`), refcount pair and release, fallbacks (off, full, list
  changed since walk 1, unlink inside a walk: EA's remaining steps from the handed-back dt give EA's calls), `rmLateRm`,
  flips, one sampled pass. The naked stub is not covered offline (smoke job).

## 10. Smoke result and the late-start fix (2026-11-02, ts3native 0.36.160, counters v153; written, not compiled)

### 10.1 v151 smoke (BP, 4 min, `--ab unseenrm`, Mode 1 on in the on slices) [measured]

| Counter | Value | Reading |
|---|---|---|
| `rmPasses` / `rmPassesRm` | 8,970 / 6,185 | root motion in walk 1 on 69% of passes |
| `rmK_7` (`k >= 10`) | 8,651 of 8,970 | `k` is the catch-up cap on almost every pass; §1.2's "~1.25" was wrong, docs/144 §3.5 right |
| `rmWalks / rmPassesRm` | 61,215 / 6,185 = 9.9 | sub-step walks per root-motion pass |
| `rmActSum / rmWalks` | ~71 (max 533) | actors per walk |
| `rmRmSum / rmPassesRm` | 13,079 / 6,185 = ~2.1 (max 8) | root-motion actors per pass |
| `rmLateRm` | 1,729 | pose calls for actors not flagged in walk 1: v151 Mode 1 did not step them (not identical to EA) |
| `rmSlFallback_2` / `rmSlPasses` | 1,187 / 3,259 (36%) | reason 2 = the list's node count or address XOR at the sub-step start differed from walk 1's |
| sampled walk | ~17.5 us per walk | ~4.5 ms/s for all sub-step walks: Mode 1's ceiling |
| root-motion pose calls | ~10 ms/s | all classes |
| unseen-Sim class | ~1.9 ms/s | below the ~3 ms/s bar for Mode 2 (§6 question 3) |

### 10.2 What reason 2 and the late starts are [verified disassembly, Steam 1.67]

- The anim pass walks the list at `[0x11CD800] + 0` (`0xD81C61..0xD81C73`, ecx = the manager). Entries enter it only
  through `0x5718C0` (thiscall(list; entry)), which links the entry **at the head**, called once, from the anim
  component's activation `0x9402C0` at `0x9402E3` (`[entry+0x2C]` bit 31 clear; bit 31 set goes to the list at `+8`
  through `0x5718E0`, the SACS pass's list). Activation sets `+0x2C |= 0x100`; `0x940310` unlinks (`0x571900`, moves
  the cursor when it is the unlinked entry). There is no "iterating, defer the insert" check: the `+0x28 |= 8` flag is
  only set and cleared in `0x571B90` / `0x571AD0`.
- So an actor activated during a pass (by another actor's time step or pose call, e.g. a clip starting on an idle Sim)
  sits before the cursor: walk 1 never time-steps or tests it, but EA's sub-step walks test it and step it if it has
  root motion. That changes the node count and XOR = **reason 2**, and it is one source of `rmLateRm`. Removals alone
  also tripped reason 2 though they are harmless to a live-cursor walk.
- The other source: an actor already tested in walk 1 gains root motion later in the pass. `0x93E190` is bit 15 of
  `[entry+0x2C]`, else (with a rig and a TrackManager `[rig+0xB0]`) `0x582830`: any track's vtable `+0x2C`. The `+0x2C`
  getters of the controller vtables (`0xFC1AA0..0xFC2548`, scan of `mov [reg], imm` constructors) are ClipController
  `0x5681C0` (byte `+0x5FB`), composites that OR their children's `+0x2C` (`0x566010` children `+0x3C` / `+0x50`,
  `0x56A1D0` `+0x2C` / `+0x30`, `0x56E310` `+0x30`, `0x570060` `+0x2C`) and constant false (`0x620D60`). `+0x5FB` is
  written in two places: `0x5688BA` (a reset of the flag block) and `0x569CC9` in the clip assign `0x569C50`
  (`mov [esi+0x5FB], al` from clip flag bit 1). So root motion turns on by a clip assignment, by bit 15, or by linking a
  controller that already holds a root-motion clip into a tree [the last one: no writer list, inferred possible].

### 10.3 Mode 1 since v153 (behaviour-identical by construction for the hooked paths)

- **Insert hook**: `call 0x5718C0` at `0x9402E3` -> `RmInsThunk` (calls EA's, then, on the sim thread inside a pass, adds
  the entry to a per-pass candidate set, `rmSlIns`). Candidates get EA's full per-actor body in every walk.
- **Clip hook**: `mov [esi+0x5FB], al` at `0x569CC9` (6 bytes checked) -> `call RmClipStub; nop`; the stub does the
  store and, when `al` is set, marks the pass dirty (`rmSlClip`). Flags are dead after the store (mov, mov, test).
- **Bit 15**: read directly from every entry in every walk (one load next to the link already read).
- **Walk rule**: EA's body (refcount pair, `0x93E190`, pose) runs for an entry that is flagged in walk 1, a candidate or
  has bit 15; for every entry when the walk is dirty (a root-motion clip was assigned since the last walk start, or by
  a pose call earlier in this walk; the flag then stays set so the next walk covers the entries before that point),
  an audit walk, or the candidate set is full (512, `rmSlCandFull`). A find that was not flagged or a candidate is
  added to the candidates (`rmSlLate`; `rmSlBit15` when bit 15 alone found it); `rmLateRm` counts the late calls.
- **Removed**: the pre-walk node count / XOR check (reasons 2 and 3) and the per-walk check (reason 0): the walk
  follows the live list with EA's cursor, removals are harmless and insertions are hooked. Left: reason 1 (walk-1
  shortlist > 512) hands the whole pass to EA's code.
- **Audit**: Mode 1 pass 8 of every 64 (never a timed pass) runs every walk full, which is EA's behaviour, and counts in
  `rmSlMiss` a root-motion actor the rules would have skipped (a path no hook covers, e.g. a controller linked into a
  tree with its clip assigned in an earlier pass). **`rmSlMiss` must be 0** for the "identical" claim; if not, the
  next step is a hook on that path, or Mode 1 stays off.
- `rmSlUs` (the removed pre-walk check) stays 0 from v153.
- Install order (`Shortlist=1`): bytes at `0x571C83` and `0x569CC9` checked, insert call patched, clip store patched,
  then the jump at `0x571C83`; any failure = no jump, Mode 1 unavailable.

### 10.4 Expected effect [inferred, to be measured]

- Fallbacks: reason 2 (36% of shortlist passes) -> 0; reason 1 stays at ~0 (max 8 root-motion actors seen).
- Full walks: about 2 per root-motion clip assignment in a pass (the dirty walk and the next) plus the audit's ~10 walks
  per 64 passes (~1.6% of walks). With ~1-5 such assignments per second against ~255 walks/s, ~2-6% of walks run full;
  a full walk costs what EA's walk costs (+ a hash probe per entry).
- Saving: the ~4.5 ms/s walk ceiling x ~0.95 minus the skip loop's own cost (link load, bit-15 load, one or two hash
  probes per entry, ~71 entries per walk) = **~3-4 ms/s on BP at k ~10** (v151's ~64% of passes gave at most ~2.9 ms/s,
  and stepped differently from EA). Lower `k` (normal speed) scales it down: it is per walk.
- **Mode 2 decision**: not built (later the same day built on Rodney's approval: §11). The unseen-Sim root-motion class is ~1.9 ms/s on BP, below the ~3 ms/s bar of §6 q3,
  and coarse-eligible time is a share of that. Keys stay read and logged, `Mode=2` runs as 0.
- Next run: the queue's Mode 1 jobs on the 0.36.160 build; pass lines `rmSlMiss` 0, `rmSlFallback_2` 0, `rmSlIns` /
  `rmSlClip` / `rmSlFullWalks` read for the full-walk share (the queue file still names the v151 fallback counters).
- Self-test `unseenrm-test.h` (v153): shortlist = EA's calls for an unchanged list, an entry linked after walk 1 through
  the insert hook, an unlink inside a walk, a root-motion start by a pose call on a later and on an earlier actor
  (dirty), a bit-15 start, an audit pass that steps an unhooked start and counts `rmSlMiss`; off, full, refcount,
  `rmLateRm`, flips, sampled pass. The naked stubs are not covered offline.

### 10.5 Audit miss (2026-11-02 unseenrm4 smoke; 0.36.163, counters v156)

The unseenrm4 smoke PASSed with `rmSlMiss` 1 in 62 audits (`rmLateRm` 770, `rmSlClip` 4,471, `rmSlFullWalks` 2,296, fallbacks 0): one root-motion actor was neither flagged in walk 1, a candidate, nor bit 15, nor caught by a clip assignment, so a normal Mode 1 pass would have skipped its sub-steps. Path unknown (I-421-7). Since 0.36.163 a miss logs one line (first 16: actor, owner and handle, owner vtable, Sim or not, flags `[+0x2C]` with bits 15 / 31, rig and TrackManager, root-motion clip assigns this pass and the dirty flag, in last pass's set, pass and walk index) and is made safe: the rest of that pass and the 63 passes up to the next audit run EA's full walks (`rmSlMissHold`), so the uncovered path is posed as EA does until an audit comes back clean. Gain is lost only for those passes.

### 10.6 unseenrm5 misses: last pass's root-motion actors (2026-11-02; written, not compiled while a queue ran)

unseenrm5 smoke (BP, 4 min): `rmSlPasses` 5,295, audits 81, `rmSlMiss` 5, `rmLateRm` 1,593, fallbacks 0. All five miss lines
alike: Sim actor (owner vtable `0x1042CA8`), flags `[+0x2C]` `0x10C` (bit 15 and bit 31 clear), no root-motion clip assignment
this pass (dirty 0), **in last pass's set**. So each was a root-motion actor last pass, walk 1's test said no, a later walk's
test said yes.

Why walk 1 and the sub-step test disagree [verified disassembly, Steam 1.67]: the test is the same function both times.
Walk 1 (`0x571BF3..0x571C51`): time step `0x942C00` (only `0x935810` on the owner, refcount pair), then `has rig = [e+0x24] &&
[[e+0x24]+0xB0] && dt > 0`, then `0x93E190`; the flag is set only for test yes and has rig. `0x93E190` = bit 15 of `[e+0x2C]`,
else with a rig and TrackManager tail-jumps to `0x582830`, which walks the TrackManager's track list (`[tm+4]`, node `+8` =
track) and returns yes when any track's vtable `+0x2C` does; for a ClipController that is `0x5681C0` = `mov al,[ecx+0x5FB]`.
`+0x5FB` is written only by the clip assign `0x569CC9` (hooked, 0 this pass). With bit 15 clear and no clip write, the test
can only flip no -> yes by a track that already has `+0x5FB` = 1 entering the TrackManager list (or the rig / TrackManager
pointer changing) between walk 1 and the sub-step. [inferred, I-421-8] The path: the actor's root-motion clip ended in the
time step (test no in walk 1, so EA runs the non-root-motion pose `0x571C3C call 0x940430(dt)` for it), and that pose (or
another actor's walk-1 pose) links the next queued root-motion clip, assigned in an earlier pass, into the list; the
sub-steps then see it. A clip-to-clip hand-off, which is why the actor is always in last pass's set.

Fix (0.36.164 candidate, counters unchanged v156):
- **Superset shortlist.** Each Mode 1 walk runs EA's body for walk-1 finds ∪ candidates ∪ **last pass's root-motion set**
  (`g_rmSet[g_rmCur ^ 1]`, pointer compare only, never dereferenced) ∪ bit 15, and every entry when full or dirty (clip
  assignments mark dirty, unchanged). EA's body for an actor whose test says no is only the refcount pair and the test,
  so behaviour stays EA's; the per-step test drops it. A find through last pass's set counts `rmSlLate` and joins the
  candidates, never `rmSlMiss`. Cost: the set is the root-motion actors (`rmRmMax`), tested once per walk.
- **Hold count.** The hold itself worked as built (rest of the pass plus 63 passes of full walks). `rmSlMissHold` read 0
  because `tools/probe/agent.js` did not read the v156 fields (`aaSlotReset`, `aaStale`, `rmSlMissHold`); fixed. The counter
  now counts in `RmSubSteps` (Mode 1 passes with root motion that ran full walks under the hold), not in `RmPassBegin`,
  so `--ab` off slices and passes without root motion are not counted.
- Self-test (`unseenrm-test.h`): an unhooked start of an actor in last pass's set is stepped exactly as EA, `rmSlLate` +1,
  no miss; the hold test now runs a Mode 1 pass and checks the count.

Verify: smoke with `rmSlMiss` 0 over >= 60 audits; a miss that remains is a start outside last pass's set (new path: log
line, then I-421-8 is wrong for it).

## 11. Mode 2: coarse root-motion steps for unseen Sims (2026-11-02, ts3native 0.36.161, counters v154; built, untested)

Status: **written, not compiled, not run** (a game queue ran; no build, selftest or Ghidra). Rodney approved building it
with the checks below despite §10.4's bar (probe, Mode 0, 10 min: BP root-motion pose calls ~11.9 ms/s, unseen-Sim class
~2.2 ms/s; IP ~7.2 ms/s with ~7.0 ms/s unseen; `k >= 10` on almost every pass; ~1.5-2.5 root-motion actors per pass). Off by
default: only `[UnseenRootMotion] Mode=2` turns it on. Mode 1 is unchanged (its code paths only run with `Mode=1`).

### 11.1 What it does

- **Where**: the existing thunks at `0x571D20` / `0x571DDC` on EA's own sub-step loop (no shortlist; `Shortlist=0` in the
  queue). `rmMode` nonzero = the ini mode, so `--ab unseenrm` (writes 1) gives Mode 2 in the on slices; `RmEff()` keeps
  Mode 1's meaning when `Mode=2` is not configured.
- **Merge**: per root-motion actor and pass, the first call decides exact or coarse (once per pass, in both arms). A coarse
  actor owes a sub-step (the call returns without posing) until `MergeSteps` steps are owed, then runs one
  `0x940430(owed + step)`. Its **last call of the pass** (the `k`-th sub-step, or the remainder walk) always runs what it
  owes, so time is kept exactly, no step exceeds the pass's remaining dt, and **owed steps never cross a pass**: the pose
  published after the pass is time-exact. At `k = 10`, `MergeSteps = 4`: calls of 4/30, 4/30, 2/30 s instead of ten 1/30 s.
  At `k <= 1` (normal speed) nothing merges.
- **Snap**: an actor that turns exact with owed steps (only possible mid-pass when the switch goes off) runs them as single
  1/30 s steps before its normal step (`rm2Snap`, `rm2SnapSteps`, move across them `rm2SnapCm` / `rm2SnapMaxCm`). Owed
  steps of an actor that gets no last call (unlinked, or its root motion ended from outside) are dropped and counted
  (`rm2Lost`, `rm2LostMs`).
- **Exact reasons** (`Rm2WhyGame`, first that holds, `rm2Why_*`): not a Sim; camera-jump hold; unclassified; visible
  (`UnObjClass`); almost visible (§11.2); parented / ridden horse (`UtHeldObj`) / group-linked agent; actor in a running
  SACS state machine (multi-actor animation, I-419-1); root motion started this or last pass (not in both walk-1 sets: the
  first steps of a clip); within `NearVisM` of a Sim visible last pass (`[UnseenAnim]`'s list plus our visible root-motion
  Sims); within 80 m of an active-household Sim or no fresh household table (`UtViewerWhy`); no locomotion agent (agent
  grid, re-checked `[agent+4] == owner`); agent not walking (`[agent+0x94] != 1`) or no path; within `GoalM` of the route
  goal or `PortalM` of a portal (`UtPathWhy`, docs/143 bands, I-207-2, I-420-20); hash slot holding another actor's owed
  steps; fewer than `HystPasses` eligible passes in a row (hysteresis; any reason resets it).
- **Not covered**: "root motion about to end" (the clip end time is not mapped). A clip that ends inside a merged step is
  counted (`rm2EndInMerge`: `0x93E190` false after the call) and loses at most `MergeSteps - 1` steps of root motion and
  event timing (§5.3 risk), measured by the drift jobs.

### 11.2 Camera

- **Margin**: the frustum test (`0x68D130`) on a sphere of radius `1 m + d x 0.6 x MarginPct / 100 + lead` at the Sim (d =
  distance to the camera; 0.6 = tan of half the view angle [inferred]), and the camera reach widened by `MarginPct` plus the
  lead; lead = camera speed x `LeadMs` (speed from the camera position / target move between passes, decaying x0.8 per
  pass, max 100 m/s, `rm2CamSpeedMax`). No camera = exact.
- **Jumps**: a camera position or target move, or a reach change, of more than 20 m between two anim passes, or the camera
  found again, holds every Sim exact for `CamIcPasses` passes (`rm2CamJump`, `rm2CamHold`). The script camera IC hook of
  §4.2 is built since 0.36.162 (§11.6); the 20 m rule stays as the fallback. It is also less needed than §4.2 assumed:
  the camera is read at the pass start (after `UnRefreshCamera`) and owed steps never leave the pass, so the first pass that
  sees the new camera is already exact for the Sims it reaches (I-420-19).

### 11.3 Drift reference and Place

- The locomotion agent's own transform write (steering slot `0x10445C0` -> `0x954580`, thiscall(self; p*, q*) since 0.36.164 (§11.7), `ret 8`,
  owner `[self-4]`, both `movaps` reads [verified bytes, I-420-16]) is recorded per owner (`rm2RouteRecs`; slot checked
  to hold `0x954580`, `rmReady` bit 3). It is the route-expected position of option 1 in §2.2 (I-420-18). `rm2QuatBad`
  checks the argument order (I-420-17).
- **Drift**: at each eligible actor's last call of a pass, transform vs this step's agent write, per arm (0 exact = off
  slices, 1 coarse), buckets < 5 / 10 / 20 / 30 / 50 / >= 50 cm (`rm2DriftN`, `rm2DriftMaxCm`); no write this step =
  `rm2RouteStale`. **Route ends**: when an actor eligible last pass reaches the goal band, transform before its first step
  vs the agent write (`rm2EndN`, `rm2End30` per arm of that last pass).
- **Place=1** (test arm, default 0): after each merged call, replay the agent's write of this step (`0x954580` -> `0x959720`
  on the owner's transform; never parented, an exact zone). Does nothing when the agent did not write this step.

### 11.4 Cost and expected effect [inferred]

- Per root-motion actor and pass: one decision (class, margin frustum test, SACS read, two hash probes, visible-list scan,
  viewer scan, grid lookup, `UtPathWhy` = `0x8469F0` + 2 vcalls) in both arms; with Mode 2 configured the agent grid is
  built every pass (it was 1 pass in 16). Per merged call: one `0x93E190`. Per agent update: one record in the steering
  thunk.
- Ceiling: the coarse-eligible share of the unseen class (2.2 ms/s BP, 7.0 ms/s IP) x (1 - calls per pass after merging
  / k) = x0.7 at k = 10. The exact zones decide the eligible share (§2.1 reading a vs b); on IP the unseen class is most of
  the root-motion time, so IP is where a gain can show.

### 11.5 Tests

- Self-test `TestUnseenRootMotion2` (`unseenrm-test.h`, fakes; game-side reasons replaced by a scripted reason per actor):
  v154 layout, `RmEff`, hysteresis and its reset, merge schedule 4/4/2 with time kept (also with the remainder walk and at
  k = 1), off arm = EA's calls but counted eligible, snap at a switch-off, lost steps, slot taken, camera found / jump /
  reach jump / lost, margin radius. Not covered offline: `Rm2WhyGame` (game reads), the steering thunk, drift and Place.
- Queue `unseen-root-motion.json` (all jobs moved to the `unseenrm3` build): `bp-urm2-smoke` (4 min, read-back, coarse
  only in on slices, snap bound, early gates), `bp-urm2-drift` (10 min A/B, p99 drift and route ends per arm, gross-drift
  gate), `bp-urm2-snapback` (repro `unseen-rm-snapback`: camera onto unseen Sims, map-view-height jumps, portrait-like
  jumps onto populated lots), `bp-urm2-stuck-off` / `-on`, 4 in-load A/Bs (BP, IP, both orders) judged by root-motion pose
  ms/s (x16 `rmUs_*`, unseen class x16 `rmUs_1`), `aaPassUs` and hitch counts, not it/s; `bp-urm2-drift-place` last.
- Ledger: docs/51 I-420-15..20 (I-420-21..22 for §11.6).

### 11.6 Script camera IC hooks (2026-11-02, ts3native 0.36.162, counters v155; written while a timing queue ran: NOT compiled, NOT run)

**Map** [verified: names from the script binding table (`tools/ghidra/symbols/ts3w-1.67.symbols.csv`, `ScriptCore.CameraController::Camera_*`);
entry bytes, calling convention and argument slots from a capstone disassembly of the Steam 1.67 `TS3W.exe` (14,510,400 bytes); managed
signatures from the decompiled `ScriptCore.CameraController`]. All are cdecl (plain `ret`, caller pops), every argument on the stack,
`Vector3` as three floats by value; each body first gets the camera through `0x96B390` -> `0xF20C20` -> `0x96B6D0` and returns
without effect when there is none. "Time" = the lerp-time slot (dword index of the stack arguments, from the body's `fld`).

| IC | VA | Entry bytes (len) | Arguments (time slot) | UI path |
|---|---|---|---|---|
| `IC_Camera_SetPositionAndTarget` | `0x73D550` | `55 8B EC 83 E4 F0` (6) | pos, target | agent `camera`, `camera_jumps` |
| `IC_Camera_RequestLerpToTarget` | `0x73D840` | `55 8B EC 83 E4 F0` (6) | target, time (3), swing | portrait click: `HudModel.MoveCameraToSim` -> `Camera.FocusOnSim` |
| `IC_Camera_RequestLerpToPositionAndTarget` | `0x73D950` | `55 8B EC 83 E4 F0` (6) | pos, target, time (6), swing | scripted focus |
| `IC_Camera_FocusOnLot` | `0x73DB00` | `E8 8B D8 22 00` (5, call rel32) | lot u64, zoom, pitch, time (4) | lot focus, `Camera.SetView` |
| `IC_Camera_FocusOnLotForBuildBuy` | `0x73DB70` | `E8 1B D8 22 00` (5) | lot u64, zoom, pitch, time (4), tolerance | build/buy entry |
| `IC_Camera_EnableObjectFollow` | `0x73DC20` | `55 8B EC 83 E4 F0` (6) | object u64, offset | `Camera.FollowSim` (routing Sim after a portrait click) |
| `IC_Camera_TetherToLot` | `0x73DDC0` | `E8 CB D5 22 00` (5) | lot u64, immediate | lot tether |
| `IC_Camera_TetherToObject` | `0x73DE60` | `E8 2B D5 22 00` (5) | object u64 | object tether |
| `IC_Camera_Lock` | `0x73DEF0` | `55 8B EC 83 E4 F0` (6) | object u64, offset, zoom, pitch, yaw, time (8) | scripted lock |
| `IC_Camera_EnableMapViewMode` | `0x73DFB0` | `E8 DB D3 22 00` (5) | time (0) | map button: `CameraModel.ToggleMapView` -> `Camera.ToggleMapView` |
| `IC_Camera_DisableMapViewMode` | `0x73E000` | `E8 8B D3 22 00` (5) | zoom, pitch, time (2) | same, leaving map view |

Not hooked (no jump): getters, `RequestLerpToZoomAndRotation` (zoom/rotation only), `DisableObjectFollow`, `Untether`, `Unlock`,
`IsMapViewModeEnabled`, NHood plop, photo mode, houseboat follow, shake.

**Hook** (`unseenrm.cpp` `kRm2CamIc`, Mode 2 with ini `CamIc=1`, the default): `InstallDetour` at each entry (a leading `call rel32` is
re-targeted in the trampoline, as `rtsites.cpp` does for `0xEC1180`); a naked stub passes the site index and the argument pointer to
`Rm2CamIcHit` (eax/ecx/edx are free at these entries: each body writes them before reading), which counts the call (`rm2CamIc[i]`)
and asks an all-exact hold of `CamIcPasses` + lerp time x 30 passes (lerp time used only when 0 < t <= 10 s) as an atomic max in
`g_rm2IcHold`. `Rm2CamStep` takes it at the next anim pass start, before the camera can move (the IC runs in the script phase, the
camera moves on the main thread later, §4.1). The target position is not used: the hold is all-exact, a superset of "Sims near the
new target"; the lerp time covers lerps longer than `CamIcPasses`. A 20 m jump during a longer IC hold keeps the longer hold (max).
`rmReady` bit 4 = all 11 hooked (a partial install logs `SKIP ... n of 11` and the 20 m rule covers the rest).

**Thread** (I-420-6): ICs run on whatever thread runs the script; the hold is atomic either way, and `rm2CamIcOff` counts calls off
the anim-pass thread (`g_aaTid`), which answers I-420-6 in the first run.

**Conflicts**: no S3SS / S3PP / Apex / MonoPatcher site at `0x73D550`-`0x73E05F` (docs/68 grep and `docs/data/apex-sites.csv`).

**Repro**: ts3agent `map_view {on}` (the map button's `CameraModel.ToggleMapView`) and `focus_sim {pick, index}` (the portrait's
`HudModel.MoveCameraToSim`) replace the 600 m camera set in `unseen-rm-snapback`; queue job `bp-urm2-snapback` moves to the
`unseenrm4` build and gates on `rmReady == 29`, `rm2CamIc_9 + rm2CamIc_10 > 0` and `rm2CamIc_1 > 0`. Self-test: v155 layout, hold
max with a jump, lerp-time slot, NaN and > 10 s ignored.

### 11.7 Smoke findings: steer argument order and the SACS hold (2026-11-02, ts3native 0.36.164, counters v157; written while a timing queue ran: NOT compiled, NOT run)

The unseenrm5 Mode 2 smoke (`probe-out/queue/unseen-root-motion-2026-11-02-bp-urm2-smoke-native.json`, "after") had `rm2Elig` 0:
`rm2Why_6` (SACS) caught 9560 decisions, every unseen root-motion Sim past the camera / visibility / margin / parent tests. And
`rm2QuatBad` == `rm2RouteRecs` (284640): every recorded steering write failed the unit-quaternion check.

**Steer argument order [verified Steam 1.67 disassembly, I-420-17].** `0x954580(self; arg1, arg2)` copies `[arg2]` to a local +0 and
`[arg1]` to +0x10 and passes the local to `0x959720` -> `0x959670`. That compares local +0 with the transform's +0x10 on all four lanes
(`movmskps` == 0xF: a quaternion) and local +0x10 with +0x20 on three lanes (`and edx, 7`: a position), then stores both. So arg1 = position,
arg2 = rotation. `Rm2SteerThunk` / `Rm2SteerFn` now take (p, q), record them in `Rm2Route.p` / `.q` and call EA's in the same order; Place
replays (p, q). Self-test: a synthetic write (p = (10, 20, 30), q = 90 deg about z) leaves `rm2QuatBad` unchanged and reaches the fake
original as (p, q); swapped arguments count one bad.

**SACS component [verified layout, meaning I-419-1 / I-420-23].** `[0x11E90F0]` +8 / +0xC is `mDrivers`: refcounted SACS state-machine
drivers (+4 refcount; `0x94E9C0` updates each with `0x804580`, `0x94E940` drops finished ones with `0x8006F0`). Each driver keeps its actor
slots at +0x10 / +0x14 as {name hash, slot*} pairs (`0x8040B0`, `0x800470`, `0x800740`); slot +8 is the actor holder and holder +0x10 / +0x14
the bound object id (`SACS_SetActorImpl 0x767890` -> `0x804110` -> `0x581F70` writes `[obj+8]` / `[obj+0xC]`; `0x581560` reads it back to the
object). A Sim is in a driver whenever script runs a state machine on it, so "vector non-empty" held every Sim.

**New test** (`Rm2SacsHold`, ini `[UnseenRootMotion] SacsHold=`): 2 (default) = exact while one driver binds 2+ actors (the Sim plus an
object, prop or other Sim); 1 = the old test (any driver); 0 = off. Loops are capped at 64 drivers / 64 slots; a fault counts `rm2Faults`
and holds exact. Counters v157 (+0x5A98): `rm2SacsHold` (ini value in force), `rm2SacsDrv[4]` (decisions at the SACS test by driver count
0 / 1 / 2 / 3+), `rm2SacsAct[5]` (by the most bound actors in one driver 0 / 1 / 2 / 3 / 4+), counted in every SacsHold mode.

**Queue** (`unseen-root-motion.json`): every Mode 2 job sets `SacsHold=2` and `report` [`rm2Why_`, `rm2CamIc_`, `rm2Quat`, `rm2Sacs`], so an
early-gate FAIL line names the non-zero reason counters (queue.ts `reportCounters`). `bp-urm2-smoke-sacs0` = the smoke with `SacsHold=0`
(correctness only, `minCounters` 157, gate `rm2Why_6` <= 0) for the comparison. Read the histograms first: if `rm2SacsAct_2+` covers almost
every decision, the 2-actor test is as useless as the old one (I-419-1 refutes) and the next step is to name the driver's state machine.
