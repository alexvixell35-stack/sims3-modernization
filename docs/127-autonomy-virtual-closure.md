# 127. Autonomy closures with virtual dispatch resolved

2026-10-20. Static only: call-graph index and assemblies of the Steam 1.67 install, the decompiled class library for the receiver sites, NRaas ErrorTrap and Overwatch extracted read-only from the Mods folder into the session scratchpad. No game run, no build. Per-definition table: `docs/data/autonomy-virtual-closure.csv`; generator `tools/autonomy-virtual-closure.ts` (closure in `tools/autonomy-tier2.ts`, dispatch in `tools/managed-callgraph.ts`).

The allow-list closures (docs/121 v0, docs/126 tier 2) followed a virtual or interface call only into its declared body (docs/126 §1 "Limit"). This note follows every override the receiver can dispatch to, up to 5 calls from each definition's `Test`/`CalculateScore` roots, and re-runs the docs/126 verdict.

Tags: **[verified]** = tool output, IL or the decompiled class library read for this note; **[inferred]** = reasoning, not checked live.

## Summary

| Run | Ready | New rows | Blocked | Hooked |
|---|---|---|---|---|
| docs/121 + docs/126 (declared body only) | 191 | 61 | 16 | 2 |
| **Overrides, typed receivers (this note)** | **23** | **131** | **114** | 2 |
| Overrides, typed, with hook H11 (§4, what-if) | 51 | 154 | 63 | 2 |
| Overrides, plain class hierarchy (upper bound) | 20 | 12 | 236 | 2 |

270 closures: the 12 v0 definitions, the 7 `HowMuchItWantsSimToCome` implementors and the 251 tier-2 candidates [verified: tool output].

