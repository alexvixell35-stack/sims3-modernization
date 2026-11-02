# 211. Unseen wake-ups: probe, parked SimUpdate passes, parked exit-reason polls

docs/209 #94 (contributor research) found that EA's LOD limits what an unseen Sim computes, not how often its script tasks wake. This note builds its actions 1 and 2: a no-skip probe of those wake-ups (`wakeprobe`) and two switches that cut them for unseen Sims (`wakepark`, `wakepoll`). All three are ts3fixes only (no native change), default off, built and selftested offline, **not run in game**.

Tags: **[verified]** = checked in the ILSpy decompile of the Steam 1.67 script DLLs, our own docs that closed the same question, or our code and selftest; **[inferred]** = reasoning or estimate, for the probe or the A/B to confirm.

## Summary

| Switch | What | Default | Visible Sims | Expected [inferred] |
|---|---|---|---|---|
| `wakeprobe` | per second, by class (seen / unseen by the docs/202 rule x EA's cached LOD): SimUpdate passes (1 in 16 timed), exit-reason polls and waits, idle passes, `Lot.UpdateReactions` calls (1 in 16 timed, lots seen / unseen) | off (test switch) | counted only | probe cost ~1-4 ms/s (measured by the `--ab wakeprobe` pair) |
| `wakepark` | an unseen low-LOD Sim's SimUpdate task sleeps 12 ticks between passes instead of 1 | off (`kWakePark`) | unchanged | BP ~3-12 ms/s, IP ~2-10 ms/s |
| `wakepoll` | an unseen Sim's `WaitForExitReason` sleeps up to 30 ticks per poll, never past its deadline, woken by `AddExitReason` | off (`kWakePoll`) | unchanged | ~0.5-3 ms/s per world |

The hooks are patched only when `kWakeHooks`, `kWakePark` or `kWakePoll` is on at startup: two of them add a managed call on every SimUpdate pass of every Sim. Test runs use `build-fixes.ts --on kWakeHooks` (package `game-copy/ts3fixes.package.wakehooks`). ts3fixes counters **491-533** (registry ids `fixes-491`, `fixes-530`), ledger §405. Queues `tools/harness/queues/wake-probe.json` (smoke job + BP + IP + cost pair) and `wake-park-ab.json` (smoke, 2 correctness repros, 8 A/B jobs); report `tools/harness/wake-report.ts`; selftest `tools/selftest/wakepark`.

## 1. What wakes every tick (code reading)

All [verified] in the decompile unless tagged.

1. **SimUpdate** (`Sims3.Gameplay.Actors.SimUpdate`, one task per instantiated Sim, `[Persistable(false)]`). Each pass: three ESRB flag tests, the elapsed sim-minutes since the last pass (kept in the task, so a longer gap is integrated, not lost), the per-tick high-LOD bookkeeping, then:
   - **high LOD** (`Autonomy.IsRunningHighLODSimulation`, EA's verdict cached 60 ticks) and under the 2-per-tick limit: the full update (curfew, hair, `Autonomy.Update`, look-ats, body shape, environment score), then a sleep of 5+ ticks;
   - **high LOD over the limit, or low LOD**: the sleep stays 0, i.e. the task wakes **every tick**. The low-LOD branch only hides a Sim standing indoors on a residential lot that is not the active household's (opacity 0, `WasHiddenForLowAutonomy`).
   - Then `InteractionQueue.UpdateTimer(elapsed)` and the active-Sim mood/motive lerp loop (active Sim only).
   `UpdateTimer` has exactly one caller (this pass, docs/209) and is **a no-op in 1.67**: it acts only while the queue's timer flag is set, and the only script writer of that flag sets it false (`DeQueue`); no NRaas source touches it. So a low-LOD pass has no gameplay effect except the indoor hide and the LOD re-check (I-405-1).
2. **Exit-reason poll** (`Sim.WaitForExitReason(minutes, accepted)`): check the reason, `Sleep(1)`, read `SimClock.CurrentTicks`, until the deadline; returns false at the first tick at or past it. 102 call sites, mostly looping interactions (`while (!WaitForExitReason(...) && !condition)`). The Sim's reason field (`mExitReason`, protected) is written only by `AddExitReason` (sets bits, fires `OnExitReasonsAdded`), `RemoveExitReason` and `ClearExitReasons`; the last two only clear bits, so `AddExitReason` is the only way a wait can end early (I-405-2). NRaas source only reads the field (MasterController test code).
3. **Idle loop** (`Sim.Simulate` -> `DoInteraction` with an empty queue): idle animation, autonomy queue entry, then `Sleep(2)` / `Sleep(10)` (in the autonomy queue) / `Sleep(20)` (autonomy off) (tunables `kSimLoopSleepTicksWhen*`).
4. **Lots** (`Lot.Simulate`, one task per lot): every tick the lot's alarm manager, door privacy and detail priority; `UpdateReactions(elapsed, full)` when the lot simulates (commercial lots always, residential when active) or while broadcasters hold Sims; inactive lots sleep 30 ticks.
5. **Clock**: `SimClock.CurrentTicks` and `Simulator.TicksElapsed` come from the same native store per `Simulate` (docs/119 §1.2, I-119-8), so a sleep of n ticks moves the clock n ticks, except `SimClock.TicksAdvanced` jumps (time skips), which only make deadlines arrive sooner.
6. **Waking a sleeping task**: EA's own code does it for tasks in finite and infinite sleeps (`Animation.PMAAnimationFinishedCallback`, `EventQueue`, `LoadSaveManagerTask`, `FinalizerTask`) with `Simulator.Wake(id, assertIfNotSleeping: false)`.

None of our built throttles (`unseenmotives`, `unseenenv`, `unseenbroadcast`, `[UnseenThrottle]`) touches these wake-ups (docs/209).

## 2. Probe (`wakeprobe`, docs/209 action 1)

Hooks (`mods/ts3fixes/src/WakePark.cs`):

| Hook | Counts | Timed |
|---|---|---|
| `InteractionQueue.UpdateTimer` + `Sim.get_IsActiveSim` | end of a SimUpdate pass: the timer hook sets a flag, the next managed call in the pass is `mSim.IsActiveSim` (nothing runs between), which gives the Sim | 1 in 16 passes per class, from the pass's LOD check (`Autonomy.get_IsRunningHighLODSimulation`, armed for that Sim's own SimUpdate task) to its end |
| `Sim.WaitForExitReason` | waits seen / unseen, polls by class | no (a poll is one wake plus a flag test) |
| `Sim.DoInteraction` | idle passes (empty queue) by class | no (the pass ends in its sleep) |
| `Lot.UpdateReactions` | calls by full / broadcasters-only x lot seen / unseen (`UfLotSeen`, cached 60 ticks) | 1 in 16 by count |

Class = `UfWhy` (docs/202 rule: live mode, not selectable or active household, not on the active or camera lot, not on screen, out of the camera query and 80 m from the household, not held) x EA's cached LOD (`Autonomy.GetCachedHighLODValue`). The verdict is cached per Sim in a direct-mapped table (1024 slots, no Dictionary): seen verdicts 60 ticks (EA's own LOD cache), unseen ones one park length. A timed call whose clock moved during the call yielded and is dropped (`wake_yielded`). Without ts3native's camera query (0.36.89+) every Sim is seen and nothing parks.

