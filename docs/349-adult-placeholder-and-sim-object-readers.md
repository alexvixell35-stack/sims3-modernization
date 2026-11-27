# 349. Adult placeholder static check (ST-6) and readers that need a Sim object (RX-6) (2026-11-26)

Static only, docs only, nothing built or run. Steam 1.67. Source: the decompiled script DLLs under
`game-copy/decompiled` (local only, never committed); `file:line` below points into that copy. Packages: ST-6 of
docs/332 §2.2 (placeholder static check for adults, docs/324 §5.6, decision 6) and RX-6 of docs/340 §4 (readers that
assume a resident has a Sim object). Ledger: docs/51 §618. Handoff: `docs/handoff/st6-rx6-1126.md`. Site list:
`docs/data/createdsim-unguarded-census.csv`.

Markers: [verified] = read in the decompile; [inferred] = follows from verified rows, not read end to end;
[rule] = classified by a stated rule, not read site by site.

**Scope rule (unchanged):** T4 never applies to a household Sim, the selected Sim, or a Sim of unknown scope. Every
rule below is for NPC residents the model owns. Nothing here changes a save: the placeholder is EA's own object.

## 0. Short answer

- **ST-6 verdict: adults may use `Sim.Placeholder`. No blocker, eight rules (§1.5).** EA already sends NPC residents of
  every age through the base placeholder: an off-screen NPC age-up (`AgeUpNpcSituation`) creates a `SimPlaceholder`,
  destroys the Sim, ages the description with no Sim, and rematerialises it [verified]. No reader of
  `Sim.Placeholder` tests age; the only age test is in the caller that picks toddlers and babies [verified].
- **What EA's own use does not cover is time.** EA holds an adult placeholder for seconds to minutes; T4 holds it for
  hours or days and across saves. Three things go wrong only over that time: an elder never dies of old age (§2.2
  ageing), the career stands still (§2.2 careers), and a household switch leaves our placeholder behind as an orphan
  object (§1.4 P4).
- **RX-6 verdict: EA's description-driven systems are null-safe for a resident with no Sim object, with three
  hazard sites (§2.3).** Careers, school, ageing, death, relationship decay, NPC parties and story progression were
  read at their entry points; all test `CreatedSim` or are gated on a selectable owner. The hazards are two career
  sites that throw with no Sim and one situation that builds every missing teen.
- Census: 3,138 `CreatedSim` reads in the three gameplay assemblies (497 files); 285 have no null test by the
  heuristic of §2.1. Of those: 228 run only for a live Sim [rule], 12 are selectable or UI gated [verified], 4 are
  guarded on reading [verified], 3 are hazards [verified], **38 are not traced** (list in §2.4).

## 1. ST-6: the placeholder path for adults

### 1.1 EA already uses it for adults [verified]

`AgeUp.Execute` (AgeUp.cs:604-655) for an NPC with a home lot starts `AgeUpNpcSituation` (AgeUp.cs:647). There is no
age test on that branch, so it runs for child, teen, young adult and adult age-ups alike. The situation
(AgeUpNpcSituation.cs:15-125):

1. sends the Sim home and inside (`GoInsideLot`), then waits until it is not on screen
   (`IsSufficientlyOnScreenForHighLODSimulation` false);
2. `Sim.Placeholder.CreatePlaceholder(sim)` (base class, object name `SimPlaceholder`) and `RunDematerialize(sim)`;
3. polls every 2 sim seconds until `CreatedSim == null`, then `AgeUp.Execute` again, which with no Sim takes the
   uninstantiated path (`AgingState.AgeTransitionWhileUninstantiated`: cancel ageing alarms, defer the outfit build to
   the next instantiate);
4. on exit `CleanUp` calls `RunRematerialize` (or destroys the placeholder if a Sim exists or the game shuts down).

Its alarms are `AlwaysPersisted` and the placeholder is a field of the situation state, so EA expects a save in the
middle [verified flags; inferred intent]. A pregnant Sim leaves the situation at once (AgeUpNpcSituation.cs:66).

So "an adult NPC resident with a placeholder and no Sim" is a state vanilla reaches on every NPC age-up. Decision 6
asks for nothing EA does not do; it only holds the state longer.

### 1.2 Every reader of `Sim.Placeholder`, every caller of dematerialize / rematerialize [verified, grep of the six script assemblies named below]

