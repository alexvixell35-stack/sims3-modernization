# 0006. Household Sims stay untouched

- **Status:** accepted (standing rule; the sources record no single decision date).
- **Context:** several fixes and throttles act on Sims the player is not controlling. A change that touches a household Sim changes what the player sees.
- **Decision:** changes may only affect Sims the player does not control. Household Sims never count as unseen, even off screen. Every deadline, cap, skip, throttle, teleport, fake or relaxed path needs an explicit household and selected-Sim exemption in code; a Sim of unknown scope counts as household. Results for controlled Sims must equal vanilla: proven-exact speedups and EA bug fixes stay, and crash guards keep a long hard ceiling.
- **Consequences:** every Sim-affecting change is reviewed for the exemption, and measurement gates check that the household is unchanged.
- **Sources:** AGENTS.md "Household Sims stay untouched"; PLAN.md gate 2 answers of 2026-11-06 ("V6 hard, only invisible Sims may change").
