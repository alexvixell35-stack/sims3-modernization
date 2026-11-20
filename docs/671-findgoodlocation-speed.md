# 671. The FindGoodLocation sim-cpu hitches: what the logs show, the search, and `[FglProf]`

2026-11-20 (UTC). Inputs: the Isla Paradiso route storms s142-s149 (13 phase logs, stall watch with `Names`, `hitch:` lines;
`sim.perf.data` of s142), Ghidra headless (DecompileTo) and capstone on the Steam 1.67 code, ilspycmd output of the game
assemblies. Built: `[FglProf]` (ts3native, default off, **measurement only**). **Nothing here has run in the game.** Branch
`feat/findgoodloc-speed`. No fix is built: the logs do not prove which waste a fix should remove (§4), and the two
candidates that can be argued from the code are not exact (§4.1) or belong to `placero.cpp` (§4.2).

## 1. Measured [logs only]

Frames with cause `sim-cpu` whose stall-watch samples are mostly `script ScriptCore.World.World_FindGoodLocation`:
**25 frames, 9.78 s** over the 13 phases (164-949 ms; cpu close to the frame ms), 94 sim-thread samples.
Stall watch samples start 150 ms into a frame and stop after 8, so short searches are not in the picture.

Where the 94 samples are (innermost marker function on the visible stack; `n/94` inclusive counts in the second table):

| part of the placement test | samples | share |
|---|---|---|
| `0xB41120` routing footprint check (docs/670 §2: `0x82F470` -> `0x632060` -> polygon walk `0x6641B0` -> clip `0x67C070`) | 33 | 35% |
| `0xB45B90` objects on the covered tiles ("TestIntersectionWithObjectsForFootprintPoly") | 10 | 11% |
| `0xB3F0A0` oriented box against the physics world (`Lot_RoomLookup`, `FootprintQuadtree_*`) | 9 | 10% |
| `0xAD27B0` tile quadrants covered by the polygon (in `0xB463F0`, before the checks) | 9 | 10% |
| `0xC8EC50` object data lookup (per test, in `0xB46FB0` / `0xB0B810`) | 6 | 6% |
| `0x7CDFC0` polygon bounding box | 5 | 5% |
| `0xB40DA0`, `0xB3CEF0`, `0xB0B470`, `0x9465C0`, `0xB0FD20`, `0xB3E260`, `0xB41310` | 8 | 9% |
| no marker in the 18 innermost frames (spiral, tile checks, interpreter, allocator) | 14 | 15% |

| on the stack | n/94 |
|---|---|
| `0xB463F0` (one footprint part under test) | 71 |
| `0xB41120` / `RoutingWorld_PortalCheckLocked` | 47 |
| polygon walk `0x6641B0` | 40 |
| clip `0x67C070` (leaf in 10 samples, the top leaf) | 24 |
| Sim octagon path `0xB469C0` / footprint-part loop `0xB46800` | 29 / 31 |
| `Lock_Acquire` (a +0x918 wait inside a cpu frame) | 13 (6 as the leaf) |

So a slow search is placement tests (76%), and half of the samples are inside the routing footprint gather, a quarter in
its per-cell polygon clip. The spiral itself is not the cost.

Four pairs (8 of the 25 frames) are two slow searches 0.5-1.9 s apart (s144 715 + 671 ms, s145 949 + 515, s146 423 + 323,
phase-20261120-0809 353 + 314): the look of a caller's retry, not proven (the logs have no search key).

**What the logs cannot say:** the managed caller (the script stack line holds one frame, the IC), the object (Sim, jig,
boat), the constraints, how many tiles and tests a slow search runs, whether it ends ok or failed, which check rejects.
s142's perf data does not help: 109 of 75,340 sim samples have a placement frame on their chain (0.26 s in 4 min, no burst
over 18 ms; that run had no slow search), and the frame-pointer chains break below the IC.

The same triage for the other names of the task: §6.

## 2. The search [verified: decompile + disassembly]

`World_FindGoodLocation*` -> `0xB0E130` -> one call `0xB0E3C6` of **`0xB470C0`** (cdecl: record, out position, out facing
index, out angle). The record: `+0` object, `+4` lot, `+8/+0xC/+0x10` start, `+0x14` DirectionToSelect, `+0x18`
InitialSearchDirection, `+0x1C` facing mask byte, `+0x20` RequiredRoomID, `+0x28` the managed ValidatePlacementDelegate,
`+0x2C..+0x3E` one byte per FindGoodLocationBooleans bit (`+0x2C` bit 0, `+0x2D` 1, `+0x2E` 2, `+0x2F` 3, `+0x30` 4,
`+0x31` 5, `+0x32` 6, `+0x36` 11, `+0x37` 12, `+0x33` 15, `+0x34` 16, `+0x38` 17, `+0x39` 18, `+0x3A` 19, `+0x3B` 22,
`+0x3C` 23, `+0x3D` 24, `+0x3E` 25), `+0x48` OffsetScale, `+0x4C/+0x50` ignore ids, `+0x54` MaximumSearchRadius.

