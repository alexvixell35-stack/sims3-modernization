# 346. Alarm hold hook and alarm callback census (ST-4, ST-7a) (2026-11-26)

Static only, Steam 1.67 decompiled script assemblies in `game-copy/decompiled` (local) and the managed call graph index
`game-copy/analysis/callgraph/index.json`. Nothing built, nothing run. Packages ST-4 and ST-7a of docs/332 §2.2 /
docs/340 §6. Ledger: docs/51 §614. Handoff: `docs/handoff/st4-st7a-1126.md`. Census data:
`docs/data/alarm-callback-census.csv` (1,339 rows).

Related, not repeated here: docs/341 §1 and H6 (Sim alarms go to the global manager; equal-tick alarms fire in heap
order), docs/331 §3 and §4.0 (how EA advances non-Sim state, timer table), docs/324 §5.2 (decision 7: alarms on a far
Sim wait until it goes live), docs/343 (sidecar format).

## 0. Short answer

- **Owner.** Every alarm is a private `AlarmManager.Timer` with a public `ObjectRef` field of type `IAlarmOwner`
  [verified]. There is one `AlarmManager` per lot; `AlarmManager.Global` is the world lot's manager [verified]. A Sim's
  own alarms always sit on the global manager with `ObjectRef` = the Sim; an object's alarms sit on its lot's manager
  and follow the object when it changes lot [verified].
- **Dispatch.** One site: `AlarmManager.Simulate()`, called only from `Lot.Simulate()` (one IL caller) [verified]. It is
  all managed code on the sim thread, inside the lot's own script task. No native hook is needed and none is proposed.
- **Hold hook.** One managed prefix on `AlarmManager.Simulate` plus a bracket on `AlarmManager.Write`, with one owner
  table (`FarHold`, the ST-15 file). Two cases under the same hook: a parked lot's manager is not simulated at all
  (its queue stays untouched); on any other manager, due timers whose `ObjectRef` is a held Sim (or an object on a
  parked lot) are moved to a side list and put back at go-live, where EA's own dispatch fires them once each. §3.
- **Census.** 1,339 registration sites in the shipped packs (804 Sims3GameplaySystems, 426 Sims3GameplayObjects,
  109 Sims3StoreObjects), 1,152 distinct callbacks in 561 top-level classes. 513 land on a lot manager, 413 are owned
  by a Sim, 374 are on the global manager with another owner or none, 40 have an owner the scan could not type. §4.

## 1. Alarm owner and dispatch [verified unless marked]

File:line is the local decompiled Steam 1.67 source.

