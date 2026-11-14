# Chains (`[MonoJit] DirectLeaf=0`) host bench, 2026-11-13

Host only, no game run. Branch `feature/monojit-chains-on`, bench commit `dd837fa9`, core 0 (the game ran on 0xFFF0), 4 rounds x 9 reps A B B A, `MONOHOST_JITLAZY=1` in both arms, base `MONOHOST_JITDIRECTLEAF=1`, piece `MONOHOST_JITDIRECTLEAF=0`, cases 0-13, 25, 27. Command: `node tools/jit-piece-bench.ts --cases 0,1,...,13,25,27 --arm base:MONOHOST_JITDIRECTLEAF=1 --arm chains:MONOHOST_JITDIRECTLEAF=0 --common MONOHOST_JITLAZY=1 --rounds 4 --reps 9 --core 0`.

## What the piece is [verified: code]

`DirectLeaf` (ini key, default 1) sets `g_jitLeafOnly` (`native/ts3native/monojit.cpp:112`, read at `:2742`; host `MONOHOST_JITDIRECTLEAF=1` at `monohost-jit.cpp:2746`). With `Direct=1`:
- `DirectLeaf=1`: only leaf methods get a lazy body; a call site in a body drops it (`:1248`, `:1255`, `:1671`), and a guarded callvirt site is refused (`JitLazySite` `:797`).
- `DirectLeaf=0` ("chains"): a lazy body may itself make lazy calls (`:1251`, `:1671` takes `EmitLazyCall` in the lazy pass), so callees with inner calls get lazy bodies, and class-guarded callvirt sites exist (`:797`, `EmitLazyCall` guard path).

Guards, all already in the emitted code: the receiver is not null, has a vtable, is not the remoting proxy class, is not a boxed value type, and its class equals the site cache's; any failed test or a callee with no lazy body at run time takes EA's normal call before anything is written (`JitLazyMat` path). Kill switches: per-method kill on a `Mode=2` verify mismatch (`JitKill` `:1752`, clears the lazy cell and guard entries), `Mode=2` verifies one call in `VerifyEvery`, `Direct=0` turns the whole family off, and `Enabled=0` (release) keeps all `[MonoJit]` keys inert. Game gates already passed with `DirectLeaf=0`: reader trap `bp-jit-trap-chain` / `ip-jit-trap-chain` PASS (2026-11-06), `bp-/ip-jit-lazy-chain` (Mode=2) PASS, and the 2026-11-13 bailcensus / boundcall runs (Direct=1 DirectLeaf=0) PASS.

## Checksums [measured]

Every case, both arms, interpreter and compiled, n = 200000: equal. The tool now logs both values (`jitbench case N sums off .. on ..`, commit `dd837fa9`).

| case | interp = compiled, both arms | case | value |
|---|---|---|---|
| 0 | 12762240 | 8 | 6500000 |
| 1 | 12762240 | 9 | 6300000 |
| 2 | 12762240 | 10 | 175000 |
| 3 | 25524480 | 11 | 1346977280 |
| 4 | 76693856 | 12 | 178671360 |
| 5 | 12897952 | 13 | 200000 |
| 6 | 742854 | 25 | 9750000 |
| 7 | 201564 | 27 | 6609375 |

No `CHECKSUM DIFFERS` line. Cases 0-2 share one value by construction (512 keys summed over 200000 lookups).

## Timing, ns per call (median over 36 reps)

