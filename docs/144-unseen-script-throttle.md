# 144. Unseen script throttle: per-Sim script and animation work for Sims the player cannot see

Written 2026-10-21. Rodney's direction: instead of moving script work to a second thread (docs/129), run per-tick script and animation work less often for Sims the player cannot see, and ship the big gains fast. Static reading of the decompiled script library (Steam 1.67) and of `TS3W.exe` (capstone over `game-copy/bin`), an offline ts3fixes build, a self-test and a queue that is written but **not run**. No game run. Tags as in AGENTS.md; ledger rows in docs/51 §207. Locomotion (the steering and avoidance `+0x918` takers) is docs/143 and native; this note is the script side plus the animation question.

## Summary

- **EA already does this for most unseen Sims [verified].** The per-Sim script update `SimUpdate.Simulate` runs its heavy body (autonomy update with motives, buffs, mood, skills, careers, relationships; environment score; look-ats; curfew) only for Sims with `Autonomy.IsRunningHighLODSimulation`, at most 2 such Sims per tick. High LOD = the active household, Sims on the active lot or on the active Sim's (non-world) lot, hosted guests, daycare children, resort workers, or outdoors **and on screen**, recomputed every 60 ticks. Every other Sim skips the body; when its autonomy runs, `Autonomy.RunAutonomy` refreshes its motives once with the whole elapsed time, and `Motive.Update` then uses the time-of-day auto-satisfy curve instead of decay. So far-away townies already get "update rarely, integrate the gap". What a throttle can still take is the **unseen part of the high-LOD set** (active lot while the household is away, guests and workers out of view, the 60-tick cache after a Sim walks off screen) and the **reaction broadcasters**, which pulse whatever the Sims' LOD.
- **Per candidate (docs/129 / docs/95 costs, BP):**

| Candidate | Cost now | Per tick | N x dt equivalent? | Verdict | Expected gain (N as built) |
|---|---|---|---|---|---|
| Reaction broadcaster pulse (`PulseSims` -> `GetInRadiusSims`) | 16.4 ms/s BP, 12.8 IP (after the 1-in-4 rule) | per broadcaster 1 / sim-minute | no integrator; enter/exit callbacks start late | **build** `unseenbroadcast`, N = 16 per broadcaster | **~4-8 ms/s BP, ~3-7 IP, ~0.5-1.5 SV** [inferred from measured counters, §2.1] |
| Environment score (`SimUpdate.CalculateSimEnvironmentScore`) | 18.3 BP, 17.4 IP | per high-LOD Sim every 2 sim-min | moving value: skip = keep the last score and buffs | **build** `unseenenv`, N = 4 | **0.7-2.7 ms/s** for an unseen share of 5-20% of high-LOD calls; real number from the probe |
| Motive decay and the rest of `Autonomy.Update` (`Motives.UpdateMotives` ~32 ms/s, of it decay ~8) | ~32+ BP | per high-LOD Sim per SimUpdate slot (>= 1 sim-min) | **yes**: every step integrates the sim-minutes since the last one; clamps, buff thresholds and rate changes make it first-order equal, at most N-1 minutes late | **build** `unseenmotives`, N = 4 sim-minutes | **1.2-4.8 ms/s** at 5-20% unseen share; probe first |
| Closest visible lot (`Camera.GetClosestVisibleLot`) | 6.0 BP | camera task, not per Sim | - | not a per-Sim throttle; `kHoursMemo` covers its hot callee | 0 |
| Zombies on lot (`Lot.UpdateNumZombiesOnLot`) | 1.5 BP / 7.5 IP | per lot | - | not per Sim; docs/129 memo stays the option | 0 |
| Environment look-ats, relationship decay, interaction queue timer | < 1 each | high-LOD only, >= 1 sim-min | look-ats: moving; relationships: inside `Autonomy.Update` | covered by `unseenmotives` (relationships) or not worth a hook | ~0 |

- **Animation (§3) [verified structure, cost inferred]:** the brain/body update of an off-screen walking Sim (`OffScreenSimBrain` + `SimpleBody`) still runs every tick; `SimpleBody`'s update slot is a stub, but the off-screen brain's path behaviour is the **larger** routine (1,575 vs 625 bytes) and both brains keep avoidance. The two animation passes (SACS driver, track Advance, rig pose) walk every actor every tick with no visibility test. Throttling Advance or the SACS driver is not equivalent (clip events and SACS events time interactions; root motion moves the Sim), so it is **design only** and needs native work and the docs/22 §6 split first.
- **Built (offline, default off):** ts3fixes `UnseenThrottle.cs` + `UnseenThrottleCore.cs`: switches `unseenenv`, `unseenmotives`, `unseenbroadcast`, probe switch `unseenprobe`; tunables `kUnseenThrottleEvery` (4) and `kUnseenBroadcastEvery` (16); counters 146-155 (registry row); agent readout `unseen_*`. Self-test `node tools/selftest/unseen/run.ts` PASS (gate cadence and stagger, time gate cap, N x dt equivalence of a clamped integrator, threshold lateness, no sim time lost). `build-fixes.ts` and `build-agent.ts` build. Queue `tools/harness/queues/unseen-script-throttle-ab.json` (10 jobs, not run).

## 1. Who runs the per-Sim script work today [verified]

### 1.1 `SimUpdate.Simulate` (one script task per Sim)

- Each loop: `ElapsedTimeInMinutes` since the last loop; a static per-tick limit `sUpdateLimit < 2` and `sHighLODCount` set how many high-LOD Sims update per tick and how long each sleeps (`kSimUpdateSleepTicks` = 5, more when over 2 high-LOD Sims).
- **High LOD and within the limit:** curfew, hair, fairy aura, `Autonomy.Update()`, then every 1 sim-minute look-ats and the clothing broadcaster, body shape every 30, and for toddlers and up `UpdateTraitRelatedBuff` plus, every 2 sim-minutes (`kEnvironmentUpdateFrequencyMinutes`) or when `Sim.UpdateEnvironment` is set, `CalculateSimEnvironmentScore` + `CalculateSimEnvironmentBuffs`.
- **Low LOD:** only the "hide indoors on a residential lot" fade; nothing above.
- Both: `InteractionQueue.UpdateTimer`, and the active Sim's motive and mood lerp (UI).

