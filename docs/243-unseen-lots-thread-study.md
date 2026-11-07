# 243. Unseen lots on another thread: design study

Written 2026-11-06. Design study only: existing run data, the decompiled script DLLs, the call graph index, docs. No build, no game run. Rodney's idea: unseen lots run on another thread and sync at tick end; behaviour may be slightly wrong while unseen, Sims reset to correct behaviour when seen.

Tags: **[verified]** read in a decompile, disassembly or doc named; **[measured]** from run files named; **[inferred]** reasoning or a sum over measured shares, still to confirm. Inference ids I-243-n are local to this doc (no docs/51 § yet). Every gain below is an estimate.

## Verdict

- **The relaxation is right, the thread is not.** "Wrong while unseen, exact when seen" already works: docs/222 runs on it (unseen-direct go-live, BP +6.6 / IP +11.2 ms/s with pacing). It covers wrong behaviour only. It does nothing for data races.
- **Option A (unseen tasks on a Mono worker) cannot be made safe.** The script layer has no locks, unseen and seen Sims share the same containers, 58-88% of Sim-task samples sit in a non-SAFE IC, native code calls C# synchronously inside the tick, and the task closure yields. Even if it worked, its ceiling is ~23-60 ms/s before any sync cost (§3).
- **Tick skipping (B) is measured dead.** Five script throttles, whole-update decimation and root-motion Mode 2 all came back ~0. The cost is per decision and per state change, not per tick; time-conserving skips keep every decision.
- **Do this instead: D1, "park at the boundary".** No thread and no frozen tasks: an unseen Sim that has finished its interaction waits longer before its next autonomy search. That removes decisions, and with them the routes, interaction steps and animation they cause. Estimate ~10-90 ms/s per world, central guess 20-40 [inferred]. Proof: one ts3fixes switch, about 1 h of build plus ~1.5 h of game runs (§7).
- **C (snapshot offload) stays the track for crowds in view,** where anything unseen-only gives nothing (`offload-goal` note 10-30).

## 1. Two kinds of risk

| | Wrong behaviour | Memory safety |
|---|---|---|
| Examples | stale pick, late motive, Sim in the wrong place, skipped reaction | container resized during a read, torn native object, GC missing a root, half-written save graph |
| What it costs | unseen oddity, gone at snap-back | exception, hang, native crash, corrupt save |
| Does snap-back help? | **yes** | **no**: the damage is to shared state, not to the Sim's behaviour; a reset runs on top of the corrupted container |
| Detectable? | by a live `Test` or a compare (docs/222 D2 hook) | not reliably: a race has no signature until it crashes elsewhere |

### What must be true

**For wrong behaviour:** the Sim is valid at every moment it can be seen; the seen test is cheap and leaves before any visible frame; no outcome that lasts is decided from stale data without a live check. The first two are built (`UfWhy`, unseen table). The third is what docs/222's one live `Test` does.

**For memory safety, all of these at once:**

1. Every managed object the worker writes has no other reader or writer in that window, and every object it reads has no writer.
2. Every native call is either safe off-thread or proxied, and proxying a call that returns a value blocks the worker.
3. GC sees the worker's stack and registers (done: docs/30 P1-P2, `[MonoThread]`).
4. Allocation and collection are locked (done: docs/30 P3, docs/57).
5. Interpreter globals are per thread (done: docs/30 P4).
6. A yielded task can resume on the worker and be saved from there (**not done**: `[AutShadow]` runs a driver with no yield, docs/122).
7. Saves, resets, object destruction and world travel drain the worker first.

Items 3-5 are genuine progress: the runtime can host a second thread, and `golive-dfn` ran it with 0 crashes [measured, PLAN]. Items 1, 2 and 6 are what `[AutShadow]` avoids by working on a snapshot in a quiescent window, with an allow-list of 13 definitions (docs/122). Option A has to meet them for the whole game.

### Shared structures unseen-lot scripts touch

Counts are regex counts over the decompiled `Sims3GameplaySystems`, `Sims3GameplayObjects`, `Sims3StoreObjects`, plus SimIFace, ScriptCore and UI for the first row [verified, this note]. Closures: `managed-callgraph.ts query --ics --delegates --depth 14`.