| case | shape | base compiled | chains compiled | gain |
|---|---|---|---|---|
| 0 | Dictionary<int>.TryGetValue | 190.1 | 148.4 | 41.6 |
| 1 | Dictionary<ulong>.TryGetValue | 8434.4 | 5003.2 | 3431.2 |
| 2 | Dictionary<ObjectGuid>.TryGetValue | 340.0 | 232.2 | 107.8 |
| 3 | List foreach | 78.1 | 65.4 | 12.7 |
| 4 | List get_Item + Count | 59.4 | 55.6 | 3.9 |
| 5 | List.Add | 124.5 | 100.8 | 23.7 |
| 6 | boxed Enum.Equals + GetHashCode | 438.3 | 438.0 | 0.3 |
| 7 | GetType() == typeof | 214.8 | 190.7 | 24.1 |
| 8 | Array.Resize | 498.6 | 485.8 | 12.8 |
| 9 | Array.IndexOf (64) | 1644.2 | 983.3 | 660.9 |
| 10 | ObjectGuid != | 66.2 | 57.0 | 9.2 |
| 11 | CASUtils flags | 51.7 | 45.2 | 6.5 |
| 12 | Dictionary foreach | 202.9 | 167.5 | 35.4 |
| 13 | ContainsKey + set_Item | 437.2 | 291.1 | 146.0 |
| 25 | JitLzChain (lazy chain) | 50.4 | 33.8 | 16.6 |
| 27 | JitLzVirt (class-guarded callvirt) | 32.7 | 15.6 | 17.1 |

No case is slower (smallest gain 0.3 ns, case 6, a boxed value-type receiver the guard refuses by design). Raw: `probe-out/jit-bench/chains2.{log,json}` (main checkout copy of this run).

## Case 1 explained [inferred from the code, consistent with the numbers]

`HostTest.cs` case 1 looks up `(r & 511) * 0x100000001UL` in a `Dictionary<ulong,int>`. The ulong hash is low word xor high word = `r ^ r` = 0, so all 512 keys sit in one bucket chain. A lookup walks about 256 entries, one `IEquatable<ulong>.Equals` callvirt each (`Dictionary.GetPrev` -> comparer `Equals`, the 4.74 M edge of `jit-call-edges.csv`). 3431 ns / ~256 = 13.4 ns saved per inner call, the same as the per-call gain of cases 25 and 27 (16.6, 17.1). Case 0 (int keys, no collision) gains 41.6 ns on about 2-3 inner calls. So case 1 is a worst-case hash collision, not a game shape, and the earlier flat-median model counted it as one of 14 equal cases.

## Model, no double counting

The saving is a per-lazy-call constant (13-17 ns: cases 1/25/27) times lazy calls per second. Lazy calls/s from the game's own counters `jitLazyCalls` over the 10 min profile, chains on (`DirectLeaf=0`), Mode=1: `probe-out/queue/boundcall-2026-11-13-{bp,ip}-boundcall-ab-*-native.json`: BP 203,900/s, IP 217,900/s. SV: no run with chains, left empty. (The Mode=2 runs show 65k/s because verify slows the game; not used.)

| | BP | IP |
|---|---|---|
| gain, 13-17 ns x calls/s (ms/s) | 2.65-3.47 | 2.83-3.70 |
| guard cost where a site bails (inferred: 23% of lazy calls bail on "no lazy body", from bailcensus bail/lazy-call totals; ~4 ns each) | 0.19 | 0.20 |
| net, Direct and chains together | 2.5-3.3 | 2.6-3.5 |
| incremental over Direct if Direct's modelled 1.8 ms/s already counted these calls | 0.7-1.5 | 0.8-1.7 |

The old 4.7 / 4.9 ms/s (flat median x 149k calls/s) is not used: it counted the same lazy-call pool as Direct and case 1. The pool is the same one, so Direct (leaf-only as shipped, `DirectLeaf=1`, nearly no listed leaf edge in game) and chains do not add: together 2.5-3.5 ms/s, chains' own share is 0.7-1.7 (if Direct is already counted) up to the whole 2.5-3.5 (if Direct only ever covers leaves). Guard-bail cost is not benched (inferred); it is far below the gain.

## Flip

Ini `[MonoJit] DirectLeaf=1` -> `0`. It only acts with `Direct=1` (ini default `Direct=0`) and `Enabled=1` (release ini `Enabled=0`): in the release the whole `[MonoJit]` section stays inert, so the flip changes no release behaviour until Direct/Enabled go on.
