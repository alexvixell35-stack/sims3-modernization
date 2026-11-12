# 23. Ideas and roadmap beyond the current plan

`PLAN.md` holds the work in progress ("Next, in order"). This doc collects what comes after, or what needs more hands than one machine: infrastructure for many contributors, and larger research bets. Each idea lists value, effort and risk (low / medium / high) and the issue it should become. Claim an idea by opening that issue with the matching template and commenting on it (see `CONTRIBUTING.md`).

Claims here are `[inferred]` unless marked otherwise; most are proposals.

## Summary

| # | Idea | Value | Effort | Risk | Issue template |
|---|---|---|---|---|---|
| 1 | Community baseline saves recipe | high | low | low | fix proposal (harness) |
| 2 | CI on GitHub Actions | high | medium | low | fix proposal (harness) |
| 3 | One-click player installer and releases | high | medium | medium | fix proposal (installer) |
| 4 | Crash-report aggregator | high | medium | low | fix proposal (harness) |
| 5 | Multi-laptop test farm and job queue | high | high | medium | research, then fix proposal |
| 6 | Public results dashboard | medium | low | low | fix proposal (harness) |
| 7 | Telemetry-free benchmark mode | high | medium | low | fix proposal (harness) |
| 8 | Mod-conflict scanner for players | medium | low | low | fix proposal (installer) |
| 9 | Shareable symbol file of verified EA addresses | high | low | medium (legal) | research |
| 10 | Decision log (ADRs) | medium | low | low | docs |
| 11 | Allocator: mimalloc vs our cache | medium | medium | high | research |
| 12 | Texture streaming and DXVK tuning for Error 12 | high | medium | low | research |
| 13 | World-level own route planner | high | high | high | research |
| 14 | Animation LOD | medium | medium | medium | research |
| 15 | Mono interpreter speedups | high | high | high | research |
| 16 | 64-bit research notes | low (clarity) | low | low | research |
| 17 | Sim-thread offload architecture (long-term goal) | high | high | high | research |
| 18 | Readability refactor (packages, files, docs; same behavior) | medium | medium (~1-2 days of agents + 1 validation night) | medium (native counter offsets, unity build split, byte-exact patch tables, install order) | refactor |

#18 waits until after the Mono offload (Rodney, 2026-10-21): by then most hitches are fixed and the project is in maintenance mode. Guards: static_asserts, selftests, identical install-log lines, release validation queue; no parallel feature branches while it runs.

Suggested order: 1, 2, 10, 9 first (they make every later contribution cheaper and comparable), then 7, 4, 3, 6, then the research bets as contributors with RE experience join.

## Next big wins (2026-10-15)

Ranked by expected gain against effort, with cheap gates first. Detail lives in `PLAN.md` (Action plan, lanes G/M/H) and the linked docs; this table only orders them.

