# Queue bake-sale 2026-10-31

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/bake-sale-2026-10-31.jsonl`.

## 2026-10-31T23:21 UTC, build d1644be, jobs bp-bake-sale-check

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-bake-sale-check | 23:21 (07:21) | 8.1 | PASS | PASS: profile 5 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->3, owspread_types_run 0->60, stuckcheckasync_queued 0->139, stuckcheckasync_run 0->138, etspread_passes 0->1, stuckcheck_noplan 0->135, stuckcheck_notstuck 0->4, stuckcheck_rearmed 0->3 | 0 | 2363/2246.3/1208.3 -> 2562.6/2401.6/973.8 | - | timing: invalid (noise high); build: asi 076fd2d4655e ts3native.asi.rel024 ts3native 0.36.105 (log ok); ts3fixes tuning dba9cc6282df not in DB (expected build/ts3fixes.package); noise: high (GPU 3D others 33.5%); others on game CPUs 0.3%; clock avg/min 4360/3987 MHz; Correctness (5 min): repro bake-sale-dead-seller-check. Pass: off HasSeller True with the deleted seller, handler threw NullReferenc |

## 2026-10-31T23:31 UTC, build d1644be, jobs bp-bake-sale-check

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-bake-sale-check | 23:31 (07:31) | 8.1 | PASS | PASS: profile 5 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->3, owspread_types_run 0->60, stuckcheckasync_queued 0->132, stuckcheckasync_run 0->131, etspread_passes 0->1, stuckcheck_noplan 0->127, stuckcheck_notstuck 0->6, stuckcheck_rearmed 0->2 | 0 | 2349.3/2092.1/1358.4 -> 2660.3/2493.4/912 | - | timing: invalid (job VOID: runInvalid: noisy machine (clock fell to 91% from 168%); noise high); build: asi 076fd2d4655e ts3native.asi.rel024 ts3native 0.36.105 (log ok); ts3fixes tuning dba9cc6282df not in DB (expected build/ts3fixes.package); noise: high (NOT comparable (load or clock)); others on game CPUs 1.0%; clock avg/min 4262/3198 MHz; Correctness (5 min): repro bake-sale-dead-seller-check |
