# Queue m2-motive-time-1116-yalias-1117-rerun1-1118 2026-11-18

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/m2-motive-time-1116-yalias-1117-rerun1-1118-2026-11-18.jsonl`.

## 2026-11-18T06:02 UTC, build bb09ea36f, jobs bp-m2-abba-off-1, bp-m2-abba-on-1, bp-m2-abba-on-2, bp-m2-abba-off-2, ip-m2-abba-off-1, ip-m2-abba-on-1, ip-m2-abba-on-2, ip-m2-abba-off-2

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-m2-abba-off-1 | 06:02 (14:02) | 5.4 | **FAIL** | FAIL: profile 10 min, seed 12345 (rng-compare.ts)<br>FAIL: noisy machine early: machine load: other CPU mean 14.2% max 15.4% (chrome 6.7, grep 3.8, claude 0.9), TS3W 12.9%, NvRemixBridge 2.7% (GPU 3D 10.0%, counted with the game), clock 144% -> min 144% (3 samples): NOT comparable: other CPU 14.2% >= 10%; game CPUs 4-15: others 28.2%, rest CPUs 99.0% busy, TS3W pinned 3/3 samples [cut: full row in the jsonl] | 1 | 1686.3/1622.3/1815 -> undefined/undefined/undefined | - | noisy: does not count, rerun as bp-m2-abba-off-1-rerun; timing: invalid (job VOID: FAIL: noisy machine early: machine load: other CPU mean 14.2% max 15.4% (chrome 6.7, grep 3.8, claude 0.9), TS3W 12.9%, ; noise high); build: asi 7b33ffaec4f5 ts3native.asi.yalias-1117 ts3native 0.36.274-yalias2 (log ok) [cut: full row in the jsonl]<br>sim ms/s 188.7; p99 86 ms, p99.9 156, max 213, >=300 0, >=1000 0 (1.3 min) |