The loop walks tiles from the start (spiral step `0xAFE3B0`, or a line when DirectionToSelect is set). It ends on the
first pass, or after 8 x the lot's larger side steps in a row outside the lot: a spiral search that finds nothing visits
every tile of the lot (tiles past the radius are stepped over, not skipped). Per tile: inside the lot (`0x807B30` /
`0x869360`), inside the radius, then cheap tile tests (`0xB4AE00`, room ids `0xB499D0`, `0xB44B00`, `0x634F10`, surface
kind `0xAB9580`). A tile that passes gets, when the caller wants a facing back and the mask is not 0, **up to 8 placement
tests `0xB47040`, one per allowed facing, starting at a random one** (`0x4FA840(8)`), then one test `0xB47070` with the
object's own facing. The first pass returns. The managed default is `FacingMaskUseAnyOrientation` and
`PreferEmptyTiles | Routable`, so a tile that cannot take the object costs 8 (4 with OnlyCardinalDirections) full tests.

One placement test (`0xB47040` -> `0xB46FB0` -> `0xB46B20`), fail-fast: lot and level bounds; then per footprint part
(`0xB46800` loops the object's kind-5 footprints through `0x9465C0`; an actor without one takes `0xB469C0`, a fixed
8-point circle of radius `[0x106E8A8]`) `0xB463F0`: transform the polygon (`0xB0FD20`), its box (`0x7CDFC0`), lot bounds,
covered tile quadrants (`0xAD27B0`), `0xB40DA0`, surface kind, then `0xB46160` = seven checks in this order: `0xB3ECE0`,
`0xB45B90`, `0xB3DE50`, `0xB3F0A0`, `0xB41120`, `0xB3DCF0`, `0xB3DDC0`. After the parts: `0xB41D10`, `0xB3E260`,
`0xB424B0`, `0xB45010`. Every one of them recomputes from the record; nothing is carried from one facing or tile to the
next.

## 3. `[FglProf]`: the missing numbers (ts3native, default off, measurement only)

14 call-site retargets (`fglprof.cpp`; each site checked to be an `E8` to its target, all cdecl with stack arguments
[verified disassembly]); every stub calls the game's function with the game's arguments and returns its answer:

| site -> target | what |
|---|---|
| `0xB0E3C6` -> `0xB470C0` | the search: timer, key, result, the two log lines |
| `0xB4756D` -> `0x807B30`, `0xB47579` -> `0x869360` | tiles visited / inside the lot |
| `0xB47DDF` -> `0xB47040`, `0xB4821B` -> `0xB47070` | placement tests (per facing / own facing), pass or fail, time |
| `0xB468DD`, `0xB46B0D` -> `0xB463F0` | footprint part / Sim octagon |
| `0xB4677F` -> `0xB46160` | the seven checks as a whole |
| `0xB4617F`, `0xB461BE`, `0xB4620A`, `0xB46249`, `0xB462F6`, `0xB46322` | six of the seven checks (`0xB462B7` -> `0xB41120` is `[PlaceTestRO]`'s site; its time is `0xB46160` minus the six) |

The inner sites are shared with `World_TestObjectPlacement*` and the slot test; outside a profiled search a stub tests one
global and calls on. One search is profiled at a time; a nested search (the managed delegate calling FindGoodLocation)
counts for its parent.

Per search of `LogMs` or more, two lines:

```
fgl prof: search 412 ms FAILED, object <id> lot <ptr> start x z <word>, dir -1 facing FF bools 00000014 room -1 radius 10000000 scale 1;
  tiles visited V in lot L tested T, tests N (pass P; octagon O, footprint parts F); first facing failed on A tiles: E more
  tests there M ms, a later facing passed on X; ms: tests = B3ECE0 + objects B45B90 + B3DE50 + box B3F0A0 + footprints
  B41120 ~ + B3DCF0 + B3DDC0 + part rest + test rest, search rest; repeat of a search K ms ago (same result) | no repeat
fgl prof: that search's failed tests by first failing check: ... ; managed caller (innermost first): A < B < C ...
```

and one line a minute with the totals (`searches`, `failed`, ms, max, slow count, tiles, tests, the facing census, repeats
within 10 s with their ms, failed tests by check). `repeat` = same object id, lot, start, constraints, direction, facing
mask, room and radius within 10 s (ring of 32). Keys: `[FglProf] Enabled=0`, `LogMs=50`, `LinesPerMin=30`, `Frames=8`
(script frames of the caller, 0 = none). No counter export, no managed slot. Cost while a search runs: nine timer pairs per
placement test.

Files: `native/ts3native/fglprof.cpp`, `fglprof-core.h` (tallies, repeat ring, flag bytes), `fglprof-test.h`
(`TestFglProfCore`, in selftest.cpp), `fglprof-host-test.cpp`; `ts3addr-ea169.h` (27 rows, unresolved: the EA 1.69 build
logs SKIP), `ts3native.ini`, `tools/ini-sections.json`; repro `ip-fglprof-storm`.

## 4. Waste: what is proven, what the profile must decide

| candidate | basis today | the `fgl prof` number that decides |
|---|---|---|
| 4.1 up to 8 facing tests on a tile that cannot take the object | code (§2); not counted | `first facing failed on A tiles: E more tests there M ms` vs the search ms; `a later facing passed on X` |
| 4.2 the polygon clip of every quadtree cell in the footprint gather | 26% of samples in `0x67C070`, 43% in the walk | `footprints B41120 ~ms` |
| 4.3 the same search repeated by its caller | 4 pairs of slow frames 0.5-1.9 s apart | `repeat of a search K ms ago (same result)`, the minute line's repeat ms |
| 4.4 failing searches that walk the whole lot | not known | `FAILED`, `tiles visited` |
| 4.5 object data lookup per test (`0xC8EC50`) | 6% of samples | `test rest` |

### 4.1 Facing tests (not exact; no switch built)

A Sim without a kind-5 footprint is tested with a regular 8-point circle. Turning it by 45 degrees gives the same shape,
so the 8 facing tests of one tile look like one test run 8 times. They are not: `0xB45B90` (`0xB077B0(object, data, lot,
angle, ...)`) and `0xB3F0A0` (sin / cos of `rec+0x14`) place the object's **box** with the candidate facing, and the
rotated circle's points differ in the last float bits. "Test only the first facing of a tile" can therefore lose a tile
the game accepts. Design, if the profile shows `E more tests` is most of the slow time and `a later facing passed` is rare:

- switch `[FglSpeed] FacingFirst=1` (default 0), in the `0xB47DDF` stub: after a failed facing test on a tile whose part
  was the octagon only, answer 0 for the tile's other facings without the call. Household and selected Sims exempt
  (`HhExempt`) unless `[Experimental] HouseholdToo=1`.
- every spot returned has passed the game's own test at the facing returned. What can differ: on a tile counted under
  `a later facing passed`, the game takes that tile, the switch goes on to the next tile of the spiral (one tile step
  further, 1 m x OffsetScale) or, when no later tile passes, fails. The second case must not happen: on a failed search that
  skipped any facing, run the game's search again without skipping (the search is re-entrant: its state is the record and
  the stack). A failed search then costs 1/8 more than today, so the switch only pays when the slow searches end ok.
- compare mode = today's profile: `a later facing passed on X` is exactly the number of tiles where the answer would differ.

### 4.2 The polygon walk (exact; belongs to `placero.cpp`, not built here)

