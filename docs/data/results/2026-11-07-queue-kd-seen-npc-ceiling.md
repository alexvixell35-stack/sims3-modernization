# Queue kd-seen-npc-ceiling 2026-11-07

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/kd-seen-npc-ceiling-2026-11-07.jsonl`.

## 2026-11-07T02:58 UTC, build 87df002b, jobs sv-kd-seen-npc-ceiling

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| sv-kd-seen-npc-ceiling | 02:58 (10:58) | 3.4 | **FAIL** | FAIL: profile 12 min, seed 12345 (rng-compare.ts)<br>FAIL: repro step sim_route_to failed: Ransom Clavell is not currently in the world (not instantiated)<br>ts3fixes activity: owchain_alarms 0->1, owspread_types_run 0->20, stuckcheckasync_queued 0->15, stuckcheckasync_run 0->15, etspread_passes 0->1, stuckcheck_noplan 0->10, stuckcheck_notstuck 0->4, stuckcheck_rearmed 0->1<br>PASS: repro events  | 1 | 2106.4/1792.1/1628.9 -> 2099.8/1951/1483.3 | asyncStarted +993, asyncRefused +31, asyncCollected +993, asyncUs +23721420, asyncMaxUs +470791, asyncPumpUs +3256886, asyncPumpWaits +2748, asyncRefusedM1Pending +2, asyncBackoffs +8, asyncQueueMax +6 | timing: invalid (job VOID: runInvalid: profiler cut short (98 of 720 s); crash or abort (no "done; game alive"); noise high; A/B 3 off / 3 on iterations, < 4 per arm (repro firings)); build: asi 82d7e02ed087 ts3native.asi.kdnpc ts3native 0.36.185 (log ok); ts3fixes tuning 3296fe74ed3e not in DB (expected build/ts3fixes.package); noise: high (other apps 21.3% of game CPUs); others on game CPUs 21.3 |

Stopped: sv-kd-seen-npc-ceiling failed (stopQueueOnFail): FAIL: profile 12 min, seed 12345 (rng-compare.ts).

## 2026-11-07T03:02 UTC, build 834bae68, jobs sv-kd-seen-npc-ceiling

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| sv-kd-seen-npc-ceiling | 03:02 (11:02) | 5.3 | PASS | PASS: profile stopped at 20 events after 2.1 min (off 10, on 10; 20 firings, 5 cycles; rule 10 events per arm), cap 12 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->2, owspread_types_run 0->40, stuckcheckasync_queued 0->100, stuckcheckasync_run 0->100, etspread_passes 0->1, stuckcheck_noplan 0->85, stuckcheck_notstuck 0->11, stuckcheck_rearmed 0->5<br>PASS: repro events  | 0 | 2111.7/1787.4/1644.9 -> 2128.9/1953.7/1471.1 | asyncStarted +2287, asyncRefused +72, asyncCollected +2287, asyncUs +58088244, asyncMaxUs +435603, asyncPumpUs +7158128, asyncPumpWaits +5526, asyncRefusedM1Pending +5, asyncBackoffs +16, asyncQueueMax +7 | timing: invalid (noise medium); build: asi 82d7e02ed087 ts3native.asi.kdnpc ts3native 0.36.185 (log ok); ts3fixes tuning 3296fe74ed3e not in DB (expected build/ts3fixes.package); noise: medium (other apps 19.3% of game CPUs); others on game CPUs 19.3%; clock avg/min 4146/4106 MHz; Correctness, forced ABBA on kd-seen-npc-ceiling with the NPC ceiling test-lowered to 100 ms. Pass: no crash, 0 watchdo |
