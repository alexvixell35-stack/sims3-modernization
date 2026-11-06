# Queue v028-verify 2026-11-06

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/v028-verify-2026-11-06.jsonl`.

## 2026-11-06T00:04 UTC, build 51eb1159, jobs bp-v028-smoke, ip-v028-smoke, sv-v028-smoke, ip-v028-lotgridmiss-save

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|

## 2026-11-06T00:23 UTC, build 51eb1159, jobs bp-v028-smoke, ip-v028-smoke, sv-v028-smoke, ip-v028-lotgridmiss-save

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|

## 2026-11-06T00:37 UTC, build 2f9253a4, jobs bp-v028-smoke, ip-v028-smoke, sv-v028-smoke, ip-v028-lotgridmiss-save

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-v028-smoke | 00:37 (08:37) | 6.7 | PASS | PASS: profile 5 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->2, owspread_types_run 0->40, stuckcheckasync_queued 0->133, stuckcheckasync_run 0->133, etspread_passes 0->1, stuckcheck_noplan 0->100, stuckcheck_notstuck 0->12, stuckcheck_rearmed 0->19, stuckcheck_reset 0->7<br>PASS: repro events fired | 0 | 2119.4/1827.4/1564.9 -> 2237.9/2039.8/1355.3 | - | timing: invalid (noise medium); build: asi 9ea87fe95217 not in DB ts3native 0.36.169 (log ok); ts3fixes tuning 10b027b92d63 rel025d-fixes/rel025d-ph-fixes/rel025e-fixes/ts3fixes.0.2.6 (expected game-copy/asi-v028/ts3fixes.package); noise: medium (other apps 23.6% of game CPUs); others on game CPUs 23.6%; clock avg/min 4181/4162 MHz; Bridgeport 5 min |
| ip-v028-smoke | 00:43 (08:43) | 6.8 | PASS | PASS: profile 5 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->2, owspread_types_run 0->40, stuckcheckasync_queued 0->154, stuckcheckasync_run 0->154, etspread_passes 0->1, stuckcheck_noplan 0->106, stuckcheck_rearmed 0->34, stuckcheck_reset 0->14<br>PASS: repro events fired | 0 | 2143.3/1850.4/1596.9 -> 2139.5/1866.1/1579.3 | - | timing: invalid (noise medium); build: asi 9ea87fe95217 not in DB ts3native 0.36.169 (log ok); ts3fixes tuning 10b027b92d63 rel025d-fixes/rel025d-ph-fixes/rel025e-fixes/ts3fixes.0.2.6 (expected game-copy/asi-v028/ts3fixes.package); noise: medium (other apps 21.3% of game CPUs); others on game CPUs 21.3%; clock avg/min 4185/4124 MHz; Isla Paradiso 5 min |
| sv-v028-smoke | 00:50 (08:50) | 6.7 | PASS | PASS: profile 5 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->2, owspread_types_run 0->40, stuckcheckasync_queued 0->156, stuckcheckasync_run 0->156, etspread_passes 0->1, stuckcheck_noplan 0->143, stuckcheck_notstuck 0->10, stuckcheck_rearmed 0->4<br>PASS: repro events fired | 0 | 2094/1775.2/1596.9 -> 2140/1816.9/1547.3 | - | timing: invalid (job VOID: runInvalid: noisy machine (others 31.2% of game CPUs); noise high); build: asi 9ea87fe95217 not in DB ts3native 0.36.169 (log ok); ts3fixes tuning 10b027b92d63 rel025d-fixes/rel025d-ph-fixes/rel025e-fixes/ts3fixes.0.2.6 (expected game-copy/asi-v028/ts3fixes.package); noise: high (other apps 26.8% of game CPUs); others on game CPUs 26.8%; clock avg/min 4182/3986 MHz; Suns |
| ip-v028-lotgridmiss-save | 00:57 (08:57) | 7 | **FAIL** | PASS: profile 5 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->3, owspread_types_run 0->60, stuckcheckasync_queued 0->187, stuckcheckasync_run 0->187, etspread_passes 1->1, stuckcheck_noplan 0->133, stuckcheck_notstuck 0->1, stuckcheck_rearmed 0->37, stuckcheck_reset 0->16<br>FAIL: events not fired: log /^save: result 0 / 2 lines (want >= 3)<br>PASS: no forbidden log line | 0 | 2102.2/1836.2/1628.9 -> 2251.5/1988.3/1355.4 | - | timing: invalid (job VOID: runInvalid: noisy machine (others 25.5% of game CPUs); noise high); build: asi 9ea87fe95217 not in DB ts3native 0.36.169 (log ok); ts3fixes tuning 10b027b92d63 rel025d-fixes/rel025d-ph-fixes/rel025e-fixes/ts3fixes.0.2.6 (expected game-copy/asi-v028/ts3fixes.package); noise: high (other apps 28.1% of game CPUs); others on game CPUs 28.1%; clock avg/min 4128/3955 MHz; [for |

Stopped: ip-v028-lotgridmiss-save failed (stopQueueOnFail): FAIL: events not fired: log /^save: result 0 / 2 lines (want >= 3).
