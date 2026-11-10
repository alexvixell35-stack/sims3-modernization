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
- **R2c** (branch `docs/races-r2c` from main c2bb5ed, static only, no build, no game run): **no drain exists; a world load can replace the R2 world words while a worker job is still inside an IC.** Safe today only because no IC passes through.
  - What the built code does at the next frame [verified: `native/ts3native/autshadow-run.cpp` `AsSimEnter`, `AsJobHook`, `AsWorldEvent` in `autshadow.cpp`]: if the job is still running (`g_mtBusy`), the sim thread sets `g_asStop` and goes on into the frame's script without waiting. The only wait is the test option `[AutShadow] WindowWaitUs`, default 0. In Satb act mode (`g_satbMode == SATB_ACT && g_satbLive`) it does not even set the stop; the job runs on beside the script by design. The epoch check at the cut (`g_asRunEpoch != g_asEpoch`) sees only world events that already fired.
  - Why that is not enough for R2: `WorldManager_LoadWorld` (`0xC6CF80`, writer of `0x11ECBC4`) is reached from `IC_LoadSaveManager_LoadGame_Impl`, i.e. inside the frame's script [verified chain, R2a]. The world events that bump the epoch (2, 3, 5, 7, 9, 0x0A) fire from the load itself, after the job got its stop. The managed job stops at its next object (`AutShadowNative.Stopped()`, KeepPartial rule `AutOffload.cs:375`), not at the next IC. So a job busy at that frame's `AsSimEnter` can read `0x11ECBC4`, `0x11D9128`, `0x11CF490`, `0x11ED034` or `0x11DC350` while the load replaces or clears them.
  - Today no harm [inferred, design §0]: worker ICs answer from snapshot rows, so no worker IC reads these words live. `autunseen` (docs/222) posts only in live mode and not while travelling or shutting down (`UfWhy`), which narrows the window to a job posted on the last live frame.
  - Fix needed before any pass-through IC reads an R2 word, and it answers §5 Q5:
    - (a) **Drain** [proposed]: a detour on the entries of the writers `0xC6CF80` (LoadWorld), `0xC671E0` (WorldManager_Init), `0xC6B500` (shutdown) and `0xC7E270` (WorldService Init; slot 4 of its vtable, so a vtable-slot hook). The other services' Init/Shutdown slots (`0x836210`/`0x836260`, `0x687310`/`0x686D90`, `0x9388D0`/`0x938900`) run in the same load sequence [inferred; R2b's witness confirms]. With a job in flight, each detour sets `g_asStop`, bumps the epoch and spins until `!g_mtBusy`. The job leaves after its current object (milliseconds), the load waits that long once, and Satb act mode is covered too.
    - (b) **Abort and re-snapshot**: cannot be made safe for pass-through. The worker could already be inside an IC body when the stop is set, so (a) is the option that closes the race.
  - Witness for the game run (with R2b): count the frames at whose `AsSimEnter` the job was busy, and whether any of them reached a load writer before the job left. One load, one travel, one return, one quit.
  - Open: the drain build itself (native, `autshadow-run.cpp` plus four hook sites; serial with other `autshadow*.cpp` work). Your Q5 decides it; nothing is built here.
