# Handoff: MonoJit paired timing (piece A2, stage 1 of 2)

Branch `feature/monojit-paired-timing` (from `feature/jit-bundle-1113` 1be42a53). Local only: not merged, not pushed. Built + host gates, NOT RUN in game.
Stage 2 = the game queue below + the report.

## Why

Pieces are too small to time per game run, and two OFF game runs share only 35-60% of Sim actions. Verify mode already runs
every sampled call twice from the same state (compiled, then EA's interpreter). Timing both passes gives compiled vs
interpreted for the same call, same arguments, same session.

## Design (as built)

- **`[MonoJit] Time=1`** (default 0, needs `Mode=2`; host: `MONOHOST_JITTIME=1` with `MONOHOST_JIT=verify`). Off = no rows,
  one pointer test in `JitVerifySlow` (Mode=1 never enters the verifier). Code: `native/ts3native/monojit-time.h`, shared by
  `monojit.cpp` and `native/monohost/monohost-jit.cpp`. No new counters in the counter block, no table sizing touched.
- **Windows** (rdtsc, TSC rate from QPC over the session): pass 1 = end of the fresh-call snapshot -> the compiled ret's
  verifier entry; pass 2 = after the restore -> the interpreted ret's verifier entry. `JitVerifySlow` became a wrapper around
  the old body (`JitVerifyStep`) that sums the verifier's own cycles; cycles inside the verifier during a window are subtracted.
  Retry (first compare differs) re-times pass 1; only verified-equal pairs are added; a window > 1e8 cycles is dropped.
  Sim / main thread only (the first host run without that guard raced with a worker: 37 k wrapped windows; 0 since).
- **Not subtractable in code: the stub + `JitEnter` probe per verifier entry.** Every position of a compiled method is a
  patched word, so pass 2 enters the verifier once per interpreted instruction (JitSum 64: 843 entries per call). Rows carry
  the entry counts (`ent1`, `ent2`); the reader subtracts a per-entry cost. Measured 49 cycles/entry on a quiet host and 62
  with a game test running (JitSum 64 vs `jit-piece-bench` case 18/19 interp 16.2 ns x 64), so the reader's default is `auto`:
  the block's own minimum interp cycles per entry over loop rows (>= 100 entries per pair). That slightly over-corrects
  (lower bound on the gain); the uncorrected total is printed as the upper bound.
- **Order bias:** pass 2 always runs second (warm caches, objects just touched) = understated gain. The verify design cannot
  swap the order (pass 2 is the compare reference; the retry and `JitKeepPass` assume compiled-first), so it is not alternated.
  Not measured in game; in the host both passes are warm anyway. Bound it in stage 2 by comparing the host rows of the bench
  methods (JitSum, JitLz, ...) with `jit-piece-bench` (separate warm runs): host 2026-11-14 JitSum 997 ns interp / 349 ns
  compiled vs bench 1037 / 301 ns.
- **Nesting:** pass 1 counts compiled methods entered inside it (`nest`). A row with `nest > 0` includes its callees' gain;
  the reader sums those rows apart ("overlap"), leaf rows are the non-overlapping total. Lazy-body callees never get their own
  pairs (no `JitEnter`), their gain is inside the caller's row.
- **Piece tags** at compile time: the compile counters that moved during the method's `JitCompile` (Direct = lazy sites,
  Guard = guarded lazy sites, Inline, Wide, RetTd, FastCopy), plus Clause (`rm[0x41]`) and LazyBody. Host has no Wide counter
  (not tagged there). VtOps / NewVt / Ldsflda / CallExit have no compile counter: not tagged.
- **Per-piece ms/s:** reader rows per exact tag set (combo) and per tag with an even split (method ms/s / its tag count).
  ms/s = calls/s x (interp - jit) ns. calls = `[MonoJit] Count=1` call counts when on, else the fresh calls the verifier saw.
- **Not timed:** E1 TypeHandleDirect, CallPath and MintFuse act in the interpreter, so both passes have them (the gain is "JIT
  vs today's interpreter with those on"). CallPath handlers are naked asm that tail-dispatch the next instruction (no exit point
  to time, ~50-190 instructions where rdtsc noise dominates); MintFuse Sub rewrites mint code at transform time (no runtime
  dispatch). Their in-session measure stays the existing in-load A/B (`--ab mintcall`) and the host benches (CallPath bench,
  MintBench case 52). Clause methods are never verified (`verifyOk` false), so ClauseLazy only shows inside callers' rows.

