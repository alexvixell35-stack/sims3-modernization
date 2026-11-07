# petpoolorphan: a pet dropped from an adoption pool with no family links (2026-11-05)

- Change: `fix/pet-pool-orphan` 4f6ee6a (docs/534, issue #166 build 52), ts3fixes built from that branch; native: release v0.2.5 asi (no native change).
- Core: **simler90's UPDATE242 core without my patches** (my core carries build 52, so its off arm would not show the bug); the harness swapped it in for this job only.
- Save: Baseline-BP. Profile: 6 min, seed 12345, repro `pet-pool-orphan-check`, queue `tools/harness/queues/pet-pool-orphan.json`.
- Machine load ok; crashes: none.

| job | result | off arm (`expect broken`) | on arm (`expect fixed`) | `petpoolorphan_disposed` |
|---|---|---|---|---|
| `bp-pet-pool-orphan-check` | PASS in 9.9 min | `RandomRemovePet` True, then valid True, household False | `RandomRemovePet` True, then valid False, household False | 1 (gate >= 1 passed) |

Off: the new adoption cat (EA's `CreatePetByType(AdoptCat)`, no family links) left the pool as a valid description with no household, the state that goes into the save. On: the hook disposed it at the drop.

Not tested: a real pool refresh followed by save + load (the "SimDescription Removed" count at the next load), and pets whose Sim is in the world (counter 2063).
