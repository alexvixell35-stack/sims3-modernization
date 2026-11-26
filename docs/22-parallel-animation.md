# 22. Animation update: can per-actor pose evaluation run on worker threads?

Offload candidate 2 from docs/20 (§3 row 4, §4 rank 2): animation update `0x571B90` -> rig pose `0x940430`, 3.3% of sim-thread samples (~33 ms/s), called straight from `MonoScriptHost_Simulate`. Research only, 2026-10-13. The game and the probe were not touched.

**Method.** Ghidra headless, read-only, against the TS3 project (`DecompileTo.java` depth 2 on `0x571B90` and depth 2 on `0x567A50`; a range decompile of `0x564000-0x584000` (727 functions); caller trees, string and scalar xrefs (clip-event type hashes `0xC1E40001-0xC1E4000A`); vtable dumps at `0xFC1A60`, `0xFC1B3C` and `0xFCBA70`). Scripts and output stayed in the session scratchpad. Tags: **[verified]** = read in the decompile or xrefs, **[inferred]** = reasoning not yet confirmed by disassembly or measurement.

## 0. Short answer

- **No, not as a parallel-for over `0x940430`.** Per-actor pose evaluation is not a pure function. Inside the per-actor step the engine **fires animation clip events synchronously** (parent/unparent, sound, VFX, visibility, script, destroy prop). It also calls into the routing/locomotion library, posts the new transform to the sim-to-render buffer, and allocates new controllers that bump a **non-atomic global id counter**. Shared objects use **non-atomic refcounts** [verified].
- **No direct Mono call was found** on this path. Script clip events go into the SACS walker's native event queue, and C# drains that queue later [verified for the push, inferred for "no Mono call anywhere below"]. **Refuted 2026-10-30 (docs/206 §2.2, I-398-1):** the push `0x742710` itself calls the script host delegate slot, so `EventQueue.OnNativeEvent` (C#) runs synchronously inside the SACS pass. Mono is therefore not the blocker. The blockers are clip events, routing and refcounts.
- **The only slice that could run in parallel is the pose evaluation itself:** the blend of active tracks (`0x5833C0`) and the root-motion math that follows it. Running that slice on workers means splitting the 847-line `0x940430` into serial, parallel and serial phases. Because one 18 KB stack frame spans all three phases, the split is a C++ reimplementation of the function, not a hook.
- **Expected gain is small and unproven:** at most ~10-20 ms/s of the ~33, realistically less [inferred]. The profile's leaf samples in this family are tiny predicates (`0x582830`, `0x566010`, `0x56BC80`), which points at many cheap per-actor visits and virtual calls, not heavy pose math.
- **Recommendation: park it**, below the read-only query caches (docs/20 rank 3) and the cheaper water-search work. First measure the split cheaply (§6). If the eval slice turns out large, the safer lever is a serial one: skip pose *evaluation* for actors that are off-screen and not moving, while still running *advance* every tick so events keep firing.

## 1. Loop structure [verified]

`MonoScriptHost_Simulate` (`0xD81840`) runs two animation passes over the same actor list:

| Site | Pass | Per actor |
|---|---|---|
| `0xD81A70` | `0x571AD0` | SACS state-machine driver update `0x94E9C0` (if the actor has the SACS component `DAT_011E90F0`; this is where SACS pushes `Scripting/EventQueue/Event`s), then time step `0x942C00`, then `0x942C50`, which calls rig pose `0x940430` (in fixed sub-steps of `DAT_010434C4` while tracks are active) |
| `0xD81C73` | `0x571B90` | time step `0x942C00`, then rig pose `0x940430` if the actor has a rig (`+0x24` -> `+0xB0`) and `dt > 0`; a catch-up loop re-runs the pose in fixed sub-steps when an actor is still active (`0x93E190` -> `0x582830` "any track active") |

