# 126. Autonomy allow-list tier 2: the rest of meta and the local phase

The next step after allow-list v0 (docs/122 §12.3: 12 meta definitions + 7 implementors). Takes the definitions after the pilot by sampled scoring share (docs/75, `docs/data/autonomy-entry-list.csv`), runs the `managed-callgraph.ts` closure for each, classifies every IC the pilot snapshot does not already answer by the docs/121 scheme (a snapshot or cache, a-stub, b sim thread, c safe, d unclear; plus **w** = the IC writes engine or sim state), and lists which definitions are ready with the existing snapshot, which need new snapshot rows and which are blocked. Static only: call-graph index and assemblies of the Steam 1.67 install, the decompiled class library for the hook sites. No game run, no build. Per-candidate table: `docs/data/autonomy-tier2.csv`; generator `tools/autonomy-tier2.ts`.

Tags: **[verified]** = tool output or read in the decompiled class library for this note; **[inferred]** = from names, the docs/62 scan or sample shares, not read to the end.

## Summary

| | Samples | Share of scoring |
|---|---|---|
| Pilot (allow-list v0 + the meta part of the shared base path) | 640 | 62.0% |
| **Tier 2 (everything else)** | **393** | **38.0%** |
| Tier 2 newly covered, ready with the 4 tier-2 hooks (§3), no new row | 164 attributed + ~111 shared base path | **~27%** [inferred] |
| Tier 2 newly covered, plus the new rows of §4 | 189 attributed + ~128 shared base path | **~31%** [inferred] |
| Left: blocked definitions, unresolved local buckets, unmapped base frames | ~76 | ~7% |

Of 1,033 scoring samples (docs/75 §2). "Attributed" = samples in a row whose types have a verdict (row 5's bucket split by its candidates' verdict counts); "shared base path" = the 142 non-meta samples of `CheckAvailability`, `CommonTests`, C2 self and `get_Parent`, which follow the local pairs and are split here in the same ratio as the attributed local time (0.78 ready, 0.90 with new rows) [inferred: pairs per type are still not measured, docs/75 §6].

**Candidates: 251** (37 meta, 31 sampled local, 169 candidates of the biggest unresolved bucket, 11 local base paths, 3 scoring functions) [verified: tool output]:

| Verdict | Count | Meaning |
|---|---|---|
| **ready** | **172** | no IC outside the pilot snapshot set, no new managed write (after the hooks and pre-warm list of §3) |
| **new rows** | **61** | needs 1-6 more class-a IC answers; 26 distinct ICs in all (§4) |
| **blocked** | **16** | sim-thread or writing IC, own-field write, or a real static write (§5) |
| hooked | 2 | the base-path row is itself a tier-2 hook site (§3) |

- **All 37 remaining meta definitions are ready** (every one of the 49 concrete `MetaInteractionDefinition` subclasses is now in v0 or tier 2), including `VisitCommunityLotWith+VisitWithDefinition` once docs/122's H6 exists. That closes rows 14, 37, 40, 52 and 66 of docs/75 and the meta parts of rows 17, 19, 20 and 31 (29 samples, 2.8%).
- No closure reaches a yield or an `EventTracker` send outside what the pilot already cuts [verified: tool output].
- **The local phase is not blocked by any single site.** Before the tier-2 hooks, 144 of the 169 bucket candidates were blocked, almost all through the generic base `Test` (§3, H7 and H8 reach 153 and 148 closures). After H7-H10 and the pre-warm list, 10 of 169 stay blocked.

## 1. Scope and method

1. **Candidates** [verified: tool output]:
   - *meta-rest*: `managed-callgraph.ts subtypes 'Sims3.Gameplay.Interactions.MetaInteractionDefinition`3'` (new subcommand) gives 49 concrete types; minus the 12 of v0 = 37.
   - *local-sampled*: every resolved or ambiguous non-meta definition row of the entry list (31 types).
   - *row5-candidate*: docs/75 row 5 (61 samples, 5.9%: a `Definition.Test` that calls the generic base `Test`, commodity search). Rebuilt from IL, not source text: concrete non-meta subclasses of `InteractionDefinition`3` named `Definition` whose own `Test` calls a generic base `Test` (`InteractionDefinition`3`, `SoloSimInteractionDefinition`1`, `ImmediateInteractionDefinition`3`, `ActorlessInteractionDefinition`3`, `InteractionDefinition`4`). **169** types; docs/75's text rule found 316 because it also counts names that only mention `base.`. The bucket's 61 samples cannot be split per type (docs/75 §6), so its verdict is a count.
   - *base-local*: the non-pilot base paths of the entry list (`CalculateScoreForObjectInteraction`, `InteractionDefinitionUtilities.SpecialCaseTests`, `UpdateCacheIfNeeded`, `LiveDragHelperModel.get_CachedTopDraggedObject`, `CheckAvailabilityTooltip`, `Motives.FunInteractionTest`, small getters).
   - *scoring-function*: the three sampled ones (`EatHereScoringFunction`, `SitInteractionModifier`, `GetBrushTeethScore`).
