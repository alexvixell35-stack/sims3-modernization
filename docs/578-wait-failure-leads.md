# 578: wait and failure leads: mopping and pet puddles (`mopunreachable`, `petpuddletile`)

2026-11-15, from the wait census and s58 (Bridgeport), built, proven in game (s60, off BROKEN / on FIXED). Both switches are off by default and act on household Sims too (EA bug fixes: the outcomes are EA's with the unreachable tries removed). The other leads of this doc number are not part of this change.

## §3b Mopping: one unreachable puddle ends the run and is re-picked (EA bug, household-visible; s58)

s58 (Bridgeport): MopPuddle #3 by route-plan time (96 s), routefails NoPathToDestination / DestinationPortalLocked for household Sims.

[verified, decompiled] `Terrain.Mopping.Run` loops `GetClosestPuddle; MopTile; if (HasExitReason) break`. `MopTile` adds
RouteFailed when `RouteToPuddle` fails or `PuddleManager.CanMopPuddle` names a blocker, so the run ends on that puddle and
returns true. `GetClosestPuddle` only looks at the start puddle's room and level and has no way to exclude a tile. Autonomy
reaches it through `Sim.MopPuddles` / `MopBurntTiles` (`MopFloor`): Test passes while some puddle passes
`IsLocationMoppableBySim` and `CanMopPuddle`; Run pushes Mopping on the *first listed* puddle passing
`IsLocationMoppableBySim` (no `CanMopPuddle`, no reachability), so the same unreachable puddle comes back every time.
`RouteToPuddle` itself is fine (max radius -1 means none; the retry without footprint checks is for mops only).

Fix `mopunreachable` (MopUnreachable.cs, off, [2766-2771]): a copy of Mopping.Run with a per-run skip list. A route that fails
without an exit reason, or a blocked tile, is skipped and the next closest tile is tried; a cancel ends the run as in EA. Only
the first failed route plays the route-fail reaction. The run ends RouteFailed (showing the last blocker) only when it mopped
nothing. A tile mopped at but still dirty without an exit reason is not retried (EA would loop on it). The skipped locations go
into a per-Sim memo (lot, puddle + burnt count, sim-minute) (other lot, changed count or 30
sim-minutes drop it); while it holds, `MopFloor.IsLocationMoppableBySim` answers false for them, so Test stops offering a mop
whose only puddles are unreachable and Run picks a reachable one. Check `MopUnreachableCheck`, repro `mopunreachable-check`.

Other callers re-read: `DrinkPuddle.Run` and `PlayInPuddle.Run` (pets) route to `GetClosestPuddle(lot, Hit)` but remove or
spread the puddle at Hit's tile. Fix `petpuddletile` (off, [2772-2774]): Hit becomes the closest puddle before EA's Run
(GetClosestPuddle from that point returns it again), so the tile removed is the tile used. Check `PetPuddleTileCheck`.
Lead, not built: `AbsorbPuddle` (plant Sims) has Mopping's loop.


The fixed run also adds each mopped tile to the run's skip list, so the loop always ends even if a tile stays listed after mopping.

### Result (s60, Bridgeport, in game)

| switch | off | on | controls |
|---|---|---|---|
| `mopunreachable` | BROKEN: the run ends on the unreachable puddle | FIXED: skipped and remembered | mopped tile, cancel, switch off, end rule and memo expiry as EA |
| `petpuddletile` | BROKEN: the clicked puddle is removed, not the one used | FIXED: the puddle used is removed | same tile, other level, no puddle, switch off as EA |

Repros `mopunreachable-check`, `petpuddletile-check`. Session health after the run: 79 Sims, 0 trapped. The live counters stayed 0 (no natural mop or pet-puddle case in that session).
