# 0005. Stop P6 routing phases; ship their switches off

- **Status:** accepted. Decision by Rodney, 2026-10-25 (~21:40 PHT).
- **Context:** the P6 ladder rerun (docs/189 §17) found no phase that reduced sim job wait: off arms 143-152 ms/s, inside the noise band. StaleLotFp and AskLent/DoorMemoX measured worse, and the take kinds moved waits to gate-12 F-F.
- **Decision:** stop P6 and ship every P5/P6 switch off. v0.2.0 is the proven routing items only. Parked: the P6 phases, clone slimming (docs/66 §7.1) and the routing move-out helper (docs/194). Next focus: what leads Isla Paradiso natural hitches of 300 ms and more.
- **Consequences:** P6 no longer gates a release; the out-of-process routing design stays on paper.
- **Sources:** PLAN.md "Decision 2026-10-25 ~21:40 PHT (Rodney): stop P6, ship off"; docs/189 §17.
