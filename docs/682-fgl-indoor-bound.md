# 682. The last FindGoodLocation frame: the lot-wide "indoors" stage, and `fglinbound`

2026-11-20 (UTC), `feat/fgl-root-1120`. Inputs: the storm logs s189-type (phase-20261120-1607 / 1613 / 1622 / 1630, `[FglProf]`
with `Frames=16`), s202 / s203 (phase-20261120-1717 / 1722, no profile), the 12 profiled phases of the day; ilspycmd output
of `GlobalFunctions`, `BandInstrument`, `World`; objdump of the Steam 1.67 code. Built: ts3fixes `fglinbound`,
`fglinboundhh` (default off), shadow tuning `kFglInBoundShadow`. **NOT RUN in the game.**

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

## 3. `fglinbound` (ts3fixes, default off): the same answer, tested only where a room can be

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

Counters (ts3fixes slots 4250-4259, `kCounterSlots` 4260):

| slot | |
|---|---|
| 4250 | indoors stages taken by our path (the early stages had failed) |
| 4251 | answered by the probe |
| 4252 | bounded searches run |
| 4253 | left to EA: the radius covers the lot |
| 4254 | calls left to EA: household or unknown Sim |
| 4255 | shadow compares |
| 4256 | shadow different (**must stay 0**) |
| 4257 | errors, or start tile not marked (EA's stage) |
| 4258 | ms of our path (probe + tile pass + search) |
| 4259 | ms of EA's stage in shadow |

Files: `mods/ts3fixes/src/FglInBound.cs`, `FglInBoundCore.cs`, the branch in `RouteRelax.cs` (`RrNearbyHook`),
`Instantiator.cs`, `Fixes.cs`, `CheckLiveMap.cs`, `tools/setup/build-fixes.ts`; `tools/selftest/fglinbound`;
repro `fglinbound-check`.

## 4. What it should cost [argued from the logged counts, not measured]

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

## 6. Run plan (2-minute Isla Paradiso storm, s202's ini and switches, same seed)

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
