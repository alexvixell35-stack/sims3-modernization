# Queue commodmap-fast-1121-correct-rerun1 2026-11-21

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/commodmap-fast-1121-correct-rerun1-2026-11-21.jsonl`.

## 2026-11-21T02:40 UTC, build 97c0aa4bc, jobs bp-commodmap-check, ip-mm-commodmap-shadow

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-commodmap-check | 02:40 (10:40) | 3.8 | **FAIL** | FAIL: profile 10 min, seed 12345 (rng-compare.ts)<br>FAIL: sim clock stuck at day 15 03:28 for ~60 s (speed Pause, sub_state LiveModeState) (fail-fast guard)<br>ts3fixes activity: etspread_passes 0->1; ts3fixes overwatchchain: not reached (sim 03:10 +18 min, no 03:00); ts3fixes stuckcheckasync: not reached (easpread_lots_passes rose 0: the 3 AM venue cleanup had not armed the stuck checks yet) [cut: full row in the jsonl] | 1 | 1746.8/1682.7/1819.4 -> 1776.4/1700/1806.1 | - | timing: invalid (job VOID: runInvalid: profiler cut short (88 of 600 s); noisy machine (other CPU 11.7% >= 10%); crash or abort (no "done; game ali; noise high); build: asi 66f4d0305688 ts3native.asi.mkit2-1121 ts3native 0.36.298-mkit2 (log ok); ts3fixes tuning e290a6386bec ts3fixes.package.cmf-1121 (expected game-copy/ts3fixes.package.cmf-1121); noise: high (other apps 39.8% of game CPUs [cut: full row in the jsonl]<br>sim ms/s 7.1; p99 <50 ms, p99.9 <50, max 0, >=300 0, >=1000 0 (0.2 min) |

Stopped: bp-commodmap-check failed (stopQueueOnFail): FAIL: profile 10 min, seed 12345 (rng-compare.ts) | FAIL: sim clock stuck at day 15 03:28 for ~60 s (speed Pause, sub_state LiveModeState) (fail-fast guard).
