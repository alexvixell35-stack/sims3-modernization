# 682. The last FindGoodLocation frame: the lot-wide "indoors" stage, and `fglinbound`

2026-11-20 (UTC), `feat/fgl-root-1120`. Inputs: the storm logs s189-type (phase-20261120-1607 / 1613 / 1622 / 1630, `[FglProf]`
with `Frames=16`), s202 / s203 (phase-20261120-1717 / 1722, no profile), the 12 profiled phases of the day; ilspycmd output
of `GlobalFunctions`, `BandInstrument`, `World`; objdump of the Steam 1.67 code. Built: ts3fixes `fglinbound`,
`fglinboundhh` (default off), shadow tuning `kFglInBoundShadow`. Design 1 (§3, one radius around the start; s207 / s208) and design 2 (§8, one search per cluster of indoor tiles; s222) gave **no gain** on lots whose rooms spread over the lot; their code is removed.
**Design 3 (§9: ts3native `[FglInside]` + ts3fixes `fglinbound`, the tile's room is read before the placement test) is what the code does now; shadow (s227) and serve (s228) ran, §9.10.** §3-§8 are kept as the record; they name the counter slots of the contributor's branch (4250-4259), which this repo never had. In this repo the slots are 1496-1505 and ts3fixes has its own `FindGoodLocationNearby` swap (`FibNearbyHook`; on the contributor's branch the branch sat in `RouteRelax.cs`'s `RrNearbyHook`, docs/675, which is not in this repo).

## 1. Who issues the slow searches [measured: `fgl prof:` line pairs]

All searches of 50 ms or more with a named caller, 12 profiled phases (101 searches):

| caller (managed) | n | ms | failed | slowest | object |
|---|---|---|---|---|---|
| `Fixes.SafeSpotBody` < `ShortcutToLot` < `VisitCommunityLotHook` (ours) | 48 | 4809 | 46 | 197 | Sim (octagon) |
| `BandInstrument.MoveJigToGoodSpot` < `Guitar.RouteBeforePlaying` < `PlayBandInstrument` / `JamWithInstrument` (through `FindGoodLocationNearby`; 8 more lines end at `FindGoodLocationNearby` with the same shape) | 24 | 5178 | 15 | 675 | instrument jig |
| `WeatherStone.TrySpawnWeatherStone` (world start) | 7 | 846 | 3 | 216 | object |
| `SpiritMaker.MakeSpirit`, front door, `UseLaptopHere`, `PutDownChild`, `Sim.AttemptToFindSafeLocation`, SafeSpot from `GoHomeHook`, social jig | 22 | 1403 | 13 | 128 | mixed |

With `[FglFacing]` and `[FglMemo]` on (phases 1230-1630) SafeSpot's failed search is 50-57 ms (13 lines, one 101), once per
lot, Sim kind and 30 sim minutes (9f20444a). It no longer makes a frame of 300 ms.

**Every frame of 300 ms or more with stall leaf `World_FindGoodLocation` in those runs is one pair** [measured, 5 of 5]:

| phase | sim time | frame | search 1 (FAILED) | search 2 (ok), same object and start |
|---|---|---|---|---|
| 1607 | Sun 6:27 PM | 453 ms | 268 ms, tiles visited 11728, tested 3342, tests 15043, **pass 196** | 75 ms, tested 490 |
| 1607 | Sun 6:32 PM | 457 ms | 267 ms, 12154 / 3336 / 15176, pass 193 | 76 ms, tested 509 |
| 1613 | Sun 2:58 PM | 373 ms | 228 ms, 12154 / 3342 / 14978, pass 196 | 67 ms, tested 512 |
| 1622 | Sun 9:46 PM | 446 ms | 265 ms, 12154 / 3344 / 14985, pass 196 | 82 ms, tested 514 |
| 1630 | Sun 6:31 PM | 384 ms | 231 ms, 12154 / 3344 / 15003, pass 196 | 67 ms, tested 514 |

Caller of all ten: `GlobalFunctions.FindGoodLocationNearby` (three overloads) < `BandInstrument.MoveJigToGoodSpot` <
`Guitar.RouteBeforePlaying` < `PlayBandInstrument.FindPlaceToPlay` < `DoPlayInstrument` (`RunFromInventory` or
`JamWithInstrument`). Lot 64 x 64, start 31-33 / 53-55, `bools 00000014`, no room, no radius.

s202 (347 ms, Sun 3:07 PM) and s203 (382 ms, Sun 7:08 PM) ran without `[FglProf]`; their script stack line holds the leaf
only. They are taken to be the same pair **[inferred]**: one frame a run at the same time of day as above, 350-380 ms
(the profiled pairs minus the profile's cost on 12,000 tile stubs), native stacks in the footprint gather (`0xB41120`),
the object check (`0xB45B90` at `+0x745BFA`) and the physics box query, which is the profiled pair's time split, and no
other caller in the profiled runs comes near 300 ms with the two switches on. The next storm names the caller without
the profile: §5 log line.

## 2. Why the first search fails [verified: decompile + disassembly]

`FindGoodLocationNearby(obj, ids, ref position, ref forward, offset, searches, bools)` after its probes at the position,
the same-room search and the adjoining-room searches runs, when the start's light-blocking room id is above 0, the
**indoors** stage: one native search of the whole lot with `PlacementCheckFunc = IsInside` (the spot's light-blocking
room id > 0). The native search asks that check only for a spot that already passed the placement test, and goes on when
the answer is no. So the stage places the jig on every tile of the lot (3344 tiles, 15,000 tests, footprint gather and
object checks each), finds 196 free spots, has every one refused as outdoors, and fails. The outdoors stage then starts
again from the same point and takes its first spot (514 tiles). The Sim stands in a small room with no space for the jig
and the lot has no other free indoor spot: the answer "no indoor spot" needs no placement test outside the rooms.

The native side:

- the record's `+0x54 / +0x58 / +0x5C` are `MaximumSearchRadius` and the start's x and z: the script IC `0x78AE70`
  (and `0x78B0A0`, the custom-depth one) builds that triple on its stack and `0xB0E130` copies it.
- the search `0xB470C0` converts the centre to lot tile space once (`0xAB8540`) and, per tile inside the lot
  (`0xB475C7`): radius above `[0xF9896C]` = 1,000,000 -> no test; else the tile is kept iff
  `(tx - sx)^2 + (tz - sz)^2 <= r^2` with the tile's **integer** coordinates. A tile that fails goes to the next
  spiral step like a tile outside the lot does, after the outside-the-lot run counter was reset: the walk and its end
  are the same with any radius. Placement tests run only on tiles that pass.
- the tile's level is the start's (`[esp+0x1A]`), plus one in a single branch (`0xB47648`).
- `World.GetRoomId(lotId, LotLocation, def)` is `0x785210`: the lot's room grid `+0x298`, then `0xB499D0` with
  `(float)mX, (float)mZ, level` and the definition; `mRoom` is not read. `0xB499D0` -> `0xC05430` checks x, z and level
  against the grid's size and answers -1 outside it. The search's own room tests call the same `0xB499D0` with the
  tile's centre. A grid cell holds up to four room ids (tiles cut by diagonal walls), so on such a tile a corner sample
  and a centre sample may name different rooms.

## 3. Design 1 (superseded by §8): one radius around the start

In `RrNearbyHook` (the `FindGoodLocationNearby` swap of docs/675), when the indoors stage would run and the task's Sim
is not household scope:

1. EA's own body runs the early stages (position probes, same room, adjoining rooms), as `fglnonescan` does.
2. **Probe**: EA's indoors search with `MaximumSearchRadius = kFglInBoundProbeRadius` (7). A spot on square ring k
   around the start's tile is taken when `(k + 1) * sqrt 2 <= radius` (ring 3 for 7): no tile of that ring or an inner
   one can fail the radius test, so the unlimited search returns the same spot. This keeps the common case (a free
   spot a few tiles away) at EA's cost.
3. Else the **tile pass**: for the start's level and the one above, `World.GetRoomId(lot, tile, LightBlocking)` for
   every tile of the room grid (its size is read from where the lookup starts answering -1); a tile is marked when
   the id is above 0, or when it is 0 and `World.HasDiagonalWall` is true. Radius = distance from the start tile's
   centre to the farthest marked tile's centre + 2.5 (`FglInBoundCore.kMargin`: the native test uses integer tile
   coordinates against the float start, at most 1.42 off; the rest is slack).
4. EA's indoors search again with that radius. Found = the call's answer. Not found = EA's body runs the later
   stages (outdoors, same lot, anywhere) from the original position.

Why the answer is EA's: `IsInside` accepts a spot only on a tile whose room id is above 0; every such tile is marked
(or is a diagonal tile, also marked), every marked tile passes the radius test, and the search walks the same rings and
ends the same way. The tiles left out can only be refused. What differs between two EA runs differs here too: the
random first facing on a tile. The radius is not used when it covers the whole lot (counter 4253) or when the start's
tile is not marked in our pass (4257): EA's stage runs then.

**[inferred]**, checked by the shadow mode, not by reading: that the spiral is ring by ring (the probe rule only), that
`LotLocation.mX / mZ` from `World.GetLotLocation` are the grid's tile indices (the start tile must come out marked or
the pass is dropped), that `HasDiagonalWall` is true for every tile holding more than one room.

