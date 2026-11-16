# JIT lazy-site guards on vs compiled out (host bench, 2026-11-14, measurement only)

Branch `feature/monojit-paired-timing` (commit base 1be42a53 + uncommitted NoGuard switch), `node tools/jit-piece-bench.ts --cases
14,21,23,25,27,29,31 --arm base: --arm ng:MONOHOST_JITNOGUARD=0x79F --arm ngsem:MONOHOST_JITNOGUARD=0x9F --common
MONOHOST_JITLAZY=1,MONOHOST_JITDIRECTLEAF=0,MONOHOST_JITBIND=1,MONOHOST_JITBOUNDCALL=3,MONOHOST_JITINLINE=2,MONOHOST_JITWIDE=1
--rounds 2 --reps 7 --core 2`. NoGuard applies only to call sites inside `MonoHostTest.HostTest.Jit*` methods (the bench driver
and the bench methods); the selftest / corpus hang without the guards.

- `ngsem` = 0x9F: null receiver (0), null `*this` (1), null vtable (2), remoting proxy (3), boxed value type (4), ForceJIT (7).
- `ng` = 0x79F: `ngsem` + depth slot (8), no MonoInvocation in the slot (9), no arena chunk (10).
- Always kept: class guard (5), no lazy body (6), arena full (11).

**Noisy machine:** a game test queue ran on cores 4-15 (core 2 pinned); the interp column (which no arm changes) moved up to 25%
between arms, so the table also gives compiled / interp in the same process (ratio), which cancels most of that drift.

| case | arm | interp ns | compiled ns | compiled / interp |
|---|---|---|---|---|
| 14 JitId (driver's call site only) | base | 22.3 | 22.0 | 0.987 |
| | ngsem | 16.8 | 16.7 | 0.994 |
| 21 JitInl 64 | base | 106.6 | 100.7 | 0.945 |
| | ngsem | 97.7 | 91.3 | 0.934 |
| 23 JitLz 64 (Direct) | base | 84.3 | 25.3 | 0.300 |
| | ngsem | 65.6 | 19.9 | 0.303 |
| 25 JitLzChain 64 | base | 110.6 | 35.3 | 0.319 |
| | ngsem | 85.9 | 27.3 | 0.318 |
| | ng | 95.1 | broken | checksum differs |
| 27 JitLzVirt 64 (Guard) | base | 43.5 | 16.1 | 0.370 |
| | ngsem | 35.9 | 13.4 | 0.373 |
| 29 JitLzAlloc 64 | base | 121.6 | 108.0 | 0.888 |
| | ngsem | 96.1 | 87.7 | 0.913 |
| 31 string comparer (BoundCall; not HostTest: control) | base | 134.6 | 111.9 | 0.831 |
| | ngsem | 130.2 | 100.6 | 0.773 |

Checksums: `ngsem` equal to base in every case. `ng` gives a wrong checksum from case 25 on (the warm-up of the chain / virt /
alloc cases breaks without the depth / slot / chunk checks), so 8-10 are correctness guards, not overhead.

Verdict [measured, noisy]: removing the null / vtable / remoting / boxed / ForceJIT checks moves compiled / interp by -0.011 to
+0.025 (no consistent sign; the control case moves -0.058), i.e. no gain above the noise. Those checks are ~6 not-taken
compare + branch pairs per lazy call (macro-fused, ~2-3 cycles) against 25-110 ns per bench call. The guards are not where the
JIT's time goes; the remaining compiled cost is the frame block, arena bump and the native body. A quiet-machine ABBA (more
rounds) would give the per-sequence ns for `jit-time.ts --guard-ns`; until then use 1 ns as an upper estimate.
