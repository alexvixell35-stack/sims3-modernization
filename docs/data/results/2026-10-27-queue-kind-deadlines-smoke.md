# Queue kind-deadlines-smoke 2026-10-27

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/kind-deadlines-smoke-2026-10-27.jsonl`.

## 2026-10-27T06:35 UTC, build 4cfe8284, jobs smoke

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| smoke | 06:35 (14:35) | 11.6 | PASS | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->5, owspread_types_run 0->100, stuckcheckasync_queued 0->494, stuckcheckasync_run 0->494, etspread_passes 0->1, stuckcheck_noplan 0->56, stuckcheck_notstuck 0->203, stuckcheck_rearmed 0->182, stuckcheck_reset 0->53<br>PASS: repro events fired | 0 | 2248.1/1849.4/1500.9 -> 2185.1/1861.2/1516.9 | asyncStarted +5826, asyncRefused +128, asyncCollected +5806, asyncDropped +20, asyncUs +66634415, asyncMaxUs +390900, asyncPumpUs +46652086, asyncPumpWaits +4889, asyncRefusedM1Pending +30, asyncBackoffs +5, asyncQueueFull +3, asyncQueueMax +8 | timing: valid-single; build: asi df445a71d641 not in DB ts3native 0.36.104 (log ok); ts3fixes tuning 8b324168a202 not in DB (expected build/ts3fixes.package); noise: low; others on game CPUs 2.8%; clock avg/min 4235/4199 MHz; Measurement smoke: IP 10 min on the kinddl asi with KindDeadlines=1 and the pair's light tools (checks the measurement path and that the switch installs: kdReady 1). |