Both passes share the same shape:
- **Intrusive linked list** of actor entries (link at entry `+0x18`, entry = link − `0x18`). The cursor is stored **in the list object** (`param_1[4]`, i.e. `+0x10`) rather than in a local, and an "iterating" flag (`+0x28 |= 8`) is set during the walk. Together these let the current entry unlink itself or others during the walk. **The engine expects the per-actor step to change the list** (for example a destroy-prop clip event, §3).
- Per actor, the owning object's refcount is bumped with a `LOCK`-prefixed increment around the work. These are the only atomic refcounts on the path.
- After the walk:
  - `0x577060` -> `0x576FB0` walks a **global hash table** (`PTR_DAT_0114F694`/`DAT_0114F698`) of tracks marked "finished" (`+0x18`) or "removed" (`+0x19`) during the step, runs their callbacks (`0x576B30`/`0x576B70`) and erases them.
  - `0x571A50` drains a **deferred list** of object ids (`+0x18..+0x1C`): per id it looks up the object (`0x939560`), calls `0x941BC0` and `0x939420`.
- So EA already defers *some* work to after the loop: track completion and the deferred-id list. Clip events are not deferred.

## 2. What `0x940430` does per actor, in order [verified]

18 KB stack frame (`__alloca_probe`; locals down to `-0x46A9`). `this` = the actor's animation component: `+0x4` owner object, `+0x24` rig state (pose/transform floats `+0x0..+0xB8`), `+0x28` optional extra, `+0x2C` flags.

1. Clears the rig state's per-step root-motion fields. If no tracks are active (`0x5833A0` == 0 and flag bit 31 clear), increments an idle counter (`+0xB4`); after `_DAT_0117B7E8` idle steps it calls `0x940310` (puts the actor to rest, posts a message via `0x692F70`). Then it goes to the epilogue.
2. Optionally calls `0x959170(1)` on the owner's transform component (`DAT_011E9158`).
3. **Advance all tracks:** `0x583D00` = TrackManager update (scratch list `"Scratch/Animation/TrackManager/TracksToRemove"`, EASTL allocator `0x4E3F90`). For each track it calls `vtable+0x8` Advance(dt), then `+0x2C`/`+0x30` to collect "has root motion" / "changed" flags. Tracks that are not alive are copied to a scratch list and removed.
4. **Routing notify:** if the owner has the component `DAT_011E8FA8` with `+0x2C` set, calls `0x947590`, which goes into the routing/locomotion library (`0x836200`, `0x829A60`, `0x833560`).
5. Reads the transform (`0x7F1620`, `0x580430`), caches it in rig state `+0x0..+0x1C`.
6. **Pose evaluation** (when the advance reported changes): `0x54C720`, then `0x5833C0` = blend each track's `vtable+0xC` Evaluate into a pose buffer (`0x554EE0`, `0x554D10`), then:
   - the actor's rig-pose buffer comes from a **pool** `0x5748A0` ("Animation/RigPose", 0x19B30 bytes, freelist `_DAT_011CD8C4` under `EnterCriticalSection(0x011CD8D8)`) and is built by `0x574F50`;
   - the root bone delta is extracted (`0x54C570`) into rig state `+0x60..+0x7C`.
7. Root-motion math: quaternion slerp, `acos`/`sin`, `rsqrt` normalisation into rig state `+0x30..+0x5C`. There is a function-local static initialised lazily (`_DAT_011DC4A0` bit 0 guards `_DAT_011DC490`).
8. Checks `0x5826B0`, `0x5EB7C0`, `0x5EC9E0`, `0x55DA60`, then **publishes the new transform**: `0x959670` -> `0x958430` -> `0x692F70` -> `0x5934C0`/`0x593E60` (the sim-to-render message buffer, same family as the triple-buffer sync `0x593590`). Global handler chain `0x936C90` over `DAT_011DC2A8[]`.
9. Epilogue: if `+0x28` is set, calls `0x5813C0(dt)`. Clears the per-step rig fields.

## 3. Side effects inside the per-actor step, and whether each is thread-safe

