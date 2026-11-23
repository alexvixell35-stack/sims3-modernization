# Queue commodmap-fast-1121-correct 2026-11-21

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/commodmap-fast-1121-correct-2026-11-21.jsonl`.

## 2026-11-21T02:33 UTC, build 97c0aa4bc, jobs bp-commodmap-check, ip-mm-commodmap-shadow

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-commodmap-check | 02:33 (10:33) | 3 | **FAIL** | FAIL: profile 10 min, seed 12345 (rng-compare.ts)<br>FAIL: repro step reflect_call answer does not match /hook path (shadow, [1-9][0-9]* calls): same; verdict: EXACT/: commodmapfast check: 150 objects, 8830 pairs (4 copies), longest list 1890, 75 removed; EA 147 ms (max 59 ms per object), batch 50 ms (max 11 ms), 0 fallbacks; batch vs EA: same; hook path (shadow, 75 calls): same; verdict: EXACT [cut: full row in the jsonl] | 1 | 1740.4/1676.5/1811.6 -> 1799.7/1725/1791.9 | - | timing: invalid (job VOID: runInvalid: profiler cut short (6 of 600 s); noisy machine (machine load: no samples); crash or abort (no "done; game al; noise high); build: asi 66f4d0305688 ts3native.asi.mkit2-1121 ts3native 0.36.298-mkit2 (log ok); ts3fixes tuning e290a6386bec ts3fixes.package.cmf-1121 (expected game-copy/ts3fixes.package.cmf-1121); noise: high (other apps 65.5% of game CPUs [cut: full row in the jsonl] |

Stopped: bp-commodmap-check failed (stopQueueOnFail): FAIL: profile 10 min, seed 12345 (rng-compare.ts) | FAIL: repro step reflect_call answer does not match /hook path (shadow, [1-9][0-9]* calls): same; verdict: EXACT/: commodmapfast check: 150 objects, 8830 pairs (4 copies), longest list 1890, 75 removed; EA 147 ms (max 59 ms per object), batch 50 ms (max 11 ms), 0 fallbacks; batch vs EA: same; hook path (shadow, 75 calls): same; verdict: EXACT; co.
