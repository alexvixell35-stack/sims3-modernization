# 245. Motive decay redesign (audit O14)

Written 2026-11-06. Static analysis over existing run data, the decompiled script DLLs and our own code. No game run. Rodney's relaxation (docs/222 §11 R1): an unseen Sim may behave differently if it does not show on focus or is not visible at all; visible Sims stay the same; safety is not relaxed. This note uses the docs/243 §1 split between behaviour risk and memory-safety risk. Ledger: docs/51 §431.

**2026-11-06 update: `unseenrefresh` dropped for D1 (Rodney).** Refresh gating would be double work: D1 removes the same retry chains, and with them the refresh, so the gate's gain falls to ~0 once D1 runs (§8). Removed unrun: `kUnseenRefresh`, `kUnseenRefreshMin`, selftest `RefreshGate`, repro `unseen-refresh-loop`, queue `unseen-refresh.json`, counter 777. Kept: `unseenprobe`'s gap histogram (778-784), which now prices D1 and shows it working. **O14 closes as no off-thread option.** §6 and §7 below describe the dropped gate and are history; D1 as built is docs/243 §9.

Tags: **[measured]** from the run files named; **[verified]** read in a decompile or our source; **[inferred]** reasoning, still to confirm.

## Verdict

- **No off-thread design for motive decay is worth building.** Unseen Sims make 1.7-3.5% of the updates that reach decay, so an unseen-only worker has a ceiling of ~0.17 ms/s before any cost of its own.
- **Decay is smaller than docs/129 said:** ~4.8 ms/s (95% range 3.2-6.5), not ~7-8. The other ~25 ms/s of `Motives.UpdateMotives` is buff triggers, the update loop and per-motive bookkeeping.
- **Unseen low-LOD Sims never run decay** [verified]. `Motive.Update` sends them to `AutoSatisfyMotive`, a time-of-day curve. EA already gives them "unseen, cheap, exact on focus".
- **The one real unseen motive cost is a different thing:** `RunAutonomy` runs a whole `Autonomy.Update` for a low-LOD Sim before each autonomy search, ~6.4 ms/s (4.5-8.2) [measured]. It is per decision, as docs/243 found for the rest of the unseen bill.
- **Built:** `unseenrefresh`, a sim-thread gate on that refresh, default off. No worker, no new state, nothing saved. `unseenprobe` prices it in one run with no behaviour change. **The gain is unproven:** it depends on how close together one Sim's searches run, and no existing data shows that. Stop rule in §7.

## 1. What it costs on the sim thread

Managed samples `probe-out/profile/<tag>-managed.jsonl`, eight release-like runs, 7,083 samples. Inclusive counts. Scale: share of all samples = share of sim-thread wall time, so 1 sample = 1.13 ms/s in a run, 0.141 pooled. That is docs/95's scale (1% of counted samples = 5.8 ms/s) [inferred there, same here]. The sampler covers 180 s of each run, and `Value ± 2√n` is the Poisson range.

| Run | Samples | `UpdateMotives` | via SimUpdate (high LOD) | via RunAutonomy (low LOD) | decay | buff triggers |
|---|---|---|---|---|---|---|
| BP `v025-slow-diag-2026-11-04-bp-diag-rel025d` | 885 | 20 | 17 | 3 | 5 | 11 |
| IP `sim-budget-v024-2026-10-29-ip-simbudget-v024` | 886 | 28 | 23 | 5 | 7 | 8 |
| SV `sim-budget-v024-2026-10-29-sv-simbudget-v024` | 887 | 23 | 20 | 3 | 2 | 9 |
| BP `v027-release-numbers-2026-11-05-bp-baseline-rel` | 884 | 29 | 26 | 3 | 5 | 10 |
| IP `...-ip-baseline-rel` | 886 | 31 | 24 | 7 | 5 | 10 |
| SV `...-sv-baseline-rel` | 887 | 35 | 27 | 8 | 3 | 15 |
| BP `wake-probe-2026-11-01-bp-wakeprobe` | 884 | 19 | 16 | 3 | 3 | 8 |
| IP `wake-probe-2026-11-01-ip-wakeprobe` | 884 | 27 | 20 | 7 | 4 | 9 |
| **Pooled** | **7,083** | **212** | **173** | **39** | **34** | **80** |

`v024` runs are flagged `timing: invalid` (docs/243 §2): read shares, not digits. No run is a true Baseline.

