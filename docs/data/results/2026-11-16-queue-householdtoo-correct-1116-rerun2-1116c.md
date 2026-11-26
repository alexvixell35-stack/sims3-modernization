# Queue householdtoo-correct-1116-rerun2-1116c 2026-11-16

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/householdtoo-correct-1116-rerun2-1116c-2026-11-16.jsonl`.

## 2026-11-16T14:26 UTC, build 68447d379, jobs bp-hhtoo-off

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-hhtoo-off | 14:26 (22:26) | 33.9 | PASS | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: autunseen_offlist 0->163485, autshadow_skips 0->177385, g8_hh_simhours_x100 0->61190, g8_lat0 0->321, g8_lat2 0->173, g8_lat3 0->179, g8_lat4 0->2950, autshadow_guardskips 0->126, g8_len_seen_ticks 0->4865, g8_w_seen_n 0->213, g8_null_picks 0->5828, g8_seen_gohome 0->288, g8_unseen_null_picks 0->5209, autshadow_rain_done 0->2, | 0 | 1746.3/1620.4/1716.5 -> 2091.5/1847.3/1599.9 | - | timing: invalid (noise medium); build: asi 17a2fcf3b5a3 ts3native.asi.master-1116c ts3native 0.36.267-mc1116 (log ok); ts3fixes tuning c64820e723fc ts3fixes.package.master-1116b/ts3fixes.package.master-1116c (expected game-copy/ts3fixes.package.master-1116c); noise: medium (other apps 51.1% of game CPUs); others on game CPUs 51.1%; clock avg/min 3880/3222 MHz; docs/271 §4 flag-off load: same as bp<br>sim ms/s 208.0; p99 123 ms, p99.9 205, max 1484, >=300 4, >=1000 2 (4.7 min) |
