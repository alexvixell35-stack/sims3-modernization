# 97. Autonomy: cheapest safe path to cut sim-thread scoring cost

2026-10-18. Static only: managed samples already on disk, the decompiled script DLLs (`game-copy/decompiled/`, not copied here) and `tools/managed-callgraph.ts`. No game, probe or queue run; nothing built. Builds on docs/59 (autonomy call graph, stage A-D design), docs/75 (entry list: meta = 80% of manager scoring), docs/78 (call graph, leaf tags), docs/64 (ThreadedObjects stalls), docs/74 (script hot spots), docs/76/79 (snapshot change hooks) and the routing result of 2026-10-18 (A10 moved planning off the sim thread and the sim waited +49 ms/s more on shared state, `docs/data/results/2026-10-18-queue-clean-night-2.md`).

Tags: [measured] = counted in the managed samples (method in §2); [verified] = read in the decompiled source or the IL index; [inferred] = reasoning, not checked live. Ledger rows: docs/51 §30.

## 1. Answer

- **Autonomy is a steady cost, not a hitch source.** Autonomy searches are 5.5% of managed samples (~55 ms/s), but only 3-5% of samples inside long ThreadedObjects slices against 7-11% far from them (lift 0.3-0.5, docs/64 §1) [measured]. The local search never yields and the meta search yields after at least 5 objects and 1 ms (docs/59 §1.3), so a search adds a few ms to the slice it runs in, every tick. Any autonomy option therefore has a small hitch cap; its value is a lower ThreadedObjects baseline.
- **The off-thread design (docs/59 stage B/C) does not survive the routing lesson.** The meta scorer, which holds the eligible time (docs/75), reads managed lot, Sim and situation state, writes a per-Sim room cache on a miss, queries native lot object lists and allocates on almost every pair. The gap 10 dirty tracking covers native per-object rows only, and only 4.1% of meta scoring samples touch those rows [measured]. A worker could not run on that snapshot; it would read shared state in place and pay the `[MonoThread]` fixed cost plus a live re-score at apply. Net -16 to +7 ms/s, hitch gain about 0 [inferred]. **Parked**, together with 12d/12e/12g/12h and, for autonomy, 10e-10g.
- **The shortest safe path is on the sim thread: exact memos on two hot lookups.** `Sim.IsAllowedInRoom` is 1.01% of all managed samples (20% of scoring) and 76% of that is the hit path of the game's own per-Sim cache (lot lookup, a version lookup keyed by `Type`, dictionary reads), not the room flood [measured]. `MetaAutonomyManager.GetTuning` is 0.48% (enum boxing in a dictionary and list scans) and depends only on the venue type and the current world [verified]. A memo scoped to one search slice (same task, same tick, inside the game's pair scoring) for the first, and a memo per world for the second, are exact by construction [inferred, verify mode checks]. Realistic gain 6-10 ms/s, cap ~15 ms/s; build ~1 day; risk low. Build spec in §7 (**AM-1**). It also adds the missing measurement: a histogram of scoring time per search slice, which decides whether time-slicing is ever worth doing.
- **Corrections to docs/59 §3.7 stage A:** A1 (per-search IC memo) is capped at ~2 ms/s, not 5-15: the hot-set ICs are 4.1% of scoring samples [measured]. A5 (`CalculateNumSimsAtHomeOrGoingHome` once per tick) saves ~0 for the manager, which runs one search per tick [verified loop shape, docs/59 §1.1].

## 2. Status of gaps 12 and 10

| Step | Status | Notes |
|---|---|---|
| 12a call extractor | done (docs/78) | index at `game-copy/analysis/callgraph/` in the main checkout (present, ~36 MB) |
| 12b leaf tags | done (docs/78 §2, §4) | top 5 meta definitions UNSAFE through 2 guarded paths; with those cut, STATIC_WRITE on the RNG |
| 12c entry list | done (docs/75) | meta 80% of manager scoring; local-only stage C caps f at 0.20 |
| 12d virtual dispatch | open, not started | needed only to close the venue body for 12e |
| 12e allow-list v0 | open; `docs/data/autonomy-allow.csv` does not exist | needs 12a-12d; only stage B uses it |
| 12f-12h | open | after 12e |
| 10a-10d | done (docs/76, docs/79) | epoch, destroy/hide, parent and position writers mapped; no detours needed |
| 10e verify counters | open | short game run with the 10e handler |
| 10f dirty-set design | open | after 10e |
| 10g tracker shadow | open | needs 10a, 10f and stage B's snapshot builder |

