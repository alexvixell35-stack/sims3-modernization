# 2026-11-20: PR #490 `spawnfix` + `spawnoffworld`, imported

By Dalexlol. Contributor data copied from his PR comments (11-19 soak comment, 22:54 evidence summary); not rerun here.

Evidence: 17 fresh game loads (queues etl14k to etl14p, Apex + bridge, 6 worlds, 493.5 min, all 17 PASS), both switches on in every job. Crashes 0, access violations 0, no script error with a frame from `SpawnFix.cs`.

| Switch | Counter sums over the 17 jobs | Default after import |
|---|---|---|
| `spawnfix` | FireEvent calls 8,394; fan-outs skipped 8,357; full fan-outs run 37; refused 15; spawners seen starting 4,657; handler calls avoided 2,291,156; ms in full fan-outs 20; fishing-spot updates narrowed 8,348 | ON |
| `spawnoffworld` | 208 spawn attempts by spawners out of the world, all skipped (Appaloosa Plains, Moonlight Falls) | off, Rodney call |

No off arm in the same load: the data shows the hooks act and keep runs clean, not a measured saving. `spawnoffworld` stays off because the author marks it as a gameplay change (fewer objects appear); flip `kSpawnOffWorld` and `<kSpawnOffWorld>` in build-fixes.ts to turn it on.

Scope: spawner objects only, no Sim behaviour changes; household, selected and unknown-scope Sims are not touched. `spawnfix` skips a fan-out only when the last and this fan-out are both "no helper" for the same active Sim, so no map tag differs.
