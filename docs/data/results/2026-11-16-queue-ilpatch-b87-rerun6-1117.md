# Queue ilpatch-b87-rerun6-1117 2026-11-16

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/ilpatch-b87-rerun6-1117-2026-11-16.jsonl`.

## 2026-11-16T23:37 UTC, build 037772c37, jobs bp-ilp-b87-off, bp-ilp-b87-on

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-ilp-b87-off | 23:38 (07:38) | 3.7 | **FAIL** | FAIL: profile 3 min, seed 12345 (rng-compare.ts)<br>FAIL: repro step ilp_scavenger_check failed: no reply to 'ilp_scavenger_check' (game paused, loading or blocked?) | 1 | 1748.9/1632.7/1679.1 -> 1778/1671.5/1679.1 | - | timing: invalid (job VOID: runInvalid: profiler cut short (9 of 180 s); noisy machine (machine load: no samples); crash or abort (no "done; game al; noise high); build: asi 17a2fcf3b5a3 ts3native.asi.master-1116c ts3native 0.36.267-mc1116 (log ok); ts3fixes tuning c64820e723fc ts3fixes.package.master-1116b/ts3fixes.package.master-1116c (expected game-copy/ts3fixes.package.master-1116c); noise: hig |
| bp-ilp-b87-on | 23:42 (07:42) | 3.7 | **FAIL** | FAIL: profile 3 min, seed 12345 (rng-compare.ts)<br>FAIL: repro step ilp_scavenger_check failed: no reply to 'ilp_scavenger_check' (game paused, loading or blocked?)<br>PASS: repro events fired<br>PASS: no forbidden log lines<br>FAIL: 1 new script errors (max 0): ScriptError_<pc>__11-17-2026__07-45-47__28780C24.xml | 1 | 1793.8/1692.2/1697.8 -> 1770.3/1675/1697.8 | - | timing: invalid (job VOID: runInvalid: profiler cut short (9 of 180 s); noisy machine (machine load: no samples); crash or abort (no "done; game al; noise high); build: asi 17a2fcf3b5a3 ts3native.asi.master-1116c ts3native 0.36.267-mc1116 (log ok); ts3fixes tuning c64820e723fc ts3fixes.package.master-1116b/ts3fixes.package.master-1116c (expected game-copy/ts3fixes.package.master-1116c); noise: hig |