| Where the pooled ms/s goes [measured] | Samples | ms/s |
|---|---|---|
| Whole `Autonomy.Update` | 286 | 40.4 |
| of it high LOD (`SimUpdate`) | 241 | 34.0 |
| of it low LOD (`RunAutonomy` refresh) | 45 | 6.4 |
| `Motives.UpdateMotives` | 212 | 29.9 |
| `UpdateMotiveWithDecay` (all high LOD, **0 samples on the low-LOD path**) | 34 | 4.8 |
| `AutoSatisfyMotive` (all low LOD) | 8 | 1.1 |
| `UpdateMotiveBuffs` | 80 | 11.3 |
| `SetValue` / `EarlyOutMotiveUpdate` | 10 / 8 | 1.4 / 1.1 |
| Rest of the loop (list copy, `Motive.Update` body, UI lerp) | 72 | 10.2 |

docs/129's ~8 ms/s for decay (43 of 2,962 pooled) came from October builds on a different set of runs. The two ranges only just touch. Whether a build changed it is not known; use 4.8 here.

### 1.1 Unseen share of the decay path [measured]

Counters [149] calls, [150] unseen, [151] skipped, read per job from `<tag>-ab-fixes.jsonl` (`unseen-script-throttle-ab-2026-11-01`, `night-1101`, `throttle-ms-2026-11-02`, 5 BP + 5 IP jobs).

| | calls | unseen | share |
|---|---|---|---|
| Pooled, 10 jobs | 190,531 | 6,623 | **3.5%** (range 1.3-6.9%) |
| Updates that really ran (unseen - skipped) | 187,037 | 3,129 | **1.7%** |

This confirms the `~2%` in the 11-02 park decision. docs/243 §2.2 agrees from another counter: unseen high-LOD passes 0.3-1.2/s, seen 27-45/s. SV has no such counter.

Ceiling for anything unseen-only on this path: decay 4.8 x 3.5% = **~0.17 ms/s** (0.06-0.33); all of `UpdateMotives` ~0.85; the whole high-LOD update ~1.2.

## 2. Why it is not pure

docs/208 §4.1 stands for the writes it lists. Read together with the code around it, one Sim's update is:

