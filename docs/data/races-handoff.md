# Open race cases: build handoff (2026-11-08)

Plan only, no code. Branch of this note: `docs/races-plan`. Each build agent reads this file first, builds its piece, and appends a line under "Status log" at the end (piece, branch, commit, what passed, what is open).

Sources [verified: read for this note]: `AGENTS.md`, `docs/131-mono-glue-native-closure.md` (§5, §9.2-9.6), `docs/data/mono-glue-races.csv` (348 rows, 85 with no mitigation), `docs/data/autonomy-tier2.csv` (316 rows), `docs/data/tier2-blockers.csv` (194 rows), `docs/219` §2, `docs/126` §3 and §4.1, `docs/122` §3.5, `docs/data/tier2-rows35-handoff.md`. Tags as AGENTS.md: [verified] / [inferred]. Effort = wall-clock for Claude with agents (CLAUDE.md units); game runs listed apart.

## 0. What the 85 cases are, and what they unblock

The csv rows were counted by `tools/mono-glue-closure.py` as if the worker ran every IC body (68 bodies in the closure). In the built tier-2 design it does not: a row is a descriptor, the cut calls the game's body on the sim thread and the worker answers from the copy (`tier2-rows-handoff.md`). So **today none of the 85 blocks a tier-2 definition** [inferred from the design; docs/131 §6: "IC bodies must stay snapshot/memo, never pass-through"]. The 85 gate two things:

1. **The M5 safety proof** (race set closed, not "inferred safe"): needed before the worker acts on any definition.
2. **IC pass-through**: the worker running a read-only IC body itself instead of a snapshot row. It saves the row build per IC and the per-cut fill. Weight below = definitions whose ICs are still rows or unbuilt (docs/219: 123 small + 23 medium `snapshot-rows` definitions = 7.4% of scoring samples; `tier2-blockers.csv` sum) [inferred upper bound; shares overlap and do not add]. Writing and yielding ICs (`writing-ic` hard, 0.2%) and the a-cut write-on-read bodies stay on the sim thread whatever happens here.

Counts re-derived from the csv by script: 85 = 41 + 37 + 6 + 1 (groups below). Kinds: 44 `worker writes`, 41 `worker reads, sim writes`. Reach: 74 only from IC bodies (a), 11 from b/c.

## 1. Groups by fix pattern

| Grp | Pattern | Rows | Cases | Status |
|---|---|---|---|---|
| **R1** | pre-warm at load: first-fill caches, written once | 41 | **A** host `+0x50` converter static class caches `0x11EE54C`-`0x11EE588` (16, guard bits `0x11EE588`); **B** function-local static empty strings `0x11D7508`-`0x11D7518`, `0x11DA048`-`0x11DA058` (10), CRT atexit table `0x123437C`/`0x1234380` (2), shared empty-string byte `0x11C7E54` (1, same-value write); **C** default-allocator block `0x11DC168`-`0x11DC184` (7), `0x11C7984`, `0x11E96C0`/`1`, `0x11ECEEC`/`ED` (option byte pair) (12) | writers and first-fill shape [verified bytes, docs/131 §5, §9.2-9.3]; "the sim thread fills them long before a worker exists" [inferred, I-131-6] |
| **R2** | read-only after load: start-up, registration and world-load singletons; worker only reads | 37 | `0x11ECBC4` world manager, `0x11E9158`/`0x11E9114`/`0x11E8FA8` (registration), `0x11D4BC8`, `0x11CB864`, `0x11ED034`, `0x11D4BBC` host world, `0x11C78D8` resource manager, `0x11CE2D0`, `0x11DC350`, `0x11D9128` routing world, `0x11CF490` physics world, `0x11D6A78` event table and 23 more | 6 named rows [verified: writer names]; the other 31 [inferred per row] (one or two writers each, "no direct caller" or CRT range) |
| **R3** | Boehm and script-host init words, covered by P3 and docs/120 rules | 6 | `0x11EE528`, `0x11EE514`, `0x11EE520`, `0x11DC18C`, `0x114C908`, `0x11CC964` | covered by M5 P3 as designed [inferred, I-131-4] |
| **R4** | the one real shared write: temp string refcount/handle | 1 | `0x11CB920` (`0x4F3CC0`, `0x4F4300`, `0x4F4400`, 20 callers), reached from `DeviceConfig_GetOption`, `GetFloorSurfaceType`, b root `0xD6FCB0` (image open) | writers [verified]; "worker never reaches it" [inferred]; **docs conflict**: docs/131 calls it a refcount, docs/126 §4.1 a lazily fetched handle set once. **R4a done 2026-11-08: lazy `GetACP()` cache, harmless, closed (docs/51 §461, verified)** |
| R5 (outside the 85) | keep on the sim thread, fill at the cut | 0 | a-cut ICs #2, #4, #18 (`+0x918` footprint update when the level bit is clear; #18 room iterator written every call); hop-6 `0xB469C0` static scratch vector (class b only) | [verified bytes, docs/131 §9.4-9.5]; already the E2 enabler in `tier2-rows35-handoff.md`, no new piece here |

