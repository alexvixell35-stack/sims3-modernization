# 595. Route-fail leads from the Bridgeport test save (2026-11-16)

Static triage only, nothing built, nothing run. Input: the `ts3fixes: routefail:` lines of the 20 test runs on 2026-11-15/16
(70 phase dirs, 16 with routefail lines, 3290 lines; one 7-Sim household of young adults on one Bridgeport lot, selected Sim
driven by the harness `gohere` commands) plus the decompiled `Run` / route call of each interaction.
`DestinationPortalLocked` rows are ignored (docs/578 §4: the door-lock probe, not a wait).

Verdict: **no lead is an EA bug** of the kind asked for (autonomy offering something whose `Run` can never route, or EA
routing to a slot or point the object lacks). All six are contention inside a crowded house, a placement of this house,
or a planned search / fallback. No switch built, no registry slot used.

| Lead | Lines (routes) | Verdict |
|---|---|---|
| SocialInteractionB -> Sim, Replan NoPath | 306 (28) | contention: partner Sim on the jig B spot |
| WashDishes -> SinkPedestalLounge / SinkCounterCeleb, Replan NoDestination | 74 (39) | contention: slot obstructed or in use |
| Scare -> Sim, NoDestination | 90 (64) | EA's planned fallback (60) + target moved (29); overlaps nothing in `scarepriority` |
| PlayWithMirror -> MirrorWallOval, NoPath | 47 (47) | layout: the mirror's slot is under a double bed |
| UseToilet -> ToiletExpensive, Replan NoDestination | 51 (17) | contention: slot obstructed (in-use check) |
| Fridge_Have / Fridge_EatLeftovers -> FridgeModerate, NoPath | 71 (70) | probe: `PlanFromPoint` search rows, not the fridge route |

## §1 SocialInteractionB (306 lines, 28 routes) — contention, skip
- [measured] Every row is `Replan sync ... NoPathToDestination` with a Sim blocker 0.1-0.5 m from the actor and the goal
  0.2-0.4 m away. ~11 replans per route, then the route gives up. 5 spots only (rooms 6, 8, 14, 10), the same spots in every run.
- [measured] The blocker is the partner: object 0x48EE0034EA1BE0E0 (the `gohere` follower of Suzy Strummer, the
  harness-driven selected Sim) in 210 rows, 0x...E040 (Matty Crewe) in 46; mapping from `gohere start` / `routefail job` lines.
- [verified, decompiled] `SocialInteractionB.Run` -> `ReceiveSocialInteraction` routes to the jig (`SocialJig.RouteToJigA/B`,
  `SocialInteraction.cs`); on failure it sets `ExitReason.RouteFailed` and the pair ends. No autonomy re-offer of an unroutable
  target, no missing slot: the B spot is free once A steps off it. The selected Sim being moved by `gohere` mid-social makes
  this far more common here than in play.
- Not fixable in script without changing jig placement (native); left as is.

## §2 WashDishes on SinkPedestalLounge / SinkCounterCeleb (74 lines) — contention, skip
- [verified] `Sink.RouteToSink`: adult `RoutingSlot_0` via `RouteToSlotAndCheckInUse` (pedestal); `SinkCounter.RouteToSink`:
  `SurfaceUtil.RouteToObjectOnSurface` (counter sink). Both slots exist: the same goals succeed or fail `NoPath` /
  `PortalLocked` in other rows.
- [measured] All `NoDestinationSpecified` rows are `Replan sync` 0.3-1.4 m from the slot: the destination became obstructed
  or in use during the walk (the replan has no free goal left). SinkPedestalLounge's goal 501.5,1551.5 is the same point as
  MirrorWallModern's routing slot in that bathroom (room 10), and the toilet slot is 1.5 m away: three objects share one spot.
- Washing dishes in a bathroom sink is vanilla behaviour (Test only checks `Broken`), not a route bug.
- Side note [verified, not in the logs]: `SurfaceUtil.CanRouteToObjectOnSurface` does not null-check the surface slot
  (`RouteToObjectOnSurface` does). Small lead, not built.