| Fact | Where |
|---|---|
| `Timer` (private nested, persistable): due date, `CallBack`, `RepeatLength`, `Handle`, `ObjectRef` (`IAlarmOwner`), one flag byte (repeating, yield required, cleared, unit, alarm type) | `AlarmManager.cs:17-180` |
| Three stores per manager: `mTimerQueue` (binary heap keyed by due tick), `mTimers` (handle -> timers), `mGameObjectIndex` (owner -> handles) | `AlarmManager.cs:232-236`; heap: `Sims3.Collections/PriorityQueue.cs` |
| One manager per lot, made in the lot's creation and kept in the lot's saved data; the manager whose lot id is the world lot's becomes `AlarmManager.Global` and takes over the old global queue | `Lot.cs:69`, `1744-1752`, `3786`, `3813`; `AlarmManager.cs:254-282` |
| All adds go through `InternalAddAlarm` (6 IL callers: the three public `AddAlarm*` bodies, `MoveAlarms`, `Read`, the lot id setter) | `AlarmManager.cs:302-322`, `378-445`; call graph index |
| `GameObject.AddAlarm*`: an owner with no lot, or a Sim, uses the global manager; any other object uses its lot's manager; `ObjectRef` is the object | `GameObject.cs:7502-7556` |
| `Situation.AlarmManager` is the global manager, so a bare `AlarmManager.AddAlarm(...)` in a situation class is global | `Situation.cs:71` |
| An object that changes lot takes its timers along (clone into the new manager, old ones cleared); Sims are skipped | `AlarmManager.cs:737-763`; `GameObject.cs:5920-5924` |
| Remove paths: `RemoveAlarm` (by handle, also destroys a not-yet-started yielding task), `OnDispose(owner)`, `OnReset(object)` (drops `DeleteOnReset` timers), `InternalRemoveAlarm`. All of them find timers through `mTimers` / `mGameObjectIndex` and mark them cleared; only `InternalRemoveAlarm(t, true)` walks the queue | `AlarmManager.cs:324-364`, `448-491`, `627-703` |
| `UpdateAlarmTime` and `SetNewAlarmTime` clone the timer with the new due time, queue the clone and clear the old one | `AlarmManager.cs:512-541` |
| **Dispatch**: `Simulate()` pops the heap head while it is due (due = strictly earlier than now). Cleared heads are dropped; a callback whose target has no proxy drops the timer; a repeating timer is moved to the first period boundary not earlier than now and re-queued **before** the callback runs, so it fires once however late it is; a one-shot is removed before its callback runs; a yielding timer becomes an `AlarmOneShot` task (`Simulator.AddObject`), others are called inline; an exception removes a repeating timer; `ResetException` is passed on | `AlarmManager.cs:543-625` |
| `Simulate()` has one caller, the first statement of the endless loop in `Lot.Simulate()` (a threaded script object). Active, world, active-Sim and commercial lots loop every tick, others sleep 30 ticks | `Lot.cs:8309-8355`, `784`; call graph index (1 caller) |
| Save: `Write` walks `mTimerQueue` and writes every timer that is persisted (`AlwaysPersisted` or `DeleteOnReset`) and has a callback. `Read` re-adds them and rebuilds both indices | `AlarmManager.cs:907-941`, `997-1010` |
| Load: `FixLoadAlarms` moves overdue heads forward (repeating ones by whole periods) | `AlarmManager.cs:1012-1045`; `SimClockUtils.cs:78`, `148`, `155` |
| Types that implement `IAlarmOwner` besides `GameObject`: `SimDescription`, `Household`, `Situation`, `Service`, `Role`, `Gig`, `VenueFlowUtility`, `AttractionNPCBehaviorController`, and 13 more (career, buff and object helper classes) | grep of the interface in the three gameplay assemblies |

Thread and locks: the sim thread, inside the lot's script task; no lock. A callback that sleeps suspends that lot's
task in the middle of the dispatch loop, so the manager can be entered by other code (adds, removes) while one of its
`Simulate` frames is suspended [verified from the loop shape; this is why NRaas Overwatch marks one-shot alarms as
yielding, docs/318].

Two EA details that matter for the design:

- The heap is not stable: equal-tick timers fire in heap order, which depends on every earlier add and remove
  (docs/341 H6). No hold can keep that order bit for bit, because withholding one timer changes the heap shape.
- `OnReset` is only called on the object's **current lot** manager (`GameObject.cs:4002`, one IL caller). A Sim's
  timers are on the global manager, so a Sim standing on a normal lot keeps its `DeleteOnReset` alarms across a reset
  [verified code path; effect inferred, I-614-1]. 146 census sites register a `DeleteOnReset` alarm on a Sim owner.

Other hooks on these methods: ts3fixes patches `AlarmManager.InternalRemoveAlarm` (`AlarmKillLog.cs:30`) and
`Timer.get_YieldRequired` (`GameSpeed78.cs:376`); nothing patches `Simulate` or `Write` [verified grep of
`mods/ts3fixes/src`]. S3SS and S3PP are native and do not touch managed alarm code; the native hook map
(docs/68) is not affected. NRaas reads `mTimerQueue` directly in Overwatch's alarm marking
(`NRaasOverwatch/OverwatchSpace/Loadup/YieldAllAlarms.cs:45-49`) and in a DebugEnabler dump; neither replaces a
method [verified in the local NRaas clone, cited only].

## 2. What a hold must not break

Checked against §1, for a timer that is taken out of `mTimerQueue` but left in `mTimers` and `mGameObjectIndex`:

| EA path while the timer is held | Result |
|---|---|
| `RemoveAlarm`, `OnDispose`, `OnReset` | timer is marked cleared through the indices; the side list drops cleared entries. Same as vanilla. |
| `UpdateAlarmTime`, `SetNewAlarmTime` | a clone with the new time is queued, the held original is cleared. The clone is held again if it comes due while the owner is still far. |
| `MoveAlarms` (object changes lot) | clones go to the new manager, held originals are cleared. |
| `GetTimeLeft` | reads `mTimers`; an overdue timer answers 0, as in vanilla. |
| `SetAlarmType`, `AlarmWillYield` | act through `mTimers`; unchanged. |
| `Write` (save) | walks the queue only: **held timers would be missing from the save.** Needs the bracket in §3.5. |
| Overwatch alarm marking | walks the queue only: a held one-shot is not marked while held; it is marked by a later pass after release, or fires inline as in plain EA. Accepted (docs/324 §5.2 already accepts the inverse). |
| Yielding tasks already started | not touched. Demotion must wait until the owner has no entry in `mAlarmOneShotList` [design rule]. |

For a parked lot whose manager is simply not simulated, nothing above changes at all: the three stores stay as EA
left them.

## 3. The hold hook (one design for far Sims and far objects)

### 3.1 Parts

- **Owner table** `FarHold` (ts3fixes, file named by ST-15): a set of held Sim object ids and a set of parked lot ids.
  Only the far-Sim model (FS) adds Sim ids and only the far-object model (FO) adds lot ids; both remove through
  `FarHold.Release`. Runtime only, never saved.
- **Hook A**: prefix on `AlarmManager.Simulate()` (same `Patch` form as the existing `Simulate` patches in ts3fixes).
- **Hook B**: prefix and postfix on `AlarmManager.Write(...)`.
- Switch `farhold` (default off), read once; with the switch off or the table empty Hook A is one static read and a
  call of the original.

### 3.2 Hook A, per call

1. Switch off, table empty and no side list anywhere: call the original, done.
2. **Parked-lot case.** The manager belongs to a lot whose id is in the parked set and the lot passes the exemption
   check (§3.6): return without calling the original. The queue, both indices and every timer stay untouched.