- `fglinbound` (`kFglInBound`, false): NPC task Sims. `fglinboundhh` (`kFglInBoundHousehold`, false): household,
  selected and unknown Sims too (needs `fglinbound`); `[Experimental] HouseholdToo=1` lifts the exemption as on every
  realistic site.
- `kFglInBoundShadow` (false): after our stage EA's stage runs too (`NearbyOrig` with the Indoors bit alone), **EA's
  answer is returned**, and found / position are compared: `ts3fixes: fglinbound: shadow same|DIFFERENT, ours ...; EA N ms ...`.
- `kFglInBoundProbeRadius` (7; below 1 = no probe).
- When `fglnonescan` or `fglnnearspot` is on, those take the call (parked switches; not combined).
- Log: `ts3fixes: fglinbound: N ms (probe a, tile pass b, search c), <object type>, lot W x H, M tiles may be indoors
  (L lookups), radius R m = T tiles, found | no indoor spot` for a stage of 20 ms or more. Switch off: a
  `FindGoodLocationNearby` call of 100 ms or more logs `ts3fixes: fglinbound: EA's FindGoodLocationNearby N ms (switch
  off), <object type>, ...` (the caller evidence s202 / s203 lack).

Counters (ts3fixes slots 1508-1517):

| slot | |
|---|---|
| 1508 | indoors stages taken by our path (the early stages had failed) |
| 1509 | answered by the probe |
| 1510 | bounded searches run |
| 1511 | left to EA: the radius covers the lot |
| 1512 | calls left to EA: household or unknown Sim |
| 1513 | shadow compares |
| 1514 | shadow different (**must stay 0**) |
| 1515 | errors, or start tile not marked (EA's stage) |
| 1516 | ms of our path (probe + tile pass + search) |
| 1517 | ms of EA's stage in shadow |

Files: `mods/ts3fixes/src/FglInBound.cs`, `FglInBoundCore.cs`, the branch in `RouteRelax.cs` (`RrNearbyHook`),
`Instantiator.cs`, `Fixes.cs`, `CheckLiveMap.cs`, `tools/setup/build-fixes.ts`; `tools/selftest/fglinbound`;
repro `fglinbound-check`.

## 4. Design 1: what it should have cost [argued; s208 showed otherwise, §8.1]

The failed stage: 3344 tiles tested -> the tiles within the radius of a small room (a 3 x 3 room: radius 3.9 m, about
50 tiles). New cost: the probe (about 150 tiles' tests), the tile pass (4096 tiles x 1-2 lookups x 1-2 levels through
the interpreter; **not measured**, the reason for the `tile pass` ms in the log line and the `live:` part of the check),
the short search. The outdoors stage stays (67-82 ms profiled; it gets less from `[FglMemo]` now, because the indoors
scan no longer pre-tests its tiles). Expected frame: about 100-150 ms instead of 350-460.

No gain: a lot whose rooms reach across it (the radius covers the lot), a start outdoors (no indoors stage), SafeSpot's
search (not this path).

## 5. Checks run (no game)

- `tools/selftest/fglinbound`: 20,000 random lots (rooms, diagonal and upper-level tiles marked without knowing, start
  anywhere in its tile, a third with no free indoor spot): the bounded model search returns the unlimited search's tile
  in all; a radius without the margin differs in 164 of them (the cases reach the boundary); the probe rule: 10,578
  spots taken, all EA's. The model is the native radius rule and ring order as disassembled (§2).
- ts3fixes build (`build-fixes.ts --tag fgl`): compiles.
- `node tools/selftest-all.ts --ci`: 200 PASS, 0 FAIL, 103 SKIP (the csc selftests are local-only there; `tools/selftest/fglinbound/run.ts` was run by hand).
- In the game: `FglInBoundCheck run` (repro `fglinbound-check`): the logged shape through the model, the controls, and
  the tile pass on the selected Sim's lot (lot size, lookups, ms, radius).

## 6. Design 1 run plan (done: s207 shadow, s208 serve; the plan for design 2 is §8.6)

Keep `[FglFacing] Mode=1`, `[FglMemo] Mode=1`. Add `[FglProf] Enabled=1 LogMs=50 Frames=16` to both arms of the first
pair only (it names the searches; it costs about 70 ms on a 12,000-tile search).

- **Arm A, shadow**: `fglinbound=on`, tuning `kFglInBoundShadow=True` (`fglinboundhh` off). Costs EA's time plus ours.
  Pass: `ts3fixes: fglinbound: shadow same` lines for the guitar frames, none `DIFFERENT`; slot 4256 = 0, 4255 > 0,
  4257 = 0; the `tile pass` ms in those lines (if it is above ~40 ms, a per-lot memo of the pass is the next step).
- **Arm B, serve**: `fglinbound=on`, `kFglInBoundShadow=False`. Pass: no frame of 300 ms or more with stall leaf
  `World_FindGoodLocation`; `fgl prof:` shows no FAILED search of ~3340 tiles from `FindGoodLocationNearby`, instead
  one with a small `radius`; slot 4252 > 0; the Sims still play (no new `routefail` class, no stuck Sim).
- **Off arm** (any run without the switch): the line `fglinbound: EA's FindGoodLocationNearby N ms (switch off),
  GuitarJig ...` next to the FindGoodLocation frame confirms §1's inference for s202 / s203.
- Later, after A is clean in two runs: `fglinboundhh=on` in shadow with a household Sim playing the guitar indoors.

## 7. Not done

- A memo of the tile pass per lot (needs an exact invalidation on wall, floor and roof edits; not built).
- The outdoors stage's own search (real work: 514 tiles to the first free spot; `fglnonescan` merges nothing here
  once the indoors stage is short).
