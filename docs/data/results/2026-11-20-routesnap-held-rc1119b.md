# RouteSnap address space held, v0.3.0 release config (rc-1119b), 2026-11-20

Source: existing run data only, no new game run. Gate: >= ~50 MB held = build the docs/194 helper. Verdict: **no (about 7 MB, peak 13 MB).**

## Static (native source)
Ini: `[RouteSnap] Enabled=1 Mode=0 Count=1 Lots=none CapMB=64 ReplayCapMB=128 TakeKinds=8`.
- Enabled=1: P3 world images in `g_spHeap` (`snap.cpp`), lot strategy `kSpLotNone` (no lot images). Counters `spKB`, `spKBMax`.
- Mode=0: audit only, no clone run. Count=1: counters only.
- Replay clone `g_rpClone` / `g_rpHeap` and the 96 MB private-pool reserve (`pools.cpp` `PlRegion`) are only created by the replay install (`replay.cpp:3816`, needs `Lots=replay`). Counters `rpKB`, `plKB`, `plResKB` read 0.

## Measured (counters from `*-native.json`, before = after load, after = end of job)
| config / job | world | spKB before -> after | spKBMax | rpKB | plKB / plResKB |
|---|---|---|---|---|---|
| rc1119b, `release-verify-rc1119b-rerun1-1120` `bp-lptrim-memtravel-x3-rc1119-r1`, 10 min | BP | 6,623 -> 6,626 | 13,356 | 0 | 0 / 0 |
| same queue `bp-apex403-s3ss-travel-mem-x3-rc1119-r1`, 9.7 min | BP | 6,623 -> 6,626 | 13,356 | 0 | 0 / 0 |
| `unseen-rate-ab-1118` `ip-unseenrate-ab` (build e7a8faba5, 11-19), 30 min | IP | 6,790 -> 6,790 | 13,575 | 0 | 0 / 0 |

BP rc1119b process commit / private / largest free (MB) at load -> after: 1763.8 / 1673.2 / 1815.3 -> 2011.5 / 1835.9 / 1661.9 (whole session, travel x3; not routesnap-specific).

## Not measured
Largest-free delta with `[RouteSnap] Enabled=0` (arm B). Held MB is bounded by the counters (6.6-13.6 MB) plus heap slack; skipped as far below the gate. Counter references: docs/66 §7, docs/191, docs/201.
