# Decision log (ADRs)

Why the project does what it does, one short file per big choice (docs/23 #10). Each file: context, decision, consequences, status, sources. A decision marked "Rodney, <date>" was made by the maintainer on that date; the sources line points at the doc section or PLAN.md entry that records it, so a reader can check. Failed experiments are in [docs/re/dead-ends.md](../re/dead-ends.md); this log keeps the choices that shape later work.

| # | Decision | Status |
|---|---|---|
| [0001](0001-async-route-plan-paused.md) | The first async route plan is paused; only the gated design ships | partly superseded |
| [0002](0002-no-64bit-resource-helper.md) | No 64-bit resource offload helper | accepted |
| [0003](0003-prefer-own-fixes-over-s3pp.md) | Prefer our own fixes over Sims 3 Performance Patch features; no fork | accepted (Rodney, 2026-10-16) |
| [0004](0004-offload-goal-and-acceptance-rule.md) | Long-term goal: offload the sim thread; accept up to ~2% loss if stable | accepted (Rodney, 2026-10-14) |
| [0005](0005-stop-p6-ship-off.md) | Stop P6 routing phases; ship their switches off | accepted (Rodney, 2026-10-25) |
| [0006](0006-household-sims-untouched.md) | Household Sims stay untouched | accepted |
| [0007](0007-default-off-until-measured.md) | New behaviour ships default off until measured | accepted |
| [0008](0008-no-rehosting-no-porting-community-mods.md) | No re-hosting of community fixes; behaviour specs, not ported code | accepted |
| [0009](0009-stop-compaction.md) | Compaction work stopped | accepted (Rodney, 2026-11-07) |
| [0010](0010-gate2-answers.md) | Gate 2 rules for the unseen-Sim work | accepted (Rodney, 2026-11-06) |

Not back-filled: "stuck Sims treated as solved (log only)". PLAN.md only has it as "log only" notes in A/B rows, with no decision entry to cite.

New ADR: copy the shape of any file here, take the next number, add a row above.