## §3 Scare (90 lines) — planned fallback / moving target, skip; no overlap with `scarepriority`
- [verified, `Sim.BaseScare.Run`] The scarer plans `PlanToPointRadialRange` behind the target (1-2.5 m, 70° cone, only the
  target's room); when that plan fails it falls back to `RouteToDynamicObjectRadius`. 60 of the rows are that first
  `Plan async ... NoDestinationSpecified` (target faces a wall or stands in a small room): EA's intended fallback then runs.
- 29 rows are `Replan sync`: the target moved during the walk (`MakeDynamicObjectAdjustments`), concentrated at two spots
  in room 3. A replan failure skips the fallback, so that one Scare fails and autonomy may pick it again; rare, not an
  unroutable offer.
- `scarepriority` (docs/580 §14) fixes the receiver wait after the route; it does not touch the route. No overlap.

## §4 PlayWithMirror on MirrorWallOval (47 lines) — layout, skip
- [measured] 45 of 47 rows: `Plan async NoPath`, goal 487.5,1554.5 (the mirror's routing slot), blocker BedDoubleModern at
  489.0,1555.5, from 9 different rooms. The mirror hangs on the wall behind a double bed, so its slot is inside the bed footprint.
- A build-mode placement of this house. EA's failure memory (`UpdateBlockTime`, see `autonomyfailblock`, docs/565 §3)
  already blocks and penalizes the retry; ~2 attempts per run.

## §5 UseToilet on ToiletExpensive (51 lines) — contention, skip
- [verified, `Toilet.UseToilet.Run`] `Line.WaitForTurn`, then `RouteToSlotAndCheckInUse(RoutingSlot_0)` (or `PlanToSlot`
  with the shoo-pet callback), then `ToiletSituation.Start` moves other Sims out and routes again.
- [measured] All `NoDestinationSpecified` rows are `Replan sync` 0.5-1.5 m from the slot of one of the two toilets
  (rooms 7 and 10). Harness `gohere` runs end with Sims stuck at 495.1,1539.0, 0.6 m from the room-7 toilet slot
  (`gohere end ... RouteFailed`), and room 10 shares its spot with the sink and mirror (§2). Seven Sims, two toilets.

## §6 Fridge_Have / Fridge_EatLeftovers on FridgeModerate (71 lines) — probe, skip
- Second pass (Contention fixes below): not `EatHeldFood`'s 3-5 m step-away; that interaction logs its own rows (18, PortalLocked only).
- [measured; caller inferred] 65 of 71 rows are `PlanFromPoint sync ... NoPath`, no blocker, goals 2-6 m from the fridge (497.5,1561.5,
  495.5,1556.5, ...), never the fridge slot (502.5,1563.5). They are the place-to-eat search run while carrying the plate,
  which tries candidates and expects failures (same class as the docs/578 §4 probe).
- The few rows that target the fridge slot are `Replan` with a Sim or chair blocker: contention.

## Next
- Nothing to build from these leads. A second save (not this 7-Sim house) would be needed to tell an EA bug from crowding.
- Small static lead: §2 side note (`CanRouteToObjectOnSurface` null slot).

## Contention fixes (2026-11-16, second pass): none built

Asked: make Sims see that another Sim is using or heading to a sink / toilet / jig spot, plus `unreachablememo` and
`eatwander`. Static RE plus the same 20 runs. Nothing built, no switch, no registry slot: each fix would act on a cause the
code and the logs do not show.

**EA already sees users of the same object** [verified, decompiled]:
- Sink: `Sink.RouteToSink` = `Line.WaitForTurn(CutAheadOfLowerPrioritySims)` then `RouteToSlotAndCheckInUse` (ObjectInUse
  after the walk). The dish pick (`ServingContainer...GetBestObjectToWashDishes` -> `CheckInUseAndRoutable`) drops broken and
  unplannable sinks and scores in-use ones down (`kCleanupScoreModifierForInUse`); the queue then waits in the Line.
- Toilet: `UseToilet.Run` = `Line.WaitForTurn`, plan to `RoutingSlot_0`, InUse check, then `ToiletSituation` (privacy).
- Privacy rooms (`PrivacySituation`, `Lot.IsRoomRestricted`) are already in autonomy's `IsAllowedInRoom` filter.

**The failing routes are our harness, not contention** [measured]: for each distinct failing `Replan sync` route, was a
`gohere`-driven Sim (harness follower) logged within 1 m of the goal in the preceding 400 lines?

| Lead | Routes | gohere Sim on the slot | Control: goal +3 / -3 / +6 m |
|---|---|---|---|
| WashDishes (NoDestination) | 42 | 36 | 2 / 8 / 0 |
| UseToilet (NoDestination) | 19 | 19 | 3 / 3 / 0 |
| SocialInteractionB (NoPath) | 9 | 5 | 1 / 0 / 2 |

Every SocialInteractionB `NoPath` blocker is a harness follower (`...BE0E0`, `...BE040`, `...BE060`, `...BE080`, `...BE0A0`;
all appear in `gohere start` lines; Matty Crewe's 46 rows are her follower too). The parked selected Sim is player-directed,
so EA does not shoo it. Fix belongs in the harness (park `gohere` goals off object routing slots), not in the mod.
The 6 sink routes without a nearby harness Sim are too few to tell privacy, bystander or user apart [inferred: any of them].

**`unreachablememo` (general per-Sim memo after NoPath): not built.**
- [verified] EA has the memory: `InteractionQueue.ShouldUpdateBlockTime` -> `GameObject.UpdateBlockTime` for any
  `ExitReason.CausesBlocking` (0x10687, includes RouteFailed): a 1 sim-min hard block for autonomous tests, then a score
  penalty fading over 30 sim-min (`kBlockingTimeToTestOut`, `kBlockingTimeToLowerScore`). `autonomyfailblock` (docs/565 §3)
  covers the posture path EA forgot.
- [measured] Of 575 distinct NoPath routes, 40 are a repeat by the same Sim, interaction, target type and goal in one run;
  14 are MopPuddle (`mopunreachable` covers) and most others are the harness-blocked toilet / sink / plate rows above.
- Invalidation is not airtight: NoPath is very often caused by Sims (dynamic blockers, see above). A memo cleared by object
  move / build edit / door change / lot or room change would keep blocking an object that is reachable again once the
  blocking Sim walks off, which is not vanilla minus failed tries.

**`eatwander` (EatHeldFood step-away): not built.** [verified] `EatHeldFood.Run` wanders 3-5 m (`doRoutFail: true`) before
`BeforeEatingCallback` and `InteractionTest`. But the §6 rows are not that wander: the route-fail logger names the Sim's
current interaction, and `EatHeldFood` runs as a continuation after `Fridge_Have` / `Fridge_EatLeftovers` end; its own rows
are logged as `EatHeldFood` (18, all `DestinationPortalLocked`, no NoPath). The Fridge rows' caller stays unknown
[inferred: a put-down or place search inside the fridge interaction]. Reordering is also not neutral:
`ServingContainerSingle.BeforeEatingCallback` plays the vegetarian retch, so moving it changes what the player sees;
a 1-3 m ring is a placement EA does not offer.