- SafeSpot's failed whole-lot search (50-57 ms, once per lot and 30 sim minutes) and the world-start WeatherStone
  searches: below the hitch line, left alone.
- Moving the search off the sim thread: the placement checks read objects, the physics world and room data that only
  the sim thread may touch while the game runs (docs/671 §4.3); nothing here needs it.

## 8. Design 2: one search per cluster of indoor tiles

### 8.1 s207 / s208 [measured]

s208 (serve, `[FglProf]` on), twice: `fglinbound: 251 ms (probe 17, tile pass 7, search 226), Jig, lot 64 x 64, 283 tiles may
be indoors (15715 lookups), radius 50.9 m = 3631 tiles, no indoor spot`; the search line: `radius 51`, tested 2848 tiles,
pass 196, FAILED. The lot's indoor tiles are spread over it, so one radius around the start covers the lot. The tile pass
is cheap (7 ms for 15,715 lookups); the probe costs 17 ms when it finds nothing. s207 (shadow) never met the guitar case.
The caller of §1 is confirmed for the unprofiled frames by this line (object `Jig`, the 64 x 64 lot).

So: 283 tiles can hold an indoor spot, about 2,850 are placement-tested.

### 8.2 The design

After the tile pass (§3 step 3, unchanged):

1. The marked tiles' 8-connected groups (`FglInBoundCore.Clusters`). More than `kFglInBoundMaxSearches` (8): the two
   groups with the nearest centres are merged until they fit. More than 96 groups, or the searches' discs holding 60%
   of the lot or more: EA's stage (slot 4253).