| Structure | How unseen work hits it | Evidence | Race result |
|---|---|---|---|
| Whole script layer | no `lock`, `Monitor` or `Interlocked` in 9,623 files; 885 `Simulator.AddObject` sites, 794 `Sleep` sites | [verified]; agrees with docs/128 | nothing is thread-safe by design |
| `Sim` task | `ProcessOneInteraction` closure 6,888 methods: 242 PROXY + 17 UNSAFE + 3 SAFE ICs, worst class YIELD (9 yield ICs); `Sim.Simulate` 6,325 methods, 177 + 19 + 2 | [verified] | worker cannot run it unchanged |
| `Lot` | `Lot.Simulate` runs the lot's own alarm manager each wake, then reactions; closure 1,353 methods, 33 PROXY + 12 UNSAFE + 2 SAFE, incl. `World_ObjectEnableFootprint`; 4,076 `.LotCurrent` reads | [verified] | a Sim walking between a seen and an unseen lot crosses threads mid-route |
| World / spatial queries | `Query_GetObjects*`, `Slots_GetParentId`, `World_CanSeeObject` are the top ICs | [measured, §2] | native lists change while read; PROXY |
| Relationships | `Relationship.Get` 726 sites in 258 files; a pair can be one seen and one unseen Sim | [verified] | shared table, no owner thread |
| Services, roles | `Services.Simulate`, `RoleManagerTask` tasks pick Sims across lots | [verified, samples] | sim-thread tasks touch "unseen" Sims |
| `Simulator` task lists | `SleepImpl`, `GetCurrentTaskImpl`, `AddObjectImpl`, `CreateObjectImpl`, `SetYieldingDisabledImpl` UNSAFE; `WakeImpl`, `DestroyObjectImpl` PROXY | [verified, `ic-safety.csv`] | the scheduler has one owner |
| Native ICs | 1,587 PROXY, 83 UNSAFE, 82 SAFE, 6 MAIN of 1,758 | [verified, `ic-safety.csv`] | only ~5% callable |
| Autonomy manager | one task, `LinkedList` queues, `mReactions`, one search per wake, static timer and check tables | [verified] | seen and unseen Sims share the queue |
| Event tracker | `EventTracker.SendEvent` 2,267 sites in 646 files; listeners run in the sender's call | [verified] | an unseen Sim's event runs wish, opportunity and mod listeners on the worker |
| Alarms | 356 `AlarmManager.Global.AddAlarm` sites; per-lot managers; Sim alarms (career) | [verified] | alarm callbacks write Sims from the sim thread |
| RNG | shared script RNG (docs/27); `[AutShadow]` aborts on it | [verified] | torn state, draw order changes |
| Native to managed callbacks | SACS events and route events 57/58 enter C# synchronously inside the tick; route events return a bool native code uses (docs/206 option 1) | [verified] | **"sync at tick end" does not exist as a boundary**: these cannot be deferred |

**The race result is not "slightly wrong".** `List` and `Dictionary` in mscorlib have no thread guards: a concurrent resize gives exceptions or a broken bucket chain, which can loop forever [inferred from the container design; Mono 1.2.3 corlib]. Native objects give a crash. Crash S (docs/202, memory `unseen-sim-fallback`) came from one bad write path we owned and was only found because it reproduced at 6-8 min every run; a race across 6,888 methods would not reproduce like that.

## 2. How much work moves

Method: `sim-budget-report.ts` `analyze()` plus `stall-split.ts category()` over existing runs. No run records the Sim class per sample, so seen/unseen is **not measured per sample**; it comes from class counters, one per bucket. `simstack` cannot split this (73-83% of stacks capped, docs/28 §0.3), so the split rests on managed samples.

Inputs: release-like builds BP `v025-slow-diag-2026-11-04-bp-diag-rel025d`, IP/SV `sim-budget-v024-2026-10-29-*` (BP v024 crashed, so rel025d stands in); switches-off arm `v027-release-numbers-2026-11-05-*-baseline-rel` (not a true Baseline, see PLAN). The v024 runs are flagged `timing: invalid` (noise, heavy samplers): read shares, not digits.

### 2.1 Budget by owner, ms/s of the sim thread [measured timers, split by samples]

