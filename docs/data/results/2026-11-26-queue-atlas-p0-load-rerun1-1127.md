# Queue atlas-p0-load-rerun1-1127 2026-11-26

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/atlas-p0-load-rerun1-1127-2026-11-26.jsonl`.

## 2026-11-26T22:48 UTC, build 84a9850c6, jobs atlas-p0-2048

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| atlas-p0-2048 | 22:48 (06:48) | 4.3 | PASS | atlas-p0-load-2048: PASS: steps done (no watch) (expected: in_world within 10 min (the step time is the load time); 2 min of camera poses over the origin quadrant and the three far corners with the game still alive; no script error)<br>PASS: repro events fired<br>PASS: 0 new script errors (max 0, 1 known EA ignored)<br>PASS: atlas.log log rules (required present [cut: full row in the jsonl] | 0 | 1308.6/1211.9/1900.5 -> 1635.3/1568.8/1796.2 | - | timing: invalid (noise high; correctness-only tool ProbeSnap.CallSites); build: asi a2ee78e8685b ts3native.asi.m1126b ts3native 0.36.306-m1126b (log ok); ts3fixes tuning 5f21313c91ab ts3fixes.package.m1126b (expected game-copy/ts3fixes.package.m1126b); noise: high (other apps 59.7% of game CPUs, NOT comparable (load or clock)); others on game CPUs 59.7%; clock avg/min 4026/3915 MHz [cut: full row in the jsonl] |
