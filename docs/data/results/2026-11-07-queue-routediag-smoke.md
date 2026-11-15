# Queue routediag-smoke 2026-11-07

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/routediag-smoke-2026-11-07.jsonl`.

## 2026-11-07T09:13 UTC, build f7d3b426, jobs bp-routediag-smoke

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-routediag-smoke | 09:13 (17:13) | 0.1 | **FAIL** | FAIL: UNKNOWN: no ts3native.log copy, log rules not checked<br>FAIL: events not fired: log /route diag: raw blocker capture on/ 0 lines (want >= 1); log /\[RouteDiag\] fail / 0 lines (want >= 1); log /\[RouteDiag\] raw follower/ 0 lines (want >= 1); log /\[RouteDiag\] footprints at/ 0 lines (want >= 1); log /\[RouteDiag\] queues \(suspect b\)/ 0 lines (want >= 1); log /\[RouteDiag\] cuts in the la | - | ? -> ? | - | timing: invalid (job FAIL: FAIL: UNKNOWN: no ts3native.log copy, log rules not checked; FAIL: events not fired: log /route diag: raw blocker captur; noise unknown; correctness-only tool ProbeSnap.CallSites); build: asi 1d4b92e4b303 not in DB ts3native ?; ts3fixes tuning 5716accd026a imp1105-fixes/imp1105b-fixes/ts3fixes.package.routefail1/ts3fixes.package.routefail2/ts3fixes.package.v029b/ts3fixes |

## 2026-11-07T09:26 UTC, build 30bc74c3, jobs bp-routediag-smoke

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-routediag-smoke | 09:26 (17:26) | 2.7 | **FAIL** | bp-route-fail-active: PASS (expected: watchdog_route_fail_sims >= 1, then watchdog_route_fails +1 once 30 sim-minutes have passed since the first failure: recent shows '<active Sim> -> watchdog reset (route plans failing)' and the Sim stands on a routable spot; with --set watchdogidle=false it stays stranded)<br>FAIL: events not fired: log /\[RouteDiag\] fail / 0 lines (want >= 1); log /\[RouteDia | 0 | 2156.3/1843/1588.9 -> 2183/1913.7/1574.2 | - | timing: invalid (job FAIL: FAIL: events not fired: log /\[RouteDiag\] fail / 0 lines (want >= 1); log /\[RouteDiag\] raw follower/ 0 lines (want >=; noise medium; correctness-only tool ProbeSnap.CallSites); build: asi 1d4b92e4b303 not in DB ts3native 0.36.168c-diag1 (log ok); ts3fixes tuning 5716accd026a imp1105-fixes/imp1105b-fixes/ts3fixes.package.routefail1/ts3fixes.package.routefail2/ts3fixes. |

## 2026-11-07T09:32 UTC, build fd191851, jobs bp-routediag-smoke2

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-routediag-smoke2 | 09:32 (17:32) | 3.8 | PASS | routediag-result16: PASS: steps done (no watch) (expected: [RouteDiag] fail with dest and RoutingSlot_0 lines, regions at failure wrap on with one region owner gone near, verdict #, verdict e, probes, recovered at region-gone.)<br>PASS: repro events fired | 0 | 2126/1850.3/1610.9 -> 2167.7/1934.3/1553.3 | - | timing: invalid (noise medium; correctness-only tool ProbeSnap.CallSites); build: asi 1d4b92e4b303 not in DB ts3native 0.36.168c-diag1 (log ok); ts3fixes tuning 5716accd026a imp1105-fixes/imp1105b-fixes/ts3fixes.package.routefail1/ts3fixes.package.routefail2/ts3fixes.package.v029b/ts3fixes.0.2.9 (expected game-copy/ts3fixes.package.diag1); noise: medium (other apps 17.0% of game CPUs); others on g |

## 2026-11-07T10:16 UTC, build ff506b46, jobs bp-routediag-diag2-smoke

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-routediag-diag2-smoke | 10:16 (18:16) | 8.4 | **FAIL** | routediag-result16: PASS: steps done (no watch) (expected: [RouteDiag] fail with dest and RoutingSlot_0 lines, regions at failure wrap on with one region owner gone near, verdict #, verdict e, probes, recovered at region-gone.)<br>FAIL: events not fired: log /\[RouteDiag\] onset first state/ 0 lines (want >= 1) | 0 | 2131.5/1830.4/1612.9 -> 2218.1/2046.4/1446.2 | - | timing: invalid (job FAIL: FAIL: events not fired: log /\[RouteDiag\] onset first state/ 0 lines (want >= 1); noise medium; correctness-only tool ProbeSnap.CallSites); build: asi c71dd118821f not in DB ts3native 0.36.168c-diag2 (log ok); ts3fixes tuning 5716accd026a imp1105-fixes/imp1105b-fixes/ts3fixes.package.routefail1/ts3fixes.package.routefail2/ts3fixes.package.v029b/ts3fixes.0.2.9 (expected  |

## 2026-11-07T10:30 UTC, build 2727981e, jobs bp-routediag-diag2-smoke2

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-routediag-diag2-smoke2 | 10:30 (18:30) | 8.4 | **FAIL** | routediag-result16: PASS: steps done (no watch) (expected: [RouteDiag] fail with dest and RoutingSlot_0 lines, regions at failure wrap on with one region owner gone near, verdict #, verdict e, probes, recovered at region-gone.)<br>FAIL: events not fired: log /\[RouteDiag\] raw #0 .*leaf content.* hex / 0 lines (want >= 1) | 0 | 2124.1/1841.7/1608.5 -> 2230.2/1947.2/1546.5 | - | timing: invalid (job FAIL: FAIL: events not fired: log /\[RouteDiag\] raw #0 .*leaf content.* hex / 0 lines (want >= 1); noise medium; correctness-only tool ProbeSnap.CallSites); build: asi 50cd3b664a12 not in DB ts3native 0.36.168c-diag2 (log ok); ts3fixes tuning 5716accd026a imp1105-fixes/imp1105b-fixes/ts3fixes.package.routefail1/ts3fixes.package.routefail2/ts3fixes.package.v029b/ts3fixes.0.2.9 |