| Bucket | BP rel | IP rel | SV rel | BP off | IP off | SV off | Unseen share used |
|---|---|---|---|---|---|---|---|
| Sim interaction step | 197 | 137 | 157 | 139 | 96 | 132 | 0.6-0.9 |
| Native event callbacks | 44 | 29 | 35 | 42 | 35 | 29 | 0.6-0.9 |
| Lot.Simulate, reactions | 83 | 90 | 74 | 134 | 87 | 67 | 0.45-0.8 |
| SimUpdate | 75 | 75 | 80 | 91 | 66 | 88 | 0.05-0.5 |
| Autonomy search | 58 | 55 | 59 | 53 | 65 | 120 | 0.77-0.88 |
| Route sync plans | 39 | 85 | 11 | 124 | 272 | 86 | BP 0.58-0.87, IP 0.65-0.68, SV 0.34-0.97 |
| Anim, locomotion, publish | 129 | 95 | 86 | 112 | 84 | 92 | 0.4-0.75 |
| Alarms, GC, other tasks, untimed | rest | rest | rest | rest | rest | rest | 0 |
| **Unseen upper bound (ms/s)** | **305-503** | **279-435** | **237-400** | 330-553 | 382-527 | 289-500 | |

**This is a ceiling, not a gain** [inferred]: count shares stand in for ms shares, and an unseen wake is cheaper than a seen one. "Other" (158-270 ms/s) does not move, and much of it scales with iterations/s.

### 2.2 Where the shares come from

| Share | Value | Source |
|---|---|---|
| Autonomy searches by unseen actors | 77-88% | PLAN results 11-01 [measured] |
| Route plans >= 300 ms, unseen ms (class-known / floor) | BP 86.6 / 57.7%, IP 68.4 / 65.4%, SV 97.3 / 34.0% | docs/202 §3.8.1 [measured] |
| Locomotion agent ms unseen | BP 88%, BP crowd 91%, IP 99.5% | `unseen-split-2026-10-29-*-native.json`, `unAgentUs` [measured] |
| Exit-reason polls unseen | BP 606-655/s vs 88-146, IP 249-275 vs 43-45 (81-86%) | `wake-park-ab-2026-11-01` off slices [measured] |
| Exit-reason waits unseen | BP 159-173/s vs 22-37, IP 61-68 vs 11 | same [measured] |
| `UpdateReactions` unseen | BP 1,324-1,333/s at 21-22 us = 28-29 ms/s vs seen 7-10; IP 2,465-2,541/s at ~7 us = 18 ms/s vs seen 0.2-22 (unstable) | same [measured] |
| SimUpdate passes | unseen high LOD 0.3-1.2/s, seen high LOD 27-45/s; unseen low LOD 2,374-2,881/s at ~16 us | same [measured] |

**Three findings that shape the options:**

1. **EA already keeps the heavy per-Sim script for seen Sims.** High-LOD passes cost 1.1-1.5 ms; unseen Sims get ~1 per second. Only ~16 us passes remain, and removing 11/12 of them (`wakepark`) moved -2.0 BP / -5.0 IP ms/s (memory `throttles-park-rule`), so the timed ~37-45 ms/s overstates what can leave [inferred].
2. **EA already has an inactive-lot rule.** `Lot.ShouldSimulate` is false for a residential lot that is not active; such a lot only runs reactions while Sims are in broadcasters, else it sleeps 30 ticks. Commercial lots always simulate [verified]. The easy suspension is already taken.
3. **Unseen cost sits in Sims doing things**: interaction steps, the plans and SACS calls they make, and the searches that start them.

### 2.3 What is in the interaction step

Leaf of `Sim.Simulate` samples, by `ic-safety.csv` verdict [measured samples; short-name match, so unmatched ICs count as managed and the non-SAFE share is a floor]:

| Run | Samples | Managed leaf | PROXY | UNSAFE | Non-SAFE |
|---|---|---|---|---|---|
| BP rel | 196 | 41% | 52% | 8% | 59% |
| IP rel | 212 | 22% | 68% | 10% | 78% |
| SV rel | 156 | 42% | 55% | 3% | 58% |
| BP off | 230 | 32% | 65% | 3% | 68% |
| IP off | 361 | 12% | 86% | 2% | 88% |
| SV off | 194 | 35% | 61% | 4% | 65% |

