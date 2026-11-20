# 2026-11-20: safespotdedupe ON (PR #511, import/dalexlol-511-1120)

By Dalexlol. Contributor data from PR #511 (his 17-job soak comment 2026-11-19 and evidence summary); not rerun here.
Rule (Rodney, memory contributor-results-on): contributor results with harness evidence merge as is and go ON. Rodney 2026-11-19 on the PR: "Results look clean (every switch fired, soak clean). OK to import for next week's set."

| Switch | Default | His evidence | Household check |
|---|---|---|---|
| safespotdedupe (counters 1476-1481, his 3531-3536 renumbered: clashed with Wishes86 on master) | ON (`kSafeSpotDedupe` true, build-fixes tuning True) | 17 fresh loads (etl14k-etl14p, 6 worlds, 493.5 min, all 17 PASS, 0 crashes, 0 access violations). Sums with the switch on: fresh retry found the same spot 5,856, another spot 2, none 1,874; repeats stopped 5,854; second verified try skipped 6,793. Also 156 earlier local games: about 38,000 FindGoodLocation calls fewer. Problems seen: none. No off arm: acts and keeps runs clean, saving not measured | `SafeSpot` is only reached from the trip shortcuts for unseen townies; the switch moves nobody and drops only a repeated deterministic search, same spots as before |
