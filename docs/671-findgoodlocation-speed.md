# 671. The FindGoodLocation sim-cpu hitches: what the logs show, the search, and `[FglProf]`

> **Carve-out note (post/route-lock-1120, 2026-11-20):** `[FglProf]`, `[FglFacing]` and `[FglMemo]` are ported to upstream in this branch, all default off. `[FglMemo]` serves only with `[Experimental] HouseholdToo=1` and is not provably exact.

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

14 call-site retargets (`fglprof.cpp`; each site checked to be an `E8` to its target). The stubs assume no calling
convention (§3.1): the game's function runs with the game's registers and stack and returns to the game's caller itself.

| site -> target | what |
|---|---|
| `0xB0E3C6` -> `0xB470C0` | the search: timer, key, result, the two log lines |
| `0xB4756D` -> `0x807B30`, `0xB47579` -> `0x869360` | tiles visited / inside the lot (both `ret 4`) |
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
(script frames of the caller, 0 = none), `Sites=0x3FFF` (bit mask of the sites in the table's order: bit 0 the search,
1-2 tiles, 3-4 tests, 5-6 part, 7 `0xB46160`, 8-13 its checks). No counter export, no managed slot. Cost while a search runs: nine timer pairs per
placement test.

### 3.1 The s150 crash and the v2 stubs

