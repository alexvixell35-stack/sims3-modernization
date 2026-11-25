# 2026-11-23: OutfitFirst ON, Mode 2 (PR #625, import/dalexlol-batch-1123)

By Dalexlol. Contributor data from PR #625 (docs/684 §9.1); not rerun here (rule: contributor results with harness evidence merge as is and go ON).

| Switch | Default | His evidence | Household check |
|---|---|---|---|
| `[OutfitFirst]` `Enabled=1`, `Mode=2` (ts3native.ini and code default) | ON | Isla Paradiso, 6-minute calm soaks, single runs per arm. Pair 1 (s233 measure, s234 first): swaps sat ready 6,327 -> 3,988 ms in all (longest 2,607 -> 1,570), other jobs built past a ready swap 8 -> 0, outfit wait p50/p90 480/1,408 -> 176/1,152 ms, household max 1,449 -> 804 ms, selected max 994 -> 97 ms. Pair 2 with JobYield on (s236, s237): 7,255 -> 2,472 ms (-66%). Verified in Ghidra/objdump: call chain and pick site | Same jobs, another build order; nothing skipped or faked; exact for every Sim, household included |

EA 1.69 and retail 1.67: the feature logs SKIP (three rows unresolved). Gain on other worlds is inferred, not measured.
