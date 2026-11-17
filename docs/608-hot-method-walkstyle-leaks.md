# 608: Hot-method pass 3 and walk-style requests EA never releases

2026-11-17, static, r65; all three fixes proven in game by their force checks (§6). Next round of the hot-logic hunt
(docs/606, PR #497): the most-called gameplay methods that the earlier passes (docs/586, 587, 604, 607; some are in
open PRs such as #495 and #515) do not name. The hot
methods themselves are clean again; reading what the hottest routing getter (`SimRoutingComponent.UpdateWalkStyle`)
returns led to three walk-style leaks, built as default-off EA fixes. Scan output stays in
`game-copy/analysis/hotlogic/` (local only; it quotes EA names and line numbers).

## 1. Ranking and tools

- **Rank.** `docs/data/compile-candidates.csv` + `docs/data/jit-hot.csv` (EA assemblies, sampled ms/s), and the
  callcensus lines of every `~/dev/ts3-runs/phase-2026111*/ts3native.log`: only 18 methods are censused and all 18
  are covered by the docs/604 pass (`BuffManager.HasAnyElement` 13.7 M calls, `ActionData.Test` 0.8 M, ...). Methods whose
  `Type.Method` is named in docs/586/587/603/604/607 were dropped. The 46 uncovered methods taken (by ms/s): 
  `SimUpdate.Simulate`, `SimClock.ElapsedTimeInMinutes`, `GameObject.GetComponent`, `InteractionObjectPair.Equals`,
  `MetaAutonomyManager.GetTuning`, `Bartending.TryGetHoursOfOperationExtended`, `SimRoutingComponent.UpdateWalkStyle`,
  `MetaAutonomyTuning.IsSomeActiveHouseholdSimAtTheVenue`, `Sim.DoInteraction`, `GameObject.SetCommodityInteractionMap`,
  `Camera.ComputeQuadrantAndDistanceFromLot`, `ReactionBroadcaster.GetInRadiusSims`, `Motive.UpdateMotiveWithDecay`,
  `Motive.UpdateMotiveBuffs`, `InteractionInstance.IsInteractionProvidingBenefit`, `InteractionDefinitionUtilities.CommonTests`,
  `DelegateListener.ProcessEvent`, `AlarmManager.Simulate`, `EventTracker.ProcessEvent`, `Motive.Update`,
  `BuffManager.IsPermaMoodlet`, `InteractionDefinitionUtilities.ScubaLotTests`, `IdleManager.ChooseIdleFromType`,
  `Cane.GetActiveCaneForActor`, `Autonomy.CheckAvailabilityOnLot`, `Motives.UpdateMotiveLerp`,
  `GameObject.ChangeCommodityInteractionMap`, `OccultManager.GetSimOccultInfo`, `SimWalkStyleRules.PostProcessWalkStyle`,
  `DesireCurve.GetYFromX`, `Hud.InteractionQueue.UpdateQueue`, `Lot.CommonVisitTest`, `MetaInteractionScoredByVenue.CalculateScore`,
  `Lot.HowMuchItWantsSimToCome`, `Household.EveryHumanSimDescription`, `Autonomy.CheckAvailability`, `LotManager.UpdateObjectLot`,
  `OccultManager.HasOccultType`, `Motive.EarlyOutMotiveUpdate`, `InteractionObjectPair.TestAndCalculateScore`,
  `CookingProcess.GetSpecificIngredientName`, `HoloDiscManager.Simulate`, plus five property getters (not found as
  method bodies by the walker: `Lot.ShouldSimulate`, `HasZombieDanceStereo`, `CommercialLotSubType`,
  `WeatherManager.UpdateCloudiness`, `RabbitHoleMapTag.IsActive`).
- **hot-logic-walker** on the 46 (49 bodies): 0 hits. With callees to depth 2 from the managed call graph (1,301
  names, 1,082 bodies): 4 NULLAFTERDEREF, the same dead checks docs/604 §2 lists.
- **semgrep** (`tools/static/*.semgrep.yml`) on the 33 files, filtered to the method ranges: 3 hits, the sleeping
  `Simulate` loops of `SimUpdate` and `HoloDiscManager` (they sleep and exit on reset or edit mode).
- **By hand** (the shapes the walker cannot see): `ChooseIdleFromType` (four per-call lists + `ToArray`, a mood
  flavor without an idle entry would throw; tuning covers every flavor [inferred]), `HoloDiscManager` (dead disc ids
  stay in its list until the disc's `Dispose`; no mutation during the walk: nothing in it yields), `UpdateMotiveBuffs`,
  `IsInteractionProvidingBenefit`, `Cane.GetActiveCaneForActor` (per-call `FindAll` list; already cut by `caneearly`,
  docs/74 §6.2), `TryGetHoursOfOperationExtended` (`hoursmemo`, docs/74 §6.3): no new logic bug.
- **NRaas** (Chain-Reaction/NRaas, read for behaviour only): DebugEnabler ships a "Reset Walk" interaction that zeroes
  every walk-style count, and ErrorTrap cleans `WalkStyleRequestHelper` maps: players do end up with Sims stuck in a walk
  style `[community]`. That pointed at the request counts behind `UpdateWalkStyle` (§2-4).
- No REA/Ghidra: no native call decides any outcome here.

## 2. megaphonerun: the megaphone waker runs forever (built, default off)

`[verified, decompiled]` `SimRoutingComponent` keeps one request count per walk style, saved with the Sim; the
highest-priority style with a count above zero is the Sim's style on every route (`UpdateWalkStyle`; `Run` ranks above
`Walk` and `AutoSelect`). `InteractionInstance.RequestWalkStyle` counts through a helper that the interaction's
`Cleanup` releases (also when it is removed from the queue unstarted, `InteractionQueue.CleanupInteraction`);
`Sim.RequestWalkStyle` does not. `Sim.WakeWithMegaphone.Run` (University Life megaphone, "wake up" a sleeping Sim) calls
`Actor.RequestWalkStyle(Run)`, then `Actor.RouteAway(...)` (a `Terrain.RouteAway` continuation; the tunable comment
says "distance to run away from the bed after doing the wake") and never unrequests. The waker, usually a
household Sim, runs on every route from then on, across save and load, until something resets the table (NRaas
Reset Walk).

Fix as EA intended (`WalkStyleLeak77.cs`, `WsMegaHook`, switch `megaphonerun`): after EA's `Run` (in `finally`), if the
Sim's Run count went up, the request moves onto the RouteAway that `Run` queued (the first `Terrain.RouteAway` in the
queue that was not there before): `routeAway.RequestWalkStyle(Run)` then `Sim.UnrequestWalkStyle(Run)`. The Sim still
runs away from the bed and walks again once the route-away ends or is cancelled. With no route-away queued (push failed,
reset) the request is just taken back. EA bug fix, every Sim. Counters 3394 moved / 3395 released / 3396 left with the
switch off / 3397 errors. The method yields: stub `RunOrig`, swap-stack-audit row.

