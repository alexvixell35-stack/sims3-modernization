# 0004. Long-term goal: offload the sim thread; accept up to ~2% loss if stable

- **Status:** accepted. Decision by Rodney, 2026-10-14.
- **Context:** the simulation is mostly single-threaded, so longer saves and more objects hit one core.
- **Decision:** move everything that can safely leave the sim thread to other cores, as a multi-step track. A change that moves measurable work off the sim thread is accepted even with no speed gain today, or a loss of up to ~2% in sim iterations/s, if it is stable (no crashes, no new stuck Sims, same results). Measure it as sim-thread ms/s moved off (route ms/s, stage ms/s), not only iterations/s.
- **Consequences:** offload steps ship on stability and moved ms/s. Roadmap in docs/23 #17, thread map in docs/28.
- **Sources:** PLAN.md "Long-term goal: offload the sim thread (Rodney, 2026-10-14)"; docs/23-ideas-and-roadmap.md #17.
