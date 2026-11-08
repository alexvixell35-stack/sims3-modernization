# 101. Routing I/O study H: search globals census, destination side, writer completeness

Written 2026-10-18. Static only: the game was not run, no probe, no queue, no native code. Part H of the routing I/O study (PLAN "Routing off-thread" item 6). It closes the open questions of docs/91 §5, docs/93 §10 (Q6-Q8) and docs/94 §2.3 gap 1, and gives the measured input for the G-vs-R gate of docs/94 §3.4.

Tools: capstone 5 over `game-copy/bin/TS3W.exe` (Steam 1.67) with `game-copy/analysis/functions.csv`; a forward closure that follows direct calls, tail jumps, function-pointer immediates and (optionally) every slot of the resolved routing classes; a write scanner over capstone operand access flags (absolute `.data`/`.bss` stores, stores through a register loaded with a data address, data addresses handed to a callee, `fs:` stores) plus a mnemonic fallback for x87 stores; raw 4-byte reference scans of `.text`; Ghidra headless decompiles (39 functions) on a private copy `game-copy/ghidra-io-h` (copied from `ghidra-usf`, read-only runs); grep of `game-copy/decompiled`. Scratch scripts stay in the session scratchpad.

Tags as in AGENTS.md: **[verified]** = read in the disassembly, decompile or decompiled C# (address given); **[inferred]** = reasoning, with a ledger row (docs/51 §34, `I-101-*`) and the live check that closes it.

**Tool fix that affects earlier closures.** `functions.csv` gives inclusive end addresses; earlier scripts (docs/91, 93, 94 scratch helpers) treated them as exclusive, which drops the last instruction of every function, so a function that ends in a tail `jmp` lost that edge (for example `RoutingWorld_Get 0x8292D0` is a 5-byte `jmp 0x836200` and decoded to nothing). About 40 small functions, mostly vtable slots, are also missing from `functions.csv`; the scanner now synthesises their range (decode to `ret`/`jmp` before `int3` padding). With both fixes the `Route_PlanLocked` direct closure is 1,354 functions instead of 1,326 [verified]; the census below uses the fixed closures.

## 1. Summary

- **Globals census (§2).** The search writes **59 absolute `.data` addresses plus one static object reached through a pointer it hands to a callee**, in 26 items [verified, every site listed in §2.2]. Only **two items are per-search scratch that the sim's own planner shares**: the A\* generation stamp `0x11580C4` (a 16-bit word: one `add` in `AStarSearch_Init 0x61E360`, two loads in `AStarSearch_StampReader 0x61C9B0` (at `0x61CC73`) and `AStarSearch_NodeState 0x61E030`) and the polygon-clip scratch object `0x1158580` (two vectors, used only by `0x66AB70`). One item is a non-atomic counter (live quadtree count and serial `0x11CF168`, lazy builds only). Three are free-list pools that already take their own lock. The other 20 are one-time static initialisations or lazily cached flags whose value never changes after first use.
- **Heap state (§2.3).** Everything else the search writes is reached through **one root, the routing world** (`this` of `Route_PlanLocked` and the global `0x11D9128`, read in exactly one instruction, the body of `0x836200`). A\* scratch, world-planner pools and caches, lot table, lot graphs, quadtrees, footprints and polygon caches all hang off that root, so a worker-owned clone makes them private automatically.
- **G-vs-R (§3).** By count the census passes the docs/94 rule (≤ ~5 globals): 2 must-redirect items + 1 root pointer + 1 optional counter. By the rule's letter ("pointer swap, no inline absolute writes") it fails: the stamp and the scratch pointer are inline absolute operands. They are **single instructions**, though: 3 stamp sites, 1 scratch site, 1 root site, each patchable to a 5-byte call into a per-thread thunk without rewriting any function. **Recommendation: G, in that thunk form, at gate G2**, provided the P2 global-write probe (docs/94 §4.1 L2) finds no global outside the §2.2 list. What still separates G from a closed system is not globals but **live non-routing reads** (lots through the world manager, objects, physics, terrain, the managed door callback) and the **per-Sim door answers the search writes into routing state** (§2.3 H9, H10); both are equally present in R unless R copies those inputs too.
- **Destination side (§4).** Four destination classes exist (point, slot array, radial range, lot); their generators return only a bool and never write a status code [verified]. **Status codes 5-9, 11-15 and 17 are never produced in this build**: no native store in the 508-function closure of the dispatcher, the four generators, `Route_PlanCommon`, `Route_MapStatusToResult` and the goal step, none in the route IC range, and the only managed status write is `FailedBecausePlannerAborted` (3) in `Route.cs` [verified]. The numbers 5 and 8 seen in routing code belong to a separate per-slot check record, not to `mType`.
- **Interest points (§4.3):** the list at `RoutingPathImp+0x170` is only ever filled by copying from another path (`Route.Append`, `Route.SmoothenSubPathConnections`, `Route.Clone`); nothing creates the first record, and no game script reads the list. Treat as always empty [inferred, I-101-6].
- **Doors (§4.4).** The goal step asks through `LotPlanner_GoalRooms 0x63F490 -> LotPortalSearch 0x63EC40`. The search asks through three paths: `LotSearch_Entry 0x640E00 -> LotPortalSearch` (the same function as the goal step), `LotPlan_Inner -> LotPortalAStar 0x63E340` per expanded portal edge, and **`0x632460`**, which asks about every portal of the start or goal room during in-room planning and, when the answer differs from the stored state, **removes the portal-lock footprint from the lot quadtree, flips its lock flag and reinserts it** [verified]. That is a write of a per-Sim managed answer into shared routing data, new in this study.
- **route `+0x1F4` (§5):** no reader anywhere in the executable [verified: function-list-independent raw scan, the Route vtable and its sub-object slots, all 96 `RouteHandle_Lookup` callers, our own native code]. It is a dead replan counter.
- **Writer completeness (§6).** The portal ICs that docs/94 flagged as unlocked do more than edit portal lists: `AddPortal`, `RemovePortals`, the four `Add*Portals` and `AddPortalLocationVariation` reach `LotRouting_RemoveObjectFootprints 0x635700` and from there quadtree remove/insert/flush, room-id refresh, lot dirty marks and water marks, all **without `+0x918`** in their full-depth direct closure [verified]. Inline-only writers: `SetPortalCost` (portal `+0x2C`), `SetPortalAgeGenderFlags` (portal `+0x78/+0x7C`), `SetPortalLockStatus` (lot entry `+0x8F`), `SetOceanPortalOwner` (world `+0x988/+0x98C`). The lock-key ICs keep the key set **on the object's footprint component** and store only a pointer to it in the routing footprint (`fp+0x20`); the planner reads the keys through that pointer, so the snapshot must copy set contents and the 4 lock-key ICs are the only writers of them [verified].
- **Room at a point (§7).** `Lot_RoomLookup 0xA8DF80` is misnamed: it samples the lot's per-level floor-height grid (`Lot+0x290`) and returns a height [verified]. The real room lookup is `0xB499D0` on the lot's **PartitionManager** (`Lot+0x298`, allocation tag "Lot/PartitionManager", 0x290 bytes, ctor `0xB50B10`), partition kind 2 = rooms, a block-compressed grid with four room ids per tile (one per quarter) [verified]. It is built at lot load and lot creation and rebuilt by `RoutingSystem_RebuildLotRouting 0x835110` and the wall-graph edit chain [verified callers]; nothing in the plan writes it.