## Log format (`ts3native.log`, host `monohost.exe.log`)

```
OK   mono jit time (docs/handoff/jit-paired-timing.md): Time=1, VerifyEvery=16, Count=1, 4096 rows, report every 10 min, after saves and at exit
jittime: begin <interval|save|exit|end> #<n>, <ms> ms, tsc <kHz> kHz, <pairs> samples, <d> dropped (<w> wrapped), <entries> entries, <rows> rows, <methods> methods
jittime: m <idx> <tags hex> <calls> <pairs> <jitCycles> <interpCycles> <nest> <ent1> <ent2> <dropped> <guards> <name>
jittime: end <why> #<n>, <rows> rows
```
Blocks are cumulative; the reader takes the last complete one (a crash-cut block has no end line). `wrapped` > 0 = timing bug.

## Reader

`node tools/jit-time.ts <log> [--stub-cycles auto|<n>] [--top 20] [--block <n>] [--csv docs/data/jit-time-<world>] [--guard-ns <ns>]`
prints totals (leaf / overlap / uncorrected), per-piece combo + split, top methods (calls/s, pairs, jit ns, interp ns,
saved ns, ms/s, nest per pair, guards/s, tags); `--csv` writes `<prefix>-methods.csv` and `<prefix>-pieces.csv` for the
benches. `--self-check` uses real host lines (incl. a guards-only row and a crash-cut block).

## Guards (Rodney: "do the guards contribute to the slowdown? what if we drop them?")

Answer [measured on the host, noisy]: **no measurable share.** Compiling out the removable lazy-site guards gave no gain above
noise; the ones that look removable but are not (depth slot, slot MonoInvocation, arena chunk) broke results. Details:
`docs/data/results/2026-11-14-jit-noguard-bench.md`. No guard removal ships; `MONOHOST_JITNOGUARD` is host-only measurement.

### Runtime guards in Mode=1 (line numbers: `native/ts3native/monojit.cpp` on this branch)