| # | Site | What it does | Age or child-only assumption |
|---|---|---|---|
| R1 | `Sim.Placeholder` Sim.cs:44236-44339 | base class: description field, `CreatePlaceholder`, `RunDematerialize`, `DoDematerialize`, `RunRematerialize`, `Rematerialize` | none |
| R2 | `CaregiverRoutingMonitor.ChildPlaceholder` CaregiverRoutingMonitor.cs:22-74 | subclass with a 5-minute alarm: rematerialise when a teen-or-older Sim of the household is on the lot | **child only by design.** T4 must never use this subclass: any household member on the lot would rebuild the adult |
| R3 | `CaregiverRoutingMonitor.DematerializeChildren` :340-372 | NPC household, no caregiver at home: every toddler-or-younger Sim on the home lot becomes a `ChildPlaceholder`; reads placeholders **on the home lot** only to skip Sims that already have one | age test is on the Sim it demotes (`ToddlerOrBelow`), not on placeholders found. An adult placeholder there is ignored |
| R4 | `AgeUpNpcSituation.DematerializeAndAgeUp` | §1.1 | none (all ages) |
| R5 | `Household.PrepareToBecomeActiveHousehold` Household.cs:4232-4275 | household switch: for each non-child placeholder of the household, exits a matching `AgeUpNpcSituation`; then instantiates every member that has no Sim at the lot entry point | none. **Does not destroy a base placeholder that has no situation** (P4) |
| R6 | `GameplayMovingModel.TryInstantiateSim` / `FindPlaceholderForSim` GameplayMovingModel.cs:890-918 | moving: a placeholder for the description **on its home lot** is rematerialised, otherwise `InstantiateOffScreen` | none |

No other class in Sims3GameplaySystems, Sims3GameplayObjects, Sims3StoreObjects, UI, ScriptCore or SimIFace names
`Sim.Placeholder`. ts3fixes does not name it either.

### 1.3 What the two EA methods do [verified, Sim.cs:44287-44339]

- `DoDematerialize`: raises the Sim's autonomy-disabled count; loop: cancel interactions whose target is on the
  placeholder's lot, sleep 60 ticks, repeat while the Sim exists and has a current interaction; reset the Sim if it is
  not standing; copy position and facing to the placeholder; `Sim.Destroy`. If the Sim vanished meanwhile, the
  placeholder moves to `LotHome.Position` (this dereferences the home lot).
- `Rematerialize`: if the description is valid and has no Sim: `SimDescription.Instantiate(Position)`, facing,
  `AttemptToPutInSafeLocation(sendHome: false)`, outfit switch without spin. Then the placeholder destroys itself in
  every case. So a rematerialise of a description that already has a Sim is harmless: it only removes the placeholder.
- The base class has no alarm and no task. Nothing wakes an adult placeholder; whoever made it must rematerialise it.

### 1.4 What a save writes and what follows for T4

- `Sim.Placeholder` derives from `GameObject`, which carries `[Persistable]`; its one field (`mSimDescription`) has no
  opt-out [verified declarations; inferred that the field is written: `PersistableAttribute` usage flags not read to
  the end]. The object is a normal world object of the lot it stands on, saved and loaded with that lot.
- The Sim's own alarms are removed with the Sim (`AlarmManager.OnDispose`, AlarmManager.cs:680) and every buff is
  removed through its removal hook (`Sim.Dispose`, docs/341 §3) [verified]. Managers that live on the Sim object go
  with it: buffs, mood, motives, queue, posture, social component, inventory. Managers on the description stay:
  career, skills, traits, occult, celebrity, ageing, genealogy, relationships.
- EA has no load-time rebuild of missing residents (docs/324 §5.3), so a loaded adult placeholder stays until someone
  calls `Rematerialize`. That is the behaviour T4 needs.

Rules that follow (all for model-owned NPCs only):

