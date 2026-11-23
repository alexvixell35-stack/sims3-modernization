# 675. Relaxed routing and placement rules: what the logs ask for, OhRudi's precedent, three switches

2026-11-20 (UTC). Inputs: the on-arm `ts3native-full.log` of the Isla Paradiso route storms s152-s166 (`routefail job:` lines
of s157-s166, `fgl prof:` line pairs of s152-s158), ilspycmd output of the game assemblies, the footprint resources of the
"Sims need less Space" / "Pets need less Space" packages read with `tools/extract-assemblies.ts` (`readIndex`, `refpack`).
Built: ts3fixes `fglnonescan`, `fglnnearspot`, `followerlasttry` (`RouteRelax.cs`, `RouteRelaxCore.cs`), all default off.
**Nothing here has run in the game.** Branch `feat/route-relax`.

Until now every routing change was an exact speed-up. This doc is about rules that are stricter than they need to be: a
relaxed rule may give another result than EA's, as long as the result is physically valid (no Sim in an object or a wall,
no two Sims on one spot, no teleport, no skipped walk).

## 1. Prior art: what OhRudi's packages change [verified: resource read]

12 packages, 165 resources of type `0xD382BF57` (FTPT, footprint) plus one name map each. Nothing else: no tuning XML, no
ITUN, no script, no slot or route parameter.

- Every resource is the footprint of a **jig** (the invisible helper object an interaction places to reserve its space):
  social jigs (`social_routing_template_1Unit / 168Unit / 3Unit`, fight, slow dance, pillow fight), solo jigs
  (`solo_routing_template`, `simSittingJig`, `workOutJig`, `performer_jig`, dance), object helpers (`homeworkJig`,
  `computerLaptop_floor_jig`, sand castle, sparkler, rake), pack ones (meteor, skating, jetpack, lifeguard, laser harp), and
  60 pet jigs (route destination, turn in place, idle, social, play, pregnant, platform enter / exit).
- The change: **the first footprint polygon is scaled about the jig's origin**, by 0.5 (98 Sim-jig and 46 pet-jig polygons) or by 0.25 (11 pet jigs, e.g. dog route destination, dog turn in place, horse social);
  nine routing-slot polygons are also scaled by 0.5. Point count, footprint type, intersection flags and
  surface flags are unchanged (0 flag changes in 165 resources); the other 336 polygons (further footprints, routing
  slots) are byte-equal to the game's. The stored bounding box is left at the original size.

| jig | game (m) | package (m) |
|---|---|---|
| `social_routing_template_1Unit` | 0.89 x 1.97 | 0.44 x 0.98 |
| `social_routing_template_3Unit` | 0.89 x 3.67 | 0.44 x 1.84 |
| `solo_routing_template_ftp` | 0.79 x 0.88 | 0.39 x 0.44 |
| `simSittingJig` | 0.80 x 0.80 | 0.40 x 0.40 |
| `performer_jig` | 1.92 x 1.82 | 0.96 x 0.91 |
| `doorwayFootprintJig` | 0.98 x 2.48 | 0.49 x 1.24 |
| `computerLaptop_floor_jig` (part 0) | 1.00 x 1.38 | 0.50 x 0.69 |
| `deathByMeteorJig` | 5.03 x 4.79 | 2.52 x 2.39 |
| `dogSocial_jig` | 0.66 x 3.12 | 0.33 x 1.56 |
| `horseSocial_jig` | 1.13 x 5.85 | 0.28 x 1.46 |
| `dogTurnInPlace_jig` | 1.14 x 1.14 | 0.28 x 0.28 |

What that means: the rule relaxed is **how much free floor a jig must find**, nothing about where Sims stand. The
standing slots keep their offsets, so on a 3-unit social jig the Sims stand at z = +-1.5 m while the reserved polygon now
ends at +-0.92 m: the slots are outside the reserved area. Each Sim still has to route to its slot (the planner checks
the goal), which is what keeps this from putting Sims into objects; the jig no longer guarantees the space before the
walk starts, so a placement can succeed and the walk to the slot fail later. That is the class a respected modder found
safe: **placement clearance of helper objects, halved**. Not installed here: "Guitar / Bass needs less Space" (the
`GuitarJig` / `BassGuitarJig` footprints; no instrument jig is among the 165).

These packages were loaded in every storm (docs/671 §4.4), so all numbers below already include them.

## 2. Where strictness costs: the failure classes [logs]

### 2.1 Placement searches (`fgl prof:` pairs of s152-s158, searches of 50 ms or more)

