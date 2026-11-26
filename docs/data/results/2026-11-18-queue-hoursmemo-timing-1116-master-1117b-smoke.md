# Queue hoursmemo-timing-1116-master-1117b-smoke 2026-11-18

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/hoursmemo-timing-1116-master-1117b-smoke-2026-11-18.jsonl`.

## 2026-11-18T02:43 UTC, build cb4272888, jobs bp-hoursmemo-ab-smoke

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-hoursmemo-ab-smoke | 02:43 (10:43) | 6 | PASS | PASS: profile 4 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: hoursmemo_hits 0->32379, hoursmemo_misses 0->96843, hoursmemo_barhits 0->24665, owchain_alarms 0->2, owspread_types_run 0->40, stuckcheckasync_queued 0->108, stuckcheckasync_run 0->108, etspread_passes 0->1, stuckcheck_noplan 0->84, stuckcheck_notstuck 0->12, stuckcheck_rearmed 0->14, stuckcheck_reset 0->1 [cut: full row in the jsonl] | 0 | 1763.3/1646/1801.8 -> 1926.4/1850.6/1633.3 | - | timing: invalid (noise medium; A/B 1 off / 2 on iterations, < 4 per arm); build: asi dd4e4c13902e ts3native.asi.master-1117b ts3native 0.36.269-mb1117 (log ok); ts3fixes tuning 8ae32451d8bc ts3fixes.package.master-1117b (expected game-copy/ts3fixes.package.master-1117b); noise: medium (other apps 22.0% of game CPUs); others on game CPUs 22.0%; clock avg/min 4140/4117 MHz; Measurement smoke 4 min [cut: full row in the jsonl]<br>sim ms/s off 159.1 / on 198.4 (on - off +39.4, +24.7%); off: p99 50 ms, p99.9 109, max 127, >=300 0, >=1000 0 (0.6 min); on: p99 90 ms, p99.9 139, max 214, >=300 0, >=1000 0 (1.1 min) |

A/B summary of this run (sim ms/s = script + route + animPose + locomotion, arm-ms.ts; hitches per 10 min over the same arms, hitch-ab.ts):

- hoursmemo-timing-1116-master-1117b-smoke-bp-hoursmemo-ab-smoke: sim ms/s off 159.1 / on 198.4, on - off +39.4 (+24.7%); >=300 ms per 10 min off 0.0 / on 0.0, >=1000 off 0.0 / on 0.0 (1 off, 1 on run)
