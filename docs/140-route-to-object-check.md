# 140. Route-to-object check read-only (`[RouteToObjRO]`, key `0x83156D`)

Design only, 2026-10-21: static work on `game-copy/bin/TS3W.exe` (Steam 1.67), no build, no game run, no queue. Sources: Ghidra headless (`DecompileTo`, `CallerTree`, `StoreScan` on the read-only TS3 project), capstone 5 over the exe (raw bytes, vtable reads), `node tools/harness/wait-ab.ts` on the existing `wait-reducers-ab` run files, and the S2 / SH / ObstructedRO / AvoidRO code and docs (`native/ts3native/steerro.cpp`, `slotro.cpp`, `obstructro.cpp`; docs/83, docs/84 §3.6, docs/90, docs/137). Tags as in AGENTS.md; ledger rows in docs/51 §198.

## 1. Why now

`wait-reducers-ab` (docs/136 §3.1): with `[SlotEnabledRO]` on (design SH, docs/90), gate 12 Contains `0x62F782` fell 12.5 -> 0.0 ms/s and a new key `0x83156D` rose 1.7 -> 9.0 ms/s. `[SlotEnabledRO]` ships on in v0.1.5, so `0x83156D` is now the sim wait that SH left behind. docs/136 inferred from the address that it is a `+0x918` take inside `RoutingWorld_RouteToObjectCheck 0x831530`. This doc confirms that, maps the path and designs a reducer.

## 2. What `0x83156D` is [verified]

**`0x83156D` is the return address of the `call Lock_Acquire 0x4E16F0` at `0x831568` in `RoutingWorld_RouteToObjectCheck 0x831530`**, the outermost `+0x918` take of the function. Bytes at `0x831559..0x83156D`: `add ecx,0x918` (ecx = the routing world, `this`), `push 0x1021CF0` (the site record `Lock_Acquire` takes as its one stack argument, `ret 4`), `E8 83 01 CB FF`. The matching release is the `call Lock_Release 0x4E17B0` at `0x8316FF` (`E8 AC 00 CB FF`). `0x831530` is `thiscall(world; out, sim, target, point, options, a6, a7, a8, a9)`, `ret 0x24`, prologue `83 EC 2C 53 8B 5C 24 3C` (no `ebp` frame); with `sim == NULL` it zeroes the result and returns before the lock (`0x831541`).

**Callers** [verified `CallerTree` depth 5 and `E8` bytes]:

| Caller | Site | Thread | Reaches the `0x83156D` wait? |
|---|---|---|---|
| `IC_RouteManager_IsRouteToObjectUnobstructed 0x74E000` | `0x74E0E2` | sim (script binding) | **yes**: no `+0x918` held on entry |
| `0x755AB0` <- `0x7583D0` <- `IC_RouteManager_IsSlotUnobstructed 0x759160` | `0x755E21` | sim | **yes**; `0x755AB0` first calls the slot position helper `0x76F0A0` at `0x755C22` (the F-B gate-12 site SH removed), then `0x831530` at `0x755E21` |
| `0x755AB0` <- `0x8182A0` <- `0x818890` (two sites) | `0x755E21` | sim | yes, but debug-draw / routing-system message path (docs/81 row 5, docs/84 §4.1); rare [inferred] |
| `RouteDest_SlotArray_Generate 0x7596E0` (vtable `0x100421C` slot `+0x10`, data ref `0x100422C`) | `0x7599BA` | worker (async plan, holds `+0x918`) or sim inside the goal step | **no** [inferred]: called from the goal step, which runs under the worker's `+0x918` or under the sim's gate-10 frame (`RouteGate` site 10, `0x75C2D0`), so the acquire is recursive and never waits |

So every `0x83156D` wait is a sim-thread script IC, `IsSlotUnobstructed` or `IsRouteToObjectUnobstructed` [verified callers; the split between the two is not measured]. The move SH caused is now explained by the call order in `0x755AB0` [verified]: before SH, `IsSlotUnobstructed` waited for the running job at gate 12 inside `0x76F0A0` and found `+0x918` free at `0x831568`; with SH, `0x76F0A0` passes during the job and the same IC call waits at `0x831568` instead. The ~40% that did not reappear are calls whose job ended in the time SH saved [inferred].

## 3. What runs under the lock

`0x831530` reads the option words (`RoutingOptions_Test 0x81C880` on the options: bits `0x40000`, `0x8000`, `0x10000`, `0x20000`) or, when `0x40000` is clear, the target's object record (`0xC8EC50` on `target+0x18`, bit 14 of `+0xA4`, only when `[0x11ED034]` is set). From that it runs up to three checks and keeps the highest result code (codes are severities; the merge is "raise to max", as `0x829250`):

| Check | When | What it touches |
|---|---|---|
| **route part `0x830340`** (`thiscall(world; out, sim, target, point, options, a6, a7, a8, a9, 1)`, prologue `55 8B EC 83 E4 F0`) | `0x40000` clear and object bit clear, or `0x40000` and `0x8000` set | routing state, §3.1 |
| ray `0x82AEC0` | object bit or option `0x10000` | physics ray `PhysicsWorld_RayCastSegment 0x686FD0` from the target's bound to the point; result 5 + hit id |
| range `0x82A8F0` | option `0x20000` and the route part not run | physics range `0x682590` ("Physics/RangeResult"), result 1 + id |