3. **Release.** If this manager has a side list, every entry whose owner is no longer held goes back with
   `mTimerQueue.Add(dueTick, timer)`, in the order (due tick, handle id, position in the handle's list). Cleared entries
   are dropped. The due tick is the one the timer had when it was taken; nothing on the timer is changed.
4. **Take.** Walk the due part of the heap read-only (the due timers form a connected subtree from the root, because a
   child is never earlier than its parent): for each due, not cleared timer read `ObjectRef` and ask `IsHeld` (§3.3).
   Each held timer is removed from the queue with `RemoveAt(index)` and appended to the side list. Timers that are not
   held are not moved.
5. Call the original. It fires what is left, exactly as EA does.

A timer is taken once, when it first comes due; a repeating timer is not re-queued while held, so a held owner costs
nothing per tick after that. Timers of a held owner that are not due stay in the queue and are saved normally.

### 3.3 `IsHeld(ObjectRef)`

| `ObjectRef` | Held? |
|---|---|
| `null` (no owner) | **never**. Scope unknown = household. |
| a `Sim` | only if its id is in the held-Sim set **and** `Fixes.HouseholdExempt(sim)` is false. A household or selected Sim found in the set is removed from it and logged. |
| another `GameObject` (alarm put on the global manager by hand, or an object without a lot) | only if its current lot is in the parked set and passes §3.6. An object with no lot (inventory, out of world) is **not held** in v1 (open question Q2). |
| anything else (`SimDescription`, `Household`, `Situation`, `Service`, `Role`, helpers) | **never** in v1. These are system alarms; the far models must tolerate them (docs/324 §5.2: global alarms that only read the Sim are not held). |

A pass-through set keyed by callback (declaring type + method) overrides a "held" answer for callbacks that must fire
on time (E1 of docs/331 §5). It ships empty; candidates are the census rows flagged "shows UI" (§4.3).

### 3.4 Release and catch-up

Release is always "put the timer back and let EA dispatch it". That gives, with no code of ours in the firing path:
one fire per held timer however long the hold was (EA's own late-repeat rule, §1), time order across all owners of
that manager, the proxy check, the yielding-task path, EA's exception handling and Overwatch's marking.

- **Far Sim (docs/324 decision 7):** FS calls `FarHold.Release(simId)` at promotion. The held alarms fire on the next
  global `Simulate`, which runs every tick (world lot), in the global manager's own task as in vanilla. FS orders
  promotion so that this happens before the Sim's first live tick; the spread queue paces how many Sims release per
  tick.
- **Far lot (docs/331 §6.2):** before `FarHold.Release(lotId)`, FO applies its computed model through EA's public
  handle API, per timer class: leave it (EA fires it once), `UpdateAlarmTime` (re-arm to the next period without a
  fire), `RemoveAlarm` (the model has produced the effect). Then the lot is released and its next `Simulate` runs.
  Generic replay (k fires, docs/331 §4.0 last row) is FO's own call of the callback before release; the hook offers
  no replay.
- **Order:** lot first, then its Sims at promotion; Sims first, then the lot at demotion (docs/331 §6.5). Within one
  manager the order is (due tick, heap order). Two runs from the same save with the same hold decisions produce the
  same side lists and the same re-insert order [inferred, I-614-3]; equal-tick order against **vanilla** is not
  promised and cannot be (§1).
- **Long holds:** EA's late-repeat loop steps one period at a time (`AlarmManager.cs:575-581`), so a 1-minute repeat
  held for a sim-week costs about 10,000 loop steps at release. FO should re-arm long-held short-period timers with
  `UpdateAlarmTime` before release [inferred cost, I-614-4].

### 3.5 Save, load, world change

- **Hook B** puts every side-list timer of the manager back into the queue before `Write` and takes exactly those
  back out after it. The save then holds plain EA alarms with their original due times, and a suspended dispatch loop
  cannot fire them in between (`Write` does not yield [inferred, I-614-5]). Parked lots need nothing: their queue was
  never changed.
- A load starts with an empty table: every alarm is EA's again and `FixLoadAlarms` treats the overdue ones as it does
  in vanilla. No mod data in the save, no cleanup step needed.
- World change, travel, Edit Town, quit to menu: `FarHold.ReleaseAll()` (puts everything back) from the shared flush
  hub the far models already need (docs/343, `DisposeSaveHook`); a manager that is disposed with a side list just
  drops it.

### 3.6 Household exemption (explicit)

- A Sim for which `Fixes.HouseholdExempt` is true is never held, whoever put it in the table. A Sim whose scope cannot
  be read counts as household.
- A lot is never treated as parked when it is the world lot, the active lot, the active household's home lot, or a
  lot with a household or selected Sim on it; Hook A re-checks the first three on every call and FO owns the fourth
  (a household Sim entering a parked lot promotes the lot first).
- `ObjectRef` null or of a non-object type is never held.
- `[Experimental] HouseholdToo=1` does **not** lift any of this: a hold is a skip, not a realistic-only site.

### 3.7 Gates and cost

- No sim-state work in pause, CAS, build mode or menus: Hook A only runs when EA runs `Lot.Simulate`; `ReleaseAll`
  runs from the flush hub.
- Leak rule: side lists are dropped on manager dispose and on `ReleaseAll`; counters for held timers, takes,
  releases, exemption evictions, and the largest side list (slots to be reserved by the builder).
- Cost per global `Simulate` with holds active: one read of the heap head when nothing is due; otherwise one
  `ObjectRef` read and one set lookup per due timer [inferred; to be measured by FO-1's probe, which uses the same
  read-only walk].
- Build estimate: Hook A + B + table + selftests about 60 minutes for one agent; one correctness queue (about 15
  minutes of game time) with a forced far Sim and a parked test lot. Game runs listed separately by FS-8 / FO-3.

### 3.8 Why not the alternatives

| Alternative | Why not |
|---|---|
| Skip the callback at the call inside `Simulate` | The call is a delegate invoke inside EA's loop; EA has already removed a one-shot or re-queued a repeat by then, so a skipped alarm would be lost. |
| Push the due time of held timers forward | Changes persisted timer data; a save made while held would not be plain EA state. |
| Pop every due timer, re-add the ones not held | Re-adding household timers changes their heap position; the read-only walk moves only held timers. |
| Hook `Lot.Simulate` for lots | Also skips door privacy, detail priority and reactions; that is FO-3's lot skip, a separate decision. The alarm part alone is the same test in Hook A. |
| Native hook | The dispatch is managed end to end. |

## 4. Callback census (ST-7a)

### 4.1 Method

Text scan of the decompiled sources of the three gameplay assemblies (UI, ScriptCore, SimIFace and Sims3Metadata have
no site): every call of `AddAlarm`, `AddAlarmRepeating`, `AddAlarmDay`, `AddAlarmDayNonRepeating`, `AddOrResetAlarm`
and of the two EA wrappers that take a callback (`BuildableShell.AddAlarmWithRandomDelta`, `AddPulseAlarm` of the
hosted and protest situations). The forwarding bodies inside `GameObject`, `AlarmManager` and the wrappers are left
out (16 lines). Per site the scan records: declaring type, registering method, API, manager, owner expression and
owner kind (class hierarchy by simple name), callback, first due time, unit, repeat, alarm type, persisted, and
effect flags read from the callback body plus one level of same-file callees.

Cross-check [verified]: the IL call graph index has 1,130 distinct (calling method, alarm API) pairs for the same
APIs; the scan has 1,327 direct sites (several per method) plus 12 wrapper calls. Eight sites were compared by hand
with the source and match. Registration facts (API, owner expression, alarm type, repeat) are read from the source
line; **owner kind for an expression, the group, the effect flags and the model hint are heuristics** [inferred] and
are a starting point for SD-2, not a verdict. 21 callbacks were resolved by name in more than one other file (flag `~n`, flags are the union), 7 not
at all, 4 are lambdas (hint "generic").

Columns of `docs/data/alarm-callback-census.csv`: `group, model_hint, hold_class, assembly, namespace, file, line,
declaring_type, registered_in, api, manager, owner, owner_kind, callback, first_due, unit, repeat, alarm_type,
persisted, effect_flags`. Expressions that are not a plain name are folded to `(expr)`.

Model hint rule, first match: shows UI -> "shows UI (E1 check)"; pushes an interaction or routes a Sim -> "Sim
hand-off"; sleeps or starts a task -> "generic"; uses the game RNG -> "seeded schedule"; creates or destroys an
object -> "closed form + pending event"; else "closed form" (state, buffs, effects, re-arming its own alarm).

### 4.2 Totals

API: `AddAlarm` 805, `AddAlarmRepeating` 266, `AddAlarmDay` 207, `AddOrResetAlarm` 29, `AddAlarmDayNonRepeating` 20,
wrappers 12. Alarm type: `AlwaysPersisted` 740, `DeleteOnReset` 295 (both saved: 1,035), `NeverPersisted` 304.
One-shot 861, repeating 478 (period unit minutes 212, days 156, weeks 62, hours 40, seconds 8).

| Class / system | Sites | Persisted | Repeating | Closed form | Closed form + event | Seeded | Sim hand-off | Shows UI | Generic | Lot mgr | Sim owner | Global, object | Global, other owner | Global, no owner | Unresolved |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| FO fire and cooking appliances | 29 | 27 | 8 | 12 | 7 | 7 | 0 | 3 | 0 | 26 | 1 | 1 | 0 | 0 | 1 |
| FO fishing and fish tanks | 21 | 12 | 10 | 14 | 4 | 2 | 1 | 0 | 0 | 17 | 3 | 0 | 0 | 0 | 1 |
| FO food, dishes, trash, puddles | 23 | 18 | 3 | 5 | 11 | 5 | 1 | 0 | 1 | 22 | 0 | 0 | 1 | 0 | 0 |
| FO other objects | 323 | 245 | 109 | 148 | 41 | 57 | 29 | 28 | 20 | 196 | 50 | 20 | 11 | 39 | 7 |
| FO plants and garden | 17 | 13 | 5 | 7 | 6 | 2 | 1 | 1 | 0 | 15 | 0 | 1 | 0 | 1 | 0 |
| FO rabbit holes, doors, shells, base objects | 124 | 94 | 46 | 76 | 13 | 12 | 4 | 11 | 8 | 104 | 11 | 3 | 0 | 4 | 2 |
| FO spawners and collectables | 26 | 23 | 15 | 6 | 9 | 10 | 1 | 0 | 0 | 20 | 1 | 1 | 0 | 2 | 2 |
| FO store objects | 101 | 88 | 33 | 29 | 17 | 6 | 18 | 27 | 4 | 75 | 16 | 1 | 1 | 3 | 5 |
| FS buffs, occult, actor systems | 166 | 129 | 72 | 54 | 6 | 13 | 71 | 21 | 1 | 9 | 130 | 1 | 14 | 9 | 3 |
| FS careers, school, skills | 128 | 99 | 58 | 68 | 2 | 9 | 15 | 30 | 4 | 6 | 49 | 1 | 51 | 15 | 6 |
| FS household, social, travel, UI | 86 | 57 | 25 | 32 | 5 | 14 | 11 | 17 | 7 | 8 | 18 | 0 | 32 | 21 | 7 |
| FS services and roles | 82 | 78 | 18 | 36 | 1 | 1 | 35 | 7 | 2 | 1 | 74 | 0 | 6 | 1 | 0 |
| FS situations and parties | 122 | 113 | 34 | 69 | 5 | 5 | 12 | 25 | 6 | 2 | 49 | 2 | 49 | 15 | 5 |
| World and lot systems | 91 | 39 | 42 | 35 | 9 | 9 | 17 | 14 | 7 | 12 | 11 | 16 | 8 | 43 | 1 |
| **Total** | 1339 | 1035 | 478 | 591 | 136 | 152 | 216 | 184 | 60 | 513 | 413 | 47 | 173 | 153 | 40 |

Groups are by namespace, with five object groups picked out by type name (docs/331 §4.0 rows). "FO other objects"
is the long tail of object namespaces (electronics, environment, hobbies, decorations, vehicles and so on); the CSV
keeps the namespace so SD-2 can split it.

### 4.3 What the census says about the hold hook

| Hold class | Sites | Meaning for §3 |
|---|---|---|
| Owner's lot manager | 513 | parked-lot case (500 via the owner, 13 registered on the lot's own manager); of these 173 repeat, 435 are saved |
| Global, Sim owner | 413 | Sim hold; 130 repeat; 215 `AlwaysPersisted`, 146 `DeleteOnReset`, 52 `NeverPersisted`; model hints: 200 closed form, 126 Sim hand-off, 45 show UI, 17 seeded, 11 closed form + event, 14 generic |
| Global, object owner | 47 | held through the object's lot when the lot is parked |
| Global, other owner (`SimDescription` 33, `Household` 21, situations, services, roles, helpers) | 173 | never held in v1; FS must tolerate them firing for a far Sim |
| Global, no owner | 153 | never held; 109 are `NeverPersisted` (static managers such as resort, holiday, lunar cycle, aging, school, mail; re-armed by their own startup code [inferred]) |
| Owner not typed by the scan | 40 | read by hand in SD-2 |

- The 45 "shows UI" callbacks on a Sim owner are the pass-through candidates of §3.3; each needs a read of whether
  the UI is conditioned on the active household (most EA notifications are).
- The 126 "Sim hand-off" callbacks on a Sim owner push or move that Sim: holding them is what keeps a parked Sim
  inert, which is the point of decision 7.
- Short repeats (period of one sim-minute or less): 44 sites (20 Sim owner, 16 lot manager, 5 other owner, 3 no owner), among them `BuffOnFire`, bee attacks, jogging,
  stroller, stove / grill `Cook`, jukebox, `Lot` trigger volumes (on the lot's own manager), `HudModel.OnMinuteChanged`. The global no-owner
  ones (HUD, dynamic challenges) stay live by rule.

### 4.4 The docs/331 §4.0 named rows, checked against the registration lines [verified lines, model as docs/331]

| Class | Registrations (file:line) | Manager, type | Note |
|---|---|---|---|
| `Plant` daily growth | `Plant.cs:2915` `AddAlarmDay` at 05:00 + offset, daily | lot, `AlwaysPersisted` | scan flags UI and RNG in the callee chain: growth model must keep the notification for household plants only (household lots are never parked) |
| `HarvestPlant` fade / delete | `HarvestPlant.cs:704`, `1128` one-shots | lot, `AlwaysPersisted` | destroy events |
| `ObjectSpawner` | `ObjectSpawner.cs:515`, `530` one-shots, re-armed by the callback | lot, **`NeverPersisted`** | re-created after load by EA; the spawn delay is drawn from the game RNG at registration |
| `Wildflower` | `Wildflower.cs:291`, `491` one-shots (days); `439` hourly repeat, **global, no owner** | lot / global, `AlwaysPersisted` | the hourly grow pass is global and never held: it must skip parked lots itself or FO filters its effect (Q4) |
| `PreparedFood` spoil | `PreparedFood.cs:266`, `288` one-shot chain | lot, `AlwaysPersisted` | in an inventory the alarm is on the global manager (no lot), Q2 |
| `ServingContainer*` | `ServingContainer.cs:2489`, `2501`; group `:690`; single `:672` | lot; single is `DeleteOnReset` | |
| `TrashPile`, `LitterPile` | `TrashPile.cs:983`, `1049`; `LitterPile.cs:78` one-shots | lot, `AlwaysPersisted` | |
| `FishingSpot` | `FishingSpot.cs:172`, `174`, `177` daily | lot, **`NeverPersisted`** | re-armed at startup, so a load already resets them |
| `FishTank` | tank `FishTank.cs:1272-1274` repeats, `1332` daily (`NeverPersisted`); per fish `:180`, `181`, `325`, `412` | lot, `AlwaysPersisted` | per-fish first due is a random draw at registration |
| `PuddleManager` | `PuddleManager.cs:199` repeat (owner = source object), `491` one-shot (**owner null**) | lot manager by explicit call, `AlwaysPersisted` | a lot-manager timer without owner: covered by the parked-lot case, invisible to an owner walk |
| `Fire`, `Fireplace` | `Fire.cs:399`, `567`, `568`, `578`, `579`, `1067`; `Fireplace.cs:651`, `1780`, `1880`, `2039`, `2119` | lot, mostly `AlwaysPersisted` | spread and go-out tests use the game RNG |

## 5. EA 1.69

Not re-read: the EA 1.69 assemblies are not in `game-copy` now. docs/235 §2 found 14 methods that differ between the
editions and none of them is in `AlarmManager`, `Lot`, `GameObject` or `PriorityQueue`, so §1 and §3 hold on EA 1.69
[inferred from docs/235, I-614-6]. Two of the 14 (`Sim::.ctor()`, `Career::ShowUploadResumeTns`) are in classes
with census rows; neither is an alarm callback in the census [verified against the CSV].

## 6. Gates

| Package | Gate (docs/332) | State |
|---|---|---|
| ST-4 | hook site, read-only due-timer read, `Timer.ObjectRef` path [verified] | met: §1 (one dispatch site, one caller), §3.2 step 4 (heap walk over public `Count` and indexer, timer fields public on a private nested type, reached by reflection as `AlarmKillLog.cs` does), `ObjectRef` field `AlarmManager.cs:30` |
| ST-7a | every callback has a row or "generic" | met for the shipped packs: 1,339 rows, 60 of them generic; the model column is a hint (§4.1) |

## 7. Open questions

- **Q1 (ST-15):** answered in docs/351 (names there win). File and API names. Proposed surface: `Hold(simId)`, `Park(lotId)`, `Release(id)`, `ReleaseAll()`,
  `IsHeld(owner)`, `HeldCount`, pass-through set. FS and FO never touch the side lists.
- **Q2:** objects without a lot (inventory of a far Sim, out-of-world objects) register on the global manager with
  the object as owner. v1 leaves them live. Holding them needs an owner lookup from object to carrying Sim per due
  timer; decide after FO-1's probe shows how many fire.
- **Q3:** `Write` bracket vs flush hub only. The bracket covers every save path, mod save paths included; if SD-4
  proves that every save passes the hub, Hook B can go.
- **Q4:** global no-owner pulses that act on lots (`Wildflower.OnGrowWildflowersOnLot`, venue helpers): never held, so FO must make parked lots invisible to them or accept their effect. List: CSV filter
  `hold_class` = "Global, no owner" and group starting with FO or World (92 rows).
- **Q5:** answered in docs/352 (EA bug with two sides: a world-lot reset drops, a lot reset keeps; ledger §621). I-614-1 (Sim `DeleteOnReset` alarms survive a reset): EA bug or intended? If a bug, a fix would be a
  separate switch; the hold hook does not depend on it.
- **Q6:** ST-7b (top-100 mods) can reuse the scan; it is not in `tools/` yet (docs-only package). Promoting it needs
  a self-test and the mod assemblies decompiled.
- **Q7:** the pass-through set starts empty; SD-1 / SD-2 decide each "shows UI" row.

## 8. docs/51 entries

§614: V-614-1 .. V-614-8, I-614-1 .. I-614-6.