v1 (commit 8b888fbf) used cdecl C functions as stubs. Run s150 (`[FglProf] Enabled=1`) crashed in the first search:
`C0000005`, eip = edx = a stack address, ebx = `0xB4757E`. Cause [verified disassembly]: **`0x807B30` and `0x869360` end
in `ret 4`** (the sites `0xB4756D` / `0xB47579` push one pointer and have no `add esp` after the call); the cdecl stub
called them, the callee popped the argument, the stub's own epilogue then ran 4 bytes off and returned into the argument
(the pointer to the caller's tile buffer). The other 12 targets: `0xB470C0`, `0xB47070`, `0xB463F0`, `0xB46160` and the six
checks end in plain `ret` and their sites clean the stack (`add esp, 8 / 0xC / 0x10 / 0x20`); `0xB47040` has no `ret` (it
tail-jumps into `0xB46FB0`, plain `ret`). No target reads a register before writing it in its first block; every site
tests `al` right after the call.

v2 does not depend on any of that. Per site a naked trampoline: `pushad`, `pushfd`, `fxsave`, call `FglEnter(site, slot)`,
restore all three, `jmp` to the game's function. For a call it times, `FglEnter` keeps the return address on a shadow
stack (48 frames, owner thread only) and writes `FglAfter` into the stack slot; the callee returns there with its own
`ret` / `ret N`; `FglAfter` saves everything again, calls `FglLeave(eax, &slot)` and returns to the real address. The
game's caller gets eax / ecx / edx, the flags, the FPU / SSE state and esp exactly as the callee left them. A frame whose
call never returned (an exception unwound it) is dropped when the next call arrives at or above its stack position; a
search whose own frame was unwound is replaced by the next search.

Files: `native/ts3native/fglprof.cpp`, `fglprof-core.h` (tallies, repeat ring, flag bytes), `fglprof-test.h`
(`TestFglProfCore`, in selftest.cpp), `fglprof-stub-test.h` (`TestFglProfStubs`), `fglprof-host-test.cpp`; `ts3addr-ea169.h` (27 rows, unresolved: the EA 1.69 build
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
- The 14 sites and 13 targets against the 1.67 code (capstone): each site is `E8` to its target; `ret` form of every target,
  the code after every site, first-block register reads (§3.1).
- `TestFglProfStubs` (clang-cl `/O2` and `/Od`, run alone under Wine): every stub against a `ret 4` fake and a cdecl fake
  that record the registers they get and hand back eax, ecx, edx and carry; the call site sees all of them, its own ebx /
  esi / edi and the right esp, on the pass-through path, the timed path, with a stale frame, on the search site and while
  another thread owns the profile; the tallies match. Pass. Not possible offline: the real search through the mapped 1.67
  code (it needs a loaded world: lot, object, room and physics data).
- `node tools/selftest-all.ts --ci`: 199 PASS, 0 FAIL, 99 SKIP (local-only cases).

## 8. Not proven, not done

- **In game:** v1 crashed (§3.1); v2 has not run. If it crashes again, halve `Sites` (`0x0001`, `0x001F`, `0x00FF`).
  `[PlaceTestRO]`'s C stub on `0xB462B7` is cdecl on a cdecl target (`0xB41120` ends in `ret`, the site has `add esp, 8`).
- **MSVC:** the CI build (`/W4 /WX`) and the full selftest have not run on this branch.
- The record's third start word is printed raw (level or y; not decided from the decompile).
- No fix: §4.

## 9. Next storm (profile arm)

```
[FglProf]
Enabled=1
LogMs=50
Frames=8
Sites=0x3FFF
```

with `[StallWatch] Managed=8` (the script stack line then names the caller for every sampled hitch as well). Read:
`OK   fgl prof: 14 call sites, Sites=0x3FFF`, the `fgl prof: search` line pairs, the minute line (`unwound frames` should stay 0).

## 10. s152 profile read and `[FglFacing]` (built, NOT RUN)

### 10.1 s152 [measured: `[FglProf]`, 3 min storm, no crash, `unwound frames 0`]

863 searches, 2,302 ms, 66 failed, 15 lines over 50 ms (max 145). 568,652 tiles visited, 411,657 placement tests.
**359,458 tests (87%) are further facings on a tile whose first facing failed, 1,302 ms; a later facing passed on 48 of
51,388 such tiles (0.09%).** Failed tests by first failing check: part pre-checks 334,842 (81%), footprints `0xB41120`
71,809, box `0xB3F0A0` 2,046, objects `0xB45B90` 1,555, `0xB3ECE0` 546. Repeats within 10 s: 133, all with the same
result, 966 ms.

Every search over 50 ms is a **failed whole-lot search**:

| caller | searches | ms | shape of one |
|---|---|---|---|
| `Ts3Fixes.Fixes.SafeSpotBody` < `SafeSpot` < `ShortcutToLot` (ours; fixed in ts3fixes 9f20444a) | 12 | 1,469 | Sim octagon; tiles visited 4195, in lot 3000, tested 1884, tests 15072 (8 a tile, pass 0); first failing check footprints 6128, pre-checks 8832, box 112; ms: tests 92 = footprints ~64 + objects 7 + part rest 8 + test rest 11, search rest 21; three in a row 115-125 ms apart |
| `Sims3.Gameplay.GlobalFunctions.FindGoodLocationNearby` (EA; the stack is 4 x that name, `Frames=8` ends there) | 3 | 249 | one object with footprint parts (octagon 0), same start; bools `0x14`, `0x14`, then `0x16` (StayInRoom): tiles visited 9204, in lot 1250, tested 748 / 748 / 694, tests 5984 / 5984 / 5552, pass 0; 61-65 of 80-85 ms in further facings |

So an EA caller retries too (same object, three whole-lot searches). Its caller needs `[FglProf] Frames=16`.

### 10.2 The questions

1. **Is a Sim's polygon the same for all 8 facings?** [verified decompile] `0xB469C0` uses one static table: 8 points
   `0.25 x (cos, sin)(k x [0xFC16DC])`, `[0xFC16DC]` = 0.7853982 (45 degrees). `0xB0B810` -> `0xB0B470(object, data, angle,
   x, z)` builds rotation by the angle plus translation (x, z); `0xB0FD20` applies it. The facing angle is index x
   `[0x11835D4]` = 0.7853981. So each facing's polygon is the regular octagon of radius 0.25 about (x, z), mathematically
   the same point set, **not bit-identical** (different products and sums, another start point). Two limits: `0xB0B470`
   adds a pivot offset that depends on the facing when its `data` argument is not 0, and `0xB0B810` passes 0 only while
   `[0x11ED034]` is 0.
2. **Which checks do not depend on the facing?** None of the costly ones by construction: `0xB45B90` and `0xB3F0A0` read the
   angle (the object's box), and everything in `0xB463F0` after the polygon is built reads the polygon or the tile
   quadrants it covers (`0xAD27B0` -> `0xB40DA0`'s tile tests). Truly independent: the level bounds and the surface-kind
   tests (`0xAB9580` of the position). The pre-check failures (81% of failed tests) are therefore not provable without a
   covered-quadrant argument (same quadrant set for the polygon drawn in and pushed out by 1 mm => same set for every
   facing => `0xB40DA0` equal, if its four sub-checks read nothing else: not read yet). They are cheap (they stop before
   `0xB46160`): in the SafeSpot search 8832 of 15072 tests but about 8 of 92 ms.
3. **What can be proven:** a failure at the footprint check. `0xB41120` = "a blocking footprint of the lot level overlaps
   the polygon" (docs/670 §2); it reads the record's object, lot, level and two flags besides the polygon, none of which
   changes with the facing. If it also answers 0 for the first facing's polygon **drawn in by 1 mm**, a blocker overlaps a
   region that lies inside every facing's polygon (float error is 1e-6, the margin 1e-3), so it answers 0 for all 8, and a
   test whose footprint check answers 0 fails whatever its other checks say. This is the costly class: ~64 of 92 test ms.
4. **The spiral outside the lot** (visited 4195 for 3000 in the lot, 9204 for 1250): the loop only ends after 8 x the lot
   side steps in a row outside. An exact early end needs a write to the search's own counter (a local of `0xB470C0`); the
   steps cost little (`search rest` 21 of 115 ms covers them, the tile checks and the rest). Not built.
5. **A non-identical switch** (first facing only, the others deferred): every slow search of s152 is a failed search, and a
   failed search with deferred facings must run them before it may answer "no spot" (or it loses the 0.09% tiles). It then
   costs what it costs today. Not built; the retry loops (10.1) are the lever for those.

### 10.3 `[FglFacing]` (ts3native, default off)

In `fglprof.cpp`, on `[FglProf]`'s call sites (installed by `[FglFacing]` alone when the profile is off: no timers, no
lines). After a facing test (`0xB47DDF`) that was the tile's first, failed, ran the octagon part only (`0xB46B0D`, no
`0xB468DD`) and failed at `0xB46160` with none of its six hooked checks failing (= `0xB41120`):

- the record's polygon must be 8 points, a regular octagon about the candidate position `rec+8 / rec+0xC` (every radius
  within 1 mm, neighbours 45 degrees apart; this also rules out a pivot offset on this facing), `[0x11835D4]` must be 45
  degrees and `[0x11ED034]` 0 (no pivot offset on any facing);
- the 8 points are drawn in by 1 mm in place, the function `0xB462B7` calls at that moment (the game's `0xB41120` or
  `[PlaceTestRO]`'s stub) is called once, the points are put back;
- answer 0: the tile is proven (this record, this object, this position). Its next facing tests jump to `return 0` in the
  `0xB47DDF` trampoline instead of `0xB47040`. Answer 1 (only the rim is blocked): nothing proven.

The own-facing test `0xB47070` still runs on a proven tile (its angle is not a multiple of 45 degrees). A proven tile costs
1 test + 1 footprint check + 1 own-facing test instead of 9 tests. The random start facing is drawn before the tests and
is not touched. No household gate: the answer is the game's for every Sim.

`Shadow=1`: nothing is answered. On a proven tile every later facing runs; one that passes is a mismatch (`fgl facing:
SHADOW MISMATCH ...`, 16 lines), and its polygon is compared with the proven one as a point set (0.5 mm): `another polygon`.
One line a minute: `fgl facing: mode M shadow S, searches N (failed F); tiles proven P, facing tests answered without the
call K; not proven: only the rim blocked, not the centred octagon, step / flag / site; shadow: facing tests run on proven
tiles T, passed X, another polygon Y`.

What rests on inference, and what checks it: (a) `0xB41120` is monotone in the polygon (a blocker that overlaps the
drawn-in octagon overlaps the full one, in the game's walk and in `[PlaceTestRO]`'s) and (b) a skipped `0xB47040` leaves
nothing later code reads (each test rebuilds the record's polygon, tiles and object list; the own-facing test follows).
Shadow checks (a) and the octagon premise on every proven tile; (b) is checked by the search results staying the same
(ok / FAILED counts of `fgl prof`).

Keys: `[FglFacing] Enabled=0`, `Mode=1`, `Shadow=0`. Address rows: `0x011835D4`, `0x011ED034`. Checks run (no game):
`TestFglFacing` (selftest, clang-cl under Wine: proof, 7 answered facings, the answered call's registers and stack, rim
only, not the octagon, another object, Shadow mismatch and shape, Mode 0), the octagon geometry in the host test, clang-cl
`/Zs /W4` (0 errors, nothing in the new files), `selftest-all --ci`.

### 10.4 Next storm

Pass 1 (compare only): `[FglFacing] Enabled=1 Mode=1 Shadow=1` with `[FglProf] Enabled=1 LogMs=50 Frames=16`. Read the
`fgl facing:` line: `tiles proven` > 0, `passed 0`, `another polygon 0`; a large `not the centred octagon` or `step / flag`
count means the premise does not hold in this build and the switch does nothing. Pass 2: `Shadow=0`: `facing tests answered`
> 0, failed whole-lot Sim searches with about a third fewer tests and about half the ms, same ok / FAILED counts.

## 11. s154 / s155: why v1 never fired, the jig searches, `[FglFacing]` v2 and `[FglMemo]` (built, NOT RUN)

### 11.1 v1's guard

s154 (Shadow) and s155: `tiles proven 0 ... not the centred octagon 0, step / flag / site 5692`. The octagon test passed on
all 5692 candidate tiles (so the first facing's polygon is the regular octagon about the position) and one of the three
later guards refused each time. The step is a constant of the image (0.7853981) and the site is an `E8`, so it is the
flag: **`[0x11ED034]` is not 0 in the running game** [inferred from the counter; v2 logs both values at install and when
they change]. With the flag set `0xB0B810` passes the object's data to `0xB0B470`, which may add a pivot offset
`0xB070B0(object, data)` for facings other than the object's own. v2 no longer needs to know.

### 11.2 `[FglFacing]` v2

Per search, every facing test teaches `learn[facing index][part ordinal]` = the distance from the candidate position to
the nearest edge of that part's polygon (0 when the position is not strictly inside a convex polygon), read from the
record after each `0xB463F0`. Polygon minus position depends on object, data and facing only (`0xB0B470`: rotation, (x, z),
offset), not on the tile, so a value learned on one tile holds on the next; a facing / part seen with two different values
is never trusted again in that search. When a facing fails at `0xB41120` on part j: `r` = the smallest learned value for
part j minus 1 mm (at least 2 cm), the record's polygon pointers are set to a regular octagon of circumradius `r` about
the position, the function `0xB462B7` calls now is called once, the pointers are put back. Blocked = a blocker overlaps
a region inside part j's polygon of every learned facing; each of them fails at part j at the latest. On that tile a
facing whose learned value covers `r` is answered 0; one not learned yet runs. So the first failing tile of a search
runs all its facings (learning), later tiles cost 1 test + 1 small check + the own-facing test. It covers Sims
(octagon: disc 0.231) and footprint-part objects (the jig) alike, with no symmetry argument: a rectangle's 180-degree
symmetry alone would save nothing, since the proof call costs what the saved footprint check costs. What it does not
catch: a blocker that only reaches the part outside the inner disc (`blocker outside the inner disc`), parts the position
is outside of (`no disc`), failures at another check.

Still inferred: `0xB41120` monotone in the polygon; a skipped `0xB47040` leaves nothing later code reads. Shadow=1 checks
the first on every proven tile (`passed`, `part without the disc`).

### 11.3 The jig searches [s154 log + ilspycmd]

`JamWithInstrument.RunFromInventory` -> `PlayBandInstrument.FindPlaceToPlay` -> `Guitar.RouteBeforePlaying` ->
`BandInstrument.MoveJigToGoodSpot(sim, jig, ...)`: unless the instrument plays where it stands, on a real lot it calls
`GlobalFunctions.FindGoodLocationNearby(jig, ref pos, ref forward)` (on the world lot one `FindGoodLocation` with
StayInRoom from the Sim's position). On failure it tries one `FindGoodLocation` from the lot's mailbox, then returns false
and `RouteBeforePlaying` returns false: the interaction ends, there is no retry loop in it (autonomy may pick it again).

`FindGoodLocationNearby` (strategies All, bools `PreferEmptyTiles | Routable` = 0x14, radius unbounded): up to 17
`World.TestObjectPlacement` probes at the position (8 turns each way, plus snapped ones), then native searches in this
order, each from the same start unless noted: (1) same room (`StayInRoom`), (2) one per adjoining room (start = each
door, `RequiredRoomID`), (3) **indoors** (`PlacementCheckFunc = IsInside`), (4) **outdoors** (`IsOutside`), (5) same lot (no
check). A search that finds nothing walks the whole lot. The managed check runs only on a spot whose native test passed,
and its "no" sends the search on: that is `tests 25380 (pass 196)` with `FAILED`: the Sim stood outdoors, the indoors
search (3) tested 3344 tiles, 196 facing tests passed, `IsInside` rejected each (654 ms); search (4) then found a spot
after re-testing 514 of the same tiles (271 ms, `repeat of a search 272 ms ago`). Same again for the second jig (675 +
273 ms). Searches (3), (4), (5) have the same object, lot, constraints and room: their native tests are the same tests.

A smaller jig would not have helped the 654 ms search: its spots were found (196 passes) and refused by the indoors rule,
not by space. It may shorten search (4) (514 tiles before the first pass). OhRudi's packs here hold no guitar / bass jig.

### 11.4 `[FglMemo]` (default off; not exact)

A failed facing test is kept by (position words, angle bits). The memo carries into the next search only if it has the
same object id, lot, FindGoodLocationBooleans, RequiredRoomID and ignore list, starts within `GapMs` (20) of the previous
search's end and `MaxMs` (3000) of the first one's start, and `g_snWriteSeq` (routing tree writes, `[RouteSnap]`) did not
move; else it starts empty. A held facing test is answered 0 without the call; passes are never kept, so the managed check
still sees every passing spot. For the jig: search (4) skips the failed tests of (3) and only re-tests what passed.

Why it is not exact: the tests also read objects on tiles, the physics world and the object's own state, and nothing
proves the managed caller changed none of them in the gap (in `FindGoodLocationNearby` it does not: door queries only).
So it answers only with `[Experimental] HouseholdToo=1` (the Sim behind a search is of unknown scope); without it, and
with `Shadow=1`, it only compares: a held test that passes is `fgl memo: SHADOW MISMATCH`. What can differ when it is
wrong: a spot the game would accept in the later search is skipped, and the search takes a later tile or fails.
A strictly exact memo is not provable with the data we track.

### 11.5 Checks (no game) and next storm

`TestFglProfCore` (host + selftest: inradius, octagon, facing index, learning, memo table), `TestFglFacing` (clang-cl,
under Wine, on fakes: learn / prove / answer per facing, non-facing angle, blocker outside the disc, pass, other object,
Shadow, no disc; memo store / hit / carry / other key / gap / Shadow), clang-cl `/Zs /W4` 0 errors, `selftest-all --ci`.

Pass 1 (compare only):

```
[FglProf]
Enabled=1
LogMs=50
Frames=16
[FglFacing]
Enabled=1
Mode=1
Shadow=1
[FglMemo]
Enabled=1
Mode=1
Shadow=1
```

Read `OK   fgl facing: ... [0x11ED034] = ...`; `fgl facing:` with `tiles proven` > 0, `passed 0`, `part without the disc 0`;
`fgl memo:` with `tests run that the memo held` > 0, `passed 0`. Pass 2: `[FglFacing] Shadow=0` (and `[FglMemo] Shadow=0`
with `[Experimental] HouseholdToo=1` if the memo is to answer): fewer tests and ms on the failed whole-lot searches, the
jig's second search near 0 ms, same ok / FAILED counts.

