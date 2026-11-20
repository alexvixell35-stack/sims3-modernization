# 2026-11-20: Dalexlol PR #590 imported (branch import/dalexlol-590-1120)

By Dalexlol. Numbers copied from his PR text (natural-play harness games with the switches on; counters moved, run-verdict PASS jobs); not rerun here.

Rule (Rodney, memory contributor-merge-evidence-only / contributor-results-on): contributor PRs with his harness results merge as is and the switches go ON. Exceptions below stay off.

- ON: ClosestWpsCut=1 (jobs with t_kdJobScope 2 only, past their deadline)
- OFF: none

## His evidence
- 267 games without: 70 sim-thread waits >= 1 s on the routing lock (max 14 s); 130 games with: 0. Different games and builds, one arm, no A/B.
- Native selftest section 11a2 (cut, count only, seen NPC, household, unknown scope, before deadline, no deadline).

Household / selected-Sim / unknown-scope read of the hook bodies: ts3-rule-reviewer pass on the import branch, 2026-11-20.
