# Queue hoursmemo-timing-1118-rerun1-smoke 2026-11-19

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/hoursmemo-timing-1118-rerun1-smoke-2026-11-19.jsonl`.

## 2026-11-19T02:47 UTC, build 8d2869457, jobs bp-hoursmemo-ab-smoke

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-hoursmemo-ab-smoke | 02:47 (10:47) | 6 | PASS | PASS: profile 4 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: hoursmemo_hits 0->35106, hoursmemo_misses 0->89078, hoursmemo_barhits 0->27038, owchain_alarms 0->1, owspread_types_run 0->20, stuckcheckasync_queued 0->80, stuckcheckasync_run 0->80, etspread_passes 0->1, stuckcheck_noplan 0->64, stuckcheck_notstuck 0->3, stuckcheck_rearmed 0->10, stuckcheck_reset 0->4 [cut: full row in the jsonl] | 0 | 1751/1627.5/1806.4 -> 1924.5/1842.5/1600.1 | - | timing: invalid (noise medium; A/B 1 off / 2 on iterations, < 4 per arm); build: asi dd4e4c13902e ts3native.asi.master-1117b ts3native 0.36.269-mb1117 (log ok); ts3fixes tuning 8ae32451d8bc ts3fixes.package.master-1117b (expected game-copy/ts3fixes.package.master-1117b); noise: medium (other apps 27.7% of game CPUs); others on game CPUs 27.7%; clock avg/min 4129/4034 MHz; Measurement smoke 4 min [cut: full row in the jsonl]<br>sim ms/s off 166.9 / on 204.2 (on - off +37.3, +22.4%); off: p99 55 ms, p99.9 101, max 123, >=300 0, >=1000 0 (0.6 min); on: p99 104 ms, p99.9 220, max 385, >=300 1, >=1000 0 (1.1 min) |

A/B summary of this run (sim ms/s = script + route + animPose + locomotion, arm-ms.ts; hitches per 10 min over the same arms, hitch-ab.ts):

- hoursmemo-timing-1118-rerun1-smoke-bp-hoursmemo-ab-smoke: sim ms/s off 166.9 / on 204.2, on - off +37.3 (+22.4%); >=300 ms per 10 min off 0.0 / on 9.1, >=1000 off 0.0 / on 0.0 (1 off, 1 on run)

## Bridge note (added 11-19 12:45 PHT)
chain-night-1118 steps 1-3 (this queue among them) ran on Apex 4.0.6 asi a1c121f3 + bridge pair 1ace01d9/faf22f07 (v4.0.6 + our 0015/0016). From step 4 (z1-m1-timing-1116-master-1117b, 12:43 PHT) the chain runs on the fork test install ours/test-integration-1119 a69a1bbc: asi a89fc5b4, pair da20b397/f2a58455 (probe-out/apex-latest-switch-1119.md). Do not compare timing across that point without a re-baseline.
