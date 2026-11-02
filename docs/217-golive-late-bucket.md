# 217. Go-live "late" bucket: what it is made of and how to move it to the worker

Static and data analysis only (2026-10-31): no build, no game run. Inputs: `node tools/harness/mono-cost-report.ts --tag golive-g7` (7 Dry jobs of 2026-10-30: BP and IP `satb`, `satb-2`, `nosatb`, BP crowd) and `--tag golive-act` (2 acting jobs of 2026-10-31: BP, IP), the `autshadow.jsonl` records of those jobs (live-drop only, no per-object detail), docs/216 and its CSV. Code read: `mods/ts3fixes/src/AutLive.cs` (`LiveMeta`, `LiveSelect`, `LiveB14`, `TryRequest`), the game's B13 `Autonomy.ScoreInteractionsForMetaAutonomy` and `YieldIfPossible` in the local decompile (read, not quoted). Follows docs/216 §6 item 3.

## Summary

- **"Late" is one thing: the rest of the request's object list after the game's B13 takes its first time-slice yield** [verified in the code, measured]. B13 restarts a static millisecond stopwatch at entry and, after every 5th meta object, yields (`Simulator.Sleep(0)`) when more than 1 ms has passed. `LiveB14` hands every object visited in a later tick than the resume to the game's B14. Objects added after the cut, objects outside the request, a second B13 pass, KeepPartial remainders and non-allow-listed definitions are **not** in it (§1).
- **Size** [measured counts, inferred cost]: 59-72% of B14 calls in used searches; 28 (BP), 46 (IP), 29 (crowd) of 47-66 objects a search; 150 / 247 / 271 calls/s; at vanilla per-object cost **~7-8 ms/s BP, ~11-12 IP, ~14-15 crowd** of the game's own scoring (§2).
- **Movable part is smaller than the bucket:** a consumed object still pays its off-list ads through the game's C1 (late objects carry 0.8-0.9 off-list ads each) and the hook. Consuming the late objects moves **~1.5-3 ms/s BP, ~2.5-4 IP, ~3-5.5 crowd** [inferred] - about the size of all worker aborts together (docs/216).
- **The worker already has the answers:** late objects are in the request and were scored at the cut. No later, second or delta cut, no list extension and no extra wait tick is needed; each of those moves ~0 ms/s on this data (§3, L4).
- **First build: L1 "late consume"** - at the first B14 of each later B13 slice, re-run the resume checks; per late object re-query `ObjStale` and consume as in the first slice. ts3fixes only, ~30-60 min + one G7 Dry job (§4).

## 1. What "late" is

### 1.1 The rule [verified in the code]

- `LiveMeta` (B13 hook prefix) posts, sleeps until the worker's result is ready (2.0 ticks a search in every job [measured]), runs `LiveCheck` + `LiveSelect`, stores `ResumeFrame`, then calls the game's B13 (`MetaOrig`).
- The game's B13 restarts its static `sUpdateTimer` (millisecond `StopWatch`) once at entry. In each of its four B14 loops (public meta objects + `mLotsKnownAbout`, then `LotCurrent`, `LotHome`, `CareerLocation`) it counts objects and, when the count is a multiple of 5 and the stopwatch reads more than 1 ms, calls `YieldIfPossible` (one `Simulator.Sleep(0)`, interrupted/destroyed returns) and restarts the stopwatch. Inspection-window searches never yield.
- `LiveB14` checks, in this order: object not in `ObjIndex` -> `notInReq`; `L.Late` or current frame != `ResumeFrame` -> `late` (sticky for the rest of the search); then the object's resume-time reason (`invalid`, `acb`, `stale`), `SameAds` (`ident`), else consume.

So `late` = every in-request object B14 visits after the first yield. Because the `late` test comes before the per-object reasons, late objects would otherwise have split like the first slice's objects.

### 1.2 What is not in it [measured / verified]