2. **Closure**: roots = `defRoots` (own or nearest `Test` + `CalculateScore`), depth 12, delegates not followed, virtual calls to the declared target only (as docs/118 and docs/121). Cuts: docs/121's two guards, the pilot's worker hooks (`IsAllowedInRoom`, `get_NumSimsDoingGardening`, `LocalizeString`, `DidSimsMeetOnline`, `GetSimCountForVenueWithCustomDesireFunction`, `GetPunishmentManagerFromSim`: docs/122 §3.5 and §12.5) and the tier-2 hooks H7-H10 (§3).
3. **Baseline**: the snapshot set is the 43 ICs of `docs/data/autonomy-proxy-ics.csv` (matched by method name, so `UTFConnector*` and `Sims3.UI` twins count). The pilot union (v0 + implementors + meta base path, same cuts) reaches 30 EA ICs, all in that set, and 6 managed write sites [verified: tool output]; a candidate's static write that the pilot union also reaches is not new.
4. **IC class** for an IC outside the set, from its docs/62 row: SAFE -> c; UNSAFE or MAIN -> b; a write verb in the name (`_Set`, `_Add`, `_Destroy`, `_Register`, ...) or a "queued" void IC whose name is not `Get`/`Is`/`Has`/`Find`/`Test` -> w; else a. Hand overrides [inferred, §4]: placement tests (`World_TestObjectPlacement*`, `UserToolUtils_CheckObjectPlacementImpl`), `World_FindPlacesOnRoad` and `Slots_GetMatrixOfSlotImpl` -> b; `World_GetObjectsImpl` -> d; `MonoScriptHost_GetEnumSimpleName` -> c (registered by the host itself, docs/119).
5. **Verdict**: blocked if the closure reaches a new b/w/d IC, a yield or event send, a new static write or static collection mutation not on the pre-warm list, or a write to a field of the definition's own type (the shared definition object); else new rows if it reaches a new a IC; else ready. Class-b ICs that the pilot already treats as abort branches (`Random_NextDoubleImpl` in 44 closures, `Slots_GetPositionOfSlotImpl` in 151, `Slots_CanAttachToSlotImpl` in 1) do not block: they abort the worker search as in docs/122 §3.2. Column `abort_branch_ics` lists them.

**Limit:** unresolved virtual calls (object-specific overrides) are not followed, so a closure can miss ICs. On the worker this costs an abort, not a wrong answer: the docs/122 guard thunk aborts `ic:<name>` on every IC not on the list. Mode R (docs/122 §4.3) counts them.

**Superseded by docs/127 (overrides followed, 2026-10-20):** with overrides up to 5 hops the verdicts of this note change for 177 of the 251 candidates (all candidates: ready 172 -> 18, new rows 61 -> 129, blocked 16 -> 102). The abort argument above does not hold for managed writes: a static write in an override body runs silently on the worker. All 37 meta-rest definitions are blocked by the rain/hail instance create (`InteractionInstance.sNextId`); with worker hook H11 (docs/127 §4) 28 are ready, 8 need new rows, 1 is blocked. In the local phase `World_IsOnRoofImpl` and `Query_GetGlobalObjects` become the most common new rows, and 31 `Test`s that build an actor route are blocked (route `Set*` ICs, I-127-2). Per candidate: `docs/data/autonomy-virtual-closure.csv`.

## 2. Results by group

| Group | Ready | New rows | Blocked | Hooked |
|---|---|---|---|---|
| meta-rest (37) | 37 | 0 | 0 | |
| local-sampled (31) | 21 | 4 | 6 | |
| row5-candidate (169) | 102 | 57 | 10 | |
| base-local (11) | 9 | 0 | 0 | 2 |
| scoring-function (3) | 3 | 0 | 0 | |
| **Total (251)** | **172** | **61** | **16** | **2** |

Before the tier-2 hooks (`--no-tier2-hooks`): 79 ready, 6 new rows, 166 blocked (meta-rest 30/0/7, row 5 22/3/144, local-sampled 15/3/13, base-local 9/0/2) [verified: tool output]. H7-H10 are what opens the local phase.

Sampled rows by verdict (docs/75 rank, samples): ready includes `CalculateScoreForObjectInteraction` (8, 30), `InteractionDefinitionUtilities.SpecialCaseTests` (11, 22), `EatHereScoringFunction` (16, 9; its `CanAttachToSlot` is an abort branch), `CheckAvailabilityTooltip` (20, 6), the phone `CallDefinition` (23, 4, needs the `RoommateManager` pre-warm), `SocialInteractionA` and `InviteToJoinConversation` (24, 3), the TV and video-game definitions (51, 53). New rows: `JamWithInstrument` (28, `IsOnPlatform`), `SwimHere` (38, pool ICs), `FrugalTurnOff` (49, light dimmer), `LookOutWindow` (55, bounding box). Blocked: `CallPet` (13, 14 samples: writes `mSimPickerList` on the shared definition, as docs/75 §3 found), the scratch/freak-out group (47, placement test), `ChangeDoorbellSound` (55, `SetLotFrontDoor` through a lazy front-door lookup), `ViewObjects` (58, creates the drag UI model).

## 3. Tier-2 worker hooks and pre-warm list

Four managed hooks (ts3fixes, same owner and pattern as docs/122 §3.5; worker path only, the sim thread runs the original):

