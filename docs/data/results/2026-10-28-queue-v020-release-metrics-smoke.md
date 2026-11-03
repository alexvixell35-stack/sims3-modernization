# Queue v020-release-metrics-smoke 2026-10-28

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/v020-release-metrics-smoke-2026-10-28.jsonl`.

## 2026-10-28T12:20 UTC, build e4677a4c, jobs bp-v020rel-1-smoke

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-v020rel-1-smoke | 12:20 (20:20) | 11.7 | PASS | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->5, owspread_types_run 0->100, stuckcheckasync_queued 0->252, stuckcheckasync_run 0->252, etspread_passes 0->1, stuckcheck_noplan 0->219, stuckcheck_notstuck 0->25, stuckcheck_rearmed 0->14, stuckcheck_reset 0->3 | 0 | 2202.5/1856.3/1532.8 -> 2330.4/2092.9/1283.3 | asyncStarted +18365, asyncRefused +207, asyncCollected +18359, asyncDropped +5, asyncUs +81246600, asyncMaxUs +189629, asyncPumpUs +41924445, asyncPumpWaits +11193, asyncRefusedM1Pending +49, asyncBackoffs +8, asyncQueueMax +5 | timing: invalid (noise medium); build: asi ac313272ebb6 ts3native.0.36.105.rel020 ts3native 0.36.105 (log ok); ts3fixes tuning 95963744a24e ts3fixes.0.2.0 (expected build/ts3fixes.package); noise: medium (other apps 6.4% of game CPUs); others on game CPUs 6.4%; clock avg/min 4195/4129 MHz; v0.2.0 release config (asi rel020, repo ini = release defaults, ts3fixes rel020 tunables) 15 min natural play |
