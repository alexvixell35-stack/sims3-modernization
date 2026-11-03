# Queue ufb-crash-control 2026-10-26

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/ufb-crash-control-2026-10-26.jsonl`.

## 2026-10-26T08:37 UTC, build 5adf437e, jobs sv-ufb-farcrowd-loop-offctl

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| sv-ufb-farcrowd-loop-offctl | 08:37 (16:37) | 14.5 | PASS | PASS: profile loop cap reached: 0 events after 9.9 min, rule not met (off 0, on 0; 12 firings, 3 cycles; rule 1000 events), cap 14 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->6, owspread_types_run 0->120, stuckcheckasync_queued 0->872, stuckcheckasync_run 0->870, etspread_passes 0->1, stuckcheckcap_marked 0->0, stuckcheck_noplan 0->107, stuckcheck_notstuck 0->539, stuc | 0 | 2111.6/1810.8/1548.9 -> 2165.3/2007.7/1337.8 | asyncStarted +7242, asyncRefused +1064, asyncCollected +7239, asyncDropped +3, asyncUs +62164514, asyncMaxUs +189913, asyncPumpUs +36328289, asyncPumpWaits +6792, asyncRefusedM1Pending +50, asyncBackoffs +6, asyncQueueFull +807, asyncQueueMax +8 | timing: invalid (noise medium; correctness-only tool GcThreadCheck; A/B 12 off / 0 on iterations, < 4 per arm (repro firings)); build: asi bc78c1d94849 not in DB ts3native 0.36.92 (log ok); ts3fixes tuning fa48a92ba45f not in DB (expected build/ts3fixes.package); noise: medium (other apps 14.4% of game CPUs); others on game CPUs 14.4%; clock avg/min 4180/4122 MHz; Crash control (docs/67 §22.1): th |
