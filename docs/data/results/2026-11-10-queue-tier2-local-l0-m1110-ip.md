# Queue tier2-local-l0-m1110-ip 2026-11-10

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/tier2-local-l0-m1110-ip-2026-11-10.jsonl`.

## 2026-11-10T05:19 UTC, build c2bb5ed, jobs ip-t2l0

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| ip-t2l0 | 05:19 (13:19) | 16.5 | PASS | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: aut_l0_pairn_seen 0->18, aut_l0_n_seen 0->18, aut_l0_us_hh 0->2739291, aut_l0_side_us 0->2196222, aut_l0_n_hh 0->281, aut_l0_us_seen 0->57760, aut_l0_pairs_seen 0->2486, aut_l0_nolocal_n 0->5952, aut_l0_pairs_hh 0->183586, aut_l0_span_ms 0->604609, aut_l0_side_n 0->12305, aut_l0_nolocal_us 0->98180, aut_l0_pairn_hh 0->281, owc | 0 | 2293/2016.4/1476.4 -> 2316.9/2087.1/1387.1 | - | timing: invalid (job VOID: runInvalid: profiler cut short (401 of 600 s)); build: asi bf3e9d8c9fc8 not in DB ts3native 0.36.239 (log ok); ts3fixes tuning 7c3921b1573a not in DB (expected build/ts3fixes.package); noise: low; others on game CPUs 0.4%; clock avg/min 4737/4613 MHz; L0 measure, Baseline-IP-Spring (resort workers, handoff §0.2), 10 min: PASS = 0 crashes, no freeze, aut_l0_exc == 0. Read |