## 2. Globals census

### 2.1 Closures

| Closure | Roots, stops | Functions | Absolute addresses written |
|---|---|---|---|
| D: `Route_PlanLocked 0x82C6E0`, direct | direct calls, tail jumps, function-pointer immediates (the 4 world cost modes, 2 quadtree visitors, 2 others); stop at `Lock_Acquire/Release`, `EA_Alloc*/EA_Free*`, the Mono block | 1,354 | 42 + 1 x87 store |
| D+V: as D plus every slot of the 31 resolved vtables (9 footprint classes outer/sub, base and middle footprint, 3 A\* node classes, A\* search, FootprintMapEntry, PathPlannerWorld, RoutingJob, route options, RoutingPathImp) | upper bound: not every slot is called by the search | 1,818 | 58 + 1 |
| P: "search proper", `RoutingJob_Execute 0x81B2A0`, direct, with the lazy builders and pending-update appliers stopped (`LotGraph_Build`, `RoomGraph_FirstUse`, `FlushDeferred`, `ApplyPendingUpdates`, `ApplyLotUpdate`, `LotTable_GetOrInsert`, `LotTable_Rehash`, lot index rebuild, `QuadTreeBuild`, `ApplyInsert`, polygon rebuild `0x80E5B0`, `RoutingJob_dtor`, `Job_Release`) | what the worker runs once the cut has forced lazy state (docs/94 E16-E18) | 1,056 | 29 |
| P+V | P plus all resolved slots | 1,591 | 49 |

No `fs:` (TLS) store anywhere in D+V [verified]. 1,168 register-indirect sites remain in D+V; slots 0 and 4 (destructor, reference count) are most of them (docs/91 §4).

### 2.2 Every written global

"Class": **S** = per-search scratch shared with the sim's planner; **C** = counter; **P** = pool with its own lock; **L** = lazily cached value or flag (idempotent); **I** = function-local static initialised once (guard bit, then a constant). "Closure": which of §2.1 reaches it.

