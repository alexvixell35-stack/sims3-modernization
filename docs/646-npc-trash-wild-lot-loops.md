# 646: NPC loops: unreachable outdoor trash can

2026-11-19, OFF/sweep logs (phase-20261115..19, `ts3native-full.log` / `ts3native.log`) + decompiled script code, built,
**NOT RUN** (package tag `npcloopsb`). Managed ts3fixes switch `trashoutsidefallback` (§2, `TrashOutsideFallback.cs`,
default off, slots 4080-4084, contributor block 4000-4089, docs/registry.md). Pure rule: `TrashOutsideFallbackCore.cs`;
self-check `tools/selftest/trashoutsidefallback`.

## 1. What the logs show

| Loop | Evidence [measured, log] |
|---|---|
| Bridgeport towers: NPC DisposeOutside | 1375 fails in 109 runs, 541 not cut by our fallbacks. Lot 0xF59000350AF186A0: the Sim holds the TrashPile on floor 1 (y 64.8, room 8), the lot's only outdoor can is 30 m below (592.0, 34.8, 1734.5), plan `FailedBecauseNoPathToDestination` with 0 nodes in 0 plans (0 ms). Bubbles, Jet and Blossom Ditto, Violet Atkins and Renee Littler retry all session (`routecause` lines, `routecauselog`, docs/580 #5) |

## 2. `trashoutsidefallback` (default off; `kTrashOutsideFallbackHours` 4)

Code path [verified, decompiled `TrashcanIndoor`, `TrashPile`]:

- `TrashcanIndoor.EmptyTrash.Run` (autonomy) makes a bag (`TrashPileIndoor`) and pushes `TrashPile.DisposeOutside` as a
  must-run continuation. This is DisposeOutside's only caller; the TrashPile itself offers `DisposeTrash`, not DisposeOutside.
- `DisposeOutside.Run` calls `PutHeldTrashInCan(forceOutdoor: true)`: it looks only for `TrashcanOutside` /
  `RecycleBinOutdoor` (and a waste chute). When `RouteToCan` fails, the forced branch calls `PutTrashDown(destroyTrash:
  false)` and returns false. With no outdoor can at all the bag is destroyed (not a loop).
- The bag is now a pile on the floor. Autonomy's `DisposeTrash` (non-forced) carries it to the closest non-full can, which is
  often the indoor can that was just emptied. The can fills again and EmptyTrash starts over.

Fix, for non-exempt NPCs only:

1. The first failure is EA's: the bag is put down. A no-path or locked-portal plan of that Sim seen during the forced call
   (RouteFailLog.RflPlan -> `TofPlan`) marks the lot as remembered for `kTrashOutsideFallbackHours` sim-hours.
2. On a remembered lot, `DisposeOutside`'s `PutHeldTrashInCan` runs EA's non-forced disposal (the same method with
   `forceOutdoor: false`: chute, `Trashcan.FindBestTrashCan`, `RecycleBin.FindBestRecycleBin`). If that search finds nothing
   (EA would then destroy the bag), the bag is put down with `PutTrashDown(destroyTrash: false)`, as EA's forced branch does
   on failure.
3. Autonomy does not pick `EmptyTrash` on a `TrashcanIndoor` of a remembered lot (EA's `true` from its Definition.Test turns
   false; user-directed picks and EA's `false` are unchanged). This is the pick that pushes DisposeOutside, so DisposeOutside
   does not run again on that lot while it is remembered.
4. Held object: after each call we make, a bag that is not destroyed and is still parented to the Sim or in its right
   hand is put down (`PutTrashDown`, not destroyed) and counted as an error. The bag is never lost and never left on the Sim.

Household rule: `Fixes.HouseholdExempt` and `IsPlayerSim` (unknown scope = household; the selected Sim is never touched).
Exempt Sims get EA's forced disposal, never record a lot, and their EmptyTrash picks are not refused.

Counters: [4080] non-forced fallbacks, [4081] bags put down (EA's failure, lot remembered; or nothing else takes the bag),
[4082] EmptyTrash picks refused, [4083] exempt, [4084] errors (and bags we had to put down after a call).

Log (at most 10 per real minute, 300 per session):
`trashoutsidefallback: <Sim>: no path to the outdoor can, bag put down by EA, lot remembered for 4 h at lot 0x<id> (<name>), level <n>`,
`trashoutsidefallback: <Sim>: outdoor can unreachable, EA's non-forced disposal at lot ...`,
`trashoutsidefallback: <Sim>: outdoor can unreachable, nothing else on the lot, bag put down at lot ...`.

Limits [inferred]: the memo is per lot, so a ground-floor resident who could reach the outdoor can also gets the non-forced
disposal for a few hours. The indoor cans of a remembered lot stay full for NPCs until the lot is forgotten.

## 3. Test

- Offline: `TS3_CSC=~/.local/bin/ts3-csc node tools/selftest/trashoutsidefallback/run.ts` passes.
- Build: `node tools/setup/build-fixes.ts --tag npcloopsb` (refs-resolve-check 0 unresolved); `build-agent.ts` (counters
  `trashoutsidefallback_*` in the agent's counter readout).
- In game (to do): an OFF/ON pair on a Bridgeport save with a selected Sim (OFF: `routecause ... DisposeOutside ...
  NoPath` repeats on the tower lot; ON: one `lot remembered` line per lot, then fallbacks / refused picks, no repeats). No repro entry yet.
