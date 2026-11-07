# invnotyet: an object given to a Sim before the Sim has an inventory (2026-11-05)

- Change: `fix/inventory-not-yet` 81ca5a4 (docs/531, issue #166 build 13), ts3fixes built from that branch; native: release v0.2.5 asi (no native change).
- Core: **simler90's UPDATE242 core without my patches** (my core carries build 13); the harness swapped it in for this job only.
- Save: Baseline-BP. Profile: 6 min, seed 12345, repro `inv-not-yet-check`, queue `tools/harness/queues/inv-not-yet.json`.
- Machine load ok; crashes: none.

| job | result | off arm (`expect broken`) | on arm (`expect fixed`) | `expect gone` | `invnotyet_refused` |
|---|---|---|---|---|---|
| `bp-inv-not-yet-check` (rerun) | PASS in 11.4 min | threw `NullReferenceException`, returned false, object left | threw nothing, returned false, the hook destroyed the object | the object is gone (2 s later) | 1 (gate >= 1 passed) |

The first run failed only in the check: it read `HasBeenDestroyed` in the same call as the hook's `Destroy`, and the proxy goes later. The check now counts the hook's `Destroy` and looks for the object a step later.

Not tested: a real Sim creation through the fixed path (`BandSkill.OnSkillAddition` from `AssignSchool`); the check calls the method directly.
