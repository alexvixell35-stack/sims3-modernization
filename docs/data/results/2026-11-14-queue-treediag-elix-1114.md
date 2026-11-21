# Queue treediag-elix-1114 2026-11-14

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/treediag-elix-1114-2026-11-14.jsonl`.

## 2026-11-14T14:29 UTC, build cc7c8106, jobs sv-treediag-1

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| sv-treediag-1 | 14:29 (22:29) | 4.1 | PASS | PASS: quit to desktop, no crash line<br>PASS: repro events fired<br>PASS: no forbidden log lines | 0 | 2141.9/1834.7/1618.6 -> 2135.8/1834.8/1618.6 | - | timing: invalid (noise high; correctness-only tool ProbeSnap.CallSites); build: asi 20fb66433952 not in DB ts3native 0.36.247-elixdiag1 (log ok); ts3fixes tuning 0373884c6b0f ts3fixes.package.rc1111e/ts3fixes.package.rel0211b/ts3fixes.package.replanlog-1114 (expected game-copy/ts3fixes.package.rel0211b); noise: high (other apps 22.5% of game CPUs, NOT comparable (load or clock)); others on game CP |
