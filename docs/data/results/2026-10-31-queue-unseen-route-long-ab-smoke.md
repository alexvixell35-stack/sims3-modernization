# Queue unseen-route-long-ab-smoke 2026-10-31

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/unseen-route-long-ab-smoke-2026-10-31.jsonl`.

## 2026-10-31T23:40 UTC, build bcad6835, jobs ip-ur-long-ab-1-smoke

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| ip-ur-long-ab-1-smoke | 23:40 (07:40) | 11.7 | PASS | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: unseenfbtrip_runs 0->350, unseenfb_cut 0->7, unseenfbpre_shortcuts 0->10, unseenfbpre_rj_colour 0->1, unseenfbpre_rj_task 0->50, unseenfbpre_holds 0->10, unseenfb_seen 4->1338, unseenfbpre_rj_caller 0->357, unseenfbpre_rj_townie 0->12, unseenfb_stuck_deferred 0->2410, unseenfbpre_rj_short 0->3461, unseenfbarrive_no_spot 0->1,  | 0 | 2145.6/1856.9/1564.9 -> 2149.4/1883.8/1539.3 | asyncStarted +6360, asyncRefused +115, asyncCollected +6349, asyncDropped +11, asyncUs +70777032, asyncMaxUs +396194, asyncPumpUs +50041240, asyncPumpWaits +4943, asyncRefusedM1Pending +33, asyncBackoffs +5, asyncQueueMax +5 | timing: invalid (noise medium); build: asi 27edd9af1a45 unseenroute2-asi ts3native 0.36.150 (log ok); ts3fixes tuning 0a13b5ab834b unseenroute7 (expected game-copy/ts3fixes.package.unseenroute7); noise: medium (other apps 12.9% of game CPUs); others on game CPUs 12.9%; clock avg/min 4211/4163 MHz; TIMING (hitches), IP natural play 30 min (autopilot, camera untouched, seed 12345), in-load --ab unse |
