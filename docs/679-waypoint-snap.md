# 679. The closest-waypoint snap under `World.ArePointsConnectedByLand`

> **Code status (2026-11-23):** `[WaypointSnap]` and its Gather mode (`wpsnap*`, `wpgather*`) and `tools/native-install-once.ts` are not on main; they are posted separately, default off, not run in the game.

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

## 5. `[WaypointSnap] Gather` (default 0): a cheaper gather

Built 2026-11-20 (`wpgather.cpp`, `wpgather-core.h`), default off, NOT RUN in the game.

### 5.1 How the game treats its own list [verified, disassembly]

- The list is in the world planner W at `W+0xE0` ("FindClosest": `+0x00` list anchor, `+0x0C` P, `+0x10` W, `+0x14` the
  world manager, `+0x18` R). `0x652C50` (from `PathPlannerWorld_Init`, call at `0x654101`) appends **ten** areas of 0x44
  bytes, each flagged `0x400` ("gather"); the count is the immediate at `0x652CB6`.
- A lookup `0x652D80` walks the list for an area with the same low three flag bits, start chunk, side classes and try and
  at least the wanted extents. Hit: moved to the front, its flags replaced by the call's (without `0x400`), candidates
  reused as they are. Miss: the last area is recycled and flagged `0x400`.
- **There is no invalidation.** The only stores of `0x400` into an area are the init (`0x652D18`) and the miss path
  (through the flags argument); the only other code that touches the list is the teardown `0x654110` (frees every area and
  its vector; called at `0x657FEA`; the W destructor `0x657A80` takes the same list at `0x657AFD`). No routing write, blocked-bit change or lot change reaches it.
  A hit therefore reuses candidates gathered at any earlier time, including record pointers; that is safe in the game only
  as long as R's record area is not replaced while W lives **[inferred: not checked whether `BuildRoutingData` re-inits W]**.
- So "the game's validity rule" cannot be reused: there is none. Enlarging the list (more than ten areas) would also change
  answers, because an area that vanilla recycles and gathers fresh would then be served from an older gather.

### 5.2 What was built

The misses are made cheap and nothing else changes: which areas the game gathers, its list of ten, the scoring, the sort
and the choice stay the game's code on the game's vector.

- The gather walks the area's chunks column by column (x outside, z inside) and appends, per chunk, that chunk's
  waypoints in record order. What one chunk contributes depends on the chunk index, the filter bits of the area's flags
  (`0x4000` and the low three; `0x800` adds the water point test) and the routing data; not on the point, the other
  chunks or the try **[verified, decompile of `0x654790`]**. So the memo is per (chunk, filter bits), not per area: a
  loop over lots reuses the chunks neighbouring lots share, tries 1 and 2 reuse the chunks of try 0, and the next loop
  reuses everything while no relevant write happened.