| # | Rule | Why |
|---|---|---|
| P1 | Use the base class through `CreatePlaceholder` + `RunDematerialize` (it runs in its own one-shot task and may sleep). Never `ChildPlaceholder` | R2 |
| P2 | Demote only a Sim that is idle and standing (SD-3 safe point). | `DoDematerialize` waits with no bound while the Sim has a current interaction, and always sleeps 60 ticks once |
| P3 | The placeholder stands on the Sim's **home lot**, and the Sim has a home lot | R3 and R6 look only there (as NRaas does, docs/324 §5.6); `DoDematerialize` dereferences `LotHome`. EA's own order gets this for free: go home, go inside, then demote |
| P4 | Whenever a model-owned description gets a Sim by any path, destroy our placeholder. At load, destroy every placeholder we own whose description is invalid or has a Sim | R5 instantiates household members directly and leaves a base placeholder behind as a saved orphan. A switch to that household also makes its Sims household Sims: they must be live at once, which R5 does by itself |
| P5 | Rematerialise through `Placeholder.Rematerialize` (or the same `Instantiate(Vector3)` entry), then apply the model's snap position | it already uses the cheap entry (docs/324 §6.1) and removes the placeholder |
| P6 | An elder close to the end of its lifespan is not T4 eligible, and `AgingManager.AgeTransitionWithoutCake` is an on-demand guard site (promote first) | §2.2 ageing: with no Sim an elder is never killed |
| P7 | Pregnant Sims stay excluded (docs/324 §5.3) | pregnancy alarms are owned by the Sim object (Pregnancy.cs:1776-1877) and die with it; `AgeUp.Execute` also refuses a pregnant Sim |
| P8 | Career rules C-a and C-b of §2.3 | two career sites throw with no Sim |

### 1.5 Verdict per risk of docs/324 §5.6

| Risk (docs/324 §5.6) | Verdict | Basis |
|---|---|---|
| 1. NRaas missing-Sim rebuilds | **unchanged, not ST-6's** (ST-2 owns the NRaas sites). EA side confirmed: its own readers also look for the placeholder on the home lot only (R3, R6) | §1.2 |
| 2. Object-bound alarms are lost | **accepted with rules.** "As EA handles a placeholder child's" means: Sim-owned alarms are dropped, description-owned alarms keep firing and are null-safe (§2.2). Recreated at instantiate: trait alarms (`Sim.OnStartup`), career fix-up, `Pregnancy.Continue` (docs/341 §3). Not recreated: beeper alarms, skipping-work alarm, the Sim's social component state | §1.4, §2.2 |
| 3. Unguarded `CreatedSim` uses | **go with three hooks.** 285 unguarded reads by heuristic; 3 hazards, 38 untraced | §2 |
| 4. Double build | **unchanged.** R5 and the prom site (§2.3 M1) call `Instantiate` directly, so they pass the one-instance guard like every other caller | §1.2, docs/324 §5.5 |
| 5. Build cost on approach | not a static question (docs/325) | - |
| Inventory (docs/324 §5.6 decision) | **vanilla already loses it.** Every NPC age-up runs `Sim.Destroy` and `Instantiate(Position)` with `addInitialObjects: true`, so an NPC resident's inventory is re-seeded at each age transition [verified path; "destroy keeps nothing" inferred as in docs/324]. `SeedNPCInventory` is random: umbrella by chance, a random fruit, toy and trait book, a cane by chance (Sim.cs:52658, 52685, 52695-52704, 52739; Umbrella.cs:1208) [verified]. So the T4 inventory test compares item kinds, and each rematerialise draws from the script RNG (add to docs/341 §5 as a hazard for DT-1) | §1.1 |
| Adults at all (decision 6) | **allowed** | §1.1 |

The second gate of decision 6, the repro `farsim-placeholder-adult`, stays as written. Add two steps to it: a
household switch to the demoted Sim's household (P4: one Sim, no orphan placeholder), and an NPC age-up of a demoted
adult (uninstantiated path, deferred outfit at rematerialise).

## 2. RX-6: who assumes a resident has a Sim object

### 2.1 Method

A script over the local decompile of Sims3GameplaySystems, Sims3GameplayObjects and Sims3StoreObjects classified
every textual `CreatedSim` read by how it is used: null test, dereference with or without a null test of the same
expression earlier in the method, copy to a local that is or is not tested, or passed on. Then the unguarded rows
were sorted by what drives the method.

| Kind | Rows |
|---|---:|
| null test | 666 |
| dereference, tested earlier in the method | 596 |
| copy to a local, tested | 650 |
| passed on (argument, return, compare) | 885 |
| copy, not used in the method | 56 |
| **dereference, no test** | **211** |
| **copy used with no test** | **74** |
| Total | 3,138 |

Limits: text heuristic, not data flow. A test in a caller or in another operand of the same condition is missed
(4 such rows were found by reading), and reads through wrapper properties (`Occupation.OwnerSim`, `Skill.SkillOwner`
alone) are not counted. The count differs from docs/324 §5.6 (293 + 86) and docs/341 §3 (2,318 lines, one assembly)
because the three assemblies and a different guard test are used. "Passed on" rows are safe only if the callee
accepts null; that was checked for the systems of §2.2, not for all 885.

### 2.2 Systems read at their entry points [verified unless marked]

