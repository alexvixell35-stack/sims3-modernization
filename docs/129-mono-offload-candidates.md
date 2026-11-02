# 129. What else the `[AutShadow]` Mono thread can run: script work beyond autonomy scoring

Once the second Mono thread of docs/120 and the `[AutShadow]` pilot of docs/122 work, the worker, the cut, the snapshot and the guard thunks exist. This note asks which other **managed** sim-thread work could use them, and ranks it by sim-thread ms/s movable per hour of work. Static only: existing managed samples, `tools/managed-callgraph.ts` closures, earlier docs. No game run, no queue, no build. Per-candidate rows: `docs/data/mono-offload-candidates.csv`. Open claims: docs/51 §174 (I-129-1 to I-129-9).

Tags: **[measured]** = counted from existing managed samples; **[verified]** = tool output over the call-graph index of the Steam 1.67 script assemblies, or our own mod source; **[inferred]** = reasoning, still to confirm.

## Summary

- **Five candidates are worth it on the worker**, all much smaller than autonomy (docs/28 S2, ~50-64 ms/s in these runs):

| Rank | Candidate | Sim thread now BP / IP ms/s | Movable ms/s | Hours | ms/s per hour | Pattern |
|---|---|---|---|---|---|---|
| 1 | Sim environment score (`SimUpdate.CalculateSimEnvironmentScore`) | 18.3 / 17.4 | ~14 | 8-12 | 1.4 | read-only precompute, buff applied on the sim thread |
| 2 | Reaction broadcaster candidate set (`ReactionBroadcaster.GetInRadiusSims`) | 16.4 / 12.8 | ~10 | 7-10 | 1.2 | read-only precompute with a radius margin; line of sight and callbacks stay |
| 3 | Motive decay (`Motive.UpdateMotiveWithDecay`) | 8.4 / 7.7 | ~7 | 6-9 | 0.95 | shadow-then-apply per motive |
| 4 | Closest visible lot (`Camera.GetClosestVisibleLot`), after `kHoursMemo` | 6.0 / 7.5 | ~2.7 | 3-5 | 0.7 | shadow-then-apply |
| 5 | Zombies on lot (`Lot.UpdateNumZombiesOnLot`) | 1.5 / 7.5 | ~3.5 (IP 6) | 4-6 | 0.7 | read-only precompute (an in-place memo costs about the same) |

  Together **~37 ms/s movable for ~28-42 h** of agent work on top of a working `[AutShadow]`, plus ~2 game jobs each and shared clean nights [inferred]. ms/s are averages over the six 2026-10-17/18 async-on runs (§1) [measured]; movable shares and hours are [inferred].
