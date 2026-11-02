# 208. Three more jobs for the `[AutShadow]` worker: environment score, broadcaster candidate sets, motive decay

docs/129 ranked what else the docs/122 worker could run once autonomy meta scoring works there. Rodney approved the top three (2026-10-30): the Sim environment score (~14 ms/s movable), the reaction broadcaster candidate set (~10) and motive decay (~7). This note closes each candidate's managed and native closure the way docs/121/125/127 did for autonomy, gives the Mode R and apply design per candidate, and records what was built (ts3native 0.36.132, ts3fixes `AutOffload.cs`) and what blocks the rest. Ledger: docs/51 §402.

Tags: **[verified]** = tool output over the call-graph index of the Steam 1.67 assemblies (`tools/offload3-closure.ts`) or our own code; **[inferred]** = reasoning, still to confirm in game.

## Summary

| Candidate | Closure verdict (driver roots, CHA 5 hops) | Built | Movable ms/s [inferred] | Blockers |
|---|---|---|---|---|
| Environment score (`SimUpdate.CalculateSimEnvironmentScore`, read part) | **ready with 2 new rows**: 0 yield, 0 event, 0 static write, only local field writes; 16 ICs: 9 pilot rows, `World_GetInformationAboutRoomImpl` (a-cut) and `World_TreesWithinRadialRangeImpl` (a) added as cut rows, 3 rare portal-object ICs abort | Mode R + dry checks, switch `autoffenv` (off); apply `autoffenvapply` (§8, off) | ~8-10 (of 18; events, reactions and buffs stay) | apply needs the worker window of docs/206 (Satb act) and requests timed to each Sim's next update (§2.5) |
| Broadcaster candidate set (`ReactionBroadcaster.GetInRadiusSims`, filters) | **ready**: 0 yield, 0 event, 0 static write; `World_CanSeeObjectImpl` (b) only behind line of sight and could-see, which stay on the sim thread; `Objects_GetHeightAboveFloor` (a, tomb branch) aborts | Mode R + dry apply rebuild, switch `autoffbcast` (off); Sim-position range answer and apply `autoffbcastapply` (§8, off) | ~4 with the range query row; ~9-10 with the Sim-position answer (§3.4, §8.4) | the range query is a cut row the sim thread fills (its cost moves into the cut, not off the thread); query order is not reproducible (§3.3) |
| Motive decay (`Motive.UpdateMotiveWithDecay`) | **blocked as a pure function**: writes the motive's own `Decay`, `Motives.EnergyGainRate`, temperature buff timeouts, the at-work fun delta; two posture overrides (`InSwingPosture`, `InWalkerPosture`) reach yields, events and destroys | design only | ~3-5 after the exclusions (§4) | per-motive input writes inside the same update loop; the time step is unknown at the cut; needs a clone per motive |

ts3native **0.36.132** (0.36.130 = the Satb branch, 0.36.131 left to the venue memo branch). ts3fixes counters 448-465. Queue `tools/harness/queues/offload3-moder.json` (smoke + 4 Mode R jobs, not run). §8 (2026-10-30): requests by due time, env and bcast apply, the Sim-position range answer; counters 481-490, queue `tools/harness/queues/offload3-act.json` (not run).

## 1. Method

- **Roots** are what each driver calls, not the whole game method: the writes (buffs, events, reactions, motive `SetValue`) stay on the sim thread by design. Roots per driver are listed in `tools/offload3-closure.ts` (`DRIVERS`).
- **Closure** as docs/127: depth 12, the pilot's cuts and worker hooks (`autonomy-tier2.ts CUT`), every game override of a virtual or interface call within 5 hops of a root (CHA). IC classes as docs/121/126 (`icClass`; `*` = already a pilot snapshot row). New in this tool: **instance field writes** of every closure method (`stfld`), because docs/129's "no static write" did not cover writes into other objects. Output: `docs/data/offload3-closure.csv` (candidate, kind = ic / field_write / static_write / event / yield, name, class, depth, path) [verified].
- Reproduce: `node tools/offload3-closure.ts --index <main>/game-copy/analysis/callgraph/index.json --asm <main>/game-copy/assemblies > docs/data/offload3-closure.csv`; `--self-check` checks that every root resolves.