`0x6641B0` clips the polygon against every child cell with `0x67C070` (Sutherland-Hodgman, two vector passes) to learn
whether they overlap. A cell whose rectangle is strictly apart from the polygon's bounding box cannot overlap: for a
1 m polygon in a lot tree that is up to three of four children per level. `[PlaceTestRO]`'s own walk (`PtWalk` /
`PtClip`, docs/670 §3.2) can make that test before the clip with the same result (strict comparison, so a touching cell
still goes through the game's clip). It then also covers the cpu frames here, but only if the clone may answer with **no
job running** (today it calls the game's `0xB41120` then; `PtStub`, `g_async.state != A_RUNNING`). Handed to the owner of
docs/670.

### 4.3 Repeats (exact memo is not provable today)

A memo "this search failed 0.7 s ago" needs every input of every check: the lot's footprints (`g_snWriteSeq` covers the
tree writes), but also objects on tiles, the physics world, room and surface data, the object's own state and the
managed delegate. No single sequence covers those, and Sims move between two tries. If the profile shows repeats with the
same result, the fix is in the managed caller (a ts3fixes switch on that retry loop), named by the `managed caller` field.

### 4.4 A relaxed variant (less space) and OhRudi's mods

The packages `OhRudi__<Pack>__Sims_need_less_Space.package` (11 packs) and `OhRudi__Routing Fix__Pets_need_less_space.package`
are in the Mods folder the harness installs into (`phasesoakr.sh` copies ts3agent / ts3fixes into the same `Mods/Packages`),
so **the s142-s149 hitches were measured with them loaded** [inferred: same folder, files dated 2026-11-10; the logs list
no packages]. A switch of ours that shrinks jig or interaction footprints would repeat what is installed. Whether the slow
searches are jig placements at all is not known (no caller, no object in the logs): the profile's `managed caller`,
`octagon / footprint parts` and `failed tests by first failing check` fields answer it. A relaxed switch is only worth a
design if most failed tests are `footprints B41120` or `box B3F0A0` against other Sims with a small miss; nothing is
built or proposed before that. Their files are prior art only and are not copied.

## 5. Reading the profile run

- most slow searches `ok` after many tiles, `E more tests` >= half of the tests, `later facing passed` ~0 -> 4.1.
- `footprints B41120 ~ms` >= 40% of tests ms -> 4.2 first (exact, all callers).
- `repeat ... (same result)` on the slow ones -> 4.3 by caller.
- `FAILED` with `tiles visited` near 8 x the lot side -> the search has no exit for a full lot; look at the caller's
  radius (`radius 10000000` = unbounded) before touching the search.

## 6. The other names of the task [logs only]

| script name on the frame | what the samples show |
|---|---|
| `World_GetLotFrontDoorImpl` 536 ms (s146) | 4 of 4 samples wait in `Lock_Acquire` under `0x631800`; the `hitch:` line has `lock918 173 (n=2, key 00631814)`; the rest of the frame is a FindGoodLocation search (`0xB470C0` on the later samples). A +0x918 wait plus §1, not a slow front-door search. `[LotFrontDoorRO]` exists (`lotfd.cpp`); key `0x631814` is one more taker for docs/670 §1's table |
| `World_ArePointsConnectedByLand` 553 / 391 ms (s144, s145) | 23 of 25 samples wait in `Lock_Acquire` under `LotTable_GetIfPresent 0x62FB60` (`lock918 250`, key `0062FB74`, in docs/670 §1's "others" row) with 261 ms of sync plans in the same frame; 2 samples are in `ClosestWaypoints_*`. A lock wait. An exact memo (the two points' land ids until a world edit) would skip the lock, but the IC first snaps each point to its closest waypoints, which is the part that takes the lock: the memo key would have to be the points themselves |
| `Objects_SetPosition` 302 ms, `Query_GetObjectsOnLot` 326 ms | one frame each; SetPosition's frame has four sync plans (world 186, host 145, water 88, post 82 ms), GetObjectsOnLot's leaf is the Sims3Performance mod's hook. Not searched further |

Neither needs a memo of its own: both are waits on the routing lock and go with docs/670.

## 7. Checks run (2026-11-20, no game)

- `fglprof-host-test.cpp` (Linux, 32-bit, freestanding): the facing census, fail tallies, repeat ring (age, result, expiry,
  eviction) and the flag-byte map. Pass.
- clang-cl `/Zs /W4` with the xwin headers: `ts3native.cpp` (Steam and `/DTS3A_TABLE`) and `selftest.cpp`: 0 errors, no
  diagnostic in the new files.
- The 14 sites and 13 targets against the 1.67 code (capstone): each site is `E8` to its target, arguments pushed, the
  callees read `[esp+4..]`.
- `node tools/selftest-all.ts --ci`: 199 PASS, 0 FAIL, 99 SKIP (local-only cases).

## 8. Not proven, not done

- **In game:** everything. The stubs sit on call sites the compiler may have built with link-time knowledge of the callee
  (registers it keeps); the sites look like plain cdecl and `[PlaceTestRO]` uses the same pattern on `0xB462B7`, but that
  switch has not run either. First run = profile run, watch for a crash in the first search.
- **MSVC:** the CI build (`/W4 /WX`) and the full selftest have not run on this branch.
- The record's third start word is printed raw (level or y; not decided from the decompile).
- No fix: §4.

## 9. Next storm (profile arm)

```
[FglProf]
Enabled=1
LogMs=50
Frames=8
```

with `[StallWatch] Managed=8` (the script stack line then names the caller for every sampled hitch as well). Read:
`OK   fgl prof: 14 call sites`, the `fgl prof: search` line pairs, the minute line.
