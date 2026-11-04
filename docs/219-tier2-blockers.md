# 219. Tier-2 autonomy definitions: what still blocks them

2026-10-30. Static only: call-graph index and assemblies of the Steam 1.67 install, `tools/autonomy-virtual-closure.ts` what-if runs, no game run, no build. Per-definition table: `docs/data/tier2-blockers.csv`; generator `tools/tier2-blockers.ts`.

docs/126 sorted the 251 tier-2 candidates by their closures; docs/127 re-ran them with virtual dispatch and found most verdicts change. Since then three pieces were built offline: worker hooks H11 (rain shelter branch) and H12 (route stub in `Test`) and the `Query_GetGlobalObjects` row (docs/127 §7). This note takes that state as the baseline and lists, for every tier-2 definition that is still not ready, the exact missing piece, an effort class and the scoring share it would move.

Tags: **[verified]** = tool output or IL read for this note; **[inferred]** = from names, sample shares or the blocker path, not read to the end.

## Summary

| | Definitions | Share of scoring |
|---|---|---|
| Tier-2 candidates | 251 | |
| Ready (H7-H12 + the global-objects row) | 74 | |
| **Not ready** | **175** | **14.7%** |
| needs only the 4 shared rows (§2) | 76 | 5.0% |
| needs other snapshot rows (§2, all designed) | 68 | 2.6% |
| blocked, small fix (§3.1, verified by what-if) | 8 | 3.5% |
| blocked, medium | 8 | 2.3% |
| blocked, hard (stays on the sim thread) | 15 | 1.4% |

Plus 2 hooked base paths (docs/126 §3). Shares are of the 1,033 scoring samples of docs/75; they leave out the shared base path (`CheckAvailability`, `CommonTests`, C2 self, `get_Parent`), which moves with the local phase as a whole (docs/126 Summary) [verified: tool output].

- **Four snapshot rows hold back most of the local phase.** 130-135 of the 144 row-only definitions reach them, all through base-path overrides, not their own code (§2). With those four, 76 definitions (5.0%) are ready, including the `SpecialCaseTests` base path (2.1%).
- **The biggest single blocker is a base path, not a definition:** `CalculateScoreForObjectInteraction` (2.9%) scores any target, so its `get_InUse` call can reach every override. 10 of the 48 `get_InUse` overrides block (§3.1). Hooks on 8 of them plus one fire guard and one pre-warm move it and 7 more definitions to rows [verified: what-if].
- **No snapshot row lacks a design.** The rows these definitions need are among the 26 of docs/126 §4.1, plus four: `World_IsOnRoofImpl`, `Objects_GetHeightAboveFloor`, `Slots_IsSlotEnabledImpl`, `Slots_GetRoutingSlotNamesImpl` [verified: tool output].
- **A receiver-typing fix moved three definitions.** `PlayOnRug`, `PetPlayOnCarpet` and `Helm+ReturnToPortTerrainProxy` call `Terrain.PlayOnCarpetInternal.Singleton.Test(...)`. The field is declared `InteractionDefinition`, so the typed closure entered every `Test` override (250 blockers). `managed-callgraph.ts` now types a receiver loaded from a static field by the only object ever stored there (§1). They are now row-only [verified].

## 1. Method

