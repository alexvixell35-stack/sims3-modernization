# Handoff: JIT bundle 1113 (integration asi for tonight's bundle measurement)

Branch `feature/jit-bundle-1113` (worktree from master 411ace46). Local only: not merged to master, not pushed. NOT RUN in game.
For: the queue agent (tonight's ON / OFF bundle queue) and the morning merge.

## Merged, in order

| # | Branch | Result |
|---|---|---|
| 1 | `feature/monojit-clauselazy-guards` (b46145e1) | clean. ClauseLazy v195 cl* at 0x62A8, jit-piece-bench + MONOHOST_LOG, 44 coverage fixtures, jit-coverage fix |
| 2 | `feature/monojit-r1-c2c-e1` | already in master (0 commits ahead): R1 CodeKB, C2c list, E1 [Binder] TypeHandleDirect |
| 3 | `feature/monojit-table-4096` (9f6fa707) | conflicts resolved (below) |
| 4 | `feature/monojit-ret-teardown` (8c227bc0) | conflicts resolved; RetTeardown stays default 0 (its bench was still running) |
| 5 | `feature/monojit-chains-on` (47a1f55c) | jit-piece-bench.ts only |
| 6 | `feature/monofast-callpath-on` (24252c27) | fixes-require.ts STARTUP_COUNTERS only |
| 7 | `feature/jitbench-vtops-wide` (0d88f893), `feature/jitbench-equalsfast` (e135fae9) | bench case clash renumbered (below) |
| 8 | `feature/jitbench-mintfuse` (a56f0221) | merged after the coordinator's update (bench done): fewest-dispatch MfFuse placement, `[MintFuse] Sub=1` default, MONOHOST_MINTFUSE=eatime; MintBench case renumbered |
| - | `bugfix/master-defaults-match-release` (018b59da) | SKIPPED: not clean (PLAN.md "Resume here" conflict with the CallPath entry). Its own content (Instantiator.cs, ini, release-defaults-check.ts) did not conflict; merge it on master separately |

## Conflicts resolved

- **Counter block** (`native/ts3native/ts3native.cpp`): every block at its registry offset, no placeholders left: v194 bc* 0x6278, v195 cl* 0x62A8 (replaces `resV195[8]`), v918 bhnHits 0x62C8, v196 jitTableFull / jitMethCap 0x62CC (T4096's `resv6018[173]` dropped), v197 rtd* 0x62D4-0x62F3 (RetTeardown's `resv62CC[2]` dropped). Size 0x62F4 = 6333 int32; every `sizeof(Ts3nCounters)` assert (decimal `N * 4` and hex) set to it; `tools/probe/agent.js` names end `..., 'bhnHits', 'jitTableFull', 'jitMethCap', 'rtdOn', ...`. Registry rows v195-v197 note 0.36.256; next free v198 (+0x62F4).
- **monojit.cpp**: `JitGuardSite` keeps BoundCall's `bound` / `tRm` / `tNoLazy` fields with T4096's dynamic `g_jitCellCap` / `g_jitGuardCap`; init keeps T4096's CodeKB default 8192 + `JitAllocOrFallback(MaxMethods)`, then installs the RetTeardown block on the region's first page; the "OK mono jit" log line carries MaxMethods, BoundCall, ClauseLazy and RetTeardown.
- **monohost-jit.cpp**: T4096's `JitMethOf` hash + 8 MB region, with RetTeardown's `JRT_PART2` include and `MONOHOST_JITRETTD` block after it.
- **fixes-require.ts**: kept master's `@name` / `STARTUP_COUNTERS` version (0ed40427); T4096's duplicate `STARTUP_COUNTER` regex in flatDelta dropped. `STARTUP_COUNTERS` = tdOn, jitRegionKB, **jitMethCap**, bcFills, rwMode, **mcSwitch**. Fix commit: `tools/harness/queues/jit-table-4096.json` rules now `@jitMethCap>=4096,@jitRegionKB>=8192` (plain rules would read a delta of 0 and FAIL every on arm).
- **docs/537**: T4096 keeps §26; RetTeardown renumbered **§27** (code, ini, handoff `docs/handoff/ret-teardown-1113.md` references fixed).
- **jit-piece-bench.ts**: both checksum parsers kept (chains-on `jitbench case N sums off/on` -> `runChecksums`, RetTeardown `jitbench sums case N interp/compiled` -> `timedSums`). monohost prints both lines (same values, two formats).
- **Bench case numbers** (`HostTest.JitBench`, `kJitCase`, `kStep`, `JIT_CASES` 53):

| Cases | Piece | Was on its branch |
|---|---|---|
| 35-44 | VtOps / Wide target shapes (vtops-wide) | 35-44 (unchanged) |
| 45-48 | RetTeardown (RetTd getters / void / ctor / mixed) | 35-38 |
| 49-51 | game-shaped string cases (equalsfast) | 35-37 |
| 52 | MintBench (mintfuse) | 35 |

  The jit-gains.csv BoundCall row's case references now say 49-51. Result files under `docs/data/results/` keep the numbers they were measured with. A bench still running on the ret-teardown branch reports cases 35-38; those are 45-48 here.

## Version and build

- ts3native **0.36.256** (next free: branches held 0.36.250-0.36.255; table-4096 and mintfuse both claimed 0.36.254).
- `[MonoJit] Enabled=0` shipped in `native/ts3native/ts3native.ini`. Shipped defaults in this ini that differ from v0.2.12: `[MonoFast] CallPath=1` (callpath-on), `[MintFuse] Sub=1` (mintfuse), `[MonoJit] DirectLeaf=0`, CodeKB=8192, MaxMethods=4096 (inert while Enabled=0).
- Asi: see "Parked pair" below.

## Bundle arms (key names checked against `GetPrivateProfileIntW` in monojit.cpp / bindertd.cpp / mintcall.cpp / mintfuse.cpp)

**ON arm**
```
[MonoJit]  Enabled=1 Mode=1 CodeKB=8192 MaxMethods=4096 Direct=1 DirectLeaf=0 ClauseLazy=1 Wide=1 BoundCall=1 BoundCallMask=3 Inline=2 RetTeardown=0 VtOps=0 CallExit=0
[Binder]   TypeHandleDirect=1
[MonoFast] CallPath=1
[MintFuse] Sub=1
```
**OFF arm**
```
[MonoJit]  Enabled=0
[Binder]   TypeHandleDirect=0
[MonoFast] CallPath=0
[MintFuse] Sub=0
```
Notes:
- Pieces without their own switch: R1 = CodeKB (+ refusal log), C2c = the built-in list (`List=` empty), E1 = `[Binder] TypeHandleDirect`.
- `Inline=2` is what every bench / verify / replay set used (jit-table-4096, ret-teardown replays, piece bench "game switch set").
- `Wide` has no line in the shipped ini (code default 0): the queue must set `MonoJit.Wide=1`. Wide sets template group 512 itself; keep `Groups=511`.
- Not in the brief, set by the queue as the shipped default unless decided otherwise: `NewVt=0` (B2b, VtOps family), `FastCopy=0`, `Ldsflda=0`, `Count=0`, `Trap=0`. The jit-table-4096 verify jobs ran FastCopy=1 Ldsflda=1 NewVt=1 with VtOps=1 CallExit=1; that is a different set from this ON arm.
- ON vs OFF also differs in CallPath and MintFuse Sub, so the bundle delta is JIT + CallPath + MintFuse together.
- Startup counters for nativeRequire: write `@jitMethCap>=4096`, `@jitRegionKB>=8192`, `@tdOn>=1`, `@bcFills>=1`, `@mcSwitch...` (plain rules on them are refused by startupDeltaLint).

## Gates

All host / static, 2026-11-13, at 758e9d8f (no game run):

- `native/ts3native/build.cmd`: built, native selftest `selftest ok` (incl. `monojit capture / retteardown / monojit: ok`). First build failed on a stale T4096 size assert in monojit-test.h (fix commit 750f9fa4).
- `node tools/hook-sites.ts --check`: rc 0.
- `node tools/patch-bytes-check.ts`: 3150 expectations, ok 3150, mismatch 0, unmapped 0.
- Host corpus (monohost selftest, `MONOHOST_JIT=verify`, ON-arm env: JITLAZY=1 JITDIRECTLEAF=0 JITCLAUSELAZY=1 JITWIDE=1 JITBIND=1 JITBOUNDCALL=3 JITINLINE=2 JITRETTD=0 JITVTOPS=0 JITCALLEXIT=0 MINTCALL=fast MINTFUSE=sub JITMAXMETHODS=4096): `SELFTEST OK: 41 passed`; jit-corpus 272 cases, 1 differ = the known `JitKO.S1AF` stack-trace text only, 0 unstable, per-call verify ok 62943, MISMATCH 0; BoundCall mask 0x3 in force.
- Replay BP / IP / SV c3g (replay text rebuilt from `probe-out/jit-capture-<w>-c3g.jitcap`; `--lazy --switches Wide=1,Inline=2,ClauseLazy=1,DirectLeaf=0,Bind=1,BoundCall=3,RetTeardown=0,VtOps=0,CallExit=0,NewVt=0,FastCopy=0,Ldsflda=0`, env MINTFUSE=sub): BP 4466 inputs / 4307 ran, IP 4374 / 4235, SV 4416 / 4275: 0 differ, 0 unstable, 0 faulted, per-call verify MISMATCH 0 each (skips = NRaas types not in the host). The first BP run hung: monohost died silently after its `jit bind` lines and jit-replay waited; four BP reruns (full set, without MintFuse, without MaxMethods, after the fix) all pass, cause not found. jit-replay now fails fast when the host exits without a summary (fix commit).
- `node tools/jit-coverage.ts --self-check`: OK.
- `node tools/selftest-all.ts`: 230 PASS, 0 FAIL, 0 TIMEOUT.

## Parked pair

| File (main checkout `game-copy/`) | sha256 |
|---|---|
| `ts3native.asi.jitbundle-1113` (0.36.256, commit 758e9d8f) | `1c968ed16e2ac74878a8a7e3083fbde21571a2c475cc99405680039bc37b2231` |
| `ts3native.ini.jitbundle-1113` (= branch ini) | `08ffb919f1a2515239374b0bccb5108e0143c4a8967ffd5092325820251fef2c` |
| `ts3native.map.jitbundle-1113` | `44f8135d5f12d3b27fa6548cee6b69c95126758b77d94302988c586884407154` |

Listed in `docs/data/builds.json`. ts3fixes: no change on this branch, pair with `ts3fixes.package.rc1111e`. No queue file written (the queue agent owns it).

## Morning merge

- Merge order as above; everything is local. `bugfix/master-defaults-match-release` needs a PLAN.md-only resolution and goes in separately.
- RetTeardown: flip to 1 only after its bench (cases 45-48 here) and a replay with RetTeardown=1 pass.
- The worktree holds a `game-copy` junction to the main checkout's game-copy: delete the link only (`[System.IO.Directory]::Delete(path, $false)`) before removing the worktree.