| caller | n | ms | what fails | the rule |
|---|---|---|---|---|
| `Fixes.SafeSpot` < `ShortcutToLot` (ours) | 33 failed | 3788 | 15,072 octagon tests, 0 pass, whole lot | ours; fixed in 9f20444a (one failed search ends the run, remembered 30 sim minutes) |
| `BandInstrument.MoveJigToGoodSpot` < `Guitar.RouteBeforePlaying` (3 named + 7 where the stack line ends at `FindGoodLocationNearby`) | 10 failed | 2677 | the **indoors** stage: 3344 tiles tested, 196 spots pass the native test, the managed `IsInside` refuses each | "any indoor spot of the lot before an outdoor one" (§3.2) |
| the same placements' outdoors stage | 4 ok | 875 | re-tests 514 of the same tiles before the first pass (`repeat of a search 272 ms ago (other result)`) | the three lot-wide stages are separate searches (§3.1) |
| `UseLaptopHere.Run` | 5 failed | 273 | two or three scans in a row, 10,264-10,568 tests each, 0 pass; `repeat ... (same result)` | same, plus: a scan that passed nothing is run again with another check |
| `PutDownChild` (toddler on the floor) | 2 failed, 1 ok | 184 | as the laptop | same |
| `WeatherStone`, `SpiritMaker`, `Sim.AttemptToFindSafeLocation`, front door | 8 | 801 | one search each | none: a single search with no retry |

So outside our own SafeSpot, every slow search is one of `GlobalFunctions.FindGoodLocationNearby`'s lot-wide stages.
One guitar placement costs 925-948 ms in two scans. In the outdoors scan the first spot that passes is 17 tiles from the
Sim (881 tiles in the lot visited); first failing checks there: footprints 2674, objects 678, pre-checks 352. That part
is space (the jig's size and what stands around), and it is where a smaller instrument jig would help.

### 2.2 Failed route plans (`routefail job:` of s157-s166: 1368 lines, 16.2 s of job time, 22-136 a minute)

| class | n | result | the rule | relaxable? |
|---|---|---|---|---|
| lot trips (`GoHome`, `VisitCommunityLot`, `GoToLot`, `VisitLot`) | 698 | 16 no path (647), 10 (37), 18 (13) | no connection between two places of the world; the storm forces these trips, its "no-path" kind on purpose | no: connectivity, not clearance |
| `GoHere` on terrain | 324 | 18 portal locked (137), 10 no destination (111), 16 no path (76) | door and lot permission (18); the storm's point is not routable (10) | no: 18 is the door rule (kept); 10 has no goal to move |
| no interaction / unknown | 169 | mixed | routes made outside an interaction | not attributable |
| `FollowerMeetUpAtPoint` | 49 | 16 no path (38), 10 no destination (11) | goals only on two 60 degree arcs beside the leader, 1-3.5 m out, in the meeting point's room; three tries 0.45 m apart | yes (§3.3) |
| interactions on a Sim (socials, tag, pillow fight, read to sleep) | 53 | 16 / 10 / 18 | jig placed, then no path to its slot | the jig rule is OhRudi's, already loaded |
| objects and rabbit holes (toilet, plate, loveseat, hot tub, bookstore) and other lot interactions | 75 | mostly 16 | no path to a routing slot or door | no: slots are where the animation fits |

The storm's failures are mostly made by the storm: 1022 of 1368 are forced lot and terrain trips that fail for
connectivity or door permission, and they are cheap (median 2 ms of job time; the no-path memo of docs/643 is for those).
The only route class with a strict goal rule behind it is the follower meet-up.

## 3. The three switches (ts3fixes, default off, each on its own)

No counter slot: each check's `result` line prints the session counts; log lines `ts3fixes: fglnonescan: ...` and
`ts3fixes: followerlasttry: ...` (rate-limited with the hitch lines).

### 3.1 `fglnonescan`: the lot-wide stages as one scan (same rule, same spot)

[verified, ilspycmd] `FindGoodLocationNearby(obj, ids, ref position, ref forward, offset, searches, bools)`: up to 17
`TestObjectPlacement` probes at the position, a native search in the same room, one per adjoining room, then from the same
start, with the same object and constraints:

1. indoors: managed check `IsInside` (light-blocking room id > 0); only when the start itself is in such a room;
2. outdoors: `IsOutside` (room id == 0);
3. same lot: no check;
4. anywhere: no constraints at all (check `IsOutsideEvenOnWorldLot` when outdoors was asked for).

The native search asks the managed check only for a spot that already passed the placement test, and goes on when the
answer is no (docs/671 §11.3: `tests 25380 (pass 196)` with `FAILED`). So stages 1-3 run the same native tests over the
same tiles and differ in which passing spots they accept.

Switch on, when two or more of stages 1-3 would run: EA's own body runs the early stages (called with those strategy
bits only), then **one** search with one check: a spot of the first stage's class ends it; of the other classes the first
one per stage is kept; at the end the earliest stage that has a spot wins. Nothing passed: stages 1-3 are over, stage 4
runs as in EA. The kept spot is returned through a second search that starts on it and accepts only it (radius 2 m, one
tile); if that does not return it, EA's stages run from the original position (`kept spot not confirmed`, should stay 0).

