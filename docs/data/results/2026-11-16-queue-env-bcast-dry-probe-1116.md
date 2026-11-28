# Queue env-bcast-dry-probe-1116 2026-11-16

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/env-bcast-dry-probe-1116-2026-11-16.jsonl`.

## 2026-11-16T19:51 UTC, build 8b6b49149, jobs bp-probe-smoke, bp-probe, ip-probe

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-probe-smoke | 19:51 (03:51) | 5.8 | PASS | PASS: profile 4 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: bcposdry_tables 0->9282, envlistmemo_miss_same 0->2140, envlistmemo_scans 0->3724, envlistmemo_first 0->34, bcposdry_game_ms 0->2170, bcposdry_missing 0->1, envlistmemo_hit_ms 0->786, bcposdry_scan_ms 0->3052, bcposdry_pulses 0->60028, bcposdry_set_eq 0->26714, envlistmemo_calls 0->7978, bcposdry_extra 0->64, envlistmemo_hit 0- | 0 | 1780.6/1660.2/1797.6 -> 1913.6/1836.4/1585.7 | - | timing: invalid (noise medium; heavy tool samplers); build: asi 17a2fcf3b5a3 not in DB ts3native 0.36.267-mc1116 (log ok); ts3fixes tuning c64820e723fc ts3fixes.package.master-1116b/ts3fixes.package.envlist-bcpos-1116 (expected game-copy/ts3fixes.package.envlist-bcpos-1116); noise: medium (other apps 24.0% of game CPUs); others on game CPUs 24.0%; clock avg/min 4130/4083 MHz; Smoke 4 min BP, both <br>sim ms/s 193.3; p99 90 ms, p99.9 190, max 226, >=300 0, >=1000 0 (0.8 min) |

Paused after bp-probe-smoke (AI usage 5h - | 7d 98% (reset 14:00 PHT) | read 6 min ago); resume with `--from bp-probe`.