With §6's ranking, none of 12d-12h or 10e-10g is on the autonomy critical path. 10e-10g stay useful for any object snapshot (routing snapshot I/O study, PLAN "Next" after the A10 wait split), not for autonomy.

## 3. Where autonomy time goes (53 sample files)

Method: every `probe-out/profile/*managed*.jsonl` in the main checkout (53 files, BP/IP/SV runs 2026-10-12 to 2026-10-18, including the clean-night runs; 45,570 samples, 1% ≈ 10 ms/s as in docs/75). A sample counts for a method when the method is a frame of its stack. docs/75 used the 26 files that existed on 2026-10-16; the shares agree (scoring 4.76% vs 4.75%; meta 66% of scoring vs 65%) [measured].

| Bucket | Samples | Share of scoring | Share of all samples |
|---|---|---|---|
| `Autonomy.ScoreInteractionsOnObjects` (all searches, manager and side) | 2,512 | | 5.51% |
| `AutonomyManager.Simulate` (manager task) | 2,464 | | 5.41% |
| `Autonomy.CalculateScore` (per-pair test and score) | 2,169 | 100% | 4.76% |
| of which meta (`ScoreInteractionsForMetaObject` on the stack) | 1,429 | 66% | 3.14% |
| of which local (`ScoreInteractionsForLocalAutonomy`) | 659 | 30% | 1.45% |
| `Autonomy.CheckAvailability` | 695 | 32% | 1.53% |
| `Sim.IsAllowedInRoom` | 439 (459 all callers) | 20% | 1.01% |
| of which the room flood `Sim.RecalculateAllowedRooms` | 106 | 4.9% | 0.23% |
| of which the cache-hit path (lot lookup 65, `Type`-keyed version lookup 89, dictionary reads 70, own frame 22, rest) | 333 | 15% | 0.73% |
| `Lot`/`RabbitHole.HowMuchItWantsSimToCome` (venue score) | 343 | 16% | 0.75% |
| of which `GetDesiredNumSimsForMetaObject` | 156 | 7.2% | 0.34% |
| `InteractionDefinitionUtilities.CommonTests` | 293 | 14% | |
| `TombRoomManager` tomb and fog checks | 125 | 5.8% | 0.27% |
| `MetaAutonomyManager.GetTuning` | 102 (218 all callers) | 4.7% | 0.48% |
| `Lot.GetHoursOfOperation` (hoursmemo, docs/74, built) | 61 (138 all callers) | 2.8% | 0.30% |
| `ScubaLotTests` | 63 | 2.9% | |
| docs/59 §3.4 row ICs (parent id, position, outside, roof, task, hidden flags) on the stack | 88 | 4.1% | 0.19% |
| manager, not scoring: `CalculateNumSimsAtHomeOrGoingHome` | 66 | | 0.14% |

Reading [verified in source unless tagged]:
- `IsAllowedInRoom` is asked twice per pair (once in `CheckAvailabilityOnLot`, once in `CommonTests`), and for a pair on another lot it first asks the same question for room 0 of the actor's own lot. Each call resolves the lot, reads the lot's portal object cache version through a dictionary keyed by `Type`, then reads the per-Sim cache. So most calls repeat a lookup whose answer cannot have changed since the previous call in the same search slice.
- `GetTuning` scans the venue's tuning list and tests each entry's world restriction lists with `List.Contains` on enums (boxing; `System.Enum.Equals` is one of the top leaves under scoring, 62 samples). The tuning table is written only by `LoadMetaAutonomy` at startup; the answer depends on the venue type and the current world name and type.
- Each lot carries 2 meta ads (`VisitLot`, `VisitCommunityLot`), and one usually fails its `Test` early, so the venue score runs about once per lot per search: a per-search memo of `HowMuchItWantsSimToCome` would rarely hit. Its Sim-independent part (`GetDesiredNumSimsForMetaObject`) changes with the clock (`hours24`) every tick, so only a quantized cross-search cache could reuse it (R3) [inferred].