| Candidate part | In `late`? | Evidence |
|---|---|---|
| Objects added or changed after the cut | no | new objects land in `notInReq`: 0 in all 9 jobs; changed objects are `stale` (0.04-0.24 a search) only in the first slice [measured] |
| Objects outside the cut's lists (other lots, tier-2 Sims, unplaced/inventory) | no | `notInReq` 0: the request lists everything B13 visits. Tier-2 Sim reads abort on the worker (`simstale`) -> `invalid`, first slice only [measured, docs/216 §1] |
| Definitions not allow-listed | no | per-ad rule (docs/124 §16.2): off-list ads of a consumed object go through C1 (`offList` pairs); only an off-list implementor makes the object `invalid` [verified] |
| Second B13 pass | no | `metaCalls == metaTicks` in all 14 jobs: one B13 call per tick (docs/216 §4) [measured] |
| KeepPartial overrun remainders | no | unfinished objects carry `stop` -> `invalid` (first slice) or `late`; overrun searches are 0.1-0.2% (`KeepPartial 1: 137 / 611 searches`) [measured] |
| Dry artefact (game scores everything in G7, so it yields as vanilla) | mostly no | acting yields almost as early: first slice 17.4 -> 19.3 objects BP, 18.8 -> 20.8 IP (Dry -> acting) [measured] |

### 1.3 Why the first slice still ends after ~19-21 objects in acting [inferred]

Consume did not make the first slice much longer, so the consumed objects are not cheap on the sim. The acting counters explain it: per search the first slice holds band-1 live C1 for 3.5 (BP) / 2.5 (IP) mostly passing pairs (`Test` + venue score), 7.5 / 8.7 off-list C1 calls, and 3.3 / 3.8 `invalid` objects scored in full. At ~34 us an average pair and 100-150 us a passing pair that is already ~0.8-0.9 ms, close to the 1 ms slice. The late objects hold few candidates: candidates over the whole request are 4.0 BP / 2.6 IP a search and band 1 sent 3.5 / 2.5 of them in the first slice [measured], so at most ~0.5 / 0.1 passing selected pairs a search sit in late objects.

## 2. Numbers per job [measured counts; cost inferred]

Seconds = eligible searches / their rate. Vanilla per-object cost = Mode R reference per request / objects per search x 0.65-0.70 (vanilla meta B14 ~0.6-0.7 x R, docs/124 §16.5).

| Job | used searches/s | objects a search | first slice (consumed / invalid / stale) | late a search | late % of B14 | late calls/s | vanilla us / object | late at vanilla, ms/s | off-list C1 a search |
|---|---|---|---|---|---|---|---|---|---|
| G7 BP satb-2 (Dry) | 5.19 | 47.6 | 17.4 (14.1 / 3.1 / 0.15) | 30.2 | 63.4 | 156 | 48-52 | 7.5-8.1 | 31.7 (all objects) |
| G7 BP satb (Dry) | 4.79 | 47.5 | 16.8 (13.4 / 3.2 / 0.14) | 30.8 | 64.7 | 147 | 52-56 | 7.7-8.2 | 31.6 |
| G7 BP nosatb (Dry) | 4.68 | 47.6 | 16.8 (13.6 / 3.1 / 0.15) | 30.8 | 64.7 | 144 | 51-55 | 7.3-7.8 | 31.7 |
| **act BP** | 5.30 | 47.5 | 19.3 (15.8 / 3.3 / 0.24) | 28.2 | 59.4 | **150** | 48-52 | **7.2-7.7** | 7.5 (consumed objects) |
| G7 IP satb-2 (Dry) | 5.14 | 66.5 | 19.4 (15.6 / 3.7 / 0.05) | 47.1 | 70.8 | 242 | 44-48 | 10.6-11.5 | 50.9 |
| G7 IP satb (Dry) | 4.80 | 66.3 | 18.3 (14.7 / 3.6 / 0.04) | 48.0 | 72.4 | 230 | 46-50 | 10.6-11.4 | 50.7 |
| G7 IP nosatb (Dry) | 4.84 | 66.2 | 18.8 (15.2 / 3.5 / 0.04) | 47.4 | 71.6 | 229 | 46-50 | 10.6-11.4 | 50.7 |
| **act IP** | 5.42 | 66.3 | 20.8 (16.9 / 3.8 / 0.05) | 45.6 | 68.7 | **247** | 45-48 | **11.1-12.0** | 8.7 (consumed objects) |
| **G7 crowd (Dry)** | 9.25 | 46.2 | 16.9 (12.8 / 4.0 / 0.12) | 29.3 | 63.5 | **271** | 51-55 | **13.7-14.8** | 30.4 |

