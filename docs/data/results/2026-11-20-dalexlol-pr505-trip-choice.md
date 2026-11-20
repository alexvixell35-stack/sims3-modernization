# 2026-11-20: icecreamdirect + nearvenue ON, stagedarrival OFF (PR #505, import/dalexlol-505-1120)

By Dalexlol. Contributor data from PR #505 (his 17-job soak comment 2026-11-19 and evidence summary); not rerun here.
Rule (Rodney, memory contributor-results-on): contributor results with harness evidence merge as is and go ON; a switch that never acted stays off. Rodney 2026-11-19 on the PR: "Results look clean (every switch fired, soak clean). OK to import for next week's set." (his comment says every switch fired; the numbers show stagedarrival placed 0).

| Switch | Default | His evidence | Household check |
|---|---|---|---|
| icecreamdirect (counters 1421-1430, his 3476-3485 renumbered) | ON (`kIceCreamDirect` true) | 17 loads, 493.5 min, all PASS, 0 crashes; calls 1,525, skipped 1,074, truck seen 206, active household lot 226, fell back to EA 0 | a truck, not a Sim; the active household's lot is never skipped |
| nearvenue (counters 1431-1440, his 3486-3495 renumbered) | ON (`kNearVenue` true) | asked 47,566, lowered 14,417, raised 7,202, kept 20,249, aborts 0 | `NvOutOfScope`: IsPlayerSim, no household, role Sims and Sims in a situation keep EA's score |
| stagedarrival (counters 1400-1420, his 3455-3475 renumbered) | OFF (`kStagedArrival` false) | asked 1,137, placed 0: all 35,760 tested candidates refused (inside a lot 16,799, water or not outdoor 18,961). On but never exercised, so not proven; he keeps "needs test" in the title | townies only, would be exempt (IsPlayerSim / unknown scope) |
