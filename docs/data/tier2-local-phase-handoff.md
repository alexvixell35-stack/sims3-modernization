# Tier-2 local-phase driver: design handoff (2026-11-08)

Design only, no code. Branch of this note: `docs/tier2-local-phase`. It answers the open item of `docs/data/tier2-rows35-handoff.md` P1 ("the worker only scores meta pairs today, so `get_InUse` and fire hooks won't fire until a local-phase driver exists") and docs/208 §9.1 ("no definition joins the allow-list ... they need the local-phase driver, docs/124 rank 1b"). Build agents read this file first and append one line under "Status log" (piece, branch, commit, what passed, what is open).

Sources [verified: read for this note]: `AGENTS.md`; `docs/59` §1.1-1.3 (call graph B/C/D/E, yield points); `docs/122` §1.2, §3.3-3.6, §4, §5 (shadow pilot flow, aborts, RNG, hooks, compare); `docs/124` §2.1-2.2 (hand-off options H1-H4); `docs/126` §2-§7; `docs/219` Summary, §4; `docs/208` §9.1; `docs/222` §1-§4, §8-§9; `docs/data/tier2-rows35-handoff.md`, `tier2-enablers-handoff.md`, `tier2-rows-handoff.md`, `races-handoff.md`; decompiled `Sims3.Gameplay.Autonomy.Autonomy` (Steam 1.67, `game-copy/decompiled`, read locally, not quoted); `mods/ts3fixes/src/AutShadow.cs` (`Request`, `Result`, `ScoreMeta`, `ScorePair`, `T2P1Fill`), `AutLive.cs` (`LiveGuard`, `DirectB14`). Tags as AGENTS.md.

## 0. Findings that shape the design

1. **The local phase only runs for high-LOD Sims, and high LOD is mostly "seen".** `ScoreInteractionsOnObjects` (B7) runs `ScoreInteractionsForLocalAutonomy` (B9) only when `ShouldRunLocalAutonomy` is true: forced (side entries), or local autonomy allowed and `IsSufficientlyOnScreenForHighLODSimulation` (B8) [verified, decompiled]. B8 is true for: the active household; a Sim on the active lot; a Sim on the same non-world lot as the active Sim; a guest of a started hosted situation with a selectable host; a daycare child; a resort worker; a Sim outdoors (or roomless) that `World.IsObjectOnScreen` sees. The flag is cached for 60 ticks (`mLODCheckTick`, `mbHighLOD`) [verified]. Low-LOD manager searches skip the local phase entirely and run only `Update` + meta [verified, `RunAutonomy(bool)`].
2. **So "unseen non-household" ∩ "runs the local phase" is a small class** [inferred]: docs/222's `UfWhy` treats the camera lot, the active household's lot and on-screen Sims as seen, and household Sims as never unseen. What is left: resort workers and daycare children on a lot the camera does not see; NPCs on the active Sim's lot but outside the camera view and > 80 m from household Sims (big venues, parks, festivals); Sims whose cached `mbHighLOD` is up to 60 ticks old after they left the view. **Nobody has measured how much B9 time this class holds.** docs/124 H4 puts the local phase at ~20% of manager scoring, docs/126 puts tier 2 at 38% of all scoring samples, but neither splits it by actor class. Piece L0 measures this first (memory rule "measure before build").
3. **The local phase never yields** (docs/59 §1.3 [verified]; no `YieldIfPossible` in B9-B12, confirmed in the decompiled body for this note). An acting design therefore cannot wait for the worker inside B9 the way docs/124 H2 waits inside B13; it needs one added yield before B9 (§5.2). Mode R needs no yield.
4. **Side entry points force the local phase for any Sim**, LOD-independent: `FindBestActionForCommodity*` (23 + 6 call sites), `FindActionsForCommodity`, `AutoSolveCommodity` set `mForceRunLocalAutonomy` / `mOverriddenLocalAutonomyLot` and run inside the caller's interaction task, which needs the answer synchronously [verified, decompiled; callers in buffs, babysitter, daycare, posture, crib, high chair and others]. They stay on the sim thread (§6).
5. **The enumeration is read-only; scoring is not.** B9-B12 read `lot.Map`, the actor's and other Sims' `SimCommodityInteractionMap`, the inventory map, `mSituationSpecificInteractions`, `SoloInteractions`, `InteractionScorer.UseValues` / `mDesires` (Dictionary lookups and list walks) [verified]. Three writes sit on the path: `InteractionCheckTable` (static pools, C1a), `BestScores.Add`, and B12's `catch` that **removes the pair from the map** on a non-reset exception [verified]. `lot.GetObjects<Sim>()` is `Query_GetObjectsOnLot` (an IC, fresh array). The worker driver never takes any of these paths (§2.3).
6. **C0 and the cached-values flag.** B7 sets `mUseCachedValues`, fills C0 (`CacheValuesToOptimizeTestFunctions`: on fire, baby coming, neat, party guest, group situation, scuba, fire near on lot, tomb room, selected household members) into fields of the shared `Autonomy` object, and clears the flag after the search [verified]. At the cut the flag is off, so every `UpdateCacheIfNeeded` on the worker would refill C0 (actor writes plus fire-manager reads): this is H8's reason in docs/126 §3. The C0 fields keep the values of the actor's last search afterwards [verified: no reset on clear].