- Off-list ads per object: whole search 0.67 BP / 0.77 IP (Dry); consumed first-slice objects 0.47 / 0.52 (acting); so late objects carry ~0.78 BP / ~0.87 IP [inferred by difference]. Allow-listed classified pairs are ~0.8 / 0.7 per object (mono-cost11, docs/124 §15.2), so allow-listed ads are ~40-55% of a late object's vanilla cost [inferred].
- If the late objects were consumable at the first slice's rate (81-82% BP/IP, 76% crowd), the move per late call is consumable share x (allow-listed share x vanilla cost - hook 3-6 us): **BP 1.6-3.1, IP 2.4-4.2, crowd 3.0-5.6 ms/s** [inferred].
- Not part of `late`, but as large: 41-46% of eligible searches are not posted at all (`Sample 1`, `Begin busy` 0), so the "not posted" reason is mostly `TryRequest`'s `CanRunAnyMetaAutonomy` / game-search guard, i.e. searches with little or no meta work [inferred, no counter splits it]. One counter per `return null` in `TryRequest` would settle it.

## 3. Levers

| # | Lever | ms/s moved BP / IP / crowd | Sim cost added | Correctness vs Mode R | Satb / cost guard | Effort | Proof job | Verdict |
|---|---|---|---|---|---|---|---|---|
| **L1** | **Late consume**: keep consuming after a B13 yield, with the resume checks re-run per slice and `ObjStale` re-queried per late object (§4) | **1.6-3.1 / 2.4-4.2 / 3.0-5.6** [inferred] | per slice `LiveCheck` ~10-20 us (~2 slices a search: ~0.1-0.2 ms/s); per late object 2 `ObjStale` calls + `SameAds` (in the 3-6 us hook figure); no cut, no GC lock, no worker work | Mode R unchanged (worker answers are the same cut-time answers). Go-live risk: answers 1-3 ticks older than today's 2-tick age; only selection can drift (selected pairs still run the game's live C1 + `BestScores`); objects/lots touched since the cut fall back via `ObjStale`; actor changes via `LiveCheck` | Satb: none (the job ended before the resume). Guard: `ConsumedShare` rises, so the guard credits the extra saving and its `CostUs` sees the extra off-list/band C1 calls; no guard change | ~30-60 min (ts3fixes only) + 1 Dry job | G7 Dry, IP, 10 min, `autlivelate` on: top-10 >= 99%, top 1 >= 99.5%, replay mismatches 0, late-consumed share and drift by age bin | **build first** |
| L2 | **Allow-list the off-list meta ads** that consumed objects still send through C1 (docs/126 tier 2, docs/127 closures, mod guard) | with L1: up to 4.5 / 6.8 / 7.5 x the share of off-list C1 whose definitions can be made ready; without L1 only the first slice: ~1.4 / 1.6 / ~2 [inferred: 168 / 275 / 281 off-list C1/s x ~31-35 us] | none at the cut beyond new table rows; the worker scores more pairs (window `stop` aborts rise slightly) | each new definition needs Mode R A1-A3 = 0 over its pairs; docs/127: the virtual closure blocks most tier-2 verdicts (ready 191 -> 23) | Satb: more worker loads under the barrier; guard: lower `CostUs` per search | counter ~15-30 min (off-list C1 by definition type in `LiveB14`, one log line per type at load end); then hours per definition | Mode R BP + IP with the new rows: A1-A3 0, A4 share; then G7 Dry | second, picked by the counter |
| L3 | **Do not let consumed objects use up B13's 1 ms slice** (restart the game's `sUpdateTimer` after a consumed object, so only game-scored work counts) | ~0 on its own with L1 (late objects are consumed anyway); fewer B13 slices = search done 1-3 ticks earlier | slices get longer by the consumed work (~0.8-0.9 ms today: §1.3), so a frame hitch risk without a cap | none for scores; changes yield timing (vanilla timing-dependent anyway) | none | ~20 min | G7 Dry + `hitch-ab.ts` | not now; only if latency matters after L1 |
| L4a | Later cut / second cut after the yield | ~0: answers exist; a fresh cut only lowers their age, at 440-1,100 us a cut (docs/216 §4) | +1 cut per slice | none | Satb: one more span | - | - | rejected |
| L4b | Extend the cut's object lists | 0: `notInReq` 0 in all 9 jobs | - | - | - | - | - | rejected |
| L4c | Delta cut / worker re-score for objects changed since the cut | < 0.1: stale objects 0.04-0.24 a search (`ObjStale` hits 0.3-0.7% of queries) | a worker round trip per stale object | - | - | - | - | rejected |
| L4d | Wait one more tick before resuming | 0 (nothing more to score); adds a tick of latency | - | - | - | - | - | rejected |

