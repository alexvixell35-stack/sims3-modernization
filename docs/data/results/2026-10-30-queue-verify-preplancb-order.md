# Queue verify-preplancb-order 2026-10-30

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/verify-preplancb-order-2026-10-30.jsonl`.

## 2026-10-30T07:50 UTC, build 69c2d490, jobs bp-preplancb-order-verify

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-preplancb-order-verify | 07:50 (15:50) | 13.8 | PASS | PASS: profile 12 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->5, owspread_types_run 0->100, stuckcheckasync_queued 0->496, stuckcheckasync_run 0->473, etspread_passes 0->1, stuckcheck_noplan 0->81, stuckcheck_notstuck 0->232, stuckcheck_rearmed 0->139, stuckcheck_reset 0->28 | 0 | 2182.8/1856.1/1500.9 -> 2389.3/2120.4/1174.4 | - | timing: invalid (noise medium; heavy tool CrashForensics.Guards); build: asi 8c0e67e2b1b6 ts3native.asi.p6f ts3native 0.36.43 (log ok); ts3fixes tuning 94c60769ef8f not in DB (expected build/ts3fixes.package); noise: medium (other apps 5.8% of game CPUs); others on game CPUs 5.8%; clock avg/min 4215/4019 MHz; preplancb (docs/185 §5, §6.1): AboutToPlanCallback before the async START, locked-door li |