| # | Item | Expected gain | Effort | Risk | Gate (decides it) | Status |
|---|---|---|---|---|---|---|
| 1 | `kFastRefTable` default on ([CHANGELOG v0.1.2](../CHANGELOG.md)) | save BP 12.7 -> 10.3 s (-19%), IP 11.6 -> ~10.0 s (-14%) [measured] | low (one flag, rebuild) | low | university + future travel cases in the save matrix | 27/28 matrix cases PASS; waits for Rodney's OK |
| 2 | `[HeightCache]` A/B ([docs/20 §4b](20-offload-candidates.md)) | ~3.5-10 ms/s [inferred]; BP A/B route ms per route -34% [measured, unconfirmed: uneven slices] | low (runs only) | low (Verify: 0 mismatches in 4.41M calls) | 20+ min A/B per world, ideally seeded; IP Verify with a terrain edit and a lot placement | built (0.10.2), off |
| 3 | Route gate, H6 ([docs/18 "H6 plan"](18-route-search-off-thread.md)) | up to route 95/82/246 ms/s BP/SV/IP off the sim thread [measured upper bound]; real share = G11 | high (S0-S5 ~4.5-5 d) | high (async routing crashed 3 times before) | G11 `/routelock`: lock-free share >= 30 ms/s IP and >= 15 ms/s BP, plus gate-site entries | S0 done; G11 probe built, never run |
| 4 | IC-transition lock `0x11F43A8` under `0xDA5970` ([docs/29 §5](29-mono-jit.md), [docs/258](258-ic-lock-valuetype-equals.md)): really Mono's loader lock, taken once per field by `ValueType.InternalEquals` (`Enum.Equals` path) | 6-9 ms/s for a per-class `InternalEquals` fast path [inferred; lock ~8.7 ms/s, uncontended before AutShadow] | medium (2-3 h + monohost corpus) | low | monohost exactness corpus, then `[IcLockCheck]` count with AutShadow on, then BP/IP A/B | analysed 2026-11-12, not built |
| 5 | S3PP type-hash cache confirm (`MonoTypeEHashProbe`, [docs/29 §5](29-mono-jit.md)) | ~20-30 ms/s inclusive, already S3PP's [inferred]; decides that we do not duplicate it | low (one simstack run) | low | simstack BP run, S3PP cache on vs off | pending |
| 6 | Save levers ([docs/26](26-incremental-save.md), PLAN "Research done") | cached reflection ~0.5-1 s, compressed-bytes cache ~0.25 s, life-event trim ~0.3-1.2 s (opt-in) per save [inferred] | medium | medium (save path) | full save matrix + save s on/off | ranked, not built |
| 7 | Mono second-thread patch `[MonoThread]` ([docs/30](30-mono-second-thread.md)) | enabler for managed offload (autonomy ~110 ms/s, [docs/28](28-sim-thread-map.md)); costs 0.2-1.5% sim-thread [inferred] | high (~2.5-3.5 weeks) | high (GC, interpreter state) | `[MonoThread]` Mode=1 proof with Verify; `[GcThreadCheck]` clean in IP, save, travel | design done; BP GcThreadCheck 0 foreign allocations [measured] |
| 8 | Animation phase split + C++ rewrite ([docs/22 §7](22-parallel-animation.md)) | ~5-15 ms/s [inferred] | high | high | G10: docs/22 §6 timing split (per-phase ms/s of `0x940430`) | research done; timers not added |
| 9 | Address space: S3SS mimalloc arena ([docs/08 §2.5](08-resource-offload-design.md)) | <= ~64 MB largest free block early in a session [inferred] | low (upstream ask for a 32 MiB option) | low | runtime confirm: `NtAllocateVirtualMemory` >= 32 MB reserve backtrace on `wear_parts` | cause inferred, no config switch |
| 10 | Address space: catalog ~8 MB resident ([docs/08 §2.5](08-resource-offload-design.md)) | ~8 MB for 14,577 products [inferred]; lazy decode not viable | medium | medium | catalog cost at the main menu (docs/08 M9 step 1) | queue job pending |

## Done or dropped (2026-10-15)