| # | Effect | Where | Thread-safe? |
|---|---|---|---|
| 1 | **Clip events fired synchronously during Advance.** `ClipController` (primary vtable `0xFC1B48`, `+0x8` Advance = `0x568E10`, `+0x4` = `0x569070`) -> `0x5689A0` walks the clip's event list between old and new time -> `0x568710` calls every registered delegate's `vtable+0x10` OnEvent(event, id). Delegates are registered by `0x566170`/`0x566290` ("Animation/DelegateRegistration"). Other controllers (vtables `0xFC1E6C`, `0xFC21CC`) fire the same way via `0x56FA30` [verified] | step 3 | **No** |
| 1a | Listener **TrackManager** (vtable `0xFCBA70`, `+0x10` = `0x583C60`) -> `0x583AB0` switch: Parent/Unparent `0x582930` (allocates "Animation/ParentEvent/SceneEntityParentRecord", reparents scene entities, looks up *other* objects by id), Sound `0x583050` (audio component `0x9442D0`), Effect/StopEffect `0x5835C0` (VFX scene entity, "Animation/EffectEventHandler/SceneEntity"), Visibility `0x583120` (calls `IC_World_ObjectDetachAutoDestroyEffects`, posts messages) [verified] | step 3 | **No.** Changes shared scene state and other objects, in event order |
| 1b | Listener **SACS WalkerClipMonitor** (vtable `0x1020348`, `+0x10` = `0x805870`): Script event (`0xC1E40004`) allocates `Scripting/EventQueue/Event` (`0x7425A0`) and pushes it to the walker queue (`0x801E70` -> `0x801D90` -> `0x742710`, the ScriptCore `EventQueue` C# drains later). DestroyProp (`0xC1E40009`) destroys a prop object (`0x581810`, `0x581D80`). Slot `+0x0` `0x8055D0` and `0x805490` push "AnimationDoneEvent" events [verified] | step 3 | **No.** Unlocked vector push; object destruction changes the actor list (hence the cursor design in §1) |
| 2 | Non-atomic refcounts (`obj[1]++`, `--` then `vtable[0](1)` at zero) on controllers, clips and nodes in `0x567A50`, `0x566650`, `0x566840`, `0x5665F0`, `0x56BF10`, `0xCF3C70`, `0x55CFA0` [verified]. Some of these objects are shared between actors (clip resources from the resource cache) [inferred] | steps 3, 6 | **No** for any object reachable from two actors |
| 3 | New controllers during Advance: `0x567A50` builds a `HoldPoseController` (`0x56FE70`). Every controller constructor (`0x565A80`, 22 callers) does `id = _DAT_0114F590++` without a lock [verified] | step 3 | **No** (duplicate ids) |
| 4 | Track "finished/removed" marks: hash lookup in the global table `0x114F694` plus a byte write on the entry (`0x576CB0`, `0x576D00`, `0x576D40`) [verified] | step 3 | Probably yes (read-only lookup, per-entry byte) if nothing inserts concurrently [inferred] |
| 5 | Routing/locomotion notify `0x947590` -> `0x829A60`/`0x833560` [verified] | step 4 | **No.** The routing library has unlocked shared state (PLAN, "Async route plan PAUSED") |
| 6 | Track Evaluate `0x566070` writes the global `_DAT_011CD7D8` (no direct reader found by xref, so likely debug state) [verified write, inferred unused] | step 6 | Racy, harmless if truly unused; make it thread-local to be safe |
| 7 | RigPose pool `0x5748A0` (critical section) and EA allocator `0x4E3F90` (per-heap lock) [verified] | steps 3, 6 | Yes (adds lock contention, like docs/20 row 2) |
| 8 | Lazy static `_DAT_011DC4A0` (constant 1.0 / 0.0) [verified] | step 7 | Benign race; pre-initialise to remove it |
| 9 | Transform publish `0x959670` -> `0x692F70` -> render message buffer `0x593E60`, global handler chain `0x936C90` [verified call chain; locking of `0x593E60` not checked] | step 8 | **Assume no.** Also order-dependent: a prop parented to a Sim's slot needs the parent's new transform [inferred] |
| 10 | Pure math on the actor's own rig state (`+0x0..+0xB8`) and stack locals [verified] | steps 6-7 | **Yes** |
| 11 | Mono / script callbacks | none found on this path; script events are queued (1b) [verified for 1b; no `mono_*` callee seen in the depth-2 trees, inferred beyond that] | n/a |

## 4. Verdict

- A parallel-for that runs `0x940430` per actor on workers, with the sim thread joining afterwards, is **unsafe**. Rows 1, 1a, 1b, 2, 3, 5 and 9 all run inside it. Deferring only the event emission (hooking `0x568710` to queue `(listener, event, id)` per actor and replaying after the join) would leave rows 2, 3, 5 and 9. It would also reorder events relative to the routing notify and the transform publish, which the parent/visibility handlers may rely on [inferred].
- **The workable design is a phase split:**
  - A, serial: steps 1-5 (advance with events, routing notify);
  - B, parallel: step 6-7 math per actor (`0x5833C0` blend + root-motion math), with the RigPose pool and allocator as the only shared calls;
  - C, serial in list order: step 8-9 (publish, `0x5813C0`), then EA's own post-loop work.
- Phase B still runs each track's `vtable+0xC` Evaluate. Those must be checked one by one (`0x566070` already writes a global, row 6) and must not touch refcounts on shared clips. The evaluate side of `0x5833C0` takes no refcounts in the decompile, but the per-controller Evaluate vtables were not all read.
- **Gain** [inferred]:
  - The whole family is ~33 ms/s, and Phase B is only part of it.
  - Phase A must stay serial, and every active actor pays for it.
  - The idle path (step 1) and the list walk are serial too.
  - If B is half the family, 8 workers save at most ~14 ms/s, minus fork/join (two passes per tick, so ~2 joins per tick) and pool/allocator lock contention.
  - Best realistic guess: **5-15 ms/s**, and only on lots with many animating Sims.
- **Code size:**
  - a C++ reimplementation of `0x940430` (847 decompiled lines, SIMD math that must match bit for bit) split into A/B/C: ~700-1,000 lines;
  - a worker pool with fork/join on the sim thread and a per-actor phase buffer: ~200 lines;
  - plus a hook on both passes (`0x571AD0` and `0x571B90`, and the sub-step loop in `0x942C50`), with a runtime switch for A/B.
  - Effort **L-XL**.
- **Risks (high):**
  - silent pose divergence from a math mismatch;
  - hidden shared state in per-controller Evaluate vtables;
  - non-atomic refcounts on shared clips (heap corruption, the same failure class as the async route crash);
  - parent/child transform order;
  - S3SS or other mods that hook the same functions.

## 5. Cheaper alternatives (serial, lower risk)

1. **Skip Evaluate for actors that need no pose** [inferred]: off-screen, not moving, no root-motion track. Keep Advance every tick so clip events, track completion and SACS timing are unchanged. Root motion (rig state `+0x60..+0x7C`, extracted in step 6) feeds movement, so actors with a root-motion track or a routing component must still evaluate. This is a serial patch around step 6 with the same ceiling as the parallel version and none of the threading risk.
2. Make the idle path cheaper. Resting actors still pay two list walks, two `0x942C00` calls, `0x93E190`/`0x582830` and the `0x940430` prologue every tick. Only measurement (§6) can say whether this matters.

### 5.1a Static check of alternative 1 (2026-10-15): dropped as specified

Ghidra headless, read-only, on a copy of the H3 project. No game run, no build.

- **An off-screen signal exists** [verified]. `IC_World_IsObjectOnScreenImpl` (`0x78AAB0`, script `World.IsObjectOnScreen`, so callable on the sim thread) works in three steps:
  - It gets the camera through `0x96B850` (`DAT_011E9400` -> `+0x14` -> `+0x8C` -> `+0xC` -> `0x9613F0`).
  - It takes the object position from `0x959000` (transform component `DAT_011E9158`) and the bounding sphere from component `DAT_011E8FC4` (`0x949DB0`, `0x68BBB0`).
  - It tests that sphere against the camera's frustum planes in `0x68D130`: about 30 multiply-adds, no locks.
  - Whether the sim-side frustum is the current or the previous frame was not checked.
- **Evaluate runs only for root-motion tracks** [verified gate, inferred meaning]. The Evaluate block in step 6 needs `cStack_46a9`, the OR of every track's `vtable+0x2C` in `0x583D00`. Its meaning is inferred: the clip flag is bit 1, and the only use of the result is the root delta (next point).
  - For ClipController, `+0x2C` is `0x5681C0` (`mov al,[ecx+0x5FB]; ret`).
  - `+0x5FB` is written only when a clip is assigned (`0x569C50`), from clip flag bit 1 (`0x57A690() >> 1 & 1`).
  - So idle actors already skip Evaluate, through the idle early exit or because none of their tracks has that flag.
- **The evaluated pose is thrown away** [verified]:
  - The RigPose comes from the pool `0x5748A0` and is initialised by `0x574F50`, which is only the constructor (identity fill).
  - It is filled by the graph root's `vtable+8` (`0x54C570`).
  - Only the root delta (pose `+0x10..+0x2C` -> rig state `+0x60..+0x7C`) is kept. The pose is released by `0x55CFA0` in the same tick.
  - The epilogue clears `+0x60..+0x7C`, so the delta only feeds this tick's transform publish (`0x959670`).
  - This Evaluate does not build the drawn pose. Where the drawn pose is built (probably the render side) is not known [inferred].
- **Consequence**:
  - Skipping Evaluate for an actor that reaches it drops that tick's root motion, so the Sim's simulated position drifts (routing, slots, saves), on screen or not [inferred from the data flow].
  - Running it every Nth tick keeps only the last tick's delta, unless the graph's delta covers the whole interval (unverified).
  - So alternative 1 as specified either saves nothing (idle actors) or changes sim state (moving actors). **Dropped.**
- **EA already has a one-shot skip** [verified]: bit 14 (`0x4000`) of `this+0x2C` skips the whole Evaluate block and is cleared on every call. Its setter was not looked up.
- **Next options**:
  - the §6 timing split first (drop the family if Evaluate is small);
  - alternative 2 (the idle prologue);
  - or find the drawn-pose path and apply the off-screen skip there.
- Not checked: side effects of the graph node `vtable+8` evaluators (a pose cache, for example) and the `+0x2C` getters of the other controllers (`0x565B10` and others).

## 6. What to measure before any build

Probe-only, in a test run (not done here):
- per-tick count of actors visited per pass;
- time split of `0x940430` into Advance (`0x583D00`), Evaluate (`0x5833C0`) and publish (`0x959670`), for example with Frida enter/leave timing on those three functions;
- the share of actors that reach step 6 at all;
- clip events fired per second through `0x568710`.

If Evaluate is under ~1% of sim-thread time, drop the idea. If it is above ~2%, prototype alternative 5.1 first.

**Timers built 2026-11-15 (lane L9, offline, not run): `[AnimPhase] Enabled=1`** (default 0, counters v201 `an*`) times four calls inside `0x940430` by rel32 rewrite: Advance `0x940569 -> 0x583D00` (`ret 0xC`), routing notify `0x94059E -> 0x947590` (`ret 8`), Evaluate `0x940742 -> 0x5833C0` (`ret 8`), publish `0x94118B -> 0x959670` (`ret 4`) [verified call sites and rets, Steam 1.67]. n / us / max us per phase; `anEvalN / anAdvN` = share reaching step 6. Not covered: actors per pass, clip events `0x568710`. Queue (not run): `tools/harness/queues/lane-L9-1115.json (job bp-l9-diag)`.

### 6.1 First G10 numbers and the class split (2026-11-18, `[AnimPhase] Split`, NOT RUN)

**The timers did run once** (`bp-l9-diag` 2026-11-15 16:17 PHT, rerun, PASS, noise medium; `probe-out/queue/lane-L9-1115-*-native.json`,
cumulative from game start incl. the world load, ~12 min) [measured]: Advance 9.46 M calls 12.2 s (max 61 ms), notify 0.51 M 0.06 s,
**Evaluate 0.31 M calls 0.25 s** (3.3% of Advance calls reach step 6, max 0.28 ms), publish 0.20 M 1.7 s. So Evaluate is ~0.4 ms/s,
under §6's 1% drop line: the parallel-Evaluate idea (§7, docs/23 Next big wins #8) has no target [measured, one noisy run]. The four
phases sum to ~21 ms/s of BP's 65 ms/s animPose bucket; the rest is pose-pass work outside these four calls (time step 0x942C00,
the sub-step walks, 0x940430's own body) [inferred, the split below measures it].

**Split built (branch `feature/animsplit-1118`, ts3native 0.36.275-animsplit1, `[AnimPhase] Split=0` default, needs Enabled=1,
counters v208 `anx*` at 0x6590; measurement only)** for Rodney's 11-18 question "what animates off camera, can it be simplified and
snapped back when visible" (measure before building):
- every rig pose call `0x940430` timed by **pose kind** (`anxPoseN/Us[kind][class]`: 0 whole tick `0x571C3C`, 1 root-motion sub-steps
  `0x571D20` / `0x571DDC`, 2 sub-step loop `0x942C50` at `0x942CC6` / `0x942CF1`) and **actor class** (0 household Sim or unknown scope,
  1-3 NPC on screen / off screen within the camera reach / unseen, 4-6 object the same, 7 not classified). All five sites are
  `mov ecx, actor; push dt; call 0x940430` [verified disassembly, Steam 1.67]. `0x571C3C` rides behind `[UnseenAnim]` when that is on
  (shipped default: Mode 1, so unseen NPC time is after its decimation); `0x571D20` / `0x571DDC` are left to `[UnseenRootMotion]` when
  it is enabled (bit 16 of `anxReady`).
- the four §6 phase timers per class of the enclosing pose call (`anxPhN/Us[phase][class]`): Evaluate share and Advance / publish cost
  of off-screen actors.
- clip event dispatches `0x568710` (3 call sites, thiscall one argument `ret 4` [verified]) inside / outside a pose call
  (`anxClipN/Us`).
- locomotion `Locomotion_UpdateAgent 0x957210` of Sims by household / seen NPC / unseen NPC (`anxLocoN/Us`, with `[UnseenProbe]`
  Enabled=1, which already splits visible / unseen x Sim / other in `unAgent*`).
- actors per pass = `anxPoseN / aaPasses` (`[UnseenAnim]` counts the passes).
- Household = the fresh held table ts3fixes pushes (`PushHeld`: all active-household actors plus riders / mount partners); no fresh
  table counts as household (ledger I-512-1).
- **Part 2 (counters v209, struct 6660 int32)**, after the static RE note (§8: the drawn pose runs on the main thread and EA already
  skips it off camera; ~40 ms/s of the `0x571B90` pass had no timer): time step `0x942C00` at `0x571BF3` by class (`anxTsN/Us`), the
  pass-end drains `0x577060` (`0x571E0F`) and `0x571A50` (`0x571E1A`) (`anxDrainN/Us`), the SACS event push `0x742710` at `0x801E51`
  inside / outside a pose call (`anxEvN/Us`), and the whole `Locomotion_Update 0x83CED0` pass at `0xD81C5C` (`anxLocoPassN/Us`, behind
  locowin.cpp's thunk through `g_g0wOrig` when `[RouteAsync] H2=1`, the shipped value). All `ret` / argument shapes [verified
  disassembly, Steam 1.67]. Remainders: `aaPassUs` - (pose + time step + drains) = the pass's own walk and root-motion tests;
  `anxLocoPassUs` - `unAgentUs` = locomotion outside the agent updates (route follow `0x83AC20` / `0x83B230`, allocs, the tail
  [inferred from the callee list]). `0x571BF3` is left to `[UnseenRootMotion]` when that is enabled. Drawn pose: not timed (main thread).

Queue (correctness + profile, not timing, not run): `tools/harness/queues/anim-split-profile-1118.json`, BP + IP + SV 10 min, ~42 min. Pass needs the `anxReady` line with every site group on.
Decision it feeds: off-screen NPC / object pose time large enough (>= ~3 ms/s per world) = design a coarse off-camera pose with
snap-back (the `[UnseenAnim]` debt model, extended to root-motion sub-steps and objects); small = park the animation lane.

## 7. H3: callee sweep to depth 3 and controller vtables (2026-10-15)

**Method.** Ghidra headless on the copy project `game-copy/ghidra-h1`, read-only: the call tree of `0x940430` to depth 3 (99 functions); every caller of the controller constructor `0x565A80` and the vtables they install (54 primary and secondary vtables in `0xFC1AA0-0xFC2548`, plus TrackManager `0xFCBA70`); slots `+0x8` (Advance) and `+0xC` (Evaluate) of each, to depth 1 (~100 more functions). Each function was tagged by pattern (Mono/IC names, event dispatchers `0x568710`/`0x5689A0`/`0x583AB0`/`0x805870`, allocators, `LOCK()` increments, plain `x[1]++`, global writes, routing range `0x82xxxx-0x83xxxx`, render publish) and the hits were read. Decompiled output stayed under `game-copy/`.

### 7.1 Classification of the `0x940430` tree

| Class | Functions | Notes |
|---|---|---|
| **Pure pose math / per-actor reads** (worker-safe) | `0x5833A0`, `0x5826B0`, `0x54C6E0`, `0x54C5C0`/`0x54C5D0`, `0x55DA60`, `0x7F1620`, `0x580430`, `0x5EB7C0`, `0x935770`/`0x935820` (component lookup by type), `0x554EE0`/`0x554D10`/`0x554DB0` (pose blend), `0x54C570` (root delta), `0x959170`, `0x575650` | No global writes, no refcounts, no calls out [verified] |
| **Evaluate graph build** (per-actor arena) | `0x54C720` (inits a 0x4000-byte frame arena inside the 18 KB stack frame, `auStack_4350`), `0x5833C0` -> controller `vtable+0xC`, `0x54C770` (allocates a pose-graph node from that arena, factory looked up by type hash in the table `DAT_011CD124`), ClipController Evaluate `0x5695F0` (emits clip, time, weight and mask nodes `0x54DCD0..0x54DEF0`) | The arena lives on the caller's stack, so it is thread-private when each worker has its own stack [verified layout; factory table read-only during play inferred]. `IC_GameUtils_GetFrameNumber` and `IC_StreamHost_Flush` appear only as native function-pointer arguments to the arena init `0x4E85B0` (names from the IC map); they are not Mono calls [verified] |
| **Pose build** | `0x574F50` (RigPose from the node graph, node vtable calls), pool `0x5748A0` (`EnterCriticalSection(0x11CD8D8)`) | Thread-safe through the pool lock [verified] |
| **Shared refcounts, atomic** | resource handles in `0x56E6E0`, `0x56EEA0`, `0x5705B0`, `0x565BB0`, `0x568900`, `0x568E10`, `0x55DFF0`, `0x55E040`, `0x7460F0` all use `LOCK`-prefixed inc/dec | Correction to §3 row 2: the resource-handle refcounts (clips, rig data) **are atomic**. The plain `x[1]++` ones (`0x567A50`, `0x566650`, `0x566840`, `0x5665F0`, `0x56BF10`, `0xCF3C70`, `0x566FB0`, `0x56FE70`, `0x56BFA0`, and the self-AddRef `*(this-0x24)++` in `0x56E6E0`/`0x5705B0`/`0x56A670`) are on controllers and tracks, which belong to one actor's TrackManager [verified split; "one owner" inferred] |
| **Non-atomic global counters / statics** | controller id `_DAT_0114F590++` in `0x565A80` (Advance only); Evaluate `0x566070` writes `_DAT_011CD7D8`; lazy statics `_DAT_011DC4A0` (step 7) and `DAT_01157D28` (`0x5EC9E0`, step 8, first-use init that allocates via `0x5EC820`) | Only `0x566070` and `_DAT_011DC4A0` sit in the parallel slice; both are fixable (thread-local / pre-initialise) [verified writes] |
| **Clip events / script queue** | Advance slots only: ClipController `0x568E10` -> `0x5689A0` -> `0x568710`; `0x569070`; TrackManager `0x583AB0`; SACS `0x805870` -> EventQueue push `0x742710` | No event dispatcher is reachable from any `+0xC` Evaluate slot or from `0x5833C0`/`0x574F50` [verified to depth 1 under each slot] |
| **Routing / locomotion** | `0x947590` -> `0x83B080`, `0x829A60`, `0x833560`, `0x8321F0` (allocates), `0x82AD60`, `0x82CBF0`, `0x82A720`, `0x8297C0`, `0x829840` | Step 4 only, before Evaluate [verified] |
| **Render publish** | `0x959670` -> `0x958430`/`0x958560` -> `0x692F70` -> `0x593E60` | `0x593E60` takes the critical section at `this+0xD0` around the message-buffer push, so the publish is **lock-safe** (§3 row 9 said "not checked"). It stays serial for order (parent before child) [verified lock; order inferred] |
| **Allocates** | `0x583D00` (TracksToRemove scratch, `0x4E3F90`), `0x5748A0` (RigPose pool), `0x5EC820`, `0x580BF0` (epilogue `0x5813C0`), routing `0x8321F0`; arena `0x4E69D0` is the locked EA allocator | Locked; contention only |
| **Mono / scheduler** | none | No `mono_*` callee and no scheduler call anywhere in the depth-3 tree or under the controller slots [verified to that depth] |

### 7.2 One new hazard in Evaluate: cross-actor reads

The controller with vtable `0xFC2068` (correction 2026-10-30, docs/67 §26: this is `Animation/ConformToRouteController`, constructors `0x56E420` / `0x56E4C0`, `0x56E580` is its destructor [verified disassembly]; Evaluate `0x56EEA0`, 352 lines of quaternion math) looks up a component of **another object** (`0x935770(DAT_011E9138)` on a target, then its transform through `vtable+0x10/+0x3C`) during Evaluate. This looks like a look-at / aim or attach controller [inferred from the math and the lookup]. Its result depends on the target's transform, which another worker may be writing in the same phase. So those actors must either evaluate serially after the parallel batch, or read the target's previous-tick transform (EA's own order may already give that, unverified).

### 7.3 Verdict after H3

- **Feasible with a phase split, unchanged in shape** (§4): A serial (Advance + events + routing notify), B parallel (`0x54C720` + `0x5833C0` + `0x574F50` + root-motion math), C serial (publish, epilogue `0x5813C0`, post-loop work).
- **Phase B is cleaner than §3 assumed** [verified]: no events, no Mono, no routing; arena on the stack; pool and render buffer locked; shared refcounts atomic. Remaining fixes for B: make `_DAT_011CD7D8` thread-local or skip it, pre-initialise `_DAT_011DC4A0`, keep actors with a `0xFC2068` controller (and any other controller whose Evaluate calls `0x935770`) in a serial tail.
- **Must stay serial:** all of Advance (`0x583D00`, clip and SACS events, controller creation with the id counter, non-atomic controller refcounts), the routing notify `0x947590`, the transform publish in list order, the epilogue, and the post-loop track/deferred-id drains. Event *deferral* is not needed for the split, because events fire only in Phase A.
- **Still a reimplementation:** B sits in the middle of `0x940430`'s single 18 KB frame, so the split needs a C++ rewrite of `0x940430` (not a hook). Effort L-XL as in §4.
- **Movable estimate** [inferred, unmeasured]: Phase B is the only movable part, at most about half of the ~33 ms/s family (~16 ms/s); after fork/join and pool contention, **~5-15 ms/s** with 8 workers, only on lots with many animating actors. The §6 timing split (`0x583D00` vs `0x5833C0`+`0x574F50` vs `0x959670`) is still the go/no-go gate before any build; the serial skip-Evaluate option (§5.1) remains the cheaper first prototype.

## 8. Off-camera follow-up (2026-11-18, static + existing run data; ledger docs/51 §511)

- **Phase split measured: drop it.** Lane L9 `[AnimPhase]` timers, `bp-l9-diag` 2026-11-15 (2 runs, BP, 10 min, correctness job so indicative only) [measured]: Advance `0x583D00` ~20 ms/s (9.5 M calls), Evaluate `0x5833C0` ~0.4 ms/s (3.5% of calls), publish `0x959670` ~2.7 ms/s, routing notify ~0.1 ms/s, of a 67.5 ms/s pass (`aaPassUs` = `animPose`). Phase B is ~0.4 ms/s; about 40 ms/s of the pass (walks, `0x942C00` time steps, drains `0x577060` / `0x571A50`) has no timer yet.
- **Drawn pose is main-thread work and EA culls it by visibility** [verified gate and call chain]: `RenderSystemService_Update 0x6E3620` -> `FrameDb_ConsumeAndInterpolate 0x693780` -> `0x6934C0` -> `0x9420E0` builds RigPoses from the frame's serialized anim records (gate `0x942374..0x942398`). The sim-side record writer `0x93F920` returns without a record unless the actor passes `0x93EAF0` (bounding sphere x3.0 `[0x1074010]` vs camera frustums `0x980F70` -> `0x980E00`), or is forced (`+0x38`, set by `IC_Animation_ForceAnimation 0x73A9E0` via `0x93E0A0`, which also posts message `0xE092878` -> model `+0x29D` bit 0), or has `+0x2C` bit 31.
- **Setter of the one-shot Evaluate skip `+0x2C` bit 14** [verified write]: `0x84B2E4` in the route-follow end-step helper `0x84B0C0` (anim component type id `0x749C6A98`, vtable slot `0x93E640`); read and cleared at `0x9406D0..0x9406E2`. Meaning [inferred]: drop the root delta on the tick a route ends with its end facing.
- Full write-up with the script-side waits and ranked designs: `probe-out/anim-offcamera-re-1118.md` (local).
