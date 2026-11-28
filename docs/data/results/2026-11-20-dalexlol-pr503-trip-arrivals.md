# 2026-11-20: PR #503 trip arrivals (`visitarrive`, `visitonlot`, `gohomeshell`), imported

By Dalexlol. Contributor data copied from his PR comments (11-19 soak comment, 22:54 evidence summary); not rerun here.

Evidence: 17 fresh game loads (queues etl14k to etl14p, Apex + bridge, 6 worlds, 493.5 min, all 17 PASS), all three switches on in every job:

| Switch | Counter sums over the 17 jobs | Default after import |
|---|---|---|
| `visitarrive` | arrivals without a route 14,274 | ON |
| `visitonlot` | 3 (ak19-sv, an19-tb, ao19-sv) | ON |
| `gohomeshell` | apartment homes asked 6,286; placed 2,157; walked 4,128 (no safe spot 2,696, other floor or lot 1,324, near or no room 108, in view 0); 5 Bridgeport jobs | off, see below |

Crashes 0, access violations 0, no script error with a frame from these hooks. No off arm in the same load: the data shows the hooks act and keep runs clean, not a measured saving.

Why `gohomeshell` stays off: the first version crashed 5 of 6 Bridgeport apartment games in EA's slot attach; the shipped version depends on the native `[EaCrashFixes] LotLevelPastEnd` (PR #449), which the ini default still has at 0. Rodney decides whether it goes ON together with `LotLevelPastEnd=1`.

Scope: townies only (`IsPlayerSim`, no household, `TownieUnseen`, `FarFromViewers`); household, selected and unknown-scope Sims never reach the code. `visitonlot` has no unseen gate (the Sim is already on the lot, nothing is moved): a seen townie on its lot skips the walk to the door.
