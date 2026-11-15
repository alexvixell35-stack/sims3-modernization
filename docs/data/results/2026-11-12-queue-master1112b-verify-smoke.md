# Queue master1112b-verify-smoke 2026-11-12

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/master1112b-verify-smoke-2026-11-12.jsonl`.

## 2026-11-12T13:45 UTC, build 527554a9, jobs smoke

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| smoke | 13:45 (21:45) | 6.7 | PASS | PASS: profile 5 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: unseenfbteleport_shortcuts 0->4, unseenfbteleport_no_spot 0->47, owchain_alarms 0->2, owspread_types_run 0->40, stuckcheckasync_queued 0->154, stuckcheckasync_run 0->154, etspread_passes 0->1, stuckcheck_noplan 0->120, stuckcheck_notstuck 0->17, stuckcheck_rearmed 0->15, stuckcheck_reset 0->4<br>PASS: repro events fired | 0 | 2170.5/1886.1/1584.9 -> 2253.5/2108.7/1341 | asyncStarted +9727, asyncRefused +130, asyncCollected +9706, asyncDropped +20, asyncUs +85601367, asyncMaxUs +287522, asyncPumpUs +11980683, asyncPumpWaits +9508, asyncRefusedM1Pending +43, asyncSkipped +1, asyncBackoffs +44, asyncQueueMax +17 | timing: invalid (noise medium); build: asi 1017eee08f92 not in DB ts3native 0.36.248 (log ok); ts3fixes tuning 30b6927ec440 not in DB (expected game-copy/ts3fixes.package.master1112b); noise: medium (other apps 12.9% of game CPUs); others on game CPUs 12.9%; clock avg/min 4214/4155 MHz; measurement smoke (AGENTS.md): BP 5 min, --ab equalsfast,gvunseen 1-min slices: version line, equals fast OK, Un |
