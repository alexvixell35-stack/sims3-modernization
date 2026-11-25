# Queue mono-measure-memgaps-1115-mm 2026-11-16

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/mono-measure-memgaps-1115-mm-2026-11-16.jsonl`.

## 2026-11-16T03:48 UTC, build dafc8ae3, jobs bp-m1-gcnat, sv-m1-gcnat

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-m1-gcnat | 03:48 (11:48) | 13.9 | PASS | PASS: profile 12 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->5, owspread_types_run 0->100, stuckcheckasync_queued 0->338, stuckcheckasync_run 0->338, etspread_passes 0->1, stuckcheck_noplan 0->290, stuckcheck_notstuck 0->27, stuckcheck_rearmed 0->31, stuckcheck_reset 0->8<br>PASS: repro events fired<br>PASS: no forbidden log lines<br>PASS: 0 new script errors (max 0) | 0 | 1747.7/1633.6/1817 -> 2087.1/2005/1566.7 | - | timing: invalid (noise medium); build: asi 7860ed4bde30 ts3native.asi.master-mono-1115 ts3native 0.36.264-mono1 (log ok); ts3fixes tuning f8853892e12c ts3fixes.package.master-dalexlol-417-1115/ts3fixes.package.l3-convgone-1115/ts3fixes.package.mono-autshadow-1115/ts3fixes.mono-offload-1115/ts3fixes.package.master-mono-1115 (expected game-copy/ts3fixes.package.master-mono-1115); noise: medium (othe |
| sv-m1-gcnat | 04:02 (12:02) | 1.1 | **CRASH** | FAIL: events not fired: log /^gcsize: gc=\d+ mode=\d live=[\d.]+ / 0 lines (want >= 20)<br>PASS: no forbidden log lines<br>PASS: 0 new script errors (max 0) | - | ? -> ? | - | timing: invalid (job FAIL: crash: xcpt <pc> 26-11-16 12.02.21.txt; FAIL: events not fired: log /^gcsize: gc=\d+ mode=\d live=[\d.]+ / 0 lines; noise high); build: asi 7860ed4bde30 ts3native.asi.master-mono-1115 ts3native 0.36.264-mono1; ts3fixes tuning f8853892e12c ts3fixes.package.master-dalexlol-417-1115/ts3fixes.package.l3-convgone-1115/ts3fixes.package.mono-autshadow-1115/ts3fixes.mono-o |
