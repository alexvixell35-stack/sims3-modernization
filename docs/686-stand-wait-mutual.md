# 686: Sims waiting on each other and on blocked spots (route waits)

Contributor doc, 2026-11-21/22. On the contributor's combo branch this file has §1-§13 (obstacle stand-and-wait,
portal lanes, pets, chores, beds); this copy carries only §10, the part this change adds. The section numbers are kept
so the code comments (`docs/686 §10`) match. Built with `node tools/setup/build-fixes.ts`; nothing here needs ts3native.

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

