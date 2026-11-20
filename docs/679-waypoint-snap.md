# 679. The closest-waypoint snap under `World.ArePointsConnectedByLand`

2026-11-20. Steam 1.67 addresses. Method: Ghidra headless decompile of `0x787240`, `0x65DF90`, `0x65BB90`, `0x6595B0`,
`0x654790`, `0x652D80`, `0x6543B0`, objdump for the two call sites, the stall-watch lines of s149, s161, s170, s188, s189.
Built: `[WaypointSnap]` (`wpsnap.cpp`, `wpsnap-core.h`), default off, NOT RUN in the game.

## 1. What one call does [verified, decompile]

`World_ArePointsConnectedByLand 0x787240` -> `0x65DF90(A, B)`:

1. `0x65BB90(A, other = B, flags 0x107, want 1, "accept without test" = 1)`: the closest waypoint of A.
2. The same for B with A as the other end.
3. Compare the two waypoints' land colour bytes (`R+0x28`, docs/676 §3). No snap result for either point = "not connected".

`0x65BB90` runs `0x6595B0` up to three times (try 0, 1, 2) until one returns a waypoint. One try:

- **Candidate area** `0x652D80`. The game already has a uniform grid: the waypoint graph is stored in chunks of 32 x 32
  world units. The area of a point is its chunk plus the neighbour on the side the point is near (position inside the
  chunk under 10: the lower neighbour too; over 20: the upper one; else the chunk alone), so 1, 2 or 4 chunks. Each try
  widens it by one chunk on every side: 9-16 chunks on try 1, 25-36 on try 2.
- **The game's own cache.** `0x652D80` keeps a short list of recently used areas, keyed by (chunk, side class, try,
  low three flag bits). An area with the same start and at least the wanted extent is reused, candidates included, and
  moved to the front. On a miss the oldest area is recycled and flagged `0x400`: gather again.
- **Gather** `0x654790` (only for a flagged area): every waypoint of every chunk of the area. A waypoint is skipped when
  it is marked dead (bit `0x80`), by type (with these flags types 5, 8, 11 and 14 are skipped, 17 always), or when none of
  its links leads to a live waypoint (a loop over its link list). For lot waypoints (types 2 and 21) the position is not
  in the chunk: it is fetched through `0x650030` (a lot lookup by position, `World_LotLookup`) and a table lookup in the
  lot. When the point itself is in water (flag `0x800`, set by `0x6595B0` from a point test), every waypoint also gets
  that point test. Each survivor is appended to the area's candidate vector (32 bytes each).
- **Score and sort** (`0x6595B0`): for every candidate the squared distance to the point and to the other end; sort by the
  first (`0x6543B0`, an introsort); then, when there are two or more candidates and the two ends are not almost the same
  point, the first six get a new score that adds a term for how far the candidate lies off the direction to the other
  end, and those six are sorted again. So "closest" is not plain distance: among the six nearest, direction towards the
  other point counts. The snap of A therefore depends on B.
- **Obstacle test: none on this path.** `0x65DF90` passes the "accept without test" byte, so the routability test
  `0x659390` (the live obstacle test docs/676 mentions) is not called here; the first candidate in score order is the
  answer. The planner's own snaps do run it.

Consequences:

- It is not a scan of all 43,942 waypoints, and a uniform grid or k-d tree over waypoint positions would only rebuild what
  the chunks already are. Candidate generation is already local.
- The cost is the gather on a cache miss, and a loop over the world's lots misses on every lot point (each lot is in
  another chunk; the area list is short). Per missed snap: all waypoints of 1-4 chunks (up to 36 after two failed tries,
  which is what a point far from any waypoint, e.g. on open water, gets), a link-list walk each, a lot lookup for each
  lot waypoint, a vector append, then scoring and an introsort of the lot.

## 2. Measured [verified, stall-watch samples; few]

Sim-thread stall samples whose stack holds `ClosestWaypoints_Find` under this IC: s149 1 (+1 in the gather without the
name), s161 4, s170 1, s189 2, s188 (natural run) 0. Innermost frames: 5 inside the gather's per-waypoint code
(`+0x38B`, `+0x4BB`, `+0x4C0` twice, the append at `0x654240`), 1 in the lot lookup under the gather (`0x650147` ->
`World_LotLookup` -> `Lot_WorldToLocal2D`), 1 in the sort's insertion pass for more than 28 candidates, 1 elsewhere. Both
snaps appear (return addresses `0x65DFEA` and `0x65E04A`). Callers by script stack: s189 `simler90Extras.Other.
StrayPetInactiveLotCheck` < `StrayPets` (two samples of one 322 ms frame, which also holds a `Query_CountObjectsOnLot`
sample); docs/677 lists `HasValidStreetParking` and `EnterHouseboatThroughGangway` for the older runs.

Ten samples say where the time is (the gather), not how many calls a frame makes or how many waypoints a gather visits.
That is what `Probe=1` is for (§4).

## 3. `[WaypointSnap] Memo` (default 0)