## 1. What the local phase is in EA's code

| Step | Method (`Autonomy`, Sims3GameplaySystems) | Per-object loop | Where it runs |
|---|---|---|---|
| B7 | `ScoreInteractionsOnObjects(CommodityKind, bool)` | none: sets `mUseCachedValues`, C0, then B9, then B13 | manager task (`AutonomyManager.Simulate` -> `TryFindBestAction` -> `RunAutonomy`), or the caller's task for side entries |
| B8 | `ShouldRunLocalAutonomy` -> `IsSufficientlyOnScreenForHighLODSimulation` | none | same; writes `IsRunningLocalAutonomy`, `mLODCheckTick`, and on a change `SetIsHighAutonomyLOD` (queue move, A5) |
| B9 | `ScoreInteractionsForLocalAutonomy(CommodityKind)` | lot pick (override lot, current lot, or `FindClosestLotForLocalAutonomy` on the world lot); then in order: `lot.Map`, own Sim map, every other Sim's map on the lot (not on the world lot unless overridden), inventory map (not for NPCs on the world lot), situation pairs (generic only), solo pairs (generic, `AlienBrainPower`, or forced) | same |
| B10 | `FindClosestLotForLocalAutonomy` | public meta objects + known lots, nearest `Lot` under `kDistanceThresholdForClosestLot` | same; allocates a list |
| B11 | `ScoreInteractions(map[, c])` | commodity search: B12 for `c`; generic: B12 per use value > 0, per desire below `MaxY` (energy at bedtime too) | same |
| B12 | `ScoreInteractionsForCommodity(map, c)` | `map.InteractionsForCommodity(c)`, C1 per pair; exception -> pair removed from the map | same |
| C1 | `CalculateScoreAndAddToCandidates(iop)` | `InteractionCheckTable.TryAdd`, C2, `BestScores.Add` | same |
| C2 | `CalculateScore(iop)` -> `InteractionObjectPair.TestAndCalculateScore(aut, CurrentSearchType, flags)` | base `Test` (C3: parent walk + H7 drag cache, C4, `UpdateCacheIfNeeded` = H8, C5, C6, own `Test`, C7), then `IOverridesCalculateScore` or C8 `CalculateScoreForObjectInteraction` (`get_InUse` = P1, fire = P1, rain shelter = H11, posture chain) | same |

The per-object work is C2. The worker replaces C2 only; everything above it stays the game's in acting (§5.2), and is replayed read-only by the driver in Mode R (§2).

## 2. Mode R driver (`AutShadow.ScoreLocal`)

### 2.1 Request (sim thread, prefix hook on B9)

Posted from a prefix hook on `ScoreInteractionsForLocalAutonomy`, sampled (1 in `LocalSample`), only when all hold: search type `Autonomy` (manager search, not a side entry: `mForceRunLocalAutonomy` and `mOverriddenLocalAutonomyLot` clear), no request in flight (`asBusy`), actor scope non-household (§4), session guard on. The request is a new kind in the existing batch (`sReqs`, `kMaxReq` = 4, docs/122 §14.18), so it shares the cut.

