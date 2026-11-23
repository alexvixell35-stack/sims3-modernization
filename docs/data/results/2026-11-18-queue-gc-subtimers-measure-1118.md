# Queue gc-subtimers-measure-1118 2026-11-18

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/gc-subtimers-measure-1118-2026-11-18.jsonl`.

## 2026-11-18T11:21 UTC, build 50eb3732b, jobs bp-gcsub-smoke, bp-gcsub, sv-gcsub, ip-gcsub

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-gcsub-smoke | 11:21 (19:21) | 7.1 | **FAIL** | PASS: profile 5 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->2, owspread_types_run 0->40, stuckcheckasync_queued 0->130, stuckcheckasync_run 0->130, etspread_passes 0->1, stuckcheck_noplan 0->111, stuckcheck_notstuck 0->11, stuckcheck_rearmed 0->8, stuckcheck_reset 0->1 [cut: full row in the jsonl] | 0 | 1697.5/1618.3/1809.6 -> 1832.8/1805.8/1731 | - | timing: invalid (job VOID: runInvalid: noisy machine (others 36.0% of game CPUs); noise high); build: asi a789aa7e4723 ts3native.asi.gcsub-1118 ts3native 0.36.279-gcsub1 (log ok); ts3fixes tuning f961d3c2c642 ts3fixes.package.master-1117c (expected game-copy/ts3fixes.package.master-1117c); start: fresh (105 s); snapshot taken (6410 ms); noise: high (other apps 35.8% of game CPUs) [cut: full row in the jsonl]<br>sim ms/s 198.1; p99 89 ms, p99.9 135, max 182, >=300 0, >=1000 0 (2.9 min) |

rewind: 0 of 1 planned jobs started from a rewind, 0 refused, 0 failed (fresh load instead); ~0 min saved vs fresh starts (105 s each: mean of this run's 1 fresh starts)

Stopped: bp-gcsub-smoke failed (stopQueueOnFail): FAIL: events not fired: log /^GC idle gate: IdleGate=2 (gcgMode 0) IdleGateMB=64 IdleGateHeapPct=15 ready=0x3$/ 0 lines (want >= 1).
