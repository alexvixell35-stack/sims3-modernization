# 0010. Gate 2 rules for the unseen-Sim work

- **Status:** accepted. Decisions by Rodney, 2026-11-06.
- **Context:** the unseen-Sim go-live needed pass rules before the long timing queues.
- **Decision:** G1 modelled gains count; G2 and G3 must agree. V6 is hard: only invisible Sims may change. The measurement band is +-10%. The V1 idle limit is fixed at 240 sim minutes. `Audit=50` stays on for players with its cost measured and shown light. `autunseenrh` stays on unless off shows a real benefit or gain. `autunseen` defaults ON if gate 2 passes. The compiler is measured statically (coverage and correctness gates), gains add up, and the ms/s stop rules were removed.
- **Consequences:** timing queues were trimmed from 26.5 h to ~12.5 h the same day.
- **Sources:** PLAN.md "Rodney's decisions 2026-11-06" and "Timing queues trimmed 2026-11-06"; docs/222 §11.
