# EP/SP census: handoff for Dalexlol (2026-11-15)

Rodney is handing the EP/SP census and sweep track to Dalexlol. Read this first, then docs/267 (what our data never covered) and docs/268 (the forced interaction sweep). Tags follow AGENTS.md: [verified] = read in code or counted in data, [inferred] = still to confirm. Times are wall clock. A live job takes about 8-15 min.

## 1. Why

All our runtime data (managed samples, JIT game list, AutShadow rows, autonomy rows) comes from three home worlds: Bridgeport, Sunset Valley and Isla Paradiso [verified, docs/267 §1].
- No profile has ever run in Champs Les Sims, Shang Simla, Al Simhara, Sims University or Oasis Landing.
- 1,239 of 1,615 EP script types (77%) never appear in our managed data.
- The JIT compiles only methods on its game list (`JitListed`), so destination-world methods stay interpreted. That means no gain there, but no JIT risk either.
- Unknown: whether our routing, unseen-Sim and Mono features behave in tombs, on hover trains, on horses, in hidden rooms, and so on.

The census answers three questions:
1. Do our fixes break anything in pack content?
2. Which pack code is hot enough to be worth Mono or routing work?
3. Which EA bugs turn up only in packs?

## 2. What exists (all on master)

| Piece | Where | State |
|---|---|---|
| Per-pack static table (1,957 types) | `docs/data/ep-pack-types.csv`, docs/267 §2 | done |
| Routing vs pack paths (tombs, hover, tube, bikes, brooms, horses, mermaids...) | docs/267 §3 | static only |
| Destination-world repros | `ep-wa-tomb`, `ep-uni-campus`, `ep-future-oasis` in `tools/harness/repros.json` | first run 11-15 (below) |
| Home-world pack repros | `ep-home-bp`, `ep-home-sv`, `ep-home-ip` | first run 11-15 (below) |
| Correctness queues | `tools/harness/queues/ep-worlds-correctness-1115.json` (5 jobs, ~1 h 15 min), `ep-home-packs-1115.json` (3 jobs, ~40 min) | run once, rerun tonight |
| Timing queue | `ep-worlds-profile-1115.json`: per world, an OFF arm (`ourMods: false`) and an ON arm (release bundle), ~2 h 30 min | not run (needs a quiet machine) |
| Forced interaction sweep | ts3agent `interaction_sweep` (`mods/ts3agent/src/plugin/Handlers/InteractionSweepHandlers.cs`), docs/268 | built, not run |
| Sweep repros and report | `tools/harness/interaction-sweep-report.ts` (`--gen-repros` makes `sweep-ep1`..`sweep-sp9`; without it, the per-pack report) | built |
| Sweep queues | `sweep-packs-home-1115.json` (one job per EP/SP in a home world, SP1 smoke first, ~12 h), `sweep-packs-dest-1115.json` (France, China, Egypt, University, Oasis Landing, ~6 h) | not run |
| Agent steps the repros use | `spawn_horse`, `buy_object {type}` + `must_place`, `@type:<class>`, `@picked:N`, `@far`, `def:<class part>`, `travel_vacation` / `travel_university` / `travel_future` with `--stay`, `test_env {season, moon_phase}` | built |

## 3. Results so far (11-15, build a96b4916)

