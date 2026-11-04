# 124. Autonomy go-live: using the worker's meta scores on the sim thread (design)

Written 2026-10-20. Static design only: no build, no game run, no queue. It answers the open decision of docs/118 §3 ("go-live, scores used from the worker, is a separate decision after G6, with the docs/59 §3.5 staleness rules") and of docs/122 §7 (after G-A6). It assumes the docs/122 shadow pilot (`[AutShadow]`, Modes 0/R/W) has passed G-A3 to G-A6 in game; none of them has run yet (PLAN.md, docs/122 §14.7).

Inputs: docs/118 (plan, G0-G6), docs/122 (shadow pilot as designed and built, §1.2, §4.2, §14), docs/121 (IC closure, RNG §4.1), docs/123 (world image, offline Mode R), docs/59 (call graph §1.2, yields §1.3, worker flow §3.2, break-even §3.3, staleness §3.5, allow-list and mods §3.6, failure modes §3.8), docs/75 (meta = 80% of manager scoring), docs/97 (§1 net estimate), docs/76 and docs/79 (change events), docs/73 §5 (events stay on the sim thread), docs/104 (mod guard), docs/27 (script RNG), docs/30 + docs/120 (second Mono thread).

Tags: **[verified, docs/N]** = verified there, not re-read here; **[inferred]** = reasoning still to confirm (ledger docs/51 §166, I-124-*); **[design]** = a choice made here. Effort in CLAUDE.md units (wall-clock for Claude with agents; game runs and clean nights listed apart).

## Summary