- **The overrides change 191 of 270 verdicts** [verified: tool output]. They reach **346 IC bindings** the old closures did not (bindings, so `UTFConnector*` twins count twice): 212 class a (29 of them already in the pilot snapshot set), 91 w, 27 b (3 already abort branches), 8 c, 6 a-stub, 2 d.
- **All 12 v0 definitions go from ready to blocked, for one reason: a static write** [verified]. In rain or hail with Seasons on, `Autonomy.CalculateScoreForObjectInteraction` creates a throwaway interaction instance for an outdoor target to ask `SeasonsManager.IsShelteredFromPrecipitation`; `InteractionInstance.Init` increments the static `InteractionInstance.sNextId` and allocates a stack trace. The old closure missed it because `CreateInstance` is virtual on the definition. The docs/122 guard thunk would not catch it either: it guards ICs, and this is a managed write. 35 of the 37 tier-2 meta definitions are blocked by the same branch.
- **With a worker hook on that branch (H11, §4)** the 12 v0 definitions need one new row each (`Query_GetGlobalObjects`, through `WorldLot.GetObjects<T>` overriding `Lot.GetObjects<T>`) and 28 of 37 tier-2 meta definitions are ready again.
- **Implementors:** `Lot` and `BakeSaleTable` need the same new row; the other 5 stay ready [verified].
- **Local phase:** the main new row is `World_IsOnRoofImpl` (reached by 152 closures, the first new reason of 61 verdict changes: `InteractionDefinitionUtilities.SpecialCaseTests` reads `IGameObject.IsOnRoof`, whose only implementation is `GameObject`'s). The main new blocker is route creation in `Test`: 31 `Test` bodies call `actor.CreateRoute()`, and `SimRoutingComponent.CreateRoute` sets route options, followers and valid rooms on the new native route (`Route_Set*`, class w by the docs/126 name rule).
- **Yields and event sends:** 15 closures newly reach one (§3.3), all through override bodies of objects or instances, none in v0.
- **Mods:** NRaas ErrorTrap and Overwatch add 27 overriding methods, all on NRaas's own interaction and definition classes; none on an object, posture, service or component class (§5).
- **Update §7 (both open items closed, static):** the world lot **is** a visit target (I-127-3 refuted): a Sim standing off-lot scores its current lot, the world lot, and `VisitLot`'s test asks it for a `Callbox` through `Query_GetGlobalObjects`. The row is a learned type key per cut answered by the game's own IC body (§7.1). With H11, the new worker hook H12 (§7.2) and that row, **all 12 v0 definitions and all 7 implementors are ready**, 35 of 37 tier-2 meta; totals 94 ready / 141 new rows / 33 blocked / 2 hooked [verified: tool output, what-if]. Route creation in `Test` (I-127-2): the route is the `Test`'s own and is only asked `IsPointRoutable`; H12 hands the worker a route with no native object and aborts on the first query. That opens 30 of the 31 (`Jetpack+FlyHere` stays blocked by `Simulator.Sleep`), worth at most ~1% of scoring.

## 1. Method

**Roots and cuts.** As docs/126 §1: `defRoots` (own or nearest `Test` + `CalculateScore`), depth 12, delegates not followed, docs/121's two guards, the pilot's worker hooks and H7-H10. Candidates: the docs/126 251, plus v0 and the implementors (docs/122 §12.3).

**Which overrides.** At a `virt` or `iface` call (docs/78 edge kinds) in a method fewer than 5 calls from a root, the closure also enters overrides; deeper calls keep the declared body only, as before. Three runs:

1. **Class hierarchy (CHA):** every override in the game assemblies (docs/103 §2's `overridesOf`). BCL declared targets (object and collection members) are not expanded. This is the sound upper bound and is far too wide: `InteractionDefinition`3.Test(actor, target, ...)` alone has 4,932 overrides, `GameObject.get_InUse` dozens, so 236 of 270 closures block.
2. **Typed (the verdicts used here):** each visit carries the class of its `this`, and a call enters only the overrides that class can reach (`familyTargets`: overrides in the class's subtree plus the nearest one up its base chain; for an interface, overrides on classes that implement it). The callee's class comes from, in order:
   - a hand-reviewed receiver site (§2);
   - the receiver's **static type in the IL** (`receiverTypes` in `managed-callgraph.ts`): C# names the original declaration in a `callvirt`, so the target says nothing about the receiver; the tool walks back from the call over instructions with a known stack effect to the one that pushed the receiver (argument, local, field, call result, cast, `newobj`) and takes its type. It gives up at a branch target in between (stack merge), on any other opcode or on a generic parameter type. When the receiver is `ldarg.0`, the caller's own class carries over [verified: implementation + self-check];
   - else the caller's class, for a call between two methods on that class's base chain [inferred: receiver not tracked there; verified for the generic base `Test` calling `this.Test(actor, target, ...)`].
   Roots carry the definition type (implementors: the implementor type).
3. **Typed + what-if cut:** the typed run with extra cuts (`--what-if`), used once for H11 (§4).

**Verdict.** docs/126 §1 step 5 unchanged (pilot union and snapshot set from the old closures). A new IC already in the snapshot set is marked `*` in the csv and does not change the verdict.

**Self-check** (`managed-callgraph.ts --self-check`, added): `Sim.OnPortalPathPlan` with overrides reaches `Turnstile.IsAllowedThrough` and without does not; `System.Object.ToString` is not expanded; the generic base `Test` resolves to 3 methods for `VisitLot+Definition` (itself and its 2 subclasses) of 4,932; `GameObject.GetInUseAutonomyMultiplier` reads `InUse` on `this`; `CalculateScoreForObjectInteraction` calls `GetInUseAutonomyMultiplier` on an `IGameObject` local; the `Lot` family of `get_InUse` is smaller than all overrides [verified: passes]. `autonomy-tier2.ts` without overrides still reproduces `docs/data/autonomy-tier2.csv` byte for byte [verified].

## 2. Hand-reviewed receivers

Where the IL type is too wide (an `IGameObject` local, a field typed by an interface) the receiver was read in the decompiled class library [verified unless marked]:

| Caller (or producer) | Declared | Receiver |
|---|---|---|
| any, receiver from `InteractionObjectPair.get_Target` / `get_InteractionDefinition` | any | the scored pair's target / definition [inferred for callers outside `CalculateScore*` and `CheckAvailability*`] |
| any, receiver from `InteractionInstanceParameters.get_Actor` / `get_Target` | any | the root `Test`'s actor / target class (the `Test(ref parameters)` overloads) |
| `MetaInteractionScoredByVenue`3.CalculateScore` | `IMetaObject` members | target (cast into a local first) |
| `Autonomy.CalculateScoreForObjectInteraction` | `IGameObject.GetInUseAutonomyMultiplier` | target (kept in a local) |
| `SeasonsManager.IsShelteredFromPrecipitation` | `ICanCountAsBeingIndoors.IsInteractionConsideredIndoors` | target on the scoring path [inferred for other callers] |
| `InteractionDefinition`3.CreateInstance` | `InteractionInstance` members | the definition's `TInteraction` (from its generic base, `instanceTypeOf`) |
| `GameObject.CreateRoute` | `RoutingComponent.CreateRoute` | `SimRoutingComponent` when `this` is a Sim (`Sim` adds that component at construction) |

The target class is the root `Test`'s second parameter, so it holds once `Test` has passed (scoring runs after it) [inferred]. Closures without a definition (base paths, scoring functions) have no actor/target/definition class, so those sites fall back to all overrides.

## 3. Results

### 3.1 v0 and the implementors

| Definition | Closure: old / typed / CHA | Typed verdict | New ICs outside the snapshot set | With H11 |
|---|---|---|---|---|
| `VisitCommunityLot+Definition` | 548 / 930 / 3,864 | blocked (`sNextId`) | `Query_GetGlobalObjects` | new rows |
| `VisitLot+Definition` | 595 / 1,030 / 3,930 | blocked (`sNextId`) | `Query_GetGlobalObjects` | new rows |
| other 10 v0 definitions | 389-411 / 796-884 / ~3,840 | blocked (`sNextId`) | `Query_GetGlobalObjects` | new rows |
| `Lot`, `BakeSaleTable` implementors | 373, 354 / 461, 422 | new rows | `Query_GetGlobalObjects` | new rows |
| `RabbitHole`, `GardeningMetaObject`, `WorldPondHelper`, `HomeMetaObject`, `WorkMetaObject` | | ready | none (only snapshot-set ICs) | ready |

[verified: tool output] The other new v0 ICs (`World_IsPositionOutsideImpl`, `World_GetMoonPhaseImpl`, `World_GetDoorRoomsImpl`, product price and resource key, `GameUtils_GetWorldName`, `Query_GetObjectsOnLot`) are already in the snapshot set; they now show up per definition because the venue score enters the target's own `HowMuchItWantsSimToCome`, which the pilot union already covers through the implementor roots.

`Query_GetGlobalObjects` path: `Lot.CommonVisitTest` -> `Lot.FindCallbox` -> `GetObjects<T>` dispatched to `WorldLot` -> global object query [verified: tool path]. The docs/62 scan reads it as class a; it is a world-wide query like `Query_GetObjects` (class d, I-121-7), so its row is a per-type global list [inferred]. Whether the target `Lot` of a visit can be the world lot at all is open (I-127-3). **Closed in §7.1:** it can; the row is a learned type key.

### 3.2 Tier 2

| Group | Old: ready / rows / blocked | Typed | Typed + H11 |
|---|---|---|---|
| meta-rest (37) | 37 / 0 / 0 | 0 / 0 / 37 | 28 / 8 / 1 |
| local-sampled (31) | 21 / 4 / 6 | 6 / 14 / 11 | 6 / 15 / 10 |
| row5-candidate (169) | 102 / 57 / 10 | 4 / 113 / 52 | 4 / 115 / 50 |
| base-local (11, 2 hooked) | 9 / 0 / 0 | 7 / 1 / 1 | 7 / 1 / 1 |
| scoring-function (3) | 3 / 0 / 0 | 1 / 1 / 1 | 1 / 1 / 1 |

[verified: tool output]

Newly blocked, by the first new reason: `InteractionInstance.sNextId` 51 (the rain branch), `Route_SetOption` 31 (route creation in `Test`, with the static `Route.sCtorInfo` and `Route_SetFollower`/`SetAGS`/`SetValidRooms`/`AddObjectToIgnoreForRoute` behind it), `World_TestObjectPlacementP` 5 (another definition's `Test` called from a `Test`), `World_RemoveObjectFromObjectManagerImpl` 4 (`BBallHoopBall.get_InUse` destroys a stale ball), `Relationship.sAllRelationships` 2, `CheckObjectPlacement` 2, one each for stair product info, world file search and a level-up sound through a chess-rank lookup in `GetInteractionName`.

Newly new-rows, by first reason (all 270 rows): `World_IsOnRoofImpl` 61, `Query_GetGlobalObjects` 30 (2 of them implementors; `WorldLot.GetObjects<T>`, `RoomConnectionObject.IsOutside` via door rooms), `Objects_GetForward` 2 (relaxing posture vision cone). Reached at all: `Query_GetGlobalObjects` by 206 closures, `World_IsOnRoofImpl` by 152.

The b and w ICs reached most often: object create/add/destroy (`Simulator_CreateObjectImpl` 34, `Simulator_AddObjectImpl` 11), placement tests (13), `Simulator_SleepImpl` 11, footprint and room-marker writes, SACS driver acquire/release, UI window create/destroy (42 via `UIManager` in `GetInteractionName`/tooltip overrides) [verified: tool output; class from the docs/126 rule, inferred].

### 3.3 Yields and event sends

15 closures newly reach a yield (`Simulator.Sleep`, `StateMachineClient.RequestState`, SACS driver acquire) or an `EventTracker.SendEvent`: meta-rest `BakeSaleTable+GotoBakeSaleTable`, `ShoppingRabbitHole+ShopInRabbitHole`1`, `WorkInRabbitHole`, `ProSports+PlayGame`; the base path `CalculateScoreForObjectInteraction` (no definition context, so its `CreateInstance` site is open); the phone `CallDefinition`; row 5 `PushableStatue+Move`, `PlayOnRug`, `MagicCauldronRegency+TeleportPotion`, `Sim+PetPlayOnCarpet`, `Helm+ReturnToPortTerrainProxy`, `InteriorDesigner+CompleteRenovation`, `RockBand+GetBandMemberToLot`, `Jetpack+FlyHere`, `OccultUnicorn+UnicornTeleport` [verified: tool output]. None is a v0 definition; all 15 are blocked in the typed run.

## 4. What changes for the pilot

**H11, rain/hail shelter branch** (new worker hook, design only): in `CalculateScoreForObjectInteraction` the shelter test runs only with Seasons on, weather rain or hail, an autonomy search and an outdoor target [verified]. On the worker, answer the branch without creating an instance: abort the pair to the sim thread when the three conditions hold (weather is constant within a search), or later serve `IsShelteredFromPrecipitation` from a per-target snapshot bit. Abort is enough for the pilot: it costs only rainy outdoor pairs. Effort: ~30-60 min (one ts3fixes hook + abort reason `rain`, counter slot per docs/registry.md) plus a Mode R check in a rain save.

**H11 built 2026-10-20 (offline, ts3native 0.36.5, docs/122 §14.10):** abort variant, in ts3fixes `AutShadow.ScorePair` on the worker before the score (`AutShadowCore.RainShelter`, conditions in the game's order, `IsOutside` read last); new abort reason `rain` (20, counters `ts3native_autshadow` v4 `abort2[0]`). The per-target shelter-bit variant stays a later option. Not run in a rain save yet.

### 4.1 H11 shelter answer (built 2026-10-31, ts3native 0.36.148 + ts3fixes, not run)

Why: `rain` is docs/216 rank 3 (BP 0.05-0.08, IP 0.09-0.23, crowd 0.85-1.75 ms/s, weather-dependent). What the branch reads [verified, decompiled `Autonomy.CalculateScoreForObjectInteraction` and `SeasonsManager`]: Seasons on, weather rain or hail, an autonomy search, `isOutside`, target not a Sim, then `IsShelteredFromPrecipitation(target, iop.InteractionDefinition.CreateInstance(target, Actor, Autonomous, true, true))`; unsheltered multiplies the score by `kRainHailObjectAutonomyScoringMultiplier`. That method uses the instance only for `ICanCountAsBeingIndoors` targets (tents, igloos, boats, a few rabbit-hole-like objects); for every other target it is the position test (diving lot below sea level, `World.HasAnyRoofOrFloorAbovePos`). The instance is built only to be passed in, and the argument is evaluated before the call, so a hook on the shelter method alone cannot stop `sNextId++`.

Design (no `CreateInstance` on the worker, so no static write and no worker-local counter to prove):
1. **Cut bit** (sim thread): `ShelterFill` stores per meta object 0 none / 1 sheltered / 2 unsheltered (`AutShadowCore.ShelterBit`): none for Sims, `ICanCountAsBeingIndoors` targets, objects not outside, or no rain/hail. Filled at the request and again at the cut in `AutReference` (the instant the reference reads; Mode 3 runs `AutReference` only on `VerifyEvery` cuts, so other Mode 3 cuts use the request's bit, the same frame).
2. **Skip** (worker, `Satb=2` act span only): `ScorePair` arms ts3native (`AutShadowNative.RainArm(definition, site)`, site = the interface method's MonoMethod) right before the score; the worker's callvirt emulation (`satb.cpp` case 0xEC) sees that receiver with that method item and does not make the call: receiver and five arguments popped, a null reference as the result, ip past the 4-word callvirt (`SatbRainSkip`, once per arm).
3. **Answer** (worker): `SeasonsManager.IsShelteredFromPrecipitation(IGameObject, InteractionInstance)` is hooked (`AutShelterHook`, game method off the worker); with a null instance and the armed target it returns the bit (`AutShadowCore.ShelterAnswer`), anything else aborts `rain`.
4. **Check**: after the score `RainArm(null)` disarms (always, in a `finally`) and returns the skips; the pair counts only with exactly one skip and one hook answer (`ShelterDone`), else `rain` as before. The inventory sub-branch (`IsShelteredFromPrecipitation(mActor)`, no instance) is unchanged.

**rain-h11 round 1 (2026-10-31, BP Mode R in rain):** `rainArm` 2,203, `rainSkip` 0, `rainMiss` 2,203, rain aborts unchanged. Cause [verified, decompiled `MetaInteractionDefinition.CalculateScore`]: the allow-listed meta definitions score 0 unless the search runs local autonomy, so a meta search never reaches the branch; the old H11 aborted these pairs although nothing in them creates an instance (false positives). Fix (0.36.148 rebuilt, same names): arm every rain pair (no bit = the hook aborts if reached), accept a pair with no skip and no hook answer (branch not reached), and skip the site on any receiver while armed (no worker `CreateInstance(5)` at all; a skip the hook did not answer aborts). bp-rain-dry had 12 real skips (the reached branch). Off paths stay the old abort: older ts3native (`Config(32)` 0), `Satb` not 2, hook not patched, no bit. Counters `ts3native_autshadow` v21: `rainArm`, `rainSkip`, `rainMiss` (+0xC8C). Claims I-410-1..4 (docs/51 §410). Tests: native `TestSatbRainSkip` (satb-test.h), `tools/selftest/autshadow` (bit, answer, done rule). Repro `aut-rain-bp` (STRESS: forced Moderate rain, `noEnv`), queue `tools/harness/queues/rain-h11.json` (BP Mode R 10 min + BP Mode 3 Dry 10 min, Satb=2). Gate: Mode R A1-A3 0 with rain pairs scored. In go-live the sim no longer runs the throwaway `CreateInstance` for consumed rain pairs, so its `sNextId` sequence skips fewer ids than vanilla (ids stay unique; nothing reads the gaps [inferred]).

**New rows:** `Query_GetGlobalObjects` (all v0 definitions, 2 implementors, most tier-2 closures; per-type global list, ~0.5-1 h after I-127-3) and `World_IsOnRoofImpl` (152 closures; one bit per row, ~0.25 h).

**Route creation in `Test`** (31 local definitions): a Sim's `CreateRoute` builds a native route and sets options on it. Writes go to an object the closure itself creates, so the w class may be too strict [inferred, I-127-2]; until checked these stay on the sim thread. **Closed in §7.2:** worker hook H12 (route stub, abort on query) opens 30 of the 31.

**The managed-write gap.** The docs/122 guard thunk aborts on unexpected ICs only. A static write in an override body (the `sNextId` case) runs silently on the worker. Mode R (docs/122 §4.3) compares results, not static fields, so it would not show a lost increment either. The allow-list therefore needs the typed closure of this note (or an equivalent check) before a definition goes in, not only the IC guard [inferred].

## 5. Mods

NRaas ErrorTrap and Overwatch were extracted from the user's Mods folder (read-only, `tools/extract-assemblies.ts`, copies in the session scratchpad, not in git) and loaded after the game assemblies for the class hierarchy only (`--mods`) [verified: tool output]:

- **27 overriding methods**, all on NRaas's own interaction and definition classes: `CreateInstance`, `Test`, `GetInteractionName` and `GetTargetLot` of Overwatch's replacement definitions (`GetMailEx`, `HarvestEx`, `AgeUpEx`, `GetSmeltEx`, `RobotScanEx`) and the shared NRaas `BaseInteraction`/`CommonInteraction` bases. None overrides an object, posture, service or component member.
- They are reached only when the scored definition (or created instance) is an NRaas type. The allow-list keys on type + EA assembly MVID (docs/122 §3.6, docs/104), so an NRaas definition never runs on the worker [inferred].
- **ErrorTrap ships its own `ScriptCore.dll`** (S3SA in `NRaas_ErrorTrap.package`) [verified: extraction]. The tool skips it (same name as EA's); the mod guard must key on MVID, not on assembly name.
- simler90's core mod (also in this Mods folder) replaces the gameplay assemblies whole; not scanned, covered by the same MVID rule (docs/103 §1).

## 5.1 Limits

- 5 hops: an override reached deeper than 5 calls from a root is not entered; the declared body is still followed to depth 12, as before.
- The receiver type is local to one call site, plus the `this` carry-over and the §2 sites; there is no flow across calls otherwise. A visit keeps the first path found per (method, class) pair.
- Base paths and scoring functions have no definition context; their typed result is close to CHA at the definition and target sites.
- Classes of the new ICs come from the docs/126 rule over the docs/62 scan, not from the disassembly (I-126-1 applies).

## 6. Ledger

docs/51 §171: I-127-1 (H11 branch conditions are the only way to the instance create on the scoring path), I-127-2 (route `Set*` ICs write only the new route), I-127-3 (a visit target `Lot` is never the world lot), I-127-4 (the `this` carry-over between chain methods). docs/51 §185: I-185-1 to I-185-10 (§7: the `Query_GetGlobalObjects` body, the world lot as visit target, row size and v0 answer, `GetObjectsOnLot` appending global objects, the route-building `Test`s, NoHit in autonomy, the `SetRouteOptions` UI path, H12 soundness). I-127-2 is now verified for `SetOption`/`SetAGS` and moot under H12; I-127-3 is refuted.

## 7. Closing I-127-2 and I-127-3

Static: capstone over the game copy of `TS3W.exe`, the decompiled class library (session scratchpad, not in git), what-if runs of `tools/autonomy-virtual-closure.ts`. No build, no game run.

### 7.1 `Query_GetGlobalObjects` (I-127-3)

**Native body** [verified: disassembly; callees named below not expanded]:

| Step | Where | What |
|---|---|---|
| Arguments | `IC_Query_GetGlobalObjects` `0x749DD0` | one: the `System.Type` (managed `Queries.Query_GetGlobalObjects(Type)`) |
| Collect | `0x749B40` | WorldManager `[0x11ECBC4]` -> container at `+0xB0` (shaped like a lot: object vector at `+0x14`, `0x532C50`); copies each object's 64-bit id (`+8`/`+0xC`) into a stack vector with room for 512 ids, growing on the EA heap (`0xD53FA0`, allocator `0x1003D90`, freed by `0x4E3F70`). No type filter, no lock |
| Filter and build | host interface `[0x11D4BBC]` slot `+0xC0` = `0xD798F0` (the same function `Query_GetObjects` uses, here with an id list) | class from the `Type` (`0xE70D50`); per id: host object maps `+0x1658`, then `+0x1678` (`0xD726F0`); keep it if the object is an instance of the class (`0xEA3820`) and the id is not in the host set `+0xBFC` (`0xD71F40`); result array from `0xEA30F0` (with host `+0xAC4`) |
| Not found | both | NULL (not an empty array) when there is no WorldManager or container, the class does not resolve, or no object matches |
| Locks | all three bodies | none: the container vector and the host maps are read while the sim thread may add, remove or move objects |
| Writes on read | all three bodies | none to game state; stack buffers, the EA heap above 512 global objects, the GC heap for the result |

**Managed side** [verified: IL]: `Sims3.Gameplay.Queries.GetGlobalObjects<T>()` turns NULL into `new T[0]`; `WorldLot.GetObjects<T>()` drops every `Sim` whose `LotCurrent` is not the world lot and copies the rest into a new array (`CountObjects<T>` counts the same set).

**The world lot is a visit target** (I-127-3 refuted) [verified: IL, unless marked]. `LotManager.AddLot` creates a `WorldLot` for lot id `0xFFFFFFFFFFFFFFFF`, and `Lot.OnCreation` adds `VisitLot` and `VisitCommunityLot` to `MetaAds` for every lot, the world lot included. The local part of `Autonomy.ScoreInteractionsForMetaAutonomy` scores `mActor.LotCurrent`, which is the world lot for a Sim on the street. `VisitLot+Definition.Test` -> `Lot.CommonVisitTest`: after the selectable-Sim, last-visit, community-lot and ownership exits, a residential target runs `FindCallbox` -> `WorldLot.GetObjects<Callbox>` -> `Query_GetGlobalObjects(typeof(Callbox))`. The world lot reads as residential because nothing sets its lot type (`mLotType` stays enum 0, `Residential`; the setters are lot-type change and edit-town paths) [inferred].

**Row design: learned type key per cut**, in the docs/122 §13 generic answer table with its learned-key mechanism:

- Entry: (IC `0x749DD0`, the `Type` pointer) -> the array contents or NULL, filled at the cut by calling the game's own body on the sim thread, so the answer is the game's answer, NULL included. References go in the rooted column; the worker builds the result with the docs/122 §14.3 array allocator.
- Keys: learned. A worker miss aborts the unit (`query`, type name logged) and adds the key at the next cut; unused keys drop after 100 cuts. v0 needs one key, `Callbox` [verified: IL]. The other paths (`GetObjectsInRoom<T>`, `GetClosestObject<T>`, `GetFrontDoor`, the tomb-room check, door rooms) bring their own types; the abort log lists them.
- Refresh: rerun a key's body only when the global container changed (object add/remove and world event `0x11`, the triggers the per-lot lists already use) [inferred: these cover every change of the container]; otherwise reuse.
- Size and cost: 16 B per key plus 4 B per matching object. The v0 answer is NULL (callboxes sit on lots) [inferred, I-185-4]. One body call walks the whole container: 2 map lookups and a type test per object, ~25-100 us at 500-1,000 objects [inferred, I-185-3; EA sized the inline buffer for 512].
- Fallback if more than ~8 keys show up: one world list of (id, reference) for the whole container (8 B per object, ~4-8 KB) with the `+0xBFC` exclusion applied at capture, filtered by class on the worker. No first-miss aborts, but a second answer path to keep bit-equal.
- Effort: ~0.5-1 h on top of the docs/122 builder (one descriptor entry, the generation trigger, the abort reason).

**Built 2026-10-21 (offline, ts3native 0.36.10, docs/122 §14.11):** descriptor row `Query_GetGlobalObjects`, `AO_CUT` (each learned type key reruns the game's body at every cut, not only on a container change: simpler, ~25-100 us per key per request cut [inferred]; the container trigger is the upgrade if the cut time shows it), abort `query` on a miss, NULL kept. Selftested offline (NULL and list keys, learned at the next cut, answers equal the body). The fallback world list was not built. Not run in game.

**Side finding** [verified: disassembly `0x749A94`-`0x749B28`]: `Query_GetObjectsOnLot`'s walker `0x7499B0` appends to the lot's own objects every object in the global container whose world component (`0x935770`, bit `[0x11ECF08]`) names that lot (Sims at least [inferred]). The docs/122 per-lot list must count them and bump its generation when such an object changes lot (world event `0x11` is already a trigger). With a room id other than -1 the room test for these objects compares a 0/1 flag against the room id (an EA quirk; `Lot.GetObjects<T>` passes -1).

**What the row unblocks** (with H11 and H12, what-if) [verified: tool output]:

| | Ready / new rows / blocked / hooked |
|---|---|
| H11 + H12 | 51 / 184 / 33 / 2 |
| H11 + H12 + `Query_GetGlobalObjects` row | **94 / 141 / 33 / 2** |

The row alone moves 43 closures to ready: **all 12 v0 definitions, both implementors that needed it (`Lot`, `BakeSaleTable`)**, 7 tier-2 meta (`CityHall+Protest`, the daycare and welcome-wagon visits, `VisitLotWith`, `VisitDiveLot`, `VisitCommunityLotWith`, `GoToImprovedProtest`; 35 of 37 meta then ready), 4 sampled local (`TV`/`VideoGameSystemBase` play definitions) and 18 row-5 candidates (mostly `Terrain` system and debug definitions, `SetFrontOfLot`). By scoring time: the pilot itself (v0 plus the meta base path, **640 samples, 62.0%**, docs/126) depends on this row and H11; the tier-2 meta part adds the 29 samples (2.8%) docs/126 counts for it.

### 7.2 Route creation in `Test` (I-127-2)

**What the `Test`s do with the route** [verified: decompiled class library, all 34 blocked by a `Route_*` IC, the 31 of §3.2 plus `MartialArts+TeleportDuringMeditation`, `SnowWaterFight+PlayWith` and `MagicCauldronRegency+TeleportPotion`]: each calls `CreateRoute()` itself, keeps the route in a local and calls `IsPointRoutable(point)` on it; `TeleportPotion` calls `PlanToPoint`. None reads an option back or stores the route. The route is fresh per call: `SimRoutingComponent.CreateRoute` -> `Route.Create` (cached `ConstructorInfo`, the static `sCtorInfo`, set once per session) -> `ScriptCore.Route(sim, flags)` -> `Route_CreateRoute` `0x75DA40` (new native route, registered in the route handle table under its lock, `0x74E390`) -> `SetRouteOptions`. It dies in the finalizer (`Route_DestroyRoute`).

**Native `Set*`** [verified: disassembly]: `Route_SetOption` `0x7543C0` and `Route_SetAGS` `0x755600` look the handle up (`0x74B2C0`: type tag `0x25`, script handle table lock, host slot `+0xCC`; NULL for any other handle), lock the route (vtable `+0x10`/`+0x14`), store into it (option block `+0x158` via `0x81C810`; `+0x3C`) and apply the same to the route's own sub-route list `+0xE0` (empty before a plan [inferred]). `SetFollower`, `SetValidRooms` and `AddObjectToIgnoreForRoute` start with the same lookup; their callees were not expanded. So the `Set*` writes stay in the route; `Route_CreateRoute` itself is not route-local (shared handle table, locked), and `Route_IsPointRoutable` `0x7538C0` runs the routing query `0x7525A0`, which reads the physics and lot quadtrees without a lock and flushes deferred inserts (docs/94 U4).

**Behind the route there is a second blocker** [verified: tool path]: `SetRouteOptions` reads Sim state and sets options only, but reaches the UI wrapper cache (`Stroller.GetStroller` -> `GameObject.IsOnHandTool` -> `UIManager.GetSceneWindow` -> `WrapperFromHandle` -> `DestroyWindow`, `UIManager` static dictionaries). A what-if that cuts only the native route (`Route.Create` and all `ScriptCore.Route` members) leaves all 34 blocked by that path. The stub must skip `SetRouteOptions`.

**Autonomy always tests with no hit** [verified: IL]: `InteractionObjectPair.TestAndCalculateScore` builds the parameters with `GameObjectHit.NoHit` (type None, point (0,0,0)); `Autonomy.OverrideInteractionParameters` carries flags only. Most of these `Test`s gate on the hit before they build a route:

| In autonomy | Definitions |
|---|---|
| never reach `CreateRoute` (19) | `DanceFloor+Dance` (fails when autonomous); `Sim+HorseGraze`, `Sim+HorseDig`, `PetSurfacePosture+LayDownHere`, `CatHuntingComponent+LocatePrey`, `BeachTowelPosture+EnterBeachTowel` (route only on the non-autonomous branch); `Terrain+SneakHere`, `+DogPeeHere`, `+CatPeeHere`, `SkipStone`, `LeafPile+Rake`, `BoatHere`, `Cane+HarassEveryone` (hit-type gate); `Terrain+WerewolfHuntTerrain`, `+HuntFor`, `EasterEgg+SearchForEggs` (route only when the hit type is not None); `Terrain+JogHere` (`SharedGoHereTests` false); `Snowman+MakeSnowman`, `+MakeSnowmanWith` (`SharedTerrainTests` skips the None case) |
| reach `IsPointRoutable((0,0,0))` (10) | `CatHuntingComponent+StalkForPrey`, `GhostHunter+ScanForSpirits`, `OccultVampire+VampireHunt`; `PutDownChildHere` (carrying a child); `Terrain+MakeIgloo`, `SnowAngel+MakeSnowAngel` (origin outside); `GhostHunter+ScanForGhosts` (job set up); `AlienUtils+SummonMeteor` (origin has a ground lot location); `MagicWand+CastFireBlastTerrain`, `+CastRestoration` (only if the lot at the origin is not the world lot, never in practice [inferred]) |
| create the route only (1) | `DanceFloorStore+DanceStore` (`IsPointRoutable` only for a real hit) |
| stay blocked (4) | `Jetpack+FlyHere` (`Simulator.Sleep` via `GetActiveJetpack`), `MartialArts+TeleportDuringMeditation` and `SnowWaterFight+PlayWith` (their docs/126 blockers), `MagicCauldronRegency+TeleportPotion` (plans with `PlanToPoint`; object creation behind it). None of the four builds a route under NoHit either |

Per-definition notes: `notes` column of `docs/data/autonomy-virtual-closure.csv`.

**H12, worker route stub** (new worker hook, design only): a ts3fixes hook on `SimRoutingComponent.CreateRoute()`, `CreateRoute(CASAgeGenderFlags)` and `CreateRouteAsAdult()`. Sim thread: pass-through. Worker: return a `ScriptCore.Route` built with its parameterless constructor (handle 0, no `Route_CreateRoute`; its finalizer skips `Route_DestroyRoute` for handle 0 [verified: IL]) and skip `SetRouteOptions`. Any route IC the `Test` then calls aborts the unit with reason `route` (the docs/122 guard already aborts every IC not on its list; it must not fall through to the body, which would answer `false` for handle 0, a wrong answer). Sound because the route never leaves the `Test`, no `Test` reads an option back, and its only consumer, the query, is never answered on the worker [inferred, I-185-10]. Effort ~30-60 min (one hook, reason `route`, counter slot per docs/registry.md) plus a Mode R check with `route` aborts by definition. A later row could answer `IsPointRoutable((0,0,0))` per actor (one query per Sim at the cut) if the aborts matter.

**H12 built 2026-10-21 (offline, ts3native 0.36.10, docs/122 §14.11):** ts3fixes `AutRouteHook` on the three methods (worker: `new ScriptCore.Route()`, no `SetRouteOptions`); native abort reason `route` (22, counters v5 `abortRoute`) for every `ScriptCore.Route::*` IC on the worker, never the body, guarded with `Guard=closure` too. Native side selftested; managed side build only. Not run in game; none of the 30 definitions is on the allow-list yet, so the stub only matters once row-5 definitions join it.

**Result** (what-if `--what-if "Sims3.Gameplay.Actors.SimRoutingComponent::CreateRoute,ScriptCore.Route::IsPointRoutable("`) [verified: tool output]: 30 row-5 candidates go from blocked to new rows (typed totals 23 / 161 / 84 / 2); the first remaining reason is `World_IsOnRoofImpl` (29) or `Query_GetGlobalObjects` (1), both rows already proposed. All 30 sit in docs/75 row 5 (61 samples, 5.9%, not splittable per type), so the share they open is at most 30/169 of it, **~11 samples, ~1.0% of scoring** [inferred: count split, as docs/126]. For 19 of them the route branch is dead in autonomy, so after H12 their time runs on the worker once the rows exist; the 10 that reach the query abort to the sim thread.

## Reproduce

```sh
node tools/managed-callgraph.ts --self-check --asm <repo>/game-copy/assemblies
node tools/extract-assemblies.ts <scratch>/mods "<Documents>/Electronic Arts/The Sims 3/Mods/Packages/NRaas_ErrorTrap.package" ".../NRaas_Overwatch.package"
node tools/autonomy-virtual-closure.ts --asm <repo>/game-copy/assemblies --index <repo>/game-copy/analysis/callgraph/index.json --mods <scratch>/mods > docs/data/autonomy-virtual-closure.csv
node tools/autonomy-virtual-closure.ts --asm ... --index ... --what-if "Sims3.Gameplay.Interactions.InteractionDefinition\`3::CreateInstance(,Sims3.Gameplay.Interactions.InteractionDefinition::CreateInstance(,Sims3.Gameplay.Interactions.IInteractionDefinition::CreateInstance(,Sims3.Gameplay.Seasons.SeasonsManager::IsShelteredFromPrecipitation(" > /dev/null   # H11 counts (stderr)
# §7: H12 alone; H11 + H12; H11 + H12 + the Query_GetGlobalObjects row (an IC cut stands in for the row)
node tools/autonomy-virtual-closure.ts --asm ... --index ... --what-if "Sims3.Gameplay.Actors.SimRoutingComponent::CreateRoute,ScriptCore.Route::IsPointRoutable(" > h12.csv
node tools/autonomy-virtual-closure.ts --asm ... --index ... --what-if "<H11 cuts above>,Sims3.Gameplay.Actors.SimRoutingComponent::CreateRoute,ScriptCore.Route::IsPointRoutable(" > h11h12.csv
node tools/autonomy-virtual-closure.ts --asm ... --index ... --what-if "<H11 cuts>,<H12 cuts>,ScriptCore.Queries::Query_GetGlobalObjects(" > h11h12g.csv
```

The committed csv was generated before `autonomy-tier2.ts` started labelling the docs/126 rows `a-row`; a rerun differs in 166 `new_ic_classes` cells by that label only (and in `mod_overrides` without `--mods`). The `notes` column was appended to the committed rows from a rerun.

Csv columns: `target_type`/`instance_type` (from the root `Test` and the generic base), closure sizes per run, `new_ics` and `new_ic_classes` (`name:class@depth`, `*` = in the snapshot set), blocking ICs, new yields/events, new static writes (`(pilot)` = the pilot union already reaches it), NRaas overrides seen in the CHA run, the old / typed / CHA verdicts and the first new reason with its path (`=>` marks a step into an override).
