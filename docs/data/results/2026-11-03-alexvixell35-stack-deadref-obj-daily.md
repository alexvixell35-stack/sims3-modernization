# deadrefsweep phases 10-12 on one walk, after a Sim leaves or daily (2026-11-03)

- Change: `fix/deadref-obj-daily` (docs/205 §2.3a), ts3fixes built from that branch; native: release v0.2.4 asi (no native change).
- Save: the player's old save `1111 kit5` (a long-played Isla Paradiso save, not a community save; the before run is on the same save).
- Profile: 20 min, seed 12345, `-disablestoryprogression`, `fixesOn deadrefsweep,rewardgiver`, no samplers, the harness autopilot.
- Machine load ok in both runs; crashes: none.

| job | build | result | passes [405] | total ms [408] | ms per pass | slices [406] | longest slice us [407] | object walks [2055] | asked [2057] | ms in walks [2056] |
|---|---|---|---|---|---|---|---|---|---|---|
| `old-gone-on` (before: 3 walks every pass) | main ca8fc13 | PASS | 206 | 18,778 | ~91 | 10,456 | 5,885 | - | - | - |
| `old-gone-objdaily` (after) | `fix/deadref-obj-daily` | PASS | 215 | 4,913 | ~23 | 3,711 | 4,421 | 40 | 40 | 2,779 |

Found (before -> after): react times 81 -> 129, Sim fields 38 -> 36, passengers 2 -> 12, topics 1 -> 1, celebrity 5 -> 4; dartboards, use lists, object owners, handlers, rewardgiver 0 in both. Refused passes 4 -> 0. Stuck Sims at the end 21 -> 11 (both runs: the repro save at the end took over 120 s, as in every run on this save today).
