# Queue routefail-smoke2 2026-11-06

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/routefail-smoke2-2026-11-06.jsonl`.

## 2026-11-06T23:32 UTC, build 2e9af5b4, jobs sv-routefail-smoke

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| sv-routefail-smoke | 23:32 (07:32) | 6.7 | PASS | PASS: profile 5 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->3, owspread_types_run 0->60, stuckcheckasync_queued 0->158, stuckcheckasync_run 0->158, etspread_passes 0->1, stuckcheck_noplan 0->145, stuckcheck_notstuck 0->10, stuckcheck_rearmed 0->3 | 0 | 2093.6/1783.1/1660.9 -> 2133.2/1953.9/1482.9 | - | timing: invalid (noise medium); build: asi ff9c22377f70 ts3native.asi.routefail2 ts3native 0.36.168c-test2 (log ok); ts3fixes tuning 5716accd026a imp1105-fixes/imp1105b-fixes/ts3fixes.package.routefail1/ts3fixes.package.routefail2 (expected game-copy/ts3fixes.package.routefail2); noise: medium (other apps 21.0% of game CPUs); others on game CPUs 21.0%; clock avg/min 4206/4168 MHz; SV 5 min: the ac |
