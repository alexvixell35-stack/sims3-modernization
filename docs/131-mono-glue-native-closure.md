# 131. Mono glue native closure: what EA native code the second Mono thread reaches, and the race set

Written 2026-10-20. **Static only:** no game run, no queue, no build. Mono lane (memory "Mono static only"). Question: can the M5 worker (docs/30 §14, docs/120) running docs/122's `[AutShadow]` scoring race the sim thread through EA's native glue? Answer here: a native call closure up to 5 hops from every native entry the worker can run, with per-function globals, locks, thread affinity, heap and managed callbacks, and a race set.

Tool: `tools/mono-glue-closure.py` (capstone 5 over `game-copy/bin/TS3W.exe`, Steam 1.67, with `game-copy/analysis/functions.csv`; Python because capstone is, as `tools/snap-vcall-sites.py`). No Ghidra project and no REA session were needed: the questions are all instruction-level (operands, stores, call sites). Outputs: `docs/data/mono-glue-closure.csv` (one row per reached function), `docs/data/mono-glue-races.csv` (race set). Tags as in AGENTS.md; ledger rows in docs/51 §177.

## Summary

**Updated 2026-10-20 (v2, §9):** the tool now types virtual-call receivers from the caller side (docs/102 step 5), reads the last instruction of every function (inclusive `end`) and indexes 5,921 vtable-slot functions that `functions.csv` lacks. v2 numbers: **1,874 functions** from 142 roots, **1,546 indirect sites, 450 unresolved = 29.1 %** (v1 40.9 %), **group a 36.1 %** (v1 62.3 %; 25.1 % without the 104 `+0`/`+4` lifetime calls), b 23.6 %, c 11.6 %; hop-6 frontier 241; **race set 348, open 85** (v1 280 / 52; the 34 new open rows are lazy static first-fills and load-time singletons, §9.3). Two v1 claims are corrected: IC bodies **do** reach managed code (§9.2), and **three** tier-2 ICs, not one, reach the steering `+0x918` write path (§9.4). The bullets below are v1 unless marked.