| Hook | Site | Why [verified in the decompiled class library] | Worker answer | Opens |
|---|---|---|---|---|
| **H7** | `LiveDragHelperModel.get_CachedTopDraggedObject` and `get_TopDraggedObject` | the generic base `Test` (`InteractionDefinition`3::Test`) walks the target's parent chain and compares each with the cached top dragged object. The getter fills a static cache on first use per search (the search clears it at its end); the fill builds the UI model singleton and asks the scene window, which reaches 7 `UIManager` ICs (`DestroyWindow`, `RegisterEvent` among them) and 6 static UI collections | the object captured at the cut (null when nothing is dragged) | 153 closures |
| **H8** | `Autonomy.UpdateCacheIfNeeded` | during a search the actor's cached test values are already filled (`mUseCachedValues`); otherwise it recomputes them (fire on lot, tomb room, party, household members), which reaches `World_GetWorldPositionImpl`, `CheckObjectPlacement` and fire-manager reads | cache active -> no-op; else abort `cache` | 148 closures |
| **H9** | `InteractionObjectPair.AddTuning` | a `Test` that builds a new pair (cloned parameters, playground and baby-pickup checks) fills two static `Dictionary` caches keyed by definition type and target type on a miss: a concurrent add with the sim thread can corrupt them | read both caches; miss -> abort `tuning` | 17 closures |
| **H10** | `Autonomy.get_IsSufficientlyOnScreenForHighLODSimulation` | every 60 ticks it recomputes the LOD flag and, on a change, sets a posture state-machine parameter (`SACS_SetParamImpl`, `SACS_UpdateEventFilterImpl`); reached from `Sim.IsSimInSituationWhichRulesOutMetaAutonomy` | the stored flag `mbHighLOD`, no recompute (at most 60 ticks stale; Mode R shows whether it matters) | 6 closures, all meta (`GoToLot`, `TakeFoodToLot`, `StayHome`, the three protest definitions) |

**Pre-warm** (one sim-thread call per load, then reads; added to docs/122 §3.5's list): `Household.RoommateManager` (lazy singleton, 8 closures, via `Sim.FamilyFunds` and the phone checks), `ScoringFunctionsForBabiesAndToddlers.sMaxHunger` (docs/121 §4.4), `AlchemyRecipe.mCachedCuriousDewRecipe`, and mscorlib lazies (`Encoding` UTF-8 instance, `MonoEnumInfo` cache, the CJK collation tables) that docs/122 §12.2 already accepts as idempotent first fills [verified for the game-side fields; inferred for the mscorlib ones]. CSV column `prewarm`.

## 4. New snapshot rows

26 distinct class-a ICs, all reached only by definitions with no blocker (column `new_ics`). Answer rule as docs/121 §2.2: a position-keyed IC is answered only for a position bit-equal to a row's position, else abort `pos`.

| Group | ICs (closures reaching) | Row content | Size | Effort |
|---|---|---|---|---|
| Terrain and floor at a row position | `GetTerrainHeight` (15), `GetTerrainSurfaceType` (6), `GetTerrainType` (3), `GetFloorSurfaceType` (2), `HasFloorFeatures` (5), `HasFloorFeaturesWorldPos` (1), `GetTilesWithFloorFeatures` (1), `IsCameraRoutable` (10), `IsOnPlatform` (2) | per row: height, terrain type, surface types, floor-feature and camera-routable bits | +12 B per row | 1.5-2.5 h |
| Pools and ponds | `IsInPool` (16), `HasPool` (12), `GetPoolObjectIdForPoolBelowPosition` (2), `GetPondAtPoint` (3), `GetPondInfo` (3), `FindPondRepresentativePositions` (1) | per row: in-pool bit, pool id; per lot: has-pool; per world: pond table (docs/100 already maps ponds) | +8 B per row, ~0.5 KB per world | 1-2 h |
| Lot and room | `GetLotLocationAtFloor` (14), `GetRoomId` (1), `GetInformationAboutRoom` (1), `LotIsEntranceFromTargetPoint` (1), `LotGetDisplayLevelInfo` (1), `GetWorldToHouseboatDisplayPosition` (1) | lot location per (row, floor); room id and room info for the actor's lot; houseboat display transform per lot | ~1-3 KB per local search | 1-2 h |
| Object fields | `Objects_GetForward` (1), `Objects_GetBoundingBox` (1), `Objects_IsValid` (1), `World_LightGetDimmerImpl` (1) | per row: forward vector, bounding box, dimmer; valid = row present | +40 B per row | 0.5-1 h |
| Session | `World_GetWorldFileName` (1) | memo | < 0.1 KB | 0.25 h |

The table above was the first pass from the docs/62 scan. §4.1 reads every body (I-126-1, closed statically) and replaces its sizes: **all 26 are snapshot rows filled on the sim thread at the cut; none is blocked, none needs a memo.** The four groups touch the same state families as docs/121 §2.2 (WorldManager, lot grid, terrain, object components), so the docs/121 §8.4 rule holds: copy, never call live.

**Snapshot for local searches.** v0 builds rows for meta searches (docs/121 §3: ~100-250 rows). Local scoring targets every object on the actor's lot plus the Sims there: docs/59 §2.5 gives 30-100 KB on a busy home lot. With the §4.1 fields, **~155-170 B per row** instead of 64-80 (v0 fields + ~91 B), plus per-lot and per-world tables (§4.1 sizes).

### 4.1 Native bodies of the 26 ICs (2026-10-20, I-126-1)

Static only, as docs/122 §14.8: Ghidra headless on a copy of the `ghidra-ac121` project (`DecompileTo.java` depth 0 and 2, `DecompStores.java` depth 4 with the rebuild `0xC68680` and the lock helper `0x4E16F0` not expanded), plus a capstone pass over `game-copy/bin/TS3W.exe` with `functions.csv` boundaries: direct-call closure to depth 4, marking the lot-grid rebuild `0xC68680` (behind the reader `0xC688A0`), the terrain re-entrancy byte `+0x116` (`0xA8DF80`, docs/121 §8.4), the steering `+0x918` acquirers `0x82EDB0`/`0x82F0B0` and any `+0x918` operand, the routing lot-table gate sites `LotTable_Contains 0x628A90` and `LotTable_GetOrInsert 0x62EAB0`, the room read lock `0x106FB00` (`0xB96380`, `0xB95DF0`), `EnterCriticalSection`, `LOCK` prefixes, absolute global stores and the raise markers of §14.8 (`mono_raise_exception 0xEA18F0`, the `0xE77000` block, `RaiseException`, `_CxxThrowException`, `longjmp`, `abort`, `exit`). Managed signatures from the decompiled `ScriptCore.World` / `Objects` externs and the `Sims3.SimIFace` structs. Every body is cdecl (`ret` with no operand), so the stack dwords below are the managed arguments in order, after the hidden result pointer when the return is a struct over 8 bytes. `WM` = the WorldManager global `[0x11ECBC4]`; "lot" = the lot found by id through `0xC62D40` (`0xC60D30` plus the vtable `+0x40` type check) or by position through the grid (`0xC68990` -> `0xC688A0`). [verified unless marked]

**Correction (2026-10-20, docs/131 §9.4, depth 6 with indirect calls resolved):** the "no `+0x918`" sentence below is wrong for three ICs. #2 `GetTerrainSurfaceType`, #4 `GetFloorSurfaceType` and #18 `GetInformationAboutRoom` reach, at hop 5, `World_SurfaceTypeAtPoint 0xAB9580` -> `RoutingWorld_SurfaceAtPoint 0x834B80` -> `RoutingWorld_EnsureTerrainFootprints 0x8341D0` -> `RoutingWorld_UpdateTerrainFootprints 0x831710`, which takes the steering lock (`Lock_Acquire(world+0x918)` at `0x831748`), adds the level's terrain footprints and sets the level bit (`or [entry+0x168]` at `0x831B61`); it runs only when that bit is clear (`0x834216`) [verified]. And #18 writes its room object on every call: the room's embedded iterator (`0xC33810`/`0xC328B0`, cursor fields `+0x3C`, `+0x3E`, `+0x54`-`+0x60`) and, when the dirty byte `+0x34` is set, a rebuild of the room's region lists (`0xC33550`) [verified]. The other 23 bodies add nothing to the list below: depth 2 writes only out-parameters, results and locals. CSV: #2, #4, #18 are class `a-cut` (`tools/autonomy-tier2.ts`, `TIER2_CUT`). Rules that follow:

- **Filled on the sim thread at the cut, never on a worker, even with a lock:** all 26 (copy-never-call), and strictly #2, #4, #18 (`+0x918` footprint write, room object writes), #19 (gate sites), #25 (refcount), and the lot-grid rebuild / `+0x116` group.
- **Cut budget:** a fill that meets a clear bit runs a footprint update and **waits on `+0x918` whenever a routing job holds it** (docs/81: a job holds it for a whole plan; 55-66 ms/s of plan time) - one such wait is milliseconds, far over the docs/122 p95 of 0.3 ms [inferred from docs/81 timings]. The bit is clear from a lot's routing-graph (re)build (`0x6236D0` resets it at `0x624070` [inferred: lot graph build]) until the first terrain or floor surface query per level. Shim rule for `a-cut` rows: at the cut, look up the lot-table entry the way `0x8341D0` does (`LotTable_GetIfPresent`, then the level bit) and fill the row only when the bit is set; otherwise leave the row stale (the worker aborts on it, reason `cut`, and the sim thread's own call builds the footprints later). Cost of the check: one gate-site lookup (I-126-5). Mode R counts how often a row is skipped (I-131-8).

**Writes and locks, all 26 (original reading, depth 4, direct calls only).** No body or callee to depth 4 reaches the steering `+0x918` lock or any `+0x918` field. No global is written except two idempotent first fills: the config byte pair `0x11ECEEC`/`0x11ECEED` (`0xC88D80`, an option read cached once; the two floor-feature ICs) and a lazily fetched handle `0x11CB920` in the string code (`0x4F3CC0`, `GetFloorSurfaceType`). Both are set by any first caller and never cleared, so the pre-warm rule of §3 covers them. The other stores `DecompStores` keeps go to out-parameters, the hidden result, caller locals or fresh native buffers (`0xAEF2B0`'s store at `0xAEF38A` is into the local list it just built). Write-on-read paths, all already known from docs/121 §8.4: the lot-grid rebuild (11 ICs), the terrain `+0x116` byte (13 ICs) and the handle refcount (`LightGetDimmer` only). New against the v0 set: **four ICs reach the routing lot table** through `0xAB9580 -> 0x834B80` or directly (`0x6319A0`): `GetTerrainSurfaceType`, `GetFloorSurfaceType`, `GetInformationAboutRoom` and `LotIsEntranceFromTargetPoint` call `LotTable_Contains` (gate site 13) and `LotTable_GetOrInsert` (gate site 0, docs/86); `LotIsEntrance` asks `GetOrInsert` only after a `Contains` hit, so it never inserts [verified in `0x6319A0`; the other three paths not read to the end]. On the sim thread at the cut these are the game's own calls and pass `[RouteGate]` like any sim call (a possible wait for a running job, docs/86); on a worker they would race the routing job, so they must never be called live. Locks taken on the way: the room read lock `0x106FB00` (`GetInformationAboutRoom`, `LotGetDisplayLevelInfo`), a critical section at `+0x9A8` of the rain-puddle owner (`0xD6DE30`; the two floor-feature ICs) and the allocator's own critical sections. **No raise** on any path except the managed allocator behind the host slots, as §14.8: `+0x50` (string, `0xD7BD40`), `+0x88` and `+0x90` (array new, `0xD710A0` / `0xD71100`) reach `mono_array_new_specific` (overflow / out of memory) with native container counts as lengths, never an argument; `+0xA0` / `+0xA8` (`0xD71170` / `0xD71200`, element copies) are leaves. Indirect calls are not followed (between 0 and 52 per closure; the most in the string and floor-material ICs).

| # | IC | Body | Stack dwords | Return | Not-found / bad input | Reads (beyond WM and the lot lookup) | Write-on-read, locks | Verdict and row |
|---|---|---|---|---|---|---|---|---|
| 1 | `World_GetTerrainHeightImpl` | `0x786200` | x 0, z 1, considerBridges 2 | float (`st0`) | no WM -> 0.0 | terrain height `0xC6A250` (bridges flag passed through) | rebuild, `+0x116` | row: float per (row x/z, bridges), 4 B; learned key on the flag |
| 2 | `World_GetTerrainSurfaceTypeImpl` | `0x7862A0` | x 0, z 1, level 2 | int | no WM -> 0 | `0xC6C0C0` (x, z, level, mask `0xFFFF`): floor material, terrain layers | rebuild, `+0x116`, **gates 13 and 0**, **`+0x918` footprint update when the level bit is clear (`a-cut`)** | row: int per (row, level), 4 B |
| 3 | `World_GetTerrainTypeImpl` | `0x789890` | x 0, z 1, level 2 | int, one flag of `1, 2, 4, 8, 0x10, 0x20, 0x40, 0x80` | no WM, no lot and no road -> 1 | level 0: height vs sea level (`WM+0x60` `+0x14`) -> 2, water test `0xC60E70` -> 8; lot: pool tests `0xAD1C00`/`0xAD1B90` -> 0x40, `0xAB19F0` -> 0x80, lot `+0x290` test -> 0x20, else 0x10; off lot: road `0xB87D30` -> 4 | rebuild, `+0x116` | row: 1 B per (row, level) |
| 4 | `World_GetFloorSurfaceTypeImpl` | `0x793A30` | lot 0-1, Vector3 2-4 | string | no WM, no lot or position not on the lot -> null | floor tile material (`0xADD740`), catalog name, string table | rebuild, `+0x116`, **gates 13 and 0**, **`+0x918` footprint update when the level bit is clear (`a-cut`)**, lazy `0x11CB920`; host `+0x50` (allocator, static class caches on first use) | row: string reference per (row, lot), 4 B; strings are immutable, so the reference can be returned as is |
| 5 | `World_HasFloorFeaturesImpl` | `0x7856C0` | lot 0-1, LotLocation 2-4 (the low bytes of x, z and level are used), flags 5, mask 6 | bool | no lot -> 0 | tile feature word `0xAB7D00` | none | row: 1 bit per (row tile, flags, mask); learned keys, as v0 rows 15-17 |
| 6 | `World_HasFloorFeaturesWorldPosImpl` | `0x785720` | Vector3 0-2, flags 3, mask 4 | bool | **flags other than 4 (`RainPuddle`) -> 0 at once; mask is never read**; no puddle owner (`WM+0x74`) -> 0 | lot by position `0xC68B00`, room id `0xB499D0`, puddle test `0xD6DEF0` | rebuild, `+0x116`, critical section `+0x9A8`, lazy `0x11ECEEC`/`ED` | row: 1 bit per row (rain puddles change with weather: refreshed at every cut) |
| 7 | `World_GetTilesWithFloorFeaturesImpl` | `0x796260` | lot 0-1, roomDef 2, flags 3, mask 4 | ulong[] | no WM, no lot, no room data (`+0x298`) or no tile -> null | every tile of every level of the lot (`+0x174`/`+0x178`/`+0x17C`/`+0x180`), room `0xB487F0`; flag bit 2 adds the puddle test | as #6; host `+0x88` / `+0xA0` | per lot and learned (roomDef, flags, mask): 8 B per matching tile (tens to a few hundred B on a normal lot) |
| 8 | `World_IsCameraRoutableImpl` | `0x7852B0` | x 0, z 1 | bool | no WM -> 0 | `0xC6BBC0` (lot by position) | rebuild | row: 1 bit |
| 9 | `World_IsOnPlatformImpl` | `0x78E450` | Vector3 0-2 | bool | no lot, or position outside the lot's tile bounds -> 0 | world-to-lot `0xAB88A0`, level `0xAB78B0`, platform test `0xB066E0` | rebuild, `+0x116` | row: 1 bit |
| 10 | `World_IsInPoolImpl` | `0x788500` | Vector3 0-2 | bool | no lot -> 0 | `0xB01390` (lot, position, 0, 1) | rebuild, `+0x116` | row: 1 bit |
| 11 | `World_HasPoolImpl` | `0x7858F0` | lot 0-1, LotLocation 2-4 (x, z as int, level as sbyte) | bool | no lot -> 0 | pool tile tests `0xAD1C00`, `0xAD1B90` | none | row: 1 bit per (lot, row tile) |
| 12 | `World_GetPoolObjectIdForPoolBelowPositionImpl` | `0x788580` | Vector3 0-2 | ulong `edx:eax` | no lot or no pool data (`+0x2BC`) -> 0 | `0xB6A340` | rebuild, `+0x116` | row: 8 B |
| 13 | `World_GetPondAtPoint` | `0x787190` | lot 0-1, Vector3 2-4 | uint | lot 0: world pond index + 1 (`0xC60EE0`), none -> 0; lot: no pond data (`+0x2C8`) -> 0 | world pond table; lot ponds `0xB64E40` | rebuild, `+0x116` (world path, through `0xB5FC40`) | row: 4 B per (row, lot) |
| 14 | `World_GetPondInfo` | `0x78BC70` | pondId 0, lot 1-2, ref PondInfo 3 | bool | 0, **out untouched** (pond 0, pond id over the count, no record, no lot pond data) | world: record `0xC60E40(id-1)` fields `+0x218..+0x228`; lot: `0xB64E00` | none | per world pond (and lot pond): the 124 B `PondInfo` after-image plus a written mask. The world path writes only `mBoundCorner0..3` (`+0xC..+0x3B`), `mfWaterHeight` (`+0x6C`) and `mID` (`+0x70`); the shim writes the same fields and leaves the rest of the caller's struct as it was. Lot path's field set not read [inferred: same rule, mask taken from a recorded diff] |
| 15 | `World_FindPondRepresentativePositions` | `0x792630` | lot 0-1, ref float[] 2 | bool | lot 0, no lot or no vertices -> 0, **ref untouched** | lot pond vertices `0xAB5FA0` | host `+0x90` / `+0xA8` (allocator only) | per lot: the float payload (4 B per value), written only on true. The decompiler loses the copy's source registers; the count is the native vector's [inferred] |
| 16 | `World_GetLotLocationAtFloorImpl` | `0x7850F0` | Vector3 0-2, ref LotLocation 3, roomDef 4 | ulong lot id (lot `+0x48`/`+0x4C`) | no WM or no lot -> 0; out x, z from the sentinel start value, level 0; **room short `+0xA` written only when the lot has room data (`+0x298`)**, else the caller's in-value stays | world-to-lot `0xADD860`, room id `0xB499D0` | rebuild, `+0x116` | row: per (row, roomDef) 8 B id + 12 B out + a room-written bit = ~21 B. Differs from v0 row 13 (`World_GetLotLocationImpl`, out always filled, §14.8) |
| 17 | `World_GetRoomIdImpl` | `0x785210` | lot 0-1, LotLocation 2-4, roomDef 5 | short | no lot or no room data -> `0xFFFF` | `0xB499D0` | none | row: 2 B per (row tile, roomDef) |
| 18 | `World_GetInformationAboutRoomImpl` | `0x7933A0` | result ptr 0 (RoomInfo, 24 B), lot 1-2, roomID 3, roomDef 4 | RoomInfo | no WM or no lot: dword 0 = 0, **dwords 1-5 = uninitialised stack** | room summary `0xB01B50` (region `0xB4DA60`, floor tiles through `0xAB9580`, `0xAB8070`) | `+0x116`, **gates 13 and 0**, room read lock `0x106FB00`, **`+0x918` footprint update when the level bit is clear (`a-cut`)**, **room object iterator and dirty rebuild on every call** | per (lot, room, roomDef): 24 B (~0.25-0.75 KB per lot). The no-lot answer cannot be bit-equal (the game's own is undefined); the shim returns zeros there and Mode R must not count dwords 1-5 of a no-lot answer |
| 19 | `World_LotIsEntranceFromTargetPointImpl` | `0x7884B0` | lot 0-1 | bool | no routing world (`0x836200`, the docs/68 T4 site), planner flag `+0x974` clear, no WM or no lot -> 0 | planner `[world+0x95C]`, `LotTable_Contains`, then the entry's `+0x20 == 1` | **gates 13 and 0** (no insert) | per lot: 1 B |
| 20 | `World_LotGetDisplayLevelInfoImpl` | `0x7869F0` | result ptr 0 (24 B), lot 1-2 | LotDisplayLevelInfo | dwords 0-3 zeroed first; no lot -> **dwords 4-5 untouched** (the caller's temporary) | lot `+0x39C`, `+0x17C`, `+0x180`, `+0x184`, `+0x3A0`, `+0x2AC`; level test `0xB95DF0` | room read lock `0x106FB00` | per lot: 24 B; as #18, a no-lot answer is compared on dwords 0-3 only |
| 21 | `World_GetWorldToHouseboatDisplayPositionImpl` | `0x788920` | lot 0-1, Vector3 2-4, out Vector3 5 | bool | no lot or not a houseboat (`+0x444` byte) -> 0, **out untouched** | lot transform `0xAB33F0` | none | per (row, houseboat lot): 12 B + bit; non-houseboat lots need only the per-lot flag |
| 22 | `Objects_GetForward` | `0x744FD0` | result ptr 0, id 1-2 | Vector3 | hash miss or no transform component -> **(0, 0, 1)**, not zero | object service `[0x11DC350]` (`0x938B50`), hash `0x939560`, component `0x935770` type `[0x11E9158]`, transform `0x959060` | none (no refcount) | row: 12 B |
| 23 | `Objects_GetBoundingBox` | `0x744CB0` | result ptr 0, id 1-2 | BoundingBox (24 B) | zeroed | component type `[0x11E8FC4]`, bounds `0x949DB0` | none | row: 24 B |
| 24 | `Objects_IsValid` | `0x7449C0` | id 0-1 | bool | hash miss -> 0 | object hash only | none | row present -> 1; a row whose id missed at the cut -> 0; an id with no row aborts `row` (the snapshot does not hold the whole id set) |
| 25 | `World_LightGetDimmerImpl` | `0x78A600` | id 0-1 | float | miss or no light component (`[0x11E8FC0]`) -> 0.0 | dimmer `0x9492F0` | handle refcount (`LOCK` inc/dec, destructor at 0, docs/121 §8.4) | row: 4 B (light objects only) |
| 26 | `World_GetWorldFileName` | `0x795450` | none | string | no WM or no host -> null | world path `0xC60CB0`, wide-string conversion | host `+0x50` (allocator only) | world row, once per load, < 0.1 KB (as v0 row 20 `World_GetWorldNameKey`; a memo would miss, since the sim thread rarely asks) |

**Verdicts: 26 snapshot rows, 0 memo, 0 blocked** (one per load: #26; per world: #13 world path, #14; per lot: #7, #15, #18, #19, #20, #21's flag; the rest per row). CSV: the 26 now carry class `a-row` in `new_ics` (`tools/autonomy-tier2.ts`, `TIER2_ROW`); candidate verdicts are unchanged (172 / 61 / 16 / 2).

**Sizes.** Per row: terrain and floor ~15 B (#1-#6, #8, #9 with the bits packed), pools ~13 B (#10-#13), lot and room ~23 B (#16, #17; +13 B on houseboat lots), object fields ~40 B (#22-#25): **~91 B per row**. Per lot: ~0.3-1 KB (#7, #18, #20 and the flags). Per world: the pond table at 124 B per pond, ~1-6 KB [inferred: 10-50 world ponds; the docs/100 pond count was not re-read], not ~0.5 KB as first guessed.

**What the shim needs that v0 did not have.** Partial outs (#14, #15, #16's room short, #21): the row stores which bytes the game wrote, and the shim writes only those, so the caller's in-values survive exactly as on the game's own call (same rule as the docs/122 §14.9 memo fix). Undefined not-found bytes (#18, #20): the compare masks them. Rows #2, #4, #18 and #19 must be filled on the sim thread only (gate sites); every other row also, by the copy-never-call rule. #2, #4 and #18 additionally only when the lot level's terrain-footprint bit is set (correction above).

## 5. Blocked

| Reason | Definitions | Fix, if any |
|---|---|---|
| Own-field write on the shared definition | `CallPet` (`mSimPickerList`), `Terrain+DEBUG_LightningStrike` (`mHitType`), `SpawnJob` (`Lot`) | stays on the sim thread; for `CallPet` the docs/75 per-tick cache idea (stage A) |
| Placement or road search (class b, §1.4) | scratch/freak-out group (3), `MartialArts+TeleportDuringMeditation`, `MagicCauldronRegency+TeleportPotion`, `ChangeDoorbellSound` | sim thread |
| `World_FindGoodLocation` (UNSAFE in docs/62) | `PondManager+IceSkateHere`, `OccultUnicorn+UnicornTeleport` | sim thread |
| Footprint toggle, object create | `PushableStatue+Move` (`ObjectEnableFootprint`), one create-out-of-world test | sim thread |
| `World_GetObjectsImpl` (class d) | `Terrain+SplashInPuddle` | as `Query_GetObjects` (docs/121 I-121-7) |
| Real static writes | `InteriorDesigner+TakeMeasurements` (`DesignModeSwap` event add), `SnowWaterFight+PlayWith` (`CauseEffectService` data allocation), `ViewObjects` (UI model singleton) | sim thread |
| Lazy front-door lookup that writes the lot (`SetLotFrontDoor`) | `ChangeDoorbellSound` | pre-warm per lot would open it (1 sample) |

Together these are 17 sampled samples (1.6%), 14 of them `CallPet` [verified: tool output + entry list].

## 6. What a tier-2 pilot needs, and effort

Units per CLAUDE.md (wall-clock for Claude with agents). On top of the docs/122 pilot (M5 + shadow), which tier 2 reuses.

| Step | Work | Estimate |
|---|---|---|
| Allow-list v1 | the 37 meta definitions into `docs/data/autonomy-allow.csv` (MVID, closure, verdict); no new driver, `ScoreMeta` already walks every meta ad | 0.5 h |
| H7-H10 + pre-warm list | four ts3fixes worker hooks, abort reasons `cache`, `tuning` (2 counter slots, docs/registry.md), pre-warm calls | 2-3 h |
| Local driver | `AutShadow.ScoreLocal`: replay of the local phase (B9-B12, docs/59 §1.2) per object on the actor's lot into own arrays, same rules as docs/122 §3.6 | 3-5 h |
| Local snapshot | rows for every object on the actor's lot and its Sims (docs/59 §2.5 builder), dirty-row updates via docs/76/79 | 2-4 h |
| §4 rows | 26 IC shims and their row fields; partial-out write masks (#14-#16, #21), learned keys (#1, #5, #7), masked compare for #18/#20 (§4.1) | 5-8 h |
| Mode R / W in game | local phase in shadow, abort reasons, compare | 2-3 h + 2-3 jobs + 1 clean night |
| **Total** | | **~15-23 h**, 2-3 game jobs, 1 clean night |

Order: allow-list v1 first (meta only, no new code paths: +2.8% of scoring), then H7-H10 with the local driver on the ready set (no §4 rows: ~27%), then §4 rows (~31%).

## 7. Open items

- ~~**I-126-1:** the 26 new class-a ICs are classified from the docs/62 scan only; read each body to depth 2 before its shim.~~ Closed statically in §4.1 (all 26 snapshot rows; ledger §170, I-126-5..9). Left for Mode R: the lot-path field set of `GetPondInfo` (I-126-7) and the indirect calls the closure does not follow.
- **I-126-5..9** (§4.1): gate-site waits added to the cut by rows #2, #4, #18, #19; partial outs; undefined not-found bytes; world pond table size.
- **I-131-7/8** (§4.1 correction, docs/131 §9.4): `a-cut` rows #2, #4, #18 can run the terrain footprint update under `+0x918`; fill them only with the level bit set, count the skips in Mode R.
- **I-126-2:** pairs per type in the local phase (docs/75 §6 counter) would replace the proportional split of the 142 shared base-path samples and the bucket ratio of row 5.
- **I-126-3:** H10 serves a flag up to 60 ticks old; Mode R must show whether any local or meta test answer changes with it.
- **I-126-4:** H7's copy taken at the cut answers `Root_TargetOnHandTool` the same as the per-search cache.
- Not split: the other unresolved local buckets (31 samples, 3.0%) and the `.ctor` base frame (9 samples); each row is under 0.6%.
- Ledger: docs/51 §167, §170 (§4.1).

## 8. Built rows (2026-11-08, branch `feature/tier2-ic-rows`, not run in game)

Inventory of the 26 §4.1 ICs against what was built after §4.1 [verified: `kAsDesc` in `native/ts3native/autshadow.cpp`]:

- **Already built, skipped (3):** #12 `GetPoolObjectIdForPoolBelowPosition` and #22 `Objects_GetForward` (docs/219 §2 four shared rows, `[AutShadow] Tier2Rows`, `autofft2`, docs/208 §9.1); #18 `GetInformationAboutRoom` (docs/208 env driver, `AO_CUT`). #18 is filled at every cut **without** the §4.1 level-bit gate, so it can run the `+0x918` footprint update at the cut; it stays class `a-cut` in the tool (not counted as proven) until that gate exists.
- **New rows (15), behind a new key `[AutShadow] Tier2IcRows=0` (needs `Tier2Rows=1`), last in `kAsDesc` (`AS_NT2IC`):** filled by the game's body on the sim thread at the cut, never called by the worker (§4.1 copy-never-call). Owner per row: `AO_WORLD` (build-time data keyed by position, like v0 `IsPositionOutside`; refreshed at a full rebuild only) or `AO_CUT` (live-mode state, re-asked at every cut [inferred, ledger §457]).

| Group | # | IC | Owner | Stack dwords / return |
|---|---|---|---|---|
| G1 terrain, floor | 1 | `World_GetTerrainHeightImpl` | world | 3 / float |
| | 3 | `World_GetTerrainTypeImpl` | world | 3 / int |
| | 5 | `World_HasFloorFeaturesImpl` | cut | 7 / bool |
| | 6 | `World_HasFloorFeaturesWorldPosImpl` | cut | 5 / bool |
| | 8 | `World_IsCameraRoutableImpl` | world | 2 / bool |
| | 9 | `World_IsOnPlatformImpl` | world | 3 / bool |
| G2 pools | 10 | `World_IsInPoolImpl` | world | 3 / bool |
| | 11 | `World_HasPoolImpl` | world | 5 / bool |
| | 13 | `World_GetPondAtPoint` | world | 5 / uint |
| G3 lot, room | 17 | `World_GetRoomIdImpl` | world | 6 / short |
| | 19 | `World_LotIsEntranceFromTargetPointImpl` | cut | 2 / bool (gate sites 13/0 at the cut, no insert) |
| G4 objects, session | 23 | `Objects_GetBoundingBox` | object | result + 2 / 24 B |
| | 24 | `Objects_IsValid` | cut | 2 / bool |
| | 25 | `World_LightGetDimmerImpl` | cut | 2 / float (refcount on the sim thread only) |
| | 26 | `World_GetWorldFileName` | world | 0 / string |

- **Refused (8):** #2 `GetTerrainSurfaceType`, #4 `GetFloorSurfaceType` (`a-cut`: the level-bit gate `LotTable_GetIfPresent` + entry `+0x168` of §4.1 is not built, and without it the cut fill can wait on `+0x918` for a whole plan); #7 `GetTilesWithFloorFeatures` (`ulong[]`: the `AK_ARRAY` payload copies 4 B per element); #14 `GetPondInfo`, #15 `FindPondRepresentativePositions`, #16 `GetLotLocationAtFloor`, #21 `GetWorldToHouseboatDisplayPosition` (partial or conditional outs: the row writes the whole out on every answer, the game leaves bytes untouched; needs the §6 write mask); #20 `LotGetDisplayLevelInfo` (no-lot answer keeps the caller's dwords 4-5; needs a masked struct answer). Next step for most of them: one write-mask field in `AsDesc` (#14-#16, #20, #21), then the level-bit gate (#2, #4, and #18 retro).
- **Race set (docs/131):** no new row is called off the sim thread; #19's gate-site lookups and #25's refcount happen in the game's own call at the cut, as §4.1 requires. No row writes `+0x918` or footprints except through #2/#4/#18, which are refused or unchanged. Household Sims: rows only answer worker calls (Mode R, unseen use); no household path calls them differently.
- **Checks:** native selftest `TestAutShadowTier2IcGate` (key gating) and one function per group (`TestAutShadowTier2IcG1`..`G4`): descriptor = §4.1 layout, kind table entry, and a dummy cdecl body called the way `AsRefresh` calls the game's (never a game address). Managed Mode R item `autofft2ic` (`AutOffload.cs` `IcRead`, `IcG1`..`IcG4`, counters 878-883, agent `autoff_t2ic_*`) reads the rows through the game's wrappers for the `autofft2` objects and compares the worker's answers with the game's. Queue `tools/harness/queues/tier2-ic-rows.json` (smoke + BP/IP 10 min, gate 0 mismatches, 0 exceptions), repro `tier2-ic-rows-dry`; not run.
- **Status by group:** all 15 rows built (G1 terrain/floor, G2 pools, G3 lot/room, G4 object fields/session, 2026-11-08): each has its descriptor (gated off), a host selftest with a dummy body in the §4.1 layout, and a managed read in `autofft2ic` (slots 0-14, `IcG1`-`IcG4`). Mode R dry compare (`tier2-ic-rows.json`, pair `tier2rows1108`) not run yet [inferred until it passes]. `tools/autonomy-tier2.ts` `TIER2_BUILT` (class `row`) holds #12, #22 and the 15: 33 definitions moved from `new-rows` to `ready` (206 / 27 / 16 / 2 ready / new-rows / blocked / hooked).
- **E rows (2026-11-08, branch `feature/tier2-enablers`, pair `tier2mask1108`, not run in game; docs/data/tier2-enablers-handoff.md, ledger §460):** two shared-path pieces open four refused rows plus the docs/208 §9.2 houseboat row. E1 write mask: `AsDesc.of` = `AS_OF_TRUE` (the hit writes outs only when the stored bool is set), `outMask` (static byte mask of the out bytes the game may write), `AS_OF_DIFF` (the cut fills twice, out bytes 00 then FF, and keeps the bytes that came out equal: written on this input) and `AS_OF_ARR` (an out that is a ref to a fresh managed array: copied to the pool at the cut, re-made per hit). E2: `AK_ARRAY` element size = `AsDesc.sz` (4 or 8 B) in the pool copy and in `AsNewArrayGc`. Rows, each behind `Tier2IcRows=1` and its own bit (`[AutShadow] Tier2IcNew` bits 0-3, `Tier2HouseboatRow`; all 0 by default): **#7** `GetTilesWithFloorFeatures` (`ulong[]`, `AO_CUT`), **#15** `FindPondRepresentativePositions` (`AS_OF_TRUE | AS_OF_ARR`, `AO_WORLD`), **#16** `GetLotLocationAtFloor` (`AS_OF_DIFF`, mask `0xDFF`: bytes 0-8 always, 10-11 only with room data, `AO_WORLD`), **#21** `GetWorldToHouseboatDisplayPosition` and the houseboat row `GetHouseboatDisplayToWorldPosition` `0x788A20` (`AS_OF_TRUE`, 12 B out, `AO_CUT`). Household rule: the lot pick aborts on the worker before it reaches a houseboat lot of the active household, and `autofft2ic` slot 15-19 reads (`IcG5`) skip household-owned lots. Still refused: **#14** (124 B out over the 24 B out slot; lot path not read, I-460-6), **#20** (struct mask), **#2, #4** (E3 level-bit gate). Checks: native selftest `TestAutShadowTier2IcE` (descriptors, per-row gate, false answer leaves the out, masked and unwritten bytes keep the caller's values, arrays re-made, `ulong[]` payload 2 dwords per element), queue jobs `bp-t2e-dry` and `ip-t2e-houseboat-dry` in `tier2-ic-rows.json` (gate 0 mismatches). `TIER2_BUILT` adds the five: 220 / 13 / 16 / 2 ready / new-rows / blocked / hooked.

## Reproduce

```sh
node tools/managed-callgraph.ts subtypes 'Sims3.Gameplay.Interactions.MetaInteractionDefinition`3' --asm <repo>/game-copy/assemblies
node tools/autonomy-tier2.ts --asm <repo>/game-copy/assemblies --index <repo>/game-copy/analysis/callgraph/index.json > docs/data/autonomy-tier2.csv
node tools/autonomy-tier2.ts --no-tier2-hooks --asm ... --index ... > /dev/null      # counts before H7-H10 (stderr)
node tools/managed-callgraph.ts def <type> --cut "<cuts of §1.2>" --ics --index ... --asm ...   # one closure by hand
# §4.1 (on a copy of the Ghidra project; output stays in game-copy or a scratch folder)
analyzeHeadless <copy>/ghidra-ac121 TS3 -process TS3W.exe -noanalysis -readOnly -scriptPath tools/ghidra -postScript DecompileTo.java <out>/ics-d2.c 2 <the 26 bodies of §4.1> -postScript DecompStores.java <out>/ds.tsv 4 <same> '!0xC68680' '!0x4E16F0'
# then a capstone direct-call closure to depth 4 over game-copy/bin/TS3W.exe (functions.csv bounds) with the markers listed in §4.1
```
