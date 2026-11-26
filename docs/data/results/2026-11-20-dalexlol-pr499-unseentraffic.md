# 2026-11-20: PR #499 `unseentraffic` + `npccarexit`, imported

By Dalexlol. Contributor data copied from his PR comments (11-19 soak comment, 22:54 evidence summary); not rerun here.

Evidence: 17 fresh game loads (queues etl14k to etl14p, Apex + bridge, 6 worlds, 493.5 min, all 17 PASS), both switches on in every job. Crashes 0, access violations 0, no script error with a frame from `UnseenTraffic.cs`.

| Switch | Counter sums over the 17 jobs | Default after import |
|---|---|---|
| `unseentraffic` | LiteDriver spot asks 50,498; teleport legs 43,118; driven because seen: car 696, spot 282, corridor 1,846, Sim or vehicle within 4 m 3,965; drive-offs 24,789 (24,671 released in place, 118 driven off seen); leg decision threw 0; 1,354,103 sim minutes held (all in the 5 Bridgeport jobs) | ON |
| `npccarexit` | drive-away exit asks 1,691; household or service exempt 0; first answer from the memo 3; far re-ask 3; learned (NoPath) 5; pending exit plans that then succeeded 1,683 | ON |

No off arm in the same load: the data shows the hooks act and keep runs clean, not a measured saving.

Scope: driverless ambient cars (`CarNpcWithNoDriver` / `LightweightTaxi` on a LiteDriver task); an occupant of any kind exempts the car; seen cars, spots and corridors (camera reach, 80 m of an active-household Sim, on screen) drive as EA's. `npccarexit`: household or selectable occupant and food trucks exempt (`NeHouseholdAboard`); on any doubt EA's answer. Review note (MINOR): `NeHouseholdAboard` checks `IsSelectable` and the active household, not `IsPlayerSim`; the exit driver is an `NpcDriversManager` Sim without a household, so no household Sim is reachable there.