"With no Sim" = what EA's code does today for a resident description whose `CreatedSim` is null.

| System | Driver | With no Sim | Verdict for T4 |
|---|---|---|---|
| Careers: day schedule | `AlarmManager.Global` alarms owned by the **description**, never persisted, re-armed at load (Career.cs:4322-4369, Occupation.cs:3364, 3413) | Two hours before: `OwnerSim == null`, so no go-to-work push (Occupation.cs:3367-3386). Start: aborted unless the owner is selectable (`ShouldAbortStartOfDayLogic`, Occupation.cs:449-461), so the unguarded `OwnerSim` use at :3315 is not reached. Hour after, fast walk, run: null test. End: demotion check runs on stored performance; fired / demoted paths null-test (Career.cs:4152-4202). Retirement pension is paid to the household funds with no Sim (Occupation.cs:3827-3839; the notification is selectable only) | **safe, but the career stands still:** no work interaction, so no pay, no performance change, no promotion. Missed-work loss is applied in `Career.UpdatePerformance` (Career.cs:4903-4960) from `UpdateCareer`, which the live Sim's own update calls (docs/341 §1.6; that caller not re-read), so it does not run either. Not equal to vanilla for a long stay: FS-18 career drift has to supply it |
| Careers: level change | `PromoteSim` / `DemoteSim` / `SetLevel`, also from EA story progression (`PromoteDemote.Execute`, PromoteDemote.cs:208-224, on a description) | base `Career` paths null-test; **one override does not** (§2.3 C-b) | hazard C-b |
| Careers: end-of-day overrides | `LawEnforcement`, `Medical`, `Film`, `ProSports` override the end alarm | Medical null-tests (Medical.cs:1047-1057); Film and ProSports touch no Sim; **LawEnforcement does not** (§2.3 C-a) | hazard C-a |
| School | same description-owned alarms (School.cs:1311-1436) | two-hours-before and end handlers return at once with no Sim; start handler null-tests | **safe, school stands still** (no grades, homework, detention). Children and teens should stay T3 until a school drift exists [inferred recommendation] |
| Ageing | one global daily alarm over ageing states (`AgingManager.DayPassedCallback`, AgingManager.cs:1193, 1682) | age-up with no Sim is EA's own path (`AgeUp.Execute`, AgeUp.cs:610-620; outfit built at the next instantiate in `AgingState.PreInstantiateSim`, AgingState.cs:631-670, which may sleep) | **safe** (EA precedent, §1.1). Age-up ordering of docs/324 §5.5 stays |
| Death by old age | `AgingManager.AgeTransitionWithoutCake` (AgingManager.cs:2230-2274) | elder with no Sim: nothing is killed; only the ageing alarms are cancelled. Ghost elder: grave flag set, no return-to-grave | **not safe to leave: a T4 elder does not die.** Rule P6. EA has a description-level kill (`KillSim.KillSimExecute(SimDescription, DeathType)`, KillSim.cs:157-187: null-tests the Sim, removes from the household, `Urnstone.FinalizeSimDeath`), which the model could call instead of promoting [verified body up to :194; side effects after that not read] |
| Death, other | `Urnstone` and `GrimReaperSituation` paths | all `CreatedSim` reads tested except inside interactions | live Sims only [rule] |
| Relationship decay | one global daily alarm, `Relationship.DecayAll` over all description pairs (SocialComponent.cs:2381-2393, Relationship.cs:1142-1250) | runs on descriptions; the two `CreatedSim` reads are null-tested (:1194-1195). `LearnInfoAboutEachOther` sets known-info flags with notifications suppressed, which short-circuits the unguarded `mLearner.CreatedSim.IsActiveSim` reads (InformationLearnedAboutSim.cs:283-316); the events are sent with a null Sim | **safe** [verified code; inferred that null-Sim events are harmless: EA already sends them for homeless households, whose members have no Sim] |
| Relationship change from play | conversations, socials | live Sims only | n/a |
| NPC parties | `NpcParty` | all 9 `CreatedSim` reads are copies with a null test | **safe**; who gets invited is docs/342 I1-I5 |
| Story progression | `Sims3.Gameplay.StoryProgression` actions (23 reads) | all tested; actions that need a Sim instantiate one (AddSim, AddRoommate, MoveInWithPartner) or skip | **safe**, except `PromoteDemote` through C-b |
| Services | service Sims live in the service household and are never T3 / T4 (docs/324 §3.5); `Service.FindSimForAssignment` reuses only descriptions with no Sim (Service.cs:988) | n/a for residents | **not traced:** a service call to a lot whose residents are T4 (who opens the door, who pays) |
| Pregnancy | alarms owned by the mother's Sim (Pregnancy.cs:1776-1877) | alarms die with the Sim; time stops | excluded (P7) |
| Health (Seasons) | `HealthManager.GetSick` null-tests (HealthManager.cs:446-452) | no incubation alarm with no Sim | safe; `TemperatureChange` :485 untraced |
| Household switch, moving | §1.2 R5, R6 | missing members are instantiated | safe with P4 |