| Run | Methods | ICs by class | Yield / event / static write | Instance field writes |
|---|---|---|---|---|
| env | 2,936 | a* 7, c* 2, b* 1 (`Slots_GetPositionOfSlotImpl`, pilot cut row), a 4, a-cut 1, a-row 1 | 0 / 0 / 0 (one static collection read: `LotManager.sLots`) | 28: lists, enumerators, exception constructors, `EnvironmentRoomInfo.roomData` (a local) |
| bcast | 321 | a* 6, c* 2, b* 1, a 2, **b 1** (`World_CanSeeObjectImpl` via `Sim.CouldSeeObject`) | 0 / 0 / 0 | 17: locals, lazy posture getters (`PetSurfacePosture.mContainer`, `SittingPosture.mContainer`, `BeachTowelPosture.mTowel`) |
| bcast-los (stays on sim) | 111 | b 1 (`World_CanSeeObjectImpl`), b* 1, a-row 1 | 0 / 0 / 0 | 7 |
| motive (whole) | 2,593 | w 9, a* 16, a 4, a-row 2, b* 1, c* 2, a-stub* 2 | 3 / 2 / 33 | 484 |
| motive-core (posture decay overrides cut) | 1,246 | a* 13, a 2, a-row 2, b* 1 (`Random_NextDoubleImpl` at depth 11), c* 2 | 0 / 0 / 2 (`Services.sAllServices` lazy fill, `RandomGen.gRandomGen`) | 119, real ones in §4.1 |

## 2. Environment score

### 2.1 What the game does [verified in the decompiled source, described, not copied]

Every 2 sim-minutes per toddler-or-older Sim (`SimUpdate.Simulate`, `mTimePassedEnvironment >= 2` or `UpdateEnvironment`), `CalculateSimEnvironmentScore` adds the lot's own score, then, unless the Sim is in a rabbit hole: **outside**, the range query for `GameObject` around the Sim, the environment objects of residential lots in range (each weighted by its tree count), and the outdoor objects of residential lots; **inside**, the room info (puddles, burnt tiles, size), the room's objects plus the lot's aromainators not in the room. Per object `CalculateObjectEnvironmentScore` (public static) collects positive scores and sums negative ones; `CalculateContributingObjectScore` (public static) sorts the positives and weights them by room size. Interleaved with the scan: per-object `SendObjectEnvironmentUpdateEvents` (dirty/broken events), per-Sim `DoSimEnvironmentUpdateInternal` (reactions, relationship flags, `AttractiveCompany`). After the scan: ~40 buff adds/removes, events, then `Motives.SetValue(Decoration, pos)` and `(Dirtiness, neg)`.

### 2.2 Closure [verified]

Driver roots: the lot and object `GetEnvironmentScore` (all overrides), `GetModifiedEnvironmentScore`, `EmitsLight`, `TraitManager.HasElement`, the black-light getter, `Lot.GetObjectsInRoom` / `GetObjects`, the positional `Queries.GetObjects`, `LotsWithinRadialRange`, `World.TreesWithinRadialRange`, `World.GetInformationAboutRoom`, `ComeAndSee`, `ConnectsToRoom`, `IsOutside`, the two public static helpers, and the Sim and lot getters. No yield, event or static write; the field writes are locals.

| IC | Class | How the worker gets it |
|---|---|---|
| `Query_GetObjectsOnLot` | a* (list row) | typed list rows (docs/122 §14.20) |
| `Query_GetObjectsInRange` | a* (cut row) | learned key, the body re-asked at each cut on the sim thread |
| `Query_GetGlobalObjects` | a (pilot cut row since 0.36.10) | learned key (world-lot rooms) |
| `World_IsPositionOutsideImpl`, `World_GetDoorRoomsImpl`, `World_GetRoomIdAtPosImpl` | a* | world rows |
| `Objects_GetPosition`, `Slots_GetParentIdImpl` | a* | object rows (learned on first miss) |
| `World_GetInformationAboutRoomImpl` | a-cut | **new cut row** (0.36.132): (ulong lot, int room, int def) -> 24-byte `RoomInfo` |
| `World_TreesWithinRadialRangeImpl` | a | **new cut row** (0.36.132): (ulong obj, Vector3, float) -> uint |
| `World_GetLotLocationAtFloorImpl`, `Slots_IsSlotEnabledImpl`, `Slots_GetRoutingSlotNamesImpl` | a-row / a | none: only `GetRoomConnections` overrides of portal objects (ladder, water slide, pool) reach them; the worker aborts `ic` |
| `Slots_GetPositionOfSlotImpl` | b* | pilot cut row (learned) |

