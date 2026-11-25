# Queue householdtoo-correct-1116-1116b 2026-11-16

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/householdtoo-correct-1116-1116b-2026-11-16.jsonl`.

## 2026-11-16T09:52 UTC, build fdbb0d9d, jobs bp-hhtoo-off

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-hhtoo-off | 09:52 (17:52) | 3.3 | **FAIL** | FAIL: profile 10 min, seed 12345 (rng-compare.ts)<br>FAIL: repro step spawn_sims failed: no reply to 'spawn_sims' (game paused, loading or blocked?)<br>PASS: repro events fired<br>PASS: no forbidden log lines<br>PASS: 0 new script errors (max 0) | 1 | 1717.6/1629.4/1721.4 -> 1767.1/1667.1/1714.3 | - | timing: invalid (job VOID: runInvalid: profiler cut short (28 of 600 s); noisy machine (machine load: no samples); crash or abort (no "done; game a; noise high); build: asi 011f124f4797 ts3native.asi.master-1116b ts3native 0.36.266-mb1116 (log ok); ts3fixes tuning c64820e723fc ts3fixes.package.master-1116b (expected game-copy/ts3fixes.package.master-1116b); noise: high (other apps 62.8% of game CP |
