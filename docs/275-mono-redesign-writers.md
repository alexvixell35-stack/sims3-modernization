# 275. Mono offload redesign for the two "writer" candidates: motive decay and zombies on lot

Written 2026-11-16. Static analysis and existing run data only: no build, no game run, no queue. Branch `docs/mono-redesign-writers-1116`. Ledger: docs/51 §490.

Question (Rodney): docs/129 rank 3 (motive decay) and rank 5 (zombies on lot) are blocked because the work writes game state. Find exactly what blocks them and any design that could save them.

Tags: **[verified]** read in the decompiled script DLLs, in tool output or in our source; **[measured]** from the run files named; **[inferred]** reasoning, to confirm before code depends on it; **[community]** modding practice, not checked here.

## Verdict

- **Neither feature can be saved as an offload.** Both are many small lookups followed by a write. The answer a worker could hand over costs about as much to check and consume on the sim thread as the lookup it replaces. This is the plain answer for both.
- **Both can be made cheaper in place, exactly, for every Sim and lot** (no household gate needed, because the result is the same as vanilla). The gains are small: ~0.7-2.2 ms/s for zombies and ~0.3-1.0 ms/s for decay [inferred].
- **Build first: Z1**, the empty-table early-out for zombies (~45 min of agent work, one 10-minute job). Then M1, the absent-buff memo for the decay multiplier (~1 h), in the same bundle.
- **The largest remaining lever for decay is the compiler lane, not the worker:** `UpdateMotiveWithDecay` stays interpreted only because of two store-object posture overrides (M2, ~1.0-2.3 ms/s [inferred], needs a check with the MonoJit lane first).
- The household rule is not in the way of any design below: every kept design is exact. `HouseholdToo` (docs/271) has no site here.

| Rank | Design | Feature | Kind | Exact | ms/s [inferred] | Effort | Verdict |
|---|---|---|---|---|---|---|---|
| 1 | Z1 empty-table early-out | zombies | in place | yes | 0.7-2.2 | ~45 min + 1 job | **build first** |
| 2 | M1 absent-buff memo for the decay multiplier | decay | in place | yes | 0.3-1.0 | ~1 h + 1 job | build with Z1 |
| 3 | M2 guarded compile of `UpdateMotiveWithDecay` | decay | compiler lane | yes | 1.0-2.3 | 3-8 h, unknown until §4.3 step 1 | ask the MonoJit lane first |
| 4 | Z2 inert-entry tracking | zombies | in place | yes | adds to Z1 only on saves with table entries | ~2 h | only if the Z1 probe shows a non-empty table |
| - | M3 worker with term-log replay | decay | offload | yes, if every stamp is complete | under ~2, at high risk | 10-16 h | **no** |
| - | M4 plan memo on the sim thread | decay | in place | same stamp problem as M3 | 2-3 | 8-12 h | **no** |
| - | M5 unseen-only cadence with catch-up | decay | throttle | no | ceiling ~0.17 [measured, docs/245] | built and parked (docs/144) | **no** |
| - | M6 fork-join inside one update | decay | offload | no (order) | under 0 | - | **no** |
| - | Z3 worker precompute of counts and stereo state | zombies | offload | no gain | under 0 | - | **no** |

## 1. Data used

- **Closures:** `docs/data/offload3-closure.csv` (docs/208 §1: depth 12, class-hierarchy overrides within 5 hops, instance field writes), candidates `motive`, `motive-core`, `zomb`, `zomb-whole`, and the `posture:*` candidates. New queries for this note: `node tools/managed-callgraph.ts query Sims3.Gameplay.Core.ZombieDanceTimeOut::HasStereoOnTheLot Sims3.Gameplay.Core.ZombieDanceTimeOut::UpdateTimeOutOnTheLots Sims3.Gameplay.Core.ZombieDanceTimeOut::Instance --ics --depth 12`, and `callers` for the methods in §3 and §4 [verified: tool output].
- **Source:** the decompiled `Motive`, `Motives`, `Lot`, `ZombieDanceTimeOut`, `BuffManager`, `GenericManager` classes in `game-copy/decompiled` (Steam 1.67) [verified]. Described here, not copied.
- **Cost:** all 410 managed sample files in `probe-out/profile/*-managed.jsonl` (2026-10-16 to 2026-11-16, 417,580 samples, mixed builds and switch sets). Scale as docs/245: 1 sample in 1,000 = 1 ms/s of sim-thread time. A throwaway node script did the counts; nothing committed. The 41 files of 2026-11-06 and 2026-11-08 are left out of the rates: they are MonoJit verify and batch runs, where decay reads 23-25 ms/s because the verify pass runs each call twice (tool cost, not game cost) [inferred from the run names].
- The 8 release-like runs of docs/245 §1 remain the best absolute numbers; the large pool is used for callee splits, where 30 samples are too few.

