# 646: NPC loops: unreachable outdoor trash can, wild animals' unreachable lots

2026-11-19, OFF/sweep logs (phase-20261115..19, `ts3native-full.log` / `ts3native.log`) + decompiled script code, built,
**NOT RUN** (package tag `npcloopsb`). Two managed ts3fixes switches, both default off:
`trashoutsidefallback` (§2, `TrashOutsideFallback.cs`, slots 4610-4614; built as 4080-4084 on the combo branch, moved at the upstream PR because main uses those) and
`wildlotskip` (§3, `WildLotSkip.cs`, posted in the pets PR with its own slots; this page keeps its combo numbers 4085-4089). Pure rules: `TrashOutsideFallbackCore.cs`,
`WildLotSkipCore.cs`; self-checks `tools/selftest/trashoutsidefallback`, `tools/selftest/wildlotskip`.

## 1. What the logs show

| Loop | Evidence [measured, log] |
|---|---|
| Bridgeport towers: NPC DisposeOutside | 1375 fails in 109 runs, 541 not cut by our fallbacks. Lot 0xF59000350AF186A0: the Sim holds the TrashPile on floor 1 (y 64.8, room 8), the lot's only outdoor can is 30 m below (592.0, 34.8, 1734.5), plan `FailedBecauseNoPathToDestination` with 0 nodes in 0 plans (0 ms). Bubbles, Jet and Blossom Ditto, Violet Atkins and Renee Littler retry all session (`routecause` lines, `routecauselog`, docs/580 #5) |
| Wild horses: GoToLot result 18 | `DestinationPortalLocked`, all uncut: Twinbrook 457 in 8 runs, Appaloosa Plains 121, Sunset Valley 50, Starlight Shores 39 |
| Raccoon | Meeko spends 15-31 sim-hours on one "Go to Lot" walk; `routecause: Meeko Raccoon ..., GoToLot, Plan FailedBecauseDestinationPortalLocked` |

Most wild animal `routecause` lines in these runs say `household`: whole runs with no selected Sim, so every Sim's scope
is unknown and counts as household (PlayerSims.cs). Both fixes keep EA there; they act only while a Sim is selected.

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

