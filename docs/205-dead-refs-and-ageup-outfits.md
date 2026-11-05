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
| `CookingProcess` appliance handlers | Needs event-delegate surgery per appliance; not read in full. | community | no |
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

### 2.4 Counters

[393] Get refusals, [394] aging states refused, [395] relationship keys removed, [396] relationship entries removed,
[397] aging states removed, [398] alarm owners cleared, [399] react-time entries, [400] coworker / former boss / chess
entries, [401] topics, [402] fishers, [403] Sim fields (pusher, talking partner, vehicle, pregnancy dad), [404] passenger
and horseshoe entries, [2045] dartboard entries (docs/516), [2046] use-list entries (docs/517), [2047] owner entries (docs/519), [2048] disposed handlers (docs/522), [2049] celebrity reactors (docs/523), [2055] object walks of phases 10-12, [2056] ms in them, [2057] walks asked by an event (§2.3a), [405] passes finished, [406] slices, [407] longest slice us, [408] total pass ms, [409] longest
save pass us, [410] passes refused or stopped, and items that threw. Agent names `deadrefguards_*`, `deadrefsweep_*`.

## 3. Switches

| Switch | Tunable | Default | Save data |
| --- | --- | --- | --- |
| `ageupkeepoutfits` | `kAgeUpKeepOutfits` | off | keeps outfits that EA would remove |
| `deadrefguards` | `kDeadRefGuards` | off | no (fewer re-added keys reach the next save) |
| `deadrefsweep` | `kDeadRefSweep` | off | yes |

## 4. Not built

Celebrity reactors, cooking-process handlers, stolen-item owner components and adoption-pool pets (§2.1). The
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

The matrix's byte-identical cases do not apply: these switches change the save by design. The reporter's long Bridgeport
save chain (ErrorTrap load scans, "SimDescription Removed" per load) is the end-to-end measure; we have no such save.

Run: `node tools/harness/queue.ts tools/harness/queues/deadrefs-ageup.json` (after building ts3fixes and ts3agent).
