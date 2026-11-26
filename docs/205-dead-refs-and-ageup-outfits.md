# 205. Dead references in saves (#91) and the age-up outfit wipe (#100)

Two EA script bugs that contributor dalexlol reported, checked against the decompiled game code and built as ts3fixes
switches. All three switches are off by default. Two of them change what goes into a save. Status (2026-10-30): built,
not run in game. Ledger: docs/51 §395. Counters 391-410 (registry `fixes-391`).

Claim marks: **[verified]** = read in the decompiled EA code by us; **[community]** = the reporter's in-game measurement,
not repeated by us; **[inferred]** = our reading, not yet checked.

## 1. Issue #100: a failed or cancelled age-up wipes every outfit

### 1.1 Mechanism

- `SimBuilderTask.Simulate` runs the whole next-age outfit build inside one catch-all. Any exception sets `Failed` and
  `FailureException` **[verified]**. That includes the `OperationCanceledException` the task throws itself at each
  `Abort` check **[verified]**.
- `Abort` is set by `SimBuilderTask.Stop` while the task is not `Done` **[verified]**. It is reached through
  `AgingState.AbortSimBuilderTask`, which two callers use: the `AgingManager.Enabled` setter (aging turned off aborts every
  state's builder) and `AgingManager.CancelAgingAlarmsForSim` **[verified]**. A builder bug is not needed.
- After the catch, the task calls `FinishOutfitSwap` whenever its "model built" flag is false. That flag is only set after
  the build returns, so it is false after any exception, whether or not the description has a Sim object **[verified]**.
  The issue says "a Sim with no Sim object". The code path is the same for instantiated Sims too, so the wipe is broader
  than reported **[inferred, not seen live]**.
- `FinishOutfitSwap` removes every outfit of every category, then adds back what `NewOutfits` holds. After an early failure
  `NewOutfits` is empty or partial, so the description is left with no outfits or only some **[verified]**.
- Follow-ons named in the issue: `AgingState.PreInstantiateSim` reads `NewOutfits[Everyday][0]` when the old outfit key is
  not in the swap map, which throws `KeyNotFoundException` if the build made no Everyday outfit **[verified]**. A later
  age-up throws `ArgumentException("Invalid SimDesc used to age.")` when there is no Naked and no Everyday outfit
  **[verified]**. The tourist and townie sightings are **[community]**.
- EA's own age-up paths already stop on `Failed`: `AgeUp` and `AgeDown` exit, and `AgingQueue` drops the state
  **[verified]**. `PreInstantiateSim` sets the new age only after reading the swap map, so a failed build never changes
  the age there **[verified]**.

### 1.2 Fix: `ageupkeepoutfits` (EaFixes.cs)

`FinishOutfitSwap` is hooked. When the task `Failed`, or when `NewOutfits` holds no outfit at all, it returns without
touching the description. The old outfits stay and the Sim keeps its current age. One log line names the Sim and the
exception. Nothing else is skipped: the method only swaps outfits and fills the old-to-new key map **[verified]**.

`PreInstantiateSim` still throws as before when there is no Everyday outfit in `NewOutfits`. That is left alone on
purpose: the reporter notes that `MergeTravelInformation` loops until the age matches, so a quiet return there could loop
forever **[inferred, theirs; we agree from the code]**.

A partial build that did not fail (some categories made, no exception) still swaps. That cannot happen without an
exception in the current code: every category list is created before it is filled **[inferred]**.

### 1.3 Saves that already have the damage

There is no safe source for the lost look: the outfits hold the face and body, and once removed they are released.
The reporter rebuilds a look with `Genetics.MakeSim` / `GeneticsPet.MakeRandomPet`, which gives a new face, not the
old one. We do not do that. We only count and name these descriptions:

- [392] = descriptions with no Naked and no Everyday outfit at the last count (the `deadrefsweep` pass counts them in
  its description phase; `AgeUpCheck empty` lists them by name).

### 1.4 Counters

[391] swaps skipped (failed or empty build), [392] no-outfit descriptions at the last count. Agent names
`ageupkeepoutfits_kept`, `ageupkeepoutfits_nooutfit`.

## 2. Issue #91: EA holders that keep disposed descriptions and destroyed Sims

"Gone" below means a description that is not `IsValidDescription`, or a Sim object that `HasBeenDestroyed`.

### 2.1 Holders checked

| Holder | Why EA keeps it | Mark | Built |
| --- | --- | --- | --- |
| `Relationship.sAllRelationships` keys | `SimDescription.Dispose` calls `RemoveSimDescriptionRelationships` and only then clears the valid flag; its later steps (household remove, Sim destroy, the disposed event) and other code call `Relationship.Get(SimDescription)`, which adds an empty key for any description it is asked about. The pair overload refuses invalid descriptions. | verified | guard + sweep |
| `AgingManager.AgingStates`, aging alarms | `Dispose` removes the state only through the `AgingEnabled` setter, which does nothing when the flag is already false. `IsAgingStateValid` checks only for null. | verified | guard + sweep |
| Alarms owned by a gone description | `Dispose` calls `AlarmManager.OnDispose` near its end; alarms added after that (e.g. the day pass's aging alarms for a state that stayed) stay. | verified (mechanism), community (counts) | sweep |
| `LookAtManager.mLastReactTime` | `CleanOldReactEntries` drops an entry only when it is older than the wave time; no check for gone descriptions. | verified | sweep |
| `Occupation.Coworkers`, `School.Coworkers`, `FormerBoss` | `School.RemoveOwnerFromCoworkers` walks only the leaving Sim's own list; `RemoveInvalidCoworkers` runs from `CheckCoworkers`, which only `Career` and `ActiveFireFighter` call. | verified | sweep (entries dropped directly, not via `RemoveCoworker`) |
| `LogicSkill.mChessRankingNextChallenger` | Re-picked only when the getter is read. | verified | sweep (cleared; the getter re-picks) |
| `SimActiveTopics.mUnforgettableTopics` | `RemoveInvalidTopics` runs each topic's own test, not a gone-parameter check. | verified (holder), inferred (that the test passes) | sweep |
| `Fish.mFishingSim` | Saved with the fish; EA tests the fisher for null at every use. | verified | sweep (cleared) |
| `SimRoutingComponent.mPusherSim` | Set by `RequestMidRoutePush`, never cleared. | verified | sweep |
| `SocialComponent.mLastTalkingTo` | Cleared on the partner only when it points back. | verified | sweep |
| `Sim.ReservedVehicle` | Nothing clears a destroyed car. | verified (no clear on destroy), inferred (picker path) | sweep |
| `Pregnancy.mDad` | Nothing clears it when that Sim object goes; `CheckForDad` finds the dad again by `DadDescriptionId`. | verified | sweep (cleared, then `CheckForDad`) |
| `CarOwnable` / `BoatOwnable` previous passengers | Kept until the next route ends; `GetSimsThatGoHomeWithActor` reads every array slot without a null check. | verified | sweep (array rebuilt without the gone Sims, list filtered) |
| `HorseshoeCourt` throwers and scores | Cleared only at round or game end. | verified | sweep |
| `Dartboard` `Players`, `Teams`, `CurrentPlayer`, `CheatingSim` | `Quit` (every cleanup) drops the Sim from its team and `Players`; `CurrentPlayer` and `CheatingSim` clear only in `DoFinishGame`; a Sim reset or destroyed mid-game never reaches `Quit`. | verified | sweep (phase 9, docs/516) |
| `GameObject` `mActorsUsingMe`, `mReferenceList`, `mRoutingReferenceList` | Persisted lists; the paired Remove runs only from the Sim's own clean-up, which a reset or destroyed Sim never reaches. | verified | sweep (phase 10, docs/517) |
| `Celebrity.mWaitingToReact` (sweep phase 13, docs/523) | Holder object not located in time. | community | no |
| `CookingProcess` appliance handlers | `AddObjectListeners` subscribes to the clicked appliance's `ObjectDisposed` and adds a state listener; nothing unsubscribes the handler while the appliance lives (`ClearObjectListeners` runs only from the disposal handler, after it nulled `ObjectClickedOn`), the state-changed path and the Prepare steps (`ObjectClickedOn = null` in FoodTray, Pot, FryingPan, BakingDish, Microwave) leave the handler. Same in simler90's core. | verified | sweep phase 14, own switch `deadrefcooking` (2026-11-15, not run) |
| `ObjectOwnerComponent` `mOwner` (a reward's giving Sim or a theft's victim), `mThief` | Saved by description id and re-read as `CreatedSim` at load; nothing updates them in a running world when that Sim object is destroyed. | verified | sweep (phase 11, docs/519) |
| `GameObject.ObjectDisposed` handlers of `GameObjectRelationship` (cars, birds) | The constructor subscribes, nothing unsubscribes: a deleted description or a decayed relationship stays reachable from the object. | verified | sweep (phase 12, docs/522) |
| Adoption pool pets | Disposing descriptions is a behaviour change, not a cleanup. | community | no |
| `Service.mPreferredServiceNpc` | Already `preferredservice` (#86). | verified | yes (#86) |

### 2.2 The load-time warning

`SimDescription.mIsValidDescription` is `[Persistable(false)]`. Every description read from a save starts invalid until
`SimDescription.Fixup` runs (post-load fix-up, urnstone load, import) **[verified: the field carries
`[Persistable(false)]` and the parameterless constructor leaves it false; which callers run Fixup is the reporter's list, community]**. A sweep that tests descriptions before that would see every household member as
gone. Our sweeps never run at load. On top of that every pass stands down when:

- the world is gone, the game is travelling or on vacation;
- (spread pass) a save is in progress or the game is not in live mode, or the switch went off;
- any active-household member is invalid (the load signature);
- more than half of a holder of 16 or more entries is gone (`DeadRefsCore.LooksLikeLoad`).

### 2.3 Fix: two switches (DeadRefs.cs)

**`deadrefguards`** (no save change of its own):

- `Relationship.Get(SimDescription)`: for an invalid description that has no key, return a shared empty collection and
  add nothing. A description that still has a key gets EA's answer, so `RemoveSimDescriptionRelationships` (which calls
  `Get` before the flag is cleared, and walks the existing key) is unchanged. Null keeps EA's behaviour.
- `AgingManager.IsAgingStateValid`: false when the state's description is invalid (in a world, not travelling), so the
  day pass no longer ages or alarms a dead state.

**`deadrefsweep`** (changes save data):

- A spread pass once per sim hour (NeverPersisted alarm), run as one script task (phases 10-12 only when due, §2.3a). Each phase gathers its items in one
  tick, then works through them in slices of at most ~2 ms (`EaSpreadCore.kSliceUs`, `StopWatch`), `Simulator.Sleep(0)`
  between slices. Each item is tested again at its turn. The safety checks of §2.2 run between every two slices.
- Phases: 1 relationship keys (gone key: removed from both sides; live key: entries naming a gone description dropped),
  2 aging states of gone descriptions (`AgingManager.RemoveSimDescription`: state removed, its alarms cancelled), 3 alarm
  owners that are gone descriptions (`AlarmManager.OnDispose`, what `Dispose` itself calls), 4 household descriptions
  (coworkers, former boss, chess challenger, pregnancy dad, no-outfit count), 5 Sims (react times, unforgettable topics,
  push requester, last talking partner, reserved vehicle), 6 fish, 7 cars and boats, 8 horseshoe courts, 9 dartboards
  (docs/516), 10 object use, reference and routing-reference lists (docs/517), 11 object owner components (docs/519), 12 ObjectDisposed handlers of relationships nobody holds (docs/522), 13 celebrity waiting-to-react lists (docs/523).
- A cheap final pass before each save (`DisposeSaveHook`): only phases 1-3, gone entries only. That is one walk over three
  dictionaries with a field read per entry; the hourly pass has already done the rest. Its time is counter [409].

### 2.3a Phases 10-12: one walk, after a Sim leaves or daily (2026-11-03, built and run)

- **Why:** phases 10-12 each gathered every `GameObject` (`Queries.GetObjects<GameObject>`) and walked it, every sim
  hour. On the player's old save (1111 kit5, job `old-gone-on`, 20 min) the whole pass cost ~91 ms (206 passes, 18.8 s)
  and these three phases found 0 [measured]. Their share of the 91 ms was not measured before this change.
- **Now:**
  - The three phases share one gathered list per pass. `SlicedPass` copies it, so each phase still walks its own copy
    and re-tests each item at its turn.
  - They run only when a Sim object was destroyed or a description disposed since the last walk, or once per 24 sim hours
    (`kDeadRefObjHours`). EA's `Sim.Destroy` sends `EventTypeId.kSimDestroyed` and `SimDescription.Dispose` sends
    `kSimDescriptionDisposed` [verified: decompiled game code]. Two `EventTracker` listeners set one flag, so many events
    in an hour cost one walk. The flag starts set at each world load.
  - What starts each phase's work: phases 10 and 11 clean entries naming a destroyed Sim object (a townie leaving, a
    reset), so `kSimDestroyed`. Phase 12 cleans handlers of relationships whose description was disposed
    (`kSimDescriptionDisposed`) or that decayed (`RemoveRelationship`). Decay sends no event, so such a handler can now
    stay up to 24 sim hours instead of 1.
  - Phases 1-9 and 13 stay hourly. A pass stopped inside phases 10-12 (an unsafe state, §2.2) asks for the walk again.
- **Gain:** it depends on how often Sims leave the world. When a Sim object is destroyed every sim hour, the walk still
  runs hourly, with one gather instead of three. Counters [2055] walks, [2056] ms in finished walks (the gather is in the
  first slice), [2057] walks an event asked for (the first walk after a load counts as asked); daily walks = [2055] -
  [2057].
- **Result (2026-11-03, player's old save 1111 kit5, release v0.2.4 asi, 20 min, seed 12345, `deadrefsweep` +
  `rewardgiver` on; results file `docs/data/results/2026-11-03-alexvixell35-stack-deadref-obj-daily.md`):**

  | | before (`old-gone-on`, 3 walks every pass) | after (`old-gone-objdaily`) |
  |---|---|---|
  | passes [405] | 206 | 215 |
  | sweep time [408] | 18,778 ms (~91 ms per pass) | 4,913 ms (~23 ms per pass), -74% |
  | GameObject walks | 618 (3 per pass) | 40 [2055], all asked by an event [2057] |
  | ms in phases 10-12 [2056] | - | 2,779 (~69 ms per walk) |
  | slices [406] / longest [407] | 10,456 / 5.9 ms | 3,711 / 4.4 ms |

  - The rest of the pass costs ~10 ms ((4,913 - 2,779) / 215), so phases 10-12 were ~81 of the ~91 ms per pass. The
    shared gather makes a walk ~15% cheaper (~69 vs ~81 ms); skipping walks does the rest.
  - A Sim object was destroyed or a description disposed often enough for a walk every ~5 passes (sim hours), so the daily
    rule never fired here.
  - Cleanup: phases 10-12 found 0 in both runs; the other phases found similar counts (react times 129 vs 81, Sim fields
    36 vs 38, passengers 12 vs 2, topics 1 vs 1, celebrity 4 vs 5). Stuck Sims at the end 11 vs 21, no exception.

### 2.3b Pause, minimized window and modes: the pass waits (2026-11-23, built, NOT RUN)

Audit 2026-11-23: the released pass (`deadrefsweep` + `deadrefcadence` ON) kept removing dead refs while the game was
paused. Its script task runs every frame (`Simulator.Sleep(0)`), and ScriptTick and TaskObjects run while the sim takes
0 steps (docs/85 I-85-2), but `DeadRefUnsafe` checked live mode and travel, not pause. A minimized window stops the clock
through GameClock pause bit 2 without `CurrentGameSpeed == Pause` (docs/85 I-85-1, repro `clock-minimize-pause` PASS
2026-10-17; docs/162 §7). Rule (Rodney 2026-11-23): nothing of ours does sim-state work while paused, in CAS, Buy/Build,
Edit Town or menus.

- Shared gate `Fixes.SimFrozenOrNotLive()` (`SimFrozen.cs`, offline part `SimFrozenCore.cs`): true when not
  `GameStates.IsLiveState` (in-world substate LiveMode only, so Buy, Build, CAS and Edit Town are out **[verified,
  decompiled GameStates.cs / InWorldState.SubState]**), travelling or shutting down, `Gameflow.CurrentGameSpeed ==
  Pause`, or `SimClock.CurrentTicks` unchanged for 100 ms of real time (`Environment.TickCount`). One sim step is 1/30 s
  and a frame of >= 33 ms posts at least one, so 100 ms without a move is a stopped clock, not a gap between steps
  **[inferred, docs/51 I-596-1]**. Shortcut: a clock that stopped before the first call is seen 100 ms after it.
- `DrWaitWhileFrozen` before every slice: unsafe (`DeadRefUnsafe`, so leaving live mode) ends the pass as before
  ([410], the groups stay due); live but paused or clock stopped waits a frame at a time without a slice, keeping the
  pass's place and its due flags (`sDeadRefObjDue`, group dirty flags), so the same pass goes on after the unpause.
  Counter [1727] `deadref_paused_waits` = frames waited. Gathers (read-only walks) may still run paused; no holder is
  changed until the next slice.
- The save-time pass (`DisposeSaveHook`, phases 1-3) is unchanged: it runs inside EA's save, which a player usually
  starts while paused.
- Other per-frame gates checked: `UfLive` (UnseenFallbackLoop.cs) feeds `ufloopbreak`, which maxes motives, removes
  moodlets and cleans lots in a `LoopTask`: its drain and lot loop now wait while live and frozen (`UfFrozen`), out of
  live mode the break is still dropped. `UfSitesLive` (UnseenFallbackSites.cs) only builds the Sim list pushed to
  ts3native: read-only, unchanged. `LiveGuard` (AutLive.cs) is a load-time check of swapped consumer methods, not a
  state gate: unchanged.
- No new switch: an EA-like bug fix on released code, ON with `deadrefsweep`.
- Repro `pause-cas-buy-check` (DeadRefCheck `pausemark` / `pausestate`), queue
  `tools/harness/queues/deadref-pause-correct-1123.json` (~15 min, not run). Build `ts3fixes.package.pausegate1`.

### 2.3c Pause gate, group 2: drains and one-shot tasks (2026-11-23, built, NOT RUN)

Same rule, the other released per-frame tasks of the audit. `IsInWorld()` is `CurState is InWorldState`, so it stays
true in in-world CAS, Buy, Build and Edit Town (they are InWorldState substates) **[verified, decompiled GameStates.cs]**;
gates that only test `IsInWorld` therefore act in those modes. Leaving live mode pauses the clock (`LiveModeState.Shutdown`
calls `GameUtils.Pause`, `EditTownState` too) **[verified, decompiled]**, so EA's alarms do not fire there.

Shared helper `SfWait(slot[, saveExits])` (SimFrozen.cs; decision `SimFrozenCore.Hold`, selftest `tools/selftest/deadrefs`):
while `SimFrozenOrNotLive()` the task waits a frame at a time with its work kept; the world going away (shutdown,
travel, out of the world) or, for wildflowers, a save ends the wait so the caller's existing exit runs. No new switch:
each fix is ON with its released switch.

| Site | Switch | Before | Now | Counter |
|---|---|---|---|---|
| FewerPlans.cs `StuckCheckDrain` | `stuckcheckasync` | live checked at enqueue only; drain ran paused / in Buy | held entry waits (`SfWait`) before the run; travel / shutdown still drop it | [1734] |
| RouteFixes.cs `StuckWatchdog` | `watchdog` (+`watchdogidle`) | first scan at task start (load, Edit Town) ungated | no `Check` / `CheckIdle` while frozen or not live (tick sleeps only end while the clock runs, so the start was the gap) | [1735] |
| ReplanDefer.cs `SpreadReplay` | `replanspread` | replayed paused / during the Buy-exit transition; household held too (playerfirst first) | queue held while frozen or not live; household, selected, unknown-scope and non-Sim callers (`IsPlayerSim(rc.Owner as Sim)`) are not held: EA's `ForceRouteReplan(false)` runs at EA's time (inside `ExitBuildBuyMode` / `ForceReplanOfAllSimRoutes`), exact | [1736] waits, [1737] run at once |
| AutLive.cs go-live wait | `kAutLive` | a worker answer arriving paused was applied; paused frames counted toward `sLiveMaxWait` | frozen while waiting = abandon, drop (`kSDropOther`), the game's own search runs (`MetaOrig`): exact for every Sim, household included | [1738] |
| AutLive.cs apply slices (`LateSliceOk`, `DirectB14`) | `kAutLive` | a later B13 slice after a pause could still consume worker answers (autlivelate within its age window, direct path) | a later slice that starts frozen or not live consumes nothing: autlivelate refusal (sticky `L.Late`) / direct snap (`L.Snapped`), so the rest of the search is the game's own B14; no wait is added inside EA's search and no frame limit is touched | [1743] |
| ScriptHotSpots.cs `WfPlaceSliced` | `wildflowertask` | gates lacked `IsLiveState` | `SfWait(…, saveExits: true)` after each yield; save / travel / world-gone exits unchanged | [1739] |
| DisposeSpread.cs drain | `disposespread` | destroyed paused | live but frozen = hold; save and non-live still flush (state 1) | [1740] |
| AptLeave.cs RemoveGuest task | `kAptLeave` | one-shot ran at once | waits for the next unfrozen live frame; world gone = skip | [1741] |
| BoatTrip.cs `BtSweepTask` | `kBoatTrip` | one-shot ran at once | same; queued flag stays set while it waits | [1742] |
| UnseenOutfitDefer.cs `OdSweep` | `kUnseenOutfitDefer` | applies paused / flushes out of live mode | **unchanged on purpose**: every waiting outfit is one EA already set at its own time, so an apply while paused or in CAS only catches the model up to EA's state (a camera pan while paused must show it); holding it would move further from EA | - |

EA timing for the replans **[verified, decompiled]**: `BuyModeState` / `BuildModeState` shutdown call
`LotManager.ExitBuildBuyMode`, which calls `OnBuildBuyExit` (`ForceRouteReplan(false)`) for every actor before live mode
starts; Edit Town and the options call `ForceReplanOfAllSimRoutes`. `ForceRouteReplan(false)` inserts a replan action that
the Sim follows only when it moves again **[inferred, docs/51 I-598-1]**; townie replays now start on the first unfrozen
live frame.

Repro `pausegate-drains-check` (`Fixes.PauseGateCheck` mark / state / replan / boatsweep): paused 60 s, in CAS and in
Buy/Build the action counters (stuck checks run [128], replans replayed [135], spread destroys [281], watchdog resets
[31]) stay +0, the replan queue holds and drains after the unpause, every household Sim keeps position and interaction.
Queue `tools/harness/queues/pausegate-drains-correct-1123.json` (~20 min, not run). Build `ts3fixes.package.pausegate3b` (pausegate3 + the apply-slice gate [1743]).
### 2.3d Pause gate, group 1: the other spread passes (2026-11-23, built, NOT RUN)

Audit 2026-11-23, released switches ON: the other spread passes had the same gap. One shared wait,
`Fixes.WaitWhileFrozen(slot, stop)` (SpreadGate.cs; `DrWaitWhileFrozen` now calls it): while `SimFrozenOrNotLive()`, wait a
frame at a time with the pass's place kept and count the frame; `stop()` true = the caller's existing end. Two stops:
`WorldGone` (our own pass tasks: wait through pause, clock stop, CAS, Buy/Build, Edit Town and travel; the world going
away still drops the rest as before, nothing queued is lost while the world exists) and `NotLiveOrGone` (a yield
inside Overwatch's or ErrorTrap's own code: wait only while paused or clock stopped in live mode, act as before out of
live mode, so a load-time caller never waits for a live mode that only comes after it returns; loading has no
in-world substate, so `IsLiveState` is false there **[verified, docs/202 §2 table, decompiled GameStates.IsLiveState]**).

| Site | Switch (released) | Was | Now | Counter |
|---|---|---|---|---|
| EaSpread.cs `RunSliced` / `RunUnits` (venue cleanup lots + actors, Overwatch inventory) | `easpreadlots`, `overwatchchain` | after each slice `WorldGone` only | `WaitWhileFrozen(WorldGone)` before every slice, the first too | 1728 lots, 1731 Overwatch |
| EaSpread.cs `LotCleanupTask` stage ticks (simler90 aging, missing Sims, hot-spot pick) | `easpreadlots` | `whole = !WorldGone()` | `whole = WaitWhileFrozen(1728, WorldGone) >= 0` | 1728 |
| EaSpread.cs `PetTask` | `easpreadpets` | no gate | waits before the build starts | 1729 |
| EaSpread.cs `PetSleepHook` on `GeneticsPet.SleepIfPossible` (EA's sleep points between the outfit steps) | `easpreadpets` | EA's sleep only: a pause mid-build let the rest finish | EA's body through the stub, then in our pet task only a wait (`WorldGone`); other callers (CAS, adoption) EA's body only | 1729 |
| EaSpread.cs `DecayTask` (household relationships) | `easpreaddecay` | after each slice `WorldGone` only | before every slice | 1730 |
| OverwatchSpread.cs `OwChainTheirs`, `OwStartTask`, agent "all" task | `overwatchchain` | no gate | waits before Overwatch's code starts | 1731 |
| OverwatchSpread.cs chain sleep between types | `overwatchchain` | Sleep, `WorldGone` | Sleep, wait (`WorldGone`), `WorldGone` | 1731 |
| OverwatchStuck.cs StuckCheck slice sleep (`SelectHook`) and per-plan yield (`RoutableHook`) | `overwatchstuckslice`, `overwatchstuck` | Sleep | Sleep, wait (`NotLiveOrGone`) | 1731 |
| ErrorTrapSpread.cs `EtSleep` in `EtPass` (household inventories) | `errortrapspread` | Sleep, `WorldGone` | Sleep, wait (`WorldGone`) | 1732 |
| ErrorTrapSpread.cs `EtSleep` in `CheckHook` (ErrorTrap's load-up chain) | `errortrapspread` | Sleep | Sleep, wait (`NotLiveOrGone`) | 1732 |
| SaveCleanupSpread.cs phase loop | `savecleanup` | `SpreadCleanupUnsafe` after each slice (ends out of live mode, not on pause) | before every phase and slice: paused waits, unsafe ends as before ([43], [415]) | 1733 |

- Pet build: the game's path runs it in the pool alarm, which may not yield, so it is one tick and never spans a pause
  **[verified, decompiled `GeneticsPet.SleepIfPossible`: sleeps only when `CheckYieldingContext`; docs/147 §3]**. Ours
  spans ticks at those sleep points, so each one now waits while frozen (`PetSleepHook`, swap-stack row with stub). The
  part between two sleep points (`CASLogic.RandomizePet`, outfit steps) still runs whole in one tick, as in the game.
- Overwatch / ErrorTrap code run out of live mode acts as before.
- Helper vs group 2 (`bugfix/pausegate-drains-1123`, §2.3c there): its `SfWait(slot[, saveExits])` in SimFrozen.cs is the
  same loop with a fixed stop (`SfLeaving`: also ends on travel and game shutdown) and returns bool. Ours lives in
  SpreadGate.cs (`WaitWhileFrozen(slot, stop)`, -1/0/1) because these passes need their own stop (WorldGone waits through
  travel; the save cleanup stops on `SpreadCleanupUnsafe`; `NotLiveOrGone`). Group 2's agent check is `PauseGateCheck` /
  `Pg*`; ours is `SpreadGateCheck` / `Sg*`, so the two merge without duplicate members. Merger: keep both helpers; text
  conflicts only in docs (both sections after §2.3b, this one is §2.3d), registry, Fixes.cs counter comments, PLAN.md,
  index line and ledger tail: keep both sides.
- Gathers (read-only lists) may still run on the first frame; no sim state changes until the gate opens.
- No new switch: bug fix on released code, ON with each pass's switch.
- Agent check `SpreadGateCheck` (SpreadGate.cs, with `WaitWhileFrozen`): `start` runs every pass now without its alarm (`lots`, `pets` = a
  test stray cat, `decay`, `owinv` / `et` with NRaas Overwatch / ErrorTrap loaded, `sc`), `mark` / `state` print per
  group passes finished and frames waited since the mark plus the active household fingerprint (relationship pairs,
  summed liking, items in member / family / fridge inventories). Repro `pausegate-spread-check`, queue
  `tools/harness/queues/pausegate-spread-correct-1123.json` (bundle: the new repro + `pause-cas-buy-check` on the same
  build, ~30 min, not run). Build `ts3fixes.package.pausegate2b` (2 without the pet sleep hook). Inferences: docs/51 §599.
### 2.3e Native timers and route results across a pause or a mode (2026-11-23, built, NOT RUN)

Same rule, second round of the audit (branch `bugfix/pausegate-native-1123`, builds `ts3native.asi` 0.36.300-pausegate4
and `ts3fixes.package.pausegate4`, counters v224 `pg*` at 0x6B60). No new switch: bug fixes on released code, ON.

| # | Gap | Fix | Counter |
|---|---|---|---|
| 1 | `[UnseenFallback]` Sites / FinishWalk (`unseenfbc.cpp`): list age 3 s and hold timers (3 s unclaimed, 10 min) ran on wall clock. A pause over 3 s expired every hold; the held unseen Sim walked on after the unpause while ts3fixes still placed it at its path end (a jump). | ts3fixes `UfSitesLoop` sends code 27 on the 0x83 channel while live mode is frozen (`SimFrozenOrNotLive`; 1 again every 0.5 s, 0 at the thaw) and pushes no list. ts3native `UfcNow` stands at the freeze start, so neither age nor hold timers advance; the thaw moves every stamp by the frozen span. No word for 3 s = the freeze ends at the last word (a dead loop never keeps an old list fresh). Out of live mode the empty list goes out as before. Older ts3native refuses code 27: ts3fixes stops asking for that load. | `pgFrozenSpans`, `pgFrozenHolds` |
| 1b | `UrPlacedSweep` (`unseenroute` repair placement, UnseenFallback.cs) could place a Sim while paused. | waits while `SimFrozenOrNotLive()` (the placed map is kept). | - |
| 2 | `[UnseenThrottle] NeedViewers` could silently become 0; `[UnseenAnim]` / `[UnseenTable]` read the household table through it. | pinned to 1: `tools/release-defaults-check.ts` `PINNED` (self-check reads master's ini) and build.yml's release job. | - |
| 3 | `[PcCheck] Enabled=1` was not in build.yml's stable list (the release job would fail "Not stable") nor in docs/PLAYERS.md. | added to both (Rodney keeps it on). | - |
| 4a | `[RouteAsync]`: a J_DONE result (kept up to 5 s) collected after a Build/Buy edit handed out a path from the pre-edit world. | routing epoch `g_rtEpoch` moves at the gate entries of `RoutingSystem_OnExitBuildBuy 0x835240` (site 13) and `RoutingSystem_RebuildLotRouting 0x835110` (site 14) and at the `RouteManager_OnExitBuildBuy` IC (routesnap E13). A job stores the epoch at START; the worker skips an older one at claim, `IcCollect` plans an older result again synchronously, as EA plans after the exit. Shortcut: with `[RouteGate]` and `[RouteSnap]` both off nothing moves the epoch (both on in release). | `pgEpochStale` |
| 4b | `[Discourage]` records deferred before the exit were applied after 0x835240's rebuild. | `DiscDrainBb` drains at the site-13 gate entry (owner thread, an IC point, I-600-4); skipped inside a job callback, with +0x918 held, with the P5c lent lock or while draining (then the next drain point applies them, as before). Site 14 is not drained: it also runs from save and lot paths that drain already or are not IC points. | `pgBbDrains`, `pgBbSkips` |
| 4c | Is `GameStates.IsLiveState` false in CAS and Edit Town? | yes **[verified, decompiled]**: `StateId == 0` (LiveMode) only; CAS / CAB substates 3-11 and 17-19, EditTown 14 (I-600-1). | - |

Not changed: `UfWhy` (read-only; its callers gate), `UnseenViewers.ViewerPushLoop` (a stale household table counts as near
the household, the safe side, I-600-3 for why it goes stale while paused), `AutLive.LiveCheck` (owned by the
pausegate-drains lane). Native selftest: `TestUnseenFallbackSites` (60 s freeze keeps list and hold, control without the
word expires it, a silent freeze ends after 3 s) and the async test (stale finished result and stale claim planned again,
same epoch collected). Queue `tools/harness/queues/pausegate-native-correct-1123.json` (2 jobs, ~25 min, dry-run ok, NOT
RUN): `pausegate-unseen-hold-pause` (SV, 60 s pause with FinishWalk holds: `ufwRelease_3` +0, `pgFrozenHolds` >= 1) and
`pausegate-buildbuy-epoch` (BP, three Buy/Build round trips with route jobs in flight: `pgEpochStale` >= 1). Ledger §600.

### 2.4 Counters

[393] Get refusals, [394] aging states refused, [395] relationship keys removed, [396] relationship entries removed,
[397] aging states removed, [398] alarm owners cleared, [399] react-time entries, [400] coworker / former boss / chess
entries, [401] topics, [402] fishers, [403] Sim fields (pusher, talking partner, vehicle, pregnancy dad), [404] passenger
and horseshoe entries, [2045] dartboard entries (docs/516), [2046] use-list entries (docs/517), [2047] owner entries (docs/519), [2048] disposed handlers (docs/522), [2049] celebrity reactors (docs/523), [2055] object walks of phases 10-12, [2056] ms in them, [2057] walks asked by an event (§2.3a), [405] passes finished, [406] slices, [407] longest slice us, [408] total pass ms, [409] longest
save pass us, [410] passes refused or stopped, and items that threw, [1727] frames a pass waited while paused or with the clock stopped (§2.3b). Agent names `deadrefguards_*`, `deadrefsweep_*`.

## 3. Switches

| Switch | Tunable | Default | Save data |
| --- | --- | --- | --- |
| `ageupkeepoutfits` | `kAgeUpKeepOutfits` | off | keeps outfits that EA would remove |
| `deadrefguards` | `kDeadRefGuards` | off | no (fewer re-added keys reach the next save) |
| `deadrefsweep` | `kDeadRefSweep` | **ON since 2026-11-18** (Rodney; gate: save matrix PASS, a FAIL turns it off; ~23 ms per hourly pass, 2026-11-03 measurement) | yes |

## 4. Not built

Adoption-pool pets (§2.1). Celebrity reactors (phase 13, docs/523), owner components (phase 11, docs/519) and cooking-process handlers (phase 14, `deadrefcooking`, 2026-11-15: a handler goes when its process no longer names that appliance or the process is dead, no owner or owner destroyed; a dead process still naming it also gets `ObjectClickedOn = null`, as EA's handler does at disposal, and a process naming no object loses its state listener; counter 994, repro `cooking-handler-gone-check`, not run) are built since. The
"valid but householdless at save" descriptions the reporter traces at the end of the issue are not dead yet at save time,
so no sweep that tests validity can catch them; they need the cause (who orphans them) **[community]**.

## 5. Test plan

Queue `tools/harness/queues/deadrefs-ageup.json` (correctness and save matrix, no timing):

1. `bp-ageup-outfits-check` (repro `bp-ageup-outfits-check`): one townie's builder started and stopped at once, off then on.
   Off: the townie loses every outfit (EA's bug, stress-labelled, test save only). On: the count stays, [391] +1.
2. `bp-deadrefs-check` (repro `bp-deadrefs-check`): `Relationship.Get` and `IsAgingStateValid` on a made-up never-valid
   description, off then on; one spread pass; every gone count 0 after it; longest slice <= ~3 ms.
3. `bp-deadrefs-saves` and 4. `ip-deadrefs-saves` (repro `bp-deadrefs-saves`, two worlds): the save matrix cases for a
   save-data change with all three switches on: new save, overwrite twice, back-to-back, unpaused, reload in
   the same session + 60 s play, save after reload.
5. `bp-deadrefs-travel`: travel to France with the switches on; both passes refuse while away, the guards still answer.
6. `deadref-pause-correct-1123` (repro `pause-cas-buy-check`, §2.3b): pass started paused, 60 s paused = slices +0 with
   paused waits rising, the same pass completes after the unpause; in CAS and Buy/Build slices +0.
7. `pausegate-spread-correct-1123` (repro `pausegate-spread-check`, §2.3d): every spread pass started paused, 60 s
   paused = done +0 with every pass waiting and the household fingerprint unchanged, all finish after the unpause; in CAS
   and Buy/Build done +0, household unchanged.
8. `pausegate-native-correct-1123` (§2.3e): unseen holds through a 60 s pause, stale route results after Buy/Build.

The matrix's byte-identical cases do not apply: these switches change the save by design. The reporter's long Bridgeport
save chain (ErrorTrap load scans, "SimDescription Removed" per load) is the end-to-end measure; we have no such save.

Run: `node tools/harness/queue.ts tools/harness/queues/deadrefs-ageup.json` (after building ts3fixes and ts3agent).

**2026-11-18 (Rodney):** `deadrefconvo` and `deadrefcooking` are default ON too, with `deadrefsweep`, in the save-matrix bundle (docs/289 §6); a matrix FAIL turns the culprit off.