- **R1a** (branch `docs/races-r2c`, static only: capstone reads of a TS3W.exe 1.67 copy, no build, no game run). All 41 R1 rows are mapped to their guard and their first-fill function [verified: disassembly]. The roots are from `docs/data/mono-glue-closure.csv`.

  | Rows | Guard / done | First fill | Order on first fill | Pre-warm call (sim thread, event 7) |
  |---|---|---|---|---|
  | A: `0x11EE54C`-`0x11EE584` (15 class slots) + guard `0x11EE588` | bits 0x1-0x4000 of dword `0x11EE588`, one per value kind | `0xD7BD40` (host `+0x50` converter, thiscall), per kind `slot = 0xD79CA0(ecx = this - 8, "Sims3.SimIFace", <name>)` | bit set before the slot store: a second thread sees the bit and reads a 0 slot | the same guarded fill per bit, with the converter's `this`; or one converter call per kind |
  | B: `0x11D7508`-`0x11D7518` | bit 1 of `0x11D7518` | `0x7935B0` (no arguments, returns the static; root `IC_EAText_GetMoneyString`) | bit before the four fields (non-thread-safe MSVC 2005 static) | call `0x7935B0` |
  | B: `0x11DA048`-`0x11DA058` | bit 1 of `0x11DA058` | `0x8A79B0` (same shape) | same | call `0x8A79B0` |
  | B: `0x11C7E54` | none | `0x4F4F40` stores 0 into the shared empty-string byte, which is always 0 | same-value byte store | none needed (benign) |
  | B: `0x123437C` / `0x1234380` | CRT `_onexit` table | `0xF7E8FC`, reached through the B statics' atexit call `0xF7E9A1` | the table update follows an import call at `0xF7F3AA`, the CRT exit lock [inferred] | covered by pre-warming the B statics (it is their atexit registration) |
  | C: `0x11C7984` | null check | `0x4D9AD0` (no arguments; root `IC_GameUtils_IsWorldInstalled`), fills it from `0x931020` | null test, then store | call `0x4D9AD0` |
  | C: `0x11DC168`-`0x11DC184` (DefaultAllocator) | `0x11DC184`, `0x11DC180` | `0x931020` (no arguments): vtable `0xFB8B38`, zeros, then the `0x11DC184`/`0x11DC180` publish; heap `[0x11CB864]` into `+4` | publishes `0x11DC180` before it stores the heap at `+4`: a second thread can use the allocator with a null heap | through `0x4D9AD0` (row above) |
  | C: `0x11E96C0` / `0x11E96C1` | done byte `0x11E96C1` | `0x9B0530` (root `IC_OnlineFeaturesHost_GetPersonaName`, via `0x9B04E0`) | done byte before the value byte | call `0x9B0530` with its receiver, or the IC once |
  | C: `0x11ECEEC` / `0x11ECEED` | done byte `0x11ECEED` | `0xC88D80` (no arguments; option `0x8000` of the store at `0x11CDDE0` via `0x523860`; root `IC_World_HasFloorFeaturesWorldPosImpl`) | value before done | call `0xC88D80` |

  - Four first-fill races are real, not just "worker writes": A (bit before slot), the two B statics (bit before fields), DefaultAllocator (publish before heap) and `0x9B0530` (done before value). In each, the second thread on a first call reads a zero. A pre-warm on the sim thread before the first cut closes them, as the plan says.
  - `0xD7BD40` also calls `GC_malloc(4)` on every call. A worker that ran it would allocate, which is outside R1 and already under M5's GC lock rules (P3).
  - For R1b: the pre-warm calls are cdecl with no arguments for `0x7935B0`, `0x8A79B0`, `0x4D9AD0` and `0xC88D80`. `0x9B0530` and the A fills need their receivers (the online-features host; the converter `this`, whose `this - 8` is what `0xD79CA0` receives), to be identified in R1b. Witness: the guard dword `0x11EE588 == 0x7FFF`, bit 1 of `0x11D7518` and `0x11DA058`, `[0x11C7984] != 0`, `0x11E96C1` and `0x11ECEED` set after the pre-warm.

