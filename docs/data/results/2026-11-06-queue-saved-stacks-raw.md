# Queue saved-stacks-raw 2026-11-06

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/saved-stacks-raw-2026-11-06.jsonl`.

## 2026-11-06T05:56 UTC, build 02a039fd, jobs ssr-raw

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| ssr-raw | 05:56 (13:56) | 2.1 | **CRASH** | FAIL: step failed<br>FAIL: UNKNOWN: [RouteAsync] on but /native has no asyncStarted: run void<br>PASS: 0 new script errors (max 0) | 1 | 2110/1840.6/1637.5 -> undefined/undefined/undefined | - | timing: invalid (job VOID: FAIL: UNKNOWN: [RouteAsync] on but /native has no asyncStarted: run void; noise high); build: asi 1d2824a5e9e3 not in DB ts3native 0.36.168 (log ok); ts3fixes tuning dfaae276e6d1 not in DB (expected game-copy/swapfix/ts3fixes-after.package); noise: high (other apps 34.8% of game CPUs, NOT comparable (load or clock)); others on game CPUs 34.8%; clock avg/min 3853/3837 MHz |