| # | Address(es) | Writer (site) | Class | Closure | What it is | Redirect plan for G |
|---|---|---|---|---|---|---|
| 1 | `0x11580C4` (word) | `AStarSearch_Init 0x61E360` (`add` at `0x61E360`); read at `0x61CC73` (`AStarSearch_StampReader 0x61C9B0`) and `0x61E030` (`AStarSearch_NodeState`) | **S** | D, P | A\* generation stamp: each search bumps it, nodes compare their stamp to it | **3 patch sites**: each becomes a call to a thunk that adds to / loads a per-thread stamp (the two loads are 5-byte `mov eax,[abs]`; the add is 9 bytes, call + `nop`s) [design] |
| 2 | `0x1158580` (2 vectors, 0x20 bytes) | `0x66AB70` loads its address into `ecx` (`0x66AC37`) and `0x67C6A0 -> 0x67C070` clears and refills both vectors | **S** | D, P | polygon clip scratch for quadtree cell walks; reached only through footprint room-id refresh (`0x635440 -> 0x6351A0 -> 0x634930 -> 0x667560 -> 0x6641B0`), which runs inside the search only when quadtree unpin applies deferred footprint work (§2.3 H13) | **1 patch site** (5-byte `mov ecx, imm` -> call to a thunk that sets `ecx` to a per-thread object) |
| 3 | `0x11CF168` | `0x664650` (quadtree ctor, `+1`), `0x666350` (dtor, `-1`) | C | D | live quadtree count, also stored as the new quadtree's serial at `+0x50` | optional (2 sites); a racy count only skews a serial nobody else reads in the closure [inferred, I-101-3] |
| 4 | `0x11581DC` | `FootprintMapEntry_DeletingDtor 0x61ED10`, `0x61ED80` | P | D | FootprintMapEntry free list, lock `0x11CEFA8` | none (locked) |
| 5 | `0x1165B80`, `0x1165B8C` | `0x807400` | P | D | footprint helper pool and its growth size, lock `0x11D8F90` | none |
| 6 | `0x1165D48` | `0x80EDC0`, `0x80F2F0` | P | D | footprint polygon-cache pool, lock `0x11D9068` (footprint registry) | none |
| 7 | `0x114C908` | `0x4E1270` | L | D, P | TLS index for the job system's current-thread handle (`TlsAlloc` once) | none |
| 8 | `0x11CB920` | `0x4F3CC0` | L | D, P | cached `GetACP()` code page, filled on first call (docs/51 §461) | none |
| 9 | `0x11E96C0`, `0x11E96C1` | `0x9B0530` | L | D, P | cached app startup flag (value, valid) | none |
| 10 | `0x11ECEEE`, `0x11ECEEF` | `0xC88DB0` | L | D | cached config bit from `0x11CDDE0` | none |
| 11 | `0x1157D28` | `0x5ECB30` (footprint slot `+0xF4 0x80E200`) | L | D+V | "reload pending" flag cleared after a one-time resource load `0x5EC820` | none; warm-up (I-101-2) |
| 12 | `0x11CF010` | `0x626540` | I | D, P | static guard | warm-up |
| 13 | `0x11CF018..0x11CF028` | `0x627D10` | I | D, P | static set from `0x59CD50`/`0x59CDF0` on a constant argument | warm-up |
| 14 | `0x11CF02C`, `0x11CF030` | `LotRouting_QuadTreeBuild 0x62F8A0` | I | D | static set on the first call from field `+0xB8` of the object being built for, not a compile-time constant | warm-up; I-101-4 |
| 15 | `0x11CF090..0x11CF0A0` | `Footprint_OwnerIsStairsHelper 0x643010` | I | D, P | as #13 | warm-up |
| 16 | `0x11CF0B0..0x11CF110` | `0x64C7C0` | I | D, P | six vector constants | warm-up |
| 17 | `0x11CF180`, `0x11CF184`, `0x11CF428` | `0x667740` | I | D, P | bit table built once (`or` only) and its done flag | none (repeated `or` of the same bits) |
| 18 | `0x11CF460`, `0x11CF464` | `WaterPath_Plan 0x671930` (x87 store at `0x67199F`) | I | D, P | constant square root | warm-up |
| 19 | `0x11D88D0`, `0x11D88D4` | `0x7CE7A0` | I | D, P | constant | warm-up |
| 20 | `0x11EB4C0`, `0x11EB4C4` | `0xAC5610` | I | D | constant | warm-up |
| 21 | `0x11EB4C8`, `0x11EB4CC` | `0xAC59B0` | I | D | constant | warm-up |
| 22 | `0x11D1390`, `0x11D1394` | `0x6C8900` (path slot `+0x16C`) | I | D+V | table built once | warm-up |
| 23 | `0x11D8930..0x11D8970` | `0x7D2460` (path slot `+0x124`) | I | D+V | four vector constants | warm-up |
| 24 | `0x11D8FC0..0x11D8FC8` | `0x8082F0` (footprint `+0xF8` class `0xFEBD10`) | I | D+V | tangent constants | warm-up |
| 25 | `0x11D8FCC..0x11D8FD4` | `0x808480` | I | D+V | as #24 | warm-up |
| 26 | `0x11D8FD8`, `0x11D8FDC` | `0x808620` | I | D+V | as #24 | warm-up |
| 27 | `0x11D9020..0x11D905F`, guard dword `0x11D9060` (bit 0) | `0x80D8C0` (slot `+0x18` of component vtable `0x10440A8`; guard `test` at `0x80D8CE`, `or` at `0x80D8E5`, then four constant 16-byte stores) | I | live-extended (docs/109 §8.2): `Matrix_CopyVirtual 0x80E060`, component lookup `0x958643` (N7), `DoorRouting_AddDoorPortals` (lazy) | function-local identity matrix the slot returns by address. Added 2026-10-19 (docs/109 §8.5, I-109-21); docs/102 and closure D missed it because it is reached only through a vtable slot | warm-up: the bytes are always the same, so no redirect and no copy; but the first call on a worker would race the sim's first call (guard set before the stores), so the P4 cold check (`P4Cold`, docs/94 §12.3.2) also needs bit 0 of `0x11D9060` set |

Data addresses the closure passes to callees but does not write [verified callee bodies]: `0x11582B0` and `0x1165D28` (constant polygon sets, only `PolySet_*` readers; written only by static initialisers), `0x11CDDE0` (config bit test `0x523860`), `0x11EB8E0` (default record copied from), `0x114C900`, `0x11ED220` (keys pushed to lookups), and the three pool locks.

> **Note 2026-10-18 (docs/113 §7.2, U1 live in BP):** `0x114C900` is written after all, through a computed address: it is the shared empty hash-table bucket array (`0` then `FFFFFFFF`), and the generic node-free helper `0xD2BC10` stores 0 into bucket 0 at `0xD2BC42` (`[ebx+edi*4]`) when a lot search context's still-empty table is cleared (`0x628A60`, inferred). The value is always 0, so it needs no redirect and no atomic; §2.2 gains no item. It is the only computed-address global write seen in 24 sim-thread plans.

Pointer roots the closure reads (all live state, none written by the search except through locked or atomic paths) [verified loads]:

| Root | Readers in P | What the search reaches through it |
|---|---|---|
| `0x11D9128` routing world (`0x836200`, the body of `RoutingWorld_Get`; 32 call sites in P) | 1 instruction | everything in §2.3 |
| `0x11ECBC4` world manager | 24 functions read it inline (D) | `World_LotLookup` (42 sites in P), lot list, Lot objects, lot index rebuild under `WM+0x228` |
| `0x11DC350` object service (`ObjectService_Get`, 9 sites in P) | 1 instruction | `ObjectService_FindById` (owner objects of footprints, stairs, portal owners; atomic `+4` refcount) |
| `0x11CF490` physics world (`PhysicsWorld_Get`) | 1 | range query and vertical ray (docs/18 S0) |
| `0x11D4BBC` script host | 1 | World event 59 (door callback) |
| `0x11CB864` heap, allocator pointers `0x11CEF50/68/90`, `0x11CF42C/30/48` | | allocations: the same allocator already serves the route worker [verified: async `Route.Plan` jobs run this closure on the worker today, 16,952 per 10 min in clean `bp-env-on`, docs/87 §1] |
| component bits `0x11E8FA8`, `0x11E9158`, `0x11E90FC`, `0x11E8FC8`, `0x11E9114`, `0x11E9138`, `0x11E8FC4`, `0x11ECF08` | | constants after init |

### 2.3 Heap state the search writes (all through the routing world root unless stated)

| # | State | Writers (verified sites) | Class | In G (worker clone) | In R |
|---|---|---|---|---|---|
| H1 | A\* search object Q `P+0xBC` (node table, open set) | `AStarSearch_Reset 0x61E320`, `0x61C020`, `QuadTreeCellAStar 0x61E500` | scratch | private | ours |
| H2 | world planner pools: closed bitset, node pool, open heap (`W+0x18`) | `WorldAStar_Search 0x676260`, `0x676130`, node pool grow | scratch | private | ours |
| H3 | world planner caches (`W+0xA4`, `+0xC4`, `+0xE0`, `+0x100`, `+0x128`, `+0x160`, docs/91 row 11) and `W+0x88` copied from `world+0x97D` (`0x82C753`) | plans | cache | private | ours |
| H4 | lot table inserts and rehash (`P+0x48`) | `LotTable_GetOrInsert 0x62EAB0`, `0x625940` | lazy cache | forced at cut, else private | ours |
| H5 | lot graph, room records, room connectivity | `LotGraph_Build 0x63CB90`, `RoomGraph_FirstUse 0x63D2F0`, `RoomToRoomAStar 0x6339F0` | lazy cache | forced at cut, else private | ours |
| H6 | quadtree pin counter `+0x48`, deferred flush/apply, FootprintMapEntry nodes | `0x667480`, `0x666A40`, `0x665C50`, `0x666750` | shared world state (applies pending writes) | private copy | ours |
| H7 | footprint polygon caches `[fp+0x38]` | footprint slots `+0xF8/+0xFC`, `0x80E5B0` | lazy cache | prebuilt in the clone | ours |
| H8 | footprint reference counts (`+8`, non-atomic) | `RefCounted_AddRefAt8 0x915880`, `RefCounted_ReleaseAt8 0x915890` | counter | private | n/a |
| H9 | **portal-lock footprint flag and its quadtree entry** | `0x632460`: per portal of the room, `Portal_IsLocked`, then if the stored flag differs `0x6675B0` (quadtree remove) -> footprint slot `+0xCC` (flag 8) -> `LotQuadTree_Insert 0x666C50`; if the footprint is missing, `LotTable_MarkDirty 0x631AA0`, `0x630F10`, `0x631AF0` [verified, decompile and call sites `0x63257E`, `0x632595`] | **shared world state holding a per-Sim answer** | private, recomputed each plan (the function re-checks every time) | must ask the same portals (§4.4) |
| H10 | lot entry `+0x8F` (portal-lock listener present) cleared | `Portal_LockState 0x64F2C0` when the callback returns 0 or > 3 (`0x64F4EE`) | shared world state | private copy; the sim-side value goes stale until the next lot load | same rule in our copy |
| H11 | per-plan portal answer cache | `PortalLockCache_Find 0x64F1E0` on the plan context object passed to `Portal_LockState` | per request | per job | per job |
| H12 | pending lot updates: R blocked bits, outside-room portal bits, `P+0x10E` | `RoutingWorld_ApplyPendingUpdates 0x82C420` -> `ApplyLotUpdate 0x82B590` (before `RoutingJob_Execute`) | shared world state | forced at cut (docs/94 E16) | same |
| H13 | **deferred footprint updates applied at quadtree unpin** | `LotPlan_Inner -> LotRouting_UnpinLotQuadTrees -> 0x63AF30 -> 0x639A80 -> 0x635EE0 -> LotRouting_RemoveObjectFootprints 0x635700` -> quadtree remove/insert, room ids `0x635440`, water marks `0x62D9F0 -> 0x670510` [verified path in P] | shared world state | private copy; forced at cut if pending | n/a |
| H14 | water/cell grid marks `world+0x8C+0x7C0` | `WaterGrid_MarkA 0x670510` (from H13 and lazy builds) | shared world state | private copy | ours |
| H15 | lot index rebuild | `WorldManager_RebuildLotIndex 0xC68680` under `WM+0x228` | shared, locked | stays live (locked) | not reached |
| H16 | route object, `RoutingPathImp` objects, result struct | `RoutingJob_Execute`, `RoutingJob_SplitSubPaths 0x81A340`, `0x819E10` | per request | per request | per request |
| H17 | `RoutingJob` on the stack; its destructor `Job_Release 0x59A990 -> Job_Cancel -> JobManager_*` | job manager under its own locks | per request; shared locked | unchanged | unchanged |
| H18 | object reference counts from `ObjectService_FindById` | `lock xadd` | shared atomic | unchanged | ours if objects are copied |
| H19 | managed `SimRoutingComponent.LockedDoorsDuringPlan` | event 59 callback | managed | proxied to the sim (docs/94 E12) | proxied |
| H20 | **portal reference counts** `portal+0x70` (vtable `0xFED048`: AddRef `0x64E8E0`, Release `0x64E8F0`, plain read-modify-write; delete at <= 1) | room record copy in `0x633FA0` (`0x6223C0` / `0x621D20`), `LotPath_AddPortalNodes 0x62FBE0` local vector, unpin `0x639A80` (pending adds `+0x54` into `+0x44`); live on the route worker in probe pass 3 (added 2026-10-19, docs/109 §8.9) [verified paths and live targets] | shared world state (live portals; the sim AddRefs and Releases the same portals, portal ICs unlocked) | **built 0.30.0, offline checks only** (docs/94 §21.8): the interlocked count pair while P4 is armed (H20a), one clone reference per portal element of `+0x44`, `+0x54` and room `+4`/`+0x14` with the room vectors copied (H20b), cut and acquire refuse a lot with pending `+0x54` (pointers) or `+0x64` (8-byte ids) and D4 stops a clone run before a lazy build (H20c), portal IC family in D5 (H20d). Left live: portal cost, flags and the variation vector `+0x5C` (M5 by H20d). **Open risks settled 2026-10-19 (docs/94 §21.8.1, static):** no inline `+0x70` write on portals outside the constructors [verified]; the only `+0x5C` grow of an existing portal (`OnExitBuildBuy`, gate 14) is behind D2, the other grows hit portals made after the cut, cost/flag stores cannot fault [verified paths]; no game frame catches the lazy stop, which leaks a few portal counts and KB per stop [frames verified, leaks inferred]. Left: an `AddPortalLocationVariation` tripwire and an `XcptLog` skip (~0.6 h) | ours |