## 3. dragoncurserun: the Phantasmal Curse target runs forever (built, default off)

`[verified, decompiled]` Store Baby Dragon, `PhantasmalCurse.DoAnimation`: after the curse animation it calls
`TargetSim.SimRoutingComponent.RequestWalkStyle(Run)` and `TargetSim.RouteAway(3, 5, ...)` (continuation on the target's
queue) and nothing ever unrequests; the curse buff (`DragonCurseBuffStore`) only adds a woke-up listener. The cursed
Sim (any Sim the picker allows, household included) runs everywhere for good. Same fix (`WsCurseHook` on `DoAnimation`,
target = `GetSelectedObject()`, which is EA's `TargetSim`), switch `dragoncurserun`, counters 3398-3401, stub `AnimOrig`,
swap-stack-audit row.

## 4. vhomewalk: busted-party guests with apartment homes keep running (built, default off)

`[verified, decompiled]` `Sim.MakeSimGoHome` sends a Sim with a virtual home (apartments, Bridgeport penthouses) through
`GoToVirtualHome`, which passes the walk style on to `GoToVirtualHomeInternal.SimWalkStyle`. Every busted party (teen,
kegger, LAN, bonfire, tailgate, masquerade, victory) asks for `Run`. `GoToVirtualHomeInternal.Run` calls
`Actor.RequestWalkStyle(SimWalkStyle)` and never releases it: the GoHome twin of docs/560 §2 (`gohomewalk` covers only
`GoHome`). Most paths fade the Sim out and destroy it, which ends the leak; the active-household return, the autonomous
reaction at the apartment lot (`kAutonomuousReactionChanceOnGoingToVirtualHome`), a cancel or a failed route keep the
Sim in the world, running. Fix as gohomewalk: in the existing `VirtualHomeHook` swap (RouteFixes.cs, no new swap), the
request count is read before EA's `Run` and the one request it added is taken back after it returns. The `virtualhome`
shortcut path never requests. Switch `vhomewalk`, counters 3402-3404.

## 5. Checks, repros, gates

- `MegaphoneRunCheck`, `DragonCurseRunCheck`, `VirtualHomeWalkCheck` (`run` / `result`): the pure decision
  (`WsMovePure`, `VhLeft`) with controls (no EA request: nothing moved) plus a live pass on the selected Sim: EA's
  Sim-level Run request, then the fix's step onto a RouteAway made by `CreateRouteAway` (not queued) or the release; the
  check takes its own request back in both arms. Off: BROKEN (one Run left), on: FIXED. `LiveAdd` probes assert
  the swaps (`WsMegaHook` 1, `WsCurseHook` 1, `VirtualHomeHook` 1). Repros `megaphonerun-check`, `dragoncurserun-check`,
  `vhomewalk-check`.
- In-play triggers (not built): megaphone wake on a sleeping Sim, then send the waker somewhere and read
  `IsWalkStyleRequested(Run)`; Phantasmal Curse on a Sim; bust a party in Bridgeport with apartment guests.
- `tools/static/gameplay-scan.sh`: the unreleased-walk-style grep filtered per file (a file with any Unrequest was
  skipped, and `SimRoutingComponent.RequestWalkStyle` receivers were dropped), which hid §2 and §3; it now lists every
  direct call site (~50 lines; posture pairs such as swimming and ghost walk release elsewhere).
- Gates: build `--on kHitchCauseHooks`, refs-resolve-check, reflect-members-check, fix-repro-check, swap-stack-audit,
  selftest-all.

## 6. In-game result

Force checks, one load, package r65 (`MegaphoneRunCheck`, `DragonCurseRunCheck`, `VirtualHomeWalkCheck`): switch off
BROKEN, switch on FIXED for all three, controls ok, members and swaps live, error counters 0. The live pass on the
selected Sim left one Run request after the route-away with the switch off and none with it on. In-play counter
evidence still needs a megaphone wake, a Phantasmal Curse and a busted Bridgeport party with apartment guests.