- Skip-Evaluate for idle off-screen actors: dropped; idle actors already skip it ([docs/22 §5.1a](22-parallel-animation.md)).
- Drop off-screen Sim meshes/textures: dropped; ~0.15 MB per Sim [measured] (PLAN lane G item 6, M11).
- Mono JIT: dropped; EA "mint" interpreter, no JIT code, yielding tasks keep interpreter frames ([docs/29](29-mono-jit.md)).
- Footprint quadtree cache (#8): NO-GO, physics radius query on moving Sims, 0.5% of samples ([docs/20 §4b](20-offload-candidates.md)).
- Catalog lazy OBJD decode: not viable, the in-memory filter needs every decoded OBJD ([docs/08 §2.5](08-resource-offload-design.md)).
- Done: sim-thread map ([docs/28](28-sim-thread-map.md)), Mono helper split G9 ([docs/29 §5](29-mono-jit.md)), second-thread design ([docs/30](30-mono-second-thread.md)), H6 plan ([docs/18](18-route-search-off-thread.md)), M9 128 MB block explained ([docs/08 §2.5](08-resource-offload-design.md)), `s3pp` label check (no stale labels, PLAN lane G item 4).

## Contributor infrastructure

### 1. Community baseline saves recipe

Results from different machines only compare when everyone measures the same save. Saves cannot be shared (EA content, personal data), but they can be rebuilt deterministically.

- `tools/harness/new-save.ts` already picks the household by name and member count, repeatably. `age-save.ts` ages with autopilot, `keep_alive` and declines, but the game's own randomness (story progression, autonomy) differs per run.
- Proposal: a `docs/data/baseline-saves.json` recipe per world (world file, household, days, checkpoints, settings such as story progression and aging, the exact installer set used while aging) and one command `node tools/harness/baseline-save.ts --world bp` that runs both scripts with it. Check equivalence with `save-stats.ts` / `save-sources.ts` (Sim, household, object counts within a tolerance) and publish those counts so a contributor can check that their save is "the" Baseline-BP.
- Open question: can the game's RNG be seeded (a native patch on the seed source) so aging is bit-identical? Research first.
- Value high, effort low, risk low. Issue: `[fix] Deterministic community baseline saves (new-save + age-save recipe)`.

### 2. CI on GitHub Actions

- Native: `windows-latest` has MSVC; run `native/ts3native/build.cmd` (path to `vcvars32.bat` made configurable) including `selftest.exe` and `decompress-test.exe`. `waterjps-test.exe` needs code from the user's `TS3W.exe`, so it stays local; CI builds it only.
- Mods: `build-fixes.ts` and `build-agent.ts` compile against the game's own DLLs, which must never be in the repo. Option: generate **reference stubs** (public type and member signatures only, empty bodies, no EA code or resources) locally with a script, compile against them in CI. Even signatures may be a legal grey zone; decide in an ADR (idea 10) before committing any stub. Fallback: CI only runs a C# syntax check (`csc /t:library` with `/nostdlib` won't resolve types, so this is weak).
- Harness: run every tool's `--self-check` (`milestone.ts`, `tests-be.ts`, `game-alive.ts`, `content-health.ts --self-test`), `node --check` on `.ts`/`.mjs`, YAML lint on the issue templates, a grep gate for personal paths (`C:\Users\`, `/c/Users/`) and EA file types in the diff.
- Value high, effort medium, risk low. Issue: `[fix] CI: native build + self-tests, harness self-checks, forbidden-file gate`.

### 3. One-click player installer and release packaging

- GitHub Releases with `ts3fixes.package`, `ts3native.asi` + `ts3native.ini` (safe defaults: only measured, default-on sections) and a small installer that wraps `install-fixes.ts` (downloads community fixes from their official sources, never re-hosts, verifies SHA-256, backs up, can uninstall). No EA files.
- Needs: a signed/hashed release checklist, a player README in plain words, a version check in `ts3native.log` and the mod.
- Risk medium: players without the harness hit untested combinations (other core mods, non-Steam builds). The ASI must refuse to patch when bytes at a patch site differ (it already logs and skips) and the installer must refuse non-1.67 exes.
- Issue: `[fix] Release packaging: ts3fixes.package + ts3native.asi + installer on GitHub Releases`.

### 4. Crash-report aggregator

- Contributors upload `xcpt` summaries (exception code, faulting module + RVA, top frames as module+offset, active change-set key from the ledger), never dumps.
- A script (`tools/harness/crash-cluster.ts`) reads `docs/data/crashes/*.txt` plus submitted summaries and clusters by module+RVA and change set, extending `crash-ledger.ts triage`. Output: a table "address -> count, builds, switches on", which points straight at the patch to suspect.
- Issue: `[fix] Crash summaries: submission format + crash-ledger clustering by address`.

### 5. Multi-laptop test farm and job queue