| Frame (inclusive) | Samples of 383,544 | ms/s | docs/245 (8 runs) |
|---|---|---|---|
| `Motive.UpdateMotiveWithDecay` | 2,413 | 6.3 | 4.8 |
| of it `Motive.GetCommodityDecayMultiplier` | 482 (20%) | 1.3 | 0.4 (3 of 34) |
| `Lot.UpdateNumZombiesOnLot` | 1,217 | 3.2 | 3.4 (24 of 7,083) |

Zombies by world, all files: BP 3.0, IP 4.2, SV 3.6 ms/s [measured]. docs/129's "IP 7.5, BP 1.5" came from 24 samples; the cost is close to even across worlds.

## 2. What blocks each feature [verified unless marked]

### 2.1 Motive decay

One call computes one motive's new value for one high-LOD Sim. `Motives.UpdateMotives` calls `Motive.Update` for each motive in turn; `Update` calls the decay step, then `SetValue`, then the buff triggers.

| # | Blocker | Evidence |
|---|---|---|
| D1 | **Own-field write on most paths:** `Motive.Decay` is written by the fun, energy, fixed-decay, auto-satisfy-curve and by-moodlet branches. `UpdateFun` compares the old `Decay` with the new one and, when they differ at work or school, calls `UpdateFunMotiveAtWork`. | closure row `Motive::Decay`; decompile |
| D2 | **Writes into other objects:** `Motives.EnergyGainRate` (lazy fill from the bed tuning), `BuffInstance.mTimeoutCount` (temperature), `MotiveDelta.Enabled` and the work interaction's fun delta, `PetBed.SetActor`, and lazy fills (`Posture.mInteractions`, `SittingPosture.mContainer`, `PetSurfacePosture.mContainer`, `BeachTowelPosture.mTowel`, `Genealogy`, `Sim+OutfitInfo.Category`, `OccultImaginaryFriend` outfit key). | `motive-core`: 119 field writes, 40 outside the BCL |
| D3 | **Static writes:** `Services.sAllServices` (lazy fill through the temperature "considered indoors" test) and `RandomGen.gRandomGen` (the BFF lookup at work draws RNG, class-b IC `Random_NextDoubleImpl`). | `motive-core` static_write 2, ic 20 |
| D4 | **Two posture overrides write and yield:** `BabySwing+InSwingPosture` (starts a child nap: sleep state, 3 event sends, 41 static writes, a destroy, 1 yield) and `WalkerClassic+InWalkerPosture` (`ClearInvalidInteractions`: removes interactions, 3 yields through `RequestState` and `Sleep`). Every yield of the whole `motive` closure goes through `InWalkerPosture`; both events go through `InSwingPosture`. The other 15 overrides of `GetPostureMotiveDecayUpdate` are read-only. | `motive` yield 3, event 2; `posture:*` rows; docs/208 §4.3 census |
| D5 | **Time step known only at the call:** `hoursPassed` comes from the clock when the Sim's update task wakes. A worker that runs before the wake has to guess it, and a float result is only bit-equal for the same `hoursPassed`. | docs/208 §4.2 blocker 1 |
| D6 | **Order inside one Sim's pass:** motive k's buff triggers (`UpdateMotiveBuffs`) add and remove buffs that motive k+1's decay reads (fun reads 6 buffs, hunger 2, hygiene 2, social 4, and so on). The loop can also yield (`Simulator.Sleep(0)` every 10 motives when the pass runs long), so other tasks can run between two motives of one Sim. | decompile of `UpdateMotives`; docs/245 §2 |
| D7 | **Wide, fast-changing read set:** current interaction and its motive deltas, posture chain, dozens of buff and trait tests, skills, occult state, lot type, tomb room, sleeping, routing walk style, weather, moon phase, time of day. | decompile; 20 ICs in `motive-core` |
| D8 | **Small budget:** 4.8-6.3 ms/s over ~32 Sim updates/s (counter [149], docs/245 §1.1) is ~150-200 µs per Sim pass, ~10 µs per motive [inferred from measured totals]. A hand-over that validates a stamp, reads a record and falls back on a miss has little room. | docs/245 §1 |