### 1.2 `Autonomy.IsSufficientlyOnScreenForHighLODSimulation`

Cached `mbHighLOD`, recomputed at most every 60 ticks: true for the active Sim's household; for a Sim on the active lot, on the active Sim's lot (not the world lot), a hosted guest of a selectable host, a daycare child, a resort worker; else true only when outdoors (`RoomId` 0/-1 or `IsOutside`) and `World.IsObjectOnScreen`. So the high-LOD set is close to "what the player can see or is responsible for", with three gaps a throttle can use: lot-based membership when the player's view is elsewhere, guests/workers out of view, and the 60-tick lag.

### 1.3 Motives

- `Autonomy.Update()`: `dt = ElapsedTimeInMinutes(ref mLastTime)`; a non-active Sim under 1 sim-minute returns **without touching `mLastTime`** (time accumulates); else `Motives.UpdateMotives(dt)`, `BuffManager.UpdateBuffs(dt)`, `MoodManager.UpdateMood`, `SkillManager.UpdateSkills`, `CareerManager.UpdateCareers`, `SocialComponent.UpdateRelationships`, opportunities. Two callers only: `SimUpdate.Simulate` (high LOD) and `Autonomy.RunAutonomy` (only when **not** high LOD, to refresh motives before scoring).
- `Motive.Update(hours)`: low LOD, or a high-LOD step over `kMaxUpdateHoursBeforeAutoSatisfy` (1 sim-hour), uses `AutoSatisfyMotive` (a time-of-day curve: not an integrator). Otherwise `UpdateMotiveWithDecay(hours)`: the decay rate plus posture, daycare, buff, trait, swimming and weather terms, **each multiplied by `hoursPassed`**, then `SetValue` (clamp), the UI arrow timer (`+= hours x 60`), `UpdateMotiveBuffs` (moodlet triggers by value range) and the UI lerp for selectable Sims.

## 2. Candidates

### 2.1 Reaction broadcasters (`unseenbroadcast`)