- Game tests are serial per machine; contributors' laptops are idle most of the day. A coordinator (one Claude Code session, or a small script) writes jobs as files (`jobs/<id>.json`: build hash, ini, installer set, save recipe, command) to a branch; each farm machine's agent pulls, runs `cycle.ts` + the command, and pushes `docs/data/results/...` back. Git is the queue and the results store; no server.
- Needs: idea 1 (same saves), a machine profile file (hardware line), a lock per job (a claim commit), a kill switch, and strict rules: farm agents run only commands from the job allow-list, never arbitrary text from a job file.
- Risk medium: a bad build crashing many machines; job files as an injection vector for agents. Start with two machines and read-only jobs (profiles of merged builds).
- Issue: `[research] Test farm: git-based job queue for harness runs across contributor machines`.

### 6. Public results dashboard

- GitHub Pages built from `docs/data/results/*.md` (parse the hardware line, build line and the table): per fix, on vs off across machines, iterations/s and hitches/s, with the hardware shown next to each point. A static page from a build script, no backend.
- Issue: `[fix] Results dashboard on GitHub Pages from docs/data/results`.

### 7. Telemetry-free benchmark mode

- Today's numbers are sim-thread metrics (iterations/s, hitches) from the probe; FPS needs test B. Players and reviewers want FPS from a fixed scenario.
- Proposal: an agent action that pins the camera to a fixed route (a few waypoints over the busiest lots) at fixed speed on the baseline save, runs for N minutes, and reports frame-time percentiles (`frame-times.js`) and sim metrics. "Telemetry-free": nothing leaves the machine; the output is a results file the player chooses to submit.
- Issue: `[fix] Benchmark mode: fixed camera route + fixed save, frame-time percentiles`.

### 8. Mod-conflict scanner for players' Mods folders

- `tools/setup/check-conflicts.ts` already finds CLASH/WARN/INFO between packages and core mods; `content-health.ts` checks Resource.cfg depth and caches. Extend for players: known-bad or outdated mods list (by resource or name, maintained in a JSON), duplicate copies (sync folders), merged packages over the size limit, and overlap with what ts3fixes patches (so a player knows which fix is shadowed).
- Issue: `[fix] Player Mods folder scanner (extends check-conflicts.ts)`.

### 9. Knowledge base of verified EA function addresses

