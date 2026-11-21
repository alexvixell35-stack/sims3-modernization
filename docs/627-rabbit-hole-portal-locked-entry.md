# 627: "portal locked" route fails to rabbit holes, and relaxed entry for unseen Sims

Written 2026-11-17. **Static and log analysis only, no game run.** Package r80. Slots 3580-3599. Switches `routefailprobe`, `rhentryrelax`, `rhentryrelaxnear`, all default off.

## 1. The lead

110 run logs (`phase-2026111*/ts3native.log`) show household Sims "failing" to route to their own job:
`routefail: <Sim> ... active, WorkInRabbitHole target DaySpa, PlanFromPoint sync FailedBecauseDestinationPortalLocked blocker None, goal ..., cmd 4`.
There are 144 such lines in 40 logs (PoliceStation 48, Bookstore 34, DaySpa 30, Diner 15, CityHall 10, Bistro 4, Grocery 3). Over all interactions there are 3,836 `PlanFromPoint sync ... PortalLocked` lines.

## 2. What they are: NPC spot probes run on the selected Sim (`routefailprobe`)

**There is no effect in game on the working Sim [verified, logs + decompiled].**
- EA's entry route is `Route.PlanToSlot(RabbitHoleProxy, EnterSlots[])` (ilspycmd `RabbitHole::RouteNearEntranceAndEnterRabbitHole` IL_02bf). Our log calls that a `Plan`, never a `PlanFromPoint`.
- All 95 `gohere` commands carrying a WorkInRabbitHole PortalLocked line belong to the **selected** Sim (`active selected selectable`). In the same command, the Sim's next `Plan async` succeeds, and `gohere end` reports `route ok`, 2 m from the rabbit hole slot. Example: phase-20261114-1937 cmd 27 (Kirby Wise to CityHall), attempt 1 is the probe, attempts 2-4 are `Plan async Succeeded`, and the Sim reaches the door.
- The goals are the same lot corners whatever the Sim is doing. For example, 618.5,34.8,1751.0 shows up under ReadBook, BedSleep, WorkFromHome, GetInCarpool and WorkInRabbitHole. 815.1,29.4,1658.2 is the corner of lot CD30 already traced in docs/582 B5 §8. The `routecause` lines list the locked doors as upper-floor apartment doors (y 59-84, rooms such as 9/13 and 4/10).
- `Sim.IsPointInLotSafelyRoutable(lot, pos)` plans from `pos` to the lot's four corners and edge midpoints with `PlanFromPoint`. EA calls it on `PlumbBob.SelectedActor` / `Sim.ActiveActor` as a stand-in for an NPC being placed: `SimDescription.Instantiate(Lot, ResourceKey)` (the resident's NPC-door slots and the elevator spot), `Service.CreateSim` (same), and `Service.GetPositionNearLotCorners`. The route carries the selected Sim's door rights (`CommonDoor.IsAllowedThrough` through event 59, docs/92), so a unit door locked to its residents, an NPC door to a hidden room, or a Nobody lock gives result 18 (docs/548: a locked door on the way to the goal room).
- `RouteFailLog.RflPlan` takes the Sim from `route.Follower` and the interaction from `CurrentInteraction`, so the probe is logged as the selected Sim's failure, counted in [866], and counted as a failed `gohere` attempt.

**What the probe affects is where the NPC appears.** That is already fixed by `residentspawnslot` and `residentspawnwalk` (docs/582 B5/B8 §8, proven s56).

**Our switches are not involved.** Lines appear in 132 runs whose `earlyfix.txt` has none of `doorlockowner`, `doorprivacynull`, `shelldoorsummon`, `tpdoorrights` or `doorgen`. They also appear in both arms of tp-on/tp-off, dlo-on/dlo-off, memo/shadow (DoorMemo) and doorgen, and in the old-asi bisects. The mechanism is EA's own code. There is no ts3fixes-less baseline to compare against: `routefail` is a ts3fixes log line.

