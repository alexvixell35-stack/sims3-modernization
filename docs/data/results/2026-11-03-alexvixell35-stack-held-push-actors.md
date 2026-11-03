# PushHeld walks LotManager.Actors, not every world object (2026-11-03)

- Change: `fix/held-push-actors`, `mods/ts3fixes/src/UnseenViewers.cs` `PushHeld` (docs/143 §8.10). Native: main `ca8fc13` asi (no native change).
- Save: the player's old save `1111 kit5` (a long-played Isla Paradiso save, ~14,500 objects, ~68 Sims; not a community save).
- Profile: 11 min, seed 12345, `-disablestoryprogression`, `fixesOn legreplanskip,bouncerskip,idlememo,stuckcheckasync,errortrapspread`, `[UnseenThrottle] Enabled=1 List=1 NeedViewers=1`, the harness autopilot.
- Measurement: a Frida sim-thread sampler (2 x 180 s windows, ~2.9 ms per sample, managed frames), the same script for both runs. The sampler pauses the thread, so the runs are for shares, not it/s.

| job | build | result | object-query samples | `PushHeld` <- `PushViewers` | early gate `utViewerBad` <= 0 |
|---|---|---|---|---|---|
| `old-play-sample` (before) | main `ca8fc13` | PASS | 13.2% / 11.9% | 5.4% / 4.5% (9.7 s / 8.2 s per 180 s) | - |
| `old-play-held` (after) | `fix/held-push-actors` | PASS (and the noisy-machine rerun PASS) | 9.7% / 10.9% | below the top 30 (< 0.03%) | held in both tries |

- Before, `PushHeld` was the top caller of `ScriptCore.Queries` on this save: `Queries.GetObjects<Sim>()` every 5 ticks, which walks every world object to find ~68 Sims [measured, sampler].
- After, it reads `LotManager.Actors` (EA's `sActorList`: `AddObjectToLot` adds a Sim the first time it is put on a lot, `RemoveObjectFromLot` with `destructed` removes it, cleared at world load [verified, decompiled `LotManager`, UPDATE242]). The loop body does not yield or make or destroy Sims, so the list does not change while it runs.
- Not measured here: the held table's content with horses (repro `unseen-horses`, job `bp-ut2-horses` covers it); this save has no ridden horse.