Counters: [4610] non-forced fallbacks, [4611] bags put down (EA's failure, lot remembered; or nothing else takes the bag),
[4612] EmptyTrash picks refused, [4613] exempt, [4614] errors (and bags we had to put down after a call).

Log (at most 10 per real minute, 300 per session):
`trashoutsidefallback: <Sim>: no path to the outdoor can, bag put down by EA, lot remembered for 4 h at lot 0x<id> (<name>), level <n>`,
`trashoutsidefallback: <Sim>: outdoor can unreachable, EA's non-forced disposal at lot ...`,
`trashoutsidefallback: <Sim>: outdoor can unreachable, nothing else on the lot, bag put down at lot ...`.

Limits [inferred]: the memo is per lot, so a ground-floor resident who could reach the outdoor can also gets the non-forced
disposal for a few hours. The indoor cans of a remembered lot stay full for NPCs until the lot is forgotten.

## 3. `wildlotskip` (default off; `kWildLotSkipHours` 6, `kWildLotSkipWalkHours` 8)

Code path [verified, decompiled `WildHorsePool`, `WildHorses`, `NPCAnimal`, `Raccoon`]:

- `WildHorsePool.HerdBehaviorCallback` (repeating alarm) asks `WildHorses.FindGoodLot(lead)` when it is time to change lots,
  and again whenever the lead horse stands on the street with no GoToLot queued (`WildHorsePool.cs:135-166`). FindGoodLot
  weights every non-port lot, with no reachability test. A GoToLot that fails at once leaves the queue empty, so the next
  callback picks again, often the same lot.
- `NPCAnimal.GetRandomLotToVisit` (raccoon, deer) weights every lot by `ScoreLot` (Raccoon: x2 with outdoor food).
  `SimulateRole` picks again while the animal is on the street and not in a GoToLot. Nothing limits how long a GoToLot runs.

Fix:

1. Note: a failed GoToLot / GoToCommunityLot plan (`DestinationPortalLocked` or `NoPathToDestination`) of a wild horse
   (`WildHorses.IsWildHorse`) or an NPCAnimal role remembers (animal, lot) for `kWildLotSkipHours` sim-hours; for a wild horse
   also (herd, lot), since the herd follows the lead.
2. Pick: when EA's pick is a remembered lot, the pick is drawn again from the same weights with the remembered lots left out.
   This gives EA's own distribution over the other lots (P(x) = w_x / sum of kept weights; the self-check tests the
   proportions). When every weighted lot is remembered, EA's pick stays. FindGoodLot has no swap of its own: wildhorselot's
   `BEp4WhHook` already patches it, so its body now builds the lists with `WhLists(horse, on)` (on = wildhorselot's
   corrected weights, off = EA's loop exactly) and calls `WlsHorsePick` with the same flag. The two switches compose: the
   redraw uses whichever weights produced the pick. `NPCAnimal.GetRandomLotToVisit` is hooked (EA's pick first, then
   `ScoreLot` via reflection for the redraw only).
3. Long-walk guard: `NPCAnimal.SimulateRole` and `HerdBehaviorCallback` first look at each animal. A GoToLot that has run
   `kWildLotSkipWalkHours` since we first saw it is cancelled with `InteractionQueue.CancelInteraction(ia, false)` (a script
   cancel, not RouteFailed, so NPCAnimal's failure teleport does not run) and its lot is remembered. EA's next look then picks
   another lot.

Household rule: wild horses and NPC animals belong to the pet household. `Fixes.HouseholdExempt` and `IsPlayerSim` still
keep EA for an adopted, selected or unknown-scope animal. A horse with no Sim yet (`SpawnWildHorse`) keeps EA's pick.

Counters: [4085] (animal, lot) pairs remembered, [4086] picks drawn again, [4087] every weighted lot remembered (EA's pick
kept), [4088] long walks cancelled, [4089] exempt or errors.

Log (at most 10 per real minute, 300 per session):
`wildlotskip: <Sim> (wild horse|Raccoon|Deer): GoToLot 0x<id> (<name>) FailedBecauseDestinationPortalLocked, lot skipped for 6 h`,
`wildlotskip: <Sim> (...): 0x<id> (<name>) skipped, drew 0x<id> (<name>)`,
`wildlotskip: <Sim> (...): every lot skipped, EA's pick 0x<id> (<name>) kept`,
`wildlotskip: <Sim> (...): GoToLot 0x<id> (<name>) running <h> sim-hours, cancelled, lot skipped`.

## 4. Test

- Offline: `TS3_CSC=~/.local/bin/ts3-csc node tools/selftest/trashoutsidefallback/run.ts` and `.../wildlotskip/run.ts` pass.
- Build: `node tools/setup/build-fixes.ts --tag npcloopsb` (refs-resolve-check 0 unresolved); `build-agent.ts` (counters
  `trashoutsidefallback_*`, `wildlotskip_*` in the agent's counter readout).
- In game (to do): an OFF/ON pair on a Bridgeport save with a selected Sim (OFF: `routecause ... DisposeOutside ...
  NoPath` repeats on the tower lot; ON: one `lot remembered` line per lot, then fallbacks / refused picks, no repeats), and
  Twinbrook / Appaloosa Plains for the wild horses (OFF: GoToLot result 18 repeats; ON: `skipped, drew` lines, fewer
  result 18 lines). No repro entry yet.

**Unicorns (2026-11-19):** a unicorn is never treated as wild (`IsUnicorn` returns false from WlsWild), and the long-walk guard leaves a wild-horse herd alone while a unicorn is on, or heading to, its target lot (EA's Summon Wild Horses pushes GoToLot on the whole herd).