| Field | Source at the request | Why here |
|---|---|---|
| actor, `Autonomy`, `c`, search type, flags | live | as the meta `Request` |
| lot | the lot B9 picks (incl. `FindClosestLotForLocalAutonomy`, run once here) | B10 allocates; keep it on the sim thread |
| Sims on the lot | `lot.GetObjects<Sim>()` (one IC call) into a rooted array | `Query_GetObjectsOnLot` is not a row |
| C0 copy | the 10 C0 fields, filled this search (flag is on at B9) | H8 check (§2.4) |
| drag object | `LiveDragHelperModel` cached top dragged object | H7 answer (docs/126 §3) |
| high-LOD reason | which B8 branch was true (household, active lot, same lot, hosted, daycare, resort, on screen, cached) | L0/L5 counters |
| P1 scope | `T2P1Fill` (built) | household latch for the P1 hooks |
| object list for rows | targets of the lot map + Sims on the lot, capped | object rows filled at the cut (piece L4) |

The pair list is **not** built here: the worker replays the enumeration (§2.3). The game's own B9 then runs unchanged on the same tick; a C1 recording hook (as docs/122 §1.2 step 2) stores the game's (pair, score) for the L report.

### 2.2 Cut and runs

Unchanged from docs/122 §1.2 steps 3-6: cut at the return of `MonoScriptHost_Simulate` `0xD81840`, snapshot rows filled by the game's bodies on the sim thread (copy, never call), Mode R reference `ScoreLocal(req, ref)` on the sim thread with the RNG saved and restored and per-unit streams (docs/125), worker `ScoreLocal(req, work)` in the quiescent window, window close at the next `Simulate`, overrun = stop flag, `KeepPartial` per pair prefix.

### 2.3 Enumeration replay (both sides, read-only)

- Same order as B9 -> B11 -> B12, with the request's lot and Sims array instead of B10 and `GetObjects`.
- Dedupe: own preallocated open-addressing set keyed by the pair reference (first occurrence wins, same rule as `InteractionCheckTable.TryAdd` on a pair hash). Never `InteractionCheckTable` (static pools), never `BestScores`.
- Per pair: `ScoreLocalPair` = `ScorePair` without the meta commodity filter (the map list is the filter) and with the local search's flags; result per pair: score, code (pass / test failed / skip / abort + reason), position in the walk.
- Off-list definition (exact runtime type + MVID not in the local allow-list, piece L3): code `skip`, no Test.
- Exception: abort `exc` for that pair; **never** the B12 map removal.
- Cap: pairs above `kMaxLocalPairs` (L0 sizes it; 8192 today for meta) -> the rest `skip`, counted `lpcap`.

### 2.4 Hooks it needs