## 3. Why A cannot be safe

1. **Only 12-42% of the work is managed.** The rest is in ICs that must run on the sim thread. Each one with a return value makes the worker wait. If sync is at tick end, a task making N such calls takes N ticks; if the sim thread answers at once, it must stop to do so and the work has not moved.
2. **Ceiling.** Interaction step x 0.75 unseen x managed leaf: BP ~61, IP ~23, SV ~49 ms/s moved, before snapshots, guards and rendezvous [inferred]. Calibration: `[AutShadow]` guard thunks alone cost 1.5-2.1 ms/s, and exact go-live netted ~0 on 13 read-only definitions after the whole M0-M6 track (docs/118, docs/222).
3. **The way out is a global lock, which removes the parallelism.** With no locks in the script layer, the only safe rule for shared objects is "one thread at a time". Lock-stepping worker and sim gives the same wall time plus switching cost.
4. **"Unseen" is not a partition.** A seen Sim talks to an unseen one, a service picks a Sim across lots, an alarm writes a Sim, a route crosses a lot edge, `Relationship` holds both, an event fires seen listeners. Ownership would flip mid-task, and class flips are frequent: `unFlips` BP 2.9/s, BP crowd 2.2/s, IP 0.07/s [measured, harness camera].
5. **Tasks yield.** The closure's worst class is YIELD. `Simulator_SleepImpl` and `GetCurrentTaskImpl` are UNSAFE, and frames live in GC memory resumed through `setjmp` on one host path (docs/29 §1.3). Every task pointer in the scheduler would need a thread owner.
6. **Mods are unknown code.** 81 assemblies on the test install (docs/128). The allow-list model breaks: a mod's `Simulator.AddObject` task on the sim thread writes Sims the worker is running, with no guard that can see it.
7. **Reset is not free.** A snap-back that resets a Sim at 2.9 flips/s costs 14.5 ms/s at an assumed 5 ms each [inferred], in bursts when the camera pans. The destroying form (`ResetSimTask.Perform(sim, true)`) replaces the object (PLAN, "Sim lost mid-visit").

**What survives of A** is what already exists: bounded, read-only, no-yield decisions on a snapshot. That is C.

## 4. Options

Gains are sim-thread ms/s and are **estimates** [inferred] unless marked measured.

| | A: Mono worker | B: coarse ticks | C: snapshot offload | D1: park at boundary | D2: lots suspended | E: separate process |
|---|---|---|---|---|---|---|
| Gain | ceiling BP 61 / IP 23 / SV 49, net likely <= 0 | 0-8; family measured ~0 | 5-20 on top of `autunseen` (measured +6.6 / +11.2) | 10-90, central 20-40 | 100-250 | ~0 on the sim thread until merge is solved |
| Hitch effect | none proven | none | small | fewer plans; unseen own 68-97% of plan ms | largest | none |
| Crowd in view | little | none | **yes** | none | none | none |
| Effort | 200+ h, no end state | spent | 28-42 h + 6-10 jobs (docs/129) | ~1 h + 6 jobs | 10-20 h + 10-15 jobs | weeks |
| Crash or corruption risk | **unbounded** | low | low, guarded | **low**: no new frames, no off-thread writes | medium | high |
| Snap-back | Sim reset, ms each, burst on pan | small catch-up step | exact path at resume (docs/222) | none needed: Sim is valid; next search queues normally | wake, place, reset stale interaction | full state merge |
| Hitch when seen | yes | none | none | none: one search per tick, 2.4-3.3 ms | possible on a full lot | yes |
| Verdict | **reject** | **reject** | keep | **do next** | only if D1 proves it and Rodney asks | **reject** |

**B.** Time-conserving skips keep every decision, so nothing leaves. Measured: `unseenmotives`, `unseenbroadcast`, `unseenenv`, `wakepoll` parked at ~0; `wakepark` -2 / -5; `[UnseenThrottle] Rate` ~0 because `+0x918` waits move to the next taker (docs/143 §8.15); Mode 2 `rm2Coarse` 0 (docs/228 §11.8); bundle +8.5 / -9.9 by order and routes/s up 10-14%. Skipping can raise work.