1. **Closure.** `autonomy-virtual-closure.ts --full`: the docs/127 typed closure (overrides up to 5 hops) with the built hooks as what-if cuts: H11's four `CreateInstance` / `IsShelteredFromPrecipitation` prefixes, H12's `SimRoutingComponent.CreateRoute` and `Route.IsPointRoutable`, and `Queries.Query_GetGlobalObjects` (the cut stands in for the row, as docs/127 §7). `--full` writes the whole docs/126 classification per candidate: every IC outside the pilot snapshot set with its class, and every blocker with the path the closure found first. The csv columns of docs/127 list only what overrides add.
2. **Singleton receivers** (new in `managed-callgraph.ts`, `exactStaticFieldType`): a static field whose every store in the game assemblies is `newobj T; stsfld`, and whose address is never taken, holds an exact `T` or null. A receiver loaded from it gets type `T` instead of the field's declared type. Self-check: the `PlayOnRug` call above resolves to `Terrain+PlayOnCarpetInternal+Definition` [verified]. This only narrows closures, so no verdict gets worse. The rerun moved 3 of 34 blocked definitions to rows and changed nothing else [verified: tool output].
3. **Missing piece.** For a row-only definition: every class-a IC outside the snapshot set, with the key its answer depends on (docs/126 §4.1 numbers where designed). For a blocked definition: its blocker list read by hand from the paths (§3). Every class-b, w or d IC, non-pilot static write, yield, event send and own-field write counts, as docs/126 §1 step 5.
4. **Effort class.** *small* = an existing pattern: a designed row, a worker hook that aborts, a pre-warm (under ~1 h each). *medium* = a new mechanism at one site: a row without a design, an `a-cut` fill rule, a per-tick cache, a per-subclass split (~1-4 h). *hard* = the `Test` really writes game state, plans, places or yields; it stays on the sim thread. Row-only definitions are small unless they need an undesigned row, an `a-cut` or `+0x918` row, or more than 3 rows beyond the shared four.
5. **Share.** The docs/75 sampled share of the definition's entry-list row. An ambiguous row is split evenly over the candidates that share it. Row 5 (61 samples, not splittable per type, docs/75 §6) is split over its 169 candidates. The tier-2 meta share (29 samples, docs/126 Summary) is split over its 37 definitions. So a row-5 or meta definition's share is a count split [inferred], each ~0.03-0.08%.

## 2. Snapshot rows

Rows by the definitions that need them (`tools/tier2-blockers.ts` stderr) [verified: tool output]:

| Row | Definitions | Their share | Answer depends on | Effort |
|---|---|---|---|---|
| `World_GetPoolObjectIdForPoolBelowPositionImpl` | 135 | 4.9% | pool id per row position (§4.1 #12) | small |
| `Objects_GetForward` | 133 | 4.9% | forward vector per row object (§4.1 #22) | small |
| `World_IsOnRoofImpl` | 131 | 6.7% | one bit per row position (docs/127, ~0.25 h) | small |
| `Objects_GetHeightAboveFloor` | 130 | 4.6% | float per row object, beside the row position | small |
| `World_GetLotLocationAtFloorImpl` | 24 | 1.1% | per (row, roomDef); room short only with room data (§4.1 #16) | small |
| `World_IsInPoolImpl`, `World_HasPoolImpl` | 18, 12 | 0.6%, 0.4% | bit per row; bit per (lot, row tile) (§4.1 #10, #11) | small |
| `World_GetTerrainHeightImpl` | 15 | 0.5% | float per (row x/z, bridges flag) (§4.1 #1) | small |
| `Slots_IsSlotEnabledImpl` | 10 | 0.6% | bit per (object, slot name); the body takes `+0x918` (docs/90), so fill at the cut like the `a-cut` rows | medium |
| `World_IsCameraRoutableImpl` | 10 | 0.4% | bit per row (§4.1 #8) | small |
| `World_GetTerrainSurfaceTypeImpl` | 6 | 0.2% | per (row, level); `a-cut` (§4.1 #2) | medium |
| `Slots_GetRoutingSlotNamesImpl` | 5 | 0.4% | slot-name array per object model, fixed per model: memo | medium |
| 17 more §4.1 rows | 1-5 each | ≤ 0.25% each | §4.1 | small, `a-cut` medium |

A definition's share counts once per row it needs, so the column does not add up. **Where the four shared rows come from** [verified: tool paths]:

- `World_IsOnRoofImpl`: the generic base `Test` -> `InteractionDefinitionUtilities.SpecialCaseTests` -> `GameObject.get_IsOnRoof` (146 of 152 paths).
- `Objects_GetHeightAboveFloor`: `TombRoomManager.IsObjectInAFoggedRoom` -> `GetRoomId` -> `GameObject.get_PositionOnFloor` (143 of 156).
- `World_GetPoolObjectIdForPoolBelowPositionImpl`: posture satisfaction -> `Pool.StandingSatisfaction` -> `SwimHere.GetDestinationPool` -> `Pool.GetPoolNearestPoint` (148).
- `Objects_GetForward`: `CommonTests` -> `Posture.Satisfies` -> `RelaxingPosture.Satisfaction` -> `GameObject.PointIsInVisionCone` (134).

All four answer per row object or position, the same families as docs/121 §2.2, filled on the sim thread at the cut (copy, never call live).

## 3. Blocked definitions, by share

| Share | Definition | Missing piece | Fix | Effort |
|---|---|---|---|---|
| 2.90% | base path `Autonomy.CalculateScoreForObjectInteraction` | `GetInUseAutonomyMultiplier` -> any `get_InUse` override: 10 of 48 block (§3.1) | §3.1 hook set | small |
| 1.36% | `Sim+CallPet` | `Test` fills `mSimPickerList` on the shared definition object | docs/75 §3 stage A per-tick cache, or a worker copy with its own list [inferred] | medium |
| 0.87% | scoring function `SitHereForEating.EatHereScoringFunction` | `GetClosestFreeSlotPair` -> chair `CanScootBack` (`ChairBarStool`, `ChairDining`): footprint disable, `World_TestObjectPlacementPQ`, enable | a per-chair bit at the cut costs one placement test per chair per cut | hard |
| 0.39% | `Phone+Call+CallDefinition`1` | the generic phone-call base: its `Test` reaches every subclass `Test` (gig proprietors 45 blockers, chess challenger 16, world file search 3) | split the entry-list row per concrete subclass; allow-list the clean ones | medium |
| 0.29% | scoring function `GameObject.SitInteractionModifier` | the same `get_InUse` family | §3.1 (then also `Query_CountObjectsInRange`, no row design) | small |
| 0.19% | `Sim+Sim_ReadToSleep_Autonomous` | `Relationship.GetMiniRelationships` adds to `Relationship.sAllRelationships`; `CauseEffectService` lazy data | pre-warm `CauseEffectService`; abort while the Sim has no relationship entry | medium |
| 0.15% | `SocialInteractionA` | the same two; its 6 own-field hits are a `new Definition(...)` in `GetYouShouldTargets`, a fresh object [inferred: rule false positive] | as above | medium |
| 0.10% | `ViewObjects`; also `WalkerClassic+HoldChild` (row 5) | `LiveDragHelperModel.get_Singleton` lazy singleton | pre-warm once per load | small |
| 0.10% | `NotAutonomousForSelectableSims` | through `CalculateScoreForObjectInteraction` | §3.1 | small |
| 0.08% | `BakeSaleTable+GotoBakeSaleTable` (meta) | `InteractionUtil.Test(InteractionDefinition, ...)`: the receiver is a parameter, so 200+ blockers come from every `Test` override | type the definition argument per call site, then re-verdict (first real blocker: `World_TestObjectPlacementP`) | medium |
| 0.03% each | row 5: `TraitFunctions+NeatTraitCleanHouse` | `Lot.DoesObjectNeedCleaning` calls `get_InUse` itself | §3.1 (hooks on the overrides, not on the multiplier) | small |
| | row 5: `Phone+CallIntelligenceAgency`, `GoToOccupationJobLocation` | `Lot.IsFireOnLot` -> `FireManager.HasOnlyLeafPileFires` -> `CheckObjectPlacement` | abort `fire` while the lot has a fire | small |
| | row 5: `InteriorDesigner+TakeMeasurements` | `Cabinetry.get_BuyModeSuite` lazy suite adds a `DesignModeSwap` event handler | pre-warm per cabinetry object, or abort while null | medium |
| | row 5: `Door+ChangeDoorbellSound` | `Lot.GetFrontDoor` lazy lookup writes the lot (`SetLotFrontDoor`, street parking) | pre-warm the front door per lot | medium |
| | row 5: `RockBand+GetBandMemberToLot` | wide `Test` dispatch, as `GotoBakeSaleTable` | type the argument, re-verdict | medium |
| | the other 14 hard ones | own `Test` places, plans, creates, destroys or yields: `MartialArts+TeleportDuringMeditation`, `FreakOutAtObject`, the two scratch definitions (placement); `PondManager+IceSkateHere` (`FindGoodLocation`); `PushableStatue+Move`, `MagicCauldronRegency+TeleportPotion`, `OccultUnicorn+UnicornTeleport` (object create/destroy, route plan); `InteriorDesigner+CompleteRenovation` (name lookup adds a skill); `Jetpack+FlyHere` (posture pop sleeps); `SpawnJob`, `SnowWaterFight+PlayWith` (own-field writes); `Terrain+SplashInPuddle` (`World_GetObjectsImpl` + placement); `Terrain+DEBUG_LightningStrike` (debug) | stay on the sim thread | hard |

### 3.1 The `get_InUse` overrides

A base path or scoring function has no definition or target type, so the typed closure enters every game override of `GameObject.get_InUse`. `tools/tier2-blockers.ts` closes each one with the same rules (the `this` family plus IL receivers) [verified: tool output]:

| Overrides | Count | Why they block | Worker answer |
|---|---|---|---|
| `Soil`, `BBallHoop+BBallHoopBall`, `BBallArcade+BBallArcadeBall`, `ChocolateFountain+ChocolateFountainSkewer`, `IceCreamMaker+IceCreamCone`, `GardenStation+GardenStationProp` | 6 | the getter can destroy the object (`ScriptObject.Destroy`: object manager remove, unparent, opacity) [inferred: cleanup of an orphaned prop] | hook: abort `inuse` on the worker |
| `IceCreamMaker`, `DeepFryerVegas` | 2 | `FireManager.IsObjectBurning` -> leaf-pile fire check -> `CheckObjectPlacement` | fire guard on `FireManager.HasOnlyLeafPileFires`: abort `fire` |
| `WalkerClassic`, `PlaypenClassic` | 2 | `LiveDragHelperModel.get_Singleton` lazy create | pre-warm |
| `StuffedAnimal` | 1 | row only: `Objects_IsValid` (§4.1 #24) | row |
| the rest | 37 | ready | |

Nine of the ten are store objects; `Soil` is base-game gardening, so a soil target aborts often [inferred]. Mode R should count `inuse` aborts by class.

**What-if** (the 6 overrides, `HasOnlyLeafPileFires` and `LiveDragHelperModel.get_Singleton` as cuts on top of the baseline) [verified: tool output]: blocked 31 -> 23, row-only 144 -> 152. The 8 that move are exactly the 8 *small* rows of §3: the base path (then needs `Objects_GetHeightAboveFloor`, `Objects_IsValid`, `World_GetLotLocationAtFloorImpl`), `SitInteractionModifier` (also `Query_CountObjectsInRange`), `NotAutonomousForSelectableSims`, `NeatTraitCleanHouse`, `ViewObjects`, `WalkerClassic+HoldChild`, `CallIntelligenceAgency` and `GoToOccupationJobLocation`. The two scratch definitions drop from 9 blockers to 1 (their own placement test).

## 4. Order of work

| Step | What | Opens | Effort |
|---|---|---|---|
| 1 | the 4 shared rows (§2) | 76 definitions, 5.0% | ~1-2 h (two are §4.1 #12, #22) |
| 2 | §3.1 hook set: 6 `get_InUse` hooks + fire guard + `LiveDragHelperModel` pre-warm; abort reasons `inuse`, `fire` (counter slots per docs/registry.md) | 8 definitions, 3.5%, to row-only; with step 1 and 3 rows most become ready | ~1 h + Mode R abort counts |
| 3 | the other §4.1 rows + the two slot rows | 68 definitions, 2.6% | 5-8 h (docs/126 §6) |
| 4 | `CallPet` cache, phone per-subclass split, relationship guard + `CauseEffectService` pre-warm | 1.4% + 0.4% + 0.3% | ~1-4 h each |
| 5 | argument typing at `InteractionUtil.Test` call sites (tool), re-verdict `GotoBakeSaleTable`, `RockBand+GetBandMemberToLot` | < 0.2%, plus soundness of other closures | ~1 h |
| - | the 15 hard ones | 1.4% | stay on the sim thread |

Steps 1-3 together: ~11% of scoring on top of what is ready now (5.0 + 2.6 + 3.2; `SitInteractionModifier` still needs `Query_CountObjectsInRange`) [inferred: shares add, aborts not counted]. Each row and hook is checked in Mode R before its definitions join the allow-list (docs/122 §4.3).

## 5. Limits

- **Shares of row-5 and meta definitions are count splits,** not measured per type (docs/75 §6, I-126-2). The 147 row-5 definitions in the csv carry 0.035% each.
- **Closure limits of docs/127 §5.1 hold:** 5 override hops, receivers typed per call site only. The new singleton rule needs the field's every store to be a `newobj`; fields filled any other way keep their declared type.
- **The blocker reading of §3 is from tool paths,** not from decompiled bodies read to the end. "Orphaned prop" and the `SocialInteractionA` false positive are [inferred].
- **Abort cost is not counted:** a hook that aborts (`inuse`, `fire`) keeps the answer right but sends the unit back to the sim thread. How often that happens needs Mode R in game.
- The baseline hooks (H11, H12, the global-objects row) are built offline and not run in game (docs/127 §7).
- **The committed `docs/data/autonomy-virtual-closure.csv` is not regenerated.** A plain docs/127 rerun today gives 22 ready / 134 new rows / 112 blocked / 2 hooked instead of 23 / 131 / 114 / 2. Three changes are the singleton rule (§1). The fourth, `EatHereScoringFunction` ready -> blocked (chair `CanScootBack`), shows up without the rule too, so the committed csv predates a tool or index change [verified: rerun before and after the rule]. docs/208's `offload3-closure.ts` also uses `receiverTypes`; its csv is not rerun either.

## Reproduce

```sh
# baseline: the built hooks as what-if cuts (docs/127 §7), whole classification as JSON
node tools/autonomy-virtual-closure.ts --asm <repo>/game-copy/assemblies --index <repo>/game-copy/analysis/callgraph/index.json --what-if "<H11 cuts>,<H12 cuts>,ScriptCore.Queries::Query_GetGlobalObjects(" --full <scratch>/t4_full.json > /dev/null
node tools/tier2-blockers.ts --asm ... --index ... --full <scratch>/t4_full.json --what-if "<same cuts>"   # writes docs/data/tier2-blockers.csv
# §3.1 what-if: add the 6 get_InUse overrides, Sims3.Gameplay.Controllers.FireManager::HasOnlyLeafPileFires( and Sims3.Gameplay.UI.LiveDragHelperModel::get_Singleton( to the cuts
node tools/managed-callgraph.ts --self-check --asm ...   # includes the singleton receiver check
```

H11 and H12 cuts as in docs/127 Reproduce. Csv columns: `definition`, `blocker_kind` (`snapshot-rows`, `managed-write`, `own-field-write`, `static-write`, `static-coll`, `sim-thread-ic`, `writing-ic`, `unclear-ic`, `yield`, `family-root`, `wide-dispatch`), `blocker_detail` (rows with their key, or the blocker and its fix), `effort`, `est_share` (fraction of scoring samples), `notes` (share basis, blocker count).
