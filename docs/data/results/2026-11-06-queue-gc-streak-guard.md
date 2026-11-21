# Queue gc-streak-guard 2026-11-06

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/gc-streak-guard-2026-11-06.jsonl`.

## 2026-11-06T21:04 UTC, build 41cfcd23, jobs bp-gc-streak-ab

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-gc-streak-ab | 21:04 (05:04) | 4.7 | PASS | PASS: profile 3 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->1, owspread_types_run 0->20, stuckcheckasync_queued 0->80, stuckcheckasync_run 0->80, etspread_passes 0->1, stuckcheck_noplan 0->69, stuckcheck_notstuck 0->4, stuckcheck_rearmed 0->7<br>PASS: repro events fired<br>PASS: 0 new script errors (max 0) | 0 | 2131/1839.1/1596.9 -> 3190/3011.4/430.1 | - | timing: invalid (noise medium); build: asi 189908e037d4 ts3native.asi.gcstreak1107 ts3native 0.36.181 (log ok); ts3fixes tuning a76b034dbe89 not in DB (expected build/ts3fixes.package); noise: medium (other apps 17.0% of game CPUs); others on game CPUs 17.0%; clock avg/min 4222/4215 MHz; Repro gc-streak-guard, off then on in one load (native_set gcstreakat). Pass: the native_check steps (off: gwEx |