## 2. Per group: unblocks, risk, pieces

Household reach: no group touches Sim, relationship, household or object state; every case is a process-global (pointer, cache, string, option byte). The household/selected-Sim exemption (AGENTS.md) has nothing to exempt; each piece still states that in its commit [inferred]. Writes: R1 first fill only, R2 none, R3 init only, R4 one refcount word.

### R1 pre-warm (41 rows)

- Unblocks (pass-through only): ICs that return strings or arrays (host `+0x50`: #4 `GetFloorSurfaceType`, #7 `GetTilesWithFloorFeatures`, text ICs), allocate (#14, #15) or read the option byte (#6, #7). docs/219 §2: those rows are each <= 0.25%; union ~1% [inferred]. For the proof: closes 41 of 85 at once.
- Risk: low. One sim-thread call per kind at world event 7 (docs/122 §3.5 list), no gameplay effect. The trap: a value kind the pre-warm misses fills later on the worker. Mitigation: a witness that reads the guard bits after the pre-warm, and a worker abort `prewarm` if a guard is clear.
- Pieces:
  - **R1a** static map (Opus, 60-90 min): per row the guard or done byte, the first-fill call, the sim-thread call that sets it; the `0xD7BD40` value-kind switch list. Parallel-safe (read-only, doc output).
  - **R1b** build (Opus for the host converter kinds, Sonnet for the rest, 60-90 min): pre-warm routine at event 7 next to the docs/122 §3.5 ones, guard-bit witness counter (`tools/registry.ts reserve native`), selftest, repro action plus `repros.json` entry. Sequential after R1a; touches the pre-warm region of `native/ts3native` and `AutShadow.cs`, so not parallel with the tier-2 P1/P2 pieces.
  - Game run: one Mode R correctness run, witness all green.

### R2 load-time singletons (37 rows)

- Unblocks: every pass-through IC that reads WorldManager, resource, object, event or routing singletons: effectively all 146 `snapshot-rows` definitions (7.4%) [inferred upper bound]. Biggest weight, cheapest to prove.
- Risk: low if "worker never spans a world load or travel" holds (docs/122 cut rule); a world-scope pointer replaced under a running worker is the only hazard (`0x11ECBC4`, `0x11D9128`, `0x11CF490`). Never a write.
- Pieces (parallel-safe, read-only or witness-only):
  - **R2a** writer classification (Sonnet, 30-60 min): run `--races` writer lists for the 31 inferred rows, tag each writer start-up / registration / world-load / shutdown / unknown by call chain; unknown goes to R2c.
  - **R2b** runtime witness (Sonnet, 45-60 min): Mode R item reads the 37 words (sim thread) at the first cut and at later cuts, logs any change; one counter, one selftest. Closes "inferred" for rows with no static proof.
  - **R2c** world-load guard check (Opus, 30-60 min): confirm in the built code that a world load, travel or shutdown aborts or drains the worker before the singletons change (the cut rule); if missing, add an epoch check at the cut.
  - Game run: one load, one travel, one return, witness diff empty.

### R3 Boehm and host init words (6 rows)

- Unblocks nothing directly; closes 6 of 85 for the proof. Risk low.
- Piece **R3a** (Sonnet, 20-30 min, folds into R2b): check each word is only touched under the P3 lock or at init (docs/120 P3 test list), add the 6 to the R2b witness.

### R4 `0x11CB920` (1 row)

- Unblocks: `DeviceConfig_GetOption` (memo today), #4 `GetFloorSurfaceType` (0.2%-class row) and the b-root image-open rule; directly ~0.2% [inferred]. The single case that would race for real, so the proof's critical row.
- Risk: medium-high. A refcount touched from two threads corrupts string lifetime; a handle fill is harmless. Which one it is decides everything.
- Piece **R4a** (Opus, 60-90 min): disassemble `0x4F3CC0`, `0x4F4300`, `0x4F4400`; settle refcount vs handle (resolve the docs conflict in the ledger); choose: pre-warm (handle), keep both ICs on the sim thread, or an interlocked detour (refcount); add the worker rule "no image open" as an abort reason (`0xD6FCB0`). Sequential before any pass-through of those two ICs; not parallel with R1b if the fix is a pre-warm.
- **R4a status (2026-11-08, done, static):** all 10 references (5 reads, 5 writes) are in the three converters; each write is `if ([0x11CB920]==0) [0x11CB920]=GetACP()` (IAT `0xF95284`) for encoding `2`, no inc/dec, no clear [verified]. Reachable from any thread and possibly first written after load [inferred], but every writer stores the same process-constant value with one aligned dword store, so the race is benign [verified code]. Fix: **harmless**, no code; R4 closed, the two ICs need no sim-thread pin and no pre-warm for this global; the "no image open" abort reason is not needed for `0x11CB920`. Risk: none. docs/131 §5 corrected; docs/126 §4.1 was right. Ledger docs/51 §461.

## 3. Ranking: weight unblocked per effort

Weight = upper-bound scoring share gated, shares overlap [inferred]. Effort = wall time of the group's pieces with parallel ones overlapped.

| Rank | Group | Rows closed | Weight | Effort | Weight / h | Note |
|---|---|---|---|---|---|---|
| 1 | R2 singletons (R2a, R2b, R2c) | 37 | <= 7.4% (all pass-through ICs) | ~1-1.5 h wall (parallel) | ~5-7% | cheapest, mostly proof work |
| 2 | R1 pre-warm (R1a, R1b) | 41 | ~1% | ~2-3 h (sequential) | ~0.4% | closes most rows |
| 3 | R4 `0x11CB920` (R4a) | 1 | ~0.2% direct | ~1-1.5 h | ~0.2% | gate for the whole proof |
| 4 | R3 Boehm init (R3a) | 6 | 0 | ~0.5 h | 0 | proof completeness only |

Gains are not additive per group: an IC passes through only when every group it reaches is closed. Critical path for the first pass-through IC: R2 + R1 + R4, about 3-4 h wall with R1a, R2a, R2b, R2c, R4a started together, then R1b. Total ~6-7 agent-hours, plus one serial Mode R run per group (no timing).

## 4. Order and parallelism

1. Start together (all read-only): R1a, R2a, R2b, R2c, R4a (Opus: R1a, R2c, R4a; Sonnet: R2a, R2b).
2. Then R1b (needs R1a); R3a folds into R2b.
3. Regenerate `docs/data/mono-glue-races.csv` once at the end, marking covered rows with the new mitigation text (e.g. `pre-warm at event 7 (R1)`) so the open count drops 85 to 0 by group; never hand-merge the csv between pieces.
4. No build of these pieces during a timing job (AGENTS.md); witnesses are correctness-only.

## 5. Design questions for Rodney

1. Do we want IC pass-through at all, or keep snapshot rows and use the 85 only as the proof gate? (decides whether the weight above is real)
2. Pre-warm trigger: world event 7 only, or re-check the guard bits at every cut?
3. Is a runtime "pointer unchanged" witness enough for the 31 inferred R2 rows, or do you want a static proof of every writer?
4. `0x11CB920`: if it is a refcount, interlocked detour or keep those two ICs on the sim thread?
5. A world load, travel or shutdown with a worker running: abort and re-snapshot, or wait for the worker to drain?
6. May the host converter pre-warm and the managed `Household::sRoommateManager` pre-warm (55 definitions, 18.7% in `autonomy-tier2.csv`) share one routine?
7. Close the proof at "85 covered, some inferred but witnessed", or hold any pass-through until all are static?

## Status log

(append: piece, branch, commit, passed, open)

- **R2a** (branch `docs/races-r2a`, static only, no game run): all 37 R2 rows classified by writer; columns `r2a_class`, `r2a_when`, `r2a_writers`, `r2a_confidence`, `r2a_note` added to `docs/data/mono-glue-races.csv` (empty for the other 311 rows). Method: capstone index of `TS3W.exe` (`tools/mono-glue-closure.py`, direct stores and `lea` escapes per global; no `lea` and no field writes for any of the 37), plus a raw scan of every `call`/`jmp` rel32, absolute code immediate and data dword in the exe for callers and vtable slots of each writer. Writer lists are complete for the 37 words [verified: index plus raw scan]. Result: **load-only 30** (8 verified, 22 inferred), **runtime 6** (3 lazy first-fill, 3 script or per-tick writers), **unknown 1** (`0x11D3E28`). Confidence: verified = trigger context shown by a direct call chain or a read of the code; inferred = writers only reachable through service vtable slots (Init slot 4, Shutdown slot 6) whose call time the static call graph does not show (the service manager calls them), so "once at load/shutdown" is the naming pattern, not a proof [inferred].
  - **Runtime-written list (needs R2b witness or a fix, not "read-only after load"):**
    - `0x11EE140`: static 16-byte SSE scratch vector rewritten per tick by `0xD69A30` (`WorldService_Update` -> `WorldManager_Update` -> `0xD6E350`) [verified chain]. Worker reads it in `0xD6DEF0`/`0xD699E0` (`IC_World_HasFloorFeaturesWorldPosImpl` path). Real race: same class as the R5 scratch vector; keep that IC on the sim thread or fill at the cut. Not closable by witness.
    - `0x11D6A78`: callback delegate table (index <= 0x4B), written by the script call `IC_World_RegisterCallbackDelegateImpl` (`0x7861A0`); reader `World_FireEvent` `0x78F2B0` [verified]. Writing IC stays on the sim thread; the worker only reads, so the risk is script registration after the first cut.
    - `0x11ED780`: user-tool manager pointer, set by script call `IC_UserToolUtils_InitUserToolManagerImpl` (`0xCF1800`), cleared by dtor `0xCF19D0` [verified].
    - `0x114CA08`, `0x114CA0C`, `0x11CB928`: lazy first-fill by `0x4F98E0` on the first call of an EAText wrapper (done byte `0x11CC928`); read only afterwards. The only other writer `0x4F9870` has no reference anywhere in the exe [verified]. Add `0x4F98E0` to the pre-warm list (R1 style), or witness the done byte.
  - **Unknown (to R2c/R2b):** `0x11D3E28` (swap-setter `0x72F1D0`, vtable `0xFFEB0C` slots 5 and 7; owner service not identified).
  - **World-load words the worker must not span** (replaced at each load or travel; R2c): `0x11ECBC4` (`WorldManager_LoadWorld` is also reached from `IC_LoadSaveManager_LoadGame_Impl`), `0x11D9128`, `0x11CF490`, `0x11ED034`, `0x11DC350`, and the `0x11D4068`/`0x11D8888`/`0x11D9848` object family.
  - **Open:** the 22 inferred rows need R2b (runtime witness) or a trace of the service manager's Init/Shutdown calls; the `--races` regeneration in section 4 drops the new `r2a_*` columns (the tool writes a fixed header), so re-apply them or teach the tool before regenerating.