**C.** Calibrate docs/129's ~37 ms/s down: go-live dry estimates over-counted 3-5x (memory `golive-unseen-only-redesign`). Motive decay is blocked (docs/208 §4). Slow, safe, and the only option for a crowd in view.

**D1.** Park at the boundary. An unseen, non-household Sim (`UfWhy` 0) with no pending push is not queued for a search until N sim-minutes after its last one. Its running interaction finishes normally. Pushes always work. EA's task idles on its own `Sleep(2)` path at existing cost. Why this differs from B: a search not run is not owed later, and 73-84% of searches end with no pick anyway (`bp-gf-g7`, docs/222 §10). `autunseen` shows cadence moves real ms: pacing took BP +10.6 to +6.6. docs/222 §6.2 sketched the search-only half (~6.5-8 ms/s) and left `u_r` unmeasured. The new part is the chain: fewer picks, fewer plans and steps.

**D2.** Freeze all tasks of a lot, fast-forward on seen. Largest number and largest bill: stale interactions, wake storm on pan, reset cost, and Overwatch (§5). Do not start it before D1 data.

**E.** A second process needs a live copy of world, Sims and script state; the offline host replays records and has no world (docs/119, docs/123). Merge at tick end is Option A's problem with serialization added. The useful process split is the narrow one in memory `routing-memory-out-of-process`: route search, not simulation.

## 5. What breaks

| Topic | A | B | D1 | D2 |
|---|---|---|---|---|
| Saves, `no-mod-data-in-saves` | must drain before `PreSave`; a mid-write save stores a torn graph | nothing stored | timestamps in memory, lost on load, re-derived; no sidecar needed | parked flag needs a sidecar or re-derive at load |
| `TaskStateCollection` stacks | stack must be saved from a worker-run task; methods keep their checksums | none | **none, if no hook frame sits under a `Sleep`** | a park hook on the stack hits I-SG6-1: a frame in a patched method is refused at load and the Sim resets; park must not add a frame |
| Wedding, party, situations | situation tasks on the sim thread write worker Sims | fine | pushes bypass the gate | must wake on `InteractionQueue` add; 2,034 add sites funnel into the queue [inferred] |
| Birthday | global alarm writes a worker Sim; `SimBuilderTask` runs on the sim thread | fine | fine | alarm runs; age-up on a frozen Sim needs a wake |
| Phone calls | `PhoneService` static | fine | fine: `ReceiveText` takes the caller as `SimDescription` [verified] | same |
| Sim coming to the active lot | ownership flip mid-route | delay | **visible risk**: fewer autonomous visitors; exempt Sims within reach (80 m); gate = arrivals per hour | same risk, plus wake at departure |
| Career | `createdSim.AddAlarm` callbacks race [verified] | fine | fine: work is pushed; `RunAutonomy` refresh must stay on or it starves `Autonomy.Update` (docs/144 §1.3), so gate the search, not the refresh | per-lot alarms must keep running: `Lot.Simulate` alarm line stays [verified]; cannot freeze the lot task |
| Needs death | as today | as today | safe: low LOD and steps over 1 sim-hour use `AutoSatisfyMotive`, not decay (docs/144 §1.3) [verified] | same; **this is EA's own fast-forward for needs, at no cost** |
| NRaas StoryProgression | unknown mod tasks race | fine | fine: scenarios push through queues [inferred] | pushes stall until wake |
| NRaas Overwatch `StuckCheck` | as today | as today | a Sim on the same spot with the same or no interaction past the limit (about a sim day) is reset [verified, source read]: N must stay well under it | **direct hit**: parked Sims look stuck; Rodney's rule is zero such resets on touched Sims |

## 6. Recommendation

**Build D1 as a ts3fixes switch, `unseenpark`, off by default, on top of `autunseen`.** Keep C for crowds in view. Reject A and E as designs; B stays parked. The gate belongs at the point where a Sim enters `AutonomyManager`'s low-LOD queue, so no frame survives it and `RunAutonomy`'s refresh stays. Confirm that site in the build; it was not read to the last caller here (I-243-3).

Open inferences:

| Id | Claim | Confirm by | Refuted if |
|---|---|---|---|
| I-243-1 | searches are not saturated: `AutonomyManager` runs one per wake, so fewer unseen entries means fewer searches, not faster service for others | searches/s per arm, queue length | searches/s falls under ~15% |
| I-243-2 | fewer searches cut the chain (routes, steps, locomotion), not only search ms | per-arm ms/s by bucket, routes/s, pushes/s | only autonomy ms falls |
| I-243-3 | an enqueue-side gate adds no saved frame | build read, save matrix | gate ends up in a frame under `Sleep` |
| I-243-4 | parked Sims stay out of `StuckCheck` at N <= 240 sim-min | Overwatch resets in the correctness job | any reset on a gated Sim |

## 7. Smallest proof, stop rule

**Build (~30-60 min):** switch + tuning `kUnseenParkMinutes` (default 30, ~6x EA's 5-minute `kAutonomyDelayNormal`), counters for gated, searched, pushes by class, queue length. Reuse `UfWhy`, unseen table, `--ab` runtime switches. `--self-check` and one selftest. Reserve counter slots first.

**Runs (~1.5 h, autonomy on, `autunseen` as release):**

| Job | What | Answers |
|---|---|---|
| `bp-upark-smoke` | 4 min `--ab`, 1 min slices | arm split, switch read-back, gated > 0 |
| `bp-upark-ab`, `-onfirst` | 10 min in-load A/B, both orders | total ms/s per arm, by bucket; I-243-1, -2 |
| `ip-upark-ab`, `-onfirst` | same on IP | same; hitches as no-regression guard |
| `bp-upark-check` | 15 min mode on with `unseen-snapback` | Overwatch resets, work attendance, GoHome, arrivals on the active lot; I-243-4 |

**Stop rule, judged by per-arm summed ms/s (memory `throttles-judged-by-ms`):**

- **Continue** when BP and IP both gain >= 5 ms/s in both orders and the check job is clean. Then tune N, and call it default ON under `clear-gain-on-by-default`.
- **One fix round, then park** when the gain is 0-5 ms/s, or only autonomy ms falls (no chain: then it is docs/222 §6.2's throttle and worth ~6-8 ms/s).
- **Stop now** when searches/s falls under ~15% (I-243-1 refuted, the waits just moved), or a routing bar FAILs: R1-R6 of docs/246 §6 (`gate2-report.ts`; plans and route work per Sim-hour, job mean, fails, `ufLong`, p99.9 routes per second; thresholds are judgement). Not the `routes` column: it counts sim-thread calls only and moves +6.7% for EA-identical Mode 1 (docs/246 §1).
- **Fix before any more runs** on a crash or a reset of a gated Sim (memory `crash-cause-before-fix`).
- **D2 only** if D1 at N = 120 gives >= 30 ms/s on both worlds and Rodney asks for more.
- **A stays closed.** Reopen only with a design that removes shared writes and the sync inside the tick; a faster worker does not change §3.

**What D1 does not do:** nothing for crowds in view, nothing for the seen active household, and a less busy town if N is large. The visitor count is the number to watch, since the R1 bar (memory `golive-unseen-only-redesign`) allows only changes the player cannot see.

## 8. Reading notes

- Not measured: ms by Sim class per sample. A light per-sample class tag in the managed sampler would replace the §2 shares; it is not needed for the D1 proof, which measures the gain directly.
- `other` was not split (docs/28 §0.3), `+0x918` waits are absent from these runs (`/native` keys missing), and GC effects of parking are unknown.
- `unFlips` come from harness camera moves, so natural play may flip less.
- Interaction with `autunseen`: gains overlap on the autonomy bucket, so D1's A/B must run with `autunseen` at its release setting.

## 9. D1 as built: `unseenpark` (2026-11-06, Rodney's call over `unseenrefresh`; never run in game)

Switch `unseenpark` (`kUnseenPark`, off), `kUnseenParkMinutes` 30 (0 = off, cap 240). ts3fixes `UnseenThrottle.cs` `UnseenParkHook`, pure logic `UnseenMath.ParkHold` / `ParkVerdict`, selftest `tools/selftest/unseen` `ParkGate`. Tags as above; ledger docs/51 §433.

### 9.1 Gate site, confirmed (closes the "confirm" of §6, I-243-3 still needs its run)

- **Site: `AutonomyManager.Add(Autonomy)`, static.** Only two callers in all decompiled assemblies, both on the idle path of `Sim.DoInteraction`, one for `CanRunAutonomyImmediately` and one after EA's idle delay [verified, grep of `game-copy/decompiled`]. `AddWhileInteracting` has no callers [verified]. The only NRaas caller is the debug tool `MasterControllerTest TestSocials` [verified].
- **Safe for saved stacks.** `Add` calls `Enqueue` and `LinkedList.AddLast`, so nothing below it yields, and the hook's own calls (`UfWhy`, the field read, the clock) do not either [verified, read]. Neither frame can sit on a saved stack. The `Simulator.Sleep` that follows is in `DoInteraction`, which this does not patch. The stack-swap CI check is **not on this base** (it lives on an unmerged saved-stacks branch), so that rule was checked by reading only.
- **Not a tunable.** `kAutonomyDelayNormal` is one static for every Sim, so it cannot tell unseen from seen.
- **Held = `AddOrig` not called.** `InAutonomyManagerQueue` stays false and the Sim loops EA's idle path. `RunAutonomy` and its refresh are untouched, as §5 asked.
- **Pushes bypass it.** `DoInteraction` runs a queued head interaction before it reaches the idle path [verified].
- **Clock: `Autonomy.mLastTime`, no table.** On the low-LOD path only `RunAutonomy`'s refresh moves it (docs/245 §3), so it is the last search time to within EA's 1-minute floor. No new state at all; §5's "timestamps in memory" is not needed. A Sim on the high-LOD queue is never gated, so `SimUpdate` keeping the clock fresh cannot starve it.

**Design change from §4:** "at existing cost" is too kind. A held Sim sleeps `kSimLoopSleepTicksWhenNotInQueue` (2 ticks), where a queued one sleeps 10 [verified], and it asks the hook on each wake, with one `UfWhy` and one boxed field read. EA's idle wait already polls the same way, for 5 sim-minutes; `unseenpark` stretches that. Poll cost is unmeasured: `unseen_park_held` counts polls, so held/s x cost per poll is the bill [inferred]. If it eats the gain, the fix round is one classifier call in N polls, which loosens "at once" to N x 2 ticks.

### 9.2 Who waits

| Case | Verdict | Why |
|---|---|---|
| `UfWhy` != 0: household, selectable, on screen, near viewers, on a seen lot, held, talking to a seen Sim, not live, gone, error | **goes at once** (`unseen_park_snap` in the window) | re-asked on every poll; a seen Sim passes in its own stagger slot, within `kUnseenParkStaggerTicks` (16 ticks = 0.43 sim-minutes at 37.5 ticks per sim-minute), not within 2 ticks |
| Sim walking toward a seen lot | not gated | it has an interaction, so it never reaches the idle path; on arrival `UfLotSeen` is true |
| High-LOD queue (`IsRunningLocalAutonomy`) | not gated | visible path unchanged |
| `Service`, `HasActiveRole` | exempt | EA itself skips the idle delay for them |
| Any situation (party, visit, date, group, wedding) | exempt | situations steer Sims partly by autonomy; covers guests |
| Pregnant | exempt | not needed by the code read below; kept out of caution |
| No clock, clock ahead of the game | goes | fail open |
| Everyone else, unseen, under N since the last search | **held** | |

Not affected, shown by reading [verified]: **work and school** are alarm pushes (`Career.RegularWorkDayGoToWorkHandle` -> `InteractionQueue.Add`). **Pregnancy** births are alarm plus queue pushes (`HaveTheBaby`, `HaveBabyHospital`). **Needs** stay safe (§5, low LOD is the auto-satisfy curve). **Careers, buffs, skills** in the low-LOD refresh take the gap as a parameter; N = 30 sits well under the 68-135 sim-minutes that gap already averages. **EA Story Progression** does not use this queue; `bp-upark-check` runs with it on.

[inferred, needs a run] **Visitors:** a visit to the active lot starts as a meta-autonomy pick by a Sim anywhere in town, so no exemption by Sim can protect it. Fewer searches may mean fewer arrivals, or may not: 68-83% end with no pick, so picks may be limited by lot demand rather than by searches. New counter `unseen_park_arrivals` (non-household Sims walking onto a seen lot, event `kEventSimMovedToLot`, listener always on and counting only, both arms) is the number. An on-arm drop is the same bar as §8's R1 line: a change the player can see.

### 9.3 Existing coverage, NRaas

Web search found no mod that gates when autonomy searches run ([N.A.P. Autonomy Disabler](https://db.modthesims.info/d/645513/n-a-p-autonomy-disabler-for-my-households-all.html) switches a household's autonomy off, a different thing) [community-reported, one search]. NRaas source (read only): nothing gates the queue. StoryProgression reads `InAutonomyManagerQueue` for a tooltip and `AutonomyDisabled` for checks, and its scenarios act through interaction pushes, which bypass the gate [verified for the reads, inferred for all scenarios]. Tagger reads the flag for display. Overwatch `CleanupAutonomy` restarts the manager only if missing. Retuner could change `kAutonomyDelayNormal`; that composes, the later of the two wins. `NRaasDebugEnabler` zeroing `mLastTime` means "no clock", which lets the Sim go. So no mod overlaps and no alongside-mod job was added; Overwatch `StuckCheck` is the only timing limit, and N stays under it (I-243-4).

### 9.4 Counters, repro, queue

Stagger (Rodney 2026-11-06): a held Sim that just became seen passes the gate only when `(tick / 4 + hash(SimDescriptionId)) % (kUnseenParkStaggerTicks / 4) == 0` (`UnseenMath.ParkStaggerPass`, tick = `SimClock.CurrentTicks`). The slot is 4 ticks wide, twice the 2-tick poll (`kSimLoopSleepTicksWhenNotInQueue`), so any Sim that polls with a gap up to 4 ticks lands in every slot at least once and cannot be starved, whatever the poll phase; a 1-tick slot would be skipped forever by half the Sims. The 4 slots of the default 16 are filled by hash, not by RNG, no state, no table. Only Sims released by being seen are staggered; never-held and exempt Sims and timed-out unseen releases are untouched (their clocks already differ). `kUnseenParkStaggerTicks` 1 (or under 8) = old behaviour. Cost: a Sim that comes into view waits up to 16 ticks (0.43 sim-minutes, about 26 sim-seconds) instead of under 2; a poll gap over 4 (stalled tick) can miss a slot and add another 16 [inferred: a gap that long was not seen]. Counters 824 (most snap releases in one tick) and 825 (polls deferred). Selftest `ParkStagger`: 30 Sims seen in one tick, 3 gaps x 4 phases with late wakes, every one released within 16-20 ticks, max per tick <= 12; 2000 ids x 2 phases all within 16 [verified, selftest]. Not run in game.

Counters 818-823: `unseen_park_held` (polls), `_snap`, `_exempt`, `_unseen` and `_seen` (searches queued outside the window; unseen share = unseen / (unseen + seen)), `_arrivals`. Histogram 778-784 fills with `unseenprobe` or `unseenpark`: with the probe alone the buckets under 30 sim-minutes are what N = 30 can hold, and with `unseenpark` on they should empty, the direct proof it works. **That also settles docs/245's open question:** if those buckets are small, the chains are small and D1 cannot reach the bar.

Repro `unseen-park-loop` (STRESS). Queue `tools/harness/queues/unseen-park.json`: smoke + 4 A/Bs (BP, IP, both orders, 10 min, `--ab unseenpark`, `unseenprobe` on in both arms) + `bp-upark-check` = §7's 6 jobs, then `bp-upark-loop` and `bp/ip-upark-focus`, `-arrive` (gate2 repros `aut-unseen-focus`, `aut-unseen-arrive` on the gate2 stack, since they need a direct pick). §7 named `unseen-snapback`; it needs go-live too, so the loop and focus jobs take its place. Stop rule unchanged: >= 5 ms/s per arm on BP and IP in both orders, hitches not worse.

Effort: built, ~2 h with this read. Game: ~1.5 h for the 6 jobs, ~1 h for the rest; no clean night asked.