## 4. Stage B/C against the routing lesson

The A10 lesson: moving work whose inputs are shared and written by the sim thread moves the time into waits on that shared state. What a worker-side scorer would touch, from the samples and the closures (docs/78 tool, depth 8, guarded paths cut as in docs/78 §4):

| Shared state | Who writes it | Share of meta scoring | Snapshot-able? |
|---|---|---|---|
| Per-Sim allowed-room cache (`RecalculateAllowedRooms` on a miss allocates and stores on the Sim; room-restriction callbacks are delegate calls) | the scorer itself on a miss; doors, lock changes, resort check-in, lot moves on the sim thread | 4.9% of scoring is the flood | no: a W-class write; the worker would abort on every miss |
| Lot portal object cache and its version (`Dictionary<Type, …>`) | lot refresh, venue open/close, room callbacks | part of the 15% hit path | in place only |
| Native lot object lists through `Query_CountObjectsOnLot`, `Query_GetObjectsOnLot`, `Query_GetObjects` (neighbour check, callbox search, pet picker) | native world, sim thread | 7.4% of meta samples | not in docs/59 §3.4 rows ("other types → abort"); needs per-lot per-type counts, not designed |
| Lot counters (`NumSimsGoingToOrAtThisLot`, `IsActorHereOrComingHere`), situations, gig schedule, location opportunities, resort data | Sim tasks between yields | most of the venue score (16%) | managed copy per lot per search; docs/59 §3.3: a managed builder never breaks even |
| RNG (`RandomGen.gRandomGen`, docs/78 §4) | everyone | base meta score | no: a worker draw shifts the seeded RNG stream (docs/27) |
| GC allocator: string concat in the day-of-week read of `GetIntensity`, enum boxing in tuning lookups, list copies, enumerators | both threads | nearly every meta pair [inferred] | no: P3 gated lock on every allocation of the sim thread (docs/30 §8: 0.6-2.0%) |
| Per-search statics: `InteractionCheckTable` pool, `sUpdateTimer`, `mBestScores` | the search | all | worker copies needed |
| docs/59 §3.4 native rows (position, parent, outside, roof, destroyed/hidden) = what gap 10 tracks | native setters (docs/76, 79) | 4.1% of meta samples | yes (gap 10) |

Verdict [inferred]:
- **Not on a snapshot.** 96% of the meta scoring samples never touch what gap 10 tracks; the rest of the inputs are managed lot-level state with no dirty tracking and native per-type object lists. A snapshot that covers them is a new managed lot snapshot per search (docs/59 §2.5 kind 2), which docs/59 §3.3 already rules out on cost.
- **In place only, so the waits move.** The only workable form is docs/59 §3.4's: the worker reads managed state in place while the sim thread runs native code. Then the sim thread pays (a) the `[MonoThread]` fixed cost on every allocation and interpreter entry, 7-25 ms/s (docs/30 §8), (b) a live re-test and re-score of the worker's top-10 at apply, ~5-10 ms/s, and (c) every room-cache miss and native-query refusal falls back to a full sim-thread search. That is the A10 pattern: the work leaves, the shared-state cost stays on the sim thread.
- **Numbers.** Meta scoring is ~31 ms/s; after AM-1 ~24 ms/s; at f ≈ 0.8, ~19 ms/s eligible against 12-35 ms/s of fixed and apply cost: **-16 to +7 ms/s**, hitch gain ~0 (§1). Stage C stays no-go. It becomes worth a second look only if `[MonoThread]` lands for another load (amortizing C_mt) and AM-1/R3 have already taken the cheap part.
- **Stay on the sim thread** for autonomy; make each search slice cheaper (§5, §7).

## 5. Sim-thread alternatives

