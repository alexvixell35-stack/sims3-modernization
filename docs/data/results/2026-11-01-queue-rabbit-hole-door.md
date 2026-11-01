# Queue rabbit-hole-door 2026-11-01

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/rabbit-hole-door-2026-11-01.jsonl`.

## 2026-11-01T09:56 UTC, build 8d454cb, jobs bp-rabbit-hole-door-check

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-rabbit-hole-door-check | 09:56 (17:56) | 8.1 | PASS | PASS: profile 5 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->3, owspread_types_run 0->60, stuckcheckasync_queued 0->135, stuckcheckasync_run 0->135, etspread_passes 0->1, stuckcheck_noplan 0->123, stuckcheck_notstuck 0->8, stuckcheck_rearmed 0->4, stuckcheck_reset 0->1 | 0 | 2323.8/2128.9/1361.6 -> 2596.2/2491.1/934.4 | - | timing: invalid (job VOID: runInvalid: noisy machine (clock fell to 129% from 162%); noise medium); build: asi 076fd2d4655e ts3native.asi.rel024 ts3native 0.36.105 (log ok); ts3fixes tuning f7b528885469 not in DB (expected build/ts3fixes.package); noise: medium (GPU 3D others 24.3%); others on game CPUs 1.2%; clock avg/min 4326/3786 MHz; Correctness (5 min, Bridgeport test save): repro rabbit-hole |
