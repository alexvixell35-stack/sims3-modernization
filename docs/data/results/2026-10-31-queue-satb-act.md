# Queue satb-act 2026-10-31

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/satb-act-2026-10-31.jsonl`.

## 2026-10-31T00:10 UTC, build dc3d3fad, jobs ip-satb-act-2

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| ip-satb-act-2 | 00:10 (08:10) | 11.7 | PASS | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: autshadow_skips 0->43594, autshadow_l_testagree 0->39945, autshadow_gm4_methods 0->684, autshadow_a0 0->40808, autshadow_a4 0->10706, autshadow_gm4_us 0->78866, autshadow_requests 0->887, autshadow_l_equal 0->39941, autshadow_l_pairs 0->39946, autshadow_guardskips 0->11, autshadow_l_top1agree 0->869, owchain_alarms 0->4, owspr | 0 | 2166.8/1876.6/1580.9 -> 2167.8/1898.1/1557.9 | - | timing: invalid (noise medium; heavy tool samplers); build: asi 1fb95baab9bd not in DB ts3native 0.36.144 (log ok); ts3fixes tuning 066246321fd4 not in DB (expected game-copy/ts3fixes.package.satb8); noise: medium (other apps 12.1% of game CPUs); others on game CPUs 12.1%; clock avg/min 4184/4141 MHz; Run 2 (second clean run, crash rule) of Satb act Mode R on IP (docs/206 §7.3, §10). PASS = 0 cras |