D1-D3 could be handled by a write log. D4 by excluding Sims in the two postures. **D5-D8 together are what kill the offload**, see §4.4.

### 2.2 Zombies on lot

`Lot.Simulate` calls `UpdateNumZombiesOnLot` for every simulating lot each time its task runs (Supernatural installed). One call: subtract the elapsed time from the lot's search interval; ask whether the lot has a zombie-dance stereo; every 3 sim-minutes count zombie Sims and maybe place a stereo; if there is no stereo, run down the lot's dance time-out.

| # | Blocker | Evidence |
|---|---|---|
| Z-B1 | **The reads write.** `ZombieDanceTimeOut.Instance()` creates the persisted static `mObject` when empty. `HasStereoOnTheLot` clears `mStereoId` when the stereo object is gone. `GetTimeOutOnTheLot` adds a table entry on a miss. The table is persisted with the save. | `zomb` static_write 1, field_write `mStereoId`, dictionary writes; decompile |
| Z-B2 | **The main cost is a write:** `UpdateTimeOutOnTheLots` (27% of samples) subtracts from `mTimeOut` on every call when the lot has an entry. `mSearchForZombieInterval` is written every call too. | decompile; 394 of 1,451 samples |
| Z-B3 | **Yields and commands on the rare branch:** `AddZombieDanceStereo` creates and places an object (`zomb-whole`: 8 yields, 4 events, 191 ICs, 69 static writes). | `zomb-whole` rows |
| Z-B4 | **Nothing is left to move.** Without the writes, the per-call work is two failed or trivial dictionary lookups on a `ulong` key. A worker result would need its own per-lot lookup to be consumed. | sample split below |

Sample split under `UpdateNumZombiesOnLot` (1,451 samples, all files) [measured]:

| Callee | Samples | Share |
|---|---|---|
| `get_HasZombieDanceStereo` (of it `HasStereoOnTheLot` 584, `Instance` 62, self 74) | 720 | 50% |
| `UpdateTimeOutOnTheLots` (of it `Dictionary.TryGetValue` 327) | 394 | 27% |
| self | 224 | 15% |
| `NumZombiesOnLot` (every 3 sim-minutes) | 90 | 6% |
| `Instance` (direct) | 23 | 2% |

