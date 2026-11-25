# Queue envlistmemo-dying-correct-1117 2026-11-17

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/envlistmemo-dying-correct-1117-2026-11-17.jsonl`.

## 2026-11-17T20:17 UTC, build 646a0d5cc, jobs bp-dying, ip-dying

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-dying | 20:17 (04:17) | 13.6 | **FAIL** | FAIL: profile 10 min, seed 12345 (rng-compare.ts)<br>FAIL: fixes counters: envlistmemo_hit_wrong=7 (want <1)<br>ts3fixes activity: envlistmemo_miss_same 0->2458, envlistmemo_scans 0->4929, envlistmemo_first 0->36, envlistmemo_hit_ms 0->2300, envlistmemo_calls 0->10020, envlistmemo_hit 0->7243, envlistmemo_events 0->344111, envlistmemo_miss_diff 0->276, envlistmemo_list_ms 0->3685 [cut: full row in the jsonl] | 1 | 1714.2/1624.6/1761.8 -> 1986.7/1790.3/1647.6 | - | timing: invalid (job VOID: runInvalid: noisy machine (other CPU 21.1% >= 10%); noise high; heavy tool samplers); build: asi 25a09ec77e35 ts3native.asi.master-1117c ts3native 0.36.271-mc1117 (log ok); ts3fixes tuning 53cd43c3c826 ts3fixes.package.envlistmemo-1117/ts3fixes.package.hhtoo-local3-1118/ts3fixes.package.hhtoo-local4-1118 (expected game-copy/ts3fixes.package.envlistmemo-1117) [cut: full row in the jsonl]<br>sim ms/s 178.4; p99 103 ms, p99.9 175, max 414, >=300 1, >=1000 0 (2.3 min) |

Stopped: bp-dying failed (stopQueueOnFail): FAIL: profile 10 min, seed 12345 (rng-compare.ts) | FAIL: fixes counters: envlistmemo_hit_wrong=7 (want <1).