## 3. G or R

| Check | Count | Verdict |
|---|---|---|
| Written globals that the sim's own planner also uses and that change per search | 2 (stamp `0x11580C4`, scratch `0x1158580`) | must be per-thread |
| Counters | 1 (`0x11CF168`) | optional |
| Root pointers the worker must see differently | 1 (`0x11D9128`, one load in `0x836200`) plus `this` of `Route_PlanLocked` | pointer swap through a thunk |
| Instruction sites to patch | 3 + 1 + 1 = **5** (+3 for the counter) | each a single instruction, no function rewrite |
| Locked pools, cached flags, static inits | 23 items | nothing; warm the statics once on the sim before enabling (I-101-2) |
| Live non-routing state read by the search (not a write-set item) | world manager (24 inline readers), object service (9 sites), physics (2), terrain and floor height (~70 call sites), event 59 | same exposure as the async jobs today; R removes it only if R also snapshots those inputs |

**Rule outcome.** docs/94 §3.4 asks for ≤ ~5 globals redirectable "by swapping a pointer the code loads (no inline absolute writes)". Count: passes (2 + 1 root). Letter: fails, because the stamp `add` and the scratch `mov ecx, imm` are inline absolute operands. Since each is one instruction, a 5-byte call into a per-thread thunk replaces it without touching the rest of the code [design]: the thunk reads a TLS slot that holds the current thread's stamp or scratch pointer, so the sim keeps the original storage and the worker gets its own. The same thunk form serves the root: `0x836200` returns the TLS world when set, else the global.

**Recommendation: G at gate G2, thunk form**, if the P2 L2 probe (docs/94 §4.1, worker-only global write log) shows no written global outside §2.2. Fall back to R if it finds more than ~3 additional per-search globals, or if the materialiser cannot give cloned footprints valid polygon caches (H7: the lazy rebuild for two footprint classes reaches live owner objects and their `X+0xA0` lock, docs/82). The remaining G work is the clone itself (docs/94 §3.2 step 3) plus the five patch sites; the live non-routing reads stay as they are for the async jobs today.

## 4. Destination side

### 4.1 Classes and generators

`Route_GenerateDestinationCandidates 0x751FF0` walks the route's destination list (`route+0x144`, list at `+0x18`), calls each destination's vtable `+0x10` with the start, the candidate vector, the non-candidate vector and the result struct, and ORs the returned bools. If every generator returned false and the result's blocker id (`+0x20/+0x24`) is set, it looks the object up and writes `mType` 16, blocker type 3 and the object's position; otherwise it clears the blocker fields [verified disassembly `0x751FF0..0x752122`].

| Destination | Vtable (8 slots) | Script IC (ctor) | Generator (`+0x10`) | What it produces [verified decompile] |
|---|---|---|---|---|
| Point | `0x10041C8` | `Route_AddPointDestination 0x7568A0` (`0x74EF80`) | `0x759560` | one candidate at the point with its lot, level and height; always returns true |
| Slot array | `0x100421C` | `Route_AddSlotArrayDestination 0x75FFB0` (`0x74F080`) | `0x7596E0` | per slot of the target object: slot transform, optional water-height clamp, `RoutingWorld_RouteToObjectCheck 0x831530`; a slot that fails the check records the blocking object id into the result (`+0x20/+0x24`) and its position, and can go to the non-candidate list (`0x74C650`); returns true only if candidates exist |
| Radial range | `0x100423C` | `Route_AddRadialRangeDestination 0x756B20` (`0x74F220`) | `0x75BE60` | rings of points between min and max radius around a point or object (`0x75B610`, `0x75AFA0`); returns candidates non-empty |
| Lot | `0x100425C` | `Route_AddLotDestination 0x7569E0` | `0x74FB00` | lot points through the world planner (`0x655EE0`); false if the lot or planner is missing |

### 4.2 Status codes 5-9, 11-15, 17

Never produced in this build [verified]:

- Native: a store scan for the immediates 5-9, 11-15 and 17 into `[reg]` over the 4-hop direct closure of the dispatcher, the four generators, `Route_PlanCommon 0x7606E0`, `Route_MapStatusToResult 0x752620` and `Route_GoalStep 0x75C2D0` (508 functions) finds two stores, `0x82B27B` (5) and `0x83050E` (8), both into the check record of `RoutingWorld_RouteToObjectCheck` (its own kind field, with the blocker id at `+8/+0xC`), not into `mType`. The same scan over every function in `0x740000-0x770000` finds only `0x755AB0` (kind 8, "object has no slot component", same record type).
- Managed: the only status write in the script code is `RoutePlanResultType.FailedBecausePlannerAborted` in `ScriptCore/Route.cs:893`; no script names any of the other unused values.
- So the native statuses are exactly {1, 2, 4, 10, 16, 18} plus 3 from C#, matching docs/93 §2.1 with its "open" rows closed. A replacement needs no code for 5-9, 11-15, 17.

### 4.3 Interest points

The list head is `RoutingPathImp+0x170` (path vtable `+0x15C` returns its address, `0x81FCD0`) [verified]. The only allocation of a "Routing/RoutingInterestPoint" record (`0x821B3D`, tag `0x10216E4`) is in path slot `+0xF8 0x821940`, and it copies each record from another path's list [verified]; slot `+0xF8` is called only by `IC_Route_Append` (`0x754992`) and `IC_Route_SmoothenSubPathConnections` (`0x761B59`), and `IC_Route_Clone` copies through `0x81FA20` [verified]. No function creates a record from anything but another record, so every list starts and stays empty [inferred, I-101-6]. No game script calls `GetNumInterestPoints` or `GetInterestPointAtIndex` (only the `SimIFace`/`ScriptCore` wrappers) [verified grep]. A replacement leaves the list empty.

### 4.4 Which doors are asked, by whom

`Portal_IsLocked 0x64F550` and `Portal_IsDiscouraged 0x64F570` are the only callers of `Portal_LockState 0x64F2C0`, the function that fires World event 59 [verified caller index]. Their callers:

| Path | Entry | Portals asked | Context (answer cache) |
|---|---|---|---|
| Goal step | `Route_GoalStep 0x75C2D0 -> LotPlanner_GoalRooms 0x63F490 -> LotPortalSearch 0x63EC40 -> PortalEdgeCost 0x634500` | portal edges walked out of the start room while testing goal-room reachability | the goal step's own context [inferred, I-101-7] |
| Search, lot level | `PathPlanner_PlanHierarchical -> LotSearch_Entry 0x640E00 -> LotPortalSearch` | same function as the goal step, from the plan's start | plan context |
| Search, lot A\* | `LotPlan -> LotPlan_Inner 0x63F6E0 -> LotPortalAStar 0x63E340 -> PortalEdgeCost` | each portal edge the lot A\* expands (locked and discouraged tests) | plan context |
| Search, in-room | `InRoomPlan 0x632E40`, `0x632630` (from `LotPlan_Inner`), `0x63AFD0` -> `0x632460 -> Portal_IsLocked` | **every portal of the room being planned** (start or goal room), unconditionally | plan context |
| Not routes | `IC_World_FindPlacesOnShoreline -> 0xC75920 -> LotSearch_Entry` | shoreline search | its own |

Consequences: (1) a replacement search must ask, on the sim thread or by proxy, every portal of the start and goal rooms (the in-room path asks all of them) plus every portal edge its lot-level search would expand; asking fewer changes `LockedDoorsDuringPlan` and the H9 flags; (2) the goal step keeps asking its own set, because it stays game code; (3) the H9 write means the lot quadtree holds the last asking Sim's answer for each portal-lock footprint, which is how the in-room cell search sees a locked door [verified mechanism; that no other reader depends on the stale value is inferred, I-101-8].

## 5. Readers of route `+0x1F4`

- Writers [verified]: `IC_Route_Replan 0x765490` (`0x7654BA`) and `IC_Route_ReplanFromPosition 0x765550` (`0x765589`) add 1; `Route_ctor 0x75D4B0`, `0x760200` and `0x760370` zero it.
- Readers: none [verified]. Checked: every instruction with displacement `0x1F4` in all `.text` (264 operands in 175 listed functions, plus a raw byte scan decoded from the preceding `int3` padding that adds 2 operands in 2 unlisted functions, neither in route code); the only functions in `0x740000-0x770000` with that displacement are the five writers; all 96 callers of `RouteHandle_Lookup 0x74B2C0` are in that range; the Route vtable `0x10047CC` and its sub-object tables `0x10047AC` and `0x100479C` (18 slot functions) have no access at `+0x1F4` or at the sub-object-adjusted offsets; our own `native/` code only mentions it in a comment.
- Verdict: a dead replan counter; a replacement may ignore it.

## 6. Writer completeness: unlocked routing writers

Full-depth direct closures (no hop limit), `+0x918` detected by `0x918` in the 20 instructions before a `Lock_Acquire` [verified]:

| IC | Closure | `+0x918` | Event | Routing writes [verified sites] |
|---|---|---|---|---|
| `AddPortal 0x757E10` | 404 | none | none | `LotRouting_AddPortal 0x6379E0` (portal list, portal object build `0x64EF70`), then `0x635920 -> LotRouting_RemoveObjectFootprints 0x635700` -> quadtree remove/insert/flush (`0x666E30`, `0x666A40`, `0x665C50`, `0x666750`), FootprintMapEntry pool (locked), room ids `0x635440`, footprint sets `0x628340`, lot dirty `0x631AF0`, water marks `0x62D9F0 -> 0x670510`, lot transform helpers `0x62EB40`/`0x62EF30` |
| `AddStairPortals 0x74B440`, `AddPlatformPortals`/`AddPoolPortals 0x74B410`, `AddDivingBoardPortals 0x74B3B0`, `AddWaterSlidePortals 0x74B3E0` | 428 each | none | none | per-kind helpers (`0x82F770` for stairs, docs/94 E9) `-> RoutingWorld_AddObjectPortals 0x82D2E0 -> LotRouting_AddPortal`, and `0x82D2E0 -> 0x636130 -> 0x635700` (same footprint and quadtree writes) |
| `AddPortalLocationVariation 0x74D3F0` | 401 | none | none | `LotRouting_AddPortalVariation 0x637C30 -> 0x635920 -> 0x635700` (same) |
| `RemovePortals 0x74D6B0` | 287 | none | none | `LotRouting_RemovePortals 0x6399E0 -> 0x635EE0 -> 0x635700` (same) |
| `SetPortalCost 0x758110` | 60 | none | none | portal `+0x2C` cost (`0x7581C8`); portals found by `LotRouting_PortalsForObject 0x62F760` (lot table find guarded by `LotTable_Contains`, no insert) |
| `SetPortalAgeGenderFlags 0x758220` | 60 | none | none | portal `+0x78/+0x7C` 64-bit mask (`0x7582CB`, `0x7582CE`), read by `Portal_LockState` |
| `SetPortalLockStatus 0x74B490` | 54 | none | none | lot entry `+0x8F` (`0x74B4BD`) |
| `FootprintAddLockKey 0x74B5C0`, `RemoveLockKey 0x74B600`, `SetLockKeys 0x74B540`, `ClearLockKeys 0x74B680` | 69-74 | none | none | `RouteManager_FootprintLockKeyById 0x82B3E0 -> RouteManager_FootprintLockKeyApply 0x82B2B0`: object's footprint component -> key set get/create `0x947700`; routing footprint by key (`LotRouting_FindFootprintByKey 0x62F600`, else the world map `+0x8B8` via `0x82AD60`); footprint slot `+0x8C` = `Footprint_SetLockKeyObject 0x807110` stores the set pointer at `fp+0x20` (non-atomic refcount), slot `+0x94` releases. Then the IC edits the set: add `0x948050` (sorted insert `0x62C010`), remove `0x9479D0` (`0x81CF40`), assign `0x947FA0` (script host slot `+0x68` marshals the key array), clear |
| `SetOceanPortalOwner 0x74B470` | 4 | none | none | world `+0x988/+0x98C` (`0x829520`) |
| `BeginPortalRouting 0x74D720` | 154 | none | none | per-Sim portal traversal state in the locomotion range `0x839000-0x83F000`; not a search input [inferred from the range and callees] |
| `DestroyPortalHandle 0x74B380` | 2 | none | none | handle table only |
| `Add/RemoveDiscourageFootprint`, `Add/RemovePrivateFootprint`, `PlaceSocialFootprintObjectImpl` | 284-891 | **yes** | discourage add fires `World_FireEvent` | as docs/94 E6/E7 |

Indirect calls not resolved in the unlocked closures (by slot): destructor and reference-count slots 0/4; `+0x90/+0x94` on objects at `[x+0x84]`/`[x+0x88]` in `0x62DE40` and `LotGraph_dtor` (allocator interface [inferred]); `0x4FB8D0`'s stack function pointer (a generic container visitor); `RoutingWorld_AddObjectPortals` `+0x14` on the portal-provider object and `SetPortalCost` `+0xC` with hash `0x90566B0E` (object-model interface queries [inferred]). Resolving every routing-range vtable by slot (an upper bound that includes unrelated classes) grows these closures to 512-1,355 functions and does reach a `+0x918` acquire, through classes the ICs never hold [inferred]; that is not a lock guarantee.

**What this changes in docs/94 §2.2.** E9 (portals) is not only a portal-list edit: the portal ICs run the same footprint and quadtree writers as object footprint updates, unlocked. The H snapshot's writer-layer leaves (`0x635700`, W1-W6) see those writes, so dirty marking at the leaves covers them; the IC slot swaps are still needed for the inline-only writers (portal cost and masks, lot `+0x8F`, ocean owner) and for the lock-key set contents, which live on the object side and are reachable only through `fp+0x20`. Today these unlocked writes race any job reading the same lot (an existing exposure of the async jobs) [inferred, I-101-9].

## 7. Room at a point and its writers

| Function | What it is [verified decompile and disassembly] |
|---|---|
| `0xA8DF80` (CSV name `Lot_RoomLookup`, misnamed) | floor height at a lot-local point on a level: `this` = `Lot+0x290`; level range `+0x10/+0x14`; outside the lot it falls back to `World_GetHeightAt`; inside it interpolates per-level corner heights (`0xA8CCE0`, `0x77D500`) |
| `0xAB78B0` | level at a point: walks levels top-down with `0xA8DF80` until the floor is below the point |
| `0x74BC60` | goal-step helper: world to lot-local (`0xAB88A0`), then `0xB499D0` |
| `0xB499D0` | room id at a point: `this` = PartitionManager (`Lot+0x298`), partition = `PM+0x44 + kind*4` (kind 2 from the goal step, `0x624780` and `IC_World_GetRoomIdImpl`); quantises the point to tile, level and quarter (`0xAF9770`, `0xAF9580`), then `0xC05430` |
| `0xC05430` / `0xAA2160` | partition grid: base level `+0x10`, grid `+0x14` with dimensions `+4/+8/+0xC`; per 4x4-tile block a 16-bit index (`+0x48`), flag `0x8000` = whole block uniform (`+0x38`), else per-tile cells (`+0x28`); a cell is four 16-bit room ids, one per tile quarter |
| `0x635440` | footprint-to-room membership for an object footprint: collects the room ids of the footprint's level from the partition's room table (`+0x38`, `0xC08510`, "Routing/roomIDs"), invalidates those rooms' records (`0x630F70`: record built byte and lot `+0x8C`), marks the lot dirty |
| `0x624780` | room index for the planner: room id + `0x10`, or level + 4 outdoors, `-1` none |

