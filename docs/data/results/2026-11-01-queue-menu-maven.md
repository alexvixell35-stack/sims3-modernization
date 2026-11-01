# Queue menu-maven 2026-11-01

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/menu-maven-2026-11-01.jsonl`.

## 2026-11-01T00:55 UTC, build 4f60f1d, jobs bp-menumaven-check

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-menumaven-check | 00:55 (08:55) | 0 | **FAIL** | cycle.ts --restart exit 1 | - | ? -> ? | - | timing: invalid (job FAIL: exit code ?; noise unknown); Correctness (5 min): repro menumaven-all-recipes-check. Pass: off IsMenuMaven True at the tuned count; on False at the tuned count, True with every recipe that counts, False after one is removed |

Stopped: bp-menumaven-check failed (stopQueueOnFail): cycle.ts --restart exit 1.

## 2026-11-01T00:55 UTC, build 4f60f1d, jobs bp-menumaven-check

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-menumaven-check | 00:55 (08:55) | 8.2 | PASS | PASS: profile 5 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->3, owspread_types_run 0->60, stuckcheckasync_queued 0->146, stuckcheckasync_run 0->146, etspread_passes 0->1, stuckcheck_noplan 0->131, stuckcheck_notstuck 0->6, stuckcheck_rearmed 0->8, stuckcheck_reset 0->2 | 0 | 2461.6/2196.7/1270.1 -> 2834.8/2560/844.8 | - | timing: invalid (job VOID: runInvalid: noisy machine (clock fell to 135% from 171%); noise high); build: asi 076fd2d4655e ts3native.asi.rel024 ts3native 0.36.105 (log ok); ts3fixes tuning 61761439fdb4 not in DB (expected build/ts3fixes.package); noise: high (NOT comparable (load or clock)); others on game CPUs 0.7%; clock avg/min 4151/3017 MHz; Correctness (5 min): repro menumaven-all-recipes-chec |
