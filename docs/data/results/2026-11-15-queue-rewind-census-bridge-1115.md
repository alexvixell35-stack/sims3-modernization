# Queue rewind-census-bridge-1115 2026-11-15

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/rewind-census-bridge-1115-2026-11-15.jsonl`.

## 2026-11-15T13:04 UTC, build ed4d0e00, jobs ip-rewind-census-bridge

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| ip-rewind-census-bridge | 13:04 (21:04) | 11.9 | **FAIL** | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->4, owspread_types_run 0->80, stuckcheckasync_queued 0->301, stuckcheckasync_run 0->301, etspread_passes 0->1, stuckcheck_noplan 0->210, stuckcheck_notstuck 0->3, stuckcheck_rearmed 0->60, stuckcheck_reset 0->29<br>PASS: repro events fired<br>FAIL: 2 new script errors (max 0, 1 known EA ignored): ScriptError_L | 0 | 1667.3/1583.5/1821.3 -> 1791.8/1710.9/1776.8 | - | timing: invalid (job VOID: runInvalid: noisy machine (other CPU 18.4% >= 10%); noise high; heavy tool samplers); build: asi b75b706ac7c9 ts3native.asi.master-merge-1115 ts3native 0.36.261-lanes1 (log ok); ts3fixes tuning abdce0a6fef1 ts3fixes.package.master-dalexlol-405c-1115/ts3fixes.package.master-dalexlol-405d-1115/ts3fixes.package.master-merge-1115 (expected game-copy/ts3fixes.package.master-m |

Stopped: ip-rewind-census-bridge failed (stopQueueOnFail): FAIL: 2 new script errors (max 0, 1 known EA ignored): ScriptError_<pc>__11-15-2026__21-11-55__D7983392.xml, ScriptError_<pc>__11-15-2026__21-13-56__867FA881.xml.
