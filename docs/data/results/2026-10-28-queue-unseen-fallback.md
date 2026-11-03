# Queue unseen-fallback 2026-10-28

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/unseen-fallback-2026-10-28.jsonl`.

## 2026-10-28T01:26 UTC, build e323b607, jobs sv-ufb-overwatch

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| sv-ufb-overwatch | 01:26 (09:26) | 11.7 | PASS | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: unseenfb_seen 0->1252, unseenfb_cut 0->1, unseenfb_marked 0->6422, unseenfbtrip_runs 0->249, unseenfb_max_ms 0->328, owchain_alarms 0->7, owspread_types_run 0->140, stuckcheckasync_queued 0->644, stuckcheckasync_run 0->642, etspread_passes 0->1, stuckcheck_noplan 0->57, stuckcheck_notstuck 0->419, stuckcheck_rearmed 0->156, st | 0 | 2117.2/1796.9/1564.9 -> 2228.6/2038.5/1371.4 | asyncStarted +6694, asyncRefused +492, asyncCollected +6686, asyncDropped +7, asyncUs +50254483, asyncMaxUs +341482, asyncPumpUs +30608913, asyncPumpWaits +5612, asyncRefusedM1Pending +38, asyncBackoffs +4, asyncQueueFull +245, asyncQueueMax +5 | timing: invalid (noise medium); build: asi df445a71d641 not in DB ts3native 0.36.104 (log ok); ts3fixes tuning 8b324168a202 not in DB (expected build/ts3fixes.package); noise: medium (other apps 4.1% of game CPUs); others on game CPUs 4.1%; clock avg/min 4220/4174 MHz; Stuck-detector compatibility (docs/202 §3.7): repro unseenfb-overwatch-cuts (20 far Sims, 12 rounds of cut plans home from one spo |

## 2026-10-28T17:31 UTC, build 686c1edb, jobs sv-ufb-stuck-loop

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| sv-ufb-stuck-loop | 17:31 (01:31) | 4.3 | PASS | PASS: profile stopped at 120 events after 1.4 min (off 60, on 60; 4 firings, 1 cycles; rule 60 events per arm), cap 12 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: unseenfb_seen 11->334, unseenfb_marked 0->1614, unseenfbpre_rj_townie 0->19, unseenfbpre_rj_task 0->36, unseenfb_stuck_seen 0->26, unseenfbpre_rj_vehicle 0->456, unseenfbpre_rj_short 0->955, unseenfbpre_rj_sim 0->144, unseenfb | 0 | 2155.3/1792.6/1564.9 -> 2154.1/1968.1/1387.3 | asyncStarted +2356, asyncRefused +71, asyncCollected +2356, asyncUs +19452832, asyncMaxUs +291656, asyncPumpUs +12587251, asyncPumpWaits +2259, asyncRefusedM1Pending +2, asyncBackoffs +3, asyncQueueMax +3 | timing: invalid (noise medium; A/B 2 off / 2 on iterations, < 4 per arm (repro firings)); build: asi df445a71d641 not in DB ts3native 0.36.104 (log ok); ts3fixes tuning 95963744a24e ts3fixes.0.2.0/ts3fixes.0.2.1 (expected build/ts3fixes.package); noise: medium (other apps 9.1% of game CPUs); others on game CPUs 9.1%; clock avg/min 4200/4160 MHz; Phase B correctness: forced ABBA on unseenfb-stuck-f |
