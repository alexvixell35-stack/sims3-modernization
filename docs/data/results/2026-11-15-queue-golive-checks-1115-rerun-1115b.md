# Queue golive-checks-1115-rerun-1115b 2026-11-15

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/golive-checks-1115-rerun-1115b-2026-11-15.jsonl`.

## 2026-11-15T10:40 UTC, build d6a8dbd6, jobs bp-golive-checks

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-golive-checks | 10:40 (18:40) | 33.4 | PASS | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: autunseen_offlist 0->167649, autshadow_skips 0->194457, g8_hh_simhours_x100 0->79177, g8_lat0 0->493, g8_lat2 0->164, g8_lat3 0->252, g8_lat4 0->3276, autshadow_guardskips 0->100, g8_len_seen_ticks 0->5581, g8_w_seen_n 0->562, g8_null_picks 0->6345, g8_seen_gohome 0->261, g8_unseen_null_picks 0->5845, g8_hh_searches 0->854, au | 0 | 2244/1897.8/1520.9 -> 2385.2/2195.9/1228.8 | asyncStarted +52218, asyncRefused +855, asyncCollected +52144, asyncDropped +11, asyncUs +777096252, asyncMaxUs +551869, asyncPumpUs +57022171, asyncPumpWaits +73979, asyncRefusedM1Pending +386, asyncSkipped +61, asyncBackoffs +769, asyncQueueFull +14, asyncQueueMax +32 | timing: invalid (noise medium); build: asi b75b706ac7c9 ts3native.asi.master-merge-1115 ts3native 0.36.261-lanes1 (log ok); ts3fixes tuning abdce0a6fef1 ts3fixes.package.master-dalexlol-405c-1115/ts3fixes.package.master-dalexlol-405d-1115/ts3fixes.package.master-merge-1115 (expected game-copy/ts3fixes.package.master-merge-1115); noise: medium (other apps 19.9% of game CPUs); others on game CPUs 19 |
