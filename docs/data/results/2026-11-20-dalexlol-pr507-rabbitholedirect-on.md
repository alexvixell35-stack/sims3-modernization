# 2026-11-20: rabbitholedirect + rhoutside ON (PR #507, import/dalexlol-507-1120)

By Dalexlol. Contributor data from PR #507 (his 17-job soak comment 2026-11-19 and evidence summary); not rerun here.
Rule (Rodney, memory contributor-results-on): contributor results with harness evidence merge as is and go ON. Rodney 2026-11-19 on the PR: "Results look clean (every switch fired, soak clean). OK to import for next week's set."

| Switch | Default | His evidence | Household check |
|---|---|---|---|
| rabbitholedirect (counters 1441-1452, his 3496-3507 renumbered) | ON (`kRabbitHoleDirect` true, build-fixes tuning True) | 17 loads, 6 worlds, 493.5 min, all PASS, 0 crashes, 0 access violations; asked 10,945, entered 7,667, followers 392, seen or scope 304, enter animation failed or threw 11 (returned as a failed entry), faded 0; no script error frame from RabbitHoleDirect.cs; no off arm, saving not measured | `IsPlayerSim`, `Household == null`, `TownieUnseen` (seen, followers, riding refuse) keep EA's trip |
| rhoutside (counters 1453-1455, his 3508-3510 renumbered) | ON (`kRhOutside` true) | calls 14,796, placed 2,700, no safe spot 17, same 17 jobs | `TownieUnseen`, world-lot and already-on-lot refuse; SafeSpot placement then EA's RouteOutside unchanged |
