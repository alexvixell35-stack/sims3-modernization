# 244. Fixed-work bench (X4)

Mono audit row X4 (`probe-out/mono-task-audit-2026-11-06.md` section 1.5). Status: built 2026-11-06, not run in game.

## Why

The Threaded stage is ~590 ms/s in every run: the sim runs flat out, so a Mono saving shows up as ms per iteration, not as ms/s.
5 ms/s is ~0.7% of it/s while it/s swings +-15-20% (`throttles-judged-by-ms.md`). Stage A/Bs of practical length cannot resolve it.
X4 times one fixed managed workload per slice and arm, so the arm effect on managed code is read without the world's noise.

## What

- Agent command `fixed_work {ms?}` (`mods/ts3agent/src/plugin/Handlers/FixedWorkHandlers.cs`), runs on the sim thread (the Mono VM the script runs in).
  Loop = `HostTest.MintBench` shape (monohost, docs/139): List / Dictionary generics, interface enumerator, monomorphic and two-type callvirt, boxed `Enum.Equals`, `GetType`.
  Pure: own objects only, no game state, no `System.Random`, no tick or RNG calls; data built once, per-round garbage = one boxed enumerator and the boxed enum compare.
- Clock: the game's Mono has no `Stopwatch` and no `DllImport`, so `DateTime.UtcNow`. A call starts and ends on a clock edge and reports the step (`resUs`); a step over 1 ms voids the run. Result: `usPer100` (us per 100 rounds), `sum` (checksum of one block, constant), `rounds`, `warm` (first call since load: dropped), `totalUs` (cost to the sim thread).
- Harness: `profile-world.ts --fixedwork <calls per slice>` (queue `run.profile.fixedwork`: number or true = 8; needs `--ab`). Each slice sends the call evenly spread, appends to `<tag>-ab-fixedwork.jsonl` (slice, arm, time) and prints the report line at the end. Two failed calls in a row = FAIL (agent package without the command).
- `--ab fwnull`: pseudo switch that flips nothing, so every slice runs the same code. It gives the same-arm noise check (smoke) and per-slice times for a cross-run ABBA (F1: the jobs differ by ini, not by `--ab`).
- Report: `node tools/harness/fixedwork-report.ts <tag>-ab-fixedwork.jsonl [--tol 1] [--secs <run s>]`. One line: off and on median (us/100 rounds), same-arm slice spread (noise floor), on vs off %, call count, tool cost as % of run time, clock step; `PROBLEMS:` when a checksum differs, the step is over 1 ms, an arm has under 2 slices, or the same-arm spread is over `--tol`. `queue.ts` smokeProblems turns a `PROBLEMS` line into a smoke FAIL.

## Cost (rule: measure each tool's cost)

Default 20 ms per call, 8 calls per 2-min slice: ~160 ms per 120 s = ~0.13% of sim time [inferred]. The printed `tool cost` line is the measured figure; read it from the first smoke. Off by default; only queue jobs that name `fixedwork` use it. A job with it is not comparable in it/s to one without (the sim thread is blocked ~20 ms 4 times a minute): compare like with like.

## Queues (not run)

- `tools/harness/queues/x4-smoke.json`: one 10-min BP job, `--ab fwnull --fixedwork 8`, slices off,on,on,off after warm-up. Pass: the two same-arm slices agree within ~1%, else X4 is dropped and F1 falls back to Threaded ms/iter.
- `tools/harness/queues/f1-callpath.json`: F1, build of `v025-e-ab`, ini `[MonoFast] CallPath` 0,1,1,0 on BP then IP (`--ab fwnull --fixedwork 8`), plus `bp-f1-mintcall-ab` (`--ab mintcall`, ini CallPath=1 so the hook installs). queue.ts runs the measurement smoke first (declared: BP, `--ab mintcall`, fixed-work on).

## Unverified

No game run: the `fixed_work` timings, the real per-call cost, whether `DateTime.UtcNow` is fine-grained in the game's Mono, and the 1% agreement are all unmeasured. The agent package must be rebuilt (`node tools/harness/build-agent.ts`) and installed by whoever owns the game before the queues can run.