Data source and writers:

- **PartitionManager** ("Lot/PartitionManager", 0x290 bytes, ctor `0xB50B10`) is created for a lot in `0xAC9B10` (lot creation path `0xACEB20`: houseboat create, lot remove/replace, `0xD41630`) and in `0xACA060` (lot load from `Lot_FinishLoad` via `0xACBC90`, data read by `0xB536D0`) [verified stores at `0xAC9CD1`, `0xACA53E`].
- Partition grid writers: `0xC07000`, `0xC087A0`, `0xC08990`/`0xC08A80`, `0xC0AF80`, `0xC0BB60`, reached from the PartitionManager's load (`0xB536D0`), creation (`0xB523C0` from `0xAC79E0`), rebuild `0xB54B60` (called by `RoutingSystem_RebuildLotRouting 0x835110`: bulldoze, houseboat create/delete, lot save, `BuildRoutingData`; and by `Lot_FinishLoad` through `0xB552B0`) and the wall-graph edit chain `0xC44AA0`/`0xC44220` (from `0xB48860`, `0xB4F390`, `0xB55190`, `0xC5ABD0` and others) [verified callers; roles inferred from callers and tags].
- Nothing in the plan closures writes it [verified: no grid writer in D+V]. Rooms are static between build edits and lot events [inferred, I-101-10]; a snapshot copies the room partition per lot image (E13-E15 already rebuild the lot).

## 8. Remaining unknowns and the live check that closes each

| # | Unknown | Live check (all observe-only, existing probe or harness) |
|---|---|---|
| U1 | The global write set on the worker matches §2.2 (no global missed by static scanning, e.g. through register-computed addresses) | P2 L2 (docs/94 §4.1): Stalker memory-operand callouts on the route worker, sampled jobs; diff the written `.data` addresses against §2.2 **BP 2026-10-18, sim thread (docs/113 §7.2): one extra address, `0x114C900`, zero stored over zero, benign; worker and IP still open.** |
| U2 | Static inits #12-#26 are all set before the first worker job, and #14's cached value is the same for every lot | read the guard words and `0x11CF02C` after load and after 10 min; compare `0x11CF02C` with `+0xB8` of several lot graphs |
| U3 | `0x1158580` is used inside jobs at all (only through H13) | P2 L1 block coverage of `0x66AB70` on the worker **BP 2026-10-18 (docs/113 §7.3): `0x66AB70` never ran in 24 plans; #2 low priority.** |
| U4 | Interest point lists are always empty | counter on `0x821B3D` (allocation) and on non-zero returns of `IC_Route_GetNumInterestPoints` |
| U5 | Status codes stay in {1, 2, 3, 4, 10, 16, 18} | histogram of `mType` at the four plan ICs' return |
| U6 | Goal-step and search door asks per plan (same portals asked twice, or different sets) | `Portal_LockState` caller histogram by return address (`0x63F65E` vs `0x641026`, `0x63E708`, `0x632554`) with portal id and plan id |
| U7 | How often H9 rewrites the quadtree inside a plan | counter on `0x63257E`/`0x632595` per plan |
| U8 | Partition grid writes happen only on build edits and lot events, on the sim thread | counter and thread id on `0xC08C30` and `0xC094E0` in a build-edit repro and in 10 min of live play |
| U9 | The unlocked portal IC writes reach `0x635700` in live play (rate, thread) | P1 leaf counters bucketed by return address into `0x635920`/`0x635EE0`/`0x636130` |
| U10 | route `+0x1F4` has no hidden reader | one session with a hardware read watchpoint on a live route's `+0x1F4` (x32dbg), expect only the two IC writers **BP 2026-10-18 (docs/113 §7.1): not refuted; the two flagged sites are freed-and-reused route memory, §5 stands.** |

## 9. Symbols added (CSV, append only)

Appended to `tools/ghidra/symbols/ts3w-1.67.symbols.csv` with tag `<docs/101>`: `RoutingWorld_GetImpl 0x836200`, `QuadTreeCell_ClipScratch 0x66AB70`, `LotQuadTree_ctor 0x664650`, `LotQuadTree_dtor 0x666350`, `LotRouting_SyncPortalLockFootprints 0x632460`, `RouteDest_Point_Generate 0x759560`, `RouteDest_SlotArray_Generate 0x7596E0`, `RouteDest_RadialRange_Generate 0x75BE60`, `RouteDest_Lot_Generate 0x74FB00`, `RoutingPathImp_InterestPoints 0x81FCD0`, `RoutingPathImp_AppendFrom 0x821940`, `RoutingPathImp_CopyFrom 0x81FA20`, `Lot_LevelAtPoint 0xAB78B0`, `Route_RoomAtPoint 0x74BC60`, `PartitionManager_RoomAtPoint 0xB499D0`, `Partition_RoomIdAtTile 0xC05430`, `PartitionGrid_Cell 0xAA2160`, `PartitionManager_ctor 0xB50B10`, `LotRouting_FootprintRoomIds 0x635440`, `LotRouting_InvalidateRoom 0x630F70`, `Partition_RoomIdsOnLevel 0xC08510`, `FootprintComponent_LockKeySet 0x947700`, `LockKeySet_Add 0x948050`, `LockKeySet_Remove 0x9479D0`, `LockKeySet_Assign 0x947FA0`, `RoutingWorld_SetOceanPortalOwner 0x829520`. The existing row `Lot_RoomLookup 0xA8DF80` is wrong (floor height, §7); the CSV is append-only, so the correction lives here and in I-101-11.

## 10. Ledger

Rows `I-101-1` .. `I-101-11` in docs/51 §34.
