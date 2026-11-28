# 2026-11-20: Dalexlol PR #592 imported (branch import/dalexlol-592-1120)

By Dalexlol. Numbers copied from his PR text (natural-play harness games with the switches on; counters moved, run-verdict PASS jobs); not rerun here.

Rule (Rodney, memory contributor-merge-evidence-only / contributor-results-on): contributor PRs with his harness results merge as is and the switches go ON. Exceptions below stay off.

- ON: boattrip, boatplace, retryscope, boatlandstuck (townies only; boats of the active household or of no known household keep EA)
- OFF: none (see rule review: boatcrash-1118 crash verdict not part of his evidence)

## His evidence
- boattrip: 205 logs, 725 boats moved to open water, 642 trip starts, 93 unseen Sims put on another shore; steps 1-2 never fired.
- boatplace: 6 x 10 min IP profiles, 28-47 lot-water boats per job moved.
- retryscope: 39 logs, 38 scope answers re-sent. boatlandstuck: 194 logs, 13 Sims put ashore.
- No off/on A/B, no forced repro.

Household / selected-Sim / unknown-scope read of the hook bodies: ts3-rule-reviewer pass on the import branch, 2026-11-20.

Maintainer changes on import after the household read (not in his PR): boattrip step 4 (get out on a shore up to 120 m on) now needs UfWhy 0 (his text says only an unseen Sim moves to another shore); boatlandstuck uses BtTownie(Actor) and BoatTownie(Target) instead of IsNpcSim (no-household Sims and boats with a household Sim aboard keep EA); boattrip sweep rethrows ResetException; boatprobe bounds checks compare 64-bit. His crash verdict for the boatcrash-1118 queue is not part of the evidence.