- Our docs name hundreds of addresses (`0x82C6E0` Route_PlanLocked, `0x4E5E40` allocator core, ...). A shareable symbol file (CSV or JSON: address, name, signature guess, confidence, doc link; addresses and names only, no code or bytes) lets every contributor start Ghidra with our names applied (`tools/ghidra/ApplyInternalCallNames.java` pattern; `tools/ghidra/anchors.csv` is a start).
- Legal check first (names and addresses we derived, not EA's text). Record the decision as an ADR.
- Issue: `[research] Shareable symbol file: verified addresses and names for TS3W.exe 1.67`.

### 10. Decision log (ADRs)

- `docs/adr/NNNN-title.md`: context, decision, consequences, status. Back-fill the big ones: pausing native async routing (three crashes, the unlocked per-lot search state, docs/18), dropping the 64-bit resource cache helper (docs/08 phase 0), stuck Sims treated as solved (log only), default-off until measured, no re-hosting of community fixes, behaviour specs instead of porting unlicensed mod code.
- Issue: `[research] Decision log: ADRs for major choices (async routing pause, offload helper drop, ...)`.

## Research bets

### 11. Allocator: mimalloc vs our cache

- S3SS offers a mimalloc option; our ts3native 0.8.0 cache sits in front of EA's dlmalloc derivative (docs/15 Phase 8) and gave +19% iterations/s in one A/B (PLAN, 2026-10-13). Open: does S3SS's mimalloc replace the same heap, do they stack, and which wastes less 32-bit address space (Error 12)?
- Measure: both, each alone, on the same save; address space after 30 min; crash-free hours.
- 2026-10-15: the one-time 128 MB reservation on the first CC outfit build [measured] is most likely S3SS mimalloc arena doubling [inferred]; see Next big wins #9.
- Risk high (heap corruption shows up late). Issue: `[research] Allocator: S3SS mimalloc vs ts3native alloc cache, address space and speed`.

### 12. Texture streaming and DXVK tuning for Error 12

- DXVK moves textures out of the process (docs/05: vanilla cannot even load the save). Remaining Error 12 risk is address space from caches and CC (docs/08, docs/17). Research: DXVK options for the 32-bit game (`d3d9.textureMemory`, memory budget behaviour on laptops with 4-8 GB VRAM), the engine's own texture LOD and streaming budgets in `graphicsrules`, and whether `GetAvailableTextureMem` is read elsewhere (PLAN next task 1).
- Issue: `[research] Error 12: texture streaming budgets and DXVK settings on 4-8 GB laptop GPUs`.

### 13. World-level own route planner

- docs/18: running the game's planner off-thread hit unlocked shared lot state; making searches cheaper is the current path. A bolder option: our own world-level planner (hierarchical: precomputed lot-to-lot graph + local search) answering the common long trips, falling back to the game's planner. High value for townie traffic, high effort and risk (route semantics: portals, doors, ownership rules, footprints).
- Start with research: what fraction of plan time is world-level vs lot-level per world (docs/04a §8 numbers), and which result fields callers read.
- Issue: `[research] Own world-level route planner: share of plan time and result contract`.

### 14. Animation LOD

- docs/22: parallel animation update is unsafe; §5 lists serial alternatives. Animation LOD (update off-screen or distant actors at a lower rate) is the cheapest. Measure first (docs/22 §6), then a native switch with in-load A/B.
- 2026-10-15: skip-Evaluate variant dropped and off-screen mesh drop dropped (see "Done or dropped"); remaining path is Next big wins #8.
- Issue: `[research] Animation LOD for off-screen Sims (docs/22 §5)`.

### 15. Mono interpreter speedups

- docs/04 F6/F7: scripts run in Mono 1.2's interpreter, the largest sim-thread cost. Ideas: a method lookup cache, inline caches for virtual/interface calls, faster field access paths, cheaper `isinst`/`castclass`. Each is a native patch inside the interpreter loop, with correctness risk across all game scripts.
- 2026-10-15: JIT dropped; helper split done ([docs/29 §5](29-mono-jit.md)): the top helper is already cached by S3PP, our first own target is the IC-transition lock (Next big wins #4, #5).
- Research first: which interpreter opcodes and lookups dominate (sample the interpreter with the probe), and what upstream Mono later changed in those paths (as design ideas, not code).
- Issue: `[research] Mono interpreter hot paths: method/virtual call caches`.

### 16. 64-bit research notes

- Why it is not realistic: a 32-bit exe with ~40k functions, embedded Mono 1.2 (32-bit, interpreter), 32-bit pointer assumptions in data files and saves, third-party 32-bit plugins (S3SS, DXVK x32, ASI loader). What it would take: per-function rewrites verified live, or an out-of-process 64-bit helper (docs/08 tried a resource cache helper and dropped it). Write it down once so the question stops costing time.
- Issue: `[research] 64-bit: why not, and what would be needed (notes only)`.

### 17. Sim-thread offload architecture (long-term goal)

- Goal (Rodney, 2026-10-14): move everything that can safely leave the sim thread to other cores, so the game scales with longer saves, more objects and more Sims instead of hitting one core's limit. Value high, effort high, risk high. Needs a redesign of how work reaches the sim thread, built step by step.
- Acceptance rule: an offload step is accepted with no speed gain today, or up to ~2% fewer sim iterations/s, if it is stable (no crashes, no new stuck Sims, same results) and moves measurable work off the sim thread (route ms/s, stage ms/s, sampler share). Over ~2% needs a plan to win it back. See `PLAN.md` (Goal).
- Known candidates and blockers: route search on a worker (`[RouteWorker]` works, synchronous today; async blocked by unlocked per-lot routing state, docs/18), save write and backup copy (`[SaveWrite]`), resource decoding (`[Decompress]`), CAS tile building (docs/21: mostly UI layout, which must stay on its thread), GC marking, autonomy scoring and story progression (Mono, research).
- First step: a map of sim-thread work by owner (native vs Mono, per stage) from the existing profiles, ranked by what could run elsewhere and what shared state blocks it. Done 2026-10-15: [docs/28-sim-thread-map.md](28-sim-thread-map.md) (top: route search with a routing gate, save backup copy and compression; every managed offload waits on whether C# can run on a second thread).
- Second managed thread: design in [docs/30-mono-second-thread.md](30-mono-second-thread.md) (`[MonoThread]`: four GC/interpreter patches, a first proof on copied input with Verify, ~2.5-3.5 weeks; game script state stays sim-thread-only).
- Issue: `[research] Sim-thread offload architecture: map, blockers, step plan`.

### 18. Route plan cache: measure repeats first (2026-10-19)

> **Changes 2026-10-19:** new section. Build only; the game was not run.

- Idea: an on-thread speedup for the sim thread's synchronous route plans (63 ms/s of plan time in BP, docs/87): when the same plan is asked again with identical inputs and nothing it depends on changed, hand back the earlier answer instead of searching again. The negative cache `[RouteCache]` (failed plans only, 0.5 m cells, no world check) hit 0.2% of plans in game (2026-10-14, ini note), so the question is whether full repeats are any more common.
- Probe built: ts3native **0.32.10** (a patch number well above master's 0.32.2, so parallel branches do not clash), `[PlanRepeat] Enabled=0` by default, counters **v60** (`pr*`, [registry](registry.md)), `native/ts3native/planrepeat.cpp`, observe only.
  - Where [verified in our code]: inside the `Route_PlanLocked 0x82C6E0` detour, around the plan itself, after the original's own early-outs. A cache would sit there: the arguments are the whole request after the route IC built the start points and drew the goals (docs/92 §6), and the time is the plan time P10 measured. The route IC (Plan, Replan, PlanFromPoint, ReplanFromPosition) comes from `[RouteSnap]`'s route IC frames on the sim thread; plans outside a route IC and plans on other threads are their own classes. A plan nested in a plan counts in the outer one.
  - Key: strict 64-bit FNV over the routing world, agent word, doPlan byte, follower ObjectGuid, options `O+0x08` and RouteOption/RouteOption2 (`O+0x28..+0x34`), the ignore and forbidden portal id lists, the preferred portal, and every start and goal record raw (64 bytes each, so priorities, radii and flags count as well as positions). The loose key is `[RouteCache]`'s 0.5 m cell key, a second, upper estimate.
  - World version: the P1/P1b change counters (`[RouteSnap] Count=1`, docs/94 §10, §15), which `[PlanRepeat]` turns on itself: every writer leaf row except W6 cache fills, on any thread, plus the 35 routing writer IC slots. The "static" version leaves out the per-object footprint family (W1, W2, footprint core, private footprints, footprint removes), which moving Sims write all the time (docs/89). A ring entry holds the version at the end of its plan, so a plan's own lazy builds do not count as a change.
  - Ring: the last 4096 plans (key, loose key, both versions, time, result hash). Result = success byte, then per path its status, node count and node positions (docs/93 §4.2), so "same answer" means the same path to the bit.
  - Counts per class: exact repeats (same key, same version) within 1 / 5 / 30 s and their plan time (the savings bound, ms/s); same or other answer on an exact repeat (another answer with no change = nondeterminism such as tie order, or an input neither the key nor the version sees); repeats where only footprints changed, with their time and answer match (what a finer, per-lot version could win); repeats after other routing changes; loose-key repeats. Log line `plan repeat: ...` every 60 s; the harness's counter dump is the end dump.
- Run (counts only, daytime OK): park `native/ts3native/out/ts3native.asi` as `game-copy/ts3native.asi.planrepeat`, run `node tools/harness/queue.ts tools/harness/queues/plan-repeat.json` (`bp-plan-repeat`, `ip-plan-repeat`: Baseline-BP/IP-Spring, seed 12345, 10 min each, only `PlanRepeat.Enabled=1`), then `node tools/harness/plan-repeat-report.ts probe-out/queue/plan-repeat-<date>-<job>-native.json`.
- **Decision rule:** build the cache only if sim-thread exact repeats (30 s column) save **>= ~5 ms/s** in BP or IP; below that, drop the idea. If the footprints-only column is large and its answers mostly match, a per-lot version is the variant to study. A cache must not change answers, so more than ~1% other answers on exact repeats needs its cause found first.
- Limits [inferred]: the world version sees only what the P1 counters see (docs/94 §15: ~2% of writer hits unattributed, all still counted); script callbacks during the search (portal lock answers, docs/92 §5) are not in the key, so they would show as other answers; the 30 s column is a lower bound when the ring evicts young entries (`prEvictYoung`, above ~136 plans/s); probe cost is one hash over inputs and result plus a ring scan per plan, of the order of 10 us per plan [not measured].
- Effort: probe ~1.5 h (done); two runs ~25 min of game time plus reading; a cache, if the rule says build, ~3-5 h (answer copy, version check, tests).

### 18.1 Results 2026-10-19 (`plan-repeat.json`, 0.32.5, only `[PlanRepeat]` on)

| World | Sim-thread plans (10 min) | Plan time | Exact repeats <= 30 s | Same key, world changed | Loose-key repeats | Savings bound |
|---|---|---|---|---|---|---|
| BP | 19,882 | 108.5 ms/s | 0 | 182 | 1 | 0.00 ms/s |
| IP | 9,810 | 166.9 ms/s | 1 | - | - | 0.00 ms/s |

**Verdict [measured]: no plan cache.** Plans almost never repeat with the same inputs: Sims move between requests, so start points differ, and the few same-key repeats come after a world change. The 108-167 ms/s of sim-thread plan time is real search work; only taking it off the sim thread (docs/94 option G) removes it.

### 19. Split into separate projects (Rodney 2026-10-23; plan only, not started)

Idea: four repositories instead of one.

| Project | Holds | Depends on |
|---|---|---|
| research (base) | RE notes, docs/, ledger, data CSVs, Ghidra/REA scripts, pointers to the others | nothing |
| profiler (test kit) | `tools/harness`, `mods/ts3agent`, repros, queues, hitch/freeze reports, measurement method (docs/161-170) | a counter schema from ts3native (version + offsets) |
| story fuzzer | `story-*.ts`, stories/, StoryHandlers | profiler's agent and measurement |
| sims3-modernization | releases only: ts3native, ts3fixes, installer, CHANGELOG, player docs | profiler for its release checks |

Assessment (2026-10-23):
- Worth it: the profiler is reusable by other modders (Apex Radiance already takes the runner, PR #33); the release repo gets small and player-facing; research notes stop mixing with build files; each can have its own issues and contributors.
- Cost now: the profiler reads ts3native counters by fixed offsets (docs/registry.md), ts3fixes switches by name and repros flip both; today one commit changes all three. Split repos turn that into versioned contracts and cross-repo PRs. Release week (v0.1.5, 2026-10-27) is the wrong time.
- Story fuzzer as its own repo is early: it shares the agent command set and the measurement join with the profiler. Start it as a folder of the profiler repo; move it out once its agent commands are a separate package.
- Order when started: (1) export a counter schema file from ts3native (generated, versioned) and make the harness read it instead of hard-coded offsets; (2) split research (docs only, no code deps; `git filter-repo` keeps history); (3) split profiler + fuzzer; (4) the rest stays as sims3-modernization. Public scrub (no paths, names, decompiled code) runs per repo.
- Start after v0.1.5 ships and the Mono offload work is in maintenance (same rule as the readability refactor, #18 in PLAN/docs/23).

### 20. Package lookup index ("virtual CC merge") (Rodney 2026-11-04; parked until after the memory fixes)

Merging CC packages helps loading because `GetPackageSource` / `FindDatabase` probe every registered package in priority order on each resource cache miss (~440 probes with heavy CC + EA) [verified mechanism, docs/08 §2.2]; memory is unchanged (docs/08 §3). Instead of physical merged copies (doubles disk with ~31 GB CC, long rebuilds, must reproduce override order, would have to hide the player's originals), ts3native builds one key -> winning-package index from the packages' tables of contents at start-up, in the game's own priority order, and hooks `GetPackageSource` to a single lookup with the game's scan as fallback. Optional index cache file in our own folder (never Mods or saves), keyed by package paths + sizes + mtimes, written only with disk headroom (e.g. free >= 5x file + fixed reserve), else in memory only.
- Expected: faster world loads with heavy CC, cheaper first lookups in CAS/build-buy/new Sims [inferred]. Costs a few to tens of MB address space: no Error 12 gain (flag rule).
- Before building: check S3PP `PackageIndexWarmup` (TOC pre-read only) and Apex Radiance for an existing lookup index (lookups are Apex's area); measure probes per miss and their share of load time and in-play hitches on a heavy-CC save vs Baseline-BP (~1 h agent + 2 game runs). Build off by default only with a measured gain path, then propose to Apex.

### 21. Unseen-direct go-live follow-ups (Rodney 2026-11-08: `autunseen` ON for the release, these causes fixed later)

The gate 2 credit A/B passed (BP +6.4, IP +12.2 ms/s, 6 runs per world) but left four open items; none met the AGENTS.md clear-regression rule (docs/release-notes/unreleased-dev.md).
- **gate2-report R5 bar:** `ufLong` per pair <= +2 failed on 2 IP pairs while the pooled counts were on 46 vs off 42 and frame hitches >= 300 ms on 55 vs off 59 (ratio 0.92 [0.63, 1.36]). Replace the per-pair +2 with the pooled exact-Poisson on/off ratio, as `hitch-ab.ts` (`rateCompare`) does: fail only when the 95% CI lies wholly above 1. `tools/harness/gate2-report.ts` `BAR.r5LongExtra`. **Done 2026-11-12:** R5 now pools `ufLong` over the arms' native seconds and uses `rateCompare`; `BAR.r5LongExtra` removed; self-check covers the 42 -> 46 IP case (pass), 10 -> 40 (fail), 0 -> 6 (fail), 0 -> 4 (pass). Gate2 11-07 data: BP 2 -> 1, IP 15 -> 15, both PASS.
- **IP ~1.3 s RouteBackoffHook.AsyncWait hitch:** the only >= 1 s iterations of the 12 IP arms, in both arms (off 1254 / 1262 ms, on 1319 ms, seed 12345). In all three the Sim's task runs from `CarryUtils.Request` (state machine wait) into our `RouteBackoffHook.AsyncWait < PlanBackoffBody < UfPlan` [verified, `hitches.jsonl` tasks]. Ours, not `autunseen`; name the route job it waits on and bound the wait.
- **Load-time `ufLong` warm-up burst:** unseen route plans and worker jobs >= 300 ms bunch up early in the run, in both arms [inferred, 11-08 analysis; not re-checked]. Find whether a cold cache or the first cuts make them, then cut or defer that work.
- **StuckWatchdog and sync replan hitches:** watchdog acts (`watchdog_idle_stationary`, home resets; similar counts in both arms) and sync `Route_ReplanFromPosition` replans (11 of 18 on-arm >= 300 ms iterations in `credit3 ip r4-offfirst`, mostly `GetInBoatRouteAction` fades; the same kind in the off arm) cost 300-400 ms each on the sim thread [measured]. Candidates: `watchdoglockskip` / the async A1 idle check (docs/250 §7.4) and the boat replan work (docs/247 §11).
