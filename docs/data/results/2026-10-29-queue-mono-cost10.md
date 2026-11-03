# Queue mono-cost10 2026-10-29

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/mono-cost10-2026-10-29.jsonl`.

## 2026-10-29T16:10 UTC, build 72437973, jobs bp-autshadow-r, ip-autshadow-r

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-autshadow-r | 16:10 (00:10) | 3.1 | **FAIL** | PASS: repro events fired | - | ? -> ? | - | timing: invalid (job FAIL: exit code ?; noise medium; heavy tool samplers); build: asi 4eb2808299f7 ts3native.asi.mono-cost10 ts3native 0.36.118; ts3fixes tuning 4deba4b8ffa0 not in DB (expected build/ts3fixes.package); noise: medium (other apps 16.2% of game CPUs); others on game CPUs 16.2%; clock avg/min 4090/3781 MHz; 1/2 Mode R on 0.36.118 (docs/122 §14.18). mono-cost9 (0.36.115): BP A0 26,606 |
| ip-autshadow-r | 16:13 (00:13) | 11.8 | PASS | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: autshadow_gm4_us 0->88029, autshadow_a0 0->38552, autshadow_a4 0->8585, autshadow_a5 0->68, autshadow_l_testagree 0->37645, autshadow_l_top1agree 0->805, autshadow_requests 0->882, autshadow_l_pairs 0->37649, autshadow_guardskips 0->7, autshadow_l_equal 0->37640, autshadow_gm4_methods 0->685, autshadow_skips 0->42222, owchain_ | 0 | 2182.3/1867.4/1554.5 -> 2197.3/1909.8/1521 | - | timing: invalid (noise medium; heavy tool samplers); build: asi 4eb2808299f7 ts3native.asi.mono-cost10 ts3native 0.36.118 (log ok); ts3fixes tuning 4deba4b8ffa0 not in DB (expected build/ts3fixes.package); noise: medium (other apps 10.3% of game CPUs); others on game CPUs 10.3%; clock avg/min 4231/4170 MHz; 2/2 Mode R on 0.36.118 (docs/122 §14.18). mono-cost9 (0.36.115): BP A0 26,606, A2 1 (GoToGa |

## 2026-10-29T16:25 UTC, build 72437973, jobs bp-autshadow-r

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|

## 2026-10-29T23:11 UTC, build 72437973, jobs bp-autshadow-r

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-autshadow-r | 23:11 (07:11) | 13 | PASS | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: autshadow_gm4_us 0->120527, autshadow_a0 0->19573, autshadow_a4 0->4771, autshadow_a5 0->6, autshadow_l_testagree 0->18737, autshadow_l_top1agree 0->618, autshadow_requests 0->631, autshadow_l_pairs 0->18742, autshadow_guardskips 0->4, autshadow_l_equal 0->18715, autshadow_gm4_methods 0->685, autshadow_skips 0->20480, owchain_ | 0 | 2115/1849.3/1642.3 -> 2258.5/2097.1/1364.9 | - | noisy: does not count, rerun as bp-autshadow-r-rerun; timing: invalid (job VOID: runInvalid: noisy machine (others 17.0% of game CPUs); noise high; heavy tool samplers); build: asi 4eb2808299f7 ts3native.asi.mono-cost10 ts3native 0.36.118 (log ok); ts3fixes tuning 4deba4b8ffa0 not in DB (expected build/ts3fixes.package); noise: high (other apps 21.9% of game CPUs, GPU 3D others 35.7%); others on g |
| bp-autshadow-r-rerun | 23:24 (07:24) | 12.6 | PASS | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: autshadow_gm4_us 0->136185, autshadow_a0 0->18176, autshadow_a4 0->4633, autshadow_a5 0->14, autshadow_l_testagree 0->17391, autshadow_l_top1agree 0->582, autshadow_requests 0->600, autshadow_l_pairs 0->17393, autshadow_guardskips 0->8, autshadow_l_equal 0->17365, autshadow_gm4_methods 0->685, autshadow_skips 0->19192, owchain | 0 | 2114.6/1861.3/1628.9 -> 2253.3/1954.2/1508.9 | - | rerun of bp-autshadow-r: noisy again, neither counts; timing: invalid (job VOID: runInvalid: noisy machine (others 19.2% of game CPUs); noise high; heavy tool samplers); build: asi 4eb2808299f7 ts3native.asi.mono-cost10 ts3native 0.36.118 (log ok); ts3fixes tuning 4deba4b8ffa0 not in DB (expected build/ts3fixes.package); noise: high (other apps 22.4% of game CPUs, GPU 3D others 37.5%); others on g |
