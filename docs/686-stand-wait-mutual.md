# 686: Sims waiting on each other and on blocked spots (route waits)

Contributor doc, 2026-11-21/22. On the contributor's combo branch this file has §1-§13 (obstacle stand-and-wait,
portal lanes, pets, chores, beds); this copy carries §10, §12 and §16. The section numbers are kept
so the code comments (`docs/686 §10`, `§12`) match. Built with `node tools/setup/build-fixes.ts`; nothing here needs ts3native.

## 10. A slot closed by furniture or a wall is used anyway (`blockeduse`, `blockedusehh`; 2026-11-22, `feat/blockeduse-1122`, built, default off; forced repro run, §10.6)

A player's wish: "if he can't get in because there's something in the way he could just go in, and bed, if one of the
sides is next to the wall just teleport him to the animation and then back". Clipping on the last stretch and a short
jump onto the slot and back are fine; ordinary walking stays real. `BlockedUse.cs`, rules in `BlockedUseCore.cs`.

### 10.1 How object use reaches a slot [verified, decompiled `Sims3.Gameplay`, `ScriptCore`, unless marked]

- `RoutingComponent.RouteToSlot` (one slot, or a list with an optional preferred slot: 735 call sites with its
  `...AndCheckInUse` / `RouteToSlotList` wrappers) makes a route, adds one slot destination (`Route.PlanToSlot`), plans
  and calls `DoRoute`. `SimRoutingComponent.RouteToUnusedPart` (beds, seats; 20 sites) adds one slot destination per
  part: weight 0 for a part in use, 1000 for the preferred part, 500 for the others, plus a "near the object" goal
  while the Sim is not close; once close, only the slots of free parts are left. So the game takes whichever free bed
  side it can reach; a side that cannot be reached is only a failure when it is the last one free.
- `SimRoutingComponent.DoRoute` tests the plan result first. A failed plan: route-fail behaviour (a walk to 1-3 m from
  the blocker in its room, the balloon), exit reason `RouteFailed`, `false`; the interaction ends. Afterwards the
  callers read the route's plan result only: the slot's name hash, and `RouteToUnusedPart` the end point, which it
  compares with the slot's place to find the part.
- The route's destination list is native and cannot be read back, so the slots a plan wanted are noted when they are
  added (`Route.AddDestination`, cleared with `Route.ClearDestinationList`).
- The game has its own switches for a relaxed plan: `PassThroughObjects` (used for ghosts, with `PassThroughWalls`)
  and `UserOverrideSlotObstruction` + `DoSlotObstructionChecksUserOverride = false` (carry, repair, houseboat routes).
- Not covered by a route at all: interaction tests that plan to a slot and hide or grey the interaction when the plan
  fails (`Sink.CanRouteToSink` and others among the 178 direct `PlanToSlot` sites).

### 10.2 Measured (storm logs `phase-*`, `routefail:` lines, read-only)

56,328 failed plans in 1,042 logs (household Sims; storms on crowded lots, so Sims and locked doors dominate). Of the
6,622 from object-use interactions (bed, tub, shower, toilet, sink, fridge, seats, counters, mirrors):

| result, blocker | lines |
|---|---|
| no path, no blocker named | 1,886 |
| destination portal locked | 1,826 |
| no destination (every wanted slot refused before the search) [what refused them: not logged] | 1,719 |
| no path, a Sim | 735 |
| no path, furniture | 403 |
| no path, a wall | 16 |

Furniture cases by pair: `TakeBath` blocked by a litter pile 107, mirror interactions by the double bed 127, toilet by
a dining chair or bar stool 18, tub by a dresser 14, sink / toilet by the crib or the tub 31. No log line says how
often a bed side at a wall fails: with the other side free the plan succeeds.

### 10.3 What is built

In `DoRoute`, for a failed plan whose route has slot destinations on an object, result "no path", "no destination" or
"slots / points obstructed", blocker not a Sim:

1. Walk up. Unless the Sim stands in the object's room, on its level, within 3 m of a wanted slot, the game's own
   "near the object" plan (1-3 m, the object's room: the plan of its route-fail behaviour) is walked as a normal
   route. No such plan = the object is out of reach (another level, a closed room): the game's behaviour stays.
2. Clip. The route is planned again with "pass through objects" and "no slot obstruction test"; walls still count. A
   plan of at most 6 m whose end no Sim stands on is walked; the options are put back afterwards.
3. Snap. Otherwise the Sim is put on the nearest wanted slot within 4 m (1 m in height) that no Sim stands on, facing
   as the slot does, and the route answers "arrived" with that slot's hash and place, which is all the callers read.
   A slot behind a wall (a bed side at the wall) takes this branch: the way round is longer than 6 m or there is none.
4. Out. The Sim is remembered with the spot it stood on before step 2 or 3. Later, when it plans a route (`Plan` /
   `Replan`), is done with that interaction, sits or lies in nothing and is still at the slot: a plan that succeeds
   ends the entry (it walks). A plan that fails is followed by one plan to the remembered spot; if that fails too the
   Sim is put on that spot (or, if that spot is not routable any more, on a spot the game's placement search finds
   near it) and the plan is made again.

Scope: `blockeduse` = Sims the player does not control, `blockedusehh` = household and selected Sims, each alone
(`IsPlayerSim`; `[Experimental] HouseholdToo` does not lift it: this is a teleport). Humans only, not riding, not in
a vehicle. Not with a Sim, a door or stair, a rabbit hole or a vehicle as the object; not on the world lot. Go Here and
every other point route has no slot destination and never comes here. A case that kept the game's behaviour is not
tried again by that Sim on that object for 30 sim-minutes. Off = compare mode: 4414 / 4415 count.

Counters 4414-4429 (`kCounterSlots` 4430): failed slot plans / would / exempt / walked up / clip / snap in / snap out /
walked out / not this kind / kept: out of reach / kept: no slot in range / walk up stopped / errors / clip walk
stopped / snap out to a found spot / kept: cooling down. Lines `blockeduse: <who> <Sim> -> <Object> ...: clip | snap in
| kept`, `blockeduse: ... snap out from ... to ...`, `blockeduse: error at <site>` (20 a minute, 300 a session).

### 10.4 Limits [inferred unless marked]

- "No destination" is taken as "every slot refused by an obstruction" from the logs' shape; a plan that answers it for
  another reason costs one walk up and one extra plan, then keeps the game's behaviour and cools down.
- A snap is a visible jump of up to 4 m; a clip walks through furniture for up to 6 m. The animation that follows
  plays where the game would play it; with a wall at the slot the Sim stands in or behind the wall for its length.
- The way out waits for the Sim's next plan. A Sim with nothing to do stands on the closed slot until autonomy or the
  player gives it a route.
- Three swaps are new: `Route.AddDestination`, `Route.ClearDestinationList` (no yield) and
  `SimRoutingComponent.DoRoute` (yields; stub, `savedstacks`). Every Sim saved mid-route has a frame in the `DoRoute`
  hook; its body is two lines on purpose, since a build that changes it resets those Sims once at the next load
  (docs/242 §2). The earlier `DoRoute` hook was removed for that reason because it only logged.
- Pets, interaction tests that plan to a slot (10.1), jig routes (socials, `SocialJigTwoPerson` blockers) and objects
  the Sim reaches by a radius route (TV, easel at a distance) are not covered.

### 10.5 Checks (2026-11-22, no game) and run plan

- `node tools/selftest/blockeduse/run.ts`: 45 checks of the rules. `BlockedUseCheck run | result | lot` (`lot`: plans
  of the selected Sim to every routing slot of the lot's tubs, showers, toilets, sinks, beds, chairs and sofas, lists
  the objects with a closed slot). Repro `blockeduse-check` (rules, counters). The forced repro `blockeduse-force` (a room built with
  `build_layout`, a fridge put on the toilet's slot with the agent's `put_in_front` step) stays on the contributor's
  combo branch until that agent step is upstream; §10.6 gives its results.
- Run plan, about 30 min of game time: 1. load, `ts3fixes applied` must show `SimRoutingComponent.DoRoute: ok`,
  `Route.AddDestination: ok`, `Route.ClearDestinationList: ok`; save and load with the switches off, Sims mid-route
  must come back (`savedstacks` 761 = 0). 2. `blockeduse-force`. 3. By hand: a double bed with one side at a wall and
  two sleepers; a tub with a dresser in front. 4. One storm with both switches on.
- Pass: 4426 = 0, no `blockeduse: error at` line, no new script errors, the Sim ends on a routable spot after use
  (4420 or 4421 follows every 4418 / 4419), no Sim left standing in a wall, `gohere` lines unchanged.

### 10.6 Results (2026-11-22, Steam Deck, Sunset Valley new game)

- Forced repro `blockeduse-force`, run bu-r11 (exit 0): a two-Sim household on an empty lot, a toilet at a wall with a
  fridge on the tile in front of it. Before each arm `BlockedUseCheck lot` showed `ToiletModerate ... 1 of 1 slots
  closed (FailedBecauseNoPathToDestination blocker Object)`.
  - off: `routefail: ... UseToilet ... FailedBecauseNoPathToDestination blocker Object` (the fridge), `would` 1; the Sim
    does not use the toilet.
  - on: `blockeduse: ... clip 1.0 m to slot ..., walked up first`; counters walked up 1, clip 2 (the interaction routes
    to the slot twice), walked out 1, errors 0.
- The same off / on by hand in a second session (bu-m1).
- Not exercised yet: snap in / snap out (a slot the clip plan cannot reach, such as a bed side at a wall). Not
  confirmed by eye: no screenshot of the Sim seated. Not run: a save and load with a Sim mid-route (the `DoRoute`
  swap is installed whatever the switches say, and its stub keeps saved stacks loading).

## 12. A chore whose spot is closed is done from within reach (`reachuse`, `reachusehh`; 2026-11-22, `feat/reachuse-1122`, built, default off, rule 1 run for a pile §12.7)

A player's wish: a firefighter, a maid or a Sim cleaning should do the job from a step further away when the place
is blocked, not give up. `ReachUse.cs`, rules in `ReachUseCore.cs`, counters 4446-4461, check
`ReachUseCheck run | result | lot`, selftest `tools/selftest/reachuse`, repro `reachuse-check`.

### 12.1 How the chores walk [verified, decompiled `Sims3.Gameplay` unless marked]

- Every route below ends in `SimRoutingComponent.DoRoute`, whose first test is the plan result: a failed plan runs
  the route-fail behaviour, adds the exit reason `RouteFailed` and returns false (§10.1). A replan that fails during
  the walk ends the same way after the walk.
- Mop (`Terrain.Mopping`: `MopPuddle`, `MopBurnt`): `RouteToPuddle` plans a ring from 1 m round the puddle,
  restricted to the puddle's room and one level; a second plan drops the footprint test near the goal. After the
  walk `PuddleManager.CanMopPuddle` refuses a tile covered by an object or a Sim.
- Fire (`ExtinguishBase.RemoveFire`, pushed for household Sims and by the firefighter's situation as
  `Fire.Extinguish`): `Fire.PlanRouteToExtinguish` plans 0.5-2.5 m in the fire's room, then 0-2.5 m. The caller
  tests the plan before `DoRoute`: a fire whose plan failed is passed over for the next one, and with none left
  nothing is walked. A burning Sim (`Sim.ExtinguishSim`) is a 2 m ring round the Sim, straight into `DoRoute`.
- Pick up, clean away (`CarrySystem.PickUp`, trash and ash piles): `RouteToObjectRadius` in the object's room when
  the thing lies on the floor; a thing on a surface is reached by the surface's routing slots.
- Plants (`Plant.DoPlantRoute`): a ring from 0.7 m round the soil, in its room.
- Wash up at a sink, clean a toilet / tub / stove / counter, repair, fill a pet bowl: routing slots of the object
  (§10's case).
- Picking up a baby or toddler (`PickUpChild`) is a social interaction with its own jig: not a chore route.

### 12.2 Measured (storm logs `phase-*`, `routefail:` lines, read-only)

57,664 failed plans (spot probes left out). "Near" = the goal within 4 m of the Sim on its level. The logs hold no
`standwait: ... near:` lines.

| interaction | failed | near | how it fails near |
|---|---|---|---|
| MopPuddle | 3,614 | 2,296 | no path 743, no destination 1,027, portal locked 512 |
| UseToilet | 2,960 | 1,656 | §10 |
| socials (A, B) | 3,884 | 2,779 | a Sim as target: out of scope |
| WashDishes | 1,253 | 1,146 | sink slots: no destination 841, no path 183, portal locked 96 |
| Fridge / serving (eat) | 1,646 | 1,271 | object use, §10 |
| Clean (toilet, counter, sink) | 468 | 331 | slots |
| PutAwayLeftovers | 172 | 153 | plate: no destination 99, no path 31 |
| ServingContainer_CleanUp | 852 | 112 | plate / bowl |
| PutDownHeldObject | 101 | 92 | the walk to a surface, not to the target |
| CleanSink / CleanBathtub / Stove_Clean | 235 | 171 | slots |
| Snack_CleanUp | 74 | 68 | no path |
| RecycleNewspaper | 133 | 55 | |
| GetHomework | 47 | 47 | no path 35 |
| RepairShower | 68 | 37 | slots |
| PutCarriedChildInCrib | 72 | 55 | crib slots, §10 |
| FillBowl / UseLitterBox / CleanLitterBox / DisposeTrash | 66 | 39 | |
| ReactToFire | 3,206 | 10 | fleeing, not a chore |
| Extinguish | 8 | 0 | the fire's plan is tested before the route: failures do not reach the log |

Gardening interactions do not appear (the storm lots have no gardens).

### 12.3 What is built

1. Point routes in `DoRoute` (the hook of §10): a failed plan with no slot destination, either the first plan or
   a replan that ended the walk (the walk answers false, the plan is failed, no exit reason on the Sim; a cancelled
   or interrupted walk keeps its answer). Plan results taken: no path, no destination, obstructed, destination
   blocked by a Sim (the chore is done from beside it); "portal locked" and aborted plans keep the game's
   behaviour. The Sim's running interaction gives the kind and the reach:

   | kind | interactions | reach | why |
   |---|---|---|---|
   | fire | `ExtinguishBase`, `ExtinguishSim` | 4 m, not nearer than 0.5 m | the game's own ring ends at 2.5 m; the spray is drawn from the Sim to the fire |
   | floor | `Terrain.Mopping` | 2.5 m | the game mops from 1 m; a tile and a half more |
   | small | targets `TrashPile`, `AshPile`, `Newspaper`, `Homework`, `LitterPile`, `FoodScrap`, `ToyScraps`; names with `CleanUp`, `PutAwayLeftovers`, `Recycle`, `PickUp`, `ThrowAway`, `DisposeTrash`, `PutInInventory` | 2 m | the game picks up from about 0.5 m; an arm and a tile more |
   | plant | any interaction on an object of `Sims3.Gameplay.Objects.Gardening` | 2 m | the game tends from 0.7 m |

   The route must be the one to the interaction's target (its goal within 1.5 m of the target; a terrain target's
   goal is the place). A Sim that stands within reach, on the target's level (1.5 m in height), with no wall between
   (`World.WallExistsBetweenPoints`, light-blocking walls, the test of the game's own line-of-sight helper) and
   sits or lies in nothing turns to the target and the route answers "arrived". Otherwise a normal plan to the
   nearest point within reach is made, in the target's room first, then in any room; its end must pass the same
   test; the Sim walks it and the route answers "arrived". The interaction then runs where the Sim stands.
2. Fires: `Fire.PlanRouteToExtinguish` (new swap, plans only) answers a failed plan with the plan of rule 1 (a
   plan to where the Sim stands when it is in reach already), so the fire is not passed over.
3. Slot chores (kind chore: names with `Clean`, `WashDishes`, `Repair`, `Unclog`, `FillBowl`, `EmptyTrash`,
   `TakeOutTrash`, `MakeBed`; and the kinds of rule 1 when the route goes to slots): a failed plan to the slots of
   the interaction's target, or of the surface the target lies on, gets §10's rules (walk up, clip, snap onto the
   slot, out again) with `reachuse` / `reachusehh` standing in for `blockeduse` / `blockedusehh`. The animations
   there need the slot.
4. A puddle tile covered by an object (not a Sim) is mopped when `mopunreachable`'s run is in use
   (`MopUnreachable.cs`, its copy of the game's tile step); the game's own run keeps refusing it.

Scope: `reachuse` = Sims the player does not control (townies, service Sims, firefighters, maids, repairmen),
`reachusehh` = household and selected Sims; each stands alone. Humans, not riding, not in a vehicle. Go Here and
every other interaction are untouched. A case that kept the game's behaviour is not tried again for 30 sim-minutes
(per Sim and goal tile). Off = compare mode. `reachuselog` writes one `reachuse:` line per act (capped).

Counters: 4446 failed point plans of a reach kind / 4447 would, off / 4448 exempt (household without `reachusehh`)
/ 4449 fire / 4450 floor / 4451 small / 4452 plant done from range / 4453 of them without a walk / 4454 slot chores
given to §10's rules / 4455 kept: nothing in reach / 4456 kept: other level or lot / 4457 kept: wall between /
4458 walk stopped / 4459 errors (`reachuse: error at <site>`) / 4460 kept: cooling down / 4461 other failed point
plans.

### 12.4 Not built, and why

- `DestinationPortalLocked` (512 of MopPuddle's near failures): a privacy-locked room, §7's subject.
- Walking through objects to a point (clip) for point routes: the Sim would end inside furniture with no slot to
  come out of. Slot routes have it (§10).
- Picking up a baby or toddler: a social with a two-person jig; a Sim as target is only taken for a Sim on fire.
- `PutDownHeldObject`, the walk of a clean-up to the dishwasher, sink or trash can: slots of another object than
  the target; `blockeduse` covers them.
- Burning windows (`Windows.PlanRouteToExtinguish`, a cone in front of the window), pets, the world lot.

### 12.5 Limits [inferred unless marked]

- The interaction's animation plays where the Sim stands: a mop or a pick-up 2 m from the thing shows a gap, a
  picked-up thing jumps into the hand. The mop jig is put on the puddle as the game does [verified].
- `WallExistsBetweenPoints` is read as "a wall crosses the straight line"; a doorway in that wall still counts as
  a wall, so a target seen through a door is kept.
- `GetApproximateOriginalDestination` of a failed plan is taken as the ring's centre; when it is not valid the
  target's place is used.
- The fire plan for a Sim in reach is a plan to its own place; `DoRoute` is expected to answer true for it.
- Replans are most of the failures (MopPuddle near: first plan 434, replan 1,091, plan from a point 765; WashDishes
  near: first plan 10, replan 945), so the hook's entry method `RouteHook` was changed once to pass a walk that
  ended on a failed plan to the rules (`RouteEnded`). [verified, docs/242 §2] A save made by a build with §10's
  hook (2026-11-22 builds only, never released) resets the Sims that were mid-route once at its first load. §10's
  slot rules get such walks too, and their counters 4414-4429 now include them.
- After a failed replan the game may have played its route-fail reaction inside the walk before the chore is done
  from where the Sim stands.
- Which plan a `PlanFromPoint` failure belongs to is not known from the logs; one that does not end in `DoRoute`
  is not covered.

### 12.6 Checks (2026-11-22, no game) and run plan

- `node tools/selftest/reachuse/run.ts`: 53 checks (plan results, kinds, reach per kind, the goal test, the place test, the slot
  rule, the covered tile, scope). `build-fixes.ts` builds, `refs-resolve-check` 0 unresolved; selftest
  `blockeduse` passes; `swap-stack-audit` has the new row
  (`Fire.PlanRouteToExtinguish`, stub `PlanOrig`).
- Live: `reachuse-check` once (off `BROKEN`, on `FIXED`, `members found=True`; `fix_applied`
  `Fire.PlanRouteToExtinguish: ok`). A storm save, off / on interleaved with `reachuselog routefaillog`: off arm
  4446 and 4447 up; on arm `reachuse reachusehh` (with `mopunreachable` for rule 4): 4450 > 0, `routefail:` lines
  of MopPuddle with `Plan ... FailedBecauseNo(Path|Destination)` down, 4459 = 0, no `reachuse: error at` line.
  Before the arms: save with a Sim mid-route and load it (the `DoRoute` hook's entry changed).
  `ReachUseCheck lot` lists the puddles, burnt tiles, fires, piles and plants the game's plan cannot reach and
  whether a place in reach exists. By eye: a puddle in a nook behind a chair (mopped from the open side), a
  kitchen fire behind a counter with the firefighter called.

### 12.7 Run in the game (2026-11-22, Deck, Sunset Valley new game) [verified]

**When the game fails at all.** Every ring above starts at about 1 m and has no outer limit, so a pile or puddle
ringed by furniture in a big room is done from any free place of that room, 2 m away or more. The game's plan only
fails where the target's room has no place 1 m away that can be stood on. By hand (session `fr-m1`): a nook ringed
by fridges (cleaned from exactly 1.0 m), a closet 1 x 2 with the pile in its middle (cleaned from the far wall,
exactly 1.0 m), a closet 1 x 1 (cannot be entered, the door is the blocker; the game fails and the rule keeps: "no
place within 2.0 m can be walked to", the places outside are behind the wall).

**Repro `reachuse-force`** (run `ru-r2`; it stays on the contributor's combo branch: its agent step `add_trash` is not upstream yet): a closet one tile wide and two deep, a trash pile 1.3 m behind its door.

| | off | on (`reachuse reachusehh`) |
|---|---|---|
| Clean Up, Sim at the mailbox | `routefail: ... DisposeTrash target TrashPile, Plan ... FailedBecauseNoPathToDestination blocker Object ... DoorSingleCountry`; the pile stays | the same failed plan, then `reachuse: selected Bea Reach, DisposeTrash -> TrashPile at ... (small, FailedBecauseNoPathToDestination): from 0.0 m at ..., walked up`; the pile is carried to the outdoor can |
| Counters | tries (4446) 1, would (4447) 1 | small (4451) 1, errors 0 |
| `ReachUseCheck lot` after | targets 1, closed 1, in reach 1 | targets 0 |

- The Sim walks onto the pile ("from 0.0 m"): the rule's own plan has no least distance for the small kind
  (`MinDist` 0) and prefers the place nearest the target. The pick-up plays there. Not changed.
- A puddle on the closet's far tile is mopped by the game itself from the closet's middle: no case.
- `ReachUseCheck lot` made one plan in any room and read "wall between" for a pile in a closet; it now plans as the
  rule does, the target's room first, with the game's 1 m ring for piles (fixed, check only).
- Open [inferred, not run]: for a puddle the rule may end within 0.6 m of the tile's centre, where the game's
  `PuddleManager.CanMopPuddle(ignoreSims: false)` counts the mopping Sim itself as blocker and plays the route-fail.
  No layout in these runs produced a failed mop plan with a place in reach, so it is not changed.
- Not run: fires, plants, slot chores (rule 3), replans that fail during the walk (most of §12.2's failures).

## 16. No route-fail balloon for looking at things (`viewlenient`; 2026-11-22, `feat/viewlenient-1122`, built, default off, NOT RUN)

A player's remark: "Routing errors contemplating surroundings or viewing something makes no sense." A Sim that
only looks at something needs no exact spot; a failed plan to the chosen spot should not end in the route-fail
balloon when a place with a view is at hand.

### 16.1 How the game routes a spectator [verified, decompiled `Sims3.Gameplay` / `Sims3GameplayObjects`]

| kind | class | route | on a failed plan |
|---|---|---|---|
| contemplate | `Terrain.Contemplate` (Genius trait, autonomous on residential and venue lots) | `PlanToPointRadialRange` 0.3-1 m round the clicked point, valid rooms = the room of one randomly picked object near the Sim | `DoRoute` false: the next object is tried (a balloon each time), with none left "bored" and the interaction ends |
| art | `ViewObjects` (`IViewable`: paintings, sculptures, photos, altars) | `CreateRouteToObject`: a ring of the viewable's min..max distance, one level; in range with a view only a turn | ends |
| performer | `MusicalInstrument.WatchBase<T>` (`Guitar+Watch`, `Drums+Watch`, ...) | `DoRouteForWatchOrDance`: a dance floor near the instrument, else a ring 1 m .. 0.9 x the sound radius in the instrument's room (front half, then all round, then platforms allowed) | ends |
| tv | `TV.WatchTV`, standing | `TV.RouteToCone`: 2-6 m, a 3/8 pi cone in front of the TV, in its room | ends |
| stars | `Sim.WatchTheStars` (two Sims) | `SocialJigTwoPerson.RouteToJigA/B` to a jig placed by `FindGoodDeltaLevelPosition` | both Sims' interactions end |

All of them hand the plan to `SimRoutingComponent.DoRoute`. A plan that failed before the walk gets
`DoRouteFailureBehavior` (the balloon, for a locked door the door reaction) when `Route.DoRouteFail` is set, the
exit reason `RouteFailed` and false. A walk that ends on a failed replan answers false with no balloon of its own
(the balloon there is only for a Sim standing on the goal or a turn jig).

Route failures in the storm logs (`ts3fixes: routefail:` lines of `ts3native-full.log`, all phases) [verified]:

| interaction | no destination | no path | portal locked | aborted | first plan / from a point / replan |
|---|---|---|---|---|---|
| Contemplate (target Terrain) | 296 | 4 | 0 | 0 | 297 / 3 / 0 |
| Watch (Guitar 61, KeyBoard 24, LaserHarp 18, Drums 5, GrandPiano 4, BassGuitar 1) | 1 | 76 | 36 | 0 | 6 / 59 / 48 |
| WatchTV | 6 | 55 | 6 | 0 | 43 / 18 / 6 |
| WatchTheStars (target Sim) | 7 | 25 | 0 | 9 | 35 / 5 / 1 |
| ViewObjects (paintings 4, altar 2) | 0 | 6 | 0 | 0 | 0 / 6 / 0 |

Contemplate's 296 "no destination" plans are the room filter: the picked object's room is not the room of the
point, so the ring has no place at all. `ViewText` (6, a phone) has no route of its own and is left out.
`ListenToGhostStory` (46, a group round a storyteller) and `LookOutWindow` (12, a window's slot) are not in.

### 16.2 What is built

`ViewLenient.cs` / `ViewLenientCore.cs`, no swap of its own: the case is a third step in blockeduse's `DoRoute`
hook (`BuRouteHook.RouteBlocked` -> `RouteReach` -> `RouteView`), reached for a failed plan (the first plan, or a
replan that ended the walk with no exit reason) that blockeduse and reachuse left alone.

- Which: the Sim's running interaction is one of the five kinds (class names, `ViewLenientCore.Kind`), the plan
  failed with no destination, no path, obstructed goal, a Sim on the goal, no free slot or portal locked
  (`PlanKind`), the route would show the balloon (`DoRouteFail`), the Sim is a human on foot.
- contemplate: a standing Sim stays where it is and the route answers "arrived" (the game then turns it to the
  object it picked). A sitting, lying or swimming Sim walks a normal plan to the nearest place within 6 m of the
  point.
- art, performer, tv: the failed route must go to the target (goal within range + 1.5 m). The Sim stands within
  the game's range (the viewable's `MaxDistanceFromObjectToView`; 0.9 x the instrument's `DanceRadius`; 6 m for a
  TV), on the target's level (1.5 m), no light-blocking wall between it and the target (for a painting or a TV a
  point 0.3 m in front of it), for a TV on its front side: it turns to the target, "arrived". Otherwise a normal
  plan to the nearest such place (reachuse's `RuNearPlan`: the target's room first, then any room) is walked and
  tested again at its end.
- stars, or no such place (also a walk that was stopped): the game's own answer (exit reason, false) with the
  route's `DoRouteFail` cleared for that call, so no balloon. A target with no place is not searched again by
  that Sim for 30 sim-minutes (quiet end at once).
- Scope: `viewlenient` = Sims the player does not control, `viewlenienthh` = household and selected Sims, each
  stands alone. Off = compare mode (counted as "would"). `viewlenientlog` = one `viewlenient:` line per act.
- `ReachUse.cs` lost its copy of the search: `RuPlace` and `ViewLenient.cs` share `RuNearPlan` and `RuWall`.

Counters 4510-4525 (`kCounterSlots` 4526): tries 4510, would 4511, exempt 4512, handled by kind: contemplate
4513, art 4514, performer 4515, tv 4516, stars 4517; by outcome: stayed in place 4518, walked to the nearest
place 4519, quiet ends 4520; kept as the game has it 4521, walk stopped or ended with no view 4522, errors 4523
(`viewlenient: error at <site>`), quiet ends while cooling down 4524, tries after a walk 4525.

### 16.3 Limits [inferred unless marked]

- Not in: socials, Go Here, every interaction not named in §16.1, routes to routing slots (blockeduse), aborted
  plans (WatchTheStars has 9), pets, riders, Sims in vehicles.
- Watch the Stars is a pose for two on a jig: only the balloon goes, the interaction ends as before.
- Contemplate in place may look at an object in another room (the game picks it before the route).
- A Sim that stays in place turns at once (`SetForward`, as reachuse does), not with a turn animation.
- A failed route onto a dance floor near an instrument (`RouteOntoDanceFloorAndFaceObject`) is answered the same
  way when it would show the balloon: the Sim watches from the nearest place and is entered in that dance floor's
  use list.
- A TV watched from a place the game's cone would not give (up to 6.5 m, anywhere on the front side): `WatchTV`
  runs its own facing test after the route.
- `RouteReach`'s body changed (it calls `RouteView`): a Sim saved in a reachuse walk by an older build resets once.
- The household switch changes what a player-ordered View / Watch does when its spot is closed (it is done from
  the nearest place); Go Here is never touched.

### 16.4 Checks (2026-11-22, no game) and run plan

- `node tools/selftest/viewlenient/run.ts`: 56 checks ok. reachuse 53, blockeduse 45, showershare 41 ok.
  `swap-stack-audit`: no change. Build ok.
- Repro `viewlenient-check` (any save): `ViewLenientCheck run` off -> `verdict: BROKEN`, on -> `verdict: FIXED`,
  `controls ok=True`; `result` = counters and the last line; `lot` = the TVs, viewables and instruments of the
  selected Sim's lot its plan cannot reach as the game does, and what the rule finds.
- Storms: with the switches off 4510 and 4511 count the cases (Contemplate is common: expect most of 4510 there);
  with `viewlenient` on 4513 and 4518 rise together (contemplate in place), `routefail:` lines for Contemplate /
  Watch / WatchTV stay (they are written at the plan) but no balloon follows; 4523 must stay 0.

### 16.5 Storms (2026-11-22, Deck) and what is still open

Ten route storms s520-s529 (5 on, 5 off, the switches of §16-§18 together): no contemplate route failure happened
in either arm, so the storms did not exercise this rule. Errors 0, crashes 0. **Not run in the game yet**: a forced
repro (a sculpture or a view spot whose view slot cannot be reached) is pending.