Same result as EA: the stage order decides between the same spots in the same scan order. What can differ is what
differs between two EA runs: the search's random first facing on a tile. All Sims, household included: the rule is EA's.
Expected: the guitar placement 925-948 ms -> about 655-675 ms (the 271-273 ms second scan goes); laptop and put-down
lose one of two or two of three failed scans (50-58 ms each). Risk: the check's position argument is taken to be the
candidate's position [inferred from the delegate's parameter names and EA's own `IsInside`], and a search started on a
spot is taken to test that spot first [inferred; the confirm step falls back to EA when it does not hold].

### 3.2 `fglnnearspot`: the nearest valid spot instead of any indoor spot first (relaxed)

The strict rule: stage 1 takes any indoor spot of the lot, however far, before the nearest outdoor one, and has to walk
the whole lot to learn there is none. In s154 that is 654 ms to refuse 196 valid spots.

Switch on (it includes the one scan): the scan also ends at the first passing spot more than `kFglnNearSpotMargin`
(4 m) further out than the nearest valid spot seen. An indoor spot still wins inside that margin; else the nearest spot
does. Distance is the larger of |dx| and |dz| from the start, as the search walks square rings.

- Seen by the player: a Sim whose room and the rooms next to it have no space plays the guitar, uses the laptop or puts
  the toddler down at the nearest free spot of the lot, which can be just outside the door, instead of in a room at the
  other end of the house. Never: a spot that fails the game's placement test (the caller's constraints, `Routable` among
  them, apply to every spot offered), a teleport, a skipped walk.
- Household and selected Sims keep EA's rule: `Fixes.HouseholdExempt` of the Sim whose task runs the search (unknown =
  household); `[Experimental] HouseholdToo=1` lifts it, as on every realistic site.
- Expected: the guitar scan ends at ring 21 instead of the lot's edge: about 1300 of 4096 tiles, roughly 300 ms instead of
  925-948 [argued from the logged tile counts, not measured]. No gain when the nearest valid spot is far and nothing
  passes beyond it.