### 2.3 Hazard and materialise sites [verified code; reach inferred, since vanilla residents always have a Sim]

| # | Site | Problem with no Sim | Rule |
|---|---|---|---|
| C-a | `LawEnforcement.RegularWorkDayEndAlarmHandler` LawEnforcement.cs:584-598 | description-owned end-of-day alarm; if the career holds a beeper object (`SpecialAgent` branch) and a random chance hits, it calls `OwnerDescription.CreatedSim.AddAlarm` with no test: null reference inside the alarm callback, after the base handler ran | a Sim whose career holds a beeper is not T4 eligible, or a prefix hook skips the beeper draw for a model-owned description with no Sim |
| C-b | `Music.SetLevel` Music.cs:376-386 | on reaching the rock or symphonic levels it reads `CreatedSim.TraitManager` and passes the Sim to `ActiveTopic.AddToSim` with no test. Reached from `PromoteSim` / `DemoteSim`, also from EA story progression's `PromoteDemote` | the model never calls EA's level setters on a description with no Sim (credit career drift at go-live); `PromoteDemote.Execute` on a model-owned description with no Sim promotes it first or is skipped (add to the docs/324 §4 site table). Other `SetLevel` overrides were not all read (Q2) |
| C-c | `Medical.RemoveLotAlarm` Medical.cs:777-784 | removes a Sim-owned alarm through `CreatedSim` with no test when the stored handle is still set; the handle outlives the destroyed Sim [inferred reach: caller chain from `LeaveJobNow` not read] | clear or re-check at go-far; covered if careers with a beeper are excluded (same class) |
| M1 | `PromSituation.PushAllNpcTeensToLocationOneShot` PromSituation.cs:1036-1056 | **materialises** every teen resident not at boarding school (`InstantiateOffScreen` at the home lot), then reads the new Sim with no test | on-demand guard site: prom night promotes all model-owned teens; our hook must never return null there |
| M2 | `Household.PrepareToBecomeActiveHousehold` (R5) | materialises every missing member of the new active household | guard site, plus P4 |
| M3 | `GameplayMovingModel.TryInstantiateSim` (R6) | materialises through the placeholder or off screen | guard site |

110 files in the three assemblies call `SimDescription.Instantiate` or `InstantiateOffScreen` [verified grep
count; most build new Sims]. They all funnel into the one entry docs/324 §5.5 guards, so the list above names only
the ones found on the systems RX-6 asks about.

### 2.4 The 285 unguarded reads by area

Columns: live = runs only while the Sim exists [rule: buff instance code, interaction classes, UI classes, active
careers, skill code, object code, work and school interaction callees]; gated = selectable or UI caller [verified];
guarded = test found on reading [verified]; hazard = §2.3; untraced = not read.

| Namespace (`Sims3.Gameplay.`) | live | gated | guarded | hazard | untraced |
|---|---:|---:|---:|---:|---:|
| Careers | 47 | 12 | 0 | 3 | 0 |
| Skills | 56 | 0 | 0 | 0 | 0 |
| ActiveCareer.ActiveCareers | 47 | 0 | 0 | 0 | 0 |
| Objects.* (both object assemblies, Store) | 50 | 0 | 0 | 0 | 0 |
| ActorSystems | 16 | 0 | 0 | 0 | 6 |
| Socializing | 0 | 0 | 0 | 0 | 9 |
| Situations | 0 | 0 | 2 | 0 | 4 |
| Core (thumbnails) | 0 | 0 | 0 | 0 | 4 |
| Roles | 0 | 0 | 2 | 0 | 0 |
| other (14 namespaces) | 12 | 0 | 0 | 0 | 15 |
| **Total** | **228** | **12** | **4** | **3** | **38** |

The prom site M1 is not among the 285: its read follows a null test and an instantiate, so the heuristic calls it
guarded. It is a hazard only if the instantiate returns null.

