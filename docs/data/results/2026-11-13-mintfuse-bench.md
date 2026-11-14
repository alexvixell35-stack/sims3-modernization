# [MintFuse] Sub host bench 2026-11-13 (docs/139 §4.7)

Host only (monohost, `native/monohost`), no game run. Branch `feature/jitbench-mintfuse` (6c6bdf2b, ts3native 0.36.254). Tags: **measured offline** (monohost), **verified** (dump / disassembly), **inferred**.

## Setup

- `HostTest.JitBench` cases 0-35 (35 = docs/139 MintBench mix, per 1/1024 round), one process per arm per round, ABBA, `MONOHOST_JIT=on`, `MONOHOST_MINTCALL=off`, arm `mf` = `MONOHOST_MINTFUSE=sub`, pinned core 3, 9 reps per case and column, round 1 dropped, per-run `MONOHOST_LOG`.
- Paired metric: per round, mf median / base median. Per call subtracts the **base** process's empty loop (case 20) from both arms. The old analysis took each arm's own loop off, which is biased for MintFuse: it speeds up the empty loop itself (below).
- Machine not quiet: another agent's monohost bench on core 1, a game queue on the machine earlier. Per-round spread is about ±25-30%, so only medians over many rounds count.
- Script: `probe-out/jit-bench/mfpaired2.ts` (local). Data: `probe-out/jit-bench/mintfuse-dp-all.json` (12 rounds, all cases), `mintfuse-dp-focus.json` (16 rounds, the cases that were positive in any run).

## Case 10 (`ObjectGuid !=`, +6.4% in the first bench): bench artefact, not a slower superop

- Mint dumps of `HostTest.JitBench` (`MONOHOST_JIT=dump`) [verified]: `ObjectGuid.op_Inequality` and `..ctor` are byte-identical base vs sub. Case 10's loop differs in one place: `brfalse.i4.s; ldloc.i4` became `0x1AE`. Its `r++` was already EA's whole-run `0x1B2` in base (a branch target sits in front of it).
- `0x1AE` handler (0xE58AE7) is the two base handlers merged, nothing slower [verified, capstone].
- Case 20 (the empty loop, `acc += r; r++`) is one run: base 9 dispatches per iteration, sub 7 (`r++` gets `0x1B2` inside the run). Measured: empty loop -20% (interp) / -19% (compiled). Every case had that loop subtracted per arm, so a case whose own loop gained nothing looked slower. The raw per-iteration time of case 10 was never slower.
- On the way: greedy left to right also lost fusions. Case 10 under greedy: `0x1AE, add1, stloc` (3 dispatches); fewest-dispatch placement: `brfalse, 0x1B2` (2). The matcher now picks the fewest dispatches (docs/139 §4.2).

## Results (fewest-dispatch matcher, paired median of per-round ratios, raw per-iteration)

| case | name | interp all (12 r) | compiled all | interp focus (16 r) | compiled focus |
|---|---|---|---|---|---|
| 3 | List foreach | -12.2% | +5.1% | -10.8% | -0.6% |
| 7 | GetType() == typeof | +3.4% | +3.9% | -0.3% | +4.7% |
| 8 | Array.Resize<int> | -2.7% | +4.3% | +1.5% | +2.4% |
| 9 | Array.IndexOf<int> | +5.2% | +4.1% | +0.5% | +1.6% |
| 10 | ObjectGuid != | -6.0% | +2.0% | -0.8% | +1.7% |
| 11 | CASUtils flags | +0.4% | +3.3% | -1.2% | -2.4% |
| 18 | JitSum 64 | -8.4% | +9.1% (0.1 ns) | -10.5% | +4.4% (0.25 ns) |
| 19 | JitSumPlain 64 | -5.6% | -9.7% | -9.8% | -10.3% |
| 20 | empty loop | -24.4% | -17.6% | -20.1% | -18.8% |
| 33 | Array.Copy 5-arg object[] | +0.9% | +6.0% | -3.0% | -1.9% |
| 34 | Array.Copy 3-arg | -2.7% | +4.8% | -4.8% | -1.5% |
| 35 | MintBench mix | -5.2% | -8.4% | -6.8% | -3.9% |

Other cases in the 12-round run: interp -0.2% to -14.8% except 21 (+2.9%), compiled -0.2% to -13.7%; per-round ranges about ±30%. No case is slower in both runs beyond that noise; every case that was positive in the all-case run is within ±5% in the focused run. Per-call (base-loop subtracted) figures are in the JSON and agree in sign.

Checksums: every warm-case checksum equal in all 48 + 32 processes, both arms; 0 `CHECKSUM DIFFERS` lines (in-process interpreter vs compiled value per case). Host selftest exit 0 with `MONOHOST_MINTFUSE=sub`.

## Transform cost (MF_TICKS, same shim and QPC timer for both)

`MONOHOST_MINTFUSE=eatime` runs EA's matcher behind our shim; 6 alternating runs each, m13 phase (10 assemblies):

| matcher | ns per run | runs | whole / inner fusions |
|---|---|---|---|
| EA 0xD89930 | 78-92 (~85) | 20459 | (EA's own) |
| ours, fewest dispatches | 393-432 (~410) | 20440 | 168 / 1643 |

Net about +0.33 us per run [measured offline]. The game walked ~106k runs by the end of a BP load and ~234k after 10 min (2026-11-08 verify): about +35 ms at load, +75 ms per session, once per method transform [inferred]. The greedy matcher cost about the same (421-469 ns).

## Game gain [inferred]

Bench mix -4% to -7% of interpreter time. docs/139 §4.5 model (interpreter self 25.5% of sim-thread samples, 2-6% fewer dispatches in the game's mix): **~2-8 ms/s** [inferred, unchanged]; part of it overlaps compiled bodies when `[MonoJit]` is on.

## Decision

Gain, no penalty beyond noise, guards hold (checksums, selftests, two greedy in-game verify runs PASS 2026-11-08/10): `[MintFuse] Sub=1` in the repo ini. Correctness queue for this build: `tools/harness/queues/mintfuse-sub-on-1113.json` (prepared, not run).
