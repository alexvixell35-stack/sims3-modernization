# Queue saved-stacks-names 2026-11-06

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/saved-stacks-names-2026-11-06.jsonl`.

## 2026-11-06T04:14 UTC, build cad0dbf7, jobs ssn-walk

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| ssn-walk | 04:14 (12:14) | 1.5 | **CRASH** | FAIL: step failed<br>FAIL: UNKNOWN: [RouteAsync] on but /native has no asyncStarted: run void<br>PASS: 0 new script errors (max 0) | 1 | 2184.8/1841.9/1566.5 -> undefined/undefined/undefined | - | timing: invalid (job VOID: FAIL: UNKNOWN: [RouteAsync] on but /native has no asyncStarted: run void; noise medium); build: asi 1d2824a5e9e3 not in DB ts3native 0.36.168 (log ok); ts3fixes tuning dfaae276e6d1 not in DB (expected game-copy/swapfix/ts3fixes-after.package); noise: medium (other apps 8.1% of game CPUs); others on game CPUs 8.1%; clock avg/min 4266/4251 MHz; GetMethods on every class of |