2. Per cluster, nearest first (smallest square ring of a member around the Sim's start tile): one native search with
   EA's object, ignore list and constraints, **started on the cluster's centre tile** on the start's level
   (`World.GetWorldPosition` of the tile; the position must read back as that tile or a neighbour on the same level,
   else EA's stage runs) with `MaximumSearchRadius` = distance from the read-back tile to the farthest member + 1.5
   (a start anywhere in its tile is at most sqrt 2 further from a tile's integer coordinates).
3. The search's check accepts nothing. For each spot that passed the placement test it does `IsInside`'s own test and
   keeps the indoor spot on the nearest ring around the Sim's start tile, then the nearest tile. So one search tests
   every tile of its disc once and offers every valid indoor spot in it.
4. A cluster whose nearest member is not on a nearer ring than the kept spot is left out, and so are all later ones.
5. The kept spot is returned through a last search that starts on it, radius 2, accepting only it (and still
   indoors): position and facing come from the native search. Not returned again: EA's stage runs (counted with 4257).

The probe (§3 step 2) is off by default now (`kFglInBoundProbeRadius` 0).

### 8.3 What is the same as EA's stage and what can differ

- **Found or not: the same.** Every tile `IsInside` can accept is a member of a cluster and inside its search's
  radius; the placement test of a tile does not depend on where the search started [inferred: the record's start is
  read by the walk and the radius test; the shadow compare checks it]; the level is the start's in every search.
- **The spot: on the same ring as EA's.** EA's search walks square rings around the start and takes the first valid
  indoor spot; ours takes a valid indoor spot on the nearest ring that has one. On that ring it can be another tile
  (EA: first in the walk's order; ours: nearest by straight distance). The selftest's model: EA's tile in 9,765 lots,
  another tile of the same ring in 2,426.
- The facing comes from a search started on the spot; EA's own facing on a tile is random among the valid ones.

Household: `fglinboundhh` stays its own switch, because the tile on the ring can differ (a valid spot at the same
distance, not the identical one).

Shadow (`kFglInBoundShadow`): `shadow same` (same tile), `shadow same ring, other tile` (counted in the check's
result text, expected), `shadow DIFFERENT` (found against not found, or another ring: slot 4256, **must stay 0**).

### 8.4 Cost [argued; the walk's cost is not measured]

Model of the s208 shape (a 3 x 3 room and four other buildings, 239 indoor tiles): 556 tiles tested in 5 searches
against 4096. For the real lot (283 tiles): a few hundred tiles instead of 2,850, about 142 ms x 600 / 2850 = 30 ms of
tests at s208's rate. Each search also walks the whole lot's rings (about 12,000 steps); with `[FglProf]` on that
walk costs about 80 ms **per search** (its tile stubs), so **a serve arm must run without `[FglProf]`**; without it the
walk's cost is not known (the unprofiled frames suggest a few ms). The log line gives `searches N ms` and the search
count; if the walks dominate, lower `kFglInBoundMaxSearches`.

### 8.5 Checks run (no game)

- `tools/selftest/fglinbound`: 20,000 random lots through `Clusters` with 1-8 searches and a search start anywhere in
  its centre tile: found iff EA's model search finds, always on EA's ring, always a valid indoor tile; members =
  marked tiles; a radius 0.5 short of the farthest member differs in 1,343 lots (the cases reach the boundary); the
  s208 shape (556 of 4096 tiles, 5 searches); merging to 2 clusters. Design 1's cases still pass.
- ts3fixes build; `node tools/selftest-all.ts --ci`: 200 PASS, 0 FAIL.

### 8.6 Run plan

1. **Forced compare, any save with a house** (repro `fglinbound-check`, `FglInBoundCheck live`): the selected Sim's
   lot must have rooms. The check creates a `GuitarJig` (the logged object) on up to three indoor tiles of that lot
   (the Sim's own tile first when the Sim is indoors), runs EA's indoors stage and ours from each, compares, destroys
   the jig. No Sim has to own or play a guitar and nothing is placed; this is the same native search with the same
   object as `Guitar.RouteBeforePlaying` makes. Pass: `live verdict: ok`, each tile `-> same` or `-> same ring, other
   tile`, the two ms side by side; slot 4256 = 0. A lot with a full house gives `found`, the storm lot gives `no
   indoor spot`: run it on both kinds if possible (in the Isla Paradiso storm save: select a Sim standing on the
   64 x 64 lot of §1, or `sim_visit_lot` there first).
2. **Arm A, shadow storm** (s202 ini, same seed, `[FglProf]` may stay on): `fglinbound=on`, `kFglInBoundShadow=True`.
   Pass: every `fglinbound: shadow` line `same` or `same ring, other tile`; slot 4256 = 0, 4257 = 0. The guitar case
   is not certain to occur in a storm (s207 missed it); step 1 covers that.
3. **Arm B, serve storm, `[FglProf] Enabled=0`**: `fglinbound=on`, shadow off. Pass: no frame of 300 ms or more with
   stall leaf `World_FindGoodLocation`; a line `fglinbound: N ms (probe 0, tile pass ~7, searches S), Jig, ... in K
   clusters, n searches over T tiles ..., no indoor spot` with N well below 226; slot 4252 > 0; the Sims still play.
4. After two clean runs of 1 and 2: `fglinboundhh=on` with a household Sim playing the guitar indoors.

### 8.7 Weighed and not built

- A memo "this lot has no indoor spot for this object" (idea b): the answer depends on every object and Sim on the
  indoor tiles; Sims move between two placements, and no sequence number covers objects, physics and rooms together
  (docs/671 §4.3). With the stage at tens of ms a memo has little left to save.
- Skipping the stage when no marked tile could take the jig (idea c): that is the placement test itself; the cluster
  searches are the cheapest exact way to ask it.

## 9. Design 3: the room test before the placement test (ts3native `[FglInside]` + ts3fixes `fglinbound`)

### 9.1 s222 [measured] and why there is no indoor spot

`FglInBoundCheck live:big` in s222: `lot 64 x 64, 1123 indoor tiles; tile 42,7: EA 187 ms no indoor spot, ours 8 ms [no gain,
1177 tiles may be indoors in 8 clusters, 4460 tiles to test] -> left to EA` (the same from tiles 35,24 and 56,56, 185 ms
each). A lot with large buildings: the clusters' discs cover it.

Why EA finds no indoor spot for the guitar jig among 1,123 indoor tiles:

- `IsInside` is exactly a room test [verified, ilspycmd]: `World.GetLotLocation(position)` then
  `World.GetRoomId(lot, location, LightBlocking) > 0`; the angle is not read. It is not the cause: it is never asked
  for a spot that failed the placement test.
- The search hands it the world position of the **tile's centre on the tile's level** [verified, disassembly
  `0xB47E0B`-`0xB47F3A`: `0xADD630(lot, {x + 0.5, z + 0.5, level})` then `0xAB8740` (lot to world), four values x, y,
  z, angle to the script delegate]. The three start tiles of the check were themselves read as indoors on their level,
  so the level is not off for the lot as a whole.
- So **every indoor tile fails the native placement test in all facings**; the 196 spots that pass are outdoors
  [measured: `pass 196`, each refused, s189 / s208]. Which check rejects the indoor tiles is **not in the logs**: the
  census of the failed search is over all tiles (s208: 9,843 of 11,780 failed tests at the part pre-checks of
  `0xB463F0`, i.e. lot bounds, covered tile quadrants, `0xB40DA0`, surface kind; footprints 809, objects 609, `0xB3ECE0`
  392, box 127). [inferred] the jig's footprint (player, case and listeners' area) does not fit the rooms of these lots
  between walls and furniture. With design 3 serving, the search tests only tiles with a room, so the same `fgl prof:`
  census and the `fgl inside:` line (tests run / passed on such tiles) answer it in the next run.

For the design: nothing can be skipped for a tile with a room (EA has to test it); everything can be skipped for a
tile without one.

### 9.2 The waste is the order [verified]

Per tile the search runs up to 8 facing tests (`0xB47DDF` -> `0xB47040`); only when one passes does it call the managed
check (`0xB47DFC`: `rec+0x28` not 0), and a refusal goes on to the next facing or tile. The check's answer depends on
the tile alone. The own-facing test `0xB4821B` is on the other path (no facing wanted); it is not touched.

### 9.3 What `[FglInside]` does (`fglprof.cpp`, `fglprof-core.h`; default off)

- ts3fixes op 48 (`userdiag.cpp`), value 1: "the next FindGoodLocation search of this thread is the indoors stage and
  its check is IsInside"; value 0: "over". The answer is 1 when `[FglInside]` is installed.
- At the search's start (`FglSearchBegin`, the existing hook on `0xB0E3C6`): armed for this thread, the record has a
  managed check (`+0x28`) and the lot (`+4`) has a room grid (`+0x298`, the pointer the search itself loads at
  `0xB470EB`) -> the search is marked; the arm is used up (one search per arm).
- At each facing test of a marked search (the existing stub on `0xB47DDF`): the test record's `+8 / +0xC / +0x10` are
  the tile's centre in lot tile space and its level. Once per tile: the room id for `(floor x, floor z)` on the level,
  the one below and the one above, each by `0xB499D0(grid, {(float)tx, (float)tz, level}, 0, 1)`, which is the call
  the script IC `World_GetRoomIdImpl 0x785210` makes for `World.GetRoomId(lot, LotLocation, LightBlocking)` (same
  function, same integer-coordinate sample, bounds-checked, -1 off the grid). All three not above 0: the facing test
  is answered 0 without the call (the same `FglRet0` path `[FglFacing]` uses). Else the game's test runs as always
  and the game asks the check itself.
- A nested search, a search another thread owns, a record without a check: nothing is answered.

### 9.4 Exactness

A spot ends the search only when (a) a facing passes the placement test and (b) the check accepts it. For an answered
tile (b) is false for every facing: the check maps the tile centre's world position back to a tile and a level and
reads the room id there with the lookup above. The tile it maps to is the same one (the centre is half a tile from every
edge, inside the lot). The level is derived from the height; that it equals the search's level is **not** assumed: a
tile is answered only when the search's level and both neighbours have no room there. So no tile is skipped that the
check could accept, the tiles are walked in the same order, the tests that do run are the game's own, and the random
first facing is drawn as before. Same accepted tile, EA's answer, for every Sim (`fglinboundhh` is still its own
switch, as asked).

Not answered although they could be: tiles with a room only on the level above or below (ground under an upper floor,
ground over a basement).

Shadows:

- `[FglInside] Shadow=1`: nothing is answered. The search returns right after the accepting spot's test, so a found
  search whose last facing test ran on a "no room" tile and passed means the check accepted a tile we would have
  skipped: `fgl inside: SHADOW MISMATCH` (counted; must stay 0).
- ts3fixes `kFglInBoundShadow`: after our armed search EA's stage runs unarmed, EA's answer is returned, found and
  position are compared (`fglinbound: shadow same|DIFFERENT`, slot 1502).

### 9.5 ts3fixes `fglinbound` (managed half), switches, counters

In `FibNearbyHook` (ts3fixes' own swap of `GlobalFunctions.FindGoodLocationNearby`, `FglInBound.cs`), when the stage would run (Indoors asked for, start on a lot in a light-blocking room) and the task's
Sim is not household scope (or `fglinboundhh`): EA's body runs the early stages; `FibArmed` sends op 48 / 1, makes
EA's stage call word for word (`FindGoodLocationParams(position, bools)`, StayInRoom cleared, room -1, check
`GlobalFunctions.IsInside`), sends op 48 / 0 in a `finally`; found = the call's answer, else EA's body runs the later
stages. Op 48 not answered (no ts3native, `[FglInside]` or `[UserDiag]` off): EA's body runs the stage (slot 1497). With the
switch off the swap calls EA's body unchanged and logs a call of 100 ms or more (`fglinbound: EA's FindGoodLocationNearby ...`).

- `fglinbound` (`kFglInBound`), `fglinboundhh` (`kFglInBoundHousehold`), `kFglInBoundShadow`; the tunings
  `kFglInBoundProbeRadius` and `kFglInBoundMaxSearches` are gone, `FglInBoundCore.cs` and `tools/selftest/fglinbound` too.
- ts3native ini: `[FglInside] Enabled=0`, `Mode=1`, `Shadow=0`.
- ts3fixes slots: 1508 stages by our path, 1497 ts3native not answering, 1498 found, 1499 no indoor spot, 1500
  household or unknown Sim, 1501 shadow compares, 1502 shadow different (**0**), 1503 errors, 1504 our ms, 1505 EA's
  stage ms in shadow.
- ts3native lines: `OK   fgl inside: Mode=.. Shadow=..`; per armed search (200 at most) `fgl inside: search found a
  spot|found nothing, start x z: tiles that reached a facing test: N with no room ... (A facing tests answered 0, R run
  of which P passed), K that may be indoors (T facing tests run, Q passed and went to the managed check)`; one a
  minute `fgl inside: mode .. arms .. stage searches .. shadow mismatch M`. No exported counter block.
- With `[FglProf]` on, `tested` in the `fgl prof: search` line still counts the answered tiles (they reach the facing
  stub); `tests` counts only the tests that ran.

### 9.6 Expected gain [argued]

The storm's guitar lot (s208): 283 of about 2,850-3,340 tested tiles have a room -> about a tenth of the tests. The big
lot of s222: 1,123-1,177 of about 3,300 -> about a third (185 ms -> roughly 60-70 ms if a tile with a room costs what
the average tile costs; not known, §9.1). The ring walk is unchanged (one search, as EA).

### 9.7 Checks run (no game)

- Linux host test `fglprof-host-test.cpp` (now with `TestFglInsideCore`): 12,000 model searches on random three-level
  lots, the check reading the search's level or one either side: the same accepted tile with and without the answers,
  fewer tests, the shadow rule silent; a check two levels off is caught by the shadow rule in over 1,000 lots (the
  cases reach it); one lookup of three levels per tile, not per facing. Pass.
- clang-cl `/Zs /W4` with the xwin headers: `ts3native.cpp` (Steam and `/DTS3A_TABLE`) and `selftest.cpp`: no error,
  no diagnostic in the changed files.
- `TestFglFacing` (selftest.cpp, runs in the Windows CI) has a new block: an armed search through the real facing
  stub against a fake room lookup (answered without the test, a room one level up runs, unarmed runs, Shadow counts a
  mismatch, a record without a check is not the stage, arm / end). **Not run here** (no Wine on this machine).
- Disassembly read for the call: `0xB499D0` (`ret 0xC`, `ecx` = grid, map by definition at `+0x44`), `0xAF9770`
  (floats to tile bytes, quadrant code from the fraction), `0xC05430` (bounds), `0x785210` (the IC's arguments),
  `0xB470EB` (grid from the lot), the facing loop and the check's call (§9.1, §9.2).
- ts3fixes build; `node tools/selftest-all.ts --ci`.
- `ts3addr-ea169.h` and `ts3addr-retail167.h`: row `0x00B499D0` (unresolved in both tables, as the other FindGoodLocation
  sites; `[FglProf]`'s call-site check refuses there, so `[FglInside]` never installs on those builds).
- Also in this PR: the `fglprof.cpp` fix from the contributor's branch that came after #613 (a frame left by an unwind is
  dropped at the right exit; a nested search marks its parent "spoiled" so `[FglFacing]` / `[FglMemo]` / `[FglInside]`
  answer nothing for it), with its selftest cases in `fglprof-stub-test.h`.

### 9.8 Run plan

Needs a CI build of ts3native from this branch. ini for both arms: s202's, plus `[FglInside] Enabled=1`; keep
`[FglFacing]` / `[FglMemo]` as in s222; `[FglProf] Enabled=1 LogMs=50 Frames=16` is fine in both arms now (one search,
as EA) and gives the census of §9.1. ts3fixes: `fglinbound=on`.

1. **Arm A, shadow**: `[FglInside] Shadow=1`, `kFglInBoundShadow=True`. Steps: `FglInBoundCheck run` (expect `arm True,
   end True`), `FglInBoundCheck live:big`, `FglInBoundCheck live` with a Sim selected in a house, then the storm.
   Pass: `live verdict: ok`, every tile `-> same`; no `fgl inside: SHADOW MISMATCH`, minute line `shadow mismatch 0`;
   slot 1502 = 0, 1497 = 0. Read from the `fgl inside: search` lines: tiles with no room against tiles that may be
   indoors (the gain to expect), and `passed` on indoor tiles (0 = §9.1's reading).
2. **Arm B, serve**: `[FglInside] Shadow=0`, `kFglInBoundShadow=False`, same steps. Pass: in `live:big` ours ms well
   below EA's 185 ms with `-> same`; in the storm no frame of 300 ms or more with stall leaf `World_FindGoodLocation`;
   `fgl inside:` lines with `facing tests answered 0` in the thousands; the Sims still play.
3. After two clean runs of A: `fglinboundhh=on` (shadow first).

### 9.9 Risks

- The native half ran in the game only on the contributor's rig (§9.10); this PR's CI is its first MSVC build here.
- One new call into the game from our stub: `0xB499D0` (read-only lookup, bounds-checked, no lock seen in it or in
  `0xC05430` / `0xAA2160`; the script IC calls it on the same thread). Three calls per tile of the armed search only.
- If the facing-test record's `+8 / +0xC / +0x10` were not the tile centre and level (they are what `[FglMemo]` keys on
  and what the search copies from its tile at `0xB47DA0`), Shadow would show mismatches or `live` a DIFFERENT.
- On lots that are mostly rooms the gain is small (§9.6).

### 9.10 Results [measured: Isla Paradiso, 2-minute route storms, every proven fix on; Steam Deck, Linux Wine + DXVK]

The slow case: the guitar jig's indoors stage of `FindGoodLocationNearby` placement-tested every tile of a 64 x 64 lot
before refusing all the outdoor spots it found (228-268 ms, the one `World_FindGoodLocation` frame of 300 ms or more per
storm in the earlier runs).

| run | arm | result |
|---|---|---|
| s227 | shadow (`[FglInside] Shadow=1`, `kFglInBoundShadow`) | `[FglInside]` shadow mismatch **0**; forced compares `FglInBoundCheck live:big` 3 of 3 `same`; a small house (`live`) 3 of 3 the same tile |
| s228 | serve | `live:big`: EA 193-197 ms -> ours **40-41 ms**, the same answer all three times; the storm skipped 58,080 placement tests on tiles without a room; no `World_FindGoodLocation` hitch frame |

- One run per arm [measured]. The answer is the game's by construction (§9.4) and in every compare made; the gain on
  lots that are mostly rooms is smaller (§9.6) [inferred].
- `fglinboundhh` (household Sims) stays a separate switch, off; it has not run.
