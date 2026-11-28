# 871: stacked stairs (basement flight under a ground flight) and the stair placement test

Status 2026-11-28: investigation only. No fix: bug 1 was not reproduced (the tools cannot build the reported layout yet), and
the placement test in bug 2 is EA's intended strictness as far as the code shows. Two community reports:

1. A basement flight placed directly under the flight to the next floor breaks routing: Sims go down and come straight back
   up, or cannot get back up. Players say it depends on the stairs' orientation.
2. Build mode refuses stairs unless nearly every nearby object is moved away, windows included.

## Earlier stair work (not duplicated)

Searched upstream PRs and issues for "stair":
- #525 stair wait deadline (`StairDup82.cs`);
- #637 ladderfoot;
- #648 sharelanes, angleentry;
- #651 petrelax;
- #737 stairflow (open).

Other repo pieces: `LevelLink.cs` (level links, reused here) and `LaneSnap*`. None of them touches stacked flights or the build-mode placement test.

## Bug 1: what the game does (verified in game, Sunset Valley new game, session s710, 2026-11-28)

- **Portal ends.** `Route.AddStairPortals` (IC `0x74B440`, `0x82F770`, `RoutingWorld_AddObjectPortals 0x82D2E0` kind 3)
  builds the portals from the object's `StairsHelper` slots. The per-kind lane loop sits behind an unrecovered jump table
  (`0x82DC4C`) [verified static].
- **Script side.** `Stairs.StairsPortalComponent` reads lane-0 ends from `RoutingSlot_Stairs_Bottom/Top<n>`.
  `GetStartSlotsForApproachingSim` picks the end by 3D distance. `OnPortalStart` sets the direction from the slot heights
  [verified, decompiled]. Each flight is its own object, so two stacked flights share no script state.
- **`StairStackCheck` (read-only, `mods/ts3fixes/src/StairStack.cs`).** Per level link it lists:
  - both lane-0 ends, with the level and room the game reads at each end;
  - flights whose ends lie within 1.5 m of each other;
  - with `"plan"`, a plan (no walk) from the selected Sim to 1 m outside every end, path by path (kind, portal object, start and end).
- **What we placed.** The basement tool digs (build UI state 16, tool 26, mode 0, drag on level 0). The stair tool places
  basement flights on level -1, always ascending toward lot -x: the tool's default facing, and no agent step rotates it.
- **Under an existing flight: always refused.** In "Condor Museum Lofts" (an L0->L1 flight ascending -x), the tool refused
  every basement flight whose run lies under the upper flight: 8 positions with the default (same) orientation. This held even
  after the upper flight's script object was destroyed.
- **Beside it or in line: placed and routing fine.**
  - Flights in line with the outdoor flight (basement top exit next to its bottom end) were placed.
  - So was one flight beside it.
  - Every plan succeeded: basement to L1 goes down the upper flight, then down the basement flight, with no down-and-up.
  - The flights that are placed and fully routable are therefore not the reported layout.
- **Not reproduced.** The reported layout cannot be built with the agent's tools:
  - no step rotates a stair;
  - build tools act on the level-0 plane (or level -1 inside a basement) whatever `build_level` shows, so no floor can be
    built on level 1 to take a new upper flight;
  - so the stacked pair cannot be built in the opposite orientation, nor by placing the upper flight after the basement one.

  Open lead [inferred, not measured]: with both flights ascending the same way, the basement flight's top end lands on L0
  inside the upper flight's L0 footprint (the space under its high end). A Sim arriving there can only go back down, which
  matches "down and straight back up" and "depends on orientation". It needs the layout built (a stair-rotate step and an
  upper-level build step in the agent) before any fix.

## Bug 2: the placement test (verified static)

- `0xCB97B0` (scratch name `ModularStairsPlacementHelper/DoesModularStairsIntersectObjects`) walks the flight step by step.
  Each step below the level's headroom height goes to `0xCB8C30` (`objectsToTest` / `objectsOnTile`).
- `0xCB8C30` gathers candidates in two ways: a physics range query over the step box, and the objects on the step's tiles. It
  passes each one to `0xCB5810`, which first compares flat AABBs: the object's bounds at `+0x44/+0x48/+0x50/+0x54` against
  the step box.
- An object whose AABB overlaps the step box blocks the stair only when its 8 box corners straddle the stair's sloped
  surface: some corners at or above it, some below.
  - An object entirely under the steps does not block, nor does one entirely above them (a ceiling lamp, a high shelf).
  - A window whose box spans the step surface does block.
- Then the strip over the flight's own footprint on the upper level is queried (`0xAC9460`, `Lot/GetObjectsWithinRect`,
  footprint-polygon test on). Any object there blocks, because the flight cuts a hole in that floor.
- Error strings live in the table at `0x1183230`:
  - `ModularStairsCantPlaceTop` / `Bottom`;
  - `CantReserveSpaceOnUpperFloor`;
  - `CantPlacePortal`;
  - `BlockedByWall`;
  - `CantIntersectModularStairs`.
- **Verdict: EA's design.** Every test compares the object's real bounds with the stair's step volume or the hole it cuts.
  Windows block when their box spans the steps, which is EA's intended strictness.
- **One oddity, not a confirmed bug.** The four flat AABB comparisons in `0xCB5810` (`comiss; ja` at `0xCB58EA`, `0xCB58F9`,
  `0xCB5904` and `0xCB5913`) count boxes that only touch as overlapping. Whether a real object's bounds ever exactly meet a
  step box (both are tile-aligned) was not measured: the in-game test (bookshelf beside a stair spot) did not get the shelf
  placed. Until a measurement shows placements refused by touching boxes only, this stays EA's behaviour; the fix would be
  `ja` -> `jae` (0x87 -> 0x83) at those four sites.

## Agent steps added for this (`mods/ts3agent`, hot-reloadable)

- `tool_drag {tiles, screen: true, dx?, dz?, dy?, dwell?}`:
  - pixels from `World.GetScreenSpacePos`, the game's own projection, at a tile corner plus an offset;
  - build_layout's drag, with a hover first and wall-clock ticks, so it works while build mode is paused;
  - the old floor-pick path failed with calibration errors over 20,000 px.
- `floor_grid {level, x0, z0, x1, z1}`: a solid-floor and wall map per level.
- `stair_lots {}` and `{home: true}`:
  - without `home`, vacant residential lots with an L0->L1 flight not on a foundation;
  - with `home: true`, the home lot's flights with their ids and end tiles.
- `destroy_object {id}`.

Basement repro steps:
- `reflect_call Sims3.UI.BuildController.sController.SetItemState [16]`;
- `tool_select {tool: 26}`;
- `reflect_call Sims3.SimIFace.UserToolUtils.SetUserToolMode [0]`;
- `tool_drag {tiles, screen, dx: 0, dz: 0}` corner to corner, on display level 0.

Stairs: `tool_select {tool: 16, product: "first"}`, then a one-point `tool_drag` on display level -1. The flight's top end
lands one tile toward -x of the click.
