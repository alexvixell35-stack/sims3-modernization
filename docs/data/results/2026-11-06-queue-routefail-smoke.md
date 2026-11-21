# Queue routefail-smoke 2026-11-06

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/routefail-smoke-2026-11-06.jsonl`.

## 2026-11-06T22:23 UTC, build 057afe6a, jobs sv-routefail-smoke

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| sv-routefail-smoke | 22:23 (06:23) | 7 | PASS | PASS: profile 5 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->2, owspread_types_run 0->40, stuckcheckasync_queued 0->154, stuckcheckasync_run 0->154, etspread_passes 0->1, stuckcheck_noplan 0->143, stuckcheck_notstuck 0->8, stuckcheck_rearmed 0->3 | 0 | 2080.6/1796.5/1676.9 -> 2122.5/1967.7/1466.1 | - | timing: invalid (job VOID: runInvalid: noisy machine (others 41.4% of game CPUs); noise high); build: asi 8bd0b99e57aa ts3native.asi.routefail1 ts3native 0.36.168c-test1 (log ok); ts3fixes tuning 5716accd026a imp1105-fixes/imp1105b-fixes/ts3fixes.package.routefail1 (expected game-copy/ts3fixes.package.routefail1); noise: high (other apps 40.9% of game CPUs); others on game CPUs 40.9%; clock avg/mi |

## 2026-11-06T22:34 UTC, build eb495ba1, jobs sv-routefail-smoke

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| sv-routefail-smoke | 22:34 (06:34) | 6.8 | PASS | PASS: profile 5 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->2, owspread_types_run 0->40, stuckcheckasync_queued 0->156, stuckcheckasync_run 0->156, etspread_passes 0->1, stuckcheck_noplan 0->141, stuckcheck_notstuck 0->12, stuckcheck_rearmed 0->3 | 0 | 2101.8/1776.7/1676.9 -> 2130.5/1944/1497.9 | - | timing: invalid (job VOID: runInvalid: noisy machine (others 22.0% of game CPUs); noise high); build: asi 8bd0b99e57aa ts3native.asi.routefail1 ts3native 0.36.168c-test1 (log ok); ts3fixes tuning 5716accd026a imp1105-fixes/imp1105b-fixes/ts3fixes.package.routefail1 (expected game-copy/ts3fixes.package.routefail1); noise: high (other apps 22.8% of game CPUs); others on game CPUs 22.8%; clock avg/mi |