| Kind | What |
|---|---|
| **Reads** | motive value and tuning, `CurrentInteraction`, posture chain (`GetPostureMotiveDecayUpdate`, 17 overrides, `Posture.Satisfies`), `BuffManager.HasElement` (buffs the previous motive's trigger may have just changed), traits, skills, occupation, daycare, seasons, weather |
| **Own writes** | `Motive.Decay`, `Delta`, `AutoUpdated`, `MotiveTimeDelta`, UI arrow state, `mUIValue` [verified]; `Motives.EnergyGainRate` lazy fill, `BuffInstance.mTimeoutCount`, work fun delta, `PetBed.SetActor` [docs/208] |
| **Shared writes** | `Curve.m_x` (dead store, docs/208 §4.3), `Posture.mInteractions` and other lazy fills; closure totals `motive-core` 119 field writes, 2 static writes, 1 static collection [verified, `docs/data/offload3-closure.csv`] |
| **RNG** | `RandomUtil.GetFloat` on a Sim's first auto-satisfy after high LOD; the BFF lookup at work [verified] |
| **Callbacks** | `mCustomClass.Invoke` by reflection in `UpdateMotiveBuffs`; `BuffManager.AddElement` / `OnRemoved` and everything they fire; `TriggerSocialWorker`, `PushBreakLeading` for pets; `Motives.MotiveChanged` (HUD); `kSimHeartbeat` and `kDnPSynchronizeEvent` from `Autonomy.Update` [verified] |
| **Yield** | `Motives.UpdateMotives` calls `Simulator.Sleep(0)` when a Sim's loop runs long, so the whole-Sim loop can yield (not listed in docs/208, which drove one motive at a time) [verified]; the two writing postures also reach yields [docs/208] |
| **Native calls** | 20 ICs in `motive-core`, from `World_IsPositionOutsideImpl`, `World_GetMoonPhaseImpl`, `Objects_GetPosition`, `Slots_GetParentIdImpl`, `World_GetRoomIdAtPosImpl`; 15 snapshot class, 2 cut rows, 2 class c, 1 class b [verified, closure CSV] |

The order matters too. Each `Motive.Update` ends in `UpdateMotiveBuffs`, and the next motive's decay reads those buffs (docs/208 §4.2 blocker 2).

## 3. The low-LOD refresh

`Autonomy.RunAutonomy(bool)` calls `Update()` when the Sim is not high LOD; `SimUpdate` is the only other caller [verified]. For a low-LOD Sim every motive takes `AutoSatisfyMotive`, the value for the time of day plus the Sim's offset, so a value does not build up from the last refresh [verified]. The rest of `Autonomy.Update` (buffs, mood, skills, careers, relationships) takes the elapsed time as a parameter.

How often it runs [measured, `g8_*` counters, `golive-unseen*-ab-fixes.jsonl`, 10 jobs]:

- Unseen searches 5-9/s, so one refresh costs ~1 ms (0.7-1.3) [inferred: two run sets].
- Mean gap between searches per unseen Sim: BP 94-135 sim-minutes, IP 68-101. The harness runs ~11 sim-minutes per real second (docs/222 §11.3).
- 68-83% of unseen searches end with no pick.

After a no-pick the Sim idles and `Sim.DoInteraction`'s idle path queues it again: at once if `CanRunAutonomyImmediately`, else after `kAutonomyDelayNormal` [verified]. So the mean hides two groups: Sims busy for hours, and idle Sims searching in a chain. The chain's gap is the number that decides §7, and no run recorded it.

## 4. Does "tick skipping gained ~0" apply?

Partly, and for a different reason.

- docs/243's finding: cost is per decision and per state change, so a time-conserving skip keeps every decision and gains ~0.
- A motive update is per call, and its cost does not grow with the gap, so skipping calls really does save here [inferred: `hoursPassed` is a multiplier]. The high-LOD throttle gained ~0 because almost no calls are unseen (§1.1), not because the work moved.
- The low-LOD refresh **is** per decision. A rate cut in sim time does nothing unless two searches fall inside the window. That is why §7 prices the gate before any A/B.

## 5. Options

Behaviour and memory safety rated apart, as docs/243 §1.

| Option | Memory safety | Behaviour | Measured path to gain | Verdict |
|---|---|---|---|---|
| A. Exact worker on clones (docs/208 §4.2), all Sims | needs the docs/206 window; clones per motive; generation hooks on buffs, traits, interactions | exact if every check holds | decay 4.8 total, minus temperature, energy, fun at work, minus clone cost on the sim thread: under ~2 [inferred] | **no** |
| B. Unseen-only worker, apply on the sim thread | same window; apply stays on the sim thread | relaxed by R1, so the checks can go | ceiling ~0.17 ms/s | **no** |
| C. Lower rate or batches for unseen Sims (`unseenmotives`, docs/144) | safe, sim thread | built, needs fine | ceiling ~0.9 ms/s at N = 4; bundle ~0, parked 11-02 | **no**, stays parked |
| D. Snap-back on focus | nothing to do | EA's own: the Sim goes high LOD and `SimUpdate` integrates the gap | no gain by itself | not an option on its own |
| E. Worker computes `AutoSatisfyMotive` for the refresh | shared curve, RNG on first use; applies on the sim thread | exact | ceiling 1.1 ms/s | **no** |
| F. Gate the refresh on the sim thread (`unseenrefresh`) | safe: no thread, no container, no new state (§6) | stale needs up to 30 sim-minutes for one search, unseen only | up to ~6.4 ms/s x the share of refreshes in the window; share unknown | **dropped for D1, 2026-11-06** |
| G. Fewer searches for unseen Sims (docs/243 D1) | safe, sim thread | Sims start things later | saves the search too (~2.4-3.3 ms vs ~1 here) | **built instead** (docs/243 §9) |
| H. Exact fixes in place, all Sims | safe | exact | see below | not built |

**H, in place.** This is the only route for the 96-98% of the bill that is visible Sims, and it needs no relaxation.

- **Correction to docs/95 §5.5:** of 29 `RemoveElement` samples under `UpdateMotiveBuffs`, **14 are real removals in `OnRemoved`**, which an early-out cannot touch. Only ~15 (2.1 ms/s) are the absent-buff path, with `ContainsKey` 9 and the delayed list 3.
- A hook needs the private `mBuffCountList` and `mDelayedBuffs`, and ts3fixes reads privates by `FieldInfo`. At thousands of calls per second that can cost more than the ~0.5 µs per call it saves [inferred, call rate not counted]. Not built.
- `BuffManager.IsPermaMoodlet` is 15 samples (2.1 ms/s), a public method that reads one static table. A small direct-mapped memo is exact and could save ~1.5 ms/s [inferred], ~30 min of build. It is under run noise and is not decay, so it is listed, not built.
- The compile lane already holds `Motive.Update`, `UpdateMotiveWithDecay`, `UpdateMotiveBuffs` and `EarlyOutMotiveUpdate` as candidates (`docs/data/compile-candidates.csv`, class Y, the yield path). That is the better answer for the visible share.

**Existing fixes checked.** Web search: no mod targets this cost; NRaas Relativity scales decay tuning and StoryProgression sets static motives, both for play style [community-reported]. NRaas source grep: nothing calls `Autonomy.Update`; `NRaasRelativity TuningAlterations` reads the same tuning, unaffected; `NRaasDebugEnabler RegisterCommands` zeroes `mLastTime` when it is ahead of the clock, which the gate passes to EA's code.

## 6. As built

ts3fixes `UnseenThrottle.cs` `LowLodRefresh`, in the `Autonomy.Update` hook that already exists; pure logic in `UnseenThrottleCore.cs`. Switch `unseenrefresh` (`kUnseenRefresh`, off), `kUnseenRefreshMin` 30.

- An unseen Sim (`SimUnseen`) on the low-LOD path with 1 <= gap < 30 sim-minutes skips the refresh; the search still runs on the last values.
- Under 1 sim-minute goes to EA's code, which returns at once. A seen Sim, and every high-LOD call, is unchanged.
- `mLastTime` is untouched, so the next refresh integrates the whole gap.

| Safety rule | Why it holds |
|---|---|
| No data race | runs on the sim thread inside the existing hook. The `[AutShadow]` sources have no `Autonomy.Update` call [verified, grep] |
| No save corruption | writes nothing to game state; a skip leaves an EA field at a value EA itself leaves |
| Vanilla-compatible, no mod data | no field, no sidecar, no static per-Sim table |
| Removing the mod | nothing to undo |

Visible Sims: a Sim that comes into view goes high LOD inside the 60-tick LOD check, and `SimUpdate` runs EA's update over the gap. Under 60 sim-minutes that is decay, not auto-satisfy [verified]. On focus its values were last set up to 30 sim-minutes ago on a slow curve; no pop beyond what EA's own update shows [inferred].

Counters [777] skipped, [778] run, [779] µs in those runs, [780-784] gap histogram (< 1, 1-5, 5-15, 15-30, >= 30 sim-minutes). Agent names `unseen_refresh_*`. The histogram fills with `unseenprobe` alone.

Selftest `tools/selftest/unseen` `RefreshGate`: window edges, cap, off at 0, bucket edges, no sim time lost over a sim day, staleness bound. Repro `unseen-refresh-loop` (STRESS: spawned Sims stand in for townies). Queue `tools/harness/queues/unseen-refresh.json`. Build and `selftest-all` pass (261 compile, 198 PASS, 0 FAIL). **Never run in game.**

## 7. Game run and stop rule

1. `bp-urf-probe`, `ip-urf-probe` (10 min each, `unseenprobe`, no behaviour change). Saved ms/s = (`gap_lt5` + `gap_lt15` + `gap_lt30`) x `unseen_refresh_us` / `unseen_refresh_run` / 600 s / 1000.
2. **Under ~2 ms/s on both worlds: stop, park the switch, close O14.** That is under ~30% of refreshes in the window.
3. Else `bp-urf-loop`, then `bp-urf-ab` and `ip-urf-ab` for correctness: routes/s, needs, stuck per arm. The ~2-5 ms/s gain sits under the ±10 ms/s run noise, so the counters judge the gain and the A/B judges side effects.

Effort: built, ~1 h with the study. Game: probes ~25 min, loop + A/Bs ~1 h if step 3 is reached. No clean night needed for the probes (counters, not timing).

## 8. Risks and open points

- **Skip share unknown** (I-431-3). The harness undercounts it: at ultra, 30 sim-minutes is ~2.7 real seconds. At normal speed the window covers more retries, but ms/s is then lower too [inferred].
- **D1 overlap.** If D1 ships it removes exactly the retry chains this gate skips; the gate's gain could fall to ~0. Measure again after D1, or keep only one.
- **Route side effect.** The docs/144 bundle raised routes/s 10-14% and no switch was blamed alone. Stale needs change picks, and one extra route costs more than many skipped refreshes. The A/B checks this, not the probe.
- **Needs under stress.** Stale values only last one window, so a Sim cannot starve by this path [inferred]; the `aut-unseen` needs bars (docs/222 §11.3) stay the check.
- `sRefreshWatch` is shared. If `UpdateMotives` yields inside a timed call, that one µs figure is wrong; rare, and it only skews the mean.
- `~1 ms` per refresh and the "safe on focus" claim are inferred until the probe and repro run.