- **R1b prep (2026-11-10, static + one live read):**
  - `0x9B0530` takes no receiver [verified]: a static bool getter, no `this`; its 10 direct callers push nothing for it. It reads option
    `Version` / `GameVersion` (default `0.0.0.0`) through `0x58A900`, parses it with `0x9B04E0`, sets the done byte `0x11E96C1`, then
    stores 1 only when all four version parts are 0 (a dev build).
  - Live read (rq1 IP job, about 10 min in): value `0x11E96C0` = 0, done = 1. On a versioned game the answer is 0, the same as the byte's
    zero default, so a second thread that reads before the store still gets the right answer. This row is **benign** on player
    installs [verified: code + live value]; the pre-warm is one call `0x9B0530()` and costs nothing, so R1b keeps it.
  - Same live read: the A guard `0x11EE588` = 0 and all 15 A slots `0x11EE54C`-`0x11EE584` are 0; the B guards `0x11D7518` and
    `0x11DA058` are 0; DefaultAllocator is published (`0x11DC180` = `0x11DC168`). So in this session neither A nor the B statics
    had been filled yet: their first fill can happen on whichever thread reaches them first, which is why the pre-warm matters.
  - So three real first-fill races are left: A (bit before slot), the two B statics (bit before fields) and DefaultAllocator
    (publish before heap, already filled by load in this session).
  - The A fill `0xD7BD40` is slot 20 of the vtable at `0x108B690` [verified]. That vtable is MonoScriptHost's base at `+8`:
    the ctor `0xD7E010` stores `0x108B818` at `+0`, `0x108B810` at `+4`, `0x108B690` at `+8` (slot 1 = `0xD77600`, R2a's row).
    Its only caller `0xD7F34A` allocates the host from the Scripting heap `[0x11EE51C]` (0x17C8 bytes) and stores it at
    **`[0x11EE514]`** (an R3 word).
  - Signature [verified]: `0xD7BD40(this = host + 8, type descriptor, out)`, `ret 8`. It allocates `GC_malloc(4)` into `*out` on
    every call and switches over 0x92 descriptor kinds (`word [desc + 0xA]`), so it is not a pre-warm call.
  - Pre-warm instead: for each A kind whose guard bit is clear, the slot is `0xD79CA0(host, "Sims3.SimIFace", name)`
    (thiscall on `[0x11EE514]`, the same call EA makes), stored before the bit, on the sim thread at event 7. Kinds:
    0x1 Vector2 `0x11EE584`, 0x2 Vector3 `0x11EE580`, 0x4 Matrix44 `0x11EE57C`, 0x8 Rect `0x11EE578`, 0x10 BoundingBox `0x11EE574`,
    0x20 ResourceKey `0x11EE570`, 0x40 RoutingSlotInfo `0x11EE56C`, 0x80 ContainmentSlotInfo `0x11EE568`, 0x100 LotCorners
    `0x11EE564`, 0x200 LotDisplayLevelInfo `0x11EE560`, 0x400 PondInfo `0x11EE55C`, 0x800 RoutePlanResult `0x11EE558`,
    0x1000 RoomInfo `0x11EE554`, 0x2000 Quaternion `0x11EE550`, 0x4000 ClipInfoNative `0x11EE54C` (all 15).
  - In the live IP session all 15 slots were still 0, Vector3 included: `0xD7BD40` is a rare marshal path, not the IC
    boundary's normal struct copy [inferred from the live read]. Only ICs that reach it need the A rows closed.

- **R2b + R3a witness (2026-11-10, built, selftest ok, game run rw1c done; one word reclassified after it, rerun queued):**
  - `[AutShadow] RaceWitness=1` (default 0, test runs only): `AsRaceWitness` (autshadow.cpp) reads 43 words on the sim thread at
    each cut, right after the cut reads the epoch. Counters v914 (contributor block, `0x5F80`): `r2wOn`, `r2wCuts`, `r2wChanged`.
  - The words: the 31 R2 rows R2a classed load or unknown and the 6 R3 rows are expected stable; the 6 R2 rows R2a found written
    at run time (`0x114CA0C`, `0x114CA08`, `0x11CB928` lazy; `0x11D6A78`, `0x11ED780`, `0x11EE140` runtime) are read too.
  - A change between two cuts of one epoch (no world event between) logs once per word (`autshadow race witness: ... inside
    epoch`); for the 37 stable words it also counts in `r2wChanged`, which must stay 0. A change across epochs logs once as a
    load-time write and never counts.
  - Selftest `TestAutShadowRaceWitness`: off reads nothing, the first cut only keeps values, a stable change counts and logs once,
    a second change counts without a second log, an epoch change only logs, a moving word logs and never counts.
  - Game run: `tools/harness/queues/races-witness.json`, one BP load, one trip to France and back, one quit, then 30 min of IP.
  - Result (run rw1c, 2026-11-10, both jobs PASS, a leak census and holder scan ran beside both):
    - BP travel job: `r2wCuts` 5, `r2wChanged` 0, no witness line. Only 5 cuts, so this job says little.
    - IP 30 min: `r2wCuts` 251, `r2wChanged` 3, all three on one word, `0x11EE520` (log: `changed inside epoch 481:
      0x19FC0000 -> 0x1A7C0000`). That word is Boehm's heap-size counter: `GC_win32_get_mem` `0xD70900` adds each new heap
      section to it (mono-glue-closure.csv), so it grows whenever the GC heap grows, here by 8 MiB. Classing it "expected
      stable" was the witness's mistake, not a race: the R3 claim for it is "written under the P3 GC lock", which a value
      witness cannot test. It moved to the logged-only group (36 stable words now, 7 logged).
    - The other 36 stable words: no change inside an epoch in 251 cuts. Across epochs `0x11EE520` and `0x11EE140` changed
      (load-time lines); `0x11EE140` also changed inside an epoch, as R2a found (expected).
    - Not shown: `r2wChanged` 0 with the new grouping (the IP job is queued again), and any word on a world other than
      BP, France and IP.

- **R1b pre-warm (2026-11-10, built, selftest ok, game run rw1c done):**
  - `[AutShadow] RacePrewarm=1` (default 0, test runs only): `AsRacePrewarm` runs from `AsWorldEvent` at event 7, on the sim
    thread. Counters v915 (`0x5F8C`): `r1pOn`, `r1pRuns`, `r1pFills`; one log line per pass with the A guard before and after.
  - It calls the five lazy fills themselves (`0x7935B0`, `0x8A79B0`, `0x4D9AD0`, `0xC88D80`, `0x9B0530`: cdecl, no arguments,
    plain `ret` [verified]), counting the ones that were cold.
  - For each clear A guard bit it stores `0xD79CA0(host, "Sims3.SimIFace", name)` into the slot, then sets the bit (slot
    before bit; EA sets the bit first). It does not call `0xD7BD40`, which would allocate. No script host yet = A waits.
  - Selftest `TestAutShadowRacePrewarm`: fake fills and class lookup; off does nothing; each fill runs once; cold ones count;
    every clear slot is written before its bit; a set slot is kept; no host leaves the A slots.
  - Game run: job 2 of `races-witness.json` (IP, 30 min, with the witness on).
  - Result (run rw1c): `r1pRuns` 1, `r1pFills` 17; log `autshadow race prewarm #1: 17 fills, A guard 0x0000 -> 0x7FFF`
    (all 15 A slots filled and 2 of the 5 lazy fills were still cold at event 7). 30 min of IP after it: PASS, no crash,
    no forbidden log line. Not measured: whether a worker would have hit a cold fill without it.
- **Witness rerun rw1d (2026-11-10, head a8df0d6):** both jobs of `races-witness.json` PASS (BP travel 9.3 min, IP 30 min
  profile 38.7 min), `r2wChanged` 0 with the heap-size counter `0x11EE520` in the logged-only group. A map-view toggle
  rider (issue #40 leak hunt, one toggle every 2 s) ran beside job 2.
- **Csv regeneration (2026-11-10): not done, and the honest open count is not 0.**
  - Not done: `tools/mono-glue-closure.py` needs `game-copy/analysis/functions.csv` (the Ghidra export of
    `tools/ghidra/ExportFunctions.java`), which only the maintainer's machine has. A new Ghidra project here would give
    other function bounds, so the diff would not stay inside the mitigation fields. Command for that machine:
    `python tools/mono-glue-closure.py --exe game-copy/bin/TS3W.exe --functions game-copy/analysis/functions.csv --cache game-copy/analysis --races docs/data/mono-glue-races.csv --csv docs/data/mono-glue-closure.csv`.
    The tool writes a fixed header: it must carry the `r2a_*` columns over by `global`, or they are lost.
  - What the finished pieces support, read row by row against this log (85 open today = R1 41 + R2 37 + R3 6 + R4 1):
    - **R4: 1 -> 0.** `0x11CB920` is a lazy fill with a constant (`GetACP()`), harmless (R4a).
    - **R1: 41 -> 38.** Closable as benign: `0x11C7E54` (the store writes the same value, R1a), `0x11E96C0` and
      `0x11E96C1` (R1b prep). The other 38 are covered only by `RacePrewarm=1`, which is default 0 and for test runs;
      the worker abort on a clear guard is not built and Q2 (trigger) is open. They close when the pre-warm is accepted
      as the mitigation.
    - **R2: 37 -> 34.** Closable: `0x11CB864`, `0x11AF670`, `0x11EBAE0` (written at start-up only, R2a). Open: the 8
      world-load words and the 4 words cleared at shutdown (R2c: no drain exists, the fix is proposed only, Q5); 14
      inferred load rows (witness stable in rw1c and rw1d, but Q3 asks whether a runtime witness is enough);
      `0x11EE140` (a real race, rewritten every tick); `0x11D6A78` and `0x11ED780` (written by script calls at run
      time, no rule yet); `0x114CA08`, `0x114CA0C`, `0x11CB928` (lazy fill by `0x4F98E0`, not in the pre-warm list);
      `0x11D3E28` (owner unknown).
    - **R3: 6 -> 6.** The witness covers them, but the static check "only under the P3 lock or at init" is not written
      down, and `0x11EE520` changes at run time (it is the heap-size counter), so a value witness proves nothing for it.
  - So the csv can go from 85 to 78 open today, and to 0 only after Q2, Q3 and Q5 are answered and the drain exists.
  - One earlier line of §1 is refuted by the R1b prep read: about 10 minutes into IP all 15 A slots and both B guards
    were still 0, so "the sim thread fills them long before a worker exists" does not hold.
