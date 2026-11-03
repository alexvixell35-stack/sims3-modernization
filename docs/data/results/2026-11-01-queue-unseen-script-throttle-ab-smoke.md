# Queue unseen-script-throttle-ab-smoke 2026-11-01

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/unseen-script-throttle-ab-smoke-2026-11-01.jsonl`.

## 2026-11-01T03:54 UTC, build 2ed35a54, jobs bp-unseen-share-smoke

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-unseen-share-smoke | 03:54 (11:54) | 11.7 | PASS | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->4, owspread_types_run 0->80, stuckcheckasync_queued 0->293, stuckcheckasync_run 0->293, etspread_passes 0->1, stuckcheck_noplan 0->253, stuckcheck_notstuck 0->24, stuckcheck_rearmed 0->20, stuckcheck_reset 0->7 | 0 | 2127.9/1837.7/1564.9 -> 2211.3/2072.1/1347.3 | - | timing: invalid (noise medium); build: asi 4f8d044696cb rel025a-asi ts3native 0.36.149 (log ok); ts3fixes tuning 2113f393f5d2 rel025a-fixes (expected game-copy/ts3fixes.package.rel025a); noise: medium (other apps 13.7% of game CPUs); others on game CPUs 13.7%; clock avg/min 4240/4183 MHz; Data first (docs/144 G-T0): BP play 10 min, unseenprobe on, every throttle off. Answers the unseen share of hi |