- **ep-worlds-correctness: 5/5 PASS** (France, China, Egypt after one retry, University, Future).
  - Egypt's first attempt hung at load on the EA package-lock deadlock (docs/539 §13.8). The harness retried it on its own, and that is an EA bug, not ours.
  - These first passes were partly hollow: the 2-day trip ended mid-repro, the tomb traps were dormant, and some buys landed in inventory. All of that is fixed on master (5-day trips with an `on_vacation` gate, traps armed through `TombTrap.EnterState`, `must_place` with lot corners, steps that fail the job when they don't run). The rerun is in tonight's chain.
- **ep-home-packs: BP PASS, SV PASS, IP FAIL** (`spawn_horse failed: no BoxStall in this world`). All eight buys had gone to the family inventory. The repro now buys and places its own stall on the far lot (`must_place`); the IP rerun is in tonight's chain.
- **First, before the sweeps:** Rodney wants your snapshot/rewind design (rewind notes, 2026-11-14) built first so these long queues run faster and steadier. We start on it now; the sweeps can wait for it or run without it.
- Tonight's rerun results will be in `docs/data/results/2026-11-15-queue-ep-worlds-correctness-1115*.md` and `...-ep-home-packs-1115*.md` after the next public sync.

## 4. Tasks for you (pick what fits your setup)

1. **Run the sweeps** on your install, since you have every pack. The order:
   1. `sweep-packs-home-1115`. SP1 is the smoke job and stops the queue if it fails.
   2. `sweep-packs-dest-1115`.

   Before running:
   - Build ts3agent from master into `build/`, because the queues take the agent from there.
   - Use throwaway save copies. The sweep skips destructive definitions by name prefix (`kIswDestructive`, docs/268 §5). Never use `destructive: true` on a save you care about.

   Report each pack with `node tools/harness/interaction-sweep-report.ts <tag> <job ts3native.log> --out docs/data/results/<date>-sweep-<pack>.md`.
2. **Write the prep pass** (docs/268 §4, design rows) for the fail reasons the first report lists, then rerun only the failed rows:
   - skill: `set_skill`;
   - career: join it;
   - occult;
   - funds;
   - relationship gates: `modify_relationship`;
   - situations: start each through EA's own entry point.
3. **Triage what the sweep finds.**
   - A stack with our frame (`Ts3Fixes.` / `Ts3Agent.`) is our bug and a FAIL. Send a fix PR.
   - An all-EA stack is listed as a finding (`scriptErrorsReport`, `EA_ONLY_RE`), not a FAIL. Add a fix behind a switch that defaults to off, with a household / selected-Sim / unknown-scope exemption, as AGENTS.md requires.
4. **Static fix candidates from docs/267 §6, if you want them:**
   - **F1:** `UfTeleport` goal check. Refuse a teleport goal inside a tomb or hidden room (`TombRoomManager.IsPointInATombOrHiddenRoom`).
   - **F2:** `TownieUnseen` ignores Late Night hidden rooms. It needs its own switch and a grep of every caller.

   Both need a counter slot (reserve it via `tools/registry.ts`) and a repro.
5. **Optional, needs a quiet machine:** `ep-worlds-profile-1115` (ON vs OFF per destination world). It also feeds the JIT game list with destination-world methods.
   - Check `jitCodeKB` / `jitMethods` / refusals in the first destination-world job. The 2 MB code region fills in every run, so a destination world may refuse different bodies [inferred, docs/267 §1].

## 5. Rules that apply

- **Household Sims stay untouched.** Any change that touches Sims needs household, selected-Sim and unknown-scope exemptions in code. The census jobs check that `kdScope` household and selected counts are 0.
- Repros teleport the Sims and force the actions. They never wait for events to happen naturally.
- Correctness before timing. `queue.ts` runs its smoke job first.
- New switches default OFF.
- Never commit EA files, decompiled code, saves or logs with personal paths.

## 6. Known limits

- **Not covered yet:** festivals, orientation / mascot / foreign-visitor situations, and definitions that only exist in states the sweep never creates (in a boat, on a horse, underwater) (docs/268 §7).
- **Script errors are attributed by time window** [inferred]. A natural NPC error during an open row counts for that row. The ScriptError xml in the user folder is the ground truth.
- **Catalogue scan.** A pack class with no in-world instance is found by creating catalogue products, which adds tens of seconds per world at setup [inferred].

## Status log

Append one line per piece: date, piece, branch/PR, what passed, what is open.

- 2026-11-15: handoff written; ep-worlds 5/5 and ep-home 2/3 first runs (hollow parts fixed), reruns in chain; sweeps unrun.