**The two physics checks need no `+0x918`** [verified `StoreScan` depth 3 of `0x82AEC0` and `0x82A8F0`: no global store, no `Lock_Acquire`, the only `LOCK` ops are the interlocked handle refcounts of `0x82A780` / `RefPtr_Release 0x7460F0`; heap stores are their own vectors and the target's transform cache `0x958560`, sim-owned]. They read the physics world, which only the sim thread writes; the sim already runs the same queries beside jobs in `CanSee*` and the pick ICs [inferred, docs/105].

### 3.1 The route part `0x830340` [verified decompile and disassembly]

In order; "early" = returns at once with that code:

1. Planner `P = [world+0x95C]` when `world+0x974` is set.
2. **Obstacle query at the point:** `RoutingWorld_ObstacleQuery 0x82EDB0(point, radius [0xFEC6DC], &vec, mode 1, a7)` (takes `+0x918` itself, recursive here). Per footprint in `vec`: plain `+1` on `[fp+4]`; skip the target's own id (`fp` sub-vtable `+4` vs `target+8/+0xC`); type (sub-vtable `+8`) in {2, 4, `0xC`, `0xD`, `0x11`, `0x15`, `0x17`}: **early code 4** (blocking footprint); plain `-1`. Then release `vec` (`0xAAC090`) and free it.
3. **Target's transform component** (`0x935770([0x11E9158])`); none: return 0. **Target's footprint in its lot:** `0x631C20` (`thiscall` on P; Sim's lot key `sim+0x48/+0x4C`, target id, level `point[9]`): with a lot key, `LotTable_Contains 0x628A90` (gate 12), `GetOrInsert 0x62EAB0` (gate 0), level slot `entry+0x74[level - entry+0x40]` for level -4..12, then `0x6649C0` (three hash walks `0x6643E0` over that level's object map; the third does a plain refcount `+1` and a **plain write-back** of the old count on each map entry it passes); with lot key 0, the world-level map walked the same way (plain `+1/-1`).
4. Target position `0x959000`. If the target has a routing slot (`0xB02360`): slot component (`0x935770([0x11E9114])`, `0x951420(0x31229A4C)`; missing: **early code 8**), slot transform (`0x959060`, slot vtable `+0x14`), and the **slot position helper `0x76F0A0`(point, target, a7, 0)**, the F-B function SH answers (docs/90).
5. Point to lot coordinates (`0x62F000`, `GetOrInsert` hit path). Target footprint point test, outer slot `+0xB8` level 0: true = **early code 0** (the point is inside the target).
6. Target footprint kind (outer `+0xC`) 1 or 9 and `+0xC4(0)` false: nearest point on the footprint (`+0xBC(0, pt, &out)`), back to world (`0x62EB40`), `0xB01770(target)`; kind 9 with the distance above `[0xFECE14]` also builds a second, shifted goal; otherwise the bit `0x10000` of `P+0xD0` decides the same flag [inferred: the `[esp+0x14]` slot tested at `0x8307F3` holds P on that path].
7. **`RoutingWorld_BlockerCheck2d 0x82E340`** from the point to the goal (`0x830838`), and, when the flag is set and the code is still below 4, a second one to the shifted goal (`0x83087F`) merged with `0x829250`. `0x82E340` takes no `+0x918` itself (it also serves `IC_RouteManager_RoutingBlockerCheck2d 0x74E110` unlocked) [verified callees]: a stack copy of the options (`0x81D3C0`, option bit 4 set on the copy with `0x81C810`, freed by `0x81CBE0`), two lot transforms `0x62EF30` (gate 0, docs/86 G-a), the **segment gather `0x631E20`** with a visitor context (`ctx+0x44` = P, `ctx+0x40` = a record with the option word `a7` and the ignore id), which runs `LotQuadTree_QuerySegment 0x667400` (plain pin `Q+0x48 +1/-1`, `FlushDeferred 0x666A40` when the pin is below 1) and collects into a footprint set (`0x628340`; released per node through footprint slot `+0x94` in `0x6282F0`); then per footprint: id (`+0x84`) in the ignore list; `0x81C970` on the options' lists; the Sim's own list (`sim+0x2C0`, `0xB69840`) when option `RouteOption (0, 4)` is set; `+0xC0` true; segment test `+0xD8(0, u0, v0, u1, v1, +0x44)` on the lot-space endpoints; then by type: 1 = portal check (`+0x88` portal, interlocked `0xB96150` / `0x643420`, lock-key test `0x946E90`), code 3; `0x14` code 1; `0xC` / `0x17` code 4; 9 code 6 with id `+0x7C`; `0x10` / `0x1D` / `0x13` / `0x18` skipped by flags; other code 7. Last, with no hit and option 4, up to 5 physics range samples along the segment (`0x682590`, `0x82A780`), code 1.

**Footprint slots used** (outer vtables of the nine classes, docs/82 §3.1, read from the exe) [verified]: `+0xC` `0x624450` (eight) / `0x70AE20` (`0xFEAE08`); `+0x44` `0x806FC0` (float, flag `+0x34 & 0x2000000`); `+0x7C` `0x806FE0`; `+0x84` `0x620CC0`; `+0x88` `0xF20360` (returns `[fp+0x20]`); `+0xB8` `0x80E8A0` / `0xBFB320`; `+0xBC` `0x80E910`; `+0xC0` `0x7EAEA0` (true) / `0x620D60` (false, `0xFEAE08`); `+0xC4` `0x80E540` (reads the cache only); `+0xD8` `0x80F080` (eight; the `0xFEAE08` table ends before it, never reached because its `+0xC0` is false). `0x80E8A0`, `0x80E910` and `0x80F080` all open with the polygon-cache check of `0x80F030`: when the level entry `[fp+0x38] + i*0x24` has valid byte `+0x20 == 0` they **rebuild it** (`0x80E5B0`), else they only read it.

## 4. What the path reads and writes, and what a running job writes

| State | `0x831530` / `0x830340` / `0x82E340` | Job writes it? | Read-only answer |
|---|---|---|---|
| Lot table, lot entry, level vector, transforms | read (gates 12, 0) | insert on a miss (W5), lot build (W4) | S2's `SrEntry` / `SrCell` / `SrXform` under QL shared; a `GetOrInsert` miss = fallback |
| Quadtree nodes, footprint lists, pin `Q+0x48`, flag `Q+0x4C` | read; plain pin `+1/-1`; flush | yes (W1-W3, pin in `LotPlan_Inner`) | S2's `SrWalk` (point walk for step 2, segment walk for step 7, no pin write; deferred flag or a moved pin = fallback) |
| Footprint refcounts `[fp+4]`, object-map entry counts | plain `+1/-1` and a plain write-back (`0x6649C0`) | yes, plain, in every job gather | **not written**: raw pointers under QL shared |
| Footprint set nodes, option copy, vectors | allocator, own stack objects | - | clone keeps fixed stack arrays |
| Polygon caches (levels 0..2) | read; **rebuilt** when invalid | yes (W6a-W6g, worker, QL exclusive) | valid byte checked first, entry bytes compared before and after (S2's T3); invalid = fallback |
| Object maps of a level (`0x6643E0` buckets) | read | **open**: who inserts into them (the lot load / footprint core `0x8321F0` on the sim, docs/83 W1-W3 on the worker) is not mapped for this map | read under QL shared; add the map's inserter to the W stubs or tripwires once found (I-198-5); until then a shadow-only item |
| Slot position helper `0x76F0A0` | lot table, portal vectors `entry+0x44/+0x48`, portal refcounts | W4/W5; portal AddRefs outside QL | the game's own body under SH (docs/90; interlocked portal refcount pair) |
| Planner flag `P+0xD0` bit 16 | read | unknown (I-198-6) | read under QL shared; shadow compares |
| Options, ignore lists, Sim `+0x2C0` list, target object, slot transforms, portal lock keys | read | no (sim-owned; the job reads them) | read directly |
| Physics world (`0x82AEC0`, `0x82A8F0`, `0x82E340` tail) | read | no (sim writes) | the game's own calls, as today's unlocked physics ICs |

**Why not design SH for the whole function (QL-shared hold over the game's `0x831530` body)?** Its body writes shared state that the job writes too, without any lock between them once `+0x918` is skipped: plain footprint refcounts (step 2, `0x631C20`, the `0x6649C0` write-back, the `0x82EDB0` copy, the set), the pin `Q+0x48` and the deferred flush (`0x667400`, `0x667480`), and polygon-cache rebuilds. SH works for `0x76F0A0` because its only serialised reads are gate calls that write nothing on a hit and its portal refcounts can be swapped to interlocked through the portal vtable; footprint refcounts are inline `add [reg+4],1`, so no swap is possible. Same verdict as docs/137 §3 for `0x82EDB0` [verified writes; I-198-3].

**Why not a cached answer per tick?** The first call per (Sim, target, point) in a tick still waits, and the answer depends on live footprints (Sims' own footprints included) and physics; script calls the two ICs mostly once per interaction attempt, so repeat hits within a tick are unlikely to cover much [inferred, not measured]. Rejected.

## 5. Design `[RouteToObjRO]`

### 5.1 Hooks: three call-site retargets, no prologue detour

Off by default: `[RouteToObjRO] Enabled=0` hooks nothing. `Enabled=1` needs `[SteerRO] Enabled=1` (QL, the 12 W1-W6 stubs, the W4 gate stub, the armed T4/T-S7 tripwire stubs), `[RouteGate] Enforce=1`, `[RouteAsync]`, and for the slot branch `[SlotEnabledRO] Enabled=1` (v0.1.5 default; without it the slot branch falls back).

| Hook | Site, bytes | Role |
|---|---|---|
| **R-acq** | `E8` at `0x831568` -> `0x4E16F0` | stub, `thiscall(lock; site)`, `ret 4`. On the owner (sim) thread, while a job runs, with the S2 state gates (§5.2) passing and `Mode=1`: set `t_rto = SKIPPED`, return without taking the lock (`0x831530` ignores `eax` after the call [verified `0x83156D`]). Otherwise jump to the game's `Lock_Acquire` (today's path, incl. the shared `LockAcquireHook`) |
| **R-call** | `E8` at `0x831633` -> `0x830340` | stub, same `thiscall` and `ret 0x28`. `t_rto == SKIPPED`: run the clone (§5.3); answer = write the 16-byte result the game writes and return; give up = take `+0x918` now through the game's `Lock_Acquire(world+0x918, 0x1021CF0)` (waits as today), set `t_rto = TOOK`, jump to the game's `0x830340`. Otherwise jump to `0x830340` |
| **R-rel** | `E8` at `0x8316FF` -> `0x4E17B0` | `t_rto == SKIPPED`: clear, return (no lock to release). `TOOK` or clear: clear, jump to `Lock_Release` |

Why call sites and not an entry detour: the option logic and the two physics checks of `0x831530` stay the game's code, unchanged and unlocked, and the lock is taken late only when the clone gives up. Calls with no route part (options `0x40000` set and `0x8000` clear, or the object bit) need no clone at all: R-acq skips, R-call is never reached, R-rel clears (`rtNoRoute`). Late locking reorders only sim-owned reads (option words, object record) before the take, which no other thread writes [verified readers; I-198-4]. `t_rto` is thread-local and cleared by R-rel on every exit; `0x831530` has no early exit between `0x831568` and `0x8316FF` [verified]. Install order: R-rel, R-call, R-acq (R-acq last, so `SKIPPED` is never set before its partners exist); any `E8` target mismatch: nothing is hooked. None of the three sites is hooked today (no hit in docs/68, `native/ts3native`, `tools/hook-sites.ts`) [verified grep].

### 5.2 When the clone runs

S2's job-path gates (docs/84 §3.6): owner thread, a job running (`A_RUNNING`), not the `+0x918` owner (so the goal-step callers in §2 never take this path), not serving a job callback, no gate frame open, `g_steerBroken` clear, tripwires armed, generation read as in 0.18.0. `Mode` is a runtime switch (`--ab rtoro`); `Mode=0, Shadow=1` compares only (§6).

### 5.3 The clone of `0x830340`

**No Sim (fix 2026-11-04):** `0x830340` answers code 0 at `0x830352` when its Sim argument is NULL, before any read [verified disassembly]. The skip path does the same: code 0, no clone, no game call, no lock. A shadow call (lock held) goes to the game. Before the fix the clone read `sim + 0x2C0` and faulted (AV reading `0x2C0`, 29 min into a soak with `[RouteToObjRO] Enabled=1`).

**Phase S (slot, only when step 4 applies):** the target's slot component and transform through the game's calls (sim-owned reads), then the game's `0x76F0A0` through SH's `SlCall` (its own QL-shared hold and gate-pass rules, docs/90 §3). SH drop or SH off: give up. Running the slot helper before the obstacle query changes nothing observable: it writes only its out point and stack, and its portal refcounts are an interlocked pair with net 0 over the call [docs/90 §2, verified there]. It is wasted work when step 2 or 3 would have returned early.

**Phase R (QL shared, try only, one hold):**

1. Obstacle query: S2's `SrQuery` point walk with visitor flag 1 (the parameter AvoidRO adds, docs/137 §4.3), radius `[0xFEC6DC]`, the record `0x82EDB0` builds for `a7`; footprints in pointer order; step 2's rule on raw pointers (no refcount): skip the target id, type in the set = code 4.
2. Target footprint: `SrEntry(contains = true)` on the Sim's lot key, level range, `SrCell`; the three `0x6643E0` walks re-implemented read-only (bucket = `id mod n`, next at `+0x10`, compare `+8` / `+0xC` / map entry `+0xC`, no count write); lot key 0 = the world map, same walk. NULL = code 0 path as the game (component found, no footprint).
3. Target position, transforms `SrXform`-style copies of `0x62F000` and `0x62EB40` (bit-exact, steerro-test), the target footprint's `+0xB8`, `+0xC`, `+0xC4`, `+0xBC` with S2's cache rule (valid byte set at level 0, 0x24 bytes compared after) and `SrSlotsOk` plus the slot targets of §3.1.
4. Each BlockerCheck2d: the option copy is replaced by direct reads of the caller's options plus the forced bit 4 (the copy only adds that bit) [verified `0x81D3C0` / `0x81C810` / `0x81CBE0` write only the stack copy]; two `SrXform` copies of `0x62EF30`; S2's segment walk with 0x82E340's record (`ctx+0x40`: option word `a7`, ignore id) instead of S2's NULL context; the set as a pointer-sorted array (the order `0x628340` gives, I-198-7); the per-footprint rules of §3.1 in order with the game's pure helpers (`0x81C970`, `0xB69840`, `0x8292B0`, slot getters, `+0xD8` with the cache rule); the portal branch calls the game's `0xB96150` / `0x946E90` / `0x643420` (interlocked refs, reads of sim-owned lock keys). Result merge with the game's rule (keep the first hit's code and id unless a higher code comes, as `0x829250`).
5. Pin `Q+0x48` and flag `Q+0x4C` unchanged across each walk, no sim-thread W1-W6 entry, no exclusive QL request, tripwire generation and epoch unchanged: else give up. Release QL.

**Phase P (no lock):** the BlockerCheck2d physics tail (up to 5 `0x682590` samples, `0x82A780`) when no footprint hit and option 4 is set: the game's calls, as in the original.

**Caps:** S2's (`kSrCap` objects per gather, local class-2 entries, depth); at most two segment gathers and one point gather per call.

### 5.4 Correctness argument

1. **Same inputs.** Every input is the caller's arguments, sim-owned objects, or the same game calls; the gather records equal what `0x82EDB0` and `0x82E340` build (steerro-test M checks the bytes).
2. **Same sets.** Under QL shared the worker's writers of the lot table, quadtree and polygon caches (W1-W6, W4) cannot run; T4 / T-S7 catch the others; pin and flag checks catch an interleaved walk (S2's argument, docs/84 §3.6). Point walks with flag 1 are AvoidRO's (docs/137 §4.5 item 2); segment walks with a visitor context are new (emu + steerro-test N).
3. **Same decisions.** The filters are the game's functions or bit-exact copies, called in the original order; no polygon test that would rebuild is called; the object-map walks copy `0x6643E0`'s bucket rule (steerro-test M).
4. **Same continuation.** The answered result is the 16 bytes `0x830340` writes; the caller (`0x831530`) then runs its physics checks and the max-merge as today. A fallback takes `+0x918` before the game's `0x830340`, so that path is the original with a later lock.
5. **No new writes on shared state.** The clone writes no refcount, pin, flag, map count or cache; only its stack, `t_rto` and counters. Phase S's writes are SH's (proven, docs/90 §4).
6. **Answer age.** Mid-job world, as S2 / AvoidRO; phase S and phase R may see the job one step apart (two holds), which equals the original run a little earlier or later. Script re-asks on the next interaction attempt.

## 6. Shadow compare

`Mode=0, Shadow=1`, only with no job running and job starts held off (`g_rgOffSimOpen`, as S2), stubs disarmed:

- **Query level** (`rtShadowQueryMismatch`, `rtShadowSegMismatch`): the game's `0x82EDB0` and `0x631E20` into private containers with the same inputs, element sequences compared with the clone's arrays, released with the game's own release calls.
- **Answer level** (`rtShadowMismatch`): R-call runs the clone (no lock), then the game's `0x830340` under the lock R-acq took as usual; code and id compared (`out[0]`, `out[2..3]`). `out[1]` is ignored: `0x831530` copies it from a stack word it never sets (`0x8316E4` -> `0x8316F2`) [verified listing], and `IsRouteToObjectUnobstructed` returns only `out[0]` [verified decompile].
- Pass: all three 0 with `rtShadowRuns` in the thousands; fallback mix printed (`rtFb[]`), result-code histogram.

Queue jobs (not added here): `bp-rtoro-shadow` (S2 Mode 1, SlotEnabledRO on, RouteToObjRO `Mode=0 Shadow=1`, BP 10 min; daytime is fine: correctness only), then `bp-rtoro-ab` on a clean night (§8.3).

## 7. Counters

Next free ts3native counters version at the time of writing: **v75** (v74 is reserved by docs/137); after the 0.36.11 merge: **v78** (v74 design U, v75 M1Retry, v76 design G, v77 reserved by docs/137). Not reserved here; reserve in docs/registry.md when the build starts.

| Field | Meaning |
|---|---|
| `rtReady` | bit 0 R-acq, 1 R-call, 2 R-rel, 3 SH link present |
| `rtMode`, `rtShadow` | runtime switches (`--ab rtoro`, `--ab rtoroshadow`) |
| `rtCalls`, `rtTaken`, `rtNoRoute` | R-acq skips, clone answers, skips with no route part |
| `rtSlot`, `rtSlotDrop` | phase S runs, SH drops |
| `rtFb[12]` | fallbacks: state, QL busy, dirty lot index, lot miss, deferred flag / pin moved, polygon cache, shape / slot, tripwire, overflow, SH off / drop, map walk doubt, job end |
| `rtUs`, `rtMaxUs` | clone time (rdtsc-scaled as S2) |
| `rtCode[9]` | answered result codes 0..8 |
| `rtShadowRuns`, `rtShadowMismatch`, `rtShadowQueryMismatch`, `rtShadowSegMismatch`, `rtShadowOrder` | §6 |
| `rtSimWriteInRo` | sim-thread W1-W6 entry during a clone (= 0) |

`tools/probe/agent.js` names them per version; `profile-world.ts` offsets checked against `selftest.cpp` asserts; `wait-ab.ts` gets the label `0x83156D: 'route-to-object check'`.

## 8. Expected gain, where the wait goes next, A/B plan

### 8.1 Key level [inferred]

With SlotEnabledRO on, `0x83156D` was 9.0 ms/s (BP). Coverage: S2's slot whitelist fell back on 31% in `bp-s2cov-shadow`; here add invalid polygon caches on the target, lot misses and SH drops. Expect 65-80% of the waits answered: **`0x83156D` 9.0 -> ~2-3.5 ms/s** (-5.5 to -7). Paths with no route part are free (`rtNoRoute`).

### 8.2 Net, and the keys that absorb moved waits [measured history, inferred forecast]

Two of two wait reducers so far moved most of their wait to the next `+0x918` taker on the same sim path (docs/136): SH 12.5 -> 0 at gate 12 with 7.3 back at `0x83156D` (58%); S2 -10.2 at `0x85FE10` with 7.5 back at avoidance `0x846D21` (74%). Expect the same here: **net summed keyed wait -2 to -4 ms/s for RouteToObjRO alone**, and for SH + RouteToObjRO versus neither, about -6 to -9 ms/s (SH's measured net -5.2 on its two keys plus this). Both are inside the summed-wait noise band (docs/136 §2: same-setting slice differences of 17-27 ms/s), so read per key.

Where it can reappear (the A/B must report each; on - off of keys a job does not target ranged from -5 to +9.4 ms/s in docs/136 §2, so a shift inside that band is not a move):

| Key | Why it is a candidate | Already in `slotenabledro` on - off |
|---|---|---|
| gate 10 goal step `0x760BFF` | after `IsSlotUnobstructed` / `IsRouteToObjectUnobstructed`, script usually plans; a sync plan (fallback, `Replan`, `PlanFromPoint`) waits here | +1.0 |
| `0x82D296` (unlabelled `+0x918` key) | rose with SH | +0.5 |
| gate 0 transform `0x62EF7A` | next transform of the next routing IC; null band -4.0 .. -0.3 | -3.7 (null) |
| gate 12 F-F `0x62FB74`, F-G `0x631F40`, front door `0x631814` | other gate-12 families in the same script stretch | 0.0 / -0.2 / -0.7 |
| destroy `0x2000003`, `UpdateSimFootprint 0x833593` / `0x8334CC` | object changes after the interaction starts (docs/88, docs/89) | -1.8 / -0.2 / -0.1 |
| steering `0x85FE10`, avoidance `0x846D21` | per-tick; take any slack; S2's null went to +9.4 | +0.3 / -2.0 |

### 8.3 A/B plan

1. Offline proof (§9) passes; `bp-rtoro-shadow` (daytime): mismatches 0, `rtSimWriteInRo` 0, fallback mix read.
2. `bp-rtoro-ab`, clean night: base = the v0.1.5 settings (SlotEnabledRO on), in-load `--ab rtoro`, 2-min slices; **two jobs with opposite start sides** (off-first and on-first) to cancel the slot-1 bias of docs/136 §2; read with `node tools/harness/wait-ab.ts` (`0x83156D` plus every key in the table above, all keyed, pump, worker job time) and `hitch-attr-report.ts` (hitches whose longest wait is `0x83156D` vs the absorbers).
3. Gates: target key down >= 4 ms/s on both jobs; no absorber up by more than its null band in both jobs; all keyed not up; 0 crashes; it/s not read as a gate (not resolvable, docs/136 §2.2).
4. If built together with AvoidRO, A/B each with the other fixed on, not as a bundle, so the moves stay attributable.

### 8.4 Cost

Per answered call one point gather, the target lookup and one or two segment gathers, comparable to S2's per-check cost; the ICs run far less often than S2's 205k checks per 10 min [inferred from the key's wait count; `rtCalls` will show it].

## 9. Selftest and offline proof plan

| Check | What |
|---|---|
| `build.cmd` (`/W4 /WX`) | all targets |
| selftest `TestRouteToObjRO` (fakes) | R-acq / R-call / R-rel state machine: off, not owner, no job, job callback, `+0x918` owner, gate frame, broken, disarmed, generation moved, job end; skip + answer, skip + give up (lock taken late, released once), no route part (no lock, cleared), an exception through the clone leaves `t_rto` clear and QL released; result merge; shadow equal / skewed / dropped |
| `steerro-test` section **M** (maps `TS3W.exe`, game code) | the game's `0x830340` vs R-call's clone on random worlds (S2's fake lots, quadtrees, nine footprint classes with polygon caches, object maps of each level, a target with and without slot, options with the list kinds and bits 1/2/4/`0x80000`/`0x100000`/`(0,4)`/`(0,0x800)`); physics and the slot helper replaced by fixed fakes on both sides; pass = equal code and id in every compared case; byte compare of `0x62F000` / `0x62EB40` / `0x62EF30` copies |
| section **N** / `tools/emu/specs/ro_segment_ctx.py` | the game's `0x631E20` -> `0x667400` with a visitor context (record, masked and leaf collects) vs `SrWalk` segment with the record; set order vs `0x628340` |
| `StoreScan` depth 3 on `0x80E910`, `0x80F080`, `0x80E540`, `0x946E90`, `0xB69840`, `0x81C970` | no store outside stack / out-parameters with a valid cache (the claim of §3.1 for the three cache slots is by analogy to `0x80F030` until then) |
| `node tools/hook-sites.ts --check`, `node tools/check-docs-code.ts`, `tools/emu/run.py --self-check` | pass; docs/68 rows for `0x831568`, `0x831633`, `0x8316FF` |

## 10. Risks

| # | Risk | Effect | Mitigation |
|---|---|---|---|
| 1 | Wait moves again (§8.2) | no it/s gain | per-key A/B with opposite start sides; the absorber table names the next lever |
| 2 | BlockerCheck2d's visitor context collects differently from S2's NULL-context walks | wrong set | emu + steerro-test N before any live run; shadow segment level |
| 3 | The level object maps (`0x6643E0`) have an inserter outside W1-W6 that runs on the worker | torn read | I-198-5 static map of inserters before the build; tripwire on it if one exists |
| 4 | `P+0xD0` bit 16 written by the job | different shifted-goal decision | I-198-6; read once inside the hold; shadow |
| 5 | Phase S and phase R see different job states | answer equal to an earlier / later original; no crash | §5.4 item 6; if the shadow shows it matters, drop phase S and give up on slot targets |
| 6 | Late lock in the fallback path changes timing of the physics checks | none: they run unlocked today on other ICs | verified store scan §3 |
| 7 | Three call-site retargets collide with a future probe on the same `E8` | double hook | docs/68 rows, `hook-sites.ts --check` |

## 11. Effort

Wall-clock for Claude with agents: **~8-12 h** build and offline proof: `rtoro.cpp` with the three stubs and the clone, reusing S2 (`SrQuery` / `SrWalk` with a record parameter, `SrEntry`, `SrXform`) and SH's `SlCall` (~4-6 h, of which the BlockerCheck2d loop is the largest part); the object-map inserter check and the `StoreScan` items (~0.5-1 h); selftest (~1-1.5 h); steerro-test M/N and the emu spec (~2-3 h); docs/68, registry, agent.js, profile-world offsets, `wait-ab.ts` label (~0.5 h). Then **2 game runs**: one daytime shadow (`bp-rtoro-shadow`, ~15 min job) and one clean night with two in-load A/B jobs (`bp-rtoro-ab` off-first and on-first, ~15 min each; IP after BP).

## 12. Built (2026-10-22, ts3native 0.36.23 + counters v93, offline, NOT run)

`native/ts3native/rtoro.cpp`, `[RouteToObjRO] Enabled=1` (default 0), `Mode` (default 1, `--ab routetoobjro` = `rtoMode` +0x3FF4), `Shadow` (default 0, `--ab routetoobjroshadow` = `rtoShadow` +0x3FF8). Needs `[RouteGate] Enforce=1` and `[RouteAsync]`; installs the S2 writer set itself (as AvoidRO); slot targets need `[SlotEnabledRO]` (else `rtoFb_10`). Parked `game-copy/ts3native.asi.0325` (combined 0324 + RouteToObjRO + CE-1/CE-4 fix (docs/154 §7), counters v93 after v95, 4130 int32; branch-only asi.routetoobjro was B1CEA17E), sha256 `E6BFDC7A0B42BBA08B04692E2A42EF2A36CE41668E9AECE91777D226E1E00425`. Queue `tools/harness/queues/routetoobjro.json`.

### 12.1 Read set re-verified in the disassembly (capstone over Steam 1.67, 2026-10-22)

| Item | Result |
|---|---|
| Three sites | `0x831568` `E8` -> `0x4E16F0` (arg `0x1021CF0`, eax unused after), `0x831633` -> `0x830340` (10 args, ret 0x28; the caller reads only its local code and id words), `0x8316FF` -> `0x4E17B0`; no exit between `0x831568` and `0x8316FF` [verified] |
| Step 2 | `0x82EDB0(point, [0xFEC6DC] = 0.123, &vec, 1, a6)`; jump table at `0x8308C0` / bytes `0x8308C8`: early types {2, 4, 0xC, 0xD, 0x11, 0x15, 0x17} [verified]; the early code-4 path writes **id 0** (`0x830523`) |
| Steps 3-4 | `0x935770(target, [0x11E9158])` none = code 0; `0x631C20(P; Sim lot, target id, point+0x24)`; slot: `0xB02360`, `0x935770([0x11E9114])` or `0x951420(0x31229A4C)` none = code 8; slot point = slot matrix row 3 through `0x959060`'s matrix, then `0x76F0A0(&pos, target, a6, 0)` [verified] |
| `0x631C20` / `0x6649C0` | lot 0: planner map `P+0x24`, holder `+8` -> object, `+0x7C` id and `+0x14` level (`0x806FD0` calls `+0x70` = `0x80E610` EnsureAllPolygons, a no-op when all levels are valid, then byte `+0x36`); else Contains, level -4..12, cell graph, maps `[Q+0x20]+0x3C` (entry for Q = none), `+0x30` (value `+8` when `+0xC == Q`), `+0x24` (holder `+8` when holder `+0xC == Q`); equal range of `0x6643E0` = the run of equal keys from the first match [verified]. I-198-5 (who inserts) stays open: read under QL shared, the shadow covers it |
| Step 5 | `0x62F000` = GetOrInsert on the **Sim's** lot + `(x·A0 + D0) + y·B0 + z·C0`; target `+0xB8(0, lot point)` true = code 0 [verified] |
| Step 6 | goal = target position (x, z, point level); kind 1/9 and `+0xC4(0)` false: `+0xBC(0, lot point, out)` then `0x62EB40` (rows `+0xE0..+0x110`, back to world); kind 9: x87 distance from the point, `> [0xFECE14]` (0.9) shifts the goal by `[0xF9A5AC]` (0.5) / d and sets the second check; kind 1: **the flag is bit `0x10000` of `+0xD0` of the object record `0xB01770(target)` = `0xC8EC50([0x11ED034]; target+0x18)`**, stored over P's stack slot (I-198-6 resolved: not the planner) [verified] |
| Step 7 | `0x82E340` 13 args (res, sim, start, goal, options, target, &ignore id, 0, flag, a7, a10, 0, a6), second call (b34 = 1) with the shifted goal and its **code only** merged (`0x829250(code2, 0, 0)`) [verified]. Its record (frame `+0x150`): `+0` = a6, `+8` = ignore id, `+0x28` = the option copy (`0x81D3C0`, bit 4 forced by `0x81C810(4, 0, 1, 0)` when option 4 holds). The gather `0x631E20` uses ctx `+0x40` = record, `+0x44` = P, `+0x50` = 0, `+0x54` = Sim (lot key), flag 1. With an options object both collectors (`0x669850` / `0x669230`) first drop ids in its lists (`0x81C970`) and keep a type-0x14 class when `RoutingOptions_Test(0x1004, 0, 0)` holds [verified]. Set = `std::set` by pointer, unsigned compare at `0x628358` (I-198-7 verified). Loop rules as §3.1; `0x814C30` is a debug draw behind byte `[0x11D90B0]` (not cloned); the portal record's handle ctor/dtor (`0xB96150` / `0x643420`) are no-ops with its null handle [verified] |
| Calls inside the hold | `0x81C970`, `0xB69840` (no store), `0x946E90` (reads, no lock), footprint slots `0x806FC0`, `0x806FE0`, `0x620CC0`, `0xF20360`, `0x80E540` (reads the level-0 entry), `0x80E8A0` / `0x80E910` / `0x80F080` (cache rule: valid byte first, 0x24 bytes compared after; their geometry `0x67B990` / `0x67D920` on the global `0x1165D28` store only to their own temporaries, 2-hop capstone scan [inferred: as S2's `0x80E8A0`]) |

### 12.2 What the build does

As §5 with these choices: R-acq is a naked stub (`RtoAcqStub`) that passes through by `jmp`, so the P0 key stays `0x83156D`; a given-up clone's late take sets `t_rtoKey = 0x83156D` (one term added to `LockAcquireHook`'s key). Phase 1 (no lock): planner, dirty lot index, the point's lot, target component, position, slot (0x76F0A0 through SH's own detour), object record, option tests, option copies, ignore ids. Phase R: one QL-shared hold (AvoidRO's protocol, own counters). S2 reuse: `SrWalk2` (segment with distinct ends; `SrWalk` forwards to it unchanged) and `SrCollect` with flag bit 2 (record options object; flags 0/1 of S2, AvoidRO and ObstructedRO unchanged, steerro-test seeds 1-3 x 3000 bit-equal). **Shortcuts (fall back, counted):** BlockerCheck2d's physics samples after a clean footprint pass (`rtoFb_8`), option 0x100000 slot children (`rtoFb_9`). Shadow: answer level (code, id) and the step-2 query set vs `0x82EDB0`; no separate segment-level compare (the answer covers it).

### 12.3 Offline checks

| Check | Result |
|---|---|
| `build.cmd` (`/W4 /WX`, all targets) | ok |
| selftest `TestRouteToObjRO` (x5) | 29 hand-derived cases (every rule above, kind 9's shifted check, 3 fallbacks) equal in Mode 1 (job) and shadow; skew counted; no-route skip, switch off, `+0x918` owner, nesting; threaded with a real `+0x918` and QL, worker flipping the fake world under QL exclusive: 0 torn reads, 0 game calls without the lock, refcounts untouched, no hang |
| `steerro-test` seeds 1-3 x 3000 | ok (incl. the three call sites on the image) |
| `node tools/hook-sites.ts --check` | the new addresses in docs/68 (6 older ones not listed, as master) |
| `node tools/check-docs-code.ts` | ok |
| Not done from §9 | steerro-test sections M/N (game's `0x830340` vs the clone on random worlds), the emu segment spec, depth-3 StoreScan of the slot functions: the shadow job is the differential check instead |

Next: `node tools/harness/queue.ts tools/harness/queues/routetoobjro.json --only bp-routetoobjro-verify` by day (5 min BP, Shadow=1: `rtoShadowMismatch` = `rtoShadowQueryMismatch` = 0, `rtoTaken` > 0, no crash), then the night pairs (alone on the SlotEnabledRO base, and inside the docs/152 B0 list). Ledger §NEW-I.

## 13. Phase P built: `[RouteToObjRO] Tail` (2026-11-04, counters v902)

Until now the clone answered `RTO_WHY_TAIL` (the game's call, the wait) whenever BlockerCheck2d `0x82E340` reached its physics tail (`0x82EB49..0x82ED0D`: no footprint hit, `b34 == 0`, option 4). `Tail=1` runs that tail as §5.3 phase P says, after QL is released: physics world `0x686A90` -> scene `[+8]`; five samples `i * ((goal.x, start.y, goal.z, goal.x) - start) * 0.25 + start`; per sample the range query `0x682590` (radius 0.15, flags `0x10060`, a vector named `"Physics/RangeResult"`); per id `0x82A780` decides "blocker", skipping the target's id (`+8/+0xC`) and the ignore id (`a8, a9`); the first blocker answers code 1 with its id; every vector freed with `0x4E3F70` [verified disassembly]. The second check (`b34`) gives only code 0 or 4 and `0x829250` merges the code with id 0, so running the tail after it gives the same result. The physics calls need no `+0x918` (§3). Runtime switch `--ab routetoobjtail` (`rtoTailMode` +0x5B78); `rtoTailRuns`, `rtoTailHits`. Selftest `TestRouteToObjTail` (stub physics: sample points, radius, flags, skip rules, first hit, result 1 only, frees, no scene) and a tail case in `TestRouteToObjRO` (Mode 1 and shadow).

**Runs [measured, combo-local 2026-11-04, save "1111 kit5", main b1d6b3c + footstep + this build, in-load A/B, 10 min, the eleven pieces and `[FootstepRO]` on in both arms].**
- `combo-rtotail-shadow` (`Shadow=1`): 13,062 shadow compares, `rtoShadowMismatch` = `rtoShadowQueryMismatch` = 0, 127 tail hits. PASS.
- `combo-rtotail` (clean, ran as `-r2`, harness PR #252): `0x83156D` 0.2 ms/s on vs 10.2 off. All keyed sim waits 39.7 vs 38.9: the wait moves to the next `+0x918` takers (destroy closure #4, `0x755990`, `0x82F4AB`), as §8.2 expected. Arm sum -2.8 ms/s (-3%), inside the +-10% single-run noise of that round.

So the tail closes the clone's last fallback, but alone it does not shorten the sim's tick: the sim meets the same job at the next taker. It pays only together with the takers after it (docs/195 §4 site C for `0x755990`) or with shorter jobs.