Not timed here: the wake itself (native scheduler + interpreter resume). `sim-budget-report.ts` gives the managed side by owner (SimUpdate, interaction step); the A/B gives the real gain. `wake-report.ts --wake-us N` prices each removable wake for the estimate line (default 3 us [inferred]).

**Cost.** Probe on: one cache lookup and counter per pass, `UfWhy` once per 60 ticks per seen Sim, two stopwatch reads per timed call: ~1-4 ms/s [inferred], measured by `bp-wakeprobe-cost` (+ on-first). Hooks present but off (kWakeHooks package): two extra calls per pass and the switch test every 256 calls, ~1-3 ms/s [inferred]; the same in both arms of every docs/211 A/B and absent from players' installs (hooks not patched with the tunings off).

## 3. `wakepark`: unseen low-LOD SimUpdate passes

At the end of a pass (inside the SimUpdate task, after `UpdateTimer`): when the Sim is unseen (verdict at most one park old), low LOD (EA's verdict of this pass), not the active Sim, no save in progress and the switch is on, the hook sleeps `kWakeParkTicks - 1` = 11 ticks; the game's own `Sleep(0)` adds the 12th. The next pass reads the whole elapsed time as it always does.

Why the decompile supports it (§1.1): the pass of a low-LOD Sim changes nothing but the indoor hide and the LOD re-check; time is integrated from the clock, not from the pass count; the task is not saved.

| Concern | Handling |
|---|---|
| Visible Sims | never parked; their passes run the game's path (one cache lookup added) |
| Sim turns visible | the verdict is re-asked at every wake (unseen verdicts expire after one park), so a parked Sim is late by at most one park (12 ticks, ~0.2-0.4 s) plus EA's own 60-tick LOD cache; a view change (camera target > 20 m in one tick, active household, selected Sim, leaving live mode) wakes every parked task at once and drops the cache (`WpView`) |
| Interaction push | not needed: `UpdateTimer` is dead (§1.1) and the queue runs in the Sim's own task, not in SimUpdate |
| Motive threshold | not needed: a low-LOD pass does no motive work (`Autonomy.Update` is in the high-LOD branch; low-LOD motives are refreshed by `RunAutonomy`, docs/144) |
| Save | SimUpdate is `[Persistable(false)]` and the save never waits on it; parks are refused while `SaveGameInProgress` anyway (`wake_park_refused`) |
| Pause | no ticks pass: a parked task stays asleep exactly as the game's own sleeps do |
| Travel / world quit / reset | tasks are torn down (reset throws through the sleep; `try/finally` drops the entry); tables cleared at world quit and load; entries of tasks destroyed while asleep have no proxy (`Simulator.GetProxy` checked before every wake) and are pruned after 4 park lengths |
| Order | wake order changes (seeded divergence): judged alone in its own A/B pair |

Effects that become late by up to one park for unseen Sims [inferred]: the indoor hide (fade) and the high-LOD flip of a Sim the active Sim walks up to without a camera move. Removed wakes: 11 of 12 passes of every unseen low-LOD Sim.

## 4. `wakepoll`: unseen exit-reason polls

The hook runs the game's loop itself (check, sleep, clock, until the deadline) and, for an unseen Sim with the switch on, sleeps `min(30, ticks to the deadline)` per poll after registering (Sim -> task, accepted reasons) in `WpWaiters`; `AddExitReason` on that Sim with an accepted bit wakes the task. Seen Sims and the probe-only case sleep 1 tick as the game does.

- **Same result on the same tick** when the wake arrives: the selftest runs the game's loop and ours on a tick model for every deadline 0-70 and reason tick 0-75 at k = 1, 2, 5, 30, 61 (equal result and return tick) [verified, selftest]. In game, the woken task resumes on the next scheduling pass, as `Sleep(1)` would [inferred, I-405-3].
- **Missed wake** (a writer that bypasses `AddExitReason`; none in EA or NRaas source, §1.2): the reason is seen up to k-1 ticks late, or missed when it lands in the last poll before the deadline (the wait returns false where the game would return true); the selftest bounds both.
- Deadline cap: never past the deadline (1:1 clock, §1.5); a `TicksAdvanced` jump ends the wait at the next wake, up to 30 ticks later than the game (`wake_poll_over` shows the worst).
- One waiter per Sim; a second concurrent wait on the same Sim polls every tick (`wake_poll_shared`).
- Pause, save, travel, reset: as §3 (the loop is the interaction task's; the save never waits on it; a reset throws through the sleep and the `finally` drops the entry).

## 5. Tests

| What | Where | Status |
|---|---|---|
| Poll equivalence, park length, cache ageing, waiter table, view-change rule | `node tools/selftest/wakepark/run.ts` | passes (2026-10-30); ts3fixes and ts3agent compile against the 1.67 DLLs |
| Report math | `node tools/harness/wake-report.ts --self-check` | passes |
| Forced unseen walkers + camera jumps + pause + save + off half | repro `wake-park-loop` | built, not run |
| Travel out and back with all switches on | repro `wake-park-travel` | built, not run |
| Wake-up rates per class, timed cost, probe cost | `wake-probe.json` (smoke job, `bp-wakeprobe`, `ip-wakeprobe`, `bp-wakeprobe-cost` pair) | not run |
| Gain per switch, per world, both orders | `wake-park-ab.json` (smoke, 2 repro jobs, 8 A/B jobs) | not run; only rows the probe prices at >= ~2 ms/s |

## 6. Estimates [inferred]

- SimUpdate inclusive is ~8% of the sim thread (docs/28 S4), mostly the high-LOD work (environment score ~14-18 ms/s, motives ~4%); low-LOD passes are a small managed body (~1-2 us) plus the wake. With ~40-100 unseen low-LOD Sims at 40-60 it/s, 1,600-6,000 wakes/s, 11 of 12 removed at ~1.5-4 us per wake: **BP ~3-12 ms/s, IP ~2-10 ms/s** (docs/209's contributor estimate 5-20, ceiling ~30).
- Exit-reason polls: ~10-30 unseen Sims in looping interactions, 600-1,800 polls/s, most removed: **~0.5-3 ms/s**.
- Idle loop and `UpdateReactions`: measured by the probe only; docs/209 puts them at ~1-5 and a ceiling of ~18 ms/s; build only if the probe prices them >= ~2 ms/s.

## 7. Risks

- Hook frames: `IsActiveSim` is hooked for every caller (extra call); `WaitForExitReason` runs our loop for every caller while `wakeprobe` or `wakepoll` is on (same semantics, one frame per poll more for seen Sims). Mono stacks gain frames in these paths (docs/51 §386, issue #92 class): stack-reading tools see `WpSimHook` frames.
- A mod that patches the same methods (none known; `tools/hook-sites.ts` covers native only) would be swapped out by ours or the reverse; the applied log names each patch.
- `[AutShadow]`'s closure guard accepts our own swaps (`sPatched`). Its worker may call `IsActiveSim` / `IsRunningHighLODSimulation` while scoring: both hooks test `OnWorker()` first and then only run the game's getter (the pass-end flag is left for the sim thread).

## 8. Run

```
node tools/setup/build-fixes.ts --on kWakeHooks
copy build\ts3fixes-on-kWakeHooks.package game-copy\ts3fixes.package.wakehooks
node tools/harness/queue.ts tools/harness/queues/wake-probe.json
node tools/harness/wake-report.ts wake-probe-<date>
node tools/harness/queue.ts tools/harness/queues/wake-park-ab.json   (after the probe; drop pairs whose target is < ~2 ms/s)
```