**What it does.** Every broadcaster (music, fire, smell, a Sim's clothing, ...) pulses once per sim-minute (`kSimulateFrequencyMinutes = 1`: `Lot.UpdateReactions` passes elapsed sim-minutes, `Simulate` pulses once when its accumulator passes 1 and drops it by 1) [verified, decompile; docs/209 #94 corrected "once per sim-second"]: a radius query for Sims (`Queries.GetObjects`), room/species/tomb filters, `Sim.CanSeeObject` per candidate, then enter/exit callbacks. `BroadcasterPulseHook` (`kBroadcasterThrottle`, on by default) lets a broadcaster far from the camera and the active household pulse one call in four, through **one shared counter** `(++sCalls & 3)`.

**Unseen share [measured counters, share inferred].** `broadcast_skipped / broadcast_pulses`: IP 0.68 (`moved-ip-*`), BP 0.66 (wildflower first-minute run, `t14`), SV 0.24-0.26 (`moved-sv-*`). Skipped = 3/4 of unseen calls, so the unseen share of pulse calls is ~0.88 (BP), ~0.91 (IP), ~0.33 (SV). Of the pulses that still **run**, unseen ones are 0.22 / (0.22 + 0.12) = **~65% on BP**, ~72% on IP, ~11% on SV. docs/129's 16.4 ms/s BP is that residual.

**Throttle.** Per broadcaster (key = reference hash, no reference kept), one pulse in `kUnseenBroadcastEvery` (16) while far from viewers, first pulse staggered by the key; near the player always. The shared counter goes: with fixed pulse orders it can starve the same broadcasters (a broadcaster whose calls keep landing on the residue that runs pulses every time, the others never) [inferred; the per-key gate cannot].

**Effect.** No integrator: an unseen Sim starts or stops reacting (enjoying music, fleeing a fire, smelling food) up to 16 pulses (~16 sim-minutes) late instead of 4. Fire and panic reactions on an unseen lot come later; the fire itself is not a broadcaster-driven object. Near the player nothing changes.

**Gain.** 65% x 16.4 x (1 - 4/16) = **~8 ms/s BP** if an unseen pulse costs what a seen one does; half that if unseen areas are emptier (radius query finds fewer Sims): **~4-8 ms/s BP, ~3-7 IP**, small on SV.

### 2.2 Environment score (`unseenenv`)

**What it does [verified].** Every 2 sim-minutes per high-LOD toddler-or-older Sim: the lot's score, then outdoors a radius object query + nearby lots' environment objects + tree counts, indoors the room's puddles and burnt tiles (event `kSawPuddleOrBurntSpot`), room objects and aromainators, Sim-to-Sim environment reactions (`DoSimEnvironmentUpdateInternal`), windows/light, then buffs (`AfraidOfTheDark`, `UnfinishedRoom`, `ItsDark`, ...) and the environment motive.

**Throttle.** For an unseen Sim (and `UpdateEnvironment` not forced) the call runs one time in N (keyed by the Sim, staggered), so every 2N sim-minutes. A skipped call writes nothing; the caller's `CalculateSimEnvironmentBuffs` still runs on the unchanged state.

**Effect.** A moving value, not an integrator: an unseen Sim's room moodlets (decor, dark, dirty room) and the puddle event come up to 2(N-1) sim-minutes late; a lot or room change sets `UpdateEnvironment` and runs at once.

**Gain.** 18.3 ms/s x unseen share s x 3/4. EA's LOD keeps most unseen Sims out (§1.2), so s is small: 0.7-2.7 ms/s for s = 5-20%. `bp-unseen-share` measures s (`unseen_env_unseen / unseen_env_calls`).

### 2.3 Motives and the rest of `Autonomy.Update` (`unseenmotives`)

**Throttle.** For a high-LOD unseen Sim on the SimUpdate path, skip `Autonomy.Update` until N sim-minutes have passed since `mLastTime` (capped at 30 sim-minutes), leaving `mLastTime` alone like the game's own 1-minute floor; the next run integrates the whole gap. RunAutonomy's refresh (low LOD) always runs.

**Equivalence [verified for the shape, self-test].** Each term of `UpdateMotiveWithDecay` is `rate x hoursPassed`, then a clamp. For a constant rate N steps of dt and one step of N x dt give the same value, also through the clamp (self-test: 2,000 random cases). The differences:
- a rate change inside the window (interaction ends, posture changes, a buff arrives) is applied to the whole window by the later rate: error at most `|r1 - r2| x` the part of the window before the change (self-test bound holds);
- moodlet triggers (`UpdateMotiveBuffs`) and buff expiry (`UpdateBuffs`) are seen at most N-1 sim-minutes late (self-test: threshold lateness <= (N-1) dt);
- the UI arrow timer is an exact integrator;
- the window must stay under 1 sim-hour, else `Motive.Update` switches to auto-satisfy: the gate caps it at 30 sim-minutes (self-test: no gap reaches it, no sim time lost).

**What the player could observe.** Only for Sims out of view and outside the household: a Sim that comes back into view shows motives that were updated up to N minutes ago; at the next SimUpdate slot (seen = no gate) they catch up in one step. Autonomy of a high-LOD unseen Sim scores motives up to N minutes old.

**Gain.** `Motives.UpdateMotives` ~32 ms/s (decay ~8, buff triggers ~11, docs/129 §2.3) plus the buffs, mood, skill, career and relationship updates under `Autonomy.Update`, x s x 3/4: **1.2-4.8 ms/s** for s = 5-20%. The `SimUpdate` slot itself is not handed to another Sim (the 2-per-tick limit counts the slot), so the saving is real sim-thread time.

### 2.4 Classifier and snap-back

Unseen = not selectable, not in the active household, `FarFromViewers` (outside the camera reach `max(80 m, 2 x camera height over target)` and 80 m from every active-household Sim, read once per tick, the trip shortcuts' rule) and not `World.IsObjectOnScreen`. The posture and follower tests of `TownieUnseen` are left out: they matter for moving a Sim, not for updating it less often. A seen call always runs and forgets the Sim's gate, so the next unseen stretch restarts staggered. The broadcaster rule tests only the broadcaster's position (as today).

## 3. Animation and the off-screen brain/body

### 3.1 Per-tick locomotion update of one agent [verified]

`Locomotion_UpdateAgent 0x957210` -> behaviour update `0x839F70(agent+8, dt)`: brain pre-update `0x8398B0` (each behaviour's slot `+0xC`, then the brain's `+0x1C(dt)`), body update `0x839AB0` (each body part's `+0xC(dt)`), brain post-update `0x839920` (`+0x20(dt)`), then the debug draw when the locomotion system's `+0x100` byte is set.

| | On screen | Off screen |
|---|---|---|
| Brain builder | `SimBrain` `0x953410` (vtable `0x1044428`) | `OffScreenSimBrain` `0x9534A0` (vtable `0x1044434`) |
| Behaviours | avoidance `0x846820` (update `0x847390`, 138 B) + behaviour A `0x848150` (vtable `0x1026EE8`, update `0x8481F0`, 625 B) | avoidance + behaviour B `0x847710` (vtable `0x1026E98`, update `0x847830`, **1,575 B**) |
| Body | `AnimatedSimBody` `0x84EFF0` (vtable `0x1027294`; slot `+0xC` `0x848CD0` looks up a component at `+0x124` by hash `0x549B548B` and forwards to its `+0x48`) | `SimpleBody` `0x858510` (vtable `0x102770C`; slot `+0xC` `0x8558D0` zeroes a 16-byte output and returns 0) |

So the off-screen swap removes the body's link to the animated component, but the off-screen path behaviour is the larger routine and avoidance stays: off-screen walking Sims are cheaper in the body only [code sizes verified; run-time cost inferred]. Behaviour B most likely integrates the path position itself (EA's kinematic follower for Sims it does not animate) [inferred]; that would make an N x dt step a bigger position jump (corner cutting, portal events late), docs/143 §4 option (a): not taken.

### 3.2 Animation passes [verified in docs/22, re-checked against this question]

`MonoScriptHost_Simulate` runs `0x571AD0` (SACS state-machine driver `0x94E9C0`, time step, rig pose) and `0x571B90` (time step `0x942C00`, rig pose `0x940430`) over the whole actor list every tick, with **no visibility or LOD test**. Idle actors exit early in `0x940430`; pose Evaluate runs only for root-motion tracks, and its result is the root delta that moves the Sim (docs/22 §5.1a). ~33 ms/s for both passes (docs/20, docs/28 N7).

### 3.3 Can animation be throttled for unseen Sims? Design only

- **Not equivalent:** Advance with N x dt every Nth tick fires clip events (prop parent/unparent, destroy prop, sound, VFX) and SACS events (which wake the interaction scripts waiting on them) up to N-1 ticks late. That makes interactions of unseen Sims last slightly longer and changes the order of events against the rest of the world. Root motion of a moving Sim would come in bigger steps.
- **Possible native design (U-A1):** at the per-actor call in `0x571B90` (and the matching one in `0x571AD0`), for an actor whose owner is an unseen Sim (docs/143's native classifier plus household ids from ts3fixes) and whose tracks have **no root motion** (the `+0x2C` OR of docs/22 §5.1a is false), accumulate dt and step every Nth tick. Gate on "no SACS event pending" if the driver exposes it (not read). Expected ceiling: unseen share of the ~33 ms/s x (N-1)/N minus the idle actors that already exit early; unknown until the docs/22 §6 split (per-pass time, idle vs active actors) runs with an unseen class.
- **Before building:** the docs/22 §6 timing split with the docs/143 `[UnseenProbe]` class added (one probe job). If unseen non-idle actors are under ~5 ms/s, drop U-A1.

### 3.4 U-A1 as built: `[UnseenAnim]` (2026-11-02, ts3native 0.36.153, counters v147, off, not run)

Rodney 2026-11-02: the five script-side unseen throttles showed no gain beyond noise (probe-out/throttle-gains-2026-11-02.md); do the native change.

**Measured cost (before building).**
- Pose pass `0x571B90` (`animPose` in stutter-profile, Frida-timed, so with hook overhead): median **BP 55.5 ms/s** (171 runs, 20.8-66.1), **IP 41.8 ms/s** (110 runs), SV 42.4 (77) over the newest 400 `probe-out/profile/*-seconds.csv` up to 2026-11-01 [measured]. The SACS pass `0x571AD0` is ~0.1-0.2 ms/s everywhere: nothing to save there, so the design leaves it alone.
- The off-screen brain: unseen walking-Sim agent updates are 56.6 ms/s on BP (docs/143, `unseen-first`), but EA already swaps them to `OffScreenSimBrain` + `SimpleBody`, and the whole-update decimation (`[UnseenThrottle] Rate=1`, docs/143 §8.4) saved nothing measurable (§8.15: the `+0x918` waits move to the next taker; Rate holds time only at speed 1). Not rebuilt.
- Unseen share of the pose pass: **not measured** (no per-class split existed). 94.4% of locomotion agent updates are unseen on BP (docs/143). Ceiling at Every=4 if the non-root-motion share of unseen Sims is 30-60% of the pass: BP ~10-25 ms/s, IP ~7-19 ms/s [inferred]. Above the ~3 ms/s stop line, so built, with its own per-class timing (`aaUs`) so the smoke job gives the real share; if `aaUs[1]` (unseen Sims' pose calls) is under ~3 ms/s on both worlds, drop U-A1 before the A/B.

**What the pass does [verified disassembly, Steam 1.67].** `0xD81C73` calls `0x571B90` (thiscall(list; dt), `ret 4`). Per actor: time step `0x942C00(dt)`; then, with a rig (`[actor+0x24]+0xB0` = TrackManager) and dt > 0, `0x93E190`: flag bit 15 of `[actor+0x2C]` or `0x582830` = any track's vtable `+0x2C` (root-motion getter, ClipController `0x5681C0` = clip byte `+0x5FB`). True: the actor is posed later in fixed 1/30 s sub-steps (`0x571D20` with `[0x10434C4]` = 1/30, remainder `>= [0xFC25C4]` = 1/60 at `0x571DDC`). False: **one call `0x571C3C` -> `0x940430(actor; dt)`** with the whole tick's dt. EA's own split thus already separates root-motion actors from the rest, and EA feeds `0x571C3C` whatever dt the tick has (large at fast speeds).

**Design (hook sites, rule).**
- Call rewrites (PatchCalls, both or neither): `0xD81C73` -> `AaPassThunk` (pass counter, camera refresh, visible-Sim list swap, pass time `aaPassUs`); `0x571C3C` -> `AaPoseThunk`. The root-motion sub-step calls are untouched, so a moving Sim's root delta is never batched.
- For an actor whose owner `[actor+4]` has the Sim component and is unseen by unseen.cpp's `UnObjClass` (off screen and beyond max(80 m, 2 x camera height) of camera and target): the call runs on every `Every`-th pass (staggered by actor address) with dt summed over the skipped passes (per-actor slot table, 1024 slots, keyed by actor + owner id). No time is dropped while the actor stays on this path.
- Holds (run now with the owed time): parented (vehicle, horse, carried), within `NearVisM` (10 m) of a Sim classed visible last pass (social partner, crowd edge), within 80 m of an active-household Sim (household and selected Sims included) or no household table fresher than 2 s (ts3fixes `UnseenViewers.cs`, now accepted when only `[UnseenAnim]` is on), slot held by another actor's debt, debt + dt over `MaxOwedMs` (250 game ms, the cap that keeps the summed step in EA's fast-speed range).
- Snap-back: a Sim classed visible runs that same pass with its owed time (`aaSnap`); the switch turning off runs the owed time at the next call (`aaFlushOff`).
- What a skipped call delays for that Sim by at most Every-1 passes (content of `0x940430`, docs/22 §2): clip events of non-root-motion tracks (sound, VFX, SACS script and AnimationDone events), the routing notify / footprint refresh (`0x947590` -> `0x833560`) and the transform publish. An unseen Sim's animation chain can run up to (Every-1) passes later per clip end [inferred]; the unseen-relaxed rule (memory "Unseen routing relaxed") accepts that.
- Ownership: animation is listed as Apex/EA in docs/28 T9. Rule "Apex-area experiments first": built by us, off by default; propose to Apex Radiance (issue #32) only with a measured gain. No overlap: Apex source, S3PP 1.1 and S3SS have no hook on `0x571B90`, `0x940430` or `0xD81C73`; no community animation LOD fix found (docs/03, docs/24).

**Switches and counters.** ini `[UnseenAnim] Enabled=0, Mode=1, Every=4 (2-8), MaxOwedMs=250 (50-500), NearVisM=10`; `aaMode` +0x5594 is the runtime switch (`--ab unseenanim`, tools/harness/native-switches.ts). Counters v147 (docs/registry.md), agent.js `aa*`. Self-test `TestUnseenAnim` (unseenanim-test.h): objects and visible Sims every pass, unseen 2 runs in 8 passes with the time conserved, snap-back, each hold, cap, switch-off flush, reused actor address. Ledger docs/51 §417.

**Queue** `tools/harness/queues/unseen-anim-ab.json` (not run): smoke (BP 4 min, `--ab unseenanim` 1-min slices: arm split, `aaMode` read back, `aaSkipped` only in on slices, `aaUs` split), correctness (BP 12 min mode on the whole run with the `unseen-snapback` camera repro: `aaSnap` > 0, no crash; stuck pair off/on 15 min), then the 10-min in-load A/B on BP and IP in both orders. Verdict: pose-pass ms/s (`aaPassUs`, animPose) and sim ms/s off minus on per world, pooled over both orders; hitch counts; stuck Sims not above off.

### 3.5 Which gate blocks U-A1, and the CapPasses loosening (2026-11-02, built, not run)

**Gate breakdown [measured, counters v147, `probe-out/profile/unseen-anim-ab-2026-11-0{1,2}-bp-ua-ab{,-r2}-ab-native.jsonl`, per-slice deltas, on slices only].** The pass sees ~17,500 pose asks/s, but **~14,400/s are non-Sim actors** (`aaN_2`, never decimated) and ~200/s visible Sims (`aaN_0`). Of the ~3,200/s unseen-Sim asks (`aaN_1`): debt cap `aaHold[5]` **2,590-3,300/s (~90%)**, parented `aaHold[0]` ~150/s, household viewer `aaHold[2]` 0-360/s, near a visible Sim ~1-4/s, slot taken ~3/s, no table / visible list full / unclassified 0; skipped `aaSkipped` 24-72/s. Root-motion actors never reach `0x571C3C` (I-417-1), so "root-motion track" is not a gate here, and the 80 m household radius is not what blocks on BP.

**Why the cap.** The cap fires with no debt: `owed + dt > MaxOwedMs` with owed 0 means one pass already carries > 250 game ms at the harness speed [inferred from the counters: cap holds ~60x the skips; dt itself is not counted]. So MaxOwedMs=250 allows no skip at all at that speed, and the ~9.5 ms/s `aaUs_1` stayed the same on vs off.

**Loosening (ini `CapPasses`, `HardCapMs`, off by default).** CapPasses = N (2-8): the cap becomes max(MaxOwedMs, N x this pass's dt), never above HardCapMs (default 1500, 250-3000). One step then sums at most N passes' time, the same bound in passes that the 250 ms gave at speed 1; all other holds unchanged (parented, near visible, household 80 m, no table, slot). Safety rests on I-418-1 (a step of up to N x EA's own fast-speed dt behaves like EA's dt). Expected: most of the ~2,800 cap holds/s turn into skips at Every=4, ~6-7 ms/s of `aaUs_1` [inferred, ceiling]. Self-test `TestUnseenAnim` adds a 0.3 s-pass case.

**Not built.** (a) Root-motion sub-steps (`0x571D20` / `0x571DDC`, 1/30 s each): pass `aaPassUs` ~59 ms/s vs ~18 ms/s in the timed `0x571C3C` calls, so ~41 ms/s sits in the time steps and sub-step loops; at > 250 ms per pass that is >= 8 sub-step poses per moving actor. Decimating them (fewer, larger sub-steps for unseen moving Sims) changes root-motion integration and needs the loop's disassembly read first (I-418-3). Read 2026-11-02 in docs/228 (loop, cost split, coarse-step design and probe plan; dt per pass is `k` x 1/30, which questions I-418-2). (b) Non-Sim actors (`aaN_2` 14,400/s, ~7 ms/s): owners unknown (doors, fountains, cars?); built in §3.6 with its per-owner histogram.


#### 3.5.1 Render-message drain crash, 2026-11-02 (triage; ts3native 0.36.163 `unseenrm5`, counters v156)

**Crash.** `bp-utb2-ttl-ab` 07:38 UTC: C0000096 (privileged instruction) at heap eip `0x133169C4`, main thread, `[esp]` = `0x59386B` in `FrameDb_DrainEvents 0x5937B0`. The build was **0.36.159** (`unseenrm2`; the queue pins it), `[UnseenAnim]` Mode 1 Every=4 **CapPasses=0**, Objects off, `[UnseenTable]` Mode 1 [verified, ts3native.log] (I-421-6).

**What the faulting call is [verified static, I-421-1].** Not the dispatch: `0x593869` destroys a message after dispatch, `ecx = [msg+4]` (the payload manager), `call [[ecx]](msg+0x10)`. Posters set `+4` to a static .data manager or 0. The faulting manager `0x35B1FAE4` and its "vtable" `0x13312F44` are both heap.

**Does destroy flush pending messages? Does the pose post them?** The pose `0x940430` posts (transform publish `0x958430`, rest `0x940310`), but those messages carry the owner **handle** and manager 0, so they never reach the faulting call and hold no pointer [verified static, I-421-2]. Destroy does not purge queued messages, and with handle payloads it does not need to [inferred, I-421-4]. `[UnseenAnim]` only pays owed time inside EA's own call for an actor in EA's live walk [verified code, I-421-5]. **The top suspect (a deferred pose posting after destroy) is refuted as stated**: a pose message cannot be this message. Left: a poster with a heap manager, or the message chunk was overwritten or written after free (I-421-3). Not called "not ours": the run had our code on, and the next fault dump names the message id.

**Changes (0.36.163).** (a) `[UnseenAnim]` stale debt: owed time not touched for > 2 x Every passes is dropped, not paid (`aaStale`); a new actor list clears the table (`aaSlotReset`); behaviour otherwise unchanged. (b) Crash log: a wild call returning to `0x59386B` / `0x593880` logs the message (0x50 bytes), 32 bytes at its manager and the last 64 `[UnseenAnim]` decisions (skip / owed-run / snap / flush-off / stale-drop / reset with actor, owner handle, owed us, pass). (c) `[CrashForensics]` Light wrote nothing because the dump only took AV and illegal instruction; C0000096 now counts as a wild call too (`XcptWild`, dump, released ring, AutShadow line).

### 3.6 Objects: unseen non-Sim actors (`[UnseenAnim] Objects`, `ObjStats`; 2026-11-02, built, not run)

Rodney 2026-11-02: "also handle non sim objects". bp-ua-ab (on slices): ~17,500 pose asks/s at `0x571C3C`, ~14,400/s non-Sim actors (`aaN_2`, ~7 ms/s, never decimated); the pose pass is ~59 ms/s, ~41 ms/s of it in the root-motion sub-steps outside this call (I-418-3).

**Hook.** The same call rewrite `0x571C3C` -> `AaPoseThunk`; a non-Sim owner goes to `AaObj` (unseenanim.cpp). `AaPassThunk` walks the locomotion agent list once per pass (only when Objects or ObjStats is on) into a 2D grid with UseM cells.

**Histogram first (`ObjStats=1`, no behaviour change, counters v150).** Every non-Sim ask is counted by owner-vtable bucket (`[owner+0]`; the 8 vtables with most asks over the first 65,536 asks, then fixed for the run; bucket 8 = other: `aoBktVt_*`, `aoBktN_*`). One ask in 16 is classed and timed: kind (0 no transform, 1 itself a locomotion agent, 2 parented, 3 in a SACS state machine, 4 plain) x seen / unseen (`aoKindN_<kind>_<unseen>`, `aoKindUs_*`), per bucket (`aoBktS_<bucket>_<unseen>`, `aoBktUs_*`, `aoBktSacs_*`). Multiply sampled counts and us by 16.

**Rule (`Objects=1`, switch `aoMode` +0x56D4, `--ab unseenobj`; off by default).** An unseen non-Sim actor (UnObjClass: table row or the 1 m rule, then also off screen with an ObjRadiusM=5 sphere) runs every ObjEvery-th pass (4, staggered by actor address) with dt summed in the shared slot table (now 4096 slots), the same owed time, CapPasses / HardCapMs cap and NearVisM / household-viewer / no-table holds as unseen Sims. Holds (run now with the owed time, `aoHold_*`):
- parented (carried, slotted);
- in a running SACS state machine: the owner's component `[0x11E90F0]` has a non-empty vector at +8 / +0xC, the list `0x94E9C0` updates in the SACS pass [verified disassembly; meaning inferred I-419-1]. This is the "animating with a Sim" link; a fault reading it holds;
- itself a locomotion agent (car, boat, bicycle; I-419-2), any agent (any Sim, seen or not, or vehicle) within UseM=8 m (2D), or no usable agent list this pass (fault, cap, no list: `aoWalkFail`). Doors, gates and elevators have no native type test we know of; they animate only with a Sim at them, so this distance rule is what keeps portal and route objects exact (I-419-3);
- near a Sim visible last pass, household viewer / no fresh table, slot taken, debt cap, unclassified.
Seen again: runs that pass with the owed time (`aoSnap`); switch off: owed time at the next call (`aoFlushOff`). `aoNear_0/1` count skips with the nearest agent in the 8-16 m window / none there (how much gain sits near the UseM edge).

**What it delays** (as §3.4 for Sims): clip events of a skipped object's non-root-motion tracks (sound, VFX, SACS events), its footprint refresh and transform publish, by at most ObjEvery-1 passes; time is summed, not dropped.

**Self-test** `TestUnseenAnimObjects` (unseenanim-test.h, run from `TestUnseenAnim`): 2 runs in 8 passes with the time kept, snap-back, SACS / agent near / owner is agent / no list / parented holds, ObjRadiusM sphere, switch-off flush, bucket freeze and samples, off = pass-through. Not compiled yet (game queue running).

**Queue** `tools/harness/queues/unseen-anim-objects.json` (not run; Sims exact in every job, `UnseenAnim.Mode=0`): smoke (switch read-back, `aoFrozen` > 0, histogram > 0), `bp-uao-snapback` (repro unseen-snapback, Objects on the whole run), `bp-uao-stuck-on`, 4 in-load A/Bs (BP/IP, both orders, `--ab unseenobj`, ObjStats in both arms). Verdict from the counters per arm: `aaUs_2` and `aaPassUs`, not it/s.

**Before enabling by default:** the histogram must show (a) the unseen plain share (`aoKindN_4_1` x16, `aoKindUs_4`) is worth it (stop under ~1,000 asks/s or ~1 ms/s), (b) the SACS share is not ~100% (else I-419-1's hold blocks everything, or the vector means something else), (c) the vtable buckets separate types (I-419-5), and the correctness and stuck jobs pass.

## 4. As built (ts3fixes, offline)

| Part | What |
|---|---|
| `mods/ts3fixes/src/UnseenThrottleCore.cs` | `EveryN` (per-key one-in-n gate, staggered by key, `Seen` resets, restarts when 4,096 keys), `UnseenMath.Due` (time gate with cap), `UnseenMath.Key`; no game types |
| `mods/ts3fixes/src/UnseenThrottle.cs` | `EnvScoreHook` on `SimUpdate.CalculateSimEnvironmentScore` (private field `mSim` by reflection), `AutonomyUpdateHook` on `Autonomy.Update` (`mLastTime` by reflection, high-LOD path only), `SimUnseen` classifier |
| `Fixes.cs` | `ApplyUnseenThrottle()`; `BroadcasterPulseHook` per-broadcaster rule when `unseenbroadcast` is on; switches; counters `int[156]` |
| `Instantiator.cs` / `build-fixes.ts` | `kUnseenEnvThrottle`, `kUnseenMotiveThrottle`, `kUnseenBroadcastThrottle` (false), `kUnseenThrottleEvery` (4), `kUnseenBroadcastEvery` (16) |
| `mods/ts3agent/.../PatchHandlers.cs` | switches settable at runtime (`patches unseenenv=1`), counters `unseen_*` |
| `tools/selftest/unseen/` | `node tools/selftest/unseen/run.ts` -> `unseen selftest: PASS` |

Counters (registry rows 146-155): 146-148 environment calls classified / unseen / skipped; 149-151 motive updates classified / unseen / skipped; 152-153 broadcaster unseen pulses run / skipped by the per-broadcaster rule (skips also count in 23); 154 unseen motive updates that left hunger, energy, bladder or hygiene at -95 or lower (needs check, compare per run update between sides); 155 gate tables restarted. 146-151 and 154 count only when `unseenprobe` or the matching switch is on.

With all switches off the hooks cost one switch lookup per call (2 high-LOD Sims per tick at most) and call the original.

## 5. Queue (ran 2026-11-01, readout §9): `tools/harness/queues/unseen-script-throttle-ab.json`

| Job | What | Pass / read |
|---|---|---|
| `bp-unseen-share` | BP 10 min, `unseenprobe` only | unseen shares s for env and motives (G-T0). Under ~5% both: skip their A/B |
| `bp-unseen-{unseenenv,unseenmotives,unseenbroadcast}-{offfirst,onfirst}` | BP 10 min, `--ab <switch>`, 2-min slices, probe on | no crash, 0 watchdog lines, stuck Sims not above the off halves; per side it/s and sim ms/s; skip counters > 0 |
| `bp-unseen-crowd-{offfirst,onfirst}` | `rare-big-crowd`, all three together | as above; crowd Sims on screen stay unthrottled |
| `bp-unseen-needs` | all three on, 20 min | stuck Sims within docs/TESTING.md; `unseen_motive_critical` per run update not above the A/B off halves |

Release asi `rel0360` and release ini as `release-validate.json`; install `build/ts3fixes.package` from this branch. `abOnFirst` needs the `--ab-on-first` harness change from the design U/G branches (master's queue.ts ignores the key, so an on-first job would silently run off-first). Timing only on a clean night; daytime runs count for correctness and counters.

## 6. Gates and effort

G-T0 `bp-unseen-share` (1 job). G-T1 `unseenbroadcast` A/B pair: pass = sim ms/s lower on than off by more than the per-run noise, it/s not down > 2%, stuck Sims not up (2 jobs). G-T2 `unseenenv` / `unseenmotives` A/B pairs only if G-T0 shows s >= ~5% (4 jobs). G-T3 crowd pair + needs job (3 jobs). Then default-on for whatever passes, in the release notes as "unseen Sims update less often". Build done (~2 h with the study); runs ~10 jobs x ~15 min; clean night for the timing numbers.

## 7. Open items

- The unseen share s of high-LOD environment and motive calls (G-T0); with a small s, a looser classifier (off screen but within the reach, e.g. visitors indoors on the active lot) would be the next lever, with visible-area risk (I-144-2).
- The per-pulse cost of unseen vs seen broadcasters (I-144-1).
- Behaviour B's per-tick work (`0x847830`) and whether it integrates the path position (I-144-5), and the docs/22 §6 split with an unseen class before any animation throttle (I-144-6).

## 8. 2026-10-22: SV frames >= 300 ms with all three switches on (analysis), catch-up cap

Data: `clean-night-0.2.0` 2026-10-21 `sv-unseen-play-offfirst` / `-onfirst` (`-600-hitches.jsonl`, `-ab.json`, warm-up slice dropped). The v0.1.5 decision draft counts 1 -> 13 frames >= 300 ms over the pair.

**Where the 13 on-side frames go [measured, stage columns of each frame]:** sync route plans 5 (305, 467, 490, 500, 939 ms; one `Route_Plan` / `Route_Replan` each, route = 93-99% of the frame), `+0x918` lock waits inside ThreadedObjects or locomotion 4 (310-391 ms, keys `0x8334CC`, `0x85FE10`, `0x62EF7A`), locomotion with a 103-212 ms wait 3 (306, 314, 427 ms), one TaskObjects frame (301 ms, the daily script burst also seen on off sides). The `script` column is 0.05-0.94 ms in every one. The off side has one (367 ms, lock wait `0x62F782`).

**Catch-up burst hypothesis: not supported.** [verified in source] none of the three switches batches work across Sims: an unseen Sim's motive catch-up (N x dt) runs inside that Sim's own `Autonomy.Update` call, the environment gate and the broadcaster 1-in-16 gate are staggered per key (`EveryN`, selftest `GateCadence`). These calls run in ThreadedObjects, which checks its budget between tasks (docs/36), so K Sims due on the same sim-minute would show as a ThreadedObjects frame without route or lock wait; no such frame >= 300 ms exists on either side [measured]. That one catch-up costs about the same as one normal update is [inferred] (the integrators take dt as a parameter; selftest `Integrators` checks the arithmetic, not the game's cost).

**What remains [inferred, not verified]:** the frames are the routing class (sync plans and `+0x918` waits, docs/134, docs/143): with the switches on, Sims' needs and environment move in steps, so autonomy picks and therefore route plans happen at other times and places, and the seeded runs diverge after the first skipped call. Which switch drives it cannot be told: the three were only measured together. Next: one SV pair per switch (`tools/harness/queues/scriptfix-ab.json`, `sv-unseen{motives,env,broadcast}-*`). No throttle rule was changed for this.

**Built (guard + proof, behind `unseenmotives`, ts3fixes):** unseen motive catch-ups are capped at `UnseenMath.kCatchUpsPerTick` = 4 per sim tick (`TickCap`); one over the cap waits a tick (counted as skipped) unless its gap reached `kHardGapMin` = 45 sim-minutes (under the 60-minute auto-satisfy switch). Counters (registry): [225] most catch-ups in one tick, [226] held a tick by the cap, [227] longest catch-up call µs (agent `unseen_catchup_max_tick`, `unseen_catchup_held`, `unseen_catchup_max_us`). Selftest `tools/selftest/unseen` `CatchUpCap`: 40 Sims due together run 4 per tick, all within 10 ticks, none lost, a forced one runs over the cap. Day job `sv-unseen-verify` (5 min, counters: skips > 0, `unseen_catchup_max_tick <= 4`, `unseen_catchup_max_us < 20000`). Ledger docs/51 §218: I-218-3, I-218-4.

## 9. Readout of the 2026-11-01 A/B queue against G-T0..G-T3 (2026-11-12, existing data, no new run)

Data: `docs/data/results/2026-11-01-queue-unseen-script-throttle-ab.md` (10 rows, build 2ed35a54, ts3native 0.36.149 `rel025a`, BP `Baseline-BP-Spring`, seed 12345, 15-min `--ab` runs with 2-min slices, `unseenprobe` on). Numbers re-derived from `probe-out/profile/unseen-script-throttle-ab-2026-11-01-*`: sim ms/s per arm with `node tools/harness/arm-ms.ts <prefix>` (script + route + animPose + locomotion, warm-up and 15 s after each switch out), it/s and hitches with `hitch-ab.ts`, counters from `<tag>-ab-fixes.jsonl` (last minus first row).

**Validity [measured].** All 10 jobs PASS (exit 0, no crash, save hashes unchanged), but every row is `timing: invalid (noise medium)`: other apps used 12.5-14.5% of the game CPUs. The order effect is larger than any switch effect: in every single-switch pair the arm that ran in the off-first job's "on" slices was slow (it/s 40.7-43.6 vs 46.3-48.9 elsewhere), so on - off flips sign or size between the two orders. Per-run noise in sum ms/s, taken as the gap between the two orders' on - off, is 5-14 ms/s.

**G-T0 share [measured, counters 146-151].** Unseen share of high-LOD calls on BP: environment `unseen_env_unseen / unseen_env_calls` = 24 / 13,500 = **0.18%** (other runs 0.2%), motives `unseen_motive_unseen / unseen_motive_calls` = 960 / 33,573 = **2.9%** (1,104-1,472 of ~35,300 in the other runs). Both below the ~5% G-T2 threshold, so §6 says skip their A/B; the pairs ran anyway and confirm it.

| Switch (gate) | Sum ms/s on - off: off-first / on-first / pair mean | Script ms/s pair (off -> on) | it/s pair mean | Switch active? | Correctness | Verdict |
|---|---|---|---|---|---|---|
| `unseenbroadcast` (G-T1) | +9.0 / -1.3 / **+3.8 (worse)** | 12.2 -> 12.1 | 46.5 -> 45.8 (-1.5%) | yes: 412k-491k unseen pulses skipped, 28k-33k run | stuck 0 / 0; max iteration 212 -> 431 ms pair (2 iterations >= 500 ms in the off-first on arm, < 10 events: no verdict) | **fail**: no gain beyond noise. The script column does not move, so the 1-in-16 rule saves nothing measurable on top of the shared 1-in-4 rule on BP [inferred: the ~4-8 ms/s estimate of §2.1 does not show] |
| `unseenenv` (G-T2) | +16.7 / +2.8 / **+9.7 (worse)** | 11.8 -> 11.2 | 46.7 -> 42.9 (-8%, order effect) | barely: 1-4 calls skipped per 15-min run | stuck 0 / 0 | **fail / not applicable**: 0.18% share, the switch is close to a no-op; the ms/s change is noise |
| `unseenmotives` (G-T2) | +4.5 / +1.7 / **+3.1 (worse)** | 12.5 -> 11.7 | 48.8 -> 44.4 (-9%, order effect) | yes: 418-529 updates skipped of ~1,100-1,260 unseen; catch-up max 2 per tick, max 12.0 ms | stuck 0 / 0; `unseen_motive_critical` 0 | **fail**: script -0.8 ms/s at most, buried in noise |
| crowd, all three (G-T3) | -1.3 / -6.1 / **-3.7 (better, -3%)** | 9.4 -> 9.5 | 215.6 -> 219.6 (+1.8%) | yes (broadcast 97k-151k skipped, motives 127-138) | stuck 1 (off-first) / 2 (on-first) runs, arm not recorded; iterations >= 200 ms 4.0 -> 0.0 per 10 min | **inconclusive**: the gain is in locomotion (-4.5 ms/s), which none of the three switches touches, and the script column is flat; timing invalid |
| needs, all three 20 min (G-T3) | - | - | - | - | PASS, stuck 0, no crash | **pass** (correctness only) |

**Household and selected Sims [verified, code].** `UnseenThrottle.cs` l.74 (`SimUnseen`): `IsSelectable` or active-household Sims are never unseen, so all three switches leave controlled Sims at vanilla; this readout found no correctness problem.

**Recommendation (Rodney decides; no default changed).** Shipping rule: default ON needs a consistent 3-5 ms/s gain plus correctness. None of the three meets it:
- `unseenbroadcast`: **OFF** (keep opt-in). Correct and active, but no ms/s gain in either order; per the one-round park rule, park it.
- `unseenenv`: **OFF**, park: 0.18% unseen share on BP leaves nothing to save (a looser classifier, §7, would be a new design with visible-area risk).
- `unseenmotives`: **OFF**, park: 2.9% share, script saving <= 0.8 ms/s.
- No rerun of these pairs: even a clean night cannot turn a <= 1 ms/s script-column change into the 3-5 ms/s bar. The crowd pair's -3.7 ms/s is not a reason to rerun the bundle, since it sits in a stage the switches do not reach. Later data with these switches in bundles is in `docs/data/results/2026-11-02-queue-throttle-ms.md`; this section covers only the 2026-11-01 queue.
