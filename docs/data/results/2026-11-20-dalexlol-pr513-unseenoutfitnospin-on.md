# 2026-11-20: unseenoutfitnospin ON (PR #513, import/dalexlol-513-1120)

By Dalexlol. Contributor data from PR #513 (his 17-job soak comment 2026-11-19 and evidence summary); not rerun here.
Rule (Rodney, memory contributor-results-on): contributor results with harness evidence merge as is and go ON. Rodney 2026-11-19 on the PR: "Results look clean (every switch fired, soak clean). OK to import for next week's set."

| Switch | Default | His evidence | Household check |
|---|---|---|---|
| unseenoutfitnospin (counters 1482-1491, his 3537-3546 renumbered) | ON (`kUnseenOutfitNoSpin` true, build-fixes tuning True); needs `unseenoutfitdefer` (ON on master, PR #457 already imported) | 17 loads, 6 worlds, 493.5 min, all PASS, 0 crashes, 0 access violations; taken 7,251, refused by reason 30 / 7 / 722 / 123 / 188 / 1,011 / 1,096, set ran at once 0, checks threw 0 | `IsPlayerSim`, selected actor, no household and seen / near Sims keep EA's funnel (not counted) |

Dependency #457 is already on master (renumbered there to 1981-1999); only the third commit of the PR was imported.