Untraced methods (the RX-6 rest, 38 rows in 36 methods): `AgingState.AgeTransitionCompleted` (age-down branch),
`HealthManager.TemperatureChange`, `MidlifeCrisisManager.CreateInitialState`, `OccultVampire.FixUpOccult`,
`PetManager.CheckForSpecialCaseSleepingSpot`, `SimBuilderTask.SwapModel`, `SimDescription.IsWearingMaternityOutfit`,
`TeenMoodSwings.GetChanceForMoodSwing`, four `ThumbnailHelper` methods, `DynamicChallenge.OnCompletion`,
`BoobyTrapComponent.CanTriggerTrap`, `SimBuilderTaskSculpture.Simulate`,
`Opportunity.ForceAcquaintanceAndInstantiateOffScreen`, `GrimReaperSituation.PostChessMatch.Init`,
`FieldTripSituation` constructor, three `ImprovedProtestSituation.ProtestOutcomeVictory` methods, four
`ActiveTopicTestFunctions` tests, `Conversation.ResetRomanticContext`, three `InformationLearnedAboutSim` members
(two of them read in §2.2), two `OnlineDatingManager` methods, two `ReturnHomeBehaviorInHomeWorld` methods, two
`MarineLifeState*` `Run` methods, `GameStates.ImportTravellingHousehold`. Most look like live-Sim or household-only
code by name [inferred]; none is on a path §2.2 found to run for a description with no Sim.

## 3. What later packages take from this

- **RX-9 (verdict):** stage "L2 Sims" (T4) is go on the script side for adult NPC residents, opt-in, with rules
  P1-P8. Sites that force "materialise": M1-M3 plus every `Instantiate` caller through the one entry. Kill input: none
  found; the open items are Q1-Q5.
- **FS-16 (T4 placeholder variant):** P1-P5 are the build rules; eligibility adds: has a home lot, idle and standing,
  not pregnant, not an elder near the end of its lifespan, no beeper career, inventory test by kind (docs/324 §5.6).
- **FS-17 / ST-2:** the EA side agrees with NRaas on "placeholder on the home lot".
- **FS-18 (career drift):** the career of a T4 Sim is frozen in vanilla code, so drift is needed for a long stay, and
  it must be credited at go-live, not through `PromoteSim` on a bare description (C-b).
- **SD-1 / DT-1:** rematerialise draws from the script RNG (`SeedNPCInventory`); add to the allowed-inputs list.
- **docs/324 §4 site table:** add `PromoteDemote.Execute` (C-b) and `AgingManager.AgeTransitionWithoutCake` (P6).
- **Repros:** `farsim-placeholder-adult` plus the two steps of §1.5; `farsim-elder-death` (an elder model-owned Sim
  reaches the end of its lifespan: dies as in vanilla, or is promoted first).

## 4. Open questions

- **Q1.** Is `mSimDescription` of a base placeholder really written and read back (attribute inheritance not read to
  the end)? The save step of `farsim-placeholder-adult` answers it; static: read `PersistableAttribute`'s usage flags
  and the persistence reader in ScriptCore.
- **Q2.** `SetLevel`, `PromoteSim`, `GivePromotionRewardObjects` and `OnPromoteDemote` overrides in the other career
  classes were not all read; only the rows the heuristic flagged were. A census of the overrides closes C-b.
- **Q3.** Services called to a lot whose residents are T4, and bills / mail for such a household, are not traced.
- **Q4.** The 38 untraced rows of §2.4.
- **Q5.** Does `CaregiverRoutingMonitor` run for an NPC household whose adults all went T4, so that its toddlers
  become child placeholders by EA's own rule (R3)? The branch at :255-270 says yes when no caregiver is home; the
  monitor's start conditions were not read. Either answer is consistent; the repro should show which.
- **Q6.** Timed buffs with side effects at removal (docs/341 Q3) is unchanged: `Sim.Dispose` runs every removal hook.
- **Q7.** EA 1.69: the managed sites were not diffed against the retail DLLs.
- **Q8.** Mods: the census covers EA's assemblies only. The same pass over the top-100 set is RX-2b's format.

## 5. How to re-check

- Open the `file:line` rows under `game-copy/decompiled/Sims3GameplaySystems` (local only).
- The site list with class, method, line, receiver and class of each unguarded read is
  `docs/data/createdsim-unguarded-census.csv` (285 rows; method names and line numbers only, no code).
- The census script is a throwaway text pass (regex over the decompile, enclosing method by indent); its rules are the
  ones in §2.1 and the `basis` column of the CSV.
