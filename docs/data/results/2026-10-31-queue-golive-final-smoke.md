# Queue golive-final-smoke 2026-10-31

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/golive-final-smoke-2026-10-31.jsonl`.

## 2026-10-31T07:35 UTC, build 407523f3, jobs bp-gf-smoke

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-gf-smoke | 07:35 (15:35) | 5.6 | PASS | PASS: profile 4 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: autshadow_skips 0->41882, autshadow_gm4_methods 0->683, autshadow_a0 0->943, autshadow_a4 0->37, autshadow_gm4_us 0->124718, g8_null_picks 0->1445, g8_simhours_x100 0->386315, autshadow_gm6_us 0->35435, autshadow_requests 0->1281, autliveguard_ratio_pm 0->-1, g8_pushes 0->735, g8_gohome 0->683, g8_lat0 0->99, g8_lat4 0->603, au | 0 | 2144.7/1850.5/1614.8 -> 2214.1/2040.3/1399.4 | - | timing: invalid (noise medium); build: asi c0a4235076c2 not in DB ts3native 0.36.148 (log ok); ts3fixes tuning 28004ab757f8 not in DB (expected game-copy/ts3fixes.package.golive-final); noise: medium (other apps 7.0% of game CPUs); others on game CPUs 7.0%; clock avg/min 4264/4213 MHz; Smoke (4 min BP, Satb=2, Dry=1): PASS = 0 crashes; logRequire read-back of the go-live line (TopN 10 Bands 3 Marg |
