# Queue wake-park-ab-smoke 2026-11-01

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/wake-park-ab-smoke-2026-11-01.jsonl`.

## 2026-11-01T06:53 UTC, build e72b7640, jobs smoke

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| smoke | 06:53 (14:53) | 5.7 | PASS | PASS: profile 4 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: stuckcheckasync_maxwait_simsec 0->48, stuckcheckasync_queued 0->74, idlememo_skipped 0->3, stuckcheckasync_run 0->74, idlememo_capped 0->144, stuckcheckasync_maxdepth 0->2, bouncerskip_skipped 0->2722, owchain_alarms 0->1, owspread_types_run 0->20, etspread_passes 0->1, stuckcheck_noplan 0->59, stuckcheck_notstuck 0->8, stuckch | 0 | 2128.1/1841.6/1564.9 -> 2184.4/2045.2/1363.3 | asyncStarted +5272, asyncRefused +100, asyncCollected +5272, asyncUs +32789370, asyncMaxUs +173350, asyncPumpUs +15236980, asyncPumpWaits +3484, asyncRefusedM1Pending +18, asyncBackoffs +4, asyncQueueMax +4 | timing: invalid (noise medium; A/B 1 off / 2 on iterations, < 4 per arm); build: asi 4f8d044696cb rel025a-asi ts3native 0.36.149 (log ok); ts3fixes tuning a0e1dc9b078c not in DB (expected game-copy/ts3fixes.package.kit5-wakehooks); noise: medium (other apps 14.6% of game CPUs); others on game CPUs 14.6%; clock avg/min 4229/4187 MHz; Measurement smoke (AGENTS.md): BP Spring 4 min, wakeprobe on, --a |

## 2026-11-01T08:48 UTC, build f429b231, jobs smoke

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| smoke | 08:48 (16:48) | 5.7 | PASS | PASS: profile 4 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: stuckcheckasync_maxwait_simsec 0->64, stuckcheckasync_queued 0->76, idlememo_skipped 0->3, stuckcheckasync_run 0->76, idlememo_capped 0->159, stuckcheckasync_maxdepth 0->2, bouncerskip_skipped 0->2560, owchain_alarms 0->1, owspread_types_run 0->20, etspread_passes 0->1, stuckcheck_noplan 0->62, stuckcheck_notstuck 0->4, stuckch | 0 | 2129.1/1838.4/1564.9 -> 2210.7/2044.6/1387.3 | asyncStarted +5022, asyncRefused +68, asyncCollected +5019, asyncDropped +1, asyncUs +31436137, asyncMaxUs +237054, asyncPumpUs +15070280, asyncPumpWaits +3506, asyncRefusedM1Pending +20, asyncBackoffs +5, asyncQueueMax +4 | timing: invalid (noise medium; A/B 1 off / 2 on iterations, < 4 per arm); build: asi 4f8d044696cb rel025a-asi ts3native 0.36.149 (log ok); ts3fixes tuning a0e1dc9b078c not in DB (expected game-copy/ts3fixes.package.kit5-wakehooks); noise: medium (other apps 14.7% of game CPUs); others on game CPUs 14.7%; clock avg/min 4227/4165 MHz; Measurement smoke (AGENTS.md): BP Spring 4 min, wakeprobe on, --a |
