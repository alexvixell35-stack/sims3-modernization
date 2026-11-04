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

## 5. Queue (not run): `tools/harness/queues/unseen-script-throttle-ab.json`

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