A per-thread table of answers of the IC's call: key = the four coordinate words exactly as passed (no rounding), a hash
of the routing objects (the object, its graph `R`, `R`'s chunk table, colour base and "components built" byte) and
`[RouteSnap]`'s write sequence `g_snWriteSeq` read before the call. A row is written only when the sequence and the
objects are unchanged after the game's call, and answers only an identical key, for at most `MemoMs` (2000). 512 rows
per thread, two per bucket. Mode 2 (shadow) runs the game's call on every hit, uses its answer and counts differences.

What it speeds up: callers that ask the same pair again while nothing was written (`HasValidStreetParking`: lot position
against its own parking spots; a lot loop from a point that has not moved; the same loop run twice in a tick). What it does
not: the first loop over the lots from a new point. That needs the gather itself to be cheaper (§5).

Why the answer is the game's:

- The call's inputs are the two points, the waypoint graph (chunks, dead bits, links, colours), the lots (positions of
  lot waypoints) and the water point test. Same words + same graph objects + no routing write = the same inputs, provided
  every writer of those bumps the write sequence. `[RouteSnap]` bumps it on its writer rows, `Footprint_SetEnabled` and
  every routing IC except the plan starts (floodmemo.cpp). **[inferred]** that this covers every write to the waypoint
  graph and to the lot table (build / buy, lot add / remove, world load); not proven from the disassembly. Hence shadow
  first, the `MemoMs` bound, and default off.
- The game's answer itself is not a pure function of those inputs: its area cache can reuse an older, wider gather
  (a point in the middle band reuses an area gathered for the upper band), so two vanilla runs with a different call
  history can pick different waypoints near a band edge **[verified in the decompile, effect not measured]**. A memo hit
  returns an answer the game gave for this exact question in this exact data state; it also skips the call's move-to-front
  in that cache, so later snaps may gather where vanilla would have reused. A shadow mismatch can therefore be either a
  missed writer or this history effect; the mismatch line prints both answers and the sequence.
- Without `[RouteSnap]` (no sequence) nothing is stored or served. A nested call, a write during the call, or no graph:
  the game's call, nothing stored.

Threads and locks: the IC runs on the calling script thread, in practice the sim thread; it takes no lock of ours and the
call below it is unchanged. The table and the burst state are thread-local, the counters interlocked, so any other thread
that reaches the IC has its own table. The route worker reaches `0x6595B0` inside plans (holding the route lock,
docs/670) but never through the IC's call site, so it never touches a memo; it does pass the gather stub when `Probe=1`,
which only reads the clock, calls the gather and adds to interlocked counters.

## 4. `[WaypointSnap] Probe` (default 0, observe only)

- Minute line `waypoint snap:`: calls and ms, longest call, memo hits / stored / not kept, shadow checks and mismatches,
  gathers under the IC by try with their ms and candidate count, gathers elsewhere (plans) with ms.
- `waypoint snap burst:` for each run of calls with gaps under 3 ms that took `LogMs` (20) or more: calls, ms, memo
  hits, gathers, candidates, the most candidates of one gather, thread. The managed caller is not named (it would cost
  a stack walk per call); the stall-watch script stack of the same frame names it.

Sites (both `E8` retargets, checked before writing): `0x787282` (the IC's call of `0x65DF90`; thiscall, four stack
words, `ret 0x10`; stub is `__fastcall` with a dummy EDX) and, for the probe, `0x659787` (`0x6595B0`'s call of the gather;
ECX only, AL). No entry detour, so `[RouteJobPhases] CandidateSplit`'s entry hooks on `0x65DF90` / `0x65BB90` keep
working under ours. EA 1.69: two unresolved rows (`0x787240`, `0x6595B0`), the section logs SKIP there.

## 5. Not built: a cheaper gather

The exact fix for the first loop is a memo of gather results per (chunk, side class, try, flag bits), larger than the
game's short list and dropped by the write sequence, fed back into the game's own scoring. Not built in this pass
because a candidate row carries a pointer into the chunk's waypoint record (read again by the scoring loop when the point
is in water), so a stale row is a wild read unless every chunk reallocation is known to bump the sequence. The probe's
numbers decide whether it is worth that proof: gathers per burst, candidates per gather, share of try 1 / 2.
An approximate variant (snap to the lot's hub waypoint, or one colour per lot) would be much cheaper but is not the
game's answer and other mods read this call; not proposed.

## 6. Checks

- Host test `wpsnap-host-test.cpp` (Linux, freestanding): 3,000,000 rounds of random pairs against a brute-force
  nearest-waypoint reference with random writes, context changes and a wrapping clock; every memo answer equals the
  reference; row rules (cap, clock going back, context, epoch, one-bit key change, -0.0 vs +0.0), bucket replacement,
  burst bookkeeping. Pass. The same test at 4,000 rounds is `TestWpSnap` in `selftest.cpp` (no timer).
- clang-cl `/Zs /W4` of `ts3native.cpp` (Steam and `/DTS3A_TABLE`) and `selftest.cpp`: no error, no warning in the new
  files. Not built with MSVC here, not run in the game.

## 7. Next run

`[WaypointSnap] Memo=2 Probe=1 LogMs=20`, `[RouteSnap] Enabled=1`; repro `ip-wpsnap-storm`. Read: `waypoint snap burst:`
lines next to the `hitch:` frames (calls and gathers per burst), the minute line's mismatch count (must be 0 for
`Memo=1`), hits against calls (what the memo can save at all).
