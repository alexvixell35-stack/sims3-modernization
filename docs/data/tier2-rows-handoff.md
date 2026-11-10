# Tier-2 IC rows: lane handoff (2026-11-08)

Branch `feature/tier2-ic-rows`. Design and inventory: `docs/126-autonomy-allow-list-tier2.md` §8 (built, refused, skipped), bodies §4.1. Ledger: docs/51 §457.

## What the scaffolding already holds (do not redo)

- **Native descriptors for all 15 rows** at the end of `kAsDesc` (`native/ts3native/autshadow.cpp`), gated by `[AutShadow] Tier2IcRows` (needs `Tier2Rows=1`; `AsNDescOn()`, `AS_NT2IC = 15`). A row in this design is only its descriptor: the cut fills it by calling the game's body on the sim thread (`AsRefresh`), the worker answers from the copy (`AsWorkerAnswer`). There is no per-row body to write.
- **Native selftest helpers** in `native/ts3native/autshadow-test.h`: `AsT2IcRow(i, name, owner, kind, nArg, sz, reason)` (descriptor + kind-table check), `AsT2IcCall(i, dummyBody, args)` (calls a dummy cdecl body the way `AsRefresh` calls the game's and masks the answer), `TestAutShadowTier2IcGate()`. Empty per-group functions `TestAutShadowTier2IcG2/G3/G4()` are already called from `TestAutShadow`.
- **Managed Mode R item `autofft2ic`** in `mods/ts3fixes/src/AutOffload.cs`: `IcRead` calls `IcG1..IcG4(GameObject o, long[] v)`; each group writes only its slots of `v`; the compare and counters 878-883 (`autoff_t2ic_items, _eq, _mis, _abort, _skip, _exc`) are shared and done. A mismatch logs `off-t2ic` with the first differing slot. Empty `IcG2/IcG3/IcG4` are in place. `sIcWorld` (the non-public `World.gWorld`, read once on the sim thread) is there for wrappers that only exist on `IWorld`.
- **Tool:** `tools/autonomy-tier2.ts` `TIER2_BUILT` (class `row`). Add your group's ICs once its selftest passes, then regenerate `docs/data/autonomy-tier2.csv` (command in docs/126 "Reproduce").
- **Queue + repro:** `tools/harness/queues/tier2-ic-rows.json`, repro `tier2-ic-rows-dry` (no change needed per lane).

## Slot map (`v` in `IcRead`)

| Slots | Group | Rows, in slot order |
|---|---|---|
| 0-5 | G1 terrain, floor (**done**) | TerrainHeight (float bits), TerrainType, HasFloorFeatures, HasFloorFeaturesWorldPos, IsCameraRoutable, IsOnPlatform |
| 6-8 | G2 pools | IsInPool, HasPool, GetPondAtPoint |
| 9-10 | G3 lot, room | GetRoomId, LotIsEntranceFromTargetPoint |
| 11-14 | G4 objects, session | GetBoundingBox (hash of 6 float bits), IsValid, LightGetDimmer (float bits), GetWorldFileName (string hash or length) |

Bools as 1 / 2 (0 = not asked), as G1 does.

## Rows per lane

Row index `i` = position in the 15 (`AsT2Ic(i)`). Addresses from docs/126 §4.1 [verified there].

### G2 pools (3 rows, `kAsDesc` rows 6-8)

| i | # | IC | Body | Stack dwords | Owner | Managed wrapper |
|---|---|---|---|---|---|---|
| 6 | 10 | `ScriptCore.World::World_IsInPoolImpl` | `0x788500` | Vector3 0-2 | `AO_WORLD` | `World.IsInPool(Vector3)` |
| 7 | 11 | `ScriptCore.World::World_HasPoolImpl` | `0x7858F0` | lot 0-1, LotLocation 2-4 | `AO_WORLD` | `World.HasPool(ulong, LotLocation)` |
| 8 | 13 | `ScriptCore.World::World_GetPondAtPoint` | `0x787190` | lot 0-1, Vector3 2-4 | `AO_WORLD` | `World.GetPondAtPoint(ulong, Vector3)` |

Race set: write-on-read only the lot-grid rebuild and `+0x116` (sim thread at the cut). Pools are build-mode data: a pool placed in live mode is a full rebuild case [inferred, I-457-1].

### G3 lot, room (2 rows, rows 9-10)

| i | # | IC | Body | Stack dwords | Owner | Managed wrapper |
|---|---|---|---|---|---|---|
| 9 | 17 | `ScriptCore.World::World_GetRoomIdImpl` | `0x785210` | lot 0-1, LotLocation 2-4, roomDef 5 | `AO_WORLD` | `World.GetRoomId(ulong, LotLocation, eRoomDefinition)` |
| 10 | 19 | `ScriptCore.World::World_LotIsEntranceFromTargetPointImpl` | `0x7884B0` | lot 0-1 | `AO_CUT` | `World.LotIsEntranceFromTargetPoint(ulong)` |

Race set: #19 calls `LotTable_Contains` (gate site 13) and, after a hit only, reads the entry; filled on the sim thread at the cut, where it passes `[RouteGate]` like any sim call (a possible wait behind a running job, I-126-5). Never on the worker. Planner flag `+0x974` is live state, hence `AO_CUT`.

### G4 object fields, session (4 rows, rows 11-14)

| i | # | IC | Body | Stack dwords | Owner | Managed wrapper |
|---|---|---|---|---|---|---|
| 11 | 23 | `ScriptCore.Objects::Objects_GetBoundingBox` | `0x744CB0` | result 0, id 1-2 | `AO_OBJ` | the object's bounding box getter (find the wrapper in the index) |
| 12 | 24 | `ScriptCore.Objects::Objects_IsValid` | `0x7449C0` | id 0-1 | `AO_CUT` | `Objects.IsValid(ObjectGuid)` |
| 13 | 25 | `ScriptCore.World::World_LightGetDimmerImpl` | `0x78A600` | id 0-1 | `AO_CUT` | `World.LightGetDimmer(ObjectGuid)` (light objects only) |
| 14 | 26 | `ScriptCore.World::World_GetWorldFileName` | `0x795450` | none | `AO_WORLD` | `World.GetWorldFileName()` |

Race set: #25 takes and drops a handle refcount (`LOCK` inc/dec, destructor at 0): sim thread at the cut only. #26 allocates its string through host `+0x50` at the fill (sim thread). #23 struct return: the dummy body takes the hidden result pointer as its first argument and returns it in `eax`; `AsT2IcCall` already passes `nArg + 1` dwords for struct rows. `Objects.IsValid` wrapper name not checked.

## Selftest pattern (per lane, native)

In your `TestAutShadowTier2IcGn()`: one `AsT2IcRow(...)` per row with the values of its descriptor, then a dummy `static ... __cdecl` body with the §4.1 stack layout and `AsT2IcCall(i, (void*)body, args)`, asserting the masked answer (bool high bytes masked, short to 2 bytes, float through `r.f`, struct through the result buffer). Never pass a game address (probe safety). Build: `native\ts3native\build.cmd <out-folder>` (VS x86, `/W4 /WX`, runs `selftest.exe`).

## Merge hot spots

| File | Shared part | Lane-owned part |
|---|---|---|
| `native/ts3native/autshadow-test.h` | helpers, gate test, runner calls | `TestAutShadowTier2IcGn` body only |
| `mods/ts3fixes/src/AutOffload.cs` | `IcRes`, compare, counters, `IcRead`, `IcWorld` | `IcGn` body only |
| `tools/autonomy-tier2.ts` | `TIER2_BUILT` set (one line per group; append, do not reorder) | |
| `docs/data/autonomy-tier2.csv` | generated: regenerate after the merge, never hand-merge | |
| `docs/126` §8 "Status by group" line, `PLAN.md` line | one line each | |
| `native/ts3native/autshadow.cpp` | descriptors are final; a lane should not need to edit it | |

G2, G3 and G4 edit disjoint functions in the same three files, so they can run in parallel; the only textual overlap is the `TIER2_BUILT` set and the regenerated CSV (merge lane regenerates). Builds: no native build while a timing job runs (`probe-out/queue/job-running`); the merge lane registers the version and the `tier2rows1108` pair once.

## Status log

- 2026-11-10 UTC, task 7 round 1, `fix/tier2-learn-taint` (contributor): dry pair `bp-t2ic-dry` / `ip-t2ic-dry` on the current master build (c2bb5ed, ts3native 0.36.239, asi tested + ts3fixes from the same tree; queue `tier2-ic-rows-t7a.json`). **First run froze BP** [verified, hang capture]: the sim thread spun in `World_HasFloorFeaturesWorldPosImpl` (0x785720 -> 0xD6E0A0 -> lot-grid walk) with x = 0x685DBD94 (~4.2e24). Cause: a struct-result IC (`Objects_GetPosition`) missed on the worker and aborted the object, the caller's Vector3 local stayed unwritten (Mono 1.2 does not zero locals), the next calls keyed on those stack words, the miss path learned them (`AsLearn`), and the cut re-asked the junk key on the sim thread. Fix: after an unanswered call that answers into caller memory (struct result or outs), the rest of that meta object learns no keys (`g_asTaint`, cleared with the abort); selftest covers taint, first-reason-wins and the scalar miss that still learns. **Rerun on the fix (rq3b4): no freeze, no crash, BP and IP 10 min each**, `autoff_t2ic_mis` 0, `_exc` 0, `_eq` 24 / 4, `unguarded` 0, `workerClassInit` 0, `workerExceptions` 0, `autshadow_exceptions` 0, no ScriptError; IP worker 717 cuts, 47,956 keys learned. Both jobs FAIL only on "Mode R void: 0 compared pairs": on the contributor's install every B13 pair is a guard skip (`autshadow_skips` == `autshadow_guardskips`, 108,094 BP). The core is not the cause [verified]: the docs/218 arm c rerun (rq3r, simler90's original UPDATE242 core in, the contributor's core and Tiny UI Fix out) has `accepted set 1 (by content)`, `autshadow_gm6_mismatch` 0, `autshadow_content_unknown` 0 and still skips every pair (112,142 BP, 93,654 IP; no freeze, `autoff_t2ic_mis` 0). The skips are G-M5: 11 script-CC types override closure virtuals (e.g. `GameObject::get_Cost`, `IGameObject::GetInUseAutonomyMultiplier`, `Posture::Satisfaction`), so `Lot`, `RabbitHole` and `GardeningMetaObject` are skipped whole. Mode R data (A1-A3, abort share per definition) needs an install without that CC, or a G-M5 that checks the receiver's exact type at the dispatch as the definitions already do. Abort share is high on this install (`autoff_t2ic_abort` 23,613 of 23,706 items BP); not judged, since the guard skips leave no compared pairs. E/E3/P6 jobs (`bp-t2e-dry`, `bp-t2e3-dry`, `ip-t2e-houseboat-dry`, `bp-t2p6-dry`) queued on the fix. No household path changed (dry compare only).
