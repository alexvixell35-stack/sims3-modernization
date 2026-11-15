# Queue turbo-apex-1114 2026-11-14

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/turbo-apex-1114-2026-11-14.jsonl`.

## 2026-11-14T02:23 UTC, build 5c1f6b1b, jobs ip-turbo-a1-apex

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| ip-turbo-a1-apex | 02:23 (10:23) | 11.7 | PASS | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->5, owspread_types_run 0->100, stuckcheckasync_queued 0->272, stuckcheckasync_run 0->272, etspread_passes 0->1, stuckcheck_noplan 0->213, stuckcheck_notstuck 0->2, stuckcheck_rearmed 0->41, stuckcheck_reset 0->17<br>PASS: repro events fired<br>PASS: no forbidden log lines<br>PASS: 0 new script errors (max 0, 2 | 0 | 1693.6/1581.3/1815.3 -> 1749.6/1661.1/1806 | - | timing: invalid (job VOID: runInvalid: noisy machine (others 26.9% of game CPUs); noise high; heavy tool samplers); build: asi 432ab1b2a191 ts3native.asi.rc1111g/ts3native.asi.rel0212 ts3native 0.36.247 (log ok); ts3fixes tuning 0373884c6b0f ts3fixes.package.rc1111e/ts3fixes.package.rel0211b (expected game-copy/ts3fixes.package.rc1111e); noise: high (other apps 26.0% of game CPUs); others on game  |

## 2026-11-14T02:34 UTC, build 5c1f6b1b, jobs ip-turbo-a2-apex-turbo

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| ip-turbo-a2-apex-turbo | 02:34 (10:34) | 5.1 | **FAIL** | FAIL: events not fired: log /^diag: world load #\d+, uptime/ 0 lines (want >= 1); log /^diag: loaded modules outside Windows .*\bApexRadiance\.asi v\d+\.\d+/ 0 lines (want >= 1); log /^diag: loaded modules outside Windows .*\bTurbo\.asi\b/ 0 lines (want >= 1)<br>PASS: no forbidden log lines<br>PASS: 0 new script errors (max 0) | - | ? -> ? | - | timing: invalid (job FAIL: FAIL: events not fired: log /^diag: world load #\d+, uptime/ 0 lines (want >= 1); log /^diag: loaded modules outside Win; noise medium; heavy tool samplers); build: asi 432ab1b2a191 ts3native.asi.rc1111g/ts3native.asi.rel0212 ts3native 0.36.247; ts3fixes tuning 0373884c6b0f ts3fixes.package.rc1111e/ts3fixes.package.rel0211b (expected game-copy/ts3fixes.package.rc1111e);  |

## 2026-11-14T02:40 UTC, build 5c1f6b1b, jobs ip-turbo-a5-apex-turbo-allon

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| ip-turbo-a5-apex-turbo-allon | 02:40 (10:40) | 5.1 | **FAIL** | FAIL: events not fired: log /^diag: world load #\d+, uptime/ 0 lines (want >= 1); log /^diag: loaded modules outside Windows .*\bApexRadiance\.asi v\d+\.\d+/ 0 lines (want >= 1); log /^diag: loaded modules outside Windows .*\bTurbo\.asi\b/ 0 lines (want >= 1)<br>PASS: no forbidden log lines<br>PASS: 0 new script errors (max 0) | - | ? -> ? | - | timing: invalid (job FAIL: FAIL: events not fired: log /^diag: world load #\d+, uptime/ 0 lines (want >= 1); log /^diag: loaded modules outside Win; noise medium; heavy tool samplers); build: asi 432ab1b2a191 ts3native.asi.rc1111g/ts3native.asi.rel0212 ts3native 0.36.247; ts3fixes tuning 0373884c6b0f ts3fixes.package.rc1111e/ts3fixes.package.rel0211b (expected game-copy/ts3fixes.package.rc1111e);  |

## 2026-11-14T02:45 UTC, build 5c1f6b1b, jobs ip-turbo-a3-apex-turbo-ual77

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| ip-turbo-a3-apex-turbo-ual77 | 02:45 (10:45) | 5.1 | **FAIL** | FAIL: events not fired: log /^diag: world load #\d+, uptime/ 0 lines (want >= 1); log /^diag: loaded modules outside Windows .*\bApexRadiance\.asi v\d+\.\d+/ 0 lines (want >= 1); log /^diag: loaded modules outside Windows .*\bTurbo\.asi\b/ 0 lines (want >= 1)<br>PASS: no forbidden log lines<br>PASS: 0 new script errors (max 0) | - | ? -> ? | - | timing: invalid (job FAIL: FAIL: events not fired: log /^diag: world load #\d+, uptime/ 0 lines (want >= 1); log /^diag: loaded modules outside Win; noise medium; heavy tool samplers); build: asi 432ab1b2a191 ts3native.asi.rc1111g/ts3native.asi.rel0212 ts3native 0.36.247; ts3fixes tuning 0373884c6b0f ts3fixes.package.rc1111e/ts3fixes.package.rel0211b (expected game-copy/ts3fixes.package.rc1111e);  |

## 2026-11-14T02:50 UTC, build 5c1f6b1b, jobs ip-turbo-a4-turbo-dxvk

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| ip-turbo-a4-turbo-dxvk | 02:50 (10:50) | 12.2 | PASS | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->4, owspread_types_run 0->80, stuckcheckasync_queued 0->245, stuckcheckasync_run 0->245, etspread_passes 0->1, stuckcheck_noplan 0->165, stuckcheck_notstuck 0->1, stuckcheck_rearmed 0->53, stuckcheck_reset 0->26<br>PASS: repro events fired<br>PASS: no forbidden log lines<br>PASS: 0 new script errors (max 0, 1  | 0 | 2189.8/1889.3/1576.8 -> 2178.9/1923.4/1547.3 | - | timing: invalid (job VOID: runInvalid: noisy machine (others 31.2% of game CPUs); noise high; heavy tool samplers); build: asi 432ab1b2a191 ts3native.asi.rc1111g/ts3native.asi.rel0212 ts3native 0.36.247 (log ok); ts3fixes tuning 0373884c6b0f ts3fixes.package.rc1111e/ts3fixes.package.rel0211b (expected game-copy/ts3fixes.package.rc1111e); noise: high (other apps 28.3% of game CPUs); others on game  |