- Risk: weather and privacy are not part of the rule (EA's outdoors stage has none either, it only comes later); a Sim
  may end up outdoors where EA would have found a far room.

### 3.3 `followerlasttry`: the full ring on a follower's last try (relaxed)

[verified, ilspycmd `GoHereWithHoldingInteraction.Run` / `AddRouteDest`; docs/643 §7.2] A follower of a group walk gets
`NumberOfTimesToRoute` (3) tries per meeting point. Each try plans to two `RadialRangeDestination`s: 60 degree arcs
(`kFollowerArcDegs`) to the left and right of the leader's direction, 1-3.5 m out, valid rooms = the meeting point's
room. A failed try that ends more than 3.5 m away moves the point 0.45 m in a random direction: the next try has nearly
the same goals. EA itself plans the full 360 degree ring for horses, boats and groups of four or more, and counts a
follower within 3.5 m as arrived whatever its angle.

Switch on: the last try (and any later one within 0.6 m and 60 sim minutes) of an NPC follower gets one full-ring
destination instead of the two arcs. Radius, room, footprint checks near goals and Sim pushing stay EA's.

- Seen: an NPC follower may stand in front of or behind the leader instead of beside them.
- Household followers keep EA's arcs (`Fixes.HouseholdExempt`).
- Expected: unknown. 49 failed plans in ten storms (287 ms); whether the ring has a reachable goal where the arcs had
  none is not in the logs. The 11 "no destination" ones (no valid goal on the arcs) are the likely gain; a follower
  with no path to the leader's room at all keeps failing.

## 4. Candidates weighed and not built

| candidate | why not |
|---|---|
| smaller instrument jigs (OhRudi's guitar / bass package) | footprints are native resources: ts3fixes cannot change them at load, and package files are not ours to ship. The logs say it would help the search that succeeds (514 tiles to the first free spot) and not the 654 ms one (space was found, the indoor rule refused it). Installing OhRudi's package is the way |
| shrink more jigs than OhRudi did, or the Sim's own 0.25 m octagon | the octagon is what keeps a Sim out of objects and walls; OhRudi left it alone. Kept |
| Sim-to-Sim spacing at goal points, `PushSimsAtDestination`, `CheckForFootprintsNearGoals` | they keep two Sims off one spot. Kept |
| drop the room rule of the follower ring (`SetValidRooms`) | the follower could end up behind a wall from the leader. Kept; only the angle is relaxed |
| wider follower ring (`kMaxFollowerRadius` 3.5 m) | a tuning value shared with EA's own "close enough" test and the boat case; no log evidence that distance is what fails |
| door locks and lot permission (result 18, 137 `GoHere`) | a safety rule: Sims would walk through locked doors |
| accept a point near an unroutable `GoHere` goal (result 10) | the storm's own bad points; for a player command the goal is the player's |
| no-path lot trips (result 16, 698) | connectivity of the world, not strictness; docs/643's memo handles the repeats |
| skip the facing tests of a failed tile | not a rule; docs/671 §10 (`[FglFacing]`) does it exactly |
| drop `PreferEmptyTiles` / `Routable` from the search | a spot that is not routable is a Sim that cannot leave: an invalid placement |

## 5. Checks run (no game)

- `tools/selftest/routerelax` (C#, `RouteRelaxCore.cs`): the one scan returns EA's spot for every sequence of up to six
  spots of the three classes under every stage set with two or more stages (4372 shapes); with a margin the spot is never
  further out than EA's and within the margin of the nearest; the s154 jig shape; the follower try count. Pass.
- `TS3_CSC=... node tools/setup/build-fixes.ts --tag relax` (Roslyn, `refs-resolve-check` 0 unresolved), `node tools/selftest-all.ts --ci`.
- Repros: `fglnonescan-check`, `fglnnearspot-check`, `followerlasttry-check` (pure, off = BROKEN / on = FIXED),
  `ip-routerelax-storm`.

## 6. Not proven

- In the game: one storm only. s169 (Isla Paradiso, all three on): `fglnonescan` fired 3 times; `fglnnearspot` and
  `followerlasttry` wrote no log line. No off/on verdict for any of the three. After s169 the owner chose not to take
  per-rule relaxations for now, so on the contributor's branch the three were PARKED (off).
- In this repo `GlobalFunctions.FindGoodLocationNearby` has one swap, `FglInBound.cs`'s `FibNearbyHook`; it asks
  `RrNearby` (this doc's one scan / near spot) first, then `fglinbound`, then EA's body. The `AddRouteDest` swap is
  `RouteRelax.cs`'s own. The `-check` repros' members line checks both.
- That the managed check's arguments are the candidate position, and that a search started on a kept spot returns it
  (§3.1): the `fglnonescan:` lines and `kept spot not confirmed` show it in the first storm.
- `followerlasttry`'s effect on failures (§3.3).
- The 7 jig-shaped searches whose stack line ends at `FindGoodLocationNearby` are counted as guitar placements by their
  shape (same lot, start and tile counts as the named ones) [inferred].

## 7. Next storm (2 min each, one switch per arm)

Ini as s158's on arm plus `[FglProf] Enabled=1 LogMs=50 Frames=16`. Arms: none, `fglnonescan`, `fglnnearspot`,
`followerlasttry` (agent `patches`, read at every search / follower plan).

- `fglnonescan`: `ts3fixes: fglnonescan: N ms, <object>, stages 7 ... kept the outdoor spot D m out, no spot of an earlier
  stage on the lot`; in `fgl prof:` no `repeat of a search ... (other result)` pair for a jig; `FglnOneScanCheck result`:
  `kept spot not confirmed 0, errors 0`.
- `fglnnearspot`: the same lines ending `scan ended by the 4 m margin`, their ms well under the first arm's for the same
  object; `ended by the margin` > 0; look at the placed jig once (free ground, no Sim in an object).
- `followerlasttry`: `ts3fixes: followerlasttry: <Sim> try 3 of 3 at x,z: the full ring ...`; fewer `routefail job:` lines
  with `FollowerMeetUpAtPoint` than the baseline arm; `FollowerLastTryCheck result`: `tries given the full ring` > 0.
- every arm: no crash, no stuck Sim, `fails/min` in the table not higher.