Native rows (both `AO_CUT`, learned keys, filled by the game's body at the cut on the sim thread; never called by the worker): the argument layouts come from the managed extern signatures, the struct return uses the hidden result pointer like every struct row [inferred, I-402-1: the bodies were not read in the disassembly; `Audit=50` re-calls them and Mode R compares the answers]. `RoomInfo` is 24 bytes and `AsEnt.st` is 16: the answer runs on into `out[24]`, which an IC without out parameters never uses (`static_assert` on the layout). docs/131 §9: `GetInformationAboutRoom` can run the lazy terrain footprint update under `+0x918` when the lot level bit is clear; at the cut that is a sim-thread call like the game's own, so the risk is a wait inside the cut, not a race [inferred, I-402-2].

### 2.3 Mode R (built, `AutOffload.cs`, switch `autoffenv`)

- **Items:** the `CalculateSimEnvironmentScore` hook records each Sim into a 32-slot ring; the B13 request that starts a batch takes the newest 8 distinct Sims and adds their rows (Sim kind 1, its lot).
- **Driver `EnvScore`:** the read part above in the game's order through the game's own static helpers; outputs positive and negative score bits, flags (outside, window, light, empty room, residential) and the new-object count. Skipped: events, Sim reactions, buffs.
- **Mod guard:** every object and its parent must be of a game-module type whose `GetEnvironmentScore` is the game's and passes L3 (`OffTypeOk`, filled on the sim thread; the reference runs first at the same cut). A type first met by the worker aborts the item with reason 23.
- **Compare:** A0 (all bits equal), A2 (differ: an `off-env` record in `autshadow.jsonl`), A4 (abort, by native reason), A5 (stopped or overrun; KeepPartial rule per item), exceptions.

### 2.4 Dry checks (switch `autoffdry`)

At the Sim's next own `CalculateSimEnvironmentScore` (within 240 ticks of the cut): the driver runs live on the sim thread and is compared with the worker's cut answer (**staleness**, counter 455), then the game runs and its `Decoration` / `Dirtiness` writes are compared with the live driver (**apply equivalence**, counter 456: proves that the interleaved events and reactions do not change the score [inferred, I-402-3]).

### 2.5 Apply (design, not built)

The sim thread runs the game's method with the scan replaced: the worker's object list (in its order) for the events, the worker's Sim list for the reactions, the worker's score for `SetValue`, when the Sim's lot, room and the list generations of its lot (docs/124 §3.2 events) are unchanged since the cut; else the game's scan. Two things decide the gain: (1) the worker must have the next tick's span, not the ~10 us gap (docs/122 §14.20; docs/206 Satb act), and (2) the request must pick Sims whose update is due before the next cut (`mTimePassedEnvironment` close to 2), not the ones that just updated (the Mode R ring). Movable [inferred]: query and list work 22+11+7, scoring 10+5 of 95 samples (docs/129 §2.1) = ~58% of 18 ms/s = ~10 ms/s, less ~1-2 for the events loop the apply keeps.

## 3. Broadcaster candidate sets

### 3.1 Closure [verified]

Roots: `GetPulseRadius`, the positional query, `InWorld`, `RabbitHoleCurrent`, `IsBonehilda`, species, `TombRoomManager.IsObjectInATombRoom`, `RoomId`, `RoomCheck` (all overrides), `CouldSeeObject`, `CanReact`, `GetDistanceToObject`, `Posture`. No yield, event or static write. Class b only via `CouldSeeObject` (`World_CanSeeObjectImpl`), the same body as line of sight: both stay on the sim thread. Lazy posture getters (`PetSurfacePosture.get_Container` and two more) write their cache on first read: an idempotent fill, the same value from either thread [inferred, I-402-4]; the reference runs them first at each cut. `RoomCheck` overrides: only `PoolRoomCheckBroadcaster` in the game (skipped by the driver, reason 23); any other override (mods) also skips.

### 3.2 Mode R (built, switch `autoffbcast`)

The `GetInRadiusSims` hook records each pulsing broadcaster; the batch takes the newest 16 and adds the broadcasting object's row. Request (sim thread): radius from `GetPulseRadius`, species, room-only flag, autonomy level. Driver `BcCandidates`: the range query at radius + buffer + **2 m margin** (`kBcMargin`: the superset for a pulse up to a tick later), then the game's filters in its order except line of sight and could-see; output = the candidate Sims in query order and, per Sim, distance <= radius. A non-`Sim` type in the result aborts (mod Sim subclasses). Compare: A0 (same Sims, order and flags), A1 (same set, other order), A2, A4, A5. Worker and reference read the same cut answer for the same key, so A1 and A2 must be 0.

### 3.3 Apply and its equivalence (dry check built, apply not)

Apply as designed: the worker's candidates in its order; per candidate, live on the sim thread: line of sight and could-see when the params ask, then in the reacting set or distance <= radius. `autoffdry` rebuilds exactly that at the game's own call and compares it with the game's dictionary (ids and order; counters 464-465).

Why it can differ: (1) the game's set needs the live query; a Sim that entered radius + buffer + 2 m after the cut is missing (the margin covers ~1 tick of walking [inferred]); for a Sim in the reacting set the game keeps it whenever the query returns it (no distance test), which the rebuild approximates by the candidate list. (2) **Order:** the dictionary follows the native query's traversal order (footprint quadtree, docs/20 row 8), which depends on positions; the worker's order is the cut's. `PulseSims` processes up to `MaxSimsToProcessPerTick` in that order, so a different order changes which Sim reacts in which tick: equal in distribution, not path-equal (like docs/124 §5.2) [inferred, I-402-5]. The dry check measures both.

### 3.4 Where the gain is [inferred]

docs/129 §2.2: query 40 of 78 samples, line of sight 12, room check 7, list 4, tomb 3. `Query_GetObjectsInRange` is a cut row today: the sim thread calls the body at every cut for every learned key, so moving it only moves its cost into the cut. Movable now = the filters, ~4 ms/s BP. The ~10 ms/s of docs/129 need the original docs/122 §2.2 design for this IC: answer `Sim` range queries on the worker from the Sim position rows (squared distance plus a footprint margin), which is not exact against the quadtree's bounding-box test (I-122-8) and must be measured by the dry check before any apply.

## 4. Motive decay

### 4.1 Closure [verified]: not a pure function

- **Posture overrides.** `Posture.GetPostureMotiveDecayUpdate` has 14 game overrides; 12 are read-only, two are not: `BabySwing+InSwingPosture` (child nap/crying: `SACS_SetParamImpl`, `Simulator_DestroyObjectImpl`, sleep state, events, game speed writes) and `WalkerClassic+InWalkerPosture` (`ClearInvalidInteractions`: interaction cleanup with yields). A worker driver must skip any Sim whose posture chain holds one of them (`posture:*` rows in the CSV).
- **Own and shared writes** with those overrides cut (`motive-core`): `Motive.Decay` (written on most decay paths: fun, decay-to-zero, tuned decay, by-moodlet; `UpdateFun` compares the old `Decay` with the new one before it decides to touch the work interaction), `Motives.EnergyGainRate` (lazy fill from the bed interaction's tuning), `BuffInstance.mTimeoutCount` (temperature commodity, `SimTemperature.CalculateSimTemperatureChangeAndBuffs`), the at-work fun delta (`Occupation.UpdateFunMotiveAtWork`: `MotiveDelta` on the work interaction, plus the BFF lookup that draws RNG), `PetBed.SetActor` (pet bed satisfaction), lazy fills (`Posture.mInteractions`, `Genealogy`, `Services.sAllServices`).
- docs/129 §2.3's "no yield, no event, no static write, no delegate" holds for the base closure only; with overrides and instance writes it does not (I-129-3 refuted as worded).

### 4.2 Design that would be exact, and why it is not built

Worker: per motive, run `UpdateMotiveWithDecay` through a closed delegate on a **shallow clone** of the `Motive` made at the request (own-field writes land in the clone), only for commodities other than Temperature and Energy (before its rate is filled), not Fun while at work or school, and not for Sims with the two posture types. Apply at `Motive.Update`: use the worker's value only if (a) `hoursPassed` and `currentHours24` are bit-equal to what the worker used, (b) the motive's `Value` and `Decay` are unchanged, and (c) none of its inputs changed since the cut. Blockers:

1. **Time step.** `Autonomy.Update` computes `hoursPassed` from the clock at the moment the Sim's `SimUpdate` task wakes (`Simulator.Sleep(num3)`, num3 = 5 ticks or more by high-LOD count); the worker must guess it. A guess from the wake tick hits only if the task wakes on time and the speed does not change [inferred]; unknown hit rate.
2. **Order inside the update.** `Motives.UpdateMotives` runs `Update` per motive, and each `Update` ends with `UpdateMotiveBuffs`, which adds and removes buffs that the next motive's decay reads (fun reads `MissingWork`, `TooManyPeople`, `GoingWild`, `CryingBaby`; hygiene, bladder, energy read others). The worker computes every motive from the cut state, so motive k's value is valid only if motives 0..k-1 changed no buff it reads: needs a per-Sim buff generation (hooks on `BuffManager.AddElement` / `RemoveElement`) plus the read set per commodity [inferred].
3. **Inputs written between cut and update:** interactions change motives every tick (`ChangeValue`, `UpdateFromInteraction`), posture and interaction changes; check (b) catches value changes, (c) needs the same generation hooks.
4. **Cost.** A clone and a delegate per motive per request on the sim thread (~20 motives per Sim) eat a large part of the ~7 ms/s; after excluding temperature, energy and fun at work, the movable part is ~3-5 ms/s [inferred].

Verdict: not built. Revisit after env and bcast go live and the docs/206 window exists; first step would be a Mode R with clones to measure blocker 1 and 2 hit rates.

### 4.3 Riders from docs/209 #93 (static, 2026-10-30)

**`Motive.EarlyOutMotiveUpdate` (~2.3 ms/s [inferred: 12 of the 132 pooled `Motive.Update` samples, docs/129 §2.3]).** The first call in `Motive.Update`; it decides whether the motive skips its update this pass. Checks in order: lifetime-reward and trait maxima (Steel Bladder for bladder, Maintenance Master for a bot's maintenance), the static-needs cheat for selectable Sims, `AutonomyRestrictions.IsMotiveDecayEnabled` (static mood-contributor flag, the description's `MotivesDontDecay`, the autonomy level), and for fatigue the Athletic skill's "prevent fatigue" test (current interaction's definition type vs body builder / fitness nut). [verified, decompile]

- **The decision is pure; the method is not.** The two maxima branches and the cheat branch call `Motives.ForceSetMax` on the Sim before they return true (a write); `motiveDecayEnabled` is an out value the caller passes on. Every read is a field, trait lookup, skill lookup, current interaction or static flag; no IC, yield, event or RNG [verified, decompile].
- **Rider design.** The worker returns one code per motive: 0 = run the update, 1 = skip, 2 = force max then skip, plus the `motiveDecayEnabled` bit. The sim thread consumes it at `Motive.Update` entry and runs `ForceSetMax` itself for code 2. Staleness: traits (`HasTrait`), `IsSelectable`, the cheat static, autonomy level and the current interaction can change between cut and update; the fatigue check reads `CurrentInteraction`, which changes every few ticks. Exact by the same generation rule as §4.2 blocker 3 (trait add/remove, selectable change, interaction start/end bump a per-Sim generation; mismatch = run the game's check) [inferred]. Fatigue rows can simply stay on the sim thread (one commodity of ~20).
- **Gain.** ~2.3 ms/s is the whole method; with the fatigue row and the consume cost kept on the sim thread ~1.5-2 ms/s [inferred]. Rides on the motive clone path of §4.2 (same cut, same generation hooks); alone it is too small for its own build.

**`Curve.Fx` shared-field write audit (`Curve.m_x`).** `Curve.Fx(float)` wraps its argument for looping curves, stores it in the instance field `m_x`, then interpolates from the local value only [verified, decompile]. So two threads that call `Fx` on the same tuning curve (decay curves, auto-satisfy curves are shared per commodity tuning) race on a 4-byte store, but `Fx`'s own result never reads the field back. Readers of `m_x`: the `Curve.X` property and the no-argument `Fx()`; `managed-callgraph.ts callers` finds **no caller** of `Curve::get_X` or `Curve::set_X` in the game assemblies or in the 74 installed mod assemblies (`--mods`), and no source call of `Fx()` [verified, index + grep]. `MotiveSatisfactionCurve.TrySetXToValueForY` writes and reads `m_x` within one call and has no caller [verified]. Verdict: the hidden write is a dead store for every installed reader; harmless on the worker as long as no reader appears (guard: the docs/132 scan should flag any mod that calls `Curve.get_X` / `Fx()`) [verified for this install].

**Mod census for the new virtuals (docs/132 G-M5 style, `managed-callgraph.ts overrides --mods` over the 171 installed packages / 74 mod assemblies).** [verified, index; bodies read in the decompiled store objects, same type names as the store-update assemblies, inferred identical]

| Virtual | Overrides (game + mods) | Mod-assembly overrides | Effect on the worker |
|---|---|---|---|
| `Posture.GetPostureMotiveDecayUpdate` | 17 (13 game + 4 store-update assemblies; §4.1's "14 game" counted the two writers among them) | `BabySwing+InSwingPosture`, `WalkerClassic+InWalkerPosture` (both writing, already excluded in §4.1), `PlaypenClassic+InPlaypenPosture` and `BabyDragon+CarryingDragonPosture` (both return 0: read-only) | skip Sims with the two writing postures; the other two are safe |
| `GameObject.GetEnvironmentScore` | 52 | `MagicCauldronRegency`, `PartyStatue`, `PeaShooter`, `PlaypenClassic`, `SpellBook`: constants or a field/handle test, then base | read-only: safe for the env scan |
| `BuffInstance.OnTimeOutUpdated` | 12 | none | game set only |
| `ReactionBroadcaster.RoomCheck` | 1 | none | game set only |
| `InteractionInstance.GetTargetLot` | 27 | NRaas `BaseInteraction<T>` (Overwatch; `Target as Lot`, else base), `RoutineMachine+TeleportBase` (field or world lot) | read-only: safe |

No new mod override writes. The `AutShadow.AddObj` scoring-function check (docs/128) stays the runtime guard; a mod added later that overrides one of these five needs the scan rerun.

## 5. Counters, switches, files

| What | Where |
|---|---|
| Switches (agent `patches`, default off, need `autshadow` on) | `autoffenv`, `autoffbcast`, `autoffdry` |
| Counters 448-456 | env: items, A0, A2, A4, A5, exceptions, dry checks, dry equal, game equal |
| Counters 457-465 | bcast: items, A0, A1, A2, A4, A5, exceptions, dry checks, dry equal |
| Abort reasons | AppDomain `ts3fixes.autoffload.reasons` int[48] (env 0-23, bcast 24-47; native reason order, 23 = mod type); agent `autoff_reasons` |
| Records | `autshadow.jsonl`: `off-env` (score bits, flags) and `off-bcast` (counts, class), 100 per load |
| Native | `autshadow.cpp`: two `kAsDesc` rows at the end and a layout `static_assert`; no counter version change |
| Managed | `AutOffload.cs` (drivers, hooks, compare), `AutOffloadCore.cs` (`SeqClass`), 4 one-line calls in `AutShadow.cs` (`OffRequest`, `OffScore` x2, `OffFinish`) |
| Tests | `tools/selftest/autoffload/run.ts` (A0/A1/A2 rule, 7 cases); `tools/offload3-closure.ts --self-check` |

Monohost (`native/monohost`): no new host check. The host's synthetic driver has no game types (no `Sim`, `Lot`, `ReactionBroadcaster`), and the two new rows are ordinary `AO_CUT` rows the host's `AS-G-A2` already covers as a kind (cut rows, struct answers); the bigger-than-16-byte struct is the only new path, covered by the `static_assert` and by `Audit=50` in game [inferred].

## 6. Gates and queue

1. `offload3-moder.json` smoke (BP 4 min, both switches): items > 0 for both, A0 > 0, exceptions 0, no crash.
2. Env Mode R BP, IP: A2 = 0, A4 < 30% of items, exceptions 0, autonomy A1-A3 still 0. Read dry and game-equal shares.
3. Bcast Mode R BP, IP: A1 = A2 = 0, A4 < 30%; read dry equal (set and order).
4. Then, not before the docs/206 Satb act mode passes its own Mode R: apply builds (env first: game-equal >= 99% and dry-equal share decide; bcast after the Sim-position range answer).

Commands (after the off3 asi and package are parked in `game-copy/`): `node tools/harness/queue.ts tools/harness/queues/offload3-moder.json` (the smoke runs first), or per job `--only bp-autoff-env-r` etc.

## 7. Ledger

docs/51 §402: I-402-1 (row layouts), I-402-2 (`RoomInfo` fill at the cut can wait on `+0x918`), I-402-3 (env interleaving does not change the score), I-402-4 (lazy posture getters idempotent), I-402-5 (broadcaster order equal in distribution), I-402-6 (2 m margin covers a tick).

## 8. Apply and the Sim-position range answer (2026-10-30, ts3fixes `AutOffload.cs`, with ts3native 0.36.136, ledger §404, not run)

Built on the docs/206 Satb act mode (`[AutShadow] Satb=2`: the job submitted at the cut keeps running while the sim runs the next tick). Everything is behind new switches, default off: `autoffenvapply`, `autoffbcastapply`, `autoffnoref`. Mode R (`autoffenv`, `autoffbcast`) and `autoffdry` keep their meaning.

### 8.1 Requests: Sims whose environment update is due next

Mode R took the 8 Sims whose update had just run (the hook's ring), so their answer was a full period old at their next update. The `CalculateSimEnvironmentScore` hook now records, per Sim, the tick of its last update and its last period (ticks between two updates; `mTimePassedEnvironment` is private, and a reflection read per Sim per request would cost ~1 us each). The request takes the 8 Sims with the largest share of their period passed, at least half (`kEnvDueMin`), largest first (`AutOffloadCore.InsertTop`, selftest `tools/selftest/autoffload`). A Sim seen once has no period yet and waits for its second update. Cost on the sim: one dictionary pass per request (~50 Sims, no reflection) [inferred, ~0.1 ms/s].

### 8.2 Environment score apply (`autoffenvapply`)

- **Worker output** (`EnvRes`): besides the score bits and flags, the scan's sequence in the game's order: every other Sim the scan meets (reactions) and every object that gets environment events (outside: in-range objects on a lot, not environment objects, outside; inside: the room's objects plus the lot's aromainators), up to 256 (`kEnvSeq`; more = not applied); the room info (puddles, burnt tiles, size, covered floor share); the context (lot, room, position, flag 32 = scanned, not in a rabbit hole). Mode R compares all of it (A0 needs the same sequence by reference).
- **Ready store.** `OffFinish` copies each accepted answer (A0, or with `autoffnoref` every finished worker answer) into a sim-only store (`sEnvReady`, 16 slots; a newer cut replaces a Sim's older answer). The worker's own arrays are rewritten by the next job, which with Satb runs during the next tick; the old dry check read them directly, a race under Satb=2 that the store also closes.
- **At the Sim's update** (the hook): a ready answer for the Sim, at most 240 ticks old and unused, applies when the Sim is where the cut saw it: same lot (and residential flag), same rabbit-hole state, same inside/outside and room, outside within 1 m (`kEnvMoveMax`, the range query's centre). Then `EnvApply` runs `CalculateSimEnvironmentScore` with the scan replaced: the sequence in order on the sim thread with the live state (`SendObjectEnvironmentUpdateEvents`, `DoSimEnvironmentUpdateInternal` through closed delegates on the game's private methods, the game's own rabbit-hole / outside / servo checks per Sim; destroyed entries skipped), the inside branch's puddle event, empty-room early return, unfinished-room and darkness buffs from the stored room info, then everything after the scan in the game's order and the `Decoration` / `Dirtiness` writes from the worker's scores. Otherwise the game's method runs (`apply_stale`, `apply_none`).
- **Not checked** [inferred, I-404-1]: objects added, removed or changed on the lot between the cut and the update (no lot generation on the managed side). `autoffdry` measures it: at each applied update the live driver runs too, and `apply_dry_eq` counts applied answers equal to it.

### 8.3 Broadcaster candidates from Sim positions, and the apply (`autoffbcastapply`)

- **Range answer** (the docs/122 §2.2 design for Sim range queries): `BcCandidates` no longer calls `Queries.GetObjects<Sim>` (the native range query, a cut row the sim thread filled per broadcaster at every cut). It walks `LotManager.Actors` (a managed list, logged by Satb like any other) and keeps every in-world Sim within radius + buffer + 2 m (`kBcMargin`) + 1 m (`kBcFoot`: the native query tests footprint bounds, the answer has centres [inferred, I-404-3]) on the ground plane, from the Sims' position rows; then the game's filters as before. A mod Sim subclass inside that radius aborts the item. Worker and reference run the same driver, so Mode R A1 = A2 = 0 still holds; the order is the actor list's, not the quadtree's (I-402-5: equal in distribution).
- **Ready store** `sBcReady` as for env, keyed by broadcaster; it also keeps the broadcasting object's position at the cut.
- **At the game's `GetInRadiusSims`** (apply on): a ready answer at most 15 ticks old (`kBcApplyTicks`: what the 2 m margin covers of walking [inferred, I-404-4]) whose broadcaster moved under 0.5 m since the cut is rebuilt on the sim thread: per candidate, live line of sight and could-see when the params ask, then within the pulse radius, or reacting and within the query radius + 1 m; returned instead of the game's query. Otherwise the game's own call. With `autoffdry` the game's own call also runs: `dry_eq` = same Sims, `dry_order` (counter 490) = same Sims in the same order.

### 8.4 Modelled gain [inferred]

| | Moved off the sim thread | Stays on the sim thread | Net at 100% apply |
|---|---|---|---|
| env (of ~18 ms/s BP, ~17 IP) | range query, lot and room object lists, aromainator lists, per-object scores, tree counts, contributing score: ~58% (docs/129 §2.1 samples 22 + 11 + 7 + 10 + 5 of 95) | events and reactions over the sequence (~1-2 ms/s), the post-scan buffs (elevator check, `AddElementPaused`; ~2 ms/s as today), the due pick (~0.1), the cut rows the worker learns (room info, trees: filled at the cut, ~0.5) | **~7-8 ms/s** x the apply share |
| bcast (of ~16 ms/s BP, ~13 IP) | the range query (40 of 78 samples: no longer a cut row) and the filters (room, tomb, species, CanReact, list) | line of sight and could-see (~15%), the per-candidate distance and the dictionary (~1 ms/s), actor position rows at the cut (~60 Sims x ~1 us a cut, ~0.5 ms/s, mostly shared with autonomy rows) | **~9-10 ms/s** x the apply share |

With Mode R on, the reference at the cut repeats the worker's work on the sim thread; timing runs set `autoffnoref` (answers kept unchecked) and `autoffdry` off. The apply share is unknown until `offload3-act.json`: env `applied / (applied + apply_none + apply_stale)`, bcast likewise.

### 8.5 Counters, switches, queue

| What | Where |
|---|---|
| Switches (agent `patches`, default off) | `autoffenvapply`, `autoffbcastapply`, `autoffnoref` (plus §5's) |
| Counters 481-485 | env: ready (answers kept), applied, apply_none (due update, no answer), apply_stale (answer, context changed), apply_dry_eq (applied answer = live driver, with `autoffdry`) |
| Counters 486-490 | bcast: ready, applied, apply_none, apply_stale, dry_order (same Sims and order as the game) |
| Changed meaning | 465 `autoff_bcast_dry_eq` = same Sims (any order); 455-456 read the ready store |
| Queue | `tools/harness/queues/offload3-act.json` (smoke + env BP/IP + bcast BP/IP, Satb=2, Mode R + apply + dry; gated on `satb-act.json` and `offload3-moder.json` passing; not run) |