| Hook / row | State | What the local driver needs from it |
|---|---|---|
| H7 drag cache | in flight (`feature/tier2-h7-h10`, no commits yet) | answer the request's captured object on both sides |
| H8 `UpdateCacheIfNeeded` | in flight | inside `ScoreLocal` (worker **and** reference): no-op; abort `cache` if the actor's C0 fields differ from the request copy (another search of the same actor refilled them). Brief the H7-H10 agent on this rule before it merges |
| H9 `AddTuning` | in flight | cache read, miss -> abort `tuning` |
| H10 LOD flag | in flight | return `mbHighLOD`, no recompute: the recompute writes `mLODCheckTick` and can move the actor between manager queues |
| H11 rain shelter, H12 route | built | as meta |
| P1 `get_InUse` (6), fire guard, drag pre-warm | built, off (`autt2inuse`, `autt2fire`, `autt2dragwarm`) | first place they fire: `CalculateScoreForObjectInteraction` on local targets. Counters 884-891 become meaningful only with this driver |
| P2 pre-warms + relationship guard | not built | opens ReadToSleep, SocialInteractionA, TakeMeasurements, ChangeDoorbellSound; until then those stay off-list |
| P3 CallPet copy | not built | CallPet off-list until then (largest single definition, 1.36%) |
| P5 Slots rows, P6 `Query_CountObjectsInRange` | P5 not started; P6 on `feature/tier2-p6-countrange`, not merged | their definitions off-list until merged and Mode R-green |
| E1/E2 rows (#7, #15, #16, #21, houseboat) | built (`tier2mask1108`), not run | answered from rows; household houseboat lots abort |
| E3 level-bit gate (#2, #4, #18) | in flight (`feature/tier2-e3`) | surface-type rows refused until then; their definitions off-list |
| tier-2 IC rows G1-G4, 4 shared rows | built, not run | answered from rows; object rows must exist for every lot target (L4) |
| races R1/R2 | R2a done, R4a closed, R1a/R1b/R2b/R2c open | not needed for Mode R (rows, no IC pass-through). R2c (world load / travel drains the worker) and the R1/R2 witness should be on in the Mode R runs; the M5 proof (85 closed) gates acting, not Mode R |

### 2.5 Compare (Mode R)

Per pair, docs/122 §5.1 classes: A0 bit-equal, A1 <= 1 ULP (must be 0), A2 score beyond 1 ULP (**0**), A3 code differs (**0**), A4 abort by reason, A5 overrun / epoch / guard trip. Extra for local: walk-order equal (same pair set and order on both sides; a difference = A3 on the request). L report against the game's own B9 (same tick, before the cut): pairs both scored, equal-score share, top-1 agreement; informational, mixes staleness and bugs.

## 3. Results and how they are checked

- Mode R: results are only compared, never used. Records go to `autshadow.jsonl` as kind `local` (request, pair, both scores and codes, snapshot version, events since the cut).
- Allow-list admission per definition: Mode R A1-A3 = 0 over the gate jobs and abort share < 20% of its pairs (docs/122 §5.1 target), else it stays off-list.
- Acting (L7) reuses the docs/222 invariants: a pick from a worker-scored pair gets a live `Test` in the D2 hook before D4/D5 (`AutChooseHook`, the local pairs join `sDirectPairs`); stale objects (`ObjStale`, `HasBeenDestroyed`) use the game's C2.

## 4. Household, selected and unknown scope

- **Mode R:** requests are posted only for actors that `T2P1Fill`'s latch calls non-household (null, destroyed, no description, selectable or active household = household, so unknown counts as household) [design; latch verified built]. Household and selected actors are never posted: counted, no shadow. Mode R changes no game state, so this is for clean data, not safety.
- **Acting:** `UfWhy(actor) == 0` (docs/222 §1) at the post **and** at the resume; any seen answer, `SafeUfWhy` error or household latch = the game's own B9 with the game's C2. A visible Sim's search never reads worker data, even for unseen objects. Selected Sim and household members are always exact, also off screen.
- Target scope: the local phase scores pairs whose targets can be household Sims (other Sims' maps on the lot). The worker only reads them; a target's own interaction set is never changed. The P1 hooks already abort for household-scope actors.

## 5. What stays on the sim thread

### 5.1 Always

Side entry points (table E of docs/59: `FindBestAction*`, `FindActionsForCommodity`, `AutoSolveCommodity`, `FindBestSocial`, `RespondToEvent`, inspection window); conversation socials (B6) and `TestInsaneAutonomy` (B5); B3 `Update`; B8 itself; C0; the enumeration, dedupe and `BestScores.Add` in acting; D2-D7 and `MakeSimGoHome`; the 15-19 hard definitions (docs/219 §3, rows35 §3); every off-list or mod-guarded definition; rain/hail shelter pairs without H11; any aborted pair.

### 5.2 Acting hand-off (L7, after Mode R is green; Rodney decides, Q2)

- **Post** at a prefix of B7 for manager searches only (search type `Autonomy`, manager task, `CheckYieldingContext` passes), unseen non-household actor, `ShouldRunLocalAutonomy` true: request as §2.1 (lot pick done here), then `Simulator.Sleep(0)` once. This is the H2 pattern of docs/124 moved from B13 to B7; B7 runs in the manager task, which already yields in B13 (Y2) [inferred: same task and search type as B13's Y2 yield; check `CheckYieldingContext` in L7; side entries are excluded by the search-type and force-flag tests].
- **Resume:** search-interrupted and actor-destroyed checks with the game's return values; `UfWhy` again; result ready check; then the game's B7 continues (C0, B9).
- **Consumer:** a hook on C1 (`CalculateScoreAndAddToCandidates`), active only while `sLocalDirect == this autonomy` and inside B9: pair in the result with code pass -> own dedupe + `BestScores.Add(iop, workerScore)`; code test-failed -> nothing (vanilla C2 scores 0 and `Add` drops it, as `DirectB14`); anything else (missing, off-list, abort, stale object) -> the game's C1. Hook on C1, not C2: `CalculateScore(iop)` is a one-line wrapper that the JIT may inline into C1 [inferred; check in L7].
- Meta then runs as today (docs/222 direct path posts again at B13 = a second yield). L8 merges both into one request and one yield.
- Cadence: one extra tick per unseen search (slower, not faster as docs/222 §8 was); the G8 by-class counters 661-669 show it.

## 6. Failure modes

| Mode | Reason / counter | Effect |
|---|---|---|
| IC with no row, position miss, memo miss | existing native reasons (`ic`, `pos`, `memo`, ...) | pair A4; acting: game's C1 |
| C0 refilled since the request | new `cache` (H8) | pair A4 |
| tuning cache miss | new `tuning` (H9) | pair A4 |
| `get_InUse` destroying override, fire on lot | `other` (19), by class 884-890 (P1) | pair A4 |
| rain/hail shelter | `rain` (20), 607-610 | pair A4 unless H11 answers |
| RNG | per-unit streams (docs/125) | no abort expected |
| exception in a pair | `exc` (12) | pair A4, never a map removal |
| pair cap | new `lpcap` | rest of the walk `skip` |
| window closed with the worker inside | `stop` (13), A5, `KeepPartial` | finished pairs valid, rest vanilla |
| epoch bump (world event, build/buy, lot events), world load, travel | A5 (docs/122 §2.1), R2c rule | whole request dropped |
| sim entered managed code in the window | guard trip, A5 | whole request dropped |
| worker reached an unguarded IC / class init | `asUnguarded`, `asWorkerClassInit` | must be 0; any = stop and triage |
| acting: actor seen at the resume, result not ready, guard over budget | snap counters (reuse 613-616 pattern) | the game's own B9 |
| acting: live `Test` of the pick fails | reuse 620-622 pattern | next pick, then vanilla (docs/222 §2) |
| mod changed a consumer-path method | `LiveGuard` L3 list + `ScoreInteractionsForLocalAutonomy`, `ScoreInteractions` (2), `ScoreInteractionsForCommodity`, `ScoreInteractionsForSituations`, `ScoreSoloSimInteractions`, `FindClosestLotForLocalAutonomy` | local path off for the session |

## 7. Metrics

- **L0 (before any build):** B9 us per search and pairs per search, split by actor class (household / seen / unseen non-household via `SafeUfWhy`) and by B8 reason; side-entry local searches counted apart; ms/s of B9 C2 per class per world. This decides go/no-go (Q1).
- **Mode R:** A0-A5 per pair and per definition, abort reasons (top 15 definitions), pairs and objects per request, cut us and snapshot bytes (local rows: ~155-170 B per object, 30-100 KB on a busy home lot, docs/126 §4), worker us, reference us, window us, overrun share, coverage = share of the game's B9 C2 us in allow-listed, non-aborted pairs, L report top-1.
- **Acting:** ms/s saved = C2 us skipped - (post + yield + consume + cut) us, per arm and order (memory rule: throttles and Mono lanes judged by ms/s); G8 by class (searches per Sim-hour, pushes, no-pick, GoHome); live-Test fail share; snap-backs; exceptions; p99/p99.9 and hitch counts as the release gate reads them.

## 8. Build pieces

Time = wall-clock for Claude with agents (CLAUDE.md units); game runs listed apart. Correctness runs only until L7; no timing job without the measurement smoke.

| Piece | Scope | Files | Model | Est. | Parallel-safe | Depends on |
|---|---|---|---|---|---|---|
| **L0 measure** | prefix/postfix counters on B9 (us, pairs via a C1 count while inside B9), B8 reason, actor class; side entries apart; `mono-cost-report.ts` line; counter slots via `tools/registry.ts reserve fixes`; switch off by default | new `AutLocalProbe.cs`, one `Patch` line in `AutShadow.cs` apply region, `mono-cost-report.ts` | Sonnet | 45-90 min + 3 jobs (BP, IP, SV, 10 min) | yes, except the `AutShadow.cs` apply region: merge after `feature/tier2-h7-h10` or use `tools/agent-merge.cjs` | none |
| **L1 H8/H10 local rules** | brief or amend the H7-H10 lane: H8 no-op inside `ScoreLocal` on both sides + C0 compare abort `cache`; H10 stored flag on both sides | `AutShadow.cs` hook region (H7-H10 lane's files) | Opus | 30-60 min on top of that lane | no: same region as H7-H10, P2, P3 | H7-H10 lane |
| **L2 ScoreLocal driver** | request kind `local` at the B9 prefix (§2.1), enumeration replay with own dedupe (§2.3), `ScoreLocalPair`, cap, exception rule; pure rules + selftest in `AutShadowCore` | new `AutShadowLocal.cs`, `AutShadow.cs` (request batch, `AutWork`/`AutReference` dispatch), `AutShadowCore.cs`, `tools/selftest/autshadow` | Opus | 2-3 h | no vs L1/P2/P3 (same files); yes vs L3, L6 | L0 go, L1 merged |
| **L3 local allow-list** | `tools/autonomy-tier2.ts` emits the local allow-list (ready + every row in `TIER2_BUILT`, MVIDs); ts3fixes loads it like `kDefNames`; regenerate after every row lane merge, never hand-merge | `tools/autonomy-tier2.ts`, `docs/data/autonomy-allow-local.csv`, `AutShadowLists.cs` (generated part) | Sonnet | 45-90 min | yes (tools + generated file) | none; refresh after P2/P3/P5/P6/E3 merges |
| **L4 local snapshot rows** | object rows for every lot target and Sim in the request (AO_OBJ fill at the cut), dirty-row reuse via docs/76/79 events, object cap, cut-cost counters | `native/ts3native/autshadow.cpp`, `autshadow-test.h`, request object list in `AutShadowLocal.cs` | Opus | 2-4 h | no vs P5, E3 (native descriptor/fill code): native lane serial | L2 |
| **L5 Mode R compare + report** | per-pair classify A0-A5 + walk-order check, `local` jsonl records, C1 recording hook for the L report, counters, `mono-cost-report.ts` local lines | `AutShadowLocal.cs`, `AutShadow.cs` compare, `tools/mono-cost-report.ts` | Sonnet | 60-90 min | after L2 (same file) | L2 |
| **L6 queue + repro** | queue `tier2-local-moder.json` (smoke + BP/IP/SV Mode R 10 min; gates A1-A3 = 0, `asUnguarded` 0, `asWorkerClassInit` 0, `autshadow_exceptions` 0, no new ScriptError, R2 witness clean); repro + agent action that gets an unseen non-household Sim into a local search (resort worker or NPC far on a big lot, camera away), `repros.json` entry; `--self-check` | `tools/harness/queues/`, `tools/harness/repros.json`, ts3agent action | Sonnet | 45-60 min | yes (harness files; not during a running chain: memory rule) | L0 result for the world choice |
| **Mode R game runs** | 3 jobs + fix rounds; then allow-list admissions per definition | - | - | 3 jobs (~12 min each) per round, 1-3 rounds | - | L2, L4, L5, L6 |
| **L7 acting** | B7 post + one `Sleep(0)`, resume checks, C1 consumer, `sDirectPairs` registration for the D2 live `Test`, snap-back, own cost guard, `LiveGuard` list extension, switch `autt2local` off by default; repro `aut-local-unseen-snapback` | `AutLive.cs`, `AutLiveCore.cs`, `AutShadowLocal.cs`, `tools/selftest/autlive` | Opus | 3-5 h | no vs any `AutLive.cs` work | Mode R green, M5 race proof (races-handoff), Q2 |
| **L7 gates** | correctness + G8 by class + ms/s bundle ABBA (docs/222 §11 gate 2) | - | - | 4-6 jobs + 1 clean night | - | L7 |
| **L8 one request, one yield** (optional) | local + meta in one post at B7; the B13 hook consumes without a second post | `AutLive.cs`, `AutShadowLocal.cs` | Opus | 2-3 h | after L7 | L7 measured |

Order: **L0 first, alone** (its number decides the rest). Then L1 with the H7-H10 lane; L3 and L6 in parallel from the start; L2 after L1; L4 after L2 and after the E3/P5 native merges; L5 after L2; Mode R runs; L7 only after Mode R is green and the race proof is closed; L8 last. Total ~11-18 h agent time to L7 (docs/126 §6 said 14-22 h for driver + snapshot + rows + Mode R; the rows are mostly built now), plus ~6-12 game jobs and 1 clean night. Builds never while a timing job runs (`probe-out/queue/job-running`).

Done per piece: AGENTS.md "Repro per fix", selftest, `--self-check` for new tools, PLAN.md "Resume here" line in the same commit, counters reserved in `docs/registry.md`, new switches off by default, status line here with the household statement ("no household path changed" or the exemption it adds).

## 9. Design questions for Rodney

1. Go rule after L0: build L2+ only if unseen non-household B9 C2 is >= 1 ms/s on some world (Mode R), and L7 only if >= 3 ms/s?
2. Acting hand-off: one extra yield at B7 for unseen searches (local phase one tick later), or keep the local phase Mode R only?
3. L8: merge local and meta into one request and one yield, or keep two yields per unseen search?
4. Mode R scope: skip household actors at the post (proposed), or shadow them too for coverage (never acts)?
5. Side entry points (`FindBestActionForCommodity*`, `AutoSolveCommodity`, socials): stay on the sim thread for good?
6. Acting with time-dependent `Test`s (docs/222 §9 ShopInRabbitHole lesson): live C2 for flagged definitions, or accept a stale "fail" for unseen Sims?
7. Busy lot over the pair cap: score the first N on the worker and the rest vanilla (proposed), or skip the whole search?
8. H10 on the worker: accept `mbHighLOD` up to 60 ticks old and let Mode R show the effect?
9. L0 worlds: BP, IP (resort workers), SV, plus one big community lot save with the active Sim on it?

## Status log

(Build agents append one line each: date UTC, piece, branch, commit, verdict.)

- 2026-11-08 UTC, design, `docs/tier2-local-phase`: this note, static only (decompiled `Autonomy` B7-B12 and side entries read, `AutShadow.cs` driver read). No code, no game run.
- 2026-11-08 UTC, L0, `feature/tier2-l0` 2f71e722 (asi sha256 db9be7aa, fixes c1090abf, registered in builds.json): existing data cannot answer it (docs/75/126 split scoring by phase and definition, never by actor class; no B9 timer existed). Built probe `autt2l0` (`AutLocalProbe.cs` + `AutLocalProbeCore.cs`, counters 903-927, agent `aut_l0_*`): hook on B7 (one frame per search; B9 not hooked because ilpatch b15 owns its IL), clock B7 entry -> B13 entry (first line of `AutMetaHook`) or B7 return = C0 + B8 + B9 (no yields), pairs = C2 calls of that autonomy in the window (`AutScoreHook`), class by `SafeUfWhy` after the clock (unknown = household), side entries and searches without the local phase (C0 + B8 floor) apart, unseen B8 reason by a read-only replica. No household path changed (measurement only, game B7 runs unchanged for everyone). Pair `tier2l0-1108` = ts3native 0.36.210 (no native change) + ts3fixes + ts3agent (game-copy/*.tier2l0-1108). selftest-all + hook-sites --check pass. **Not run. Next step:** `node tools/harness/queue.ts tools/harness/queues/tier2-local-l0.json` (smoke, bp-t2l0, ip-t2l0; TS3_AGENT_PACKAGE=game-copy/ts3agent.package.tier2l0-1108), then unseen ms/s = (aut_l0_us_unseen - aut_l0_n_unseen x aut_l0_nolocal_us / aut_l0_nolocal_n) / aut_l0_span_ms per world, and the Q1 verdict line here.
