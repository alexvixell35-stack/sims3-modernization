# Queue hotfix-v031b-ctl-1121 2026-11-20

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/hotfix-v031b-ctl-1121-2026-11-20.jsonl`.

## 2026-11-20T23:29 UTC, build 58f85dfe3, jobs bp-speedlock-modeswitch-ctl-rc1119b

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-speedlock-modeswitch-ctl-rc1119b | 23:29 (07:29) | 3.6 | **FAIL** | FAIL: profile 3 min, seed 12345 (rng-compare.ts)<br>FAIL: sim clock stuck at day 15 03:36 for ~60 s (speed Pause, sub_state LiveModeState) (fail-fast guard)<br>ts3fixes activity: etspread_passes 0->1; ts3fixes overwatchchain: not reached (sim 03:10 +25 min, no 03:00); ts3fixes stuckcheckasync: not reached (easpread_lots_passes rose 0: the 3 AM venue cleanup had not armed the stuck checks yet) [cut: full row in the jsonl] | 1 | 1718.9/1657/1810.4 -> 1753.6/1673.2/1802.2 | - | timing: invalid (job VOID: runInvalid: profiler cut short (88 of 180 s); crash or abort (no "done; game alive"); noise medium); build: asi ba8c7211b7b9 ts3native.asi.hf031 ts3native 0.36.295a (log ok); ts3fixes tuning 710a355f3c57 ts3fixes.package.rc-1119b/ts3fixes.package.rel030 (expected game-copy/ts3fixes.package.rc-1119b); noise: medium (other apps 32.6% of game CPUs) [cut: full row in the jsonl]<br>sim ms/s 6.7; p99 <50 ms, p99.9 <50, max 0, >=300 0, >=1000 0 (0.2 min) |

Stopped: bp-speedlock-modeswitch-ctl-rc1119b failed (stopQueueOnFail): FAIL: profile 3 min, seed 12345 (rng-compare.ts) | FAIL: sim clock stuck at day 15 03:36 for ~60 s (speed Pause, sub_state LiveModeState) (fail-fast guard).
