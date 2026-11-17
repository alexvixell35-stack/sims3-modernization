# Possible stuck Sims (log only)

Stuck Sims are treated as solved (Rodney, 2026-10-13: many runs with 0 real stuck Sims). Possible cases are recorded here and not researched; revisit only if a case repeats often or one of our fixes clearly caused it.

| Date | Run | Sim(s) | Interaction | Lot | Notes |
|---|---|---|---|---|---|
| 2026-10-13 | bp-bundle1 | Victor Sharpe, Victoria Boxer | Watch Door | Bridgeport Sports Zone | Vanilla bouncer role loop (VelvetRopes.WatchDoor), not stuck; stuck rule now skips it (stuck-rule.ts) |
| 2026-10-13 | bp-idle-unroutable repro | Wogan Hemlock | (stranded by repro) | Hogan's diner | Left on his own (sent to his hospital job); repro needs a Sim with nothing scheduled |
| 2026-11-17 | phase-20261117-0008, phase-20261116-2233 | Girl Clubber | Work out until fatigued 921 min; Take Out Trash 1122 min | AJV Wellness Center; Bridgeport Armory | Stuck from load in both runs, motives frozen, ended by our stationary watchdog after a sim-day; save state, not researched (docs/613 §4) |
