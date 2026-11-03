# Queue pan-swarm-baseline 2026-10-24

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/pan-swarm-baseline-2026-10-24.jsonl`.

## 2026-10-24T14:23 UTC, build 759f926e, jobs sv-pan-swarm-baseline-1, sv-pan-swarm-baseline-2

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| sv-pan-swarm-baseline-1 | 14:23 (22:23) | 7.7 | PASS | PASS: profile 6 min, seed 12345 (rng-compare.ts)<br>mono ledger INCOMPLETE: per-item numbers below the table | 0 | 2151.9/1793.7/1548.9 -> 2206/1965.4/1339.8 | - | timing: invalid (noise medium); build: asi baa6f8e055a1 ts3native.asi.objres ts3native 0.36.46 (log ok); ts3fixes tuning d6c5e8711bdb not in DB (expected build/ts3fixes.package); noise: medium (other apps 24.7% of game CPUs); others on game CPUs 24.7%; clock avg/min 4191/4137 MHz; No Apex. Our logging build 0.36.46 with release settings + [CrashForensics] all rounds. Read: stale-list and DRAW BAD  |
