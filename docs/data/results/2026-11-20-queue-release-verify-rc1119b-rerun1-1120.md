# Queue release-verify-rc1119b-rerun1-1120 2026-11-20

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/release-verify-rc1119b-rerun1-1120-2026-11-20.jsonl`.

## 2026-11-20T00:07 UTC, build dab876a6b, jobs bp-lptrim-memtravel-x3-rc1119-r1, bp-apex403-s3ss-travel-mem-x3-rc1119-r1

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-lptrim-memtravel-x3-rc1119-r1 | 00:07 (08:07) | 10 | PASS | travel: PASS<br>summary: 1 cases, 1 passed, 0 failed<br>PASS: repro events fired<br>PASS: no forbidden log lines | 0 | 1763.8/1673.2/1815.3 -> 2011.5/1835.9/1661.9 | - | timing: invalid (noise medium; correctness-only tool ProbeSnap.CallSites); build: asi 527949741d2b ts3native.asi.rc-1119b ts3native 0.36.295-rc1119b (log ok); ts3fixes tuning 710a355f3c57 ts3fixes.package.rc-1119b (expected game-copy/ts3fixes.package.rc-1119b); noise: medium (other apps 36.6% of game CPUs); others on game CPUs 36.6%; clock avg/min 4037/3940 MHz [cut: full row in the jsonl] |
| bp-apex403-s3ss-travel-mem-x3-rc1119-r1 | 00:17 (08:17) | 9.7 | PASS | travel: PASS<br>summary: 1 cases, 1 passed, 0 failed<br>PASS: repro events fired<br>PASS: no forbidden log lines | 0 | 1752.5/1664.5/1813.9 -> 1999.5/1844.3/1659.7 | - | timing: invalid (noise medium; correctness-only tool ProbeSnap.CallSites); build: asi 527949741d2b ts3native.asi.rc-1119b ts3native 0.36.295-rc1119b (log ok); ts3fixes tuning 710a355f3c57 ts3fixes.package.rc-1119b (expected game-copy/ts3fixes.package.rc-1119b); noise: medium (other apps 33.0% of game CPUs); others on game CPUs 33.0%; clock avg/min 4098/3997 MHz [cut: full row in the jsonl] |
