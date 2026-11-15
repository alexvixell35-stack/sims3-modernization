# Handoff: MonoJit RetTeardown (B3), 2026-11-13

Branch `feature/monojit-ret-teardown` (from `feature/monojit-r1-c2c-e1`, E1 + R1 + C2c). Not merged, not pushed. Design, guards and gate numbers: docs/537 §27.

## Status

- Built: steps 1-4 (implement, guards, counters / version, host gates). **Not done (by scope): bench, flip, game queue.** Default stays 0.
- Gain target: **incremental over E1, ~1.92 ms/s ceiling.** The census ret line (4.39 ms/s) includes `RuntimeTypeHandle.get_Value` 2.47 ms/s, which E1 (`[Binder] TypeHandleDirect`) removes; do not count it again [inferred from the census split].

## What is built

| Item | Value |
|---|---|
| Game switch | `[MonoJit] RetTeardown` 0/1, default 0 (repo ini) |
| Host env | `MONOHOST_JITRETTD=1`, `MONOHOST_JITRETTD_EVERY=<n>` (verify sampling, default 1) |
| Replay switch | `node tools/jit-replay.ts ... --switches ...,RetTeardown=1` |
| Version | ts3native 0.36.255 (0.36.246-0.36.254 taken by other branches) |
| Counters | v197 at `0x62D4`-`0x62F3`: `rtdOn, rtdSites, rtdBails, rtdVerified, rtdMismatch, rtdVSkipped, rtdFaults, rtdKillWhy`; `Ts3nCounters` now `0x62F4` (gap `resv6018[175]` before it) |
| Logs | OK line `..., RetTeardown=1`; kill: `FAIL mono jit ret teardown: ...`; host: `jit ret teardown: on, sites, bails, verified, MISMATCH, skipped, faults, kill` |
| Code | `native/ts3native/monojit-ret.h` (new, shared game + host), hooks in `monojit.cpp` / `monohost-jit.cpp` (EmitExit, JitVerifySlow, install), replay line in `monohost-jitreplay.cpp`, corpus KRT block in `tools/jit-corpus.ts` |

Merge notes: `tools/harness/agent.js` names for the v197 counters are not added (base agent.js ends before them); add `rtd*` at merge. Every `*-test.h` size assert moved `6150*4` -> `6333*4` and `0x6018` -> `0x62F4`; a sibling that also grows the counters (method-table cap agent) will clash there: renumber the later one.

## Gate results (host, 2026-11-13)

- Native selftest: `monojit retteardown: ok`, `selftest ok` (differential vs a stand-in EA, negative control, fault test, kill).
- Corpus, verify + RetTeardown: 264 cases, 1 differ (`JitKO.S1AF`, known, also with RetTeardown=0), MISMATCH 0; teardown verified 1327, mismatch 0, faults 0.
- Corpus, on + RetTeardown: same known differ only; sites 1422, bails 43183, faults 0, kill 0.
- Replay BP / IP / SV c3g, verify, lazy, `VtOps=1,CallExit=1,NewVt=1,Inline=2,Wide=1,RetTeardown=1`: 0 differ, 0 unstable, 0 faulted, MISMATCH 0 each; teardown verified only 4 / 4 / 5 (replay inputs return to the host at depth 0, which bails by design).
- hook-sites `--check` rc 0; patch-bytes-check 3143 ok / 0 mismatch; selftest-all 226 PASS, 0 FAIL.
- Known in every arm, not ours: `AS-install` selftest failure (34/35), `JitKO.S1AF` stack-trace text.

## Open risks

