# Queue gu2-arrive-rerun 2026-11-07

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/gu2-arrive-rerun-2026-11-07.jsonl`.

## 2026-11-07T23:38 UTC, build 94f7bc07, jobs ip-gu2-arrive-off, ip-gu2-arrive-on, bp-gu2-arrive-off, bp-gu2-arrive-on

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| ip-gu2-arrive-off | 23:38 (07:38) | 16 | PASS | PASS: profile 14 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: g8_hh_searches 0->254, gohome_runs 0->315, autshadow_skips 0->66023, g8_seen_game 0->1208, g8_seen_exact 0->237, g8_len_unseen_ticks 0->69659, g8_seen_game_nopick 0->962, autshadow_gm4_methods 0->683, autshadow_a0 0->1416, autshadow_a4 0->35, autshadow_gm4_us 0->136902, g8_q_unseen_ticks 0->4183238, g8_q_seen_n 0->1437, g8_nul | 0 | 2172.3/1877.3/1572.9 -> 2223.9/1900.6/1504.5 | - | timing: invalid (noise medium); build: asi efc5de17539d ts3native.asi.g2pair1108 ts3native 0.36.190 (log ok); ts3fixes tuning 1265927843a6 ts3fixes.package.g2pair1107/ts3fixes.package.castrim3/ts3fixes.package.master0189/ts3fixes.package.g2pair1107b/ts3fixes.package.g2pair1108 (expected game-copy/ts3fixes.package.g2pair1108); noise: medium (other apps 20.9% of game CPUs); others on game CPUs 20.9% |

## 2026-11-07T23:59 UTC, build 3b574f16, jobs ip-gu2-arrive-on, bp-gu2-arrive-off, bp-gu2-arrive-on

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| ip-gu2-arrive-on | 23:59 (07:59) | 16.3 | PASS | PASS: profile 14 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: autunseen_cost_us 0->827753, autunseen_guard_windows 0->110, g8_hh_searches 0->190, gohome_runs 0->272, autunseen_posted 0->1864, autshadow_skips 0->111823, g8_seen_game 0->430, g8_seen_exact 0->315, g8_len_unseen_ticks 0->81768, g8_seen_game_nopick 0->273, autshadow_gm4_methods 0->683, autunseen_tagged 0->1, autshadow_a0 0->2 | 0 | 2188.1/1874.5/1572.8 -> 2217.9/1881.3/1511.4 | - | timing: invalid (job VOID: runInvalid: noisy machine (others 27.9% of game CPUs); noise high); build: asi efc5de17539d ts3native.asi.g2pair1108 ts3native 0.36.190 (log ok); ts3fixes tuning 1265927843a6 ts3fixes.package.g2pair1107/ts3fixes.package.castrim3/ts3fixes.package.master0189/ts3fixes.package.g2pair1107b/ts3fixes.package.g2pair1108 (expected game-copy/ts3fixes.package.g2pair1108); noise: hi |