- **Reached (v1): 1,766 functions** within 5 hops of 139 roots: (a) 68 EA internal-call bodies (docs/121 v0 + docs/126 tier-2 class-a rows) = 973 functions, (b) 50 EA functions the Mono range calls directly = 468, (c) 21 Mono runtime entry points of the worker path = 747 [verified: tool output].
- **Indirect calls: 1,464 sites**, 826 resolved (667 imports, 99 switch tables, 41 global function pointers, 39 from docs/102's site table, 5 by receiver vtable; §3), 39 partly (slot signature, at most 6 targets), **599 unresolved = 40.9 %** of all sites, **75 % of the non-import sites**. By group: a **62.3 %** unresolved, b 27.2 %, c 12.4 %. **Hop-6 frontier: 248 functions** (a 184, b 83, c 68). 45 of the 139 roots have no unresolved site within 5 hops (33 of b, 11 of a, 1 of c).
- **No UI, window, D3D device or COM call** is reached from any root through direct or resolved edges [verified for those edges]. Thread affinity in the closure is TLS only: Mono's own keys (domain, loader error, marshal, thread-static), the class-init owner thread id in `mono_runtime_class_init` `0xEA3370`, and EA's per-thread handle record `0x4E1270` (lazy TLS, reached from Boehm heap growth) [verified].
- ~~**No EA root calls back into managed code within 5 hops**~~ **Refuted in v2 (§9.2):** v1 could not resolve the script host vtable `0x108B690` (its slots point at code missing from `functions.csv`). With it, `IC_Slots_CanAttachToSlotImpl` (docs/121 class b, never on the worker) reaches `World_FireEvent` -> `ScriptHost_InvokeDelegate` `0xD75FA0` / `0xD7D2F0` at hop 5, and the host slots `+0x50`/`+0x88`-`+0x90`/`+0xC0`/`+0xC4` of 16 IC bodies reach Mono class init (`mono_array_new` -> `mono_class_vtable` -> `.cctor` via `0xE71450`, warm-up only) [verified: resolved slots, call sites]. The Mono-internal re-entries of v1 (`0xEB6020`, `0xE76C60`, `0xEAE270`, `0xE71450`, `0xE76A20`) stand.
- **The eglib heap is locked.** `g_malloc`/`g_free` (`0xD70A10`/`0xD70A60`) call `GeneralAllocator_Malloc`/`_Free` (`0x4E69D0`/`0x4E5920`), which take the `CRITICAL_SECTION` at allocator `+0x4E4` when it is non-null, and the allocator's first init `0x4E5710` always creates it (`InitializeCriticalSectionAndSpinCount`, spin 10) [verified bytes]. That the script heap `[0x11D4BC8]` (set once by host init `0xD77600`) went through that init is [inferred] (I-131-2). The frame arena (`0xD71900`/`0xD71A20`/`0xD71B70`) has no lock of its own, as docs/120 §9.1 says; P3 covers it.
- **Race set: 280 globals**, 152 written by the worker, 128 read by the worker and written by sim-side code, with no lock common to every access [verified: tool, function-level lock model]. **228 are already covered by the M5 design** (98 Boehm globals under P3, 123 Mono runtime globals under docs/120 §3.3 rules, 5 frame arena under P3 + ArenaFix, 2 P4 frame globals). **52 stay open**: 10 reached from b or c (run in any IC mode), 42 only from IC bodies (a). Read in the bytes, the open 52 fall into four kinds (§5): load-time singletons read by the worker (safe while the worker never spans a world load), lazy first-fill caches (pre-warm), Boehm-only EA hooks (P3 already), and **one real shared write, the temp string refcount `[0x11CB920]`** (docs/121 I-121-6), which a b root reaches too.
- **New hazard [verified, direct calls]** (v2: also `World_GetFloorSurfaceTypeImpl` and `World_GetInformationAboutRoomImpl`, §9.4): tier-2 IC `World_GetTerrainSurfaceTypeImpl` `0x7862A0` reaches `RoutingWorld_EnsureTerrainFootprints` `0x8341D0`, which, when a lot level's bit at `+0x168` is clear, calls `RoutingWorld_UpdateTerrainFootprints` `0x831710` under the steering lock `+0x918` and writes routing footprints. So I-126-1 ("the 26 tier-2 ICs write nothing on the call") is **refuted for this IC**; the docs/126 rule "copy, never call live" already keeps it off the worker, but it must never become pass-through.
- **Verdict:** b and c roots are clear except the items M5 already handles plus the image-open path (warm-up only) and the four lazy fills (pre-warm). **IC bodies cannot be cleared for pass-through statically** (62 % unresolved indirect, writes on read in three ICs, EA locks from seven): the docs/122 snapshot/memo design is required, not optional. No blocker for M5 as designed.

## 1. Roots

| Group | Source | Roots | Notes |
|---|---|---|---|
| a, IC bodies | `docs/data/autonomy-proxy-ics.csv` (docs/121 v0: 41 bodies with an address, incl. `DeviceConfig_GetProcessorLevel`) and the class-a/c `new_ics` of non-blocked rows of `docs/data/autonomy-tier2.csv` (docs/126), names mapped through `docs/data/ic-safety.csv` | 68 | `MonoScriptHost_GetEnumSimpleName` has no address in ic-safety.csv (Mono-side IC, not an EA body). Class-b/w/d ICs of blocked rows are left out: the worker aborts on them (docs/122 §3.3). Each body counts for both modes: **pass-through** (body on the worker) and **sim-thread memo** (body on the sim thread, worker reads the copy). The race set assumes pass-through; in memo mode the a-only rows leave the race set |
| b, Mono -> EA | every direct call from a function in the Mono range `0xE450C0`-`0xEC0000` to a function outside it | 50 | covers docs/118 §4.3 D1 (eglib heap thunks), D2 (abort/log), D4 (GC hooks), D5 (yield, frame arena, interface offsets, binder, trampolines), D6 (class registration `0xD790A0`), D7 (image open `0xD6FCB0`). Indirect hooks into EA (root-push hook `[0x11949CC]`) are reached through their Mono thunk |
| c, runtime | `RUNTIME_ROOTS` in the tool: worker entry `0xE5A600`, `0xE59FD0`, `0xE52B60`, `mono_runtime_invoke` `0xEA1090`, delegate invoke `0xEA1400`, class init `0xE6EE20`/`0xEA3370`, `mono_class_vtable`, `mono_lookup_internal_call`, bind helper `0xE520E0`, object/array/string alloc, `mono_value_box`, `mono_ldstr`, four exception constructors, throw path `0xE53740`, task state alloc/free | 21 | from docs/118 §4.2 and docs/120 §3 |

Root rate for ranking (column `root_rate`): **per alloc/call** (eglib heap thunks, frame arena, `Class_GetInterfaceOffset` `0xD70870`, managed allocs), **per invoke** (interpreter entries, task state, `ldstr`), **per search/exception** (IC bodies, exception constructors), **warm-up/rare** (class init, vtables, icall lookup, class registration, image open, GC heap growth) [inferred from docs/118-122].

## 2. Method

1. **Whole-exe index** (one pass, ~10 min, cached): for each of the 40,263 functions of `functions.csv`, a linear disassembly from entry to end with a register tracker (constant, value loaded from a global, object with a vtable store seen, vtable of an object, slot of a vtable, field of a global's object). Recorded: direct calls and tail jumps; indirect sites with the tracked operand; absolute data reads and writes (capstone operand access); fields of objects a global points to (`mov r,[G]` then `[r+off]`, as `[G]->+off`); lock calls (`Lock_Acquire` `0x4E16F0` with its `ecx`, `EnterCriticalSection`/`TryEnterCriticalSection`/`WaitForSingleObject` with the pushed argument); imported APIs; `fs:` reads; immediate stores of function pointers and vtables.
2. **Indirect resolution in docs/102 order**, as far as this tool goes: imports; switch tables (targets inside the caller); constants; global function pointers (every function address stored to the global anywhere plus its initial value); receiver vtables (vtable store in the frame, object returned by a known getter: `0x7692B0` -> `0x108B690`, `0xD7CFD0` -> `0x108B818`, docs/121 §1); docs/102's resolved sites (`routing-indirect-sites.csv`); slot-signature fallback over 1,554 vtables (at most 6 distinct targets = partial). Not done: docs/102 step 4 argument chains into callers and step 5 receiver typing from callers (§7).
3. **Closure:** BFS per root over direct and resolved edges to 5 hops; hop 6 = frontier.
4. **Locks per access:** a function's own lock calls cover its accesses (the enter/leave span is not modelled); inside the closure, locks every caller path holds are added (intersection over callers).
5. **Race set:** a writable global the worker closure touches, with (worker writes it and any function of the exe touches it) or (worker reads it and any function of the exe writes it), and no lock common to all those functions. The sim thread is assumed to run any function, including the worker's own ones. Globals never written by code (load-time data) are dropped.
6. **Mitigation column** by address rules from the M5 design: all worker accessors in Boehm (`0xE48E80`-`0xE51400`) = P3 GC lock; in the frame arena functions = P3 + ArenaFix; `[0x11F4170]`/`[0x11F416C]` = P4; cursor `[0x11EE5A4]` = F1; all in the Mono range otherwise = docs/120 §3.3 rules (warm-up, loader lock, no GCHandle/reflection).

## 3. Coverage

| Group | Roots | Functions (hop 0/1/2/3/4/5) | Indirect sites | Resolved | Partial | Unresolved | Frontier (hop 6) |
|---|---|---|---|---|---|---|---|
| a | 68 | 973 (68/182/160/192/186/185) | 761 | 249 | 38 | **474 (62.3 %)** | 184 |
| b | 50 | 468 (50/34/71/114/111/88) | 331 | 236 | 5 | 90 (27.2 %) | 83 |
| c | 21 | 747 (21/87/198/191/131/119) | 621 | 544 | 0 | 77 (12.4 %) | 68 |
| all | 139 | **1,766** | **1,464** | 826 | 39 | **599 (40.9 %)** | **248** |

By basis: import 667, slot signature with more than 6 targets 326, register of unknown origin 163, switch 99, slot signature with no match 72, global function pointer 41, docs/102 prior 39, slot signature (partial) 39, unknown receiver 13, receiver vtable 5. The unresolved sites are almost all virtual calls on objects passed in as arguments (game objects, the world, lots), which this tool cannot type without caller-side receiver typing.

Roots with the most unresolved sites within 5 hops: `UserToolUtils_ProductGenericImpl` 136, `OnlineFeaturesHost_GetPersonaName` 135, `World_GetFloorSurfaceTypeImpl` 98, `World_GetInformationAboutRoomImpl` 88, `interp_mono_invoke_with_state` 67 (mostly the IC dispatch, which docs/122's guard thunks bound), `World_LotIsEntranceFromTargetPointImpl` 57, `World_GetTilesWithFloorFeaturesImpl` 55, `GC_win32_get_mem` 51 (EA memory tracker, only with tracking mode on). With none: 33 of the 50 b roots; of a: `DeviceConfig_GetProcessorLevel`, `Objects_IsValid`, `Objects_GetBoundingBox`, `World_GetSunriseTime`, `World_GetSunsetTime`, `World_StaysAfterEvictImpl`, `World_GetSeaLevelImpl`, `World_GetMoonPhaseImpl`, `World_ObjectGetHiddenFlags`, `GameUtils_GetWorldName`, `GameUtils_GetWorldType`.

## 4. Facts per area

### 4.1 Locks reached

| Lock | Where (example) | Reached from | Tag |
|---|---|---|---|
| EA allocator CS `+0x4E4` | `GeneralAllocator_Malloc` `0x4E69D0`, `_Free` `0x4E5920`, `MallocCore` `0x4E5E40`, `Decommit` `0x4E4F90` | a, b, c (every `g_malloc`/`EA_AllocTagged`/`EA_Free`) | verified bytes |
| Mono CS `0x11F43A8` (loader), `0x11F41F8`, `0x11F43E4`, `0x11F4380`, `0x11F47B0`, `0x11F4174`, `0x11F4574`, `0x11F4788`, `0x11F4698`; Boehm `GC_write_cs` `0x1222580` | metadata/loader, class init, marshal, GC stop | b, c | verified call sites (docs/58, docs/120) |
| `Lock_Acquire` (member, dynamic) | `0x4E16F0` from `World_GetLotLocationImpl` (hop 2) | a | verified |
| lot index `member+0x228` | `WorldManager_RebuildLotIndex` `0xC68680` (rebuild on read, docs/121 §8.4) from `Slots_GetPositionOfSlotImpl` and `World_GetLotLocationImpl` | a | verified |
| steering `member+0x918` | `RoutingWorld_UpdateTerrainFootprints` `0x831710` from `World_GetTerrainSurfaceTypeImpl` (hop 5) | a (tier 2) | **new**, verified |
| CS `member+0xF0` (`0xAB5290`), Lock `member+0xA0` (`0xB96380`) | from `World_IsPositionOutsideImpl` | a | verified (as docs/123 I-123-16) |
| CS `member+0x9A8` | `0xD6DE30` from `World_HasFloorFeaturesWorldPosImpl` | a (tier 2) | new, verified call; owner object not named |
| EA Lock `0x11D3C98` | `0x72EC00` from `GameUtils_IsWorldInstalled` (hop 5) | a | verified |
| `member+0x58` | `ResourceCache_AdjustBytes` `0x731F60` from `UserToolUtils_ProductGenericImpl` | a | verified |

No lock is reached from the frame arena (3 hops) [verified]: the pool `[0x11EE508]` relies on P3.

### 4.2 Thread affinity

TLS and thread ids only, all in Mono except one: domain `0xE75260`, loader error `0xE848B0`/`0xE848C0`, marshal `0xE92150`, `0xE85550`/`0xE855C0`/`0xE85620`/`0xE862B0`, thread-static `0xEBAB30`, current thread `0xEB9F40`, interpreter context `0xE52B60`/`0xE59FD0`, `GetCurrentThreadId` in `mono_runtime_class_init` `0xEA3370` (class-init owner), `0xE68150`, `0xE68290`, `GC_thr_init` `0xE513D0`; EA's per-thread handle record `0x4E1270` (TLS index `[0x114C908]` filled once, then one record per thread through `DuplicateHandle`), reached from `GC_win32_get_mem` [verified]. None of these breaks on a second thread once docs/120's TLS setup runs; `0x4E1270`'s index fill is a first-use race only before any thread has called it (it has, at start-up) [inferred].

### 4.3 Heap

Allocators used across the closure (functions): `EA_Free` 133, `g_free` 94, `g_realloc` 54, `EA_AllocTagged` 52, `g_malloc0` 35, `g_malloc` 25, `mono_object_new` 20, `mono_string_new` 19, `GC_malloc` 15, `GeneralAllocator` direct 11, `GC_malloc_explicitly_typed` 6, `VirtualAlloc` 5, `GC_free` 5, `VirtualFree` 3, `GC_malloc_uncollectable` 3, frame arena 2, `mono_array_new` 2. EA heaps lock (§Summary); Boehm and the frame arena lock only under P3. No CRT `malloc` from the worker roots outside Boehm's `0xE4EC40` (docs/120: never runs on NT).

### 4.4 Managed callbacks

None from a or b roots within 5 hops. Mono-internal `mono_runtime_invoke` callers (c, and `0xEB6020` also from b via class registration -> class init) run `.cctor`s and exception constructors: the warm-up rule (docs/120 §3.3) covers the `.cctor` case; exception construction on the worker is already in docs/122's abort path.

## 5. Race set

`docs/data/mono-glue-races.csv`: 280 rows, sorted open first, then by score = root rate x (2 if the worker writes) x worker functions touching it.

| Mitigation | Rows |
|---|---|
| Boehm globals, M5 P3 GC lock | 98 |
| Mono runtime globals, docs/120 §3.3 rules | 123 |
| frame arena, P3 + ArenaFix | 5 |
| P4 per-thread frame globals | 2 |
| **open** | **52** (10 from b/c, 42 a only) |

Top 10 open, by score [writers read in the bytes, verified unless tagged]:

| # | Global | Kind | Rate | Worker side | Sim-side writers | Reading |
|---|---|---|---|---|---|---|
| 1 | `0x11CB864` | worker reads | per alloc/call | `0x931020` and 3 more (default heap pointer) | `0x4E4070` only | EA main heap pointer, set once at start-up: safe |
| 2 | `0x11EE528` | worker writes | per alloc/call | `0xD70810` | same | the flag `GC_init` sets to 1 (docs/118 D4): init-time only, safe |
| 3 | `0x11ECBC4` | worker reads | per search | 46 functions from `GameUtils_GetWorldName` on | `WorldManager_Init` `0xC671E0`, `WorldManager_LoadWorld` `0xC6CF80`, `0xC6B500` (shutdown) | world manager pointer: safe while the worker never spans a world load or shutdown (docs/122 cut rule) [inferred] |
| 4 | `0x11E9158` | worker reads | per search | 16 functions from `World_GetObjectTombRoomIdImpl` | `ComponentsService_RegisterAll` `0x933FB0` | service registration at start-up: safe |
| 5 | `0x11ED034` | worker reads | per search | 14 from `World_GetDoorRoomsImpl` | `0xC8E730`, `0xC8F7F0`, `0xC93000` (no direct callers: vtable or init) | load-time singleton [inferred] |
| 6 | `0x11EE514` | worker reads | per invoke | `ScriptHost_Sleep` `0xD70820` | `0xD71580`, `0xD7F2B0` (from `ScriptThreadService_Init`) | script host pointer, start-up only: safe |
| 7 | `0x11E9114` | worker reads | per search | 9 from `Slots_GetContainmentSlotNamesImpl` | `ComponentsService_RegisterAll` | start-up: safe |
| 8 | `0x114CA0C` | worker reads | per search | 6 from `EAText_GetMoneyString` (`0x4F5800`) | `0x4F98E0` (no direct callers) | locale table pointer [inferred load-time]; IC is a docs/121 a-stub, never called on the worker |
| 9 | `0x11E8FA8` | worker reads | per search | 3 from `World_ObjectGetHiddenFlags` | `ComponentsService_RegisterAll` | start-up: safe |
| 10 | `0x11AF670` | worker reads | per search | 2 from `Objects_GetForward` (`0x959060`) | `0xF7FBB0` (no direct callers) | CRT-range global [inferred load-time] |

The other open rows, grouped:

- **Lazy first-fill caches written by the worker** (a only): `0x931020` default-allocator singleton (`[0x11DC168]`-`[0x11DC184]`, 7 rows: fills a static block once, then only reads), `0x4D9AD0` (`[0x11C7984]`, caches that pointer), `0x9B0530` (`[0x11E96C0]`/`[0x11E96C1]`, value + done byte), `0xC88D80` (`[0x11ECEEC]`/`[0x11ECEED]`, value + done byte) [verified bytes]. Each writes only on its first call; the sim thread calls them long before any worker exists [inferred]. Add to the pre-warm list if any of them ever goes pass-through.
- **Correction 2026-11-08 (docs/51 §461, verified):** `[0x11CB920]` is not a refcount but the lazily cached `GetACP()` code page (filled 0 -> code page, same value from every thread, never cleared), so this row is a harmless lazy first fill, not a real shared write; the text below is the original reading.
- **Real shared write:** the temp string refcount `[0x11CB920]` (`0x4F3CC0`, `0x4F4300`, `0x4F4400`; 20 callers) [verified writers], docs/121 I-121-6. Reached from IC `DeviceConfig_GetOption` and from b root `0xD6FCB0` (image open, D7) at hop 2. The worker never opens an image after warm-up (docs/120 §3.3) and `DeviceConfig_GetOption` is memo-only, so neither runs on the worker as designed [inferred]; it is the one row that would race for real if either rule broke.
- **Boehm-only EA hooks** labelled open by the address rule: `[0x11EE520]` (heap-size counter in `GC_win32_get_mem` `0xD70900`, also read by `0xD70990`), `[0x11DC18C]` (tracking mode, docs/120 rule "worker only while 0"), `[0x114C908]` (EA thread record TLS index), `[0x11CC964]`: only Boehm calls `0xD70900`, so P3 covers them [inferred, I-131-4].
- **Load-time singletons read by IC code** (the remaining ~25 a-only rows: resource manager `[0x11C78D8]`, localized string service `[0x11CE2D0]`, object service `[0x11DC350]`, host world `[0x11D4BBC]`, event table `[0x11D6A78]` and others): one or two writers each, all init, registration or load paths by name or by having no direct caller [inferred per row; writers listed by `--races`].

**Singleton fields** (`[G]->+off`): the tracker records them, but none reached the open set; every one that the worker touches and the exe writes is inside Boehm or the Mono range [verified: tool output].

## 6. What this changes

| Item | Change |
|---|---|
| docs/122 §3.2 (worker answers) | unchanged; strengthened: IC bodies must stay snapshot/memo, never pass-through, until a typed closure clears them (62 % unresolved) |
| docs/126 I-126-1 | refuted for `World_GetTerrainSurfaceTypeImpl` (write path under `+0x918`); v2 §9.4: also `GetFloorSurfaceType` and `GetInformationAboutRoom`, plus the room-object writes of the latter |
| docs/122 pre-warm list | add the four lazy fills of §5 only if their ICs ever go pass-through (they do not in v0/tier 2) |
| docs/120 §3.3 rules | add: no image open on the worker (temp string refcount via `0xD6FCB0`) |
| M5 P3 | covers the frame arena and Boehm's EA hooks as designed; nothing new to detour |

## 7. Limits

- **Function-level lock model:** a lock call anywhere in a function covers all its accesses; an access before the `Enter` or after the `Leave` is counted as covered. The race set can miss races in such functions (false negatives) [inferred].
- **Linear register tracker:** no control flow; a value set on one branch is used on the other. Receiver typing from callers (docs/102 step 5) and argument chains (step 4) are not done, hence 75 % of non-import sites unresolved. (v2, §9: state is restored at forward branch targets after a `ret`/`jmp`, and receivers are typed from callers; esp after an indirect call assumes callee-pop unless an `add esp, N` follows, so an argument index can be wrong in rare cdecl frames [inferred].)
- **Index gaps:** only code inside `functions.csv` ranges is indexed. Example: the store of the script heap pointer at `0xD77607` lies in a vtable-only function Ghidra never made, so `[0x11D4BC8]` shows no code writer [verified by byte search]. Globals written only from such code are dropped from the race set as load-time data.
- **Sim side = whole exe:** every function that touches a global counts as sim-thread code, including load-time and debug paths; that inflates "worker reads, sim writes" rows, which is why §5 reads the writers by hand.
- **5 hops:** 248 frontier functions are not analysed; deeper writes (like the `+0x918` path at hop 5) can sit one hop further.

## 8. Next (wall-clock, Claude with agents)

1. ~~Caller-side receiver typing (docs/102 step 5) in the tool, then rerun~~ done in v2 (§9): group a 62.3 % -> 36.1 % (25.1 % without lifetime calls). Further: data flow through containers (lists, hash maps of game objects), ~3-5 h, or the live target probe of docs/102 §6 on the a closure.
2. ~~Depth-2 read of the 25 other tier-2 IC bodies for write-on-read~~ done (§9.4).
3. Mode R / G-A gates in game stay the real check (docs/122 §7); nothing here needs a game run first.

## 9. v2: caller-side receiver typing (2026-10-20)

Static only, same exe and `functions.csv`. Tool changes in `tools/mono-glue-closure.py` (`--self-check` has a new case: a constructed object typed through `new` + constructor, passed as `this` and as a stack argument, both dispatches resolved):

1. **Read bug:** `functions.csv` `end` is the inclusive last byte (docs/102 §1); v1 dropped every function's last instruction (often a tail `jmp`). Fixed.
2. **Index gap:** vtable slots that point at code with no `functions.csv` entry are added as functions (5,921, ending at the next known entry). This includes the script host vtable `0x108B690` (119 slots, 63 missing; `+0x134` = `ScriptHost_InvokeDelegate`), which v1 could not resolve at all. Vtables are now every read-only array of code pointers stored anywhere (any displacement, stack objects too): 1,643 bases.
3. **Caller-side typing (docs/102 step 5):** esp tracking with `ret N` of each callee from a pre-pass (callee-pop assumed for unknown callees, corrected by an `add esp, N` right after), stack arguments, register state at forward branch targets, ecx and the pushed arguments at every direct call, eax at every `ret`, objects on the stack, `this` passed to a constructor (`thisof`: the vtable the constructor leaves, else the type it had), field loads of typed objects and typed field stores. A receiver is typed by evaluating those states in the callers (for `this` and arguments), the callee (returns), the storers (globals, fields), plus every vtable holding the function (virtual `this`). All sources typed = resolved; some = partial; fields are always partial (stores we cannot see). `--no-typing` keeps 1 and 2 only.

### 9.1 Coverage, v1 -> v2

| Run | Functions | Sites | Unresolved (all) | a | b | c | Frontier |
|---|---|---|---|---|---|---|---|
| v1 (docs/131 §3) | 1,766 | 1,464 | 599 (40.9 %) | 474 / 761 (62.3 %) | 90 / 331 (27.2 %) | 77 / 621 (12.4 %) | 248 |
| v2 `--no-typing` (fixes 1-2) | 1,812 | 1,464 | 510 (34.8 %) | 404 / 867 (46.6 %) | 90 / 390 (23.1 %) | 73 / 642 (11.4 %) | 222 |
| **v2** | **1,874** | **1,546** | **450 (29.1 %)** | **341 / 945 (36.1 %)** | 93 / 394 (23.6 %) | 75 / 645 (11.6 %) | **241** |

v2 by basis: import 801, slot signature over 6 targets 265, switch 110, register 93, **receiver typed by callers 62 resolved + 21 partial**, global function pointer 52, function-pointer field 51, receiver typed in the frame 39, docs/102 prior 39, unknown receiver 13. The slot-signature "partial" class of v1 (39) is gone: with 1,643 vtables no signature fits 6 or fewer. Roots with no unresolved site: a 14 of 69, b 33 of 52, c 1 of 21. The a target of < 30 % is met only without lifetime calls: the 341 split into 104 `+0`/`+4` slot calls (Release/deleting destructor, docs/102 §3.3), 138 other slots, 85 `call reg`, 13 global pointers, 1 field. The rest are receivers passed in by callers that are untyped themselves (316 caller states: objects from containers, game objects, field-of-field loads); only data flow across containers or a live probe closes them.

### 9.2 Managed callbacks (corrects §4.4)

| From | Path (hops) | Runs on the worker? |
|---|---|---|
| `IC_Slots_CanAttachToSlotImpl` (v0 class b) | `0xB44D30` -> `0xCCDD80` -> `0x78F7C0` -> `World_FireEvent` -> slot `+0x134` `ScriptHost_InvokeDelegate` / `+0x138` `0xD7D2F0` (5) | no: class b aborts on the worker (docs/122 §3.3) |
| 16 IC bodies (text, persona, lot and world names, door rooms, the four `Query_*`, ponds, floor tiles, floor surface, world file name) through host slots `+0x50` (`0xD7BD40`), `+0x88`/`+0x8C`/`+0x90` (`0xD710A0`/`0xD710D0`/`0xD71100`), `+0xC0` (`0xD798F0`), `+0xC4` (`0xD74C00`) | `mono_array_new` / `mono_object_isinst` -> `mono_class_vtable` / `mono_class_init` -> `.cctor` (`0xE71450`, `0xEB6020`, `0xEAE270`) (4-5) | only on a class's first use: docs/120 §3.3 warm-up rule, already required |

`0xD7BD40` (host `+0x50`) is a native-to-managed value converter (type switch on the native value's kind) with **function-local static class caches** (`0x11EE54C`-`0x11EE584`, guard bits `0x11EE588` set by a plain `or`, filled through `ScriptHost_FindClass` under the Mono loader lock) [verified bytes]. A first use per value kind on two threads at once would race the guard; after warm-up they are read-only.

### 9.3 Race set, v1 -> v2

348 rows (v1 280): +70, -2. Mitigated: Boehm/P3 130 (+32), Mono rules 127 (+4), frame arena 5, P4 2. **Open 85** (v1 52): 11 from b/c (v1 10), 74 a only. The 34 new open rows, read in the bytes [verified]:

| Rows | What | Reading |
|---|---|---|
| 16 | `0x11EE54C`-`0x11EE588` | host `+0x50` static class caches (§9.2): lazy first fill |
| 10 | `0x11D7508`-`0x11D7518`, `0x11DA048`-`0x11DA058` | function-local static empty strings (`0x7935B0`, `0x8A79B0`: guard bit, then fill): lazy first fill |
| 2 | `0x123437C`, `0x1234380` | CRT `atexit` table (`0xF7E8FC`), written only when such a static is first built |
| 1 | `0x11C7E54` | the shared empty-string byte; `0x4F4F40` stores 0 over 0 (same-value write) |
| 1 | `0x11D4BC8` | script heap pointer, now seen because its writer `0xD77607` is indexed (fix 2): host init only (as I-131-2) |
| 4 | `0x11CF490`, `0x11D4BC0`, `0x11D9128`, `0x11ECF08` | singleton getters (`PhysicsWorld_Get`, `ScriptMessageServer_Get`, `RoutingWorld_GetImpl`, `IC_World_GetObjectTombRoomIdImpl`): load-time [inferred per row] |

So v2 adds no new kind of race: the open set is still load-time singletons, lazy first fills (pre-warm), Boehm-only hooks and the one real shared write `[0x11CB920]` (§5). The pre-warm list grows by the host converter caches and the two static strings, which any sim-thread IC call fills long before a worker exists [inferred, I-131-6].

**Update 2026-11-12 (races R1-R4, R2c):** the mitigation column of `docs/data/mono-glue-races.csv` now carries the docs/data/races-handoff.md text for all 85 open rows (`--remitigate`, the tool's `HANDOFF` table): 82 covered by code behind `[AutShadow] Races` (built, not run), 2 closed statically (`0x11C7E54`, `0x11CB920`), 1 rule (`0x11EE140` stays on the sim thread). A fresh index today drifts from this set (346 rows, two new open rows); see the handoff status log, R2c.

### 9.4 Tier-2 bodies: write on read (I-126-1 rest)

Depth-6 closure of the 26 docs/126 §4.1 bodies over direct and resolved edges, with markers for the routing and lot writers of docs/102 §2.1, every lock and every global store, plus a store scan of each body and its callees to depth 2 [verified unless marked]:

| Path | ICs (docs/126 §4.1 #) | Write | Lock / wait |
|---|---|---|---|
| `World_SurfaceTypeAtPoint 0xAB9580` -> `RoutingWorld_SurfaceAtPoint 0x834B80` -> `RoutingWorld_EnsureTerrainFootprints 0x8341D0` -> `RoutingWorld_UpdateTerrainFootprints 0x831710` (hop 5) | #2 `GetTerrainSurfaceType` and #4 `GetFloorSurfaceType` (through `0xC6C0C0`), **#18 `GetInformationAboutRoom`** (through `0xB01B50`) | routing footprints (`PathPlanner_AddFootprint`, `Footprint_SetLevelPolygons`), then the level bit: `or [entry+0x168]` at `0x831B61` | `Lock_Acquire(world+0x918)` at `0x831748`; only when the lot-table entry's bit for the level (`+0x168`, relative to `+0x40`) is clear (`0x834216`) |
| room object embedded iterator `0xC33810` / `0xC328B0` on the room `0xB487D0` returns | #18 (and v0 `World_IsPositionOutsideImpl`, through `0xB4DA60`) | cursor fields of the shared room object (`+0x3C`, `+0x3E`, `+0x54`-`+0x60`) **on every call**; when the dirty byte `+0x34` is set, a rebuild `0xC33550` (frees and refills the room's region lists) then clears it | none of its own; heap CS |
| `LotTable_GetIfPresent 0x62FB60` -> `LotTable_GetOrInsert` only after a `Contains` hit | #2, #4, #18 (hop 5), #19 (hop 2) | none (lookup) | gate sites 13 and 0 (docs/86), as docs/126 said |
| lot-grid rebuild `0xC68680`, terrain byte `+0x116` | as docs/126 §4.1 | as docs/126 | lot index `+0x228` |
| host `+0x50` static caches, static strings, `0x4F3CC0` string handle, `0xC88D80` option byte | #4, #26 (host); #4 (`0x11CB920`); #6, #7 (`0x11ECEEC`/`ED`) | first fill only | Mono loader lock on the first `ScriptHost_FindClass` |
| room read lock `0xB96380` (`+0xA0`), puddle owner CS `+0x9A8`, dimmer refcount | #18, #20; #6, #7; #25 | as docs/126 | as docs/126 |

No other body reaches a routing, lot-graph, terrain-chunk or imposter writer within 6 hops, and the other 20 bodies write only out-parameters, the hidden result and locals at depth 2. When the `+0x918` path runs, `0x6236D0` resets the bit word at `0x624070` (lot routing graph build [inferred]), so the bit is clear from a lot's (re)build until the first terrain or floor surface query per level.

### 9.5 Hop-6 frontier (241 functions)

| Class | EA | Mono | Glue | Reading |
|---|---|---|---|---|
| no lock, no global store, no thread API (plain or leaf) | 152 | 70 | 3 | nothing to add |
| global writers | 3 | 10 | | EA: `0x4F4300` (`[0x11CB920]`, the known real shared write), `0x7CE7A0` (static float, first fill), `0xB469C0` (**static scratch vector `0x1183748`-`0x1183778`, written on every call**); Mono/Boehm: P3 and docs/120 §3.3 already |
| lock takers | 1 | 1 | | allocator CS (`0x4E4440`); `mono_release_type_locks` |
| thread affinity | | 1 | | `GC_new_thread` (Boehm thread registration, docs/120) |
| managed | | | 1 | `0xD74100` -> `mono_runtime_invoke`, only behind `ScriptHost_InvokeDelegate` |

Every hop-6 EA hazard (`0xB469C0`, `0x7CE7A0`, `0xD74100`) is reached only from `IC_Slots_CanAttachToSlotImpl` (class b, sim thread). **Hop 6 changes no verdict.**

### 9.6 What v2 changes

| Item | Change |
|---|---|
| docs/126 §4.1, `autonomy-tier2.csv` | #2, #4, #18 are class `a-cut`: fill at the cut only when the level bit is set (§9.4); #18 also writes its room object on every call |
| cut budget (docs/122: p95 <= 0.3 ms, <= 2 ms/s) | a fill on a clear bit runs a terrain footprint update and can wait for a routing job holding `+0x918` (docs/81: whole plans, 55-66 ms/s of plan time); one such wait breaks the p95 [inferred] |
| docs/122 pre-warm list | add host `+0x50` static caches, the two static strings (only if their ICs go pass-through) |
| §4.4 managed callbacks | corrected (§9.2) |
| IC pass-through | still forbidden: 36 % unresolved in a, writes on read in at least five tier-2 ICs (#2, #4, #18, #25 and the first fills) |


**Live check 2026-10-25 (docs/133 §8, passive call sites, BP routing repros):** 185 of 541 armed glue sites fired; static 36.8% resolved -> 55.0% with live answers (57.9% some target known). I-131-12 pass (10 of 62 caller-typed sites fired, 0 targets outside the static set). New pairs only on `[switch]` rows (98, in-function case targets) and 8 receiver-typed rows with untyped caller sources. Ledger docs/51 §180 stays: verdict pass on this coverage.

## Ledger

docs/51 §177: I-131-1 .. I-131-5; §180: I-131-6 .. I-131-12 (v2).

## Reproduce

```sh
python tools/mono-glue-closure.py --self-check
python tools/mono-glue-closure.py --cache <scratch dir>          # index ~4 min first time (two passes, cache mono-glue-index-v2.pkl), then ~2 min
python tools/mono-glue-closure.py --cache <scratch dir> --no-typing --csv <tmp> --races <tmp>   # §9.1 middle row
# writes docs/data/mono-glue-closure.csv and docs/data/mono-glue-races.csv; --summary FILE for the coverage lines
```