| Where | Check | Runs | Removable? |
|---|---|---|---|
| `JitEnter` 1828-1856 | ip-table hash probe | every entry from the interpreter (calls, returns into a method) | no (it is the lookup) |
| `JitEnter` 1844 | sim-thread check (`fs:[24h]` vs `g_jitSimTid`) | every entry | safety: compiled code is single-threaded |
| `JitEnter` 1846-1849 | `inc jitCalls`; `g_jcOn` (Capture), `g_jitVerifyOn` tests | every entry | counter + two predictable tests, ~1-2 cycles; not in the host's JitEnter |
| `EmitLazyCall` 847-875 | null this / null `*this` / null vtable / remoting proxy / boxed value type (why 0-4) | every lazy call | semantic (EA throws or takes another path); measured removable = no gain |
| `EmitLazyCall` 852, 856 | class guard on EA's inline cache (why 5); `JitGuardFill` call when the cache target changes | every guarded (virtual) lazy call; fill only on change | no: correctness |
| `EmitLazyCall` 861, 864 | lazy body present (why 6) | every lazy call | no: the call target |
| `EmitLazyCall` 866 | MonoPatcher ForceJIT byte (why 7) | every lazy call | only without MonoPatcher; no gain measured |
| `EmitLazyCall` 885-891 | depth slot, slot MonoInvocation, arena chunk, arena full (why 8-11) | every lazy call | no: 8-10 off broke the chain / virt / alloc bench results; 11 = memory safety |
| `EmitLazyCall` 902 | `inc jitLazyCalls` | every lazy call | counter |
| `EmitLazyCall` 928 | reader-trap window | only `Trap=1` | off by default |
| `monojit-bound.h` 98-105 | BoundCall: bound body present, `g_jitBc->on` kill switch, ForceJIT, exception after the bound call | guarded sites with no lazy body | kill switch + exception = correctness |
| `JitTryInline` 1055-1099 | null receiver, ForceJIT, remoting, arena chunk / end, nulls in inlined field chains | every inlined site | semantic (EA's exception path) |
| `EmitExit` 589-590 | `inc jitExits` (+ count-mode site counter only with `Count=1`) | every exit to the interpreter | counter |
| `EmitExit` 598 | RetTeardown dead flag | compile time only (`g_jitRtDead`) | no runtime cost |
| post-pass 1259 | ClauseLazy allow list + kill switch | compile time; Mode=2 adds `clVerRuns` (1306) | no Mode=1 runtime cost |
| templates 724-733, 1367-1663 (35 sites) | null / bounds / class-init checks in field, array, string, static-field templates | per op | semantic: they reproduce EA's exceptions / class init; a hit exits to EA's handler |

### Guard executions per method

`[MonoJit] Time=1` also emits one `inc` per lazy call site run (`EmitLazyCall`, before its guards) into the method's row: the
`guards` column (every call, not sampled; compiled code only, so it adds one instruction to the code being timed). The reader
prints guards/s per method and in total, and `--guard-ns <ns>` turns that into ms/s. Host corpus: 197 871 guard sequences/s.

### With vs without (host bench, ns per call)

See the results file for the table. Per piece, compiled / interp in the same process, base vs `ngsem` (null / vtable / remoting
/ boxed / ForceJIT off): Direct (case 23) 0.300 vs 0.303, chain (25) 0.319 vs 0.318, Guard (27) 0.370 vs 0.373, Inline (21)
0.945 vs 0.934, LzAlloc (29) 0.888 vs 0.913. Guard cost as a share of the JIT gain: below the noise (~±3% of compiled time on
this busy machine); by instruction count ~2-3 cycles per lazy call out of 25-110 ns. Rerun as a quiet-machine ABBA for exact ns.

## Build

**NOT PARKED YET.** The final asi build waits for a build window: `boundcall-abba-1113` (timing, 8 jobs) started 22:58 UTC
right after the last source change, so the build rule blocked it. In the next window (no `*-ab*` / `*abba*` queue running):

```
native\ts3native\build.cmd out-jt          (selftest must print "monojit: ok" and "selftest ok")
copy out-jt\ts3native.asi  <main>\game-copy\ts3native.asi.jittime-1114
copy out-jt\ts3native.ini  <main>\game-copy\ts3native.ini.jittime-1114
copy out-jt\ts3native.map  <main>\game-copy\ts3native.map.jittime-1114
sha256 all three -> this table + docs/data/builds.json
```
then the gates still open below (replays, selftest-all). An earlier build of this branch (before the guard counter and the
include move) passed the native selftest with the new timing asserts; it is not the one to park.

0.36.258 = 0.36.256 (jit-bundle-1113) + this; 0.36.257 is left for the table-grow branch.

## Gates (host / static, 2026-11-14, no game run)

- Native build + selftest (`native/ts3native/build.cmd out-jt`, 0.36.258, before the last two edits): `monojit: ok`,
  `selftest ok`; the Mode=2 test now asserts one timed pair, no wrap, no pair from a mismatch.
- Host corpus, bundle ON-arm env + `MONOHOST_JIT=verify` (final host code): `MONOHOST_JITTIME=1` and `=0` both: 272 cases,
  1 differ (case 110, the known stack-trace text), 0 unstable, per-call verify MISMATCH 0, `SELFTEST OK: 40 passed, 0 failed`
  (the bundle handoff says 41; 40 with and without Time, so not from this change: check on the bundle build).
  Time=1: 2.35 M timed pairs, 0 dropped, 0 wrapped, 917 rows.
- Sanity (jit < interp on known-gain cases), host rows vs `jit-piece-bench`: JitSum 64 interp 997 ns / compiled 349 ns vs bench
  1037 / 301 ns; Math.Min 175 / 23 ns; JitK.DivUnI4 / MulI4 jit < interp. `node tools/jit-time.ts --self-check`: OK.
- NoGuard bench: see Guards.
- **Open (next build window):** replays BP / IP / SV c3g with `MONOHOST_JITTIME=1` (`node tools/jit-replay.ts --run
  <main>/probe-out/jit-capture-<w>-c3g.txt --lazy --switches Wide=1,Inline=2,ClauseLazy=1,DirectLeaf=0,Bind=1,BoundCall=3,RetTeardown=0,VtOps=0,CallExit=0,NewVt=0,FastCopy=0,Ldsflda=0`,
  env `MONOHOST_MINTFUSE=sub`); `node tools/selftest-all.ts`; the final native build above. Not run now: monohost / selftest-all
  load the CPU and a timing queue is running.
- Note: a monohost run killed by `spawnSync`'s timeout leaves its child `monohost.exe` running; kill leftovers by path.

## Stage 2 queue spec (not written: the queue agent owns it)

- One verify session per world, BP / IP / SV, ~15 min each, correctness-class (not a timing queue: Mode=2 doubles the
  compiled work; do not compare its ms/s with anything).
- asi `ts3native.asi.jittime-1114` (or the merged build, below), ini = the bundle ON arm (docs/handoff/jit-bundle-1113.md) with
  `[MonoJit] Mode=2 VerifyEvery=16 Time=1 Count=1` (+ `Wide=1`, which the shipped ini lacks). Light crash forensics as usual.
- `nativeRequire` / `logRequire`: `^OK   mono jit time .*Time=1`, `^jittime: end (save|interval|exit) #\d+`, forbid
  `^jittime: begin .*\((?!0 wrapped)\d+ wrapped\)` and `^jit verify MISMATCH` (one MISMATCH line = a compiler bug: stop, triage).
  Test those regexes against the lines above before use.
- Save once at ~12 min (a `save` block), then exit (an `exit` block). Read: `node tools/jit-time.ts <job>/ts3native.log --csv
  docs/data/jit-time-1114-<world>`, then `--stub-cycles 0` for the upper bound.
- **With the table-grow asi:** merge `feature/monojit-table-grow` into this branch (or this into it) and rebuild; this branch
  touches `JitVerifySlow` (now a wrapper + `JitVerifyStep`), the `JitCompile` call in the post-pass, `EmitLazyCall`'s first
  lines, `JitTick`, `JcnExit`, one ini key before `VerifyEvery`, and a separate OK line; no table sizing. Rows are sized by
  `g_jitMethCap` at init; `JT_CALLS` reads `g_jcn->calls[i]`, sized `JIT_METH_MAX`: if table-grow raises the cap past
  `JIT_METH_MAX`, cap `JT_CALLS` there too. Version: take the next free number after both.

## Open questions

1. Stub cost in game: `auto` assumes the game's loop rows sit near the host's floor; check the auto value against 49-62.
2. Order bias: not separable in game; only bounded by the host-vs-bench comparison.
3. CallPath / MintFuse: no in-session per-call timing (above); is the existing `--ab mintcall` in-load A/B enough?
4. Overlap rows: an exact exclusive split needs per-callee time inside pass 1 (time the nested entries' windows); not built.

## Status 2026-11-14 07:10 PHT (branch feature/monojit-time-table, worktree wt-timetable)

- Merged `feature/monojit-paired-timing` + `feature/monojit-table-grow` (commit 8fc089f6): conflicts only in `docs/index.md` (both lines kept) and `TS3NATIVE_VERSION` (0.36.259). `CodeKB` default 16384 -> 12288 (code + ini): table-grow demand ~5.5k bodies x ~1.7 KB = ~9.4 MB (Mode=1); Mode=2 ~2.1 KB a body = ~11.5 MB, under 12288 but tight: a `jitFullN > 0` verify run is a finding, rerun with CodeKB=16384. Address space with Enabled=1: 18852 KB.
- Queue `tools/harness/queues/jit-time-1114.json` written and dry-run OK (3 jobs bp/ip/sv-jt-time, 15 min, regexes tested at parse).
- NOT built yet: `boundcall-abba-1113` (timing) was still running at 07:04 (7 jobs left, until ~08:45). Open: asi build `game-copy/ts3native.asi.jittimetable-1114`, replays BP/IP/SV with `MONOHOST_JITTIME=1`, selftest-all, native selftest, builds.json, and the same gates on the paired-timing-only asi (`jittime-1114`) if wanted. Build only in the 08:50-10:50 correctness window.
