# reltablesapart: two Sims' relationship tables that came apart (2026-11-05)

- Change: `fix/relationship-tables-apart` fc2dc0f (docs/535, issue #166 build 14), ts3fixes built from that branch; native: release v0.2.5 asi (no native change).
- Core: **simler90's UPDATE242 core without my patches** (my core carries build 14); the harness swapped it in for this job only.
- Save: Baseline-BP. Profile: 6 min, seed 12345, repro `rel-tables-apart-check`, queue `tools/harness/queues/rel-tables-apart.json`.
- Machine load ok; crashes: none.

| job | result | off arm (`expect broken`) | on arm (`expect fixed`) | `reltablesapart_rejoined` |
|---|---|---|---|---|
| `bp-rel-tables-apart-check` | PASS in 9.2 min | threw `ArgumentException`, returned null, in both tables False | threw nothing, returned the shared relationship, in both tables True | 1 (gate >= 1 passed) |

Off: with the shared relationship taken out of x's table only, EA's `Relationship.Get(x, y, true)` threw the same-key `ArgumentException` (and left a second relationship in x's table; the check puts the original back). On: the hook returned the shared relationship and both tables held it again.

Not tested: how the tables come apart in play (a census of one-sided pairs around NRaas resets).
