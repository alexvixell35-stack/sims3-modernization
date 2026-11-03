# Queue mono-cost13 2026-10-30

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/mono-cost13-2026-10-30.jsonl`.

## 2026-10-30T05:28 UTC, build f24fcd58, jobs bp-autshadow-r, ip-autshadow-r

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|

## 2026-10-30T06:09 UTC, build 87b275a3, jobs bp-autshadow-r, ip-autshadow-r

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-autshadow-r | 06:09 (14:09) | 12 | PASS | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: autshadow_gm4_us 0->90087, autshadow_a0 0->22409, autshadow_a4 0->6189, autshadow_l_testagree 0->21495, autshadow_l_top1agree 0->731, autshadow_requests 0->733, autshadow_l_pairs 0->21498, autshadow_guardskips 0->6, autshadow_l_equal 0->21459, autshadow_gm4_methods 0->685, autshadow_skips 0->24078, owchain_alarms 0->3, owsprea | 0 | 2193.4/1847.2/1552.6 -> 2313.2/2118.1/1346.9 | - | timing: invalid (noise medium; heavy tool samplers); build: asi 1f5b6f3d96e5 ts3native.asi.mc13 ts3native 0.36.123 (log ok); ts3fixes tuning 4deba4b8ffa0 not in DB (expected game-copy/ts3fixes.package.mc13); noise: medium (other apps 19.1% of game CPUs); others on game CPUs 19.1%; clock avg/min 4155/4132 MHz; 1/2 Mode R on 0.36.123 (docs/122 §14.21). PASS = 0 crashes, no freeze; compared pairs > 0 |
| ip-autshadow-r | 06:21 (14:21) | 11.9 | PASS | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: autshadow_gm4_us 0->93612, autshadow_a0 0->35651, autshadow_a4 0->6911, autshadow_l_testagree 0->34779, autshadow_l_top1agree 0->729, autshadow_requests 0->733, autshadow_l_pairs 0->34780, autshadow_guardskips 0->10, autshadow_l_equal 0->34769, autshadow_gm4_methods 0->685, autshadow_skips 0->38152, owchain_alarms 0->4, owspre | 0 | 2219.5/1869.7/1544.7 -> 2152.3/1889.8/1544.7 | - | timing: invalid (noise medium; heavy tool samplers); build: asi 1f5b6f3d96e5 ts3native.asi.mc13 ts3native 0.36.123 (log ok); ts3fixes tuning 4deba4b8ffa0 not in DB (expected game-copy/ts3fixes.package.mc13); noise: medium (other apps 16.8% of game CPUs); others on game CPUs 16.8%; clock avg/min 4180/4126 MHz; 2/2 Mode R on 0.36.123 (docs/122 §14.21). PASS = 0 crashes, no freeze; compared pairs > 0 |