- Mode 1 runs the block unverified (fault watch only). Mode 2 on game data is the real proof.
- Bail rate in game unknown: a ret whose caller is depth 0 / newobj / has stack values pays the tests and then the old exit. If the bench shows the bail path slower than the old exit, the piece can lose.
- EA scratch slots excluded from the compare (CallPath's dead-slot proof, docs/139, not re-proved).
- Verify adds two JitEnter trips per sampled ret: Mode 2 is not a timing arm.

## For the bench agent (step 5)

1. Merge `feature/monojit-clauselazy-guards` (or master once it has `tools/jit-piece-bench.ts` + `docs/data/jit-bench-howto.md`) into a bench branch from this one; rebuild monohost (`cmd /c "<wt>\native\monohost\build.cmd <main>\game-copy\assemblies"`).
2. Case: rets of short compiled callees into an **interpreted** caller (that is the only place the block runs). Existing cases 4 (`List get_Item + Count`), 10 (`ObjectGuid !=`) and 14 (`JitId (compiled, trivial)`) fit (names from `kJitCase` on `feature/monojit-clauselazy-guards`); if you add one, a loop in `HostTest.JitBench` calling 3-4 tiny static int / void / object returns, `acc` as the checksum. Read `bails` vs `sites` from the log: a case where most rets bail measures the wrong thing.
3. Arms: `--arm base: --arm rettd:MONOHOST_JITRETTD=1 --common "MONOHOST_MINTCALL=fast MONOHOST_JITLAZY=1 MONOHOST_JITVTOPS=1 MONOHOST_JITCALLEXIT=1 MONOHOST_JITNEWVT=1 MONOHOST_JITINLINE=2 MONOHOST_JITWIDE=1"`, `--rounds 6 --reps 9`, core outside any game mask; checksums must match the interpreter.
4. jit-gains.csv row: ns/ret saved x the ~1.92 ms/s remainder's ret count (incremental over E1, not 4.39). Ship rule: gain, no penalty on the bail-heavy case, guards above.

## For the queue agent (step 6, only after a bench gain)

- `tools/harness/queues/jit-ret-teardown.json`: BP / IP / SV, the current MonoJit game set + `[MonoJit] Mode=2 RetTeardown=1` (verify), then Mode=1 arms off / on for timing (ABBA per the bench rules).
- Gates: `@rtdOn>=1`, `@rtdVerified>=1`, `@rtdMismatch<=0`, `@rtdFaults<=0`, `@rtdKillWhy<=0`; no `FAIL mono jit ret teardown` line; OK line contains `RetTeardown=1`; script errors 0. Read `rtdBails / rtdSites` per world.
- Smoke job first (queue.ts does it); counter names must be in agent.js before the run (see merge notes).

## Status 2026-11-13 (bench agent, step 5): benched, NO GAIN, stays off

- Branch merged with `feature/monojit-clauselazy-guards` (1dddb35a): counters v195 / v918 fill 0x6018-0x62CB, `resv62CC[2]` holds v196, rtd* v197 stays at 0x62D4, `Ts3nCounters` 0x62F4 (6333 int32), `tools/probe/agent.js` has `resv62CC` + `rtd*`, the 4 KRT corpus cases are now 247-250 (Count 251). Native selftest ok (`monojit retteardown: ok`), monohost builds.
- New host bench cases 35-38 (`HostTest.RtBox` / `RtI` / `RtV`: getters x4, void methods x4, ctor via newobj, mixed). The host logs `jitbench rettd case C bails B iters N sites S` and `jitbench sums case C interp X compiled Y`; `tools/jit-piece-bench.ts` prints both and puts them in `--json` (`rettd`, `timedSums`).
- Run: core 2, no game, `--rounds 6 --reps 9`, game switch set (`LAZY VTOPS CALLEXIT NEWVT INLINE=2 WIDE`), two runs each with `MONOHOST_MINTCALL=off` (game default CallPath=0) and `=fast`. Data: `probe-out/jit-bench/retteardown.csv` / `.json` (main checkout, not committed).
- Result (ns per iteration, piece minus base, compiled medians): case 14 (1 ret) +2.8 / -2.9 / +3.1 / -0.6; case 35 (4 rets) +11.1 / +1.7 / +6.3 (one run: -51, base arm inflated, discarded); case 36 (4 ret.void) +4.5 / +7.5 / -0.1; case 37 (ctor, bails 1 of 1) +5.9 / +2.4 / +5.3 / +2.5; case 38 (1 bail of 4) +1.6 / -3.2 / +8.5. Hit path: 0 to +3 ns per ret, never a clear win. Bail path: +2.4 to +5.9 ns per ret, never faster. Checksums equal in every arm and equal to the interpreter (case 4, 10, 14, 35-38).
- Hit vs bail: `bails` 0 per iteration on 4 / 14 / 35 / 36 (block hit every ret), 1 per iteration on 10 / 37 (all bail), 1 of 4 on 38. The corpus's 43183 bails vs 1422 sites is the native driver (depth 0). Game: the census ret rows list no ctor; the four ctors on the game list (`Enumerator`, `KeyValuePair`, `ObjectGuid`, `Vector3`) are structs, whose newobj returns would bail, and they are no ret row. Expected game bail share is low [inferred], so the hit path decides, and it shows no gain. Game rtdBails / rtdSites stays unmeasured.
- CallPath: not required. `monojit-ret.h:161-167` accepts EA's ret entry or CallPath's; the `MINTCALL=off` runs installed it (sites 587) and hit it. With CallPath on the old exit already lands in CallPath's McRet, so the block replaces nearly the same code.
- Model: gain per ret about 0 (central), so incremental ms/s over E1 about 0 (cap 1.92 not approached); at +1 to +3 ns per ret and the census's ~140k rets/s (41.9 M run calls over ~300 s [inferred]) the piece would cost 0.1 to 0.4 ms/s. Row in `docs/data/jit-gains.csv`: verdict `off`.
- Verdict: do not flip, do not queue the game jobs (step 6). Reopen only if a different block shape removes EA's remaining work (the dispatch through the live table is the same indirect jump EA does).
