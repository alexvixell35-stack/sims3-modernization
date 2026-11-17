# Handoff: MonoJit table grow (piece A1, 2026-11-14)

Branch `feature/monojit-table-grow` from `feature/jit-bundle-1113` (1be42a53). Local only: not merged to master, not pushed. NOT RUN in game.
Details: `docs/537-monojit-lazy-frames.md` §30.

## Cause

- Full table = the **method table** `g_jitMeth` (cap `g_jitMethCap` = `[MonoJit] MaxMethods`), refused at `native/ts3native/monojit.cpp:1212` (`JitCompile`: `if (g_jitMethN >= g_jitMethCap) return ts3native_counters.jitTableFull++, "method table full";`).
- It does not fill early: it fills **at** MaxMethods. Every `jit-bundle-ab-1113` ON run ended with `jitMethods` = `jitMethCap` = 4096, `jitTableFull` 1147-1435, `jitFullN` 0, region 6944-6986 of 8192 KB. ~3400-3500 methods compile during load, 10 min of play wants ~1.8-2.1k more: demand ~5.2-5.5k a run. The extra rows are mostly closed generic types (one runtime method per `List<T>`, `Dictionary<K,V>`, enumerator instantiation).
- Not the ip hash table, positions, lazy cells or guard sites. Refused methods take no row. A compiled rm is never compiled twice.
- The verify runs (`jit-bundle-verify-1113`, `Mode=2`, ~2.1 KB a body) were full on the code region instead: 3854-3873 methods at 8188 of 8192 KB.

## Fix (commit 5ec1797a)

- `JIT_METH_MAX` 4096 -> 8192: `MaxMethods` 512-8192, default 8192 (positions, lazy cells and guard sites scale with it).
- ip table 2^17 -> 2^18 (`JitEnter` mask `0x3FFFF`, static_assert keeps it at or under 3/4 full).
- `CodeKB` default 8192 -> 16384 (the existing max). Repo ini: `MaxMethods=8192`, `CodeKB=16384`, `Enabled=0` unchanged.
- Guards unchanged: alloc fallback to 2048 / 4096 KB (logged), compile-fault session kill, verify sampling + `JitKill`, ClauseLazy kill switch, `jitSwitch`.
- No counter change: `jitTableFull` / `jitMethCap` (v196) already exist.

## Cost

- Address space, only with `Enabled=1` (one reservation at install): **22948 KB**, was 11512 KB: **+11436 KB** (+8192 code, +2048 ip table, +1196 rows/positions/cells/guards). Fallback 8980 KB (was 6892).
- `CodeKB=12288` still holds the measured ~5.5k bodies (~25% headroom) for 4096 KB less, if address space is tight.
- `JitLazyCellFor` (linear) on a guard fill: ~0.63 us at ~5.5k cells (was ~0.47 us at 4096) [inferred from the §26 bench].

## Gates (no game run)

- `native/ts3native/build.cmd`: built, `selftest ok`. `hook-sites --check` rc 0. `patch-bytes-check` 3150 ok, 0 mismatch.
- Host corpus (rebuilt monohost, `MONOHOST_JIT=verify`, jit-bundle ON-arm env): `SELFTEST OK: 41 passed`, 272 cases, 1 differ = the known `JitKO.S1AF` stack-trace text, per-call verify ok 62943, MISMATCH 0.
- Replay BP / IP / SV c3g (`--mode verify --lazy`, ON-arm switches, `MONOHOST_MINTFUSE=sub`): BP 4466 inputs / 4307 ran, IP 4374 / 4235, SV 4416 / 4275: 0 differ, 0 unstable, 0 faulted, per-call verify MISMATCH 0 each.
- `node tools/selftest-all.ts`: 230 PASS, 0 FAIL, 0 TIMEOUT.
- The host mirror is unchanged (ip table 2^17, `MONOHOST_JITMAXMETHODS` 4096): the host compiles fewer methods than that, so it never needs the game's new sizes.

## Pair (main checkout `game-copy/`)

| File | sha256 |
|---|---|
| `ts3native.asi.jittable-1114` (0.36.257, commit 5ec1797a) | `b5cf7ca65375732005f5a22546a2017149121e36570404becfe6853c2a83ea11` |
| `ts3native.ini.jittable-1114` (= branch ini) | `6402e7f5a9eea270f17102852eb2c01f2322de0e75f2f1dda9790800493d569a` |
| `ts3native.map.jittable-1114` | `9046ae7832d59a519e761298ecfcd21c37f586d1d77b0e8a9c28b0e9841714cf` |

Listed in `docs/data/builds.json`. ts3fixes: no change, pair with `ts3fixes.package.rc1111e`.

## Queue needed (not written; the queue agent owns it)

- The jit-bundle ON arm with this pair, plus `MonoJit.MaxMethods=8192` and `MonoJit.CodeKB=16384` set explicitly.
- Read back: the startup line regex `OK   mono jit .*code region 16384 KB \(CodeKB\), 8192 methods \(MaxMethods\)` (tested: matches the 11-13 line with the new numbers, not the old one). nativeRequire `@jitMethCap>=8192`, `@jitRegionKB>=16384` (startup counters need `@`).
- Pass: `jitTableFull` ~0 (target 0, `<=10` for a long tail), `jitFullN` 0, `jitMismatch` 0. Record end `jitMethods` (expect ~5.2-5.5k) to see whether first-use demand levels off over a longer run.
- Order: one `Mode=2` verify job per world (correctness, region not full at ~2.1 KB a body), then ON/OFF timing (only after a smoke).

## Merge notes

- Commits on top of jit-bundle-1113: code (5ec1797a), docs (§30, index, builds.json, this file). Merge after `feature/jit-bundle-1113`.
- Version 0.36.257 (next free 0.36.258).
- The worktree holds a `game-copy` junction to the main checkout's game-copy: delete the link only (`[System.IO.Directory]::Delete(path, $false)`) before removing the worktree.

## Status 2026-11-14 07:10 PHT (branch feature/monojit-time-table, worktree wt-timetable)

- Merged `feature/monojit-paired-timing` + `feature/monojit-table-grow` (commit 8fc089f6): conflicts only in `docs/index.md` (both lines kept) and `TS3NATIVE_VERSION` (0.36.259). `CodeKB` default 16384 -> 12288 (code + ini): table-grow demand ~5.5k bodies x ~1.7 KB = ~9.4 MB (Mode=1); Mode=2 ~2.1 KB a body = ~11.5 MB, under 12288 but tight: a `jitFullN > 0` verify run is a finding, rerun with CodeKB=16384. Address space with Enabled=1: 18852 KB.
- Queue `tools/harness/queues/jit-time-1114.json` written and dry-run OK (3 jobs bp/ip/sv-jt-time, 15 min, regexes tested at parse).
- NOT built yet: `boundcall-abba-1113` (timing) was still running at 07:04 (7 jobs left, until ~08:45). Open: asi build `game-copy/ts3native.asi.jittimetable-1114`, replays BP/IP/SV with `MONOHOST_JITTIME=1`, selftest-all, native selftest, builds.json, and the same gates on the paired-timing-only asi (`jittime-1114`) if wanted. Build only in the 08:50-10:50 correctness window.
