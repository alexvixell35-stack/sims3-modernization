# Queue burnttilefloor-sv-1116 2026-11-16

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/burnttilefloor-sv-1116-2026-11-16.jsonl`.

## 2026-11-16T19:43 UTC, build 76142a409, jobs sv-burnttile-off, sv-burnttile-on

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| sv-burnttile-off | 19:43 (03:43) | 7.8 | **FAIL** | PASS: profile 6 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->3, owspread_types_run 0->60, stuckcheckasync_queued 0->226, stuckcheckasync_run 0->226, etspread_passes 0->1, stuckcheck_noplan 0->210, stuckcheck_notstuck 0->11, stuckcheck_rearmed 0->4<br>FAIL: events not fired: log /routefail: .*, MopBurnt [cut: full row in the jsonl] | 0 | 1690.6/1577.9/1834.7 -> 1900/1810.8/1733.5 | - | timing: invalid (job FAIL: FAIL: events not fired: log /routefail: .*, MopBurnt, Plan async FailedBecauseNoDestinationSpecified .*goal 1064\.7,52\.; noise medium); build: asi 17a2fcf3b5a3 not in DB ts3native 0.36.267-mc1116 (log ok); ts3fixes tuning 3719648bbb6a ts3fixes.package.hoursmemo-svprobe-1116 (expected game-copy/ts3fixes.package.hoursmemo-svprobe-1116) [cut: full row in the jsonl]<br>sim ms/s 129.5; p99 56 ms, p99.9 127, max 244, >=300 0, >=1000 0 (2.6 min) |

Stopped: sv-burnttile-off failed (stopQueueOnFail): FAIL: events not fired: log /routefail: .*, MopBurnt, Plan async FailedBecauseNoDestinationSpecified .*goal 1064\.7,52\.8,1243\.2,/ 0 lines (want >= 1).