- **Six more are possible but poor value** (≤ 0.4 ms/s per hour): service NPC checks, venue desired-Sim totals, environment look-ats, EA story progression, wishes, relationship decay.
- **Four are cheaper in place than on a thread:** our own `StuckWatchdog` (~6-8 ms/s, world-wide `Query_GetObjects`), occult info under the HUD update, spawner world counts, and `Lot.UpdateDetailPriority` (fix already built, off).
- **The rest cannot move** with this machinery: UI work (HUD, tooltips, map tags, mood lerp callbacks), CAS outfit building (one native SimBuilder plus the `ObjectDesigner` lock), native event callbacks and event listeners (order-dependent), role starts (they create Sims), motive buff triggers (writes; the docs/95 §5.5 in-place fix is exact), car traffic (creates objects, road placement). Lot scoring (docs/65) is native main-thread work: not a Mono candidate.
- **The mods (consumer only):** NRaas Overwatch's daily `CleanupInventory` (23% of ≥ 300 ms hitch time, docs/96 B) spends its time in `get_CatalogName` → `UserToolUtils_ProductGenericImpl`, a class-a session cache. The worker cannot fill it faster than the sim thread; the fix stays docs/96 B1/B2. NRaas StoryProgression was not in the sampled runs.
- **Why the ceiling is low:** the big managed owners are writes (interaction steps, buffs, outfits, events) or UI. What moves is the read side of a few per-tick scans. The 2% rule (CLAUDE.md, docs/23 #17) still makes each of them acceptable when it is stable; ranks 1-3 are real gains only if the cut rows they add stay well under the docs/122 budget (≤ 2 ms/s for the whole cut) [inferred].

## 1. Data and method

- **Cost.** Managed samples `probe-out/profile/<tag>-managed.jsonl` of the docs/95 async-on sets: BP `clean-night-2026-10-17-bp-env-on`, `clean-night-2-2026-10-18-bp-p10`, `-bp-p1b` (2,657 samples); IP `clean-night-2026-10-17-ip-env-on`, `-ip-p10`, `-ip-p1b` (2,660). Counted = non-empty stacks whose leaf is not a `ScriptCore.Route.*` frame (BP 1,558, IP 1,404). Share = counted samples with the frame anywhere on the stack (inclusive). ms/s uses docs/95's scale: 1% of counted samples ~ **5.8 ms/s (BP), 5.3 ms/s (IP)** [inferred there]. Unlike docs/95 the samples are not aligned to profiler seconds, so shares can differ from docs/95 §3.2 by a few tenths (for example `Lot.UpdateDetailPriority` 3.66% here vs 4.1% there). With 10-100 hits per row the error is ±10-30% of the share (Poisson). A throwaway node script (as in docs/74 and docs/95) did the counts and the caller/callee splits; nothing committed. Settings as docs/95 §2: story progression off, docs/74 hot-spot switches off, UI caches and `kBroadcasterThrottle` on.
- **Closures.** `node tools/managed-callgraph.ts query <root> --ics --depth 8 --cut Sims3.Gameplay.Seasons.SeasonsManager::LazyAllocateManager,Sims3.Gameplay.Socializing.Relationship::Get` per root (index from the main checkout's `game-copy/analysis/callgraph`). Depth 8, not 12: at 12 every closure reaches `Sim.Kill`, passport and save branches and the counts stop telling candidates apart. Counted per closure: methods, `YIELD`, `EVENT` (`EventTracker.SendEvent`), `STATIC_WRITE`, `DELEGATE` sites [verified: tool output].
- **IC classes** as docs/121: **a** = snapshot or cache (docs/121 CSV class, or a new-row IC of docs/126); **a-stub** = tooltip text; **b** = needs the sim thread (docs/121 class b, or `UNSAFE` in `docs/data/ic-safety.csv`); **c** = safe as is; **d** = unclear, needs a native closure first (PROXY in ic-safety, not yet classified); **w** = a write or command (`queued` proxy in ic-safety), which only the sim thread may do, so it becomes an apply step. Mono runtime ICs are left out (M5 handles them). Class d and w come from the docs/62 scan, so they are [inferred] until each body is read.
- **Patterns.** **P1 read-only precompute:** the worker computes a pure function of the snapshot; the sim thread uses it when the inputs' generation is unchanged since the cut, else runs the game's code (docs/124 §3 staleness model). **P2 shadow-then-apply:** as docs/124, the worker computes the result and the writes it would make; the sim thread re-checks and makes the writes through the game's own methods. **Not offloadable:** the work is the write, the UI, or arbitrary callbacks.
- **Hours** = agent build time on top of a working `[AutShadow]` (docs/122 parts 1-2 plus M5 in game): native closure of new class-d ICs (~0.5 h each), snapshot rows, the ts3fixes driver, the consumer hook with its generation check, Mode R compare. Game jobs (Mode R, then an ABBA A/B) are counted separately: ~2 per candidate, clean nights shared.

## 2. Ranked candidates

### 2.1 Sim environment score (rank 1)

**Cost [measured].** BP 3.15% (~18 ms/s), IP 3.28% (~17 ms/s), all under `SimUpdate.Simulate`. Callees: `Lot.GetObjectsInRoom` 22 of 95, `WorldLot.GetObjects` 11, `CalculateObjectEnvironmentScore` 10, `Lot.GetObjects` 7, `CalculateContributingObjectScore` 5, the elevator check 5, `BuffManager.AddElementPaused` 5. IC leaves: `Query_GetObjectsOnLot` 21, `Query_GetGlobalObjects` 11, `World_GetDoorRoomsImpl` 7, `World_GetInformationAboutRoomImpl` 2.

**Closure [verified].** 2,044 methods at depth 8; 3 yields, 3 event sends and 15 static writes. **Correction (docs/208 §2.1, docs/209 #93):** not all on the buff-add branch: per-object environment update events (dirty/broken) and per-Sim reactions (`DoSimEnvironmentUpdateInternal`: relationship flags, `AttractiveCompany`) run interleaved with the scan, per visited object and Sim [verified, docs/208 closure]; the buff adds (moodlet add → events, dreams manager flag) come after the scan. ICs: 20 a, 5 a-stub, 4 b, 14 d, 12 w. Near the root (depth ≤ 4): `Query_GetObjectsInRange`, `Objects_GetPosition`, `World_IsPositionOutsideImpl`, `World_GetLotLocationImpl`, sunrise/sunset (all a); `World_TreesWithinRadialRangeImpl`, `World_GetInformationAboutRoomImpl`, `World_GetPercentageOfWallsCoveredImpl`, `World_GetLotBeautifulVistaBuf` (d).

**Pattern P1.** The worker computes each high-LOD Sim's environment score from the snapshot (room objects, their environment values, room info); the sim thread compares the Sim's lot, room and the per-lot generation (docs/124 §3.2 object events) and then runs only the game's buff step with that score. Mode R can hold this bit-equal, as for meta scoring (the cut sees the same managed state). New snapshot rows: the four class-d room ICs and `Query_GetGlobalObjects` (5 native closures, ~2.5 h), all reads of room and lot geometry like docs/121 §2.2 [inferred].

**Movable** ~14 ms/s (the scan, ~80%; the buff step stays). **Hours** 8-12. Claim I-129-1.

### 2.2 Reaction broadcaster candidate set (rank 2)

**Cost [measured].** `GetInRadiusSims` BP 2.82% (~16 ms/s), IP 2.42% (~13 ms/s), the residual after `kBroadcasterThrottle`. Callees: `Queries.GetObjects` (radius query) 40 of 78, `Sim.CanSeeObject` 12, `RoomCheck` 7, list allocation 4, tomb-room check 3.

**Closure [verified].** 169 methods; no yield, event or static write. ICs: 8 a (`Query_GetObjectsInRange`, `Objects_GetPosition`, ...), 2 b (`World_CanSeeObjectImpl`: line of sight walks the unlocked physics quadtree that moving Sims change, docs/28 N14/N18), 1 d.

**Pattern P1, exact by superset.** The worker returns, per broadcaster, the Sims within radius + margin (the farthest a Sim moves in one tick) that pass the room, species and tomb filters; the sim thread re-checks distance on that short list, runs `CanSeeObject` and the enter/exit callbacks. Same answer as the game whenever the margin covers the movement, the docs/95 §5.4 argument [inferred]. It combines with §5.4's empty-radius early-out (which only skips empty pulses).

**Movable** ~10 ms/s (query and filters; LOS ~15% and the callbacks stay). **Hours** 7-10. Claim I-129-2.

### 2.3 Motive decay (rank 3)

**Cost [measured].** `Motives.UpdateMotives` is BP 5.58% / IP 6.20% (~32-33 ms/s). Under `Motive.Update` (132 samples pooled): `UpdateMotiveBuffs` 61, `UpdateMotiveWithDecay` 43, `EarlyOutMotiveUpdate` 12, `SetValue` 7. Decay alone: 43 of 2,962 pooled = 1.45% (~8 ms/s).

**Closure [verified].** `UpdateMotiveWithDecay`: 506 methods, **no yield, no event, no static write, no delegate**; 11 ICs, all class a (`World_IsPositionOutsideImpl`, `World_GetMoonPhaseImpl`, ...). The cleanest closure in this survey for the base closure only. **Correction (docs/208 §4.1, docs/209 #93):** with instance writes and posture overrides it is not pure: it writes `Motive.Decay`, `Motives.EnergyGainRate` (lazy fill), the Frostbitten / temperature buff timeout (`BuffInstance.mTimeoutCount`) and the at-work fun delta; two posture overrides of `GetPostureMotiveDecayUpdate` write and reach yields (`InSwingPosture`, `InWalkerPosture`) [verified, docs/208]. `UpdateMotiveBuffs`, by contrast, has 23 static writes, 3 event sends, 15 class-d ICs: it stays (fixed in place by docs/95 §5.5).

**Pattern P2.** The worker computes each motive's new value (decay rate, multipliers, temperature); the sim thread applies it in `Motive.Update` in place of the game's decay step only when the motive's value and modifier inputs (buffs, traits, posture, lot) are unchanged since the cut, else runs the game's step. The buff triggers run after it as today. How often a motive is touched between the cut and its update decides the hit rate, and is unknown (I-129-3).

**Movable** ~7 ms/s at a high hit rate. **Hours** 6-9.

### 2.4 Closest visible lot (rank 4)

**Cost [measured].** `Camera.Simulate` BP 1.03% / IP 1.42%; 31 of 36 samples in `GetClosestVisibleLot`, 20 of those in `Lot.IsOpenVenue` (the hours lookup that `kHoursMemo`, docs/74 §6.3, already memoises), 5 in the quadrant/distance math.

**Closure [verified].** 268 methods; 1 static write (`Camera.sCurrentFocusedGreetedLot`), 8 a, 1 d. **P2:** the worker picks the lot from the cut's camera position; the sim thread writes the static and acts. The camera moves every frame, so a memo cannot do this; a one-tick-old pick is invisible [inferred]. **Movable** ~2.7 ms/s after `kHoursMemo` is on. **Hours** 3-5. Claim I-129-4.

### 2.5 Zombies on lot (rank 5)

**Cost [measured].** IP 1.42% (~7.5 ms/s), BP 0.26%. Callees: `ZombieDanceTimeOut.UpdateTimeOutOnTheLots` 11 of 24, `Lot.get_HasZombieDanceStereo` 8. Supernatural-world work.

**Closure [verified].** 256 methods; 3 yields and 4 b ICs, all on the dance time-out branch; 4 a, 6 d. **P1:** per-lot zombie counts and stereo presence precomputed; the time-out branch stays on the sim thread. A per-lot stereo memo invalidated on object add/remove costs about the same and needs no worker, so this is a tie [inferred]. **Movable** ~3.5 ms/s average (IP ~6). **Hours** 4-6. Claim I-129-5.

### 2.6 Possible, poor value

| Candidate | ms/s BP / IP | Closure (depth 8) | Pattern | Movable | Hours | Why low |
|---|---|---|---|---|---|---|
| Service NPC checks (`Service.CheckForAssignment`, pool add/remove) | 6.0 / 13.2 | 925 methods, 1 yield, 2 events, 12 d, 3 b; CAS preset ICs on the NPC branch | P2 | ~4 | 8-12 | NPC creation and outfits stay; 12 class-d ICs to close (I-129-6) |
| Venue desired-Sim totals (`MetaAutonomyManager.CalculateTotalDesiredNumSims`) | 1.7 / 1.6 | 609 methods, 12 static writes, 5 a | P2 | ~1.3 | 3-5 | small |
| Environment look-ats (`LookAtManager.ProcessEnvironmentLookAts`) | 5.2 / 0.4 | 617 methods, 12 w (`LookAt_Set*`), 4 d | P2 | ~2 | 5-8 | the LookAt writes stay; mostly BP |
| EA story progression task | ~7 (docs/28 S11, older data) / 0.8 | 212 methods, 3 yields, RNG (b), destroy (w) | P2 | ~4 | 12-20 | off in the sampled runs, actions are virtual (closure open); measure first (I-129-7) |
| Wishes (`DreamsAndPromisesManager.Update`) | 4.1 / 6.0 | 1,134 methods, 12 static writes, 8 d | P2 | ~2.5 | 10-16 | event-driven node state |
| Relationship decay (`Relationship.DecayAll`) | 1.1 / 0 | 1,640 methods, 3 events, 18 static writes | P2 | ~0.4 | 6-10 | daily, tiny |

### 2.7 Cheaper in place

- **`StuckWatchdog` (ours, `mods/ts3fixes/src/RouteFixes.cs`)**: BP 1.41% / IP 1.14% (~6-8 ms/s) [measured]; 27 of 38 samples end in `Query_GetObjects` (class d, I-121-7) from `CheckIdle`/`Check`. Replacing the world-wide object query with the household and actor lists we already walk elsewhere is ~1-2 h and needs no thread; a worker version (P1, ~3-5 h) would only make sense afterwards if the rest is still visible. I-129-8.
- **Occult info under the HUD update** (`OccultManager.GetSimOccultInfo`, ~3 ms/s, docs/95 §4 residual): no IC in its closure, a perfect P1 shape, but a memo per Sim invalidated on occult changes is the same 2-3 h without the worker.
- **Spawner world counts** (`MinorPet.CanSpawnMoreMinorPetsInWorld`, `Wildflower.CanSpawnWildflowers`, ~3.8 ms/s): the count memo of docs/95 §4.
- **`Lot.UpdateDetailPriority`** (21 / 36 ms/s): `kDetailPriorityCache` (docs/74 §6.1) is built and off; after it, ≤ 2 ms/s is left.

### 2.8 Not offloadable

| Work | ms/s BP / IP | Reason [verified closure unless noted] |
|---|---|---|
| `HudModel.UpdateSimInfo` (rest), `MoodManager.UpdateMoodLerp`, `TooltipManager`, map tag visibility | 12.7 / 15.5; 4.1 / 3.4; 3.7 / 5.3; 4.1 / 3.8 | UI: window tree and layout ICs (32 UI ICs in the tooltip closure, 43 in map tags), `SimStateChangedCallback`/`MoodChangedCallback` into the HUD, `World_UpdateMoodStateImpl` (w). In-place throttles exist (docs/74) |
| `SimBuilderTask.UpdateSimOutfitsOnAgeUp`, `OutfitUtils` | 15.6 / 20.0; 22.7 / 24.5 (overlap) | 64 class-d CAS ICs plus `SimBuilderBuildModel`/`ProcessModel` (b): one native SimBuilder behind the `ObjectDesigner` lock (docs/28 S8, docs/21) |
| `LotManager.UpdateObjectLot` (native `OnObjectLotUpdate` callback) | 18.6 / 11.7 | lot membership writes in event order; `World_SetWallCutawayFocusPosImpl` (w) |
| `EventTracker.ProcessEvent` | 6.7 / 10.2 | listeners are arbitrary script, run in order; deferral on the sim thread is docs/73's route |
| `Motive.UpdateMotiveBuffs` | 9.7 / 13.2 | buff writes and events; docs/95 §5.5 exact in-place fix |
| `Role.StartRole` | 3.7 / 0.4 (7.9% of BP ≥ 100 ms slices) | creates and dresses a Sim; docs/95 §5.3 splits it over ticks |
| Car traffic (`UpdateTrafficTask`, `LiteDriver`) | ~19 / < 3 | object creation and `World_FindPlaceOnRoadOffScreen` on the route world (sampled leaves) |
| NRaas Overwatch `CleanupInventory` (consumer only) | 3.0 / 3.4 average, daily bursts | `get_CatalogName` closure: `UserToolUtils_ProductGenericImpl` (a, session cache filled on the sim thread); the mod only consumes the answer, so B1 (switch off) or B2 (catalog-name cache) of docs/96. I-129-9 |
| Lot scoring `0xC6C290` (docs/65) | ≤ 5.5 ms per second, main thread | native, not on the sim thread, not Mono |

## 3. What the candidates need from `[AutShadow]`

- **One worker, shared cut.** All five run in the same quiescent window after `MonoScriptHost_Simulate` returns (docs/122 §2.1) as extra jobs on `MtSubmit`. Autonomy keeps priority; a job that does not finish before the next Simulate is dropped and the game's code runs (docs/124 §4 fallback) [design].
- **Snapshot additions.** Rank 1: room info and global-object rows (5 ICs to close). Rank 2: none beyond the meta set (positions and the range query are already rows). Rank 3: none (all 11 ICs are in docs/121's class a). Ranks 4-5: one class-d IC each. The per-lot generation counters and object events of docs/124 §3.2 already give the staleness checks.
- **Drivers in ts3fixes** next to `AutShadow.ScoreMeta`, each a read-only copy of the game method's read part writing into its own result arrays (the docs/122 §3.5 rules: no shared pools, no yield, no stopwatch), plus a consumer hook on the game method that uses the result or calls `Orig`.
- **Gates per candidate:** Mode R bit-equal (A0) on the same cut, then an ABBA cost run; the 2% rule applies to the sum, so ranks 1-3 should ship together after autonomy go-live (docs/124 G9) [inferred].

## 4. Limits

- Only six runs, two worlds, story progression off, no NRaas StoryProgression; Sunset Valley and heavy-CC saves not covered. Shares of 10-30 hits are rough.
- Class d and w come from the docs/62 automatic scan; none of the new class-d ICs has been read in the disassembly for this note.
- Depth-8 closures can miss rare branches that depth 12 reaches; the Mode R gate is what catches them.
- Movable shares and hours are estimates; the generation-check cost on the sim thread is not modelled beyond "small".
- Not run: `TreeManager.DropLeaves` (seasons, ~3 ms/s), `SimUpdate` remainders (`IsInside`, `get_LotCurrent`) and `Sim.RunIdleLogic` (an alarm calling `RouteManager_IsOkToIdleAtCurrentLocation`, a routing IC).

## 5. Ledger

docs/51 §174.
