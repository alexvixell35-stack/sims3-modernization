# 2026-11-20: unseenlanesnap + unseenlegsnap ON (PR #509, import/dalexlol-509-1120)

By Dalexlol. Contributor data from PR #509 (his 17-job soak comment 2026-11-19 and evidence summary); not rerun here.
Rule (Rodney, memory contributor-results-on): contributor results with harness evidence merge as is and go ON. Rodney 2026-11-19 on the PR: "Results look clean (every switch fired, soak clean). OK to import for next week's set."

| Switch | Default | His evidence | Household check |
|---|---|---|---|
| unseenlanesnap (counters 1456-1470, his 3511-3525 renumbered) | ON (`kUnseenLaneSnap` true) | 17 loads, 493.5 min, all PASS, 0 crashes, 0 access violations; asked 12,253, snapped 3,974 (5 Bridgeport jobs), near 5,203, errors 0, longest snap 500 cm; no script error frame from LaneSnap.cs | `LaneSnapUnseen`: not IsPlayerSim, household present, unseen (SafeUfWhy 0) |
| unseenlegsnap (counters 1471-1475, his 3526-3530 renumbered) | ON (`kUnseenLegSnap` true) | leg ends asked 1,578, not an unseen townie 135, snapped 1,439 | same predicate (RouteSources.cs LegSnap) |

Dependency #452 safespotpool is already on master (carried commit dropped).