Ranking by moved ms/s per hour of work: L1, then L2 (bigger ceiling, slow per-definition work, multiplied by L1), then nothing else. The first-slice cost itself (band-1 live C1 of passing pairs, §1.3) is the consume-cost question of docs/124 §16.5 (input-checked score reuse, design C), not a late lever.

## 4. L1 design (first build)

**Switch:** ts3fixes `autlivelate` (default off, `--on` package variant like `kAutLive`), `Instantiator.kAutLiveLateTicks` = maximum ticks after `ResumeFrame` a late slice may consume (default 3; older slices run the game's B14 as today).

**`LiveSearch`:** new fields `SliceFrame` (frame of the slice last re-checked) and `LateOk`.

**`LiveB14`, the `late` branch** (replaces the unconditional fallback when the switch is on):
1. Frame `f` = `AutShadowNative.Config(19)`. If `f != L.SliceFrame` (first B14 of a new slice): `L.LateOk = !L.Late && f - L.ResumeFrame <= kAutLiveLateTicks && LiveCheck(L, a) == 0`; `L.SliceFrame = f`. A failed check sets `L.Late` (sticky, as now) and counts its reason.
2. `!L.LateOk` -> today's path: count `late`, game's B14.
3. Else the object goes through the normal path with one extra test: if `ObjWhy[o] == 0`, re-query `ObjStale(ObjKey[o], 0, CutFrame)` and the lot key (resume-time `ObjWhy` is older than this slice) -> `stale`. Then `SameAds`, consume, band 1 / off-list / filtered C1 exactly as in the first slice. Count `lateConsumed`.
4. Dry: the same path; `DryCompare` then holds back the late objects' unselected pairs too, so G7's top-10/top-1 compare covers them without other changes.
5. Bands postfix and `ConsumedShare` need no change: late-consumed objects set `Consumed` / `Eligible` like first-slice objects.

**Counters** (ts3fixes `PatchCounters`, reserve with `node tools/registry.ts reserve fixes` at build time; `al[]` is full at 56 in autshadow v20): late slices checked, late slices refused by reason (age, epoch, state, lot, head, actor), late objects consumed, late objects stale, age bins of consumed late objects (1, 2, 3 ticks after resume), and B14 time by class (consumed, game's B14 for late, game's B14 for invalid) with the existing `sLiveB14Sw` so per-object costs stop being inferred (§2). `mono-cost-report.ts liveLines` gets one line for them.

**Self-check:** the slice rule as a pure function in `AutLiveCore` (frames, `LateTicks`, check result -> consume / late) with cases in the existing ts3fixes selftest.

**Risks:** an actor change inside B13 between slices that `LiveCheck` does not cover (same exposure as the 2-tick resume today, docs/124 §13.6); a mod B13 override (session mod guard unchanged); off-list C1 order changes nothing (vanilla calls the same C1 per ad).

**Jobs:** (1) G7 Dry IP 10 min with `autlivelate` (IP has the most late calls) - the proof; (2) acting BP + IP 10 min for the moved ms/s and the per-class B14 timers; crowd after both pass.

## 5. Open items

- Per-object B14 cost by class is inferred from Mode R R-per-object and the yield positions; L1's timers measure it [unknown until run].
- Which off-list definitions carry the 0.78-0.87 off-list ads of a late object (L2 counter) [unknown].
- The split of not-posted searches (§2 last bullet) [unknown].