- `0x659787` (the gather's only call site) -> `WpgGatherHook`. It derives the area's chunk list from the same inputs as
  the game (point, side class, extent, try, world origin and size), takes each chunk's rows from a per-thread table and
  appends them to the area's vector with the game's append `0x654240`, clears `0x400` and returns 1, as the gather does.
  A chunk that is not in the table is gathered **by the game** on a one-chunk area of ours (class 1, extent 1, try 0 at
  the area's own sum for that column and row) and stored.
- Rows are stored by value: position, waypoint index, chunk, type and the record as an **offset** into R's record area.
  The pointer is rebuilt from the live base, and a table is only used with the exact record area (base, size, offset
  table), R, W, P, world manager and world geometry it was filled under; any difference empties it. A stale row can
  therefore not point outside the live record area.
- Write sequence `g_snGatherSeq` (routesnap.cpp, on the existing stubs): `LotRouting_QuadTreeBuild`, `LotGraph_Build`,
  `LotTable_Insert`, `RemoveLot`, `ApplyLotUpdate` and `RestoreLotBlockedBits` (the two writers of the blocked bit,
  docs/112 §3.2), the G builds (they rewrite waypoint types, docs/112 §3.1), the terrain reload, and every routing IC
  except the plan starts and the twelve footprint-only ones. Object footprint, quadtree, polygon cache and water grid
  rows do not count: the gather reads R, the world manager, the lot lookup by position, P's lot table and the lot graph's
  waypoint hash and node positions, none of which those rows write **[inferred for the lot graph fields `+0x90`,
  `+0x120`, `+0x16C`: their writers were not enumerated; shadow mode is the check]**. If one of the counted rows or IC
  slots is not hooked, nothing is stored (`ready` lacks bit 4). `Gather` turns the `[RouteSnap]` counters on by itself.
- Tripwire: each stored chunk keeps the chunk's offset word and waypoint count; both are compared with the live ones at
  every use. A difference under an unchanged sequence counts as `stale` (a missed writer), empties the table, and the
  game's gather runs.
- Not served (the game's gather runs, counted): areas with the water point test (`0x800`; its inputs are terrain and
  ponds, no sequence for them), areas with any sum within 0.01 of a chunk border or a world bound or left of the origin
  (the game computes them in x87 single / extended precision, the hook in doubles: outside that margin both must agree),
  a write during the call, more than 16,384 rows, no memory.
- Shadow (`Gather=2`): the game's gather runs and its vector is used; the list built from the table is compared with it
  row for row (position bits, waypoint, chunk, record pointer, type, order and count). This is stronger than comparing the
  chosen waypoints or the final answer: the scoring and the choice are the game's code on that vector, so equal vectors
  give equal waypoints and an equal answer.
- Scope: only snaps under `World_ArePointsConnectedByLand` (the IC's call site `0x787282`, a second retarget over
  wpsnap.cpp's, marks them per thread); `GatherAll=1` extends it to the planner's snaps (the result is the same list, so
  household plans are not changed; default off until shadow is clean). Per thread: table 2.8 MB, scratch 0.35 MB,
  allocated at the thread's first served gather.
- Line `waypoint gather:` once a minute: gathers asked, avoided and the ms they took, partly served, game gathers and
  their ms, hook ms, chunks from the table / gathered, not-served reasons, table resets by cause, shadow checks and
  mismatches.

### 5.3 Not done

- Water areas (`0x800`): a point on open water still gathers up to 36 chunks with a point test per waypoint. Needs a
  sequence for terrain water and ponds.
- The game's own hits stay as stale as vanilla's (§5.1); making them fresh would change answers.
- An approximate variant (snap to the lot's hub waypoint, or one colour per lot) would be cheaper still but is not the
  game's answer and other mods read this call; not proposed.

## 6. Checks

- Host test `wpsnap-host-test.cpp` (Linux, freestanding): 3,000,000 rounds of random pairs against a brute-force
  nearest-waypoint reference with random writes, context changes and a wrapping clock; every memo answer equals the
  reference; row rules (cap, clock going back, context, epoch, one-bit key change, -0.0 vs +0.0), bucket replacement,
  burst bookkeeping. Pass. The same test at 4,000 rounds is `TestWpSnap` in `selftest.cpp` (no timer).
- clang-cl `/Zs /W4` of `ts3native.cpp` (Steam and `/DTS3A_TABLE`) and `selftest.cpp`: no error, no warning in the new
  files. Not built with MSVC here, not run in the game.
- `Gather` (§5): host test `wpgather-host-test.cpp` (Linux, freestanding), 2,000,000 rounds: a model of the game's
  gather (chunk grid, waypoint types, blocked bits, links into other chunks, lot waypoints, the filter, single-precision
  sums and bound tests) is the brute-force reference for whole areas; the tested path (plan in doubles, per-chunk table,
  one-chunk fills from the same model) must give the same list row for row whenever it serves, under random writes
  (blocked bits, types, lot positions, rebuilt chunks), a moved record area, writes during a fill, an age cap and a
  wrapping clock. Plus plan order and refusals (chunk border, world bound, below the origin, bad class / extent / try,
  NaN), table rules (context, sequence, header tripwire, aged entry, full slots and pool, generation wrap), row compare.
  Pass. Two mutations were tried and caught (no sequence check: rows differ in the random run; no border margin: the
  directed case). The same test at 3,000 rounds is `TestWpGather` in `selftest.cpp` (no timer). clang-cl `/Zs /W4` as
  above: no error, no warning in the new files. **Not covered anywhere:** the two stubs, the append call, the one-chunk
  area against the real gather, the write sequence's completeness. Not built with MSVC, not run in the game.

## 7. Next run

`[WaypointSnap] Memo=2 Probe=1 LogMs=20`, `[RouteSnap] Enabled=1`; repro `ip-wpsnap-storm`. Read: `waypoint snap burst:`
lines next to the `hitch:` frames (calls and gathers per burst), the minute line's mismatch count (must be 0 for
`Memo=1`), hits against calls (what the memo can save at all).

`Gather`: `[WaypointSnap] Gather=2 Probe=1 LogMs=20` (an MSVC build), repro `ip-wpgather-storm`. Read from the
`waypoint gather:` line: shadow checks > 0 with mismatch 0 and stale 0 (the condition for `Gather=1`), chunks from the
table against chunks gathered (the reuse a real lot loop gives), the water and edge counts (what stays with the game).
Then `Gather=1`: avoided gathers and their ms against the game gathers' ms. `Memo=1` and `Gather=1` are independent (a
memo answer skips the whole call).

## 8. The 2026-11-20 hang with `Gather=2 GatherAll=1` (s206, s209, s211 and the full-set runs)

Reported as "17-18 sim-cpu hitch frames of 33-39 s, 0 storm waves, no counter line". The gather is not slow; the first
gather in scope never returns.

**Cause.** `InstallWpGather(ini);` stood twice, one line after the other, in ts3native.cpp's install block [verified:
source of the built commit, and the build itself has two calls of the install function in a row]. The install read what
each call site calls as "the original" and pointed the site at its hook, without looking at what it had read. The second
pass therefore read the hooks themselves: both originals became the hooks [verified: the install stores the site's
present target into the two original pointers on every pass; both hooks call through those pointers]. From then on
`WpgGatherHook` calls itself (and `WpgSameHook` itself), about 0.7 KB of stack per level [verified: the hook's frame is
0x290 bytes plus saved registers], with no path that ends: every branch of the hook calls "the game's gather".

**What the runs show** [verified, s211 `ts3native-full.log`; s206 / s209 / the full-set runs have the same shape]:
- the install line `OK   waypoint gather: ready 0x7 ...` is printed twice (in the clean runs `waypoint gather off` is
  printed twice: the doubled call is there in every run of this build, harmless only while `Gather=0`);
- right after the world load the route worker starts the job of one route and never ends it: `route async: lock ... busy
  for 2 s during the job of route ..., still waiting (owner thread <worker>)`, repeated every 2 s up to 178 s (the end of
  the run); the sim thread's stall-watch samples sit in `Lock_Acquire` the whole time. In the runs without the lock line
  it is `route async: thread <sim> waited N s for the job of route ...`;
- the 19 `hitch:` frames are not the gather's: the clean runs (s210, s202) have the same frames with the same lengths
  (1259 / 1263 ms, 3489 / 3503, ..., 9124 / 9083, 3752 / 3861: the world load and start-up, XML and class loading in the
  samples) and then go on to more; the bad runs stop at frame 19-20 because no sim frame ends after the hang. The storm
  had 0 waves because the agent's commands are served on the sim thread, which was waiting for that job.
- What happens to the worker once its stack is used up is [inferred]: no stack-overflow exception line was logged and the
  process lived on, so the thread stopped inside the job with the routing lock held (the stall watch samples only the sim
  and main threads). The first gather in scope happened to be the worker's (a plan, `GatherAll=1`); on the sim thread it
  would have been the same recursion there.

**Why no counter line.** `MaybeLogCounters` runs at the start of a plan (`PlanLockedBody`). The minute's lines printed
before the hang had no gather yet, and the gather line is not printed while `wgCalls + wgOutScope` is 0; after the hang no
plan began again. `[WaypointSnap] Memo / Probe` were off in these runs (`waypoint snap off`), so that line was never due.

**Ruled out** by the above: a slow one-chunk gather, a table that is rebuilt on every call, more than one extra gather
per miss in shadow, a stuck counter state. None of that code ran to its end even once.

**Fix.**
- The doubled install call is gone.
- The originals are decided by `WpgChainPlan` (wpgather-core.h): once per process, and never from a site that already
  calls one of the two hooks (each alone and crossed); a refused pass writes nothing and logs `SKIP waypoint gather:
  installed already ...`. `InstallWpSnap` got the same once-only guard (the same pattern, not hit).
- `TestWpGatherChain` (wpgather-test.h; host test and `selftest.cpp`): a model of the two sites, the install run one, two
  and three times; a gather must reach the game's code in two calls, the originals must not be the hooks, a refused pass
  must write nothing; also a lost "done" word, crossed sites and wpsnap.cpp's stubs already in front. The walk with the
  hook as its own original (the bug) is asserted to never end. Mutation: without the two refusals the test fails.
- `tools/native-install-once.ts` (runs in `selftest-all.ts --ci`, so in every CI build): an `Install*(ini)` /
  `Apply*(ini)` call that stands more than once in the product's .cpp files fails the check; on the old ts3native.cpp it
  names lines 6816 and 6817. The self-test sources (`selftest.cpp`, `*-test.cpp`) call installs several times on purpose
  and are not scanned.

**Still not covered:** everything §6 lists (the stubs, the append call, the one-chunk area against the real gather). The
hang hid all of it: no shadow compare has run yet. Not built with MSVC, not run in the game.

**Run to prove it** (one shadow storm, an MSVC build of this branch): the proven baseline ini plus `[RouteSnap] Enabled=1`,
`[WaypointSnap] Gather=2 GatherAll=1` (Memo=0, Probe=0, GatherMs=0), repro `ip-wpgather-storm` or the s211 storm. Pass:
exactly one `OK   waypoint gather: ready 0x7` line; storm waves as in the baseline (13 in s210); no `route async: lock ...
busy` run beyond the baseline's; a `waypoint gather:` minute line with gathers asked > 0, shadow checks > 0, mismatch 0,
stale 0; `hook ms` against `game gathers ... ms` on that line is the shadow's own cost (expected about twice the game's
while the table fills, less once chunks repeat). Only then `Gather=1`.
