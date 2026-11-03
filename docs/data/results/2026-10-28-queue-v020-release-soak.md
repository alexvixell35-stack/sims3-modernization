# Queue v020-release-soak 2026-10-28

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/v020-release-soak-2026-10-28.jsonl`.

## 2026-10-28T15:51 UTC, build 2a914997, jobs sv-v020rel-soak60

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| sv-v020rel-soak60 | 15:51 (23:51) | 46.8 | PASS | PASS: profile 45 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->25, owspread_types_run 0->500, stuckcheckasync_queued 0->2020, stuckcheckasync_run 0->2020, etspread_passes 0->1, stuckcheck_noplan 0->1897, stuckcheck_notstuck 0->101, stuckcheck_rearmed 0->26, stuckcheck_reset 0->1 | 0 | 2159.8/1793.5/1580.9 -> 2223.1/2005.5/1371.4 | asyncStarted +29537, asyncRefused +791, asyncCollected +29465, asyncDropped +65, asyncUs +244834377, asyncMaxUs +323459, asyncPumpUs +149093588, asyncPumpWaits +24107, asyncRefusedM1Pending +186, asyncSkipped +7, asyncBackoffs +15, asyncQueueMax +5 | timing: invalid (noise medium); build: asi ac313272ebb6 ts3native.0.36.105.rel020 ts3native 0.36.105 (log ok); ts3fixes tuning 95963744a24e ts3fixes.0.2.0 (expected build/ts3fixes.package); noise: medium (other apps 4.9% of game CPUs); others on game CPUs 4.9%; clock avg/min 4195/4139 MHz; v0.2.0 release build, 60 min SV soak, light tools only; pairs with release-015-extra sv-soak-rel015 (same sav |