By leaf frame, 54% of the cost is dictionary plumbing (`UInt64.GetHashCode`, `TryGetValue`, `GetPrev`, `GetSlot`, the comparer's `GetHashCode`).

## 3. Zombies: designs

### 3.1 Z1: empty-table early-out (build first)

**What.** Two hooks with the existing `Patch(..., Hook, Orig)` mechanism (as `ExactMemos.cs`):

- `ZombieDanceTimeOut.HasStereoOnTheLot(lotId)`: when the time-out table has no entry at all, return false.
- `ZombieDanceTimeOut.UpdateTimeOutOnTheLots(lotId, t)`: when the table has no entry at all, return.

Otherwise each hook calls the game's method. The table is a private field: read its reference once per `ZombieDanceTimeOut` instance by reflection, keep it as `System.Collections.ICollection`, and ask `Count` per call. The keeper is refreshed whenever the instance reference handed to the hook differs from the kept one (load, travel, `ClearStatics`). Off the sim thread the hooks call the game's method (docs/128 G-M3).

**Exactness.** With an empty table [verified in the decompile]:

- `HasStereoOnTheLot`: the lookup fails, the method returns false and writes nothing. The hook returns false and writes nothing.
- `UpdateTimeOutOnTheLots`: the lookup fails, nothing is written. The hook returns.
- All other steps (`Instance()`, the interval subtraction, the 3-minute count, `GetTimeOutOnTheLot`, `AddZombieDanceStereo`) still run in the game's code, in the game's order. If the count step adds an entry, the table is no longer empty and the very next hook call goes to the game's method, so the "0 minus elapsed, clamp at 0" step is the game's own.
- The table's only writers are the three add sites and `PostWorldStartup` inside `ZombieDanceTimeOut` itself (private field, no other accessor) [verified]. Each hook has exactly one caller in the game assemblies (`callers`: `UpdateNumZombiesOnLot` and `get_HasZombieDanceStereo`) [verified].
- Nothing new is stored, nothing in the save changes, removing the mod needs no cleanup.

So the result equals vanilla for every lot, household lots included: a proven-exact speedup, no scope gate (CLAUDE.md household rule, "proven-exact speedups stay").

**To verify before the build.**

1. EA 1.69 has the same IL for the two methods and for `Instance` (IL hash, as `AutShadowIlHashes.cs` does for other methods).
2. `Patch` works on these two instance methods of a `[Persistable]` class and on the sim thread costs clearly less than one `Dictionary<ulong, T>.TryGetValue` (interface `Count` call plus two reference compares against ~5 interpreted calls with a comparer dispatch). If the hook dispatch itself is not cheaper, stop.
3. Mod census: no installed mod assembly calls or replaces the two methods (`managed-callgraph.ts callers --mods`). NRaas source: no reference to `ZombieDanceTimeOut` or `UpdateNumZombiesOnLot` [verified, grep of the local NRaas source].
4. How often the table is empty in real saves: unknown. Entries are added the first time a lot has 3 or more zombies at a search and are never removed while the lot exists, and the table is saved. A new counter pair (calls, calls with an empty table) answers it in the first job.

**Gain [inferred].** The two hooked subtrees are 67% of 3.2 ms/s (584 + 394 of 1,451). If the hook costs a third of what it replaces: ~1.4 ms/s pooled, ~1.9 on IP. A whole-method replacement of `UpdateNumZombiesOnLot` would also save the getter, three `Instance()` calls and part of the self time (~2.2 ms/s), but it must write the private `mSearchForZombieInterval` per call; reflection per call would eat the gain, and compiling against non-public members relies on the runtime not checking access [community]. Not proposed. With MonoJit on (default since 2026-11-15) the dictionary leaves are compiled, which may halve what is left to save: **0.7-2.2 ms/s**.

**Risk.** Low: no new state, sim thread only, falls back to the game's method on any doubt. Main risk is a gain under run noise.

**Effort.** ~45 min agent build with selftest (pure rule in a `*Core.cs` file: empty, non-empty, instance swap) and counters (reserve with `tools/registry.ts`); one 10-minute job with a `verify` switch that runs both paths and counts mismatches, plus per-arm ms/s from the counters. No clean night.

**Stop rule.** Empty-table share under ~80% of calls on the test saves, or saved time (skipped calls x measured per-call cost) under ~0.5 ms/s on both BP and IP: park the switch, default off. A verify mismatch: stop and read the IL again.

### 3.2 Z2: inert entries count as absent (only after Z1's probe)

An entry with `mTimeOut == 0` and an invalid stereo id behaves as absent for both hooked methods when the elapsed time is greater than 0 (0 minus t is negative, the game writes 0 back). Z2 keeps a count of entries that are not inert and takes the early-out when it is 0. That needs hooks on `ResetTimeOutOnTheLot`, `SetStereoOnTheLot`, `GetTimeOutOnTheLot`, the two stereo-clearing reads and the time-out reaching 0, with reflection on a private nested class. ~2 h. Exact only with the guard `t > 0` (a zero, negative or NaN step goes to the game's method). **Build only if Z1's probe shows saves where the table is rarely empty**; otherwise it is dead weight.

### 3.3 Z3: worker precompute (docs/129 §2.5 as planned) — cannot be saved

- The count (`NumZombiesOnLot`) is 6% of the cost and runs every 3 sim-minutes. Moving it saves ~0.2 ms/s before any consume cost.
- The stereo state and the time-out are per-lot table entries. A worker-made answer per lot has to be found per lot on the sim thread, which is the lookup we wanted to avoid.
- The game's getters cannot run on the worker at all (Z-B1: they write persisted data).

No write-log, shadow or cadence design changes this: the cost is the lookup, and the lookup is needed to consume any answer. **Rank 5 is closed as an offload.** docs/129 §2.5 already called the in-place memo a tie; with the real split it wins outright.

## 4. Motive decay: designs

### 4.1 Where the time goes [measured, 3,244 samples, all files]

| Callee of `UpdateMotiveWithDecay` | Samples | Share |
|---|---|---|
| `GetCommodityDecayMultiplier` (nearly all in `BuffManager.GetElement`) | 1,085 | 33% (20% without the verify runs) |
| self (the method's own 5,132 IL bytes: ~40 commodity tests, float math) | 731 | 23% |
| `SimTemperature.CalculateSimTemperatureChangeAndBuffs` | 116 | 4% |
| `MotiveTuning.get_Min` / `get_Max` | 180 | 6% |
| `BuffManager.HasElement` / `HasAnyElement` | 118 | 4% |
| `Sim.get_CurrentInteraction` | 90 | 3% |
| `DaycareSituation.GetDaycareSituationForChild` | 88 | 3% |
| `SeasonsManager.get_Enabled` | 81 | 2% |
| everything else (traits, werewolf test, tomb room, sleepy, curves, enumerators) | ~750 | 23% |

No single callee other than the multiplier is worth a memo of its own.

### 4.2 M1: absent-buff memo for the decay multiplier

**What.** `GetCommodityDecayMultiplier` asks the Sim's buff manager for three reward buffs (`CommodityDecayModifier`, `...1`, `...2`) on every decay path, for every motive of every pass: ~3 dictionary lookups x ~15 motives per Sim pass. Almost no Sim has them. Hook the method: keep the last buff manager seen and a stamp; when the manager is the same, the stamp is unchanged and all three buffs were absent at the last full check, return 1. Otherwise call the game's method and refresh the memo by one `HasElement` test per buff.

**Stamp.** A buff that is absent stays absent until something is added to that Sim's buff table. The per-Sim table (`mValues`) has exactly two add sites, both in the private `BuffManager.AddBuff` overloads [verified: grep of the decompiled assembly; the field is protected, and object and store assemblies have no subclass of `BuffManager` to check: to verify with `subtypes`]. A hook on those two bumps one global counter. Removals need no hook (they cannot make an absent buff present). A loaded or travelled Sim has a new buff manager object, which misses on the reference compare.

**Exactness.** With all three buffs absent the game's method multiplies nothing and returns the constant 1 [verified]. The memo returns that constant only while (same manager, no add since the check). The buff triggers of the previous motive run through `AddBuff`, so D6 (order inside the pass) is covered by the stamp. A mod that inserts into `mValues` by reflection bypasses it; a `verify` switch counts such mismatches, as `permamemoverify` does. Same answer for every Sim, so no household gate.

**To verify.** (1) `Patch` on the two private `AddBuff` overloads and on the private `GetCommodityDecayMultiplier` (EA 1.69 IL hash too); (2) the cost of the extra hook on the add path: buff adds per second are not counted yet, read counter-wise in the first job; (3) no other writer of a `BuffManager`'s `mValues` in any game assembly (`managed-callgraph.ts` field-write scan over all four gameplay assemblies, not only the grep of one); (4) NRaas: Relativity and Career only read or retune these buffs [verified, grep], re-check with `--mods`.

**Gain [inferred].** The multiplier is 0.4-1.3 ms/s (9% of decay in the 8 release-like runs, 20% in the large pool). The memo keeps roughly a quarter of it: **0.3-1.0 ms/s**, less with MonoJit compiling the dictionary leaves. Small; it ships only as part of an exact-memo bundle (the rare-case bundle rule), not on its own timing.

**Risk.** Low to medium: the stamp must cover every add path. Effort ~1 h agent build with selftest, one 10-minute job with verify on. **Stop rule:** any verify mismatch not explained by a mod, or saved time under ~0.3 ms/s on both worlds: leave it off.

### 4.3 M2: let the compiler lane take `UpdateMotiveWithDecay`

**Why it is not compiled today.** `docs/data/jit-hot.csv` and `compile-candidates.csv` class it **Y** ("yields: stays interpreted", "8 hops to RequestState"). The closure shows the only yield paths are the two store-object posture overrides of D4; with those two cut, `motive-core` has **0 yields, 0 events** [verified]. The method is Y by class-hierarchy over-approximation, as docs/207 suspected for "the motive updates" (its note under the yielding-methods table).

**Design.** Split the pure part from the writing part at the one virtual call: a guard on the receiver's type at the `GetPostureMotiveDecayUpdate` call site (or at method entry: `sim.Posture` and its chain are not one of the yielding override types, mod overrides included via the docs/132 scan). Guard true: run the compiled body. Guard false: run EA's interpreter for this call. The body, its writes (D1-D3) and their order are unchanged; only the executor changes, so exactness is the MonoJit lane's existing per-method proof (host corpus, `Mode=2` verify), not a new argument.

**To verify, in this order.**

1. With the MonoJit lane (docs/537): can a class-Y method be admitted behind a per-call guard at all? The rule exists because a compiled frame cannot be on the stack when a task yields. If a compiled body leaves through an exit to EA's interpreter at every non-listed call, the guard may not even be needed; if it holds a native frame across calls, the guard must cover every callee that can yield, and the closure says that is exactly the two overrides.
2. Compiler coverage of this body: 5,132 IL bytes, 4 protected regions (the `foreach` loops), 7 switches, 1 `newarr`, float compares. docs/537 §8 says a compiled method has no clauses; if the refusal log names clauses or size, M2 needs new compiler pieces and stops here.
3. The posture chain: `GetPostureMotiveDecayUpdate` is called on `sim.Posture` only; the 17 overrides do not forward to a previous posture's override that could be one of the two [inferred, to read].

**Gain [inferred].** Self time of the method is 2.1 ms/s (`jit-hot.csv`, mean of three worlds) to 3.4 (`compile-candidates.csv`, BP). At the 2-3x the lane reaches on bodies: **1.0-2.3 ms/s**. The siblings (`Motive.Update` 1.2, `UpdateMotiveBuffs` 2.0, `EarlyOutMotiveUpdate` 0.3 ms/s self) are Y through buff callbacks and `Sleep`, not through an enumerable override set, so they do not follow.

**Risk.** Medium: a large method in the compiler, on every visible Sim's needs. `FaultRecover` and verify mode are the nets. **Effort:** 30 min for steps 1-2 with the lane owner's docs; 3-8 h of build if both pass; host gates plus one verify job. **Stop rule:** step 1 or 2 negative, or under ~1 ms/s in paired timing (`[MonoJit] Time=1`).

### 4.4 M3: worker with a term-log replay — exact on paper, not worth building

This is the strongest offload design found; it is written down so the question does not come back.

**Design.** The decay step is, for fixed inputs, a short ordered list of float operations on the current value and `hoursPassed`: add `c x h`, multiply by a trait factor when the running sum is positive, clamp against zero, clamp to the range. The ~40 tests around them (which buffs, traits, posture, interaction deltas apply) change far less often than the value does. So the worker does not compute a value (D5 kills that). It computes the **term log**: the ordered operations with their constants and the value-dependent conditions left symbolic (sign of the value, the by-moodlet band of the value, the time-of-day curve read). The sim thread, inside `Motive.Update` at the exact place and order of the game's own call, replays the log with the real `hoursPassed` and value, then writes `Decay` and the value itself. Replay in the game's order at the game's call site is the deterministic point; no batching, so D6's order holds by construction. Excluded and left to the game: Temperature (writes buff time-outs, takes `hoursPassed` inside), Energy before its gain rate is filled, Fun at work or school (D1's compare and the RNG), the two postures of D4, horses while routing.

**Exactness argument.** Bit-equal if (a) the replay performs the same IEEE operations in the same order as the interpreter does (same x87 path, to prove in the host as the MonoJit float cases were, docs/221), and (b) every input of every test is covered by a stamp that changes when the input changes: buff adds and removes, trait adds and removes, posture set, interaction start and end and each `MotiveDelta` edit, skill level, occult change, lot change, sleep state, walk style, weather and moon. (b) is D7.

**Why not.**

1. **The worker adds nothing.** A term log stays valid across many passes until a stamp moves, so it is a memo. The sim thread can fill a memo itself on a miss; the worker only moves the misses. That makes M3 equal to M4 (same memo, filled in place) plus the whole worker window (docs/206) for the miss share.
2. **The stamp is the risk.** About a dozen hook families on hot paths (buff add and remove alone run thousands of times per second, docs/245 §5 H). One missed writer of one input is a silent wrong need on a visible, mostly household Sim, with no crash and no log. The household rule asks for results equal to vanilla; a design whose proof is "we believe we hooked every writer of 60 inputs" does not meet "proven-exact".
3. **The budget.** ~10 µs per motive today (D8). Replay, stamp compares and the hook dispatch are interpreted too: perhaps 3-4 µs. Best case ~3 ms/s for M4, ~2 for M3 after its excluded commodities, before the cost of the new hooks on the buff and interaction paths [inferred].
4. **Who it would help.** 96-98% of decay calls are seen Sims (docs/245 §1.1), so there is no unseen-only version with a relaxed check that is worth anything, and `HouseholdToo` does not apply: it lifts the exemption on realistic-only sites, and this design would have to be exact for seen NPCs anyway.

**Verdict: no.** Effort would be 10-16 h plus Mode R jobs. The offload goal accepts up to ~2% loss for a stable move, but not an unprovable exactness risk on household needs. If a later profile shows decay well above ~15 ms/s in real play (large households, parties on the active lot), reopen M4, not M3, and start with a Mode R run that counts stamp hits per commodity.

### 4.5 The remaining patterns, each with its reason

- **M5 unseen-only cadence with analytic catch-up: no.** EA already does it: low-LOD Sims never run decay, they read a time-of-day curve [verified, docs/245 §3]. Unseen Sims that still run decay are 1.7-3.5% of calls, ceiling ~0.17 ms/s [measured, docs/245 §1.1]. Decay is also not analytic over a long gap: the by-moodlet rate depends on the value's band, decay-to-zero clamps at 0, and each buff trigger feeds the next step; EA itself switches to the curve past its own gap limit. `unseenmotives` (docs/144) built the cadence with a capped catch-up and its bundle gained ~0.
- **M6 fork-join (worker computes motives k..n while the sim thread does 0..k-1): no.** D6 makes the motives of one pass sequentially dependent, and the worker would run managed code while the sim thread does, which is the docs/206 window problem for ~150 µs of work.
- **Batch or vectorise across Sims on the sim thread: no.** Only 2 Sims run the high-LOD body per tick (docs/144 §1), and the per-motive work is branchy lookups, not arithmetic. Hoisting per-pass constants (current interaction, posture, daycare, horse tests) out of the per-motive call needs a re-implementation of a 5 KB method that must track both game versions and every mod; the hoistable calls are ~8% of the cost (§4.1).
- **Shadow-then-apply on clones (docs/208 §4.2): no**, for D5 and D6; superseded by the term-log idea, which fails for the reasons above.
- **Rider `EarlyOutMotiveUpdate` (docs/208 §4.3):** rode on the clone path; without M3 it has no carrier. Its self time is 0.3 ms/s (`jit-hot.csv`). Dropped.

## 5. Household rule check

| Design | Touches household Sims or lots | Result vs vanilla | Gate needed |
|---|---|---|---|
| Z1, Z2 | yes, all lots | equal, by the argument in §3.1 / §3.2 | none (proven-exact speedup); `verify` switch in test runs |
| M1 | yes, all Sims | equal while the add stamp is complete | none; `verify` switch counts mismatches |
| M2 | yes, all Sims | equal by the MonoJit per-method proof | the lane's own gates |
| M3, M4 | yes | equal only if every stamp is complete | not provable to the standard the rule asks: not built |
| M5 | unseen only | not equal | would need the household and selected-Sim exemption; not built |

No design here uses `HouseholdToo`.

## 6. Order of work

1. **Z1** with its probe counters and `verify` switch, default off. ~45 min build, one 10-minute BP or IP job (rewind from an existing session where possible).
2. **M1** in the same build. ~1 h.
3. **M2 steps 1-2** (30 min, static, with docs/537): decide go or stop.
4. Z2 only if step 1's probe says the table is seldom empty.
5. Update docs/129 §2.3 and §2.5 and docs/208 §4.2 and §9.3 with a pointer here when the first result is in. PLAN.md gets its line when Z1 is built, not for this note.

Game runs listed separately: 2 jobs of ~10 min (Z1 + M1 verify, then per-arm ms/s), no clean night.

## 7. Limits

- Sample files mix builds, switch sets and MonoJit states; absolute ms/s are ±30%. Callee splits are steadier.
- Per-call costs (hook dispatch, one `TryGetValue`, one `ICollection.Count`) are not measured in game; the gain ranges rest on them.
- The table-empty share (Z1) and the buff-add rate (M1) are unknown until the first job.
- EA 1.69 was not read for this note; every hook needs its IL hash there.
- M2 was not checked against the MonoJit source, only against docs/537 and the two CSVs.

## 8. Ledger

docs/51 §490.

## 9. Build (2026-11-16, branch `feature/z1-m1-inplace-1116`, NOT RUN)

Z1 and M1 are built in `mods/ts3fixes/src/WriterMemos.cs`, rules in `TickMemo.cs` (`TableKeeper`, `AbsentMemo`), model checks in `tools/selftest/hotspots` (a mutated empty rule and a missing post-add bump both fail them). Both default off; no scope gate (exact for every Sim and lot). Ledger docs/51 §491.

| Design | Switch (tuning) | Verify switch | Counters (fixes slots, agent names) |
|---|---|---|---|
| Z1 | `zdemptyout` (`kZombieDanceEmptyOut`, off) | `zdemptyoutverify`: the game's method also runs on every early-out; it must answer false and leave the table empty | 1064 `zdemptyout_stereo_calls`, 1065 `zdemptyout_stereo_empty`, 1066 `zdemptyout_timeout_calls`, 1067 `zdemptyout_timeout_empty`, 1068 `zdemptyout_mismatches` |
| M1 | `decaymemo` (`kDecayMultMemo`, off) | `decaymemoverify`: the game's method also runs on every memo hit; it must return 1 | 1060 `decaymemo_hits`, 1061 `decaymemo_misses`, 1062 `decaymemo_mismatches`, 1063 `decaymemo_buffadds` |

**M1 stamp proof [verified].** An IL scan of every method body in the ten game assemblies (`game-copy/assemblies`, 165,766 bodies) and in simler90's core copies (127,829 bodies) for every `ldfld` / `ldflda` / `stfld` of `GenericManager<BuffNames, BuffInstance, BuffInstance>.mValues` and of the open generic field: the only calls that add to a buff manager's table are the `Dictionary.Add` in the two private `BuffManager.AddBuff` overloads. The other sites read (`OnLoadFixup`, `HasElementFromOrigin`, `StoreBuffsForTravel`, `HasElement`, `GetElement`, `get_List`, `get_Count`), remove (`RemoveElement`), or store a new empty table (`.ctor`, `RemoveAllElements`). `get_List` hands out the read-only value collection; no site passes the table itself out. No string literal names `mValues` (no reflection on it in the game). `managed-callgraph.ts subtypes BuffManager`: no subclass, so `GetElement(ulong)` (virtual in `GenericManager`) is the base `TryGetValue` and absent = `!HasElement`. NRaas source: no buff `mValues` access (only the `SkillManager` and `TraitChipManager` tables). A load or travel gives a new manager object (a reference miss). Both add hooks bump the generation before and after the game's body, because `AddBuff(BuffInstance)` runs `OnAddition` before the `Add`. Not covered: a mod that writes the table by reflection or IL of its own, or replaces `AddBuff` wholesale; `decaymemoverify` counts both.

**Z1 table writers [verified].** The same scan for `ZombieDanceTimeOut.mZombieDanceTimeOutOnLots`: written only by the private constructor; adds only in `GetTimeOutOnTheLot`, `ResetTimeOutOnTheLot`, `SetStereoOnTheLot`; removals in `PostWorldStartup`. So the reference is kept per `ZombieDanceTimeOut` instance and `Count` is asked per call. Callers: one each (`Lot.get_HasZombieDanceStereo`, `Lot.UpdateNumZombiesOnLot`).

**Hook cost.** A switch read is a string-keyed dictionary lookup, about what Z1 saves, so both hooks re-read their two switches every 256 calls (a test flip takes effect within 256 calls; both paths are exact). Per call: `OnWorker()`, one counter, one reference compare and an `ICollection.Count` (Z1) or one property read (M1). The two `AddBuff` hooks are installed whenever the patch applies and bump the generation with the switch off too.

**Not checked.** EA 1.69 IL of the hooked methods (no EA 1.69 assemblies in `game-copy`; the hooks fall through to the game's method for every non-empty or non-hit case, and the verify switches cover an EA correctness run). Installed third-party mod assemblies beyond simler90 and the NRaas source.

**Queues.** `tools/harness/queues/z1-m1-correct-1116.json` (RUN FIRST: BP + IP, 10 min each, verify switches on, Z1 probe) and `z1-m1-timing-1116.json` (needs a quiet machine; smoke + in-load A/B per design and world; run only after the probe shows >= 80% empty on both worlds, or drop the two Z1 jobs). Build `ts3fixes.z1-m1-1116` + `ts3agent.z1-m1-1116` over native pair `master-1116b`.