- **Memo inside one search slice (exact).** Valid while the same task runs in the same tick and only the game's pair scoring runs; ends at the meta yield. Targets: `IsAllowedInRoom` (repeated per pair and per remote lot), and `TombRoomManager` checks in the local phase (objects carry several interactions). `HowMuchItWantsSimToCome` does not repeat within a search (§3), so it is not a target.
- **Memo per world (exact).** `GetTuning`: venue type → tuning entry, cleared when the world name or type changes (read once per tick).
- **Cache across searches (near-exact).** Sim-independent venue parts (`GetDesiredNumSimsForMetaObject` per venue and sim-minute; tomb and scuba lot facts per lot and world epoch). Behaviour changes by at most one sim-minute of the desired-population curve; never cache `NumSimsGoingToOrAtThisLot` (herding: every Sim would pick the same venue).
- **Score fewer candidates.** Skip whole lots by a cross-search prefilter (closed venue, denied lot). Behaviour-changing; pass rate per lot is not measured. Parked.
- **Cheaper meta visits.** The first two items are the cheap part of the meta visit; the venue score itself is a single call per lot per search.
- **Time-slicing.** Yields in the local loop (docs/04a #2) or a 0.3 ms meta slice (docs/04a #10). Hitch cap ~0 by docs/64; a shorter meta slice spreads a search over 3x more ticks, so Sims decide later (docs/04a #7, idle-looking Sims). Only if AM-1's slice histogram shows scoring slices of 5 ms or more in more than 1% of slices.

## 6. Ranked options

Gain caps are averages over steady play; "hitch cap" is what the option can take off a long ThreadedObjects slice. Effort in Claude-with-Rodney days. All gains [inferred] from the §3 shares.

| # | Option | Avg gain (cap / realistic) | Hitch cap | Risk | Effort | Verdict |
|---|---|---|---|---|---|---|
| R1 | **AM-1**: exact slice memo of `IsAllowedInRoom` + world memo of `GetTuning` + slice histogram (§7) | 15 / 6-10 ms/s | ≤ ~1.5% of a long slice (autonomy ≤ 5% of it, these two ~30% of search time) | low: exact by construction, verify mode | 1 d build + 1 clean night | **next** |
| R2 | rest of docs/59 stage A: A1 IC memo, A3 lot Sim array, A4 meta list copy, A5 home count | ≤ 2 + < 1 + < 1 + ~0 ms/s | ~0 | low | 1 d | skip (corrected caps, §1) |
| R3 | near-exact cross-search venue parts (desired population per venue per sim-minute; tomb and scuba lot facts per epoch) | 7 / 3-5 ms/s | ~0 | medium (small behaviour change) | 1-1.5 d | after R1 data, if wanted |
| R4 | room-flood misses (0.23%, 2.3 ms/s): why does the per-Sim cache miss this often? | 2.3 ms/s | ~0 | unknown | 0.5 d after R1's counter | decide from AM-1 counter 96 |
| R5 | yields in the local search | ~0 avg | ~0 (docs/64) | medium-high (per-search state across a yield) | 1-2 d | only if the histogram demands |
| R6 | 0.3 ms meta slice | 0 avg | ≤ 0.7 ms per slice | medium (decision latency) | 0.25 d | only if the histogram demands |
| R7 | fewer candidates (lot prefilter across searches) | unknown | ~0 | medium | 1-2 d + measurement | parked |
| R8 | stage B shadow (snapshot + re-score on the sim thread) | 0 by design | 0 | medium | 12d+12e+12g (~250-280k tokens) + native builder + a managed lot snapshot not yet designed: 2-3 weeks | parked (§4) |
| R9 | stage C meta worker | -16 to +7 ms/s | ~0 | high | 4-6 weeks incl. `[MonoThread]` steps 2-5 | no-go (§4) |

## 7. Build spec: AM-1 (ts3fixes, all off by default)

**Scope.** Managed hooks in ts3fixes with the existing `Patch(target, method, args, hook, replacement, stub)` pattern (docs/74 `ScriptHotSpots.cs` is the model: `HoursMemoHook`, `TickStamp`, `VerifyNow`). No native code, no game method body copied.

**Files to touch:**
- new `mods/ts3fixes/src/AutonomyMemo.cs` (hooks, apply function called next to `ApplyScriptHotSpots`);
- `mods/ts3fixes/src/TickMemo.cs`: game-independent `RoomMemo` (keyed table) and `SliceHistogram` so the selftest compiles them alone;
- `mods/ts3fixes/src/Instantiator.cs`: tunings `kAutonomyRoomMemo`, `kAutonomyTuningMemo`, `kAutonomySliceTime` (false);
- `mods/ts3fixes/src/Fixes.cs`: `ShortcutOn` cases (`autroommemo`, `auttuningmemo`, `autslicetime`; `autroommemoverify`, `auttuningmemoverify` return false like the other verify switches), counters array size and comment block;
- `mods/ts3agent/src/plugin/Handlers/PatchHandlers.cs`: array size and counter names;
- `docs/registry.md`: reserve the slots below before coding (next free today is 90; take the next free range at build time);
- `tools/selftest/hotspots/HotSpotsTest.cs`, `tools/harness/repros.json`, `tools/harness/queues/autonomy-memo.json`, PLAN.md resume section (same commit as the build).

**Hooks:**

| # | Target (overload) | Kind | Behaviour |
|---|---|---|---|
| H1 | `Autonomy.CalculateScoreAndAddToCandidates(InteractionObjectPair)` (private, instance, void): the per-pair entry of local, meta and side searches (docs/59 C1) | scope | Only when `autroommemo` or `autslicetime` is on, else straight to the game. Read `Simulator.TicksElapsed()` and `Simulator.CurrentTask`; if (this `Autonomy`, tick, task) differs from the open segment, close it (histogram) and open a new one (table generation +1). Mark "inside a pair of this segment" (save the previous mark for nesting), run the game's method in try/finally, restore the mark, note the exit time. |
| H2 | `Sim.IsAllowedInRoom(ulong, int)` (public; the `int` overload calls it) | exact memo | Outside a pair or switch off: the game's method. Inside: key (Sim reference, lot id, room id) in a 64-slot open-addressing table stamped with the segment generation. Hit: answer; with `autroommemoverify`, 1 hit in 8 also runs the game's method, counts a mismatch and returns the game's answer. Miss: game's method, store. Exceptions pass through, nothing stored. Full table: no store. |
| H3 | `Sim.RecalculateAllowedRooms(ulong, int)` (private) | count only | Counts floods inside a pair (R4 input); always runs the game's method. |
| H4 | `MetaAutonomyManager.GetTuning(MetaAutonomyVenueType)` (public static) | exact memo | Switch off: the game's method. Once per tick (`TickStamp`) read the current world name and type; on a change clear the memo. Array indexed by the enum value (size from the enum's values at first use; out of range: the game's method). Store null answers too. With `auttuningmemoverify`, 1 hit in 8 compares by reference with the game's answer. |
| H5 | histogram (in H1 at segment close) | measure | Segment scoring time = open to last pair exit, from one `Sims3.SimIFace.StopWatch` (the clock the game's own `sUpdateTimer` uses): buckets < 1, 1-2, 2-5, 5-10, ≥ 10 ms, and the max in µs. One segment = one search slice: the local phase plus the first meta slice, or one later meta slice. |

**Why exact** [inferred; verify mode and I-97-3/5 check it]:
- H2: the game's own per-Sim cache is the source of the answer; it changes only through a portal cache version bump (venue open/close, room callbacks, lot refresh, a portal object changing lot) or an explicit recalculation (door lock changes, resort, lot moves). None of these run inside a pair's test and score: the closure has no YIELD and reaches EVENT only through the two guarded paths of docs/78 §4. The memo lives only inside pairs of one task in one tick. A meta yield (`Sleep(0)`) resumes on a later tick (docs/04a §1, inferred), so the next pair opens a new segment.
- H4: the tuning table has one writer (`LoadMetaAutonomy`, startup); the world is re-read every tick.

**Counters** (ts3fixes slots; shown as base+n, base = next free at build time, 90 today):

| Slot | Meaning |
|---|---|
| +0 | pairs entered under H1 |
| +1 | segments opened |
| +2 / +3 | room memo hits / misses |
| +4 | room verify mismatches |
| +5 | room calls outside a pair (switch on) |
| +6 | room floods inside a pair (H3) |
| +7 / +8 | tuning memo hits / misses |
| +9 | tuning verify mismatches |
| +10 | tuning clears on world change |
| +11..+15 | slice buckets < 1, 1-2, 2-5, 5-10, ≥ 10 ms |
| +16 | max slice µs |

**Offline proof (before any game run):**
- selftest `tools/selftest/hotspots`: `RoomMemo` against a `Dictionary` reference model over 10,000 random store/lookup/generation-bump operations (distinct Sim references, room -1 and 0 kept apart, full table never stores); segment open on any change of (owner, tick, task), nested mark save/restore; histogram bucket edges (0.999, 1.0, 4.999, 5.0, 10.0 ms).
- `node tools/managed-callgraph.ts query` on the hooked methods as in §3/§4 (no YIELD; DELEGATE only the room-restriction callbacks inside the flood); `node tools/check-docs-code.ts`.
- Build ts3fixes; the first load's `ts3fixes.applied` must list the four patches ok (a patch reports "already compiled" when the method ran before patch time; then that hook is off).

**Repro** `autonomy-memo` (repros.json, as `hours-memo`): `patches` (autroommemo, auttuningmemo, autslicetime on, both verify switches on) → `spawn_sims` 5 (townies with meta autonomy) → `set_speed` ultra 90 s → `remove_sims` → `patches` readout. Expect: hits > 0 on both memos, mismatches 0, slices counted.

**Live jobs** (`tools/harness/queues/autonomy-memo.json`, draft; timing jobs only in Rodney's clean-night queue):
1. `bp-autmemo-verify`, `ip-autmemo-verify`: 5 min smoke, verify on, repro `autonomy-memo`. Pass: both mismatch counters 0, hits > 0, applied ok, no script errors, possible stuck Sims logged only.
2. `bp-autmemo-ab`, `ip-autmemo-ab`: 10 min, one load each, `--ab autroommemo,auttuningmemo`, verify off, `autslicetime` and `hoursmemo` on in both halves (`hoursmemo` also removes `GetTuning` calls; keep it equal on both sides). Pass: managed sampler `IsAllowedInRoom` share ≤ 0.4% (from 1.01%) and `GetTuning` ≤ 0.1% (from 0.48%) in the on halves, ThreadedObjects ms/s not higher, machine load line clean (else rerun).

**Decisions after the runs:** gain ≥ 5 ms/s and 0 mismatches → default-on candidate (Rodney decides). Slice histogram: ≥ 5 ms slices in more than 1% of segments → design R5/R6; else autonomy hitch work is closed. Counter +6 high → R4. R3 only if another 3-5 ms/s is wanted.

**Not built here:** tomb and scuba memos (small, R3 batch); any cross-search cache; anything off the sim thread.

**Built 2026-10-30 (docs/122 §14.24):** H4 as ts3fixes `venuememo` (per world pair, read on every call; null answers not kept), together with a typed scan of the venue score's rain/hail list. H1-H3 and H5 are not built. R3 has no exact key (curves linear between points, many inputs), so it stays open.

## 8. Gaps

- Pairs per search, segments per search and lots per meta search are not measured; AM-1's counters give the first two (pairs/segments), lots per search stays inferred.
- The hit rate of H2 (~70% assumed: two calls per pair plus the repeated own-lot question) and the cost of a memo hit against the game's hit path (a table probe plus two cheap ICs in H1 per pair) are estimates; the A/B settles them.
- Whether `Sleep(0)` can resume a task in the same tick is inferred (docs/04a §1); H2's verify mode would show it as mismatches.
- The worlds are the BP/IP/SV test saves, unmodded script set; NRaas-style definition replacements do not touch these four methods [inferred: they replace interaction definitions, not `Sim`/`Autonomy`/`MetaAutonomyManager` methods].
