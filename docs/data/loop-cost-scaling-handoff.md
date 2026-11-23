# Loop-cost scaling packages: handoff (docs/333)

Read docs/333 §6 (packages, ON criterion, order), §7 (census) and §9 (answers) first. Format follows docs/332 §3. Every LC
package is built with its switch off. It is flipped ON in the same lane once three things hold: correctness passes, timing
shows no regression (our ms/s and hitch p99 / p99.9 / max), and the docs/333 scaling case shows a gain at 4x and 10x. A small
or zero gain at 1x is accepted (Rodney, 2026-11-23).

**Lanes (2+2 rule):**
- **Now:** LC-1 and LC-4 can run in parallel. They share no files except `UnseenViewers.cs`, and only LC-4 touches it.
- **After M-1 reports:** LC-5, then LC-7. Both edit DeadRefs.cs, so they run one after the other, or LC-7 rebases on LC-5.
- **After FO-4 and the FS T3 owner flag:** LC-6.

### M-1 counters-only measurement (2026-11-23, Opus agent)
- State: done (built, queued, not run)
- Changed: mods/ts3fixes/src/LoopMeasure.cs (new); hook lines in DeadRefs.cs and UnseenViewers.cs; LoopCostCore.cs `MeasureBand`
  and `MeasureSampled`; tools/selftest/loopcost; agent names in PatchHandlers.cs; queue loopcost-measure-1123.json
- Branch / commit: feature/loopcost-measure-1123 daf7ed277 (off feature/ts3fixes-loop-cost-1122)
- Build: ts3fixes.package.loopcost-measure-1123 bf881a6b687f (tuning = -1122d); game-copy/ts3fixes.package.loopmeasure1,
  agent game-copy/ts3agent.package.loopmeasure1
- Switches: none
- Counters / slots: 1590-1657 (fixes-1590), `lcm_*`
- Tests: selftest-all 309 PASS / 1 FAIL (mts-helper chrome port race, fixed on master 240de422d); queue dry-run ok
- Household: read-only
- 4x / streamed world: counters only; the band pass is sliced inside p10
- Next: when chain-loopcost-measure-1123 ends, fill docs/333 §9 (per-part split, q per world, gone_without_event, worst-slice
  phase) and settle LC-1 (b, c) and LC-5's floor

### LC-1 exact cuts (inputs for the builder)
- Partition: one sliced loop over the snapshot into `Sim`, `Fish`, `CarOwnable` / `BoatOwnable`, `HorseshoeCourt`,
  `Dartboard` lists. Same items and per-type order as the 5 `DrOfType` calls (DeadRefs.cs phases 5-9 gathers).
- One `ObjectDisposed` read and one invocation list shared by `HandlerBody` and `CookingBody`; re-read only if the handler body
  changed the field.
- `ShortcutOn("deadrefcooking")` and `PatchCounters()` once per slice.
- Switch: a new sub-switch, default off. ON per the criterion; scaling case ~5-7 ms/s at 4x, ~12-17 at 10x.
- HH: R (same items).

### LC-4 viewer push without Actors walks
- Ride set by hooks:
  - `RidingPosture.StartRiding` (RidingPosture.cs:454);
  - `Mount.Run` (Sim.cs:40470), `Dismount.Run` (Sim.cs:40636), `BeingDismounted.Run` (Sim.cs:40777), each one `Run` with no
    EA subclass;
  - seed with one Actors walk at load (saved rides may skip the setter), and keep a 10 s Actors walk as a floor for mods.
- Positions: ts3native reads household Sim positions by id at its consumers (`0x939560` object by id, `0x959000` transform
  position, as in native/ts3native/unseen.cpp). The managed side sends ids on membership change.
- Shadow compare: the hook ride set against the walk-every-wake set.
- Switch: new, default off. ON per the criterion; scaling ~10-12 ms/s at 4x, ~30 at 10x (~120 with rides in progress).
- HH: H. Household positions are read at every consumer; unknown = household.

### LC-5 reverse index (after M-1)
- Index Sim -> objects at the choke points (docs/333 §7.1):
  - `GameObject.SetStealActors` / `SetRewardGiver` and `ObjectOwnerComponent` import (we already hook `SetRewardGiver`,
    EaFixes.cs:113);
  - the `GameObjectRelationship` ctor (`ObjectDisposed` subscribe);
  - `CookingProcess.AddObjectListeners` / `ClearObjectListeners`.
- Seed with one walk at load.
- Use lists keep a slim walk: three list reads, gone test against the Sims destroyed since the last pass. They have 27 direct
  writes in EA scripts, so no index.
- No per-lot dirty set: cross-lot holders are common (§7.2).
- Floors stay: 4 h idle, 24 h full walk (`kDeadRefObjHours`). Size them from M-1 `gone_without_event`.
- Switch `deadrefdelta`, default off. ON per the criterion; scaling ~25-30 ms/s at 4x, ~65-80 at 10x.
- HH: R (same removals).

### LC-7 description phases (after M-1, after or rebased on LC-5)
- Reverse lookups at the writers (docs/333 §7.3):
  - `Occupation.AddCoworker` / `RemoveCoworker` / `ClearCoworkers` + ~6 direct sites;
  - `Career.SetBoss`, plus `Occupation.ImportContent`;
  - the `LogicSkill` chess challenger;
  - `Pregnancy` dad (4 sites).
- p4 visits only descriptions that reference a disposed one; the 4 h full scan stays (public persisted fields).
- Switch: new, default off. ON per the criterion; scaling ~5-6 ms/s at 4x, ~13-15 at 10x.
- HH: R.

### LC-6 far-model hooks (after FO-4, FS T3, LC-5)
- The slim use-list walk skips parked lots and model-owned Sims, using per-lot lists for live lots instead of the world snapshot.
- docs/331 §6.1 gets the rule "no use, reference or routing list on the lot holds any Sim" as a park condition.
- Repro: T4 demotion of a Sim that was listed on a since-parked lot.
- HH: H (household lots never park).