- **Hand-off point [design]:** the meta phase of a manager search. At entry of `Autonomy.ScoreInteractionsForMetaAutonomy` (docs/59 B13), the existing ts3fixes B13 hook (docs/122 §14.5) posts the request and yields once (`Sleep(0)`, the same kind of yield B13 already makes at its Y2 sites). The cut runs at the return of `MonoScriptHost_Simulate` `0xD81840` of that tick [verified, docs/122 §14.1], the worker scores in the native tail, and the result is marked ready at the top of the next Simulate. When the manager task resumes in tick t+1, the hook runs the **game's own B13** (`Orig`). A new hook on **B14 `ScoreInteractionsForMetaObject`** consumes the result per meta object: for an object with a valid worker result it sends only the selected ads through the game's own C1 `CalculateScoreAndAddToCandidates`, so they are tested and scored **live** and enter `BestScores` exactly as in vanilla. Every other object runs the game's B14 unchanged.
- **Selected ads = the worker's top N (default 10 since 2026-10-30, §16.1; was 20) passing pairs** across the whole meta list (none of the 12 definitions has `AlwaysChooseBest` in the shipped tuning, §13.3; a pair whose runtime tuning has it sends its object to the game's B14), plus a margin check that pulls in the next band when the live scores come out lower than the worker predicted. `BestScores` keeps the top 10 (docs/59 D1), so every candidate D2 draws from has been tested and scored on live state. Staleness can only drop a pair the worker rejected or ranked low on a one-tick-old view; the next search sees it again.
- **Staleness:** result level (age > 1 tick, checked again at every B14 call because B13 can yield between objects, §13.1; world epoch, actor destroyed, lot or LOD change, head interaction, interrupted search, an actor field compare instead of an event listener, §13.4; tuning generation, save/travel/build) means the whole result is dropped and the game's B13 runs as today. Object level (object events `6`, `0x0B`/`0x0C`, `0x18`, `0x21`, `0x12` for the object; per-lot generation from object events `4`/`6` and world event `0x11`; lot events `0x0D`-`0x10`, `0x46`) means that object runs the game's B14. Pair level is covered by the live re-test of every selected pair.
- **RNG:** the decision draws (D2 choice, D4 interrupt chance) stay on the sim thread. Pet actors abort on the worker (`rng`) and score live, in B13 order. What changes are the tooltip draws in `Localization.FillInTokens` for pairs whose `Test` failed on the worker: vanilla makes them and throws the text away, go-live skips them. Picks stay **equal in distribution**, not path-equal; seeded runs (docs/27 `seed_rng`) repeat only within one go-live setting.
- **Mods:** everything docs/122 §4.1 and §14.5 guard (assembly MVIDs, per-method L3 integrity, exact runtime types) plus the consumer path itself: 18 methods, 13 of them new (§13.5); a hit there turns go-live off for the load. Side searches from mods (NRaas StoryProgression and others) never reach the hand-off and invalidate an in-flight result, as vanilla aborts a parked search.
- **Gates:** G7 dry apply (predicted top 10 vs the game's own search on the same tick), G8 behaviour in distribution (seeded on/off), G9 cost and stability (clean-night ABBA plus two clean runs per world).
- **Effort:** ~10-18 h on top of the pilot, **9-11 game jobs and 1-2 clean nights** (§10). Net sim-thread effect -18 to +10 ms/s [inferred, §8.3]: an offload step under the 2% rule, not a speed win, as docs/59 §3.3 and docs/97 §1 already said. **Superseded 2026-10-29 by §14** (Mode R measured in game): the M5 GC lock cost goes away with `LazyGate=1`, the per-search cut is now the deciding cost; plan S1-S6 ~12-23 h, ~18-20 game jobs, 1-2 clean nights; net +4 to +18 ms/s if the cut reaches <= 0.5 ms per search, -11 to +10 ms/s at today's cut [inferred]. **Gate G-C changed 2026-10-29 (§14.3a):** cut cost <= ~25% of the scoring it moves, both per second at the go-live rate, instead of a fixed <= 0.5 ms per search.
- **Top risks:** net cost (M5 fixed cost plus the per-search cut at `Sample=1`), staleness bias in which pairs reach the top 10, and M5 in game (the worker has not interpreted in the game yet; the P4 TLS fix landed in 2f4c595) (§11). Update 2026-10-29: the worker has interpreted in game (mono-cost6: 1,691 jobs, 0 exceptions, 0 crashes); the cut is now risk 1 (§14.3).
- **Worker window (2026-10-30, docs/206):** the §2.2 row "worker scores in the native tail of t" does not hold: the quiescent gap between Simulate iterations is ~10 us (docs/122 §14.20), the native span inside an iteration (BP ~3.3, IP ~2.2, SV ~1.4 ms) calls C# synchronously (route events 57/58, SACS event pushes), and a copied heap costs more than a job moves. **Chosen: copy on first write** (docs/206 §3.3): from the cut to the worker's end the sim logs the old value of every managed word it overwrites, the worker's loads check the log, so §3.4's in-place reads see the cut while the sim runs t+1 (window ~26-32 ms, overruns ~0, ~1-2.5 ms/s barrier [inferred]); S0 store-rate probe first, then ~15-24 h and 6-10 jobs before S2.
- **Consume cost (2026-10-30, §15, offline from mono-cost11):** live C1 for the top 20 pairs costs ~9-20 ms/s on the sim, and IP overruns (17-44% of searches) cost another 8-16. Net after consume at `TopN` 20 is BP -4.9 to +1.3 and IP -3.6 to +8.2 ms/s; the range is the pass share, which nothing counts yet. Break-even N is BP 8-16 and IP 9-30. `TopN` 10 gives BP +1 to +5 and IP +2 to +16 [inferred]. Before S2: count the pass share, make 10 the default `TopN`, fix the IP overruns. **Decided 2026-10-30 (§16):** `TopN` 10 with an exact band rule, a per-ad rule for mixed meta objects, scores stay exact (no Test-only option). Net with band 1 only: BP +1.0 to +4.9, IP +2.3 to +15.7 ms/s [inferred].

- **Built 2026-10-31 (§17, ts3native 0.36.142, not run):** `[AutShadow] Mode=3` + ts3fixes `autlive` (B13 wait, B14 consumer, §16.1 bands, `ObjStale`, `VerifyEvery`, `Dry=1` for G7), queues `golive-g7.json`, `golive-act.json`, `golive-ab.json`.

## 1. Starting point

| Have (docs/122, built offline) | Missing for go-live |
|---|---|
| B13 request hook, sampled; request = actor, commodity, search type, flags, meta object list in B13's order | a yield between request and use, and a consumer |
| Cut + window on `[MonoThread]`'s Simulate slot stub; worker via `MtSubmit`; result arrays `sWork` per (meta object, ad): score, code; per object meta score | "result ready" published before the script tick of t+1 (today `finish` runs at a later Simulate return) |
| Epoch drop and overrun at the next Simulate entry (`LastRun` 1/2) | object-level and actor-level invalidation queries for managed code |
| Mode R reference with the MT state at host `+0xC30` saved and restored [verified, docs/122 §14.1] | sampled reference while live, to keep catching bugs |
| Mod guard: MVIDs + L3 over the driver's callees | L3 over the consumer path |

## 2. Hand-off point

### 2.1 Options

| # | Where the result is used | For | Against | Verdict |
|---|---|---|---|---|
| H1 | Precompute the **next** search of the same Sim while it waits in the queue | no extra yield | the result is many ticks old by the time it is used (a Sim waits ~N ticks with N Sims queued, docs/59 §1.1); motives, Sims and lots move | no |
| **H2** | **B13 entry of the same search**: post, yield once, use at resume one tick later | age 1 tick; B13 already yields (Y2), so the manager task is already a legal yielding context there; the game's own B13 runs around the consumer (gates, B15, B16, go-home pairs, Y2, abort checks unchanged) | one tick of extra latency for meta searches that would have finished in one slice | **chosen** |
| H3 | Take the worker's top-1 and push it (skip D2) | cheapest | not vanilla: D2's weighted random pick, `AlwaysChooseBest` and the local-phase scores are lost | no |
| H4 | Whole search (local + meta) on the worker, docs/59 §3.2 | more work moved | local phase is 20% of manager scoring and a long tail of definitions (docs/75 §1); its non-yielding slice cannot wait a tick | later, separate |

### 2.2 Timeline of one search (H2)

| Tick | Thread | Point | Work |
|---|---|---|---|
| t | sim, manager task | B4 prologue, local phase B9-B12 | unchanged; local pairs enter `BestScores` |
| t | sim, manager task | **B13 entry, hook prefix** | if eligible (§2.3): `Begin`, `AddRow`, `Post` as docs/122 §14.1; then `Simulator.Sleep(0)` |
| t | sim, native | return of `0xD81840` | cut (snapshot update, version), optional sampled reference (§7.1), `MtSubmit` |
| t → t+1 | worker | native tail of t (animation, locomotion, publish, waits) | `AutShadow.ScoreMeta` on the snapshot [verified as built, docs/122 §14.5] |
| t+1 | sim, native | top of Simulate | window close: worker done = result **ready** (version, search id, epoch at cut); still inside = stop, overrun, result not ready |
| t+1 | sim, manager task | **resume in the hook prefix** | B17-equivalent checks (search interrupted, actor destroyed) with the same return values; result-level rules (§3.1); then the game's own B13 |
| t+1 | sim, manager task | each B14 call inside the game's B13 | **B14 hook**: object in the request and valid → selected ads through the game's C1; else the game's B14 (§2.4) |
| t+1 (+ Y2 slices) | sim | after B13 | D2 choose, D4/D5 push, as vanilla |

Why the consumer is B14 and not a new B13 [design]: the game's B13 keeps every rule we would otherwise have to copy (`CanRunAnyMetaAutonomy`, `CalculateNumSimsAtHomeOrGoingHome`, toddler/pet inside pairs B16, go home and stay home pairs, current lot, home lot, career location, the stopwatch and Y2 yields, the `SearchInterrupted`/`ActorDestroyed` returns) and re-copies the meta list after the resume. Objects added since the request are simply not in the request and run the game's B14.

### 2.3 Eligibility at B13 entry (else the game's B13, no request)

1. `[AutShadow] Mode=3` and switch `autlive` on; the session and per-load mod guards passed (§6).
2. Search type `Autonomy` (manager search), not inspection, not `BuffAutoSolve`, not an override (`AddWhileInteracting` turns meta off anyway) [verified, docs/59 §1.2-1.3].
3. The yield context check that Y2 uses passes (`CheckYieldingContext`) [verified, docs/59 §1.3]. The hook runs directly in the manager task's B13 frame, not inside a native-to-managed call, so the docs/04 yield guard case (a yield from code running inside a native-to-managed call aborts the game) does not apply [inferred, I-124-1].
4. No rain, hail or deep-snow search (C8 builds an `InteractionInstance`, out of the pilot, docs/122 §1.1).
5. Worker idle and no request in flight (`Begin` refuses when busy, docs/122 §14.1).

### 2.4 The B14 consumer, per meta object

| Case | What the hook does |
|---|---|
| Result not ready, dropped (§3.1) or search id mismatch | game's B14 (whole search falls back) |
| B14 called in a later tick than cut + 1 (B13 yielded at a Y2 site between objects, §13.1) | game's B14 for this and every later object of the search; the margin check still runs |
| A selected or unselected ad's runtime tuning has `AlwaysChooseBest` (mod tuning; none in the shipped tuning, §13.3) | game's B14 |
| Object not in the request (list changed) | game's B14 |
| Object invalidated since the cut (§3.2) | game's B14 |
| Object `skip` or aborted on the worker (A4 reason: `rng`, `row`, `pos`, `memo`, `query`, `sf`, `privacy`, `roomcache`, `simstale`, `exc` ...) | game's B14 |
| Object has an ad of a non-allow-listed definition or a mod type | ~~game's B14 (re-scores the allow-listed ads too; small)~~ Changed 2026-10-30 (§16.2): no full-object fallback; the per-ad rule of §16.2 sends the non-allow-listed ads through C1 and takes the allow-listed ones from the worker |
| Valid | for each **selected** ad of this object, in `MetaAds` order: identity check (same ad reference, same `MetaAds` count as at the cut, else game's B14 for the object), then the game's C1 with the same pair object B14 would pass. Unselected ads: nothing |

C1 does the check-table dedupe (C1a), C2 (live `Test` + score) and `BestScores.Add` [verified, docs/59 C1]. So `BestScores` and `InteractionCheckTable` are filled by the game's code with live values; the hook only chooses which pairs reach C1. The worker already applied B14's commodity filter (`Tradeoff.SatisfiesCommodity` / `MetaAdSatisfiesCommodity`) to the same commodity kind [verified as built, docs/122 §14.5], so every selected ad passed it.

### 2.5 Selection (computed once, at resume, before the game's B13)

1. Collect worker pairs with code 0 (pass) from valid objects, sort by worker score.
2. Select the top `TopN` (default 20, twice the `BestScores` capacity of 10 [verified, docs/59 D1]). **Changed 2026-10-30:** default 10, and the band rule of step 4 is replaced by §16.1.
3. ~~For each allow-listed definition whose tuning has `AlwaysChooseBest`: also select its best pair.~~ Dropped after S4 [verified, §13.3]: none of the 12 has it in the shipped tuning, so the per-definition best-pair rule is replaced by the guard row of §2.4 (an object with such an ad runs the game's B14, which keeps `BestScores`' remembered-best handling exactly as vanilla).
4. **Margin check** (after the game's B13 returns, still in the hook): let `s10` = the 10th live score in `BestScores` (or the lowest if fewer than 10) and `w` = the best worker score among unselected pairs. If `w × (1 + Margin) ≥ s10` (default `Margin` = 0.05 [design; G7 sets it]), send the next `TopN` band through C1 (same identity checks), up to `Bands` (default 3); after that, the game's B14 for every remaining valid object. Counted (`alBand2`, `alBand3`, `alBandFull`).

The pairs that never reach C1 are exactly those the worker judged failed or ranked below the selected bands. Their live verdict is not computed; that is the saving and the only source of difference from vanilla (§5).

## 3. Staleness and invalidation

### 3.1 Result level: drop everything, the game's B13 runs (counted by reason)

| Rule | Source | Tag |
|---|---|---|
| Result age > 1 tick (resume tick ≠ cut tick + 1), or `MaxAge` (default 1) | version and tick words | [design] |
| World epoch moved since the cut: world events `2`, `3`, `5`, `7`, `9`, `0x0A`, lot events `0x0D`-`0x10`, build/buy slot swaps `World_LotClusterTreesImpl` `0x11623B0` and `RouteManager_OnExitBuildBuy` `0x1160DA8` | routesnap's `World_FireEvent` `0x78F2B0` detour; already drops in-flight runs (`LastRun` 2) | [verified, docs/76 §2.2; docs/122 §14.1] |
| Actor destroyed, out of world, `HasBeenDestroyed` | B17-equivalent check at resume; `ActorDestroyed` return as vanilla | [verified, docs/59 §1.3] |
| Another search type ran for the actor (`mAutonomySearchWasInterrupted`, side entry points incl. mod story progression) | same check; `SearchInterrupted` as vanilla (nothing pushed) | [verified, docs/59 §1.2 E] |
| Actor `LotCurrent` differs from the request, or the LOD flag flipped | request fields vs live | [design, docs/59 §3.5] |
| Actor got a head interaction and the search is not an override | B4 bail rule | [verified, docs/59 B4] |
| Actor fields differ from the request: `SimDescription` age and species, `LotHome`, `Household`, `IsSelectable` (`IsNPC` is its negation, not compared twice), `AssignedRole`, `Occupation` (reference) plus its `JobCount` and `CurrentJob` (`HasJobAtLot` reads the job list, which work-day alarms change under the same `Occupation`, §13.6), `CareerLocation`, the bot trait `CapacityToLoveChip` (the two date definitions only) | the request stores the values at B13 entry; the resume compares them (about ten reads, no listener). Replaces the planned `EventTracker` actor-generation listener: most of the actor state the 12 tests read has no event (§13.4). Writers and same-identity changes: §13.6 | [verified fields, §13.4, §13.6; design] |
| Tuning generation moved (Retuner-style edits) | hook on the tuning loader and setters (docs/59 §3.5) | [design] |
| Save, load, travel, CAS, Edit Town, build/buy started | game state hook; the worker is paused for these | [design, docs/59 §3.8] |
| Overrun, worker exception for the whole run, submit refusal | `LastRun` 1/4/5 | [verified as built, docs/122 §14.1] |

### 3.2 Object level: that object runs the game's B14

| Change since the cut | Event source | Tag |
|---|---|---|
| Object destroyed, removed from or returned to the scene | object events `6`, `0x0B`/`0x0C` | [verified, docs/76 §1] |
| Visibility flag | object event `0x18` | [verified, docs/76 §1] |
| Parent changed, child notification | object events `0x21`, `0x12` | [verified, docs/79 §1-2] |
| Transform published (non-Sim objects only; Sims are always refreshed) | object event `0x0E` | [verified, docs/76 §1]; rate is high from `RigPose_Update`, so the meta object row ignores `0x0E` for animated props unless the meta object itself moved [design] |
| Lot membership (objects added to or removed from a scored lot) | per-lot generation from object events `4`, `6` and world event `0x11` `OnObjectLotUpdate` (`0xC8A290`) | [verified, docs/122 §2.2] |
| Lot changed | lot events `0x0D`-`0x10`, `0x46` | [verified, docs/76 §2.2] |

ts3native already keeps these as row dirty bits and lot generations (docs/122 §13). New for go-live: one query IC `AutShadowNative.ObjStale(index)` that answers "row or its lot changed since version v" from the same bits [design].

### 3.3 Pair level

No rule needed: every selected pair is re-tested and re-scored live by C1/C2. A pair that fails live drops out as in vanilla. Not covered (accepted, as docs/59 §3.5 already accepts): a pair that the worker failed or ranked low on the one-tick-old view and that would now rank in the top 10. G7 measures how often it happens.

### 3.4 Managed state in the window

The worker reads managed state in place in the quiescent window (docs/59 §3.5, docs/122 §1.2). That premise still needs G-A5. If a native-to-managed call on the sim thread enters the window, ts3native sets stop and the run is an overrun; in go-live that means a fallback to the game's B13 at t+1, never a wrong score.

## 4. Fallback to the sim-thread search

The fallback is always the game's own code on the sim thread: the game's B13 (result-level drop) or the game's B14 (object level). Cost of a fallback: the vanilla meta phase plus one tick of latency and the cut. No retry, no second request for the same search. Session-level off switches (counted, one log line each): mod guard failure (§6), watchdog (worker inside > 50 ms, docs/59 §3.8), more than 3 worker exceptions for one definition (that definition leaves the allow-list for the session, docs/59 §3.6), `asUnguarded` or `asWorkerClassInit` non-zero, a sampled reference mismatch A2/A3 (§7.1).

## 5. RNG and equivalence with vanilla

### 5.1 Draws in vanilla's meta phase

| Draw | Where | Go-live |
|---|---|---|
| Pet branch of the base meta score (`RandomUtil.GetRandomObjectFromList`) | C2 of pet actors [verified, docs/121 §4.1] | the worker aborts `rng` on that object; it runs the game's B14 on the sim thread, so the draw happens there in B13 order |
| `Localization.FillInTokens` (`RandomUtil.GetInt`) | tooltip text built on failing `Test` branches, even for autonomous tests [verified, docs/121 §2.6, docs/78] | for pairs the worker failed, the draw is skipped (the worker's `LocalizeString` shim returns the rooted empty string, docs/122 §3.5); for selected pairs, C2 runs live and draws as vanilla |
| D2 choice, D4 interrupt chance | sim thread after B13 [verified, docs/59 D2, D4] | unchanged |

### 5.2 Equal in distribution, not path-equal

- The decision input D2 sees is the top 10 of live scores. When the selection covers vanilla's top 10 (§2.5), it is the same set with the same scores.
- The skipped tooltip draws only pick text that autonomy discards [inferred, I-121-4, I-124-6]. With the MT treated as a stream of independent uniform values, skipping k values that no decision reads leaves the D2 draw uniform and independent of the scores, so the pick distribution is unchanged; only the position in the stream moves.
- Two more path changes, both already timing-dependent in vanilla (docs/27 §3: bit-exact replay is out of scope): the meta phase usually spans fewer ticks (fewer Y2 slices), and other tasks' draws interleave differently with the one-tick wait.
- Consequence for testing: a seeded A/B (`seed_rng`) of go-live on vs off compares distributions, not paths (G8). Seeded runs that compare anything else must use the same `autlive` setting on both halves (docs/121 §4.1 note).
- No parity mode [design]: advancing the MT by the "skipped" count would need the number of draws `FillInTokens` would have made, which depends on the text it would have built; computing it means running the text code the worker must not run (EAText globals, docs/121 §2.6).
- Rejected alternatives (docs/121 §4.1): a per-worker MT seeded from the sim MT, and a draw-ahead buffer. Both reorder the sim's own decision draws and gain nothing over abort-on-draw for the meta allow-list.

## 6. Mod compatibility

| Mod behaviour [community, read for behaviour only] | What go-live does | Tag |
|---|---|---|
| A mod replaces EA script assemblies (core mods) | session guard: MVID mismatch, go-live off for the session | [verified as built, docs/122 §14.5] |
| A run-time patcher (MonoPatcher-style, ts3fixes' own patch host) swaps a method in the driver's closure or **in the consumer path** (the 18 methods of §13.5: B13's caller, B13, B17, B14, C1, C2 and its pair getters, `BestScores` add and choose, D2) | L3 check (`MonoMethod+0x08` = declaring class) over the driver's callees, extended by the consumer path; hit in the driver closure = that definition `skip`, hit in the consumer path = go-live off for the load. Our own known swaps (AM-1 `IsAllowedInRoom` memo, `HoursMemoHook`) are accepted by target and hook class, as docs/104 §1 does for routing | [verified, docs/104 §5.4; design for the extension] |
| NRaas mods that replace definition singletons with derived types (Woohooer is the known case, docs/59 §3.6) or add their own definitions and meta objects | exact runtime type + MVID: not on the allow-list, so their ads and objects run the game's B14 live | [verified as built, docs/122 §4.1] |
| NRaas StoryProgression and other mods that push interactions or run `FindBestAction*` from their own tasks | side entry points never reach the hand-off; one for the queued actor sets `mAutonomySearchWasInterrupted`, so the parked search returns `SearchInterrupted` as vanilla | [verified, docs/59 §1.2 E] |
| Mods that change autonomy through tuning (Retuner, autonomy on/off settings) | tuning generation drops in-flight results; the worker reads tuning in place | [design, docs/59 §3.6] |
| Overwatch / ErrorTrap resets a Sim mid-search | actor destroyed or reset: result-level drop | [design] |
| A mod overrides `Autonomy.RunAutonomy`, B13 or `AutonomyManager.Simulate` | caught by L3 on the consumer path; if the mod calls B13 from another context, rule 2/3 of §2.3 sends it to the game's B13 | [inferred, I-124-7] |
| Mod event listeners | the worker raises no event (docs/73 §5.3); every C1/C2 and push runs on the sim thread, so listeners see the same calls in the same task | [verified, docs/73 §5] |

## 7. Switches, counters, logs

### 7.1 Switches

- Ini `[AutShadow]`: `Mode=3` (live; 0/1/2 as docs/122 §4.3). New keys, all ignored unless `Mode=3`: `TopN=10`, `Margin=0` (both changed 2026-10-30, §16.1; were 20 and 0.05), `Bands=3`, `MaxAge=1`, `VerifyEvery=50` (every 50th live search also runs the Mode R reference at the cut with the MT saved and restored; an A2/A3 there turns live off for the session), `Dry=0` (G7: compute and compare, do not use; §8.1). `Sample` is honoured (1 = every eligible search; larger = staged roll-out).
- ts3fixes switch `autlive` (on/off for `--ab`), off by default; tuning `kAutLive` false. AGENTS.md rule: every new switch defaults off; nothing here changes save data.

### 7.2 Counters (reserve in `docs/registry.md` at build time, not here)

- ts3native `ts3native_autshadow` v3: `alReady`, `alNotReady` (by `LastRun`), `alVerify`, `alVerifyMis`, `alObjStaleQ`.
- ts3fixes: `alEligible`, `alPosted`, `alApplied`, `alDrop[12]` (age, epoch, destroyed, interrupted, lot, lod, head, actor, tuning, gamestate, overrun, other), `alObjLive` by reason (stale, notinreq, abort, skip, mixed, late, acb), `alPairsWorker`, `alPairsC1` (selected), `alBand2`, `alBand3`, `alBandFull`, `alApplyUs`, `alLatencyTicks` (histogram: request to push), pick histogram per definition type (live vs `Dry` reference), `alTop10Same`, `alTop1Same` (Dry only).
- Log: `autshadow.jsonl` gets `live-drop` (first 20 per reason per load) and `dry-diff` records (pair, worker score, live score, invalidation events since the cut).

## 8. Gates

Numbering continues docs/118 (G0-G6). Prerequisites: G-A3 to G-A6 passed (docs/122 §7), the P4 TLS fix confirmed in game (no `FrameTls SKIPPED`), worker overrun < 5% (G-A5).

| Gate | After | Pass when | Fail means |
|---|---|---|---|
| **G7** dry apply | `Mode=3 Dry=1`, BP, IP, SV, 10 min each + `bp-autlive-edit` (build/buy, Edit Town) | over ≥ 2,000 eligible searches per world: predicted top 10 (selected bands, scored live with C2 under MT save/restore) equals the game's own top 10 of the same search in ≥ 99% and top 1 in ≥ 99.5%; every difference record has a pair and its cause (stale reject, below band); live scores of the shared pairs bit-equal; `alVerifyMis` 0; 0 crashes in two clean runs (crash rule) | widen `TopN`/`Margin`, add the missing invalidation event, or stop: the one-tick view is too stale for meta |
| **G8** behaviour | `Mode=3` live vs off, same `seed_rng` seed, BP and IP, 2 x 30 min per world (correctness runs: daytime allowed) | per-definition pick shares: chi-square p ≥ 0.01 over definitions with ≥ 50 picks; autonomous pushes per Sim-hour within ±5%; `MakeSimGoHome` fallbacks within ±10%; median request-to-push latency not up by more than 1 tick; fallback share < 10% with every reason sized; 0 crashes; possible stuck Sims logged only | find the biased definition (dry-diff records by definition), exclude it from the allow-list, rerun |
| **G9** cost and stability | clean night ABBA `--ab autlive` BP and IP (18 min); then 2 clean runs per world (BP, IP, SV) with one save + reload during a meta burst | sim it/s not down > ~2% and ThreadedObjects ms/s not up > ~2% (offload rule, CLAUDE.md); manager-task meta ms/s on the sim thread down (work moved off, measured, docs/75 §5 method); 0 crashes in 6 runs; `asUnguarded` 0, `asWorkerClassInit` 0; save during an in-flight search drops the result and the save loads | keep `autlive` off; write the measured break-even (§8.3 inputs) into docs/59 §3.3 |

Shipping after G9 is Rodney's decision (release defaults, docs/RELEASE.md); this design keeps everything off.

### 8.1 Dry mode

`Dry=1` posts and yields exactly as live (so the staleness is the real one-tick staleness), then at resume scores the selected bands with the game's C2 directly (no `BestScores`, no check table), with the MT at host `+0xC30` saved before and restored after, and runs the game's B13 normally. The Mode W C1 record hook (docs/122 §14.5) gives the game's own scores for the same search; the compare runs after B13. Side effects of the extra C2 calls are the idempotent lazy fills docs/122 §12 S1 lists [verified, docs/122 §12].

### 8.2 Repros (tools/harness/repros.json, AGENTS.md rule)

- `aut-meta-burst` (exists in the docs/122 plan: idle adults, needs max, unpause) drives G7-G9.
- New: `aut-live-pet` (pet household: `rng` aborts go live), `aut-live-interrupt` (start a commodity search for a queued Sim during the one-tick wait: `SearchInterrupted`), `aut-live-venue-hour` (advance time across a venue opening hour during a burst: a store-hours `Test` flips between cut and apply), `aut-live-save` (save during a burst).

### 8.3 Cost model [inferred, replaced by G9]

| Item | ms/s | Source |
|---|---|---|
| Meta scoring in the manager task | ~31 | docs/75 §1: 80% of ~39 ms/s C2 |
| Allow-listed share f | ~0.75 → ~23 eligible | docs/75 §1 (13 types), docs/122 §1.1 |
| Re-scored at apply (top 20 + bands + live objects), r ≈ 10-25% of eligible | -2 to -6 | [inferred; pairs per search not measured] |
| Cut at `Sample=1` (10-30 meta searches/s x ≤ 0.3 ms) | -3 to -9 | docs/122 §2.3 budget at `Sample=4` was ≤ 2 |
| Request build (AddRow per meta object) | -1 to -3 | [inferred] |
| `[MonoThread]` fixed (P3 gate, P4) | -7 to -25 | docs/59 §3.3, docs/30 §8 |
| Guard thunks on all ICs | ≤ -1 | docs/122 §7 G-A3 limit |
| **Net on the sim thread** | **-18 to +10** | consistent with docs/97 §1 (-16 to +7) |

The M5 fixed cost dominates; it amortises only if another worker load shares it (docs/59 §3.3). That is why G9 is judged by the offload rule, with ms moved off the sim thread reported next to it/s.

## 9. Failure modes

| Failure | Detection | Effect / fallback |
|---|---|---|
| Worker not done at the top of t+1 | `LastRun` 1 | game's B13 at t+1 (one tick of latency) |
| Worker exception on one object | per-object `try/catch` (A4 `exc`) | that object live; 3 per definition = definition off for the session |
| Worker hangs | watchdog, 50 ms | live off for the session; worker parked, not killed (it may hold the P3 lock, docs/59 §3.8) |
| Extra yield refused at B13 entry (no context) | rule 3 of §2.3 | no request, game's B13 |
| Hook resumes in the wrong search (id mismatch after an abort and requeue) | search id + actor id in the result | drop |
| Stale reject: a pair the worker failed now passes and would rank top 10 | Dry diff records (G7); sampled in live via `VerifyEvery` only for bugs, not staleness | accepted; next search sees it; counted in G7 |
| Live scores lower than predicted across the board (motive change since the cut) | margin check | bands 2-3, then the game's B14 for the rest |
| A selected pair object changed identity | `MetaAds` reference and count check | that object live |
| Snapshot or shim bug (wrong worker score) | sampled reference A2/A3 (`VerifyEvery`) | live off for the session; candidates were still live-scored, so a bug changes ranking at most, never a pushed score |
| Mod patches the consumer path at run time after the load check | L3 rolling slice per search (docs/104 §6) | live off for the load |
| Save, travel, build/buy during a search | game state hook | worker paused, result dropped |

## 10. Effort (CLAUDE.md units)

| Step | Work | Estimate |
|---|---|---|
| S4 static checks | B14 call sites and signature (I-124-2), `HowMuchItWantsSimToCome` call site (I-124-3), `AlwaysChooseBest` in the 12 definitions' tuning (I-124-4), actor state read by the tests (I-124-8): **done 2026-10-20, §13.1-13.4**; L3 list for the consumer path, writers of the compared actor fields, request object list: **done 2026-10-20, §13.5-13.7** | 1-2 h (done ~2 h) |
| ts3native | `Mode=3`: result ready at window close, `ObjStale` IC, `VerifyEvery`, `Dry` flag passed through, counters v3, selftest cases (ready/not ready, stale object, verify mismatch off) | 2-3 h |
| ts3fixes | B13 prefix yield + resume checks, selection and bands, B14 hook (with the per-call tick check), actor field compare, tuning generation, Dry compare, counters, pick histogram, `autlive` switch | 3-5 h |
| Offline | selection/margin/identity logic selftest (`tools/selftest/autshadow`), monohost: result-ready timing with a synthetic driver | 1-2 h |
| Game rounds | G7-G9 build and fix rounds | 3-6 h |
| **Total** | on top of the docs/122 pilot (~17-29 h + M5) | **~10-18 h** |
| Game runs | G7: 3 dry + 1 edit repro, plus reruns to two clean; G8: 4 seeded (2 per world, 30 min); G9: 2 ABBA + 6 stability (shared with G7 reruns where possible) | **9-11 jobs** (~8-30 min each) |
| Clean nights | G9 ABBA (one night, BP + IP); a second if the first is noisy | **1-2** |

## 11. Risks

| # | Risk | Effect | Mitigation |
|---|---|---|---|
| 1 | **Net cost**: M5 fixed cost plus a cut per search at `Sample=1` eat the eligible ~23 ms/s | G9 fails the 2% rule although correct | measure f, r and the cut in G7 before G9; `Sample` > 1 halves cut cost but also the moved work; share M5 with another worker load |
| 2 | **Staleness bias**: one-tick-old rejects are not random (venue hours, buffs, a Sim arriving), so some definitions lose picks systematically | behaviour drifts from vanilla in distribution | object and actor invalidation, bands, G7 diff records by cause, G8 per-definition chi-square, exclude a biased definition |
| 3 | **M5 in game**: the worker has never interpreted in the game (P4 TLS was skipped in game until 2f4c595; shadow Mode R jobs were dropped from the queue) and the quiescent-window premise is unmeasured | crash, hang or many overruns | G-A3 to G-A6 first; crash rule; overruns fall back, never use a partial result |
| 4 | The extra yield at B13 entry | a new yield point in the manager task; a mod or our own hook calling B13 from a non-task context | eligibility rules 2-3; L3 on B13; `aut-live-interrupt` repro |
| 5 | C1/C2 side effects skipped for unselected pairs | a lazy fill or cache the game relies on later is not made | docs/122 §12 S1 found only idempotent fills and hooked writes [verified there]; I-124-9 confirms C1 has no other consumer |

## 12. Ledger

Open claims: docs/51 §166 (I-124-1 to I-124-9). S4 results (I-124-2, -3, -4 verified; I-124-8 refuted as worded, design changed) and the new rows I-124-10 and I-124-11: docs/51 §169. S4 remainder (L3 list, field writers, request objects; I-124-12 to I-124-15): docs/51 §176.

## 13. Static step S4 results (2026-10-20)

Sources: `tools/managed-callgraph.ts callers` over the call-graph index of the Steam 1.67 script assemblies (`game-copy/analysis/callgraph`, built by `managed-callgraph.ts build` from `tools/extract-assemblies.ts` output), the decompiled class library in `game-copy/decompiled` (read locally, not quoted), and the interaction tuning (ITUN, type `0x03B33DDF`) of `Game/Bin/Gameplay/GameplayData.package` read in place from the Steam install (4,128 rows; docs/121 §8.4 found no ITUN in any other installed package). No build, no game run.

### 13.1 B14 call sites (I-124-2) [verified]

- `Autonomy.ScoreInteractionsForMetaObject` (private, non-virtual, instance) has exactly one caller in the index: B13 `ScoreInteractionsForMetaAutonomy`. No delegate creation (`ldftn`) of it. B13 itself has one caller, `ScoreInteractionsOnObjects` (the manager search).
- B13 calls B14 from 4 sites: the loop over a copy of the public meta objects plus `mLotsKnownAbout` (skips objects with `HasBeenDestroyed`; only when `CanRunFullMetaAutonomy`), then, only when `CanRunLocalAutonomy`, the actor's `LotCurrent`, `LotHome` (if different from `LotCurrent`) and `CareerLocation`. The request (docs/122 §14.1) must list the last three too, or they simply run the game's B14 (the "not in the request" row).
- A lot can reach B14 twice in one search (in `mLotsKnownAbout` and as `LotCurrent`/`LotHome`). Vanilla's second pass is a no-op through the C1 check table (`InteractionCheckTable.TryAdd` per pair). The consumer behaves the same: selected pairs are already in the table, unselected ones are skipped again. No extra rule.
- C1 `CalculateScoreAndAddToCandidates` is also called directly by B13 for the three go-home pairs (`GoHome`/`GoToVirtualHomeMeta`, `StayHome`, `GoOutToYard`) and by B16 (`ConsiderBringingToddlersInside`, `ConsiderBringingPetsInside`, between the public loop and the lot sites). These never pass through B14, so they stay live, as §2.2 assumed.
- **Design change:** B13 can yield between objects (every 5th object, when its update stopwatch is past its limit, `YieldIfPossible`). The rest of the loop then runs one or more ticks later, older than the one-tick view §3.1 allows. The §2.4 table now has a per-call tick check: from the first B14 call after a yield, every remaining object runs the game's B14. Counted under `alObjLive` (new reason `late`).

### 13.2 `HowMuchItWantsSimToCome` (I-124-3) [verified]

- Implementors: `Lot` (plus its private per-venue-type overload, called only from its own public one), `RabbitHole`, `HomeMetaObject`, `WorkMetaObject`, `GardeningMetaObject`, `WorldPondHelper`, `BakeSaleTable`. No direct call to any implementation; the only call site is the interface call `IMetaObject.HowMuchItWantsSimToCome` in `MetaInteractionScoredByVenue.CalculateScore`.
- That `CalculateScore` is reached only through `IOverridesCalculateScore.CalculateScore`, whose only caller is `InteractionObjectPair.TestAndCalculateScore` (C2), after the definition's `Test` returned `Pass`, and only when the search is not in its local phase. B13 itself (including `GetDesiredNumSimsForMetaObject` and `CalculateNumSimsAtHomeOrGoingHome`) never calls it.
- So an unselected pair skips the call entirely (the §8.3 saving holds), and a pair that fails `Test` never made it in vanilla either.

### 13.3 `AlwaysChooseBest` (I-124-4) [verified for the shipped tuning]

- The flag is `InteractionTuning.AlwaysChooseBest`, set only by the tuning parser from the `alwaysChooseBest` attribute of a tradeoff's `ScoringFunction` element. No other store to the field in any script assembly. It is read only in `BestScores.Add` (true = remembered-best list instead of the priority queue).
- Tuning lookup (`AutonomyTuning.GetTuning`) tries the target type, then its base types up to `GameObject`, then the definition's generic base; every one of the 12 allow-listed definitions (and the excluded `VisitCommunityLotWith`) has exactly one ITUN row, on `Lot`, `GardeningMetaObject`, `Restaurant`, `CityHall`, `Hospital`, `RabbitHole` (Stadium), `ShoppingRabbitHole` or `ShowVenue`, and all 13 say `alwaysChooseBest="False"`.
- 61 of the 4,128 rows say `True`; all are object interactions of the local phase (toilets, cleaning, trash, hay, sand play, booby traps and similar), none a meta definition.
- **Design change:** §2.5 step 3 is dropped. A mod tuning package can still set the flag, so the consumer checks the pair's runtime tuning and sends that object to the game's B14 (§2.4 guard row). One field read per selected object.

### 13.4 Actor state the tests read (I-124-8) [refuted as worded; design changed]

The 12 definitions' `Test` paths (the `InteractionDefinition.Test` wrapper, `CheckAvailability` with the shipped tuning values, `CheckAvailabilityOnLot`, `CommonTests`, the meta `SpecialCaseTests`, the definitions' own tests and `SharedVisitTests`/`SharedVisitVenueTests`/`Lot.CommonVisitTest`) read this actor state:

| Group | Actor state read | Event? |
|---|---|---|
| tuning, all 12 | age and species flags; lot rules: owned residential lot, daycare situation, `IsAllowedInRoom`, `IsStray`, greeted on lot, `Household`, `LotCurrent`, outside / public room; tomb room; `IsSelectable` for `DisallowPlayerSim`; `WorkMotive` | age: yes; the rest mostly no |
| tuning, 2 date definitions | required trait `CapacityToLoveChip` (bots) | no general one |
| tuning, not used by the 12 | motive, mood, skill and career thresholds, buff and occult restrictions, `funExit` (all `None`/`Ignore`/`False` in the 12 rows) | - |
| definition tests | `IsSelectable` + the player's autonomy level option; `LotCurrent`, `LotHome`, `IsNPC`, teen or below, `AssignedRole`, `IsNeighborOfPlayerWhoIsLastOneAtHome`, `IsGreetedOnLot`, `LastAutonomousVisitDate`, `Occupation.HasJobAtLot`, `Household` (vacation lot ownership), celebrity lot access; plus the clock: venue hours, `IsTooLateToVisit`, restaurant `IsOpen` | few; the clock has none |
| meta score (C2, live for every selected pair) | `LotCurrent`, `LotHome`, vampire + night, celebrity flag and level, `IsRich`, traits, `PreferredVenues`, relationships to Sims on the lot | ranking only |

So a "short list of `EventTracker` ids" cannot cover what flips a `Test` in one tick: greeted state, room permissions, lot rent, the player's autonomy level and above all the clock (venue opening and closing, too late to visit) change without any actor event, and the buff/occult/outfit events the design listed are read by none of the 12. **Design change [design]:** no event listener. The request stores the cheap identity fields (§3.1 row: age and species, `LotHome`, `Household`, `IsSelectable`, `IsNPC`, `AssignedRole`, `Occupation`, `CareerLocation`, the chip trait for the two date definitions) and the resume compares them; any difference drops the whole result. Everything else is the accepted one-tick staleness of §3.3: a pair the worker rejected that now passes is missed for this one search (the next search sees it); a pair the worker passed that now fails is re-tested live and drops out as in vanilla. The clock flips are the expected main cause; `aut-live-venue-hour` and the G7 diff records (cause `clock`) size them (I-124-10).

Effect on effort: the ts3fixes row loses the listener and gains the compare and the per-call tick check; the total stays ~10-18 h.

### 13.5 L3 list for the consumer path [verified callees; design for the verdicts]

Source: direct callees (edge kinds `call`, `cv`, `virt`, `iface`) of B13, B14, C1, C2, `BestScores.Add`/`ChooseInteraction`, B17 and D2/D4 in the call-graph index (`managed-callgraph.ts query --depth 1` and `callers`). Owner of all 18 methods: `Sims3GameplaySystems`, MVID `3681889397f4aa44b399a2926b2454fc` (the allow-list MVID, `docs/data/autonomy-allow.csv`), so the session MVID guard already covers a replaced assembly; L3 covers run-time swaps. Token = MethodDef row in that image (from the index's row order). The checker resolves each method by type and exact signature at load, as docs/104 §5.4 does, not by name: `AutShadow.AllL3Ok` matches by name, which would put both `CalculateScore` overloads under our own record hook's whitelist. A hit on any row turns go-live off for the load (one log line; every later search counts `alDrop` reason `other`); the pilot's own sets keep their per-definition `skip` verdict for shadow modes.

| # | Method | Token | In the pilot set? | What a mod swap breaks |
|---|---|---|---|---|
| 1 | `Autonomy.ScoreInteractionsOnObjects(CommodityKind, bool)` (B13's only caller; called by `RunAutonomy`, `FindBestAction`, `FindActions`) | `0x06011E9B` | no | a replacement can call B13 out of vanilla's order (meta before local, twice, or with `BestScores` cleared after it), so the margin check's "local pairs already in `BestScores`" premise and the one-request-per-search rule no longer hold |
| 2 | B13 `Autonomy.ScoreInteractionsForMetaAutonomy` | `0x06011EA5` | no (hooked) | our prefix is itself a swap; a mod swap means one of the two is lost. Whitelist: our hook class only |
| 3 | B17 `Autonomy.YieldIfPossible` | `0x06011EBA` | no | the resume copies B17's `SearchInterrupted` / `ActorDestroyed` returns; a changed B17 makes the copy diverge from the rest of the search |
| 4 | B14 `Autonomy.ScoreInteractionsForMetaObject` | `0x06011EA6` | no (hooked) | as 2 for the go-live hook; a mod body here (own commodity filter, own ad list) would be bypassed for consumed objects |
| 5 | C1 `Autonomy.CalculateScoreAndAddToCandidates` | `0x06011E86` | no | the consumer sends only the selected pairs through it; a mod that counts, logs, filters or collects every meta pair there sees a subset |
| 6 | C2 `Autonomy.CalculateScore(InteractionObjectPair)` | `0x06011E88` | self-check only | a mod that rescales or adds score here changes live scores but not the worker's, so the top-N picks the wrong pairs (the margin check sees it only as lower live scores). Whitelist: the docs/122 Mode W / `Dry` record hook, on this overload only |
| 7 | `InteractionObjectPair.TestAndCalculateScore` | `0x06004D6A` | yes (shared) | worker and live scores differ, as 6 |
| 8 | `InteractionObjectPair.get_Tradeoff` | `0x06004D62` | yes (shared) | B14's commodity filter (applied by the worker) differs from live |
| 9 | `InteractionObjectPair.get_InteractionDefinition` | `0x06004D60` | yes (shared) | allow-list type check and filter read another definition than live |
| 10 | `InteractionObjectPair.get_Target` | `0x06004D61` | yes (shared) | the lot passed to `MetaAdSatisfiesCommodity` differs |
| 11 | `Tradeoff.SatisfiesCommodity` | `0x060050BF` | yes (shared) | as 8 |
| 12 | `InteractionObjectPair.get_Tuning` | `0x06004D64` | no | the §2.4 `AlwaysChooseBest` guard reads it; a swap that returns other tuning hides a mod-set flag, and the remembered-best list misses pairs |
| 13 | `BestScores..ctor` | `0x06006359` | no | sets the heap capacity (10, docs/59 D1) that `TopN` = 20 and `s10` assume; a wider heap shows the unselected pairs' absence in D2's input |
| 14 | D1 `BestScores.Add` | `0x0600634C` | no | a mod that keeps more than the top 10 or ranks by its own rule relies on seeing every pair |
| 15 | `BestScores.AddInteractionOnlyIfBest` | `0x0600634E` | no | as 14, for the remembered-best list |
| 16 | D2 `Autonomy.AssignProbabilitiesAndChooseInteraction()` | `0x06011E85` | no | a mod D2 that reads beyond `BestScores` (the check table, a rescan of the meta objects) sees fewer scored pairs; §5's equal-in-distribution argument covers only vanilla's D2 |
| 17 | `BestScores.ChooseInteraction(float, DecisionSnapshot)` | `0x0600634F` | no | as 16 |
| 18 | `BestScores.ChooseInteractions(int)` (multi-pick, `FindActions`) | `0x0600634D` | no | as 16, for the side entry point that takes several candidates |

Not on the list [design], with the reason:
- `CanRunAnyMetaAutonomy` `0x06011EA4`, `CanRunFullMetaAutonomy` `0x06011EA3`, `CanRunLocalAutonomy` `0x06011E74`: the request (by reflection) and the game's B13 call the same body, so a swap acts on both; a different answer at t+1 only makes objects "not in the request".
- `InteractionCheckTable.Allocate` `0x06011EE9` / `TryAdd` `0x06011EED`, the `PriorityQueue` methods (`.ctor`, `Add`, `InsertElement`, `ReplaceTopElement`, `ReSortAfterReplacingTopElement`, `SmallestChildIndex`, `get_SmallestElement`, `Element`, `get_CurrentNumElements`), `BestScores.ChooseBestInteraction` / `AssignProbabilitiesAndChooseInteraction(float, DecisionSnapshot)` and `InteractionCandidate.CompareTo`: reached only through rows 5 and 13-18, and they act the same on a subset as on the full set.
- `IMetaObject.get_MetaAds` and `IMetaInteractionDefinition.MetaAdSatisfiesCommodity`: already checked per implementor and per definition by the pilot (`kImplCallees`, `kDefCallees`); a hit sends that object or definition to the game's B14, the right fallback here too.
- D4 `ConsiderPerformingAutonomousAction` `0x06011E95` and D5: they see only the chosen instance.
- `Simulator.Sleep` / `CheckYieldingContext` (`SimIFace`): every yield in the game uses them; our extra yield behaves like B13's own Y2 under any swap.
- The actor getters of the §3.1 compare: both sides of the compare go through the same getter.

Cost: 13 more `MonoMethod` checks per sweep (docs/104 §6: nanoseconds each), in the pilot's load / world load / `ForceJIT`-change sweep.

### 13.6 Writers of the compared actor fields (item 2) [verified writers; inferred timing]

Backing state from the getters' field reads in the index, writers from its field-write tags, their callers from `callers`. "Window" = from the B13 post in tick t to the resume in t+1: the rest of tick t's script slice and the start of t+1, where every other task (the actor's own interaction task, alarms, `RoleManagerTask`, situations, UI commands) can run on the sim thread.

| Field (getter path) | Backing state | Writers (callers) | In the window? | Same identity, changed meaning? |
|---|---|---|---|---|
| age, species (`SimDescription.Age`, `.Species`) | `SimDescription.mSimFlags` | `set_Age` (`AgeUp`/`AgeDown.RunNonPersistable`, `AgingState.PreInstantiateSim`, `UpdateFromCasData`, `OccultRobot.MakeRobot`, `GeneticsPet.MakePet`), `set_Species`, `set_AgeGenderSpecies`, `Set`, `Init`, `UpdateFromOutfit`, `ImportContent`, `MergeTravelInformation` | yes: a birthday runs in the actor's own interaction task while its search is parked | no: compared by value (age and species bits only, so gender and handedness writes to the same word do not drop) |
| `Household` | `SimDescriptionCore.mHousehold` | `SimDescription.OnHouseholdChanged` (from `Household.Add`/`Remove`/`AddTemporary`/`RemoveTemporary`/`Cleanup`/`ImportContent`, `PetPool.AddPet`, `NPCMermaidManager.RefreshMermaidHousehold`, `AlienUtils.AlienRefreshCallBack`, travel import, moving model), `Dispose` | yes (adoption, move-in, marriage, mermaid or alien refresh) | members and funds change under the same reference; the 12 tests read the household for lot ownership (covered by `LotHome`) and `IsServiceNpcHousehold` (fixed per household). Vacation and venue ownership is read from `Household.ActiveHousehold.RealEstateManager`, not the actor's household: a purchase in the window is accepted staleness (§3.3), G7 cause `actor` |
| `LotHome` | `Household.mLotHome` via `SimDescription.LotHome` | `Household.SetLot` (`Lot.MoveIn`/`MoveOut`, `Household.Init`, `Sim.OnLoad`, `EditTownModel.EndMoveRotateLotContents`), `Household.Cleanup` | moving UI and Edit Town (game state rule drops those); `MoveIn`/`MoveOut` from interactions possible | no: compared by reference (the `Lot`); lot contents are object level (§3.2) |
| `IsSelectable` (`IsNPC` = not selectable) | `PlumbBob.mSelectedActor` (`Sim.ActiveActor`), the actor's `Household`, `SimDescription.mFlags` bit `IsNeverSelectable` | `PlumbBob.DoSelectActor`, `PlumbBob.CheckForChangeInActiveHousehold`, `SimDescription.SetFlags`, the household writers above | yes (the player selects another Sim or household) | no: compared by value |
| `AssignedRole` | public field `SimDescription.AssignedRole` | `Role..ctor`, `Role.RemoveSimFromRole` (from `RoleManager.UpdateAndGetRolesThatNeedPeople`, `Pregnancy.HourlyCallback`, `Sim.OnBecameSelectable`, tourist / explorer / time-traveler `KillSim`, register and barista `Dispose`, `HouseParty.PushComeToParty`, others), `RoleManagerTask.FillInWorldRole`, `RoleManager.DestroyAllRoleBotsBeforeSwitchingWorlds` | yes (`RoleManagerTask`, hourly callbacks) | the tests read null-ness and `Role.Type`; `Role.mType`'s only writer is the `Role` constructor, so the reference compare is complete. `Role.IsActive` changes with role hours under the same reference, but no caller of `SimDescription.HasActiveRole` is a meta definition test [inferred, I-124-14] |
| `Occupation` | `CareerManager.mJob` via `SimDescription.Occupation` | `CareerManager.AssignJob` (from `Occupation.AssignOccupation`), `ImportContent`, `OnLoadFixup` | yes (join or quit from interactions and the phone) | **yes:** `HasJobAtLot` reads `Occupation.mJobs`, written by `AddJob` (`TryCreateJob`, `PerformanceCareer.CreateGigFromSpec`), `RemoveJob` (`Job.ShutDown`, show stage `Tend`, others) and `RemoveAllJobs` (`RegularWorkDayStartAlarmHandler`, firefighter work-day end, `AcademicCareer.CreateCoursesForToday`, `LeaveJobNow`, others). Hence `JobCount` and `CurrentJob` in the compare; a same-count swap of other jobs in one tick stays accepted staleness (it happens at work-day alarms, so G7 sees it as cause `clock`) |
| `CareerLocation` | `Career.mCareerLoc.Owner` (`Sim.CareerLocation` returns the rabbit hole) | `Career.set_CareerLoc` (virtual, from `TransferBetweenCareerLocations`, `SetAttributesForNewJob`, `LeaveJobNow`, `School.LeaveJobNow`, `CareerLocation.Dispose`, `OnLoadFixup`); `CareerLocation.Owner` is written only by its constructor | yes (transfer, new job, quit) | no: compared by reference; `Dispose` comes from build/buy (game state rule) |
| `CapacityToLoveChip` (2 date definitions) | the actor's `TraitManager` trait set | `TraitManager.AddElement` / `AddHiddenElement` / `RemoveElement` (bot chip install and removal) | yes, rare | no: compared by value (`HasElement`) |

So the only same-identity change of actor state that moves a `Test` verdict in the window is the job list behind `Occupation`, closed with two more reads; the vacation-property case is household-of-the-player state, not actor state. Every other write changes the compared reference or value, or runs only in a game state the existing rule drops.

### 13.7 Request objects `LotCurrent`, `LotHome`, `CareerLocation` (item 3) [verified in the source]

The docs/122 request already lists them: `AutShadow.TryRequest` (ts3fixes) adds, after the public meta objects and `mLotsKnownAbout` (only when `CanRunFullMetaAutonomy`), the actor's `LotCurrent`, `LotHome` when it differs from `LotCurrent`, and `CareerLocation`, all only when `CanRunLocalAutonomy`: the same 4 sites and order as B13 (§13.1). `AddObj` also rows each object's lot. No change to the object list. Two small differences, both safe: `AddObj` skips destroyed objects on every site (B13 checks only in the public loop) and stops at `kMaxObjs`; such an object is "not in the request" and runs the game's B14.

The go-live change to the request is the §3.1 compare, not objects: `Request` gains the age and species bits, `LotCurrent` (already read for the list; stored for the §3.1 lot rule), `LotHome`, `Household`, `IsSelectable`, `AssignedRole`, `Occupation`, `JobCount`, `CurrentJob`, `CareerLocation` and the chip flag, filled in `TryRequest` before `Post`.

## 14. Go-live plan after the first in-game Mode R (2026-10-29)

Static update from measured runs; no build or game run for this section. Ledger: docs/51 §385. Sources: docs/122 §14.12-§14.15 (Mode R rounds mono-cost3 to mono-cost6), docs/30 §15 (GC lock entry cost, lazy gate), docs/51 §376-§383, PLAN.md "Mono goal 2026-10-29", reports `probe-out/queue/monocost5-1029-report.txt` and `monocost6-1029-report.txt` (`tools/harness/mono-cost-report.ts`), and the v0.2.4 release sim budget `probe-out/queue/simbudget-v024-report.txt` (docs/160). mono-cost7 (0.36.112: x87/MXCSR fix, `ColdIc=1`, cut phase counters) is running in tonight's chain; its numbers are not in this section.

### 14.1 What Mode R proved [measured unless marked]

All from mono-cost6 (`[AutShadow] Mode=1 Sample=4 WindowWaitUs=5000`, `[MonoThread]` P3+P4, LazyGate off), BP rerun and IP, 10 min each, 0 crashes, 0 worker exceptions.

| Fact | BP | IP | Note |
|---|---|---|---|
| Worker jobs (requests) | 835 | 856 | ~1.6 per s at `Sample=4`; 0 `Begin busy`, 0 `noWorker` |
| Compared pairs, bit-equal (A0 of compared) | 25,071 of 25,112 = **99.84%** | 36,775 of 36,793 = **99.95%** | A3 0 on both worlds |
| A1 / A2 | 8 / 33 | 6 / 12 | all in two groups: G1 x87/MXCSR rounding (1-~200 ULP, cause inferred, fixed in 0.36.112) and G2 gardening count read (verified, fixed); docs/122 §14.15 |
| Hit rate = compared share of allow-listed pairs | 78.7% (A4 21.3%) | 84.2% (A4 15.5%) | off-allow-list skips (BP 26,813, IP 39,033) are out of A0-A5 since 0.36.112; G-A4 limit is A4 < 20% |
| A4 by reason (pairs) | row 4,784, desirefn 1,423, rain 308, simstale 142, list 111 | row 4,504, desirefn 1,344, rain 712, simstale 27, list 66 | `row` = first reads of objects with no rows (0.36.112 adds object and Sim-parent rows); `desirefn` = H5, `rain` = H11 by design |
| Overruns (worker not done at window close) | 12 (1.4%) | 104 (12%) | with the 5 ms window wait (Mode R only); go-live consumes one tick later (§2) |
| Answer table | max 6,817 rows, evicted 0, refused 0 | max 5,011 rows, evicted 0, refused 0 | 8,192-row table is large enough on both worlds |
| Lazy GC lock, idle worker (`bp-mt-idle-worker-lazy`, mono-cost5) | **0 locked entries** in 529 s (vs 290.6 M = 484k/s with the gate always on, `bp-mt-idle-worker-rerun`) | - | lazy gate with jobs has not run yet: mono-cost6/7 run LazyGate off |

What it does not prove yet: the x87 fix (A1 = A2 = 0) and the cut split (mono-cost7, running); the lazy gate under jobs; anything about the apply step (G7-G9).

### 14.2 Measured costs on the sim thread

Per request = per sampled meta search at `Sample=4`. "At Sample=1" multiplies by 4 [inferred: sampling is 1 in 4 of eligible searches, so cost and moved work scale together].

| Cost | BP | IP | Kind | Paid by go-live? |
|---|---|---|---|---|
| Cut + snapshot | 4.3 ms/s, avg **3.07 ms** per cut (max 16.5 ms) | 2.3 ms/s, avg **1.63 ms** (max 5.5 ms) | measured | **yes**, once per search: 17.2 / 9.2 ms/s at Sample=1 [inferred] |
| Guard thunks (all EA ICs) | 104.2k calls/s x 17 ns = 1.8 ms/s | 93.6k calls/s x 17 ns = 1.6 ms/s | calls measured, ns from selftest = inferred | yes, fixed (independent of Sample) |
| GC lock, gate always on | 555k entries/s x 68 ns = 37.8 ms/s | 536k/s = 36.4 ms/s | entries measured, ns selftest (docs/30 §15) = inferred | no: go-live requires `LazyGate=1` |
| GC lock, lazy gate | idle: 0 entries [measured]; with jobs: locked only while the worker runs, ~2% of the second at Sample=1, so ~11k entries/s, ~0.8 ms/s [inferred] | same | contended waits measured at Sample=4: 21 waits, ~0 ms | yes, small |
| Reference re-score | 5.6 ms/s (~3.5 ms per request) | 7.1 ms/s (~4.4 ms) | measured | **no**: Mode R test cost only (it is the work go-live moves) |
| Window wait (`WindowWaitUs` 5000) | 4.6 ms/s | 6.2 ms/s | measured | **no**: Mode R test cost only; go-live does not wait |
| Worker scoring (on the worker thread) | 4.5 ms/s (~2.9 ms per job) | 6.1 ms/s (~3.7 ms) | measured | not a sim cost |

The mono-cost report's stop-rule line ("worker adds 40-44 ms/s, STOP") is the gate-always-on configuration; it is not the go-live configuration. With the lazy gate the fixed cost falls to guard + lock ~2.4-2.6 ms/s [inferred], and the cut becomes the cost that decides.

### 14.3 Expected gain [inferred]

Work moved off the sim thread = the eligible meta scoring the reference measures (5.6-7.1 ms/s at Sample=4, so **22-28 ms/s at Sample=1**, matching §8.3's ~23 and ~50-60% of the 44-51 ms/s autonomy-search owner in the v0.2.4 sim budget) times the hit rate (79-84%): **~18-24 ms/s**. Costs at Sample=1: cut (above), guard 1.6-1.8, lazy lock ~0.2-1, live re-score of the selected bands at apply 2-6 (§8.3, pairs per search still unmeasured), request build 1-3.

| Cut per search | Cut ms/s at Sample=1 | Net on the sim thread |
|---|---|---|
| today (1.6-3.1 ms) | 9-17 | **-11 to +10** |
| ColdIc estimate (~1.0-1.3 ms BP, docs/122 §14.15) | 4-8 | **-2 to +15** |
| target <= 0.5 ms | <= 3.3 | **+4 to +18** |

All three rows are [inferred]; the cut row is the only one with a measured input that mono-cost7 replaces. PLAN.md's goal (>= ~10 ms/s net) needs the target row.

### 14.3a Gate G-C as a share: cut <= ~25% of the scoring it moves (2026-10-29)

The 0.5 ms per search target assumed one cut per search and a fixed ~5 ms of scoring per search. Neither holds any more: docs/122 §14.18 shares one cut among every request posted before it (cut cost per second scales with frames that have a B13 call, not with searches), and the scoring per search differs by world (mono-cost9 reference ~4.4 ms per request on IP, ~3.4 on BP [measured: refUs / requests]). What decides the plan is the net per second: moved - cut - fixed costs. So G-C is now **cut ms/s <= ~25% of scoring moved ms/s**, both at the go-live rate [inferred, `mono-cost-report.ts` "go-live estimate" line: moved = reference us per request x every B13 call per second x the answered share (1 - A4); cut = cut ms x frames with a B13 call per second]. Why 25%: the fixed costs (guard 1.6-1.8, lazy lock ~0.2-1, apply re-score 2-6, request build 1-3 ms/s, §14.3) take another ~5-12 ms/s, so at the 22-28 ms/s this plan moves, a cut at 25% (5.5-7 ms/s) still leaves net ~+3 to +17 ms/s, the same band as the old target row; a cut at 50% leaves at most ~+9 and often <= 0 (the old "today" row). Stop rule: cut > 50% of moved after two cut rounds stops the go-live (as the old "> 1 ms per search" rule did).

### 14.4 Build plan, gates and stop rules

| Step | Work | Gate (pass) | Stop rule | Agent hours | Game jobs |
|---|---|---|---|---|---|
| S0 (running) | mono-cost7 Mode R: x87 fix, ColdIc, cut phases | G-A4: A2 = A3 = 0, A1 = 0 or explained, A4 < 20% of allow-listed pairs on BP and IP; 0 crashes; second clean run per world (crash rule) | an A2/A3 record that the snapshot cannot explain: fix before S1 | - | 2 (+2 reruns) |
| S1 cut to budget | from mono-cost7's split: per-lot list generation if `bodyListUs` leads, cold rows, row churn; then one Mode R round with `[MonoThread] LazyGate=1` (first lazy run with jobs) | **G-C** (§14.3a): cut ms/s <= ~25% of the scoring moved ms/s at the go-live rate on BP and IP (`mono-cost-report.ts` go-live estimate line; was: cut avg <= 0.5 ms per search); lazy `lockedCalls` only inside job windows (<= ~20k/s), `lazyOn` = `lazyOff` +-1, 0 GC crashes, Mode R still G-A4 | **cut > 50% of the scoring moved on BP after 2 cut rounds: stop go-live** (was: > 1 ms per search; net cannot clear noise at Sample=1); keep `[AutShadow]` as a correctness tool and move to docs/139 fast paths | 2-4 | 2-4 |
| S2 build Mode=3 | §10 rows: native ready-at-window-close, `ObjStale` IC, `VerifyEvery`, `Dry`; ts3fixes B13 yield + resume checks, bands, B14 consumer, actor field compare, tuning generation; offline selftests | selftests + `tools/selftest-all.ts` green | - | 6-10 | 0 |
| S3 G7 dry apply | `Mode=3 Dry=1` BP, IP, SV + `bp-autlive-edit` | G7 (§8): top 10 equal >= 99%, top 1 >= 99.5%, every diff has a cause; 0 crashes in 2 clean runs | top-10 match < 99% after widening `TopN`/`Margin` once: stop (one-tick view too stale) | 1-3 | 4-6 |
| S4 G9a cost first | clean-night ABBA `--ab autlive`, Sample=1, BP + IP, sim-budget report per arm (docs/160) | net sim-thread ms/s gain (off - on, sim busy ms/s; autonomy-search owner down) outside the noise band of the pooled ABBA pairs, it/s not down > 2% | **PLAN.md rule: no net gain outside the noise band in 2 build rounds = ship off** | 1-3 (fix round) | 2 per round |
| S5 G8 behaviour | seeded on/off, BP + IP, 2 x 30 min each | G8 (§8) | a biased definition: exclude it, rerun once | 1-2 | 4 |
| S6 G9b stability | 2 clean runs per world (BP, IP, SV) with one save + reload during a meta burst | 0 crashes in 6 runs, `asUnguarded` 0, `asWorkerClassInit` 0 | any crash: crash rule, then rerun | 1-2 | 6 |
| **Total** | | | | **~12-23 h** | **~18-20 jobs**, 1-2 clean nights (S4) |

Order change against §8: the cost check (S4) runs before the behaviour gate (S5), because cost, not correctness, is what can kill go-live (Mode R already shows bit-equal scores). Noise band: single 10-min runs are not enough to judge 10 ms/s. The BP Mono bucket ranged 325-378 ms/s across three mono-cost5 arms with the same features off [measured], so S4 judges by pooled ABBA pair differences (both orders), like `hitch-ab.ts` does for hitches.

### 14.5 Next offload candidates, ranked by the v0.2.4 owner split

The Mono script owners on the v0.2.4 release build (sim-budget, BP / IP / SV ms/s, measured) are Sim interaction step 79 / 52 / 71, SimUpdate (motives, buffs) 57 / 61 / 63, Lot.Simulate / reactions 51 / 67 / 50, autonomy search 44 / 51 / 50 and native event callbacks 31 / 24 / 28. docs/129's candidates map onto them. Their movable ms/s and hours are from the 10-17/18 runs, not re-measured on v0.2.4 [inferred].

| Rank | Candidate | Owner today (ms/s) | Movable ms/s | Hours | Note |
|---|---|---|---|---|---|
| 1 | Autonomy meta scoring (this plan) | autonomy search 44-51 | ~18-24 | 12-23 | pays the cut, guard and lock that later candidates share |
| 2 | Sim environment score (docs/129 §2.1) | SimUpdate 57-63 | ~14 | 8-12 | read-only precompute; adds rows to the cut, so it needs S1's cut budget |
| 3 | Reaction broadcaster candidate set | Lot.Simulate / reactions 50-67 | ~10 | 7-10 | read-only precompute with a radius margin |
| 4 | Motive decay | SimUpdate 57-63 | ~7 | 6-9 | shadow-then-apply |
| 5 | Zombies on lot / closest visible lot | Lot.Simulate (IP ~6) / camera | ~3.5 / ~2.7 | 4-6 / 3-5 | small |
| 1b | Autonomy local phase (docs/126 tier 2) | autonomy search | +27-31% of scoring on top of rank 1 | 14-22 | after rank 1 is live |

Not movable with this machinery: Sim interaction step (writes) and native event callbacks (order-dependent), together ~75-110 ms/s of the Mono bucket (docs/129 §1). If S1 or S4 stops the plan, the interpreter fast paths (docs/139: sub-run fusion ~2-8 ms/s, call/return ~15-30 ms/s [inferred]) are the next Mono source of sim-thread time, with no worker, cut or staleness risk.

## 15. Offline consume cost check (2026-10-30)

Question: does the go-live net gain that Mode R predicts (report line "go-live estimate": BP ~+14, IP ~+27-31 ms/s = scoring moved - cut - guard) survive the work go-live keeps on the sim thread, mainly the game's C1 (live `Test` + score into `BestScores`) for the worker's selected pairs (§2.4-§2.5)? Offline only: no build, no game run. Inputs are the four mono-cost11 Mode R runs (0.36.119, `bp-autshadow-r`, `ip-autshadow-r` and their reruns, 10 min each, A1-A3 0). Report: `node tools/harness/mono-cost-report.ts probe-out/queue/mono-cost11-2026-10-30.jsonl`, new line "go-live after consume".

Why no monohost measurement: monohost (docs/119) loads the script assemblies without a world. C1 needs a live actor, lots and meta objects, and the docs/123 world image that would provide them is not built. So the per-pair cost comes from Mode R's sim-thread reference (`refUs`), which runs the same `Test` and score bodies on the real heap, and everything below that depends on it is **[inferred]**.

### 15.1 Sim-thread steps go-live adds or keeps

| Step (§) | When | Covered by | Cost used here |
|---|---|---|---|
| B13 prefix: eligibility, request build (`AddRow` per meta object), `Post` (§2.2-2.3) | per eligible search | not timed apart (Mode R pays it inside the manager task today) | in the fixed 0.15 ms per search [inferred] |
| `Simulator.Sleep(0)`, resume, B17-equivalent checks, actor field compare (~10 reads) (§2.2, §3.1) | per search | no counter | in the fixed 0.15 ms |
| Selection sort, `ObjStale` per meta object, identity check per selected ad (§2.4-2.5, §3.2) | per search | no counter (`alApplyUs` planned, §7.2) | in the fixed 0.15 ms |
| Cut at the `0xD81840` return | per frame with a B13 call | `cutUs`/`cuts` x `metaTicks` [measured] | BP 1.19-1.30 ms per cut, 8.7-9.4 ms/s; IP 0.69-0.73 ms, 5.1-6.0 ms/s |
| Guard thunks | every EA IC call | `guardSim` [measured] x 17 ns [inferred] | 1.3-1.6 ms/s |
| Lazy GC lock | worker windows | mono-cost11 ran `LazyGate` off | not in the numbers (~0.2-1 ms/s, §14.2) |
| **Live C1 for the selected pairs** (§2.4) | per selected pair | no counter (`alPairsC1` planned, §7.2) | §15.3 |
| Margin bands 2-3 (§2.5) | when the live 10th score is close | `alBand2`/`alBand3` planned | not in the numbers (each band = up to another `TopN` pairs) |
| Game's B13 after a dropped result (§3.1, §4) | per dropped search | overruns: `overrun`/`req` [measured]; other drop reasons have no counter yet | overrun searches move nothing and still pay the cut |
| Game's B14 per object (abort, skip, stale; §2.4) | per object | A4 share [measured], already out of "scoring moved" | - |
| Game's B14 for an object with an off-allow-list ad (§2.4 "mixed") | per object | not counted: Mode R skips off-list pairs per pair (29-43 skipped pairs per search next to 32-48 classified) | not in the numbers: lowers the moved share by an unmeasured amount |

### 15.2 Inputs per run [measured]

| Run | Scoring per search R | Allow-listed pairs per search P | Per pair | Answered | B13 calls/s | Overrun searches |
|---|---|---|---|---|---|---|
| BP | 4.20 ms | 38.8 | 108 us | 79.0% | 7.4 | 0.7% |
| BP rerun | 4.31 ms | 38.4 | 112 us | 80.2% | 7.3 | 1.3% |
| IP | 5.19 ms | 48.0 | 108 us | 86.1% | 8.7 | 17.3% |
| IP rerun | 5.55 ms | 32.3 | 172 us | 85.9% | 7.0 | 44.2% |

P counts the classified pairs (pass, `Test` failed, aborted) that passed the commodity filter; overrun searches classify none, which is why the IP rerun shows fewer pairs per search and a higher per-pair cost.

### 15.3 Model [inferred]

- A selected pair costs R/P x (τ + (1 - τ)/p): its own `Test`, plus the score share, which only passing pairs carry. τ = 0.7 = the `Test` side of meta pair scoring in the docs/75 manager samples. Of 670 meta samples, about 460 are in `CheckAvailability`, `CommonTests`, the `VisitCommunityLot`/`VisitLot`/garden `Test` frames and other base checks, and 156 are in the venue score override, which calls `HowMuchItWantsSimToCome` only for passing pairs (§13.2). The sample counts are measured; splitting them into Test and score is inferred. Moving τ to 0.6 or 0.8 changes the nets below by about 2 ms/s.
- The pass share p (worker code 0 among classified pairs) has **no counter**: Mode R logs per-pair codes only for A1-A3 records. So every result spans p = 0.25 / 0.5 / 0.75 / 1.
- Selected pairs per search = min(`TopN`, p x P x answered). The C1 check-table and `BestScores.Add` overhead is paid by selected pairs as in vanilla. Unselected pairs skip it, a saving left out here, so the estimate is conservative on that side.
- Net = (1 - overrun) x moved - cut - guard - 0.15 ms x searches/s - searches/s x (1 - overrun) x selected x pair cost.
- Upper-bound caveats: R includes one `HowMuchItWantsSimToCome` per meta object that the worker driver makes and vanilla does not (vanilla only calls it inside passing pairs, §13.2), so R and "moved" are upper bounds. The Mode R overrun rate comes from a run that executes the reference on the sim before submit and waits up to 5 ms at window close. Go-live does neither, so its overrun rate may differ.

### 15.4 Results [inferred]

| Run | Before consume (report line) | Before consume, with overruns | Consume at `TopN` 20, p = 0.25/0.5/0.75/1 | **Net at `TopN` 20** | Break-even N | Net at `TopN` 10 |
|---|---|---|---|---|---|---|
| BP | +14.1 | +12.8 | 11.5 / 15.7 / 17.4 / 15.8 | **+1.3 / -3.0 / -4.6 / -3.0** | 8.5 / 12.5 / 14.7 / 16.2 | +1.3 / +2.5 / +4.1 / +4.9 |
| BP rerun | +14.3 | +12.8 | 11.8 / 16.1 / 17.7 / 16.1 | **+1.0 / -3.3 / -4.9 / -3.3** | 8.4 / 12.3 / 14.5 / 15.9 | +1.0 / +2.4 / +4.0 / +4.8 |
| IP | +31.4 | +23.4 | 15.2 / 20.2 / 17.1 / 15.5 | **+8.2 / +3.3 / +6.4 / +7.9** | 15.9 / 23.2 / 27.4 / 30.2 | +8.7 / +13.3 / +14.9 / +15.7 |
| IP rerun | +26.8 | +11.1 | 8.8 / 12.0 / 14.7 / 13.3 | **+2.3 / -0.9 / -3.6 / -2.3** | 8.7 / 12.8 / 15.1 / 16.6 | +2.3 / +2.4 / +3.7 / +4.4 |

All values in ms/s. Break-even N = the number of live C1 pairs per search at which net reaches 0. When fewer pairs pass than N, nothing more is selected, so a low p can stay positive even above its break-even N.

### 15.5 What it means

- **Overruns:** on BP they are small (0.7-1.3% of searches, about -1 ms/s). On IP (17-44%) they cost 8-16 ms/s, more than the cut. An overrun search pays the cut and then the game's whole B13. IP's worker `stop` aborts (11-22% of aborts) belong to the same window problem.
- **Consume at the designed `TopN` = 20 takes 9-20 ms/s:** that wipes out the BP gain (net -4.9 to +1.3) and leaves IP at -3.6 to +8.2. Neither world reaches the PLAN.md goal (>= ~10 ms/s), and the S4 cost gate (§14.4) would most likely fail on BP. The cause: a search has about 8-40 passing pairs, and `TopN` 20 (twice the `BestScores` capacity of 10) re-runs most of them live, score phase included. What is left as saving is the failing pairs' `Test` and the passing pairs below the band.
- **Break-even N:** BP 8-16, IP 9-30 (lower when overruns are high). `TopN` 20 is above the BP break-even for every pass share except the lowest.
- **Before S2 (build Mode=3), in order:**
  1. Count the pass share: add one Mode R counter (worker pairs with code pass per request) in the next ts3fixes build round. That turns the p range into one number per world.
  2. Make `TopN` 10 (the `BestScores` capacity) the default and keep the margin bands as the safety net: BP +1 to +5, IP +2 to +16 ms/s. G7 then shows how often band 2 fires; each band adds up to another 10 live pairs.
  3. Bring the IP overrun rate down (worker window length and `stop` causes) before any go-live run. It is worth more than any consume lever.
  4. Mixed objects: the B14 consumer should send off-list ads through C1 itself (vanilla behaviour, same commodity filter) instead of handing the whole object to the game's B14. Otherwise the moved share shrinks by an amount Mode R does not measure.
- **Option for Rodney, not designed:** re-run only `Test` live and keep the worker's score for selected pairs that still pass (BP about +2 to +9, IP +2 to +18 ms/s at `TopN` 10-20). It drops the vanilla-exact score: the scores come from the one-tick-old view (bit-equal in Mode R on the same view, §14.1). G7's "live scores of the shared pairs bit-equal" check would then measure staleness instead.
- With the lazy GC lock (~0.2-1 ms/s) and the unmeasured band and mixed-object costs still to subtract, the honest band is: BP about -6 to +5 and IP about -4 to +16 ms/s after consume at `TopN` 10-20 [inferred]. Go-live is a viable offload step under the 2% rule only with `TopN` 10 and IP overruns fixed.

## 16. Consume design after the consume check (2026-10-30)

Rodney's decision on §15.5: `TopN` 10 with margin bands as the safety net, mixed meta objects handled per ad, a pass-share counter in Mode R (another agent is adding it), and the IP overruns fixed by the cut work. Scores stay exact: every pair that can reach `BestScores` is tested and scored live by the game's C1. The Test-only option is dropped. This section replaces §2.5 steps 2 and 4 and the mixed-object row of §2.4. Static design, no build.

### 16.1 Band 1 and the band rule [design]

**Band 1.** At resume, before the game's B13 runs (§2.5 step 1), the hook sorts the worker's passing pairs (code 0) from valid objects by worker score, highest first. Ties keep request order, which is B13's object order and then `MetaAds` order. Band 1 is the first `TopN` = 10 of that list. 10 is the `BestScores` capacity [verified, docs/59 D1], so band 1 is exactly the set the worker says belongs in the top 10 if nothing changed since the cut. Band 1 pairs reach C1 inside the game's B13, through the B14 consumer, in `MetaAds` order per object (§16.2).

**Which live scores the band rule reads.** While band 1 runs, the hook records the live score of each selected pair. It uses the C2 wrapper that Mode W already installs (`AutScoreHook.ScoreHook` on `Autonomy.CalculateScore`, docs/122 §14.5), which writes the game's own result for a pair that is in the request [verified as built: `ts3fixes/src/AutShadow.cs`]. A pair whose live `Test` fails scores 0 there, as in vanilla [verified, AutShadowCore L report comment].

**The band rule.** It runs after the game's B13 returns, in the hook's postfix, still in the manager task and before D2 picks. Let:
- `s10` = the lowest score in `BestScores`. It holds local and meta pairs, and the go-home and B16 pairs B13 adds itself.
- `cnt` = the number of entries in `BestScores`.
- `w` = the worker score of the best pair not yet sent to C1.
- `drift` = the largest relative drop from worker score to live score over the pairs sent so far, max(0, (worker - live) / worker). A pair that failed live counts as drift 1.

Band k+1 (the next 10 in the sorted list) goes through C1 when either of these holds:
- (a) `cnt` < 10 and unsent passing pairs remain. In vanilla every remaining passing pair would enter `BestScores`, so one could be picked.
- (b) `w` x (1 + `drift` + `Margin`) >= `s10`. An unsent pair might beat the 10th live score, either because live scores moved down since the cut (that is what `drift` measures) or because of a tie: vanilla's heap keeps either of two equal scores depending on add order, so `>=` sends ties.

`Margin` defaults to 0 [design]. With `drift` 0 (band 1 scored live exactly as on the worker, the Mode R result on the same view, §14.1), the rule fires only when the 11th worker score ties or beats the 10th live one. That is the exact condition: with a fixed state, an unsent pair cannot enter the top 10. G7 may raise `Margin` if it finds top-10 misses that `drift` does not explain, meaning a pair the worker ranked low whose live score rose (the §3.3 case).

After each band the hook re-evaluates `s10`, `cnt`, `w` and `drift`. After `Bands` = 3 bands, every remaining worker-passing pair goes through C1 (`alBandFull`). Worker-failed and worker-filtered pairs never go through C1. Their live verdict is the only thing go-live does not compute, as before (§2.5, §5).

**How bands 2-3 reach C1.** They do not go through B14, because the game's B13 has already returned. The hook calls C1 `CalculateScoreAndAddToCandidates` itself with the pair objects saved in the request, each after the same checks the B14 consumer makes: object not stale (`ObjStale`), same ad reference, same `MetaAds` count. C1's check table (C1a) drops a pair that already reached C1 in this search, for example through a lot B13 visits twice (§13.1) [verified, docs/59 C1a]. Band order differs from vanilla's add order only for these late pairs. `BestScores` keeps the top 10 by score whatever the add order, apart from exact ties, which rule (b) already sends. So the set D2 draws from matches vanilla [inferred, G7 checks it].

**After a B13 yield** (the `late` case, §13.1): objects after the yield run the game's B14 (unchanged). Band pairs from objects before the yield still go through C1 in the postfix. Their scores are live, only their selection is older, and `drift` covers that.

**Counters** (added to §7.2): `alBand2`, `alBand3`, `alBandFull` as before; new `alBandWhy[2]` (count rule a, margin rule b), `alDriftMax` (histogram of `drift` per search), and `alPairsC1` split by band.

### 16.2 Mixed meta objects: per-ad rule at B14 [design]

A mixed meta object has ads of allow-listed definitions and ads that are not allow-listed: definitions off the allow-list, or exact runtime types that the mod guard rejects (§6). Mode R already scores such objects per pair. Off-list pairs are skipped (`kSkip`) and the allow-listed pairs of the same object are scored and compared [verified: `ScoreMeta` skips only pairs with no allow-listed definition, and the whole object only when its implementor is off the list (`ObjImpl` < 0)]. The 29-43 skipped pairs per search in mono-cost11 are those off-list pairs. The old §2.4 row sent the whole object back to the game's B14 and lost the allow-listed half; that is gone.

**Code points.**
- **B14 hook** (`Autonomy.ScoreInteractionsForMetaObject`, one caller, B13 [verified, §13.1]). The consumer replaces the body for objects in the request with a valid result. It walks the object's `MetaAds` in order and first applies B14's own commodity filter (`Tradeoff.SatisfiesCommodity`, else `MetaAdSatisfiesCommodity` for meta definitions), the same filter the worker driver copies [verified as built, `ScorePair`]. Then for each ad:

| Ad | Action |
|---|---|
| filtered out by the commodity check | nothing (as vanilla) |
| not allow-listed (definition off the list, or a guarded mod type) | **C1** with the same pair object vanilla's B14 would pass |
| allow-listed, the object's worker result valid, pair selected (band 1) | identity check, then **C1** |
| allow-listed, valid, not selected (worker failed, or ranked below band 1) | nothing now; it may come back in a later band (§16.1) |
| allow-listed, but the object aborted or skipped on the worker, or stale | **C1** (live, as vanilla) for every allow-listed ad of this object |

- **C1 per-ad entry.** The hook calls `CalculateScoreAndAddToCandidates` (C1) once per ad that the table sends there. That runs C1a, C2 and `BestScores.Add`, exactly the per-ad call vanilla's B14 makes [verified, docs/59 B14/C1]. No other entry is used, so the check table, `BestScores` and the remembered-best handling stay the game's.

Objects not in the request, a whole-object `AlwaysChooseBest` hit, and `late` objects still run the game's own B14 unchanged (§2.4). Everything an off-list ad does runs on the sim thread live, as in vanilla. No off-list code reaches the worker, so the mod guard (§6) needs no new row; the 18-method L3 list of §13.5 gains nothing, because the consumer calls C1, which is already on it.

**Cost.** Off-list ads go through C1 in go-live and in vanilla alike, so they are cost-neutral. The moved estimate (allow-listed answered scoring, §15) needs no mixed-object correction under this rule. Under the old rule it would have shrunk by an unmeasured amount.

### 16.3 What Mode R and G7 must check

- **Mode R** (before S2, with the pass counter round):
  - The pass share per request: worker code 0 among classified pairs (the other agent's counter; `mono-cost-report.ts` reads it when present).
  - Mixed objects split out: the number of objects with both kinds of ad, and A0-A5 over the allow-listed pairs of those objects. They must stay A1-A3 = 0 like the rest; an allow-listed pair's score may not depend on its off-list neighbours.
  - Over each request, the share of searches where the worker's 11th passing score ties or is within 1% of its 10th. That is the band rule's fire rate with `drift` 0 and an early read of band cost.
- **G7 dry apply** (`Dry=1`, §8.1):
  - (1) Top-10 equality as §8 says. The predicted set is built from band 1 (+ bands the rule fires) scored with C2 under MT save/restore, plus the game's own scores for the off-list ads of mixed objects, taken from the Mode W C1 record hook.
  - (2) Off-list completeness: every off-list ad that vanilla's B14 sent to C1 in that search is in the consumer's C1 set exactly once (`alOffListC1` = the game's off-list C1 count per search; any difference is a G7 failure record with the pair).
  - (3) Bands: `alBandWhy` counts and the `drift` histogram. Every top-10 miss must have a cause: `drift` under-estimated, a §3.3 rise, or a stale object.
  - (4) Cost: band 2+ fire rate per search. Per §16.4 a rate near every search wipes out the gain, which stops go-live by the S4 rule.
- **G9 / S4** as §14.4, judged with the report line below.

### 16.4 Net after consume with `TopN` 10 [inferred]

`mono-cost-report.ts` "go-live after consume" now uses `TopN` 10 and prints two bounds: band 1 only, and every band firing (all passing answered pairs re-scored live). It takes the pass share from the Mode R pass counter when the native JSON or ts3fixes counters have it; otherwise it prints the 0.25-1 range. mono-cost11, ms/s, pass share 0.25 / 0.5 / 0.75 / 1:

| Run | Band 1 only | Every band | Break-even N |
|---|---|---|---|
| BP | +1.3 / +2.5 / +4.1 / +4.9 | +1.3 / -3.0 / -7.2 / -11.4 | 8.5 / 12.5 / 14.7 / 16.2 |
| BP rerun | +1.0 / +2.4 / +4.0 / +4.8 | +1.0 / -3.3 / -7.6 / -12.0 | 8.4 / 12.3 / 14.5 / 15.9 |
| IP | +8.7 / +13.3 / +14.9 / +15.7 | +8.2 / +2.6 / -3.0 / -8.7 | 15.9 / 23.2 / 27.4 / 30.2 |
| IP rerun | +2.3 / +2.4 / +3.7 / +4.4 | +2.3 / -0.9 / -4.2 / -7.4 | 8.7 / 12.8 / 15.1 / 16.6 |

The result depends on how often bands fire. The exact rule (`Margin` 0, `drift`) should fire rarely if one-tick drift is small, but nothing measures that yet; Mode R's tie share and G7's `alBandWhy` measure it. IP's numbers also depend on the overrun fix (before consume: +23.4 at 17% overruns, +11.1 at 44%). The lazy GC lock (~0.2-1 ms/s) is still to subtract. BP stays below the ~10 ms/s goal at any pass share. IP reaches it only with a pass share >= ~0.5, few band fires and overruns near the first run's 17%.

### 16.5 Note after mono-cost13 (2026-10-30)

The first runs with the consume counters change three inputs of this section; docs/122 §14.23 has the numbers.
- **Staleness is small, and the top-pick figure is an artifact** [verified in the code, derived from the counters]. After about one tick, 0.24% (BP) and 0.07% (IP) of the pairs both sides answered differ. The 20.9-27.5% top-1 and 82-98% top-10 "changes" come from `FreshCheck` ranking each side's own passing pairs: the worker's A4 objects are missing on its side, and the top-10 compare counts padding slots. Staleness can explain at most 7.7% (BP) and 3.3% (IP) of searches. With `drift` that small, band 2+ (§16.1) should rarely fire; G7's `alBandWhy` still confirms it.
- **Pass share:** 11.9% BP and 5.6% IP of answered pairs. Worker aborts fall on objects with passing pairs more often than average.
- **"Scoring moved" is an upper bound by 1.4-1.7x** [measured shares, inferred scale]. R includes the driver's per-object `HowMuchItWantsSimToCome` (~47% of R), which vanilla never runs (§13.2). Vanilla's whole meta B14, off-list ads included, is ~0.6-0.7 x R per search. Pair cost: `Test` ~60-70%, venue score ~25-34%; no route cost. With that, design A (this section) nets about -5 to +3 ms/s on BP and -1 to +9 on IP, not +4.6 / +16.1. The report formula also discounts aborts twice (small net effect).
- **Before S2:**
  1. Add the docs/122 §14.23 counters (answered-only top compare, reference-vs-fresh top, reference passes among A4 pairs, per-pair C2 timer by allow-list and pass/fail).
  2. Build docs/97 AM-1/R3 (exact memos of the pure venue parts: cheaper vanilla and cheaper live C1).
  3. Get the BP cut to <= ~0.4 ms.
  4. Reconsider the input-checked score reuse (docs/122 §14.23 design C), which keeps scores exact and gains roughly what the dropped Test-only option would.
  
  BP above ~10 ms/s needs step 3 plus design C (or B).

## 17. Go-live consume, built (2026-10-31, ts3native 0.36.142, ts3fixes contract 10, not run)

Built offline from §2, §3, §16 and the docs/206 Satb act mode (`Satb=2`; satb6 BP: 33,506 of 34,329 compared pairs bit-equal across its runs, A1-A3 0, no crash; worker cost to the sim 3.7-3.8 ms/s; report "go-live after consume" ~15 ms/s net [measured satb6, inferred net]). Nothing here has run in the game. Ledger docs/51 §408 (I-408-1 to I-408-5; I-124-16 = I-408-3).

### 17.1 Switches

| Where | Key | Default | Meaning |
|---|---|---|---|
| ts3native `[AutShadow]` | `Mode=3` | 0 | go-live: the native flow of Mode R (cut, worker, finish), with the sim-thread reference only at every `VerifyEvery`-th cut |
| | `TopN` | 10 | band 1 and each later band (1-64) |
| | `Bands` | 3 | bands including band 1 before every remaining worker-passing pair goes through C1 (1-8) |
| | `Margin` | 0 | per mille added to the factor of rule (b) (§16.1); 0 = the exact rule |
| | `MaxWait` | 3 | ticks a search waits for its result before the game's B13 runs (1-16) |
| | `VerifyEvery` | 50 | every Nth cut also runs the reference; an A2/A3 there turns go-live off for the session (0 = never) |
| | `Dry` | 0 | 1 = G7: compute and compare, use nothing |
| ts3fixes | switches `autshadow` + `autlive` | off | `autlive` off in Mode 3 = the game's search and no request (the off arm of `--ab autlive`) |
| ts3fixes | tuning `kAutLive` | False | patches B14 at startup; only in test packages (`node tools/setup/build-fixes.ts --on kAutLive`): the pass-through costs one interpreter frame on every meta B14 (I-408-5) |

### 17.2 Flow as built (`mods/ts3fixes/src/AutLive.cs`, `AutLiveCore.cs`; native `autshadow-run.cpp`, `autshadow.cpp`)

1. **B13 hook** (`MetaHook` -> `LiveMeta`): eligible = manager search (`CurrentSearchType` Autonomy), not the inspection window, `Simulator.CheckYieldingContext(false)`, B14 hook in, consumer-path guard clean, go-live not switched off. `TryRequest(a, c, live: true)` builds the Mode R request (same object list and order, §13.7), takes a `LiveSearch` from a pool of 8, copies the request into it, stores the actor fields of §3.1 (age and species bits, `LotCurrent`, `LotHome`, `Household`, `IsSelectable`, `AssignedRole`, `Occupation` with `JobCount` and `CurrentJob`, `CareerLocation`, the chip trait) and watches every meta object (object id) and its lot (LotId) for `ObjStale`. Shared cuts (up to 4 requests) work as in Mode R.
2. **Wait**: `Simulator.Sleep(0)` until `AutFinish` marked the search ready, at most `MaxWait` ticks. After every yield the two checks of the game's `YieldIfPossible` run with its returns (interrupted: clear the flag, `SearchInterrupted`; actor destroyed: `ActorDestroyed`). With Satb=2 the job runs during t+1 and the finish comes at the end of t+1, so the search resumes in t+2 (I-408-4): the view is ~1-1.5 ticks old, not 1 as §2.2 assumed. A task destroyed while parked leaves its slot; the pool reclaims it 64 frames after its finish (or 600 after its post), and a world load clears all.
3. **Result checks** (§3.1, each a counted fallback to the game's B13): run status (stopped without a kept part, epoch inside the run, no worker or refused, exception), world epoch moved since the cut (`Config(20)` vs the cut's `Config(21)`), `GameStates.IsLiveState` and not shutting down, `LotCurrent`, a head interaction, the actor fields. Not built: the tuning generation (no Retuner-style hook; G7 sees its effect) and the LOD flag compare.
4. **Selection**: an object is consumed only if its implementor is allow-listed, the worker neither aborted nor skipped any of its allow-listed pairs (KeepPartial `stop` included), none of its ads has `AlwaysChooseBest`, and `ObjStale` says neither it nor its lot had an event after the cut frame (§17.3). Candidates = worker-passing pairs of those objects, ordered by worker score, ties in request order; band 1 = the first `TopN`.
5. **B14 hook** (`AutB14Hook` -> `LiveB14`) inside the game's B13: not in the request, a late call (the frame moved since the resume: B13 yielded at Y2) or an invalid object: the game's B14. Else the ads must be the request's (same references and count, else the game's B14), then per ad in `MetaAds` order the game's own commodity filter and: off-list ad (definition off the list, mod guard, scoring function): C1; worker-filtered but passing now: C1; band 1: C1; anything else waits for the bands. C1 = the game's public `CalculateScoreAndAddToCandidates` (check table, C2, `BestScores.Add`).
6. **Bands** after B13 returns `Continue`, before D2: `AutLiveCore.Bands.Step` on the game's own `BestScores` (`Count`, `Element(i).InteractionScore`); drift from the live scores the C2 hook records for sent pairs; rule (a) count, (b) `w x (1 + drift + Margin) >= s10`; only pairs of objects the B14 hook consumed; a destroyed object's pair is skipped.
7. **Dry (G7)**: steps 1-4 as live; the B14 hook marks consumed objects but runs the game's B14; the C2 hook records every C1 call of the search. After B13: replay the calls into a model of the game's queue from the queue before B13 (must equal the game's queue: `drySimMis`), and again with only what consume would send plus the bands with the game's own live scores; compare the top-10 set and the top 1 (up to ties at the top score) with the game's queue; the model is the game's heap in slot order (min ties evict the root); `AlwaysChooseBest` pairs left out of both replays (BestScores keeps them on a side list); a replay mismatch logs a `dry-sim-mis` record (queue before, replay, game: `[id, score]` in slot order); misses by cause (worker failed `Test` = stale reject, ranked below the bands, other); `dry-diff` records in `autshadow.jsonl` (50 per load).
8. **Verify** (`VerifyEvery`): the native cut runs the reference on that cut only; `AutFinish` runs Mode R's `CompareR` (A0-A5 counters as before) and compares the worker's and the reference's top 1 and top-10 set over pairs both answered; an A2/A3 turns go-live off for the session.
9. **Consumer-path guard** (§13.5): L3 by exact signature over `ScoreInteractionsOnObjects`, B13, `YieldIfPossible`, B14, C1, C2, `AssignProbabilitiesAndChooseInteraction()`, `InteractionObjectPair.get_Tuning`, `BestScores.Add` / `AddInteractionOnlyIfBest` / `ChooseInteraction` / `ChooseInteractions`; our own swaps (B13, B14, C2) pass as own swaps. A hit = go-live off for the load. `BestScores..ctor` is not checked (the resolver takes methods only).

Not built from the design: the tuning-generation and LOD rules (step 3), the pick histogram per definition (§7.2; G8 territory). The `aut-live-pet` / `-interrupt` / `-venue-hour` repros (§8.2) were added 2026-10-31 with `golive-final.json` (§17.6).

### 17.3 Native (0.36.142)

- `Mode=3` in `AsInstallRun` (keys above, clamped; one log line `autshadow go-live: Mode=3 ...`), the cut frame (`g_asCutFrame`), the reference every `VerifyEvery`-th Mode 3 cut (`alCuts`, `alVerify`).
- `AutShadowNative` contract 10: `LiveStat(slot, add)` into `al[56]`, `LiveWatch(id, isLot)`, `ObjStale(id, isLot, cutFrame)`; `Config` 13-18 settings, 19 frames ended, 20 epoch now, 21 epoch at the cut, 22 cut frame, 23 the last cut ran the reference.
- `ObjStale` watch map (`autshadow.cpp`, 8,192 slots, reset at 6,144: a query for a cut before the reset answers stale): each object event on a watched object id stamps it with the frame in progress; an add (4) or destroy (5/6) also stamps the lot its world component names (key LotId with bit 63). Unwatched = stale. Cost: one locked hash probe per object event while anything is watched.
- Counters `ts3native_autshadow` v20 (+0xB8C): `alOn` (bit 0 Mode 3, bit 1 Dry), `alCuts`, `alVerify`, `alWatchN`, `alWatchResets`, `alStaleQ`, `alStaleHit`, `alStaleEv`, `al[56]` (+0xBAC; slots in §17.4). `tools/probe/agent.js` reads them; `mono-cost-report.ts` prints the "go-live", "go-live objects" and "G7 dry apply" lines.

### 17.4 Counter slots (`al[]`, ts3fixes `AutLive.cs` kS*)

0 eligible, 1 posted, 2 consumed, 3 dry compared, 4 not posted (sampling, busy, pool, guard), 5 not ready in `MaxWait`, 6 run stopped, 7 epoch in the run, 8 no worker or refused, 9 worker exception, 10 epoch since the cut, 11 lot, 12 actor fields, 13 game state, 14 interrupted after our yield, 15 destroyed after our yield, 16-22 B14 calls: not in request, invalid (abort, skip, off-list implementor), stale, ads changed, `AlwaysChooseBest`, late, consumed; 23 band-1 pairs through C1, 24 band 2+ pairs, 25 off-list ads, 26 band 2 fired, 27 band 3, 28 full, 29 rule (a), 30 rule (b), 31-35 drift per search (0, < 1 %, < 5 %, < 20 %, more), 36 dry top-10 equal, 37 dry top 1 equal, 38 dry replay mismatch, 39 consumer time (us), 40 wait ticks, 41 off by verify, 42 candidates, 43 head interaction, 44 dry miss (worker failed `Test`), 45 dry miss (below the bands), 46 off by guard (reserved), 47-49 verify runs / top 1 same / top-10 same, 50 worker-filtered ads through C1, 51 other, 52 dry off-list C1 calls, 53 dry miss other, 54 C2 calls of a search found by its autonomy after `sLiveCur` moved to another search during a B13 yield (were lost before), 55 dry top 1 equal only up to a tie at the top score (counted in 37 too).

### 17.5 Tests

- `tools/selftest/autlive` (C#, `AutLiveCore`): queue semantics (positives only, strict replace), rules (a)/(b) with ties, drift and bins, order with ties, band steps and the full band, and 3,000 random searches: exact and one-factor live scores give the game's queue in every run (I-408-1); per-pair +-20 % noise differs in ~2.5 % (measured, not asserted).
- ts3native selftest `TestAutShadowLive`: the reference on every 4th Mode 3 cut only, `Config` 13-23, `LiveStat` bounds, the watch map (stamp after the cut, lot stamp on add/destroy only, unwatched = stale, reset after 6,144 ids, Mode 3 only); T5 updated to contract 10 and 31 ICs.
- monohost `AS-live-verify`: 8 Mode 3 requests through the real worker, 2 references (VerifyEvery 4), every result finished; `AS-install` wants 31 ICs and contract 10.
- `node tools/harness/mono-cost-report.ts --self-check` (the go-live lines); `tools/selftest/autshadow` unchanged.

### 17.6 Queues (not run; asi `game-copy/ts3native.asi.golive1`, package `game-copy/ts3fixes.package.golive2` = `build-fixes.ts --on kAutLive`)

| Queue | Jobs | Gate |
|---|---|---|
| `golive-g7.json` | smoke 4 min; BP and IP 10 min with Satb=2 and with Satb off (`WindowWaitUs` 5000); BP crowd in view (`aut-live-crowd-view`, 15 min); second clean BP/IP Satb=2 runs | G7 (§8): top-10 >= 99 %, top 1 >= 99.5 % over >= 2,000 searches per world, `drySimMis` 0, every miss with a cause |
| `golive-act.json` (after G7) | smoke; BP, IP 10 min; BP crowd 20 min; BP with two saves (`aut-live-save`); second clean runs | 0 crashes, no script error, consumed >= 80 % of posted, verify A1-A3 0, Sims keep acting |
| `golive-ab.json` (after act) | measurement smoke (1-min slices); BP, IP 10 min `--ab autlive` 2-min slices both orders; BP crowd in view 25 min, 5-min slices, both orders | S4 (§14.4): sim ms/s off - on outside the pooled noise band, autonomy-search owner down, it/s not down > 2 % |
| `golive-final.json` (the single go-live run, 2026-10-31, asi/package placeholders `golive-final`) | smoke (Dry, read-back of the go-live and Satb log lines); G7 BP, IP, SV, crowd, `bp-autlive-edit`, `aut-live-venue-hour`, second runs; act smoke, BP, IP, crowd, `aut-live-save`, `aut-live-pet`, `aut-live-interrupt`, second runs; guard crowd both orders; G8 seeded off/on pairs BP + IP 30 min; G9b `aut-live-save-reload` SV, BP, IP x 2 (30 jobs, ~445 profile min, ~9.5 h) | G7, act, §17.10 guard, G8, G9b in that order; stops by hand at a failed gate (queue.ts stops only on crash rules); `golive-ab.json` stays a separate clean-night timing queue |

G8 tooling gap [verified 2026-10-31]: no counter for autonomous pushes per Sim-hour, `MakeSimGoHome` fallbacks or the per-definition pick histogram (§7.2), and `rng-compare.ts` groups seeded vs unseeded only; golive-final's G8 jobs judge picks from the 30 s `sims_activity` samples (interaction changes, a proxy) and the mean wait ticks (al slot 40), and report the go-home rule as not measured. New agent commands for the §8.2 repros (not built yet): `aut_interrupt` (Generic `FindBestActionForCommodity` for every Sim whose manager search is parked: sets `mAutonomySearchWasInterrupted`), `venue_hours` (read-only, `Lot.GetHoursOfOperation` boundaries and crossings).

### 17.7 Risks

1. Staleness of ~1-1.5 ticks with Satb=2 (I-408-4): a pair the worker failed that now passes is missed for one search; G7's `dryMissStale` sizes it. `MaxWait` 3 and the epoch rule bound it.
2. The extra interpreter frames (I-408-5) on deep `Test` chains: an F1 overflow or a stack overflow would show as a worker give-up 0xD8A8E0 or a script error in G7/act.
3. `ObjStale` misses lot changes without add/destroy (I-408-3), and there is no tuning-generation rule: both only change which pairs are selected, never a pushed score (every pair in `BestScores` is scored live).
4. A parked search holds its `LiveSearch` slot until its task resumes; 8 slots; reclaimed 64 frames after the finish.
5. The net gain is still the §16.4 / docs/122 §14.23 estimate (BP -5 to +3, IP -1 to +9 ms/s with design A; satb6's report line ~15 ms/s): the A/B decides.

### 17.8 golive-g7 round 1: nothing posted (2026-10-31, ts3fixes fix, package golive2)

golive-g7 on golive1 (bp/ip Satb=2, bp Satb off) failed with "[AutShadow] no data: cuts 0": `metaCalls` 0, every `al` slot 0, native Mode 3 and Dry up [measured, `probe-out/queue/golive-g7-2026-10-30-*`]. The ts3fixes applied log named both causes [verified there]:
- `go-live ... B14 hook missing: off`: the B14 patch was never attempted. `Fixes.Apply` runs in `Instantiator`'s static constructor, which the tuning loader triggers before it writes the `[Tunable]` fields, so `kAutLive` read there is always the C# default (False) even in the `--on kAutLive` package [inferred from the log: no B14 patch line at all with the package's tuning hash equal to the expected one]. Fix: the B14 patch moves to the first world load (`ApplyAutLiveB14` from `AutShadowWorldLoad`, once per session; a refusal is logged). The same ordering hits every tuning-gated patch in `Apply` (`kVenueMemo`'s venue score hook, `kWalkStyleComparer`): not changed here.
- `mod guard: ...AutMetaHook.ScoreInteractionsForMetaAutonomy replaced` -> `consumer path swapped: off`: `OwnSwap` looked our swaps up by the `MethodInfo` object `Fixes.Patch` used, and `GetMethods` hands out other objects, so our own B13 swap read as a foreign mod. Fix: `sPatched` is keyed by `MethodHandle.Value` (the `MonoMethod*`). This also makes `AllL3Ok` accept our venuememo swap as docs/122 §14.24.7 intended.

Native unchanged from 0.36.142; the queues now pin asi `game-copy/ts3native.asi.satb7` (0.36.143 = 0.36.142 + the docs/206 Satb native writers and cold warm-up, same contract 10) with package `game-copy/ts3fixes.package.golive2` (built from the same master).

### 17.9 Every tuning-gated patch moved out of `Fixes.Apply` (2026-10-31, ts3fixes, offline)

§17.8's ordering bug is general: anything in the `Fixes.Apply` call tree that reads a `[Tunable]` sees the C# default. Static scan of that tree (every method reachable from `Apply`, `Instantiator.k*` and `ShortcutOn` reads) [verified in source]:

| Tuning | C# default / shipped tuning | Patch gated in `Apply` | Turned on by tuning | Effect before the fix |
|---|---|---|---|---|
| `kAutLive` | False / False | B14 `Autonomy.ScoreInteractionsForMetaObject` | `--on kAutLive` = `game-copy/ts3fixes.package.golive2` (golive-g7/act/ab) | never patched; fixed in §17.8 (golive1 runs void) |
| `kVenueMemo` | False / False | venue score hook `Lot.HowMuchItWantsSimToCome` | `--on kVenueMemo` = `game-copy/ts3fixes.package.venuememo` (queues `venue-memo-ab`, `venue-memo-verify`; repro `venue-memo`) | never patched: the venuememo arm ran only the `GetTuning` memo |
| `kWakeHooks` / `kWakePark` / `kWakePoll` | False / False | 7 WakePark hooks (docs/211) | `--on kWakeHooks` = `game-copy/ts3fixes.package.wakehooks` (queues `wake-probe`, `wake-park-ab`; repros `wake-park-loop`, `wake-park-travel`) | never patched: wakeprobe/wakepark/wakepoll counters would stay 0 |
| `kWalkStyleComparer` | True / True | walk-style comparer hook | (on by default) | always patched: a player's `False` was ignored |

Past results: only golive-g7 round 1 (2026-10-30, 4 jobs, already void in §17.8). `probe-out/queue` holds no run of `venue-memo-*`, `wake-probe` or `wake-park-ab` and no `venuememo`/`wake*` applied lines [verified 2026-10-31], so no other measured result was invalid. Every other tuning in the tree is read per call by its hook (unaffected).

Fix: `Fixes.ApplyTuned` (once) installs the venue score hook, the WakePark hooks and B14, called from `World.OnStartupAppEventHandler` when `kInstantiator` is already True (every ts3fixes tuning sets it, so the fields are written) and else at the first world load (first in the world-load handler, before `AutShadowWorldLoad`'s guard sweep). WakePark is all or none (`NotPatchable` checks the seven targets first: wakepoll's parked wait needs the `AddExitReason` hook). The walk-style hook stays in `Apply` (routing may compile `Compare` early) and reads `kWalkStyleComparer` per call, falling back to the game's method via a `CompareOrig` stub. Each gated patch logs `tuned: <tuning> on: <what> patched|MISSING` in `ts3fixes.applied`; `queue.ts` `tunedFails` fails any profile/soak job with a `MISSING` line. Not verified in game: whether app startup fires after our tuning is parsed (ts3agent notes it can fire first; then the world-load path runs) and that none of the WakePark targets is compiled before the first world load ends [inferred]; the first `wake-probe` run shows it (a `MISSING` line fails the job).

### 17.10 Crowd cost guard (2026-10-31, ts3fixes + ts3native Config keys, built offline, not run)

Why: golive-g7's BP crowd job (`node tools/harness/mono-cost-report.ts --tag golive-g7`, section `bp-golive-g7-crowd`) [measured]: 13.7 searches/s, cut 1.1 ms each = 14.7 ms/s = 36% of the scoring moved (G-C gate <= 25%), worker adds 14.8 ms/s to the sim (9.9 measured cut + GC lock waits, 4.9 inferred lock entries and guard calls), net after consume ~4.3 ms/s [inferred]; normal BP 0.7 ms cut, 23%. A bigger crowd can turn that net negative. Required before the v0.2.5 opt-in: go-live must step back by itself where it costs the sim more than it saves.

Rule (`AutLiveCore.CostGuard`, pure, selftest below): a window of at least `kAutLiveGuardMinSearches` (40) eligible searches and `kAutLiveGuardMinFrames` (300) frames sets
- **cost** [measured, except one term]: cut + snapshot us (native `cutUs`), the sim's GC lock waits (`[MonoThread]` `simLockWaitUs`), the sim's locked GC entries x 68 ns ([inferred] docs/30 §15, as the report), our consumer time (`kSApplyUs`: check, select, bands) and the band-1 / off-list C1 calls inside the B14 hook (new stopwatch, the live re-score consume pays);
- against **saved** = reference us per request (native `refUs / refRuns`, the `VerifyEvery` reference cuts; the per-request figure the report's go-live estimate uses) x the sum over used searches of the share of their allow-listed pairs whose object the B14 hook consumed [inferred mapping: the reference scores every allow-listed pair of a request].

Cost above `kAutLiveGuardOffPm` (1000 = break-even) per mille of saved: no posting for `kAutLiveGuardHold` (600) frames, so every eligible search runs the game's own B13/B14 (the existing fallback; no new behaviour). Hold doubles on each skip in a row, up to 16 x. The window after a skip must come in at or under `kAutLiveGuardOnPm` (800) or it skips again (hysteresis); a pass resets the hold. Nothing consumed in a window but a reference known = ratio 10^6 (skip). No reference yet (`refRuns` < 3): no decision, go-live stays on. Guard thunk calls are left out on purpose: they are paid with or without a post. Crowd size or world enter only through the measured window; there are no Sim counts.

Where it hooks: `LiveMeta` right after `kSEligible` (`GuardAllow`): skip = `h.MetaOrig(c)` with no request; each finished acting search adds its consumer us and consumed share (`sGuard.Add`); the window closes in `GuardAllow` from the native cumulative words (`AutShadowNative.Config` 25-30; key 24 = 1 marks a ts3native that has them). Dry (G7) and a ts3native without the keys: the guard never runs (applied log `cost guard n/a (dry)` / `off (ts3native without cost keys)`).

Switches and tunables: ts3fixes switch `autliveguard` (default = tuning `kAutLiveGuard` True; acts only with `autlive` acting). Off = still measured and reported (ratio in the counters), never skips, and an active skip ends at once: the A/B off arm. Tunables `kAutLiveGuardOffPm`, `OnPm`, `MinSearches`, `MinFrames`, `Hold` in the Instantiator tuning (build-fixes.ts XML), logged with the go-live line at load.

Counters: ts3fixes 538 windows closed (`autliveguard_windows`), 539 skip windows (`_skipwindows`), 540 eligible searches skipped (`_skipped`), 541 last window's ratio per mille (`_ratio_pm`, a gauge, -1 = no reference). Records: `live-guard` in `autshadow.jsonl` (ratio, hold frames, frame; 20 per load). Report: `mono-cost-report.ts` "go-live cost guard" line (acting only).

Tests: `tools/selftest/autlive` (`GuardTests`, 16 checks: window minimums, no reference, cheap stays on, the crowd G7 numbers at ~894 per mille stay on, expensive skips for the hold, hysteresis 900 per mille after a skip skips again with the hold doubled, 700 resets, hold cap, nothing saved, switch off, wrapped native counters); `mono-cost-report.ts --self-check` (guard line); native `TestAutShadowLive` asserts Config 24-30. C# compile-checked against the game assemblies; ts3native not built (no build window).

What it means for the measured crowd [inferred]: at OffPm 1000 the G7 crowd (~36.5 ms/s cost vs ~40.8 saved, ~890 per mille) stays on: the report's net there is still positive. The guard catches crowds where the net turns negative; a stricter OffPm (e.g. 800) would step back already in that crowd. Limits: `VerifyEvery` 0 leaves the guard without its saved side (golive-ab's timing ini has 0; `golive-guard.json` uses 50); one cut serves several requests, so a skip saves cut time only in frames where no other search posts (the next window measures that); the reference per request is a since-load average.

Needs: next ts3native build (Config 24-30, registry note) + a `--on kAutLive` ts3fixes package; then queue `tools/harness/queues/golive-guard.json` (not run: smoke, BP crowd guard on vs off both orders 25 min, BP natural 10 min), after golive-act.

### 17.11 Per-search feature mask (2026-10-31, ts3native 0.36.148 + ts3fixes, branch `feature/aut-rain-h11`, built offline, not run)

Why (Rodney): a wrong pick or a crash must name the feature that touched the search, without bisect runs. Each go-live search gets a 9-bit mask (`AutLiveCore.Feat`): `late` (docs/217 L1 late consume used), `prelearn` (PreLearn put rows at this search's cut), `tierreqlots` (TierReqLots refreshed a Sim at the cut), `rain` (a pair answered from the H11 shelter bit, docs/127 §4.1), `desirefn` (the Mode 3 driver-meta skip, docs/216 #2: every Mode 3 search), `satb` (the job's worker loads went through the Satb redirect), `guard` (posted in the cost guard's probation window, §17.10), `cold` (a cold-path give-up on the worker in the job), `partial` (KeepPartial overrun remainder). Native bits (`Config(33)`: cut deltas of the PreLearn / TierReqLots counts, job deltas of `satbWLoads` and the worker cold give-ups) are read at the finish; the managed ones at the finish and at a late consume. The cut-level bits say the feature acted at the search's cut, not that the worker read that row [design: no per-row tracking].

Where it shows: `feat` (hex) + `feats` (names) on every `dry-diff`, `dry-sim-mis`, `dry-nocmp`, `live-drop` and the new capped `live-use` record (acting searches, first 50 per load: consumed share, top worker pick); ts3fixes counters 575-583 searches per bit, 584-592 mismatches (dry unequal or replay mismatch, once per search), 593-601 give-ups (live-drop fallbacks, dry-nocmp), agent names `feat_<bit>_searches/_mis/_drop`; `mono-cost-report.ts` line "go-live feature mask" (searches / mismatches / rate / give-ups per bit). Crash path: every search end calls `SearchMark(req, mask, top definition)`; `XcptLog` ([Crash] Log=1) adds per access violation / illegal instruction a line `autshadow search: run, posted req, worker unit obj/pair (worker or sim thread), this cut's native bits; last finished search req, mask [names], top definition`. Tests: `tools/selftest/autlive` (Build, Text, Json, Count), native `TestSatbRainSkip` (names, mark copy, Config 33), `mono-cost-report.ts --self-check`. Needs a ts3agent rebuild for the counter names.

Builds (2026-10-31, rebased on master 383278fa): `game-copy/ts3native.asi.golive-final` and `ts3fixes.package.golive-final` replaced by this build (= `asi.rainh11` / `package.rainh11`; the round-7 copies kept as `*.golive-final-r7bak`); `golive-final.json` and `rain-h11.json` now run PreLearn=0 (thrashes the row table, mono-r7) and TierReqLots=1.