**Fix (`routefailprobe`, logging only).** FewerPlans' `IsPointInLotSafelyRoutable` hook records the probed spot for each task. A failed `PlanFromPoint` inside that check whose start is more than 1.5 m from the follower is a probe. With the switch on, it is written as `spot probe at ... (stand-in for a placed NPC, not this Sim's route)` and kept out of [866] and the `gohere` attempts. A check of the Sim's own spot (`IsInSafeLocation`, `AttemptToFindSafeLocation`) stays the Sim's. Counters: [3580] tagged, [3581] counted as the Sim's (switch off), [3582] errors. Check `RouteFailProbeCheck`, repro `routefailprobe-check`.

## 3. Ranked rabbit hole failures (all result codes)

Source: native `routefail job:` lines with a rabbit hole target, which cover every Sim, seen or unseen. 2,292 lines.

| # | result | how | count | after |
|---|---|---|---|---|
| 1 | 16 NoPathToDestination | `cut (UnseenFallback)`: a long plan to a rabbit hole cut at the 150-220 ms deadline, unseen Sims | 2,218 | 1,892 retried and succeeded at backoff level 1, 73 at level 2; 145 retries failed; 108 not retried |
| 2 | 1 Succeeded but cut | `cut (UnseenFallback, mask 1)` | 21 | |
| 3 | 16 uncut | real no-path | 19 | |
| 4 | 10 NoDestinationSpecified | uncut | 13 | |
| 5 | 18 DestinationPortalLocked | uncut | 1 | |

By target: Hospital 793, Diner 339, CityHall 330, Bistro 289, Bookstore 273, MovieCineplex 150, SchoolRabbitHole 55, Grocery 42. By interaction: AutonomouslyVisitHospital 763, GoForMealInRestaurant 623, ShopInRabbitHole 313, GoToCityHall 308, AttendShow 152, WorkInRabbitHole 74, GoToSchool 55.

Script-side `routefail` lines with a rabbit hole target: 153 in total. 139 are the §2 probes; 13 are `Replan NoPathToDestination blocker Sim` (a crowd on the slot, docs/548 (c)); 1 is `Plan NoPath blocker Object`.

**What follows from this:** placement at the door is rarely the failure. The common case is a long cross-town plan for an unseen Sim. The relaxed entry below targets the rest: a plan that fails outright, where EA ends the interaction (no retry on a plan failure; the 3-attempt loop only repeats a failed `RouteOutside`).

## 4. Relaxed entry for Sims nobody sees (`rhentryrelax`, `rhentryrelaxnear`)

These are EA's own relaxed entries [verified, decompiled; ilspycmd for the entry method]:
- **Route-less arrival.** `Subway.OnPortalStart` and `HoverTrainStation` call `AnimateEnterRabbitHole(s, slot, doRoute: false, RouteMetaType.None)`. `HoverTrainStation.PlaceArrivingSim` and `AnimateExitRabbitHole` put the Sim on the slot with `SetPosition` / `SetForward`.
- **Another door of the same rabbit hole.** `AnimateExitRabbitHole` uses the `ExitSlots`, all of which are in `SlotToSlotInfo` (with a door number for the door animation).
- **EA's retry rule** (the `RouteOutside` loop): `RemoveExitReason(RouteFailed)`, then stop if `HasExitReason(Default)`.

The hook `RheEnterHook` on `RouteNearEntranceAndEnterRabbitHole` chains after `RabbitHoleHook`. With the switch off, only EA's body runs. With it on, EA's body runs first, and the relaxed path is considered only when all of these hold:
- EA's method returned false;
- the Sim carries `RouteFailed`;
- `beforeEntering` never ran (a wrapper delegate records it);
- the Sim is standing alone (no followers, horse or carried child);
- the Sim is not a player Sim (`IsPlayerSim`: household, selectable) and not the selected Sim.

- **Unseen tier** (`rhentryrelax`). Who: `TownieUnseen` (not on screen by `UcOnScreen`, and `FarFromViewers`, more than 80 m), with the door also off screen and far from viewers. The tier picks a random enter slot (EA's own pick), runs `beforeEntering`, re-checks unseen at the moment of action, then calls `SetPosition` / `SetForward` on the slot and `AnimateEnterRabbitHole(doRoute: false)`. Counters: [3584] arrivals, [3585] kept EA, [3586] beforeEntering refused, [3587] errors.
- **Middle tier** (`rhentryrelaxnear`, needs `rhentryrelax`). Who: not a player or selected Sim, and not on screen (`World.IsObjectOnScreen`), but near viewers. The Sim walks a fresh `PlanToSlot` to the rabbit hole's `ExitSlots` that are not `EnterSlots`, then enters there with `doRoute: false`. There is no teleport and no placement. On-screen is checked before the route starts. Counters: [3588] entries, [3589] no alternate slot or its plan failed, [3590] on screen at the moment of action, [3591] entry refused after the walk, [3592] errors. [3593-3599] spare.

Household, selected and on-screen Sims keep EA's path. When the relaxed path does not act, `RouteFailed` is put back, so EA's failure stands exactly as it was.

Checks: `RhEntryRelaxCheck` and `RhEntryRelaxNearCheck` (pure tier decision with controls, plus a live count of Sims heading to a rabbit hole by tier). Repros `rhentryrelax-check` and `rhentryrelaxnear-check`.

Not done: cutting EA's retry sleeps or plan time for the middle tier. `kRouteAttemptsToEnterRabbitHole` is a shared static read across yields, and native plan deadlines belong to the UnseenFallback scope, which does not include seen Sims.

## 5. Open

- Live run: off then on for each Check. Look for [3580] growing with the selected Sim at work while NPCs spawn, and for [3584]/[3588] when entries fail.
- A "seen" Sim that is not on screen still gets uncut long plans (8,573 long jobs in the seen scope vs 39,021 unseen). The middle tier only acts after a failure; a deadline for that scope is native work.
