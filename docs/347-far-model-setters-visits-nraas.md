# 347. Far model: EA setters for drift, EA visit logic, NRaas sites (ST-2, ST-3, ST-13) (2026-11-26)

Static only. Nothing built, nothing run. Packages ST-2, ST-3 and ST-13 of docs/332 §2.2 (phase R of docs/340 §6.1).
Feeds FS-3 (drift and visit proposals in shadow), ME-10 (EA visit-rate baseline), FS-14 and FS-17 (NRaas precedence
and rebuild overrides). Ledger: docs/51 §616. Handoff: `docs/handoff/st2-st3-st13-1126.md`.

Sources:
- EA: decompiled Steam 1.67 script DLLs (local only, never committed). `file:line` below is relative to
  `game-copy/decompiled/Sims3GameplaySystems/` unless a path starts with `Sims3GameplayObjects/`. Folder prefixes are
  shortened: `Actors/Sim.cs` = `Sims3.Gameplay.Actors/Sim.cs`.
- NRaas: https://github.com/Chain-Reaction/NRaas at commit `2f57186` (no license; read and cited for behaviour only,
  no code copied). `SP/` = `NRaasStoryProgression/StoryProgressionSpace/`, `OW/` = `NRaasOverwatch/OverwatchSpace/`.
- REA was not needed: all three packages are managed-code questions. No native address is claimed here.

Markers: [verified] = read in the decompiled code or the NRaas source. [inferred] = reasoned, with a ledger row.
Counts from regex searches are match counts, not classified sites.

Hard rule for everything below: the far model writes only to Sims it owns. A household Sim, the selected Sim and a Sim
of unknown scope are never model-owned, and a relationship with such a Sim on either side is never written
(docs/324 §2.7, shared predicate `Fixes.HouseholdExempt`). This doc changes no behaviour.

## 0. Short answer

- **ST-3:** every drift kind has one EA setter that needs no Sim object and shows nothing for an NPC pair, as long as
  the drift stays inside the current relationship state and below a skill level-up (§1). Motives are the exception:
  they live on the Sim object, so they can only be applied at go-live.
- **ST-13:** EA starts a visit in one place, `VisitSituation.Create`, reached mostly from `Sim.LotChanged`. Who comes
  is decided by meta autonomy scoring `VisitLot` on lots the Sim knows. EA's score has **no distance term and no
  relationship-strength term**; the rate is set by three cooldowns and a tuning table that is not in the script DLLs
  (§2). The decay in docs/324 §3.9 is therefore our own addition, not a copy of EA.
- **ST-2:** NRaas StoryProgression reaches a body through one helper (`ManagerSim.Instantiate`) and nine push helpers
  in `ManagerSituation`. Its drift writers use the same EA setters as §1. Overwatch has two alarms that will fight a
  parked T3 Sim (`StuckCheck`, `ResetCheck`) on top of the known missing-Sim rebuild (§3).

## 1. ST-3: EA setters for seeded drift

### 1.1 Setter per drift kind

| Kind | Setter to use | Needs Sim object | What it does besides the write | Line |
|---|---|---|---|---|
| Relationship liking, exact value | `LongTermRelationship.SetLiking(value)` | no | clamps to -100..100, then `UpdateLTR` (state check, see §1.2) | `Socializing/LongTermRelationship.cs:610` [verified] |
| Relationship liking, EA-scaled delta | `LongTermRelationship.UpdateLiking(change)` | no | `Relationship.UpdateKnown` (sets the has-met bit, `kMetSim` event when a body exists), then scales the delta by traits, species, attraction and the room the first Sim stands in; clamp; `UpdateLTR`; `kMarriageOnTheRocks` event for a spouse below -30 | `:275`-`:467` [verified] |
| Skill points | `Skill.AddPoints(points)` on an existing skill | no | multiplies by `ComputeSkillGainMod` (traits, logic boost, per-skill modifiers); adds XP for an XP-based career; `UpdateProgress` (level-up check, §1.3) | `Skills/Skill.cs:611`-`:621` [verified] |
| Skill points, capped level | `Skill.AddSkillPointsLevelClamped(points, level)` | no | same, stops at the given level | `Skills/Skill.cs:752` [verified] |
| Career performance | `Career.AddPerformance(change)` | no | positive change x set-object multiplier; school buff multipliers and the `kCareerPerformanceChanged` event only when a body exists; clamp -100..100. **No promotion or demotion check** | `Careers/Career.cs:4968`-`:4990` [verified] |
| Household money | `Household.ModifyFamilyFunds(delta)` | no | cap 99,999,999 (notice only for the active household); `FamilyFundsChanged` delegate; per instantiated member: low-funds topic and `kLowFamilyFunds`, or `kHighFamilyFunds`, and `FamilyFundsChangedEvent` | `CAS/Household.cs:1210`-`:1260` [verified] |
| Motives | `Motives.SetValue(commodity, value)` | **yes** (`Sim.Autonomy.Motives`) | clamps to the motive's tuning range; `Motive.SetValue` writes value, delta and the UI arrow; refused when decay is off for that motive or a lifetime reward / cheat holds it | `Autonomy/Motives.cs:1156`, `Autonomy/Motive.cs:1636`-`:1680` [verified] |
| Age | none: do not write | - | `AgingManager.DayPassedCallback` adds one day to every `AgingState` from a global alarm, body or not | `ActorSystems/AgingManager.cs:1191`, `:1682` [verified] |

Notes per kind:

- **Relationship.** Use `SetLiking(current + delta)` on a relationship that already exists
  (`Relationship.Get(a, b, createIfNone: false)`), so the applied delta is exactly the seeded one. `UpdateLiking`
  rescales the delta from live state (traits, the room of Sim A), which breaks "same save, same drift".
  [verified for the scaling; choice is a recommendation]
- **Never create a relationship from drift.** `Relationship.Get(..., true)` plus `UpdateKnown` fires `kMetSim` and
  `MakeAcquaintances` adds map tags and known lots (`Socializing/Relationship.cs:692`-`:737`). [verified]
- **Skill.** `AddPoints` returns false and writes nothing at max level or when the owner cannot learn (robots without
  the chip, `Skills/Skill.cs:1212`). "Never a skill the Sim has not started" (docs/324 §2.7) maps to
  `SkillManager.GetSkill`, not `SkillManager.AddElement` (`Skills/SkillManager.cs:689`), which creates the skill and
  runs `OnSkillAddition` (`kSkillLearnedSkill` event). [verified]
- **Career.** Promotion runs only from EA's work tick (`Career.UpdateCareer` -> `PromoteIfShould`,
  `Careers/Career.cs:2845`-`:2864`), from `WorkInRabbitHole` (`Careers/WorkInRabbitHole.cs:310`) and from
  `PromoteSim` (`:4113`); demotion only from `DemotionChecker` (`:2988`, `:3135`). So a drift write can leave
  performance at 100 and EA promotes at its next work tick, as docs/324 §2.7 wants. [verified] `School` and XP-based
  careers route through `UpdatePerformanceOrExperience` (`Careers/Career.cs:2157`,
  `Careers/XpBasedCareer.cs:351`); XP careers are out of the drift's scope. [verified they differ; scope is a choice]
- **Money.** docs/324 §3.10 says the model does no spending and no income. The setter is listed for completeness.
  EA's own pay path is `Occupation.PayOwnerSim` -> `SimDescription.ModifyFunds` (skips NPC roommates) + `GotPaidEvent`
  (`Careers/Occupation.cs:3429`, `CAS/SimDescription.cs:3905`). [verified]
- **Motives.** `Motive.SetValue` adds no buff and runs no distress: buffs and failure (`UpdateMotiveBuffs`,
  `MotiveDistress`, `Autonomy/Motive.cs:1909`, `:2110`) run from the next motive update. A go-live write of a
  low value can therefore trigger a failure on the first tick; keep go-live values above the distress range.
  [verified for the split; the "first tick" consequence is inferred, I-616-2]

### 1.2 Side effects of a relationship write

`UpdateLTR` (`Socializing/LongTermRelationship.cs:247`) always runs: state recompute, UI refresh of both bodies if
present (`Relationship.UpdateSocializingUI`, `Socializing/Relationship.cs:611`), a "worry about relationship" topic
for partners below zero, and the best-friend check. [verified]

If the liking crosses a state threshold (`CalculateLTRState`, `:1279`), `ChangeState` (`:772`) runs. That is where
every visible effect sits: [verified]

| Effect in `ChangeState` | Condition | Line |
|---|---|---|
| "Relationship decayed" notification | one side selectable, friendly state dropping to distant friend or worse | `:967`-`:997` |
| Partner set or cleared, breakup / divorce handling, midlife-crisis hooks | new state sets partner, or old partner state lost | `:823`-`:840` |
| Marriage, engagement, split-up, enemy events and topics | romantic or enemy states | `:933`-`:961`, `:1013`-`:1040` |
| Charisma counters, lifetime opportunity check, **liking +25** when either is a charisma celebrity | stranger -> acquaintance | `:1195`-`:1220`, `Skills/Charisma.cs:363` |
| `kRelationshipLTRChanged` to each side that has a body | any state change | `:1226`-`:1237` |
| Best-friend-lost notices | selectable side | `:1151`-`:1176` |
| Celebrity points, romance visibility and reputation | non-pets | `:882`, `:867`-`:868` |

Consequence for FS-3: clamp each drift step so the new liking stays on the same side of every threshold of the pair's
current state (thresholds from `LTRData.Liking` per state, read at runtime, not copied). Then `ChangeState` returns at
its first check and the write has no event, no notification and no partner change. [verified for the early return at
`:776`; the clamp is the design rule]

EA's own daily decay (`Relationship.DecayAll`, `Socializing/Relationship.cs:1142`, called from
`Socializing/SocialComponent.cs:2392`) keeps running over all relationships and scales NPC-NPC pairs by
`kNPCRelationshipDecayMultiplier` = 0.001 (`Socializing/SocialComponent.cs:212`). The drift adds to it; it does not
replace it. [verified]

### 1.3 Side effects of a skill level-up

`UpdateProgress` raises the level while points allow, calls the skill's level-up delegates, then `SkillLeveledUp`
(`Skills/Skill.cs:849`): [verified]

- With a body: resets the ambitious-trait alarm, removes the anxious buff, adds an "acquired skill" topic, sends
  `kSkillLevelUp`.
- Only for a selectable Sim: the level-up sound, the level text notification, the certificate in the mailbox at max
  level.
- Without a body: nothing but the level and the delegates. `SkillManager.UpdateSkillUI` still runs.

So an NPC level-up at snap is silent for the player, with or without a body. Subclasses override `SkillLeveledUp`
and `AddPoints` (both virtual); those overrides were not read. [inferred safe, I-616-3]

## 2. ST-13: where EA starts visits, conditions, rate

### 2.1 Start point

`VisitSituation.Create(guest, lot)` (`Situations/VisitSituation.cs:1522`) is the only constructor path. It returns
null for a null or community lot, the guest's own home, a lot without a household, a guest with no current lot and
wild animals; Bonehilda and non-playable ghosts are greeted with no situation; an existing situation for the same
guest and lot is returned as is. [verified]

Callers: [verified]

| Caller | When | Line |
|---|---|---|
| `Sim.LotChanged` | any Sim enters a lot that is not its home and has a household | `Actors/Sim.cs:53996` |
| `Sim.RoomChanged` | same test with `IsResidentialOwnedBy`, on a room change | `Actors/Sim.cs:54115` |
| `VisitLot.Run` (actor and followers) | end of the `VisitLot` interaction | `Core/VisitLot.cs:402`, `:410` |
| `Household` member move | host or pet changes home lot during a visit | `CAS/Household.cs:2905`, `:2909` |
| `MovingSituation`, `SplitHousehold` | leftover Sims after a move or split | `Moving/MovingSituation.cs:546`, `StoryProgression/SplitHousehold.cs:191` |
| Interior designer job, ensorcelled buff, sneak-in | special cases | `ActiveCareer.ActiveCareers/InteriorDesigner.cs:381`, `ActorSystems/BuffEnsorcelled.cs:133`, `Situations/SneakOntoLotSituation.cs:166` |

So docs/324 §3.9 stands: the model pushes `VisitLot` and EA creates the situation at arrival. A model that placed a
Sim on the lot by any other means would still get a `VisitSituation` from `LotChanged`.

### 2.2 Who comes: meta autonomy

- `VisitLot.Definition` is a `MetaInteractionScoredByVenue` (`Core/VisitLot.cs:24`) advertised by every lot
  (`Core/Lot.cs:3884`). [verified]
- A Sim scores it only in the meta pass (`Autonomy.ScoreInteractionsForMetaAutonomy`, `Autonomy/Autonomy.cs:2272`,
  called at `:2104`), over the public meta objects plus the lots that Sim knows (`:2282`-`:2283`). The pass is off
  when `AllowedToRunMetaAutonomy` is false, autonomy is disabled or a situation rules it out (`:2255`). [verified]
- Lots a Sim knows: its saved known-lot ids plus the player's neighbours (`Autonomy/Autonomy.cs:772`-`:788`). A lot
  becomes known when two instantiated Sims with different homes meet (`Relationship.MakeAcquaintances`,
  `Socializing/Relationship.cs:717`-`:735`). The player's neighbours are the 3 nearest occupied lots to the active
  Sim's home (`Lot.UpdatePlayerNeighbors`, `Core/Lot.cs:9207`-`:9233`, `kNumPlayerNeighborHouseholds`, `:1033`).
  [verified]

### 2.3 Score

Outside local autonomy the score is `Lot.HowMuchItWantsSimToCome(sim, hour)`
(`Interactions/MetaInteractionScoredByVenue.cs:10`-`:25`, `Core/Lot.cs:2796`). For a residential lot: [verified]

1. 0 when the Sim is on the lot or it is the Sim's home.
2. Tuning row by lot kind: `VisitPlayer` (the player's home), `VisitNeighbor` (one of the 3 neighbours), `VisitOther`.
3. `desired` = that row's hourly desired-Sim curve (`MetaAutonomyTuning.DesiredNumSims`,
   `Autonomy/MetaAutonomyTuning.cs:287`), capped at 4 / 9 / 13 / 20 by processor level (`:303`-`:306`). A gig
   within 2 h overrides this.
4. 0 unless the hours since `Lot.TimeOfLastVisit` reach `kMinTimeSinceLastVisitForActiveLot` = 24 (active
   household's lot) or `kMinTimeSinceLastVisitForNPCLot` = 3 (`Core/Lot.cs:788`, `:792`, `:2834`-`:2837`).
5. 0 unless fewer Sims reference the lot than `desired` (`:2839`).
6. On the active household's lot: 0 when the Sim has a relationship below zero with any household member
   (`:2842`-`:2850`).
7. Score = `desired` x `MetaAutonomyTuning.GetIntensity` (`Autonomy/MetaAutonomyTuning.cs:207`): 2000 when the
   active Sim is at or heading to the venue, 0 for an excluding trait, else the row's intensity x day-of-week x
   trait x rich x celebrity factors.

What is **not** in the score: distance or travel time, and how much the visitor likes the household (only the
"below zero" veto in step 6). A stranger who knows the lot scores like a best friend. [verified inside
`HowMuchItWantsSimToCome` and `GetIntensity`; that the shared autonomy scorer adds no distance term for meta
interactions is inferred, I-616-1]

### 2.4 Conditions

`VisitLot.Definition.Test` (`Core/VisitLot.cs:86`): not already greeted on the lot, then `Lot.CommonVisitTest`
(`Core/Lot.cs:4462`-`:4621`, returns true at `:4620`): [verified]

| Condition | Applies to | Line |
|---|---|---|
| Selectable Sims visit autonomously only at the highest autonomy level | autonomous | `:4464` |
| Days since the Sim's `LastAutonomousVisitDate` >= `kAutonomousVisitFrequencyInDays` = 3 | autonomous | `:4468`-`:4474`, `Core/Lot.cs:776` |
| Houseboat must be docked | autonomous, inside the same block | `:4475`-`:4482` |
| Not a community lot, not the Sim's own lot; a household or a callbox exists | all | `:4484`-`:4508` |
| Celebrity lot: needs access unless an NPC party runs | all | `:4510` |
| Not `VisitSituation.IsTooLateToVisit` (24:00 to 05:00) | all except the Sim finder | `:4515`, `Situations/VisitSituation.cs:862`, `:866` |
| Not banned from a visible NPC party | all | `:4520` |
| Someone is on the lot and at home, unless the Sim has a job there or a callbox exists | all | `:4525`-`:4571` |
| A child visitor needs a child living there | autonomous | `:4574`-`:4578` |
| Not already inside the lot | all | `:4580` |
| Not the last one at home on a lot neighbouring the player | autonomous | `:4584`, `Actors/Sim.cs:46312` |
| Less than 1 day since the Sim's `LastVisitedSomeone` | autonomous | `:4588`-`:4595` |
| NPC celebrity to the active household: a member on the lot must be allowed to socialise with it | autonomous | `:4600`-`:4618` |

The three timestamps are written by `VisitLot.Run`: `LastAutonomousVisitDate` at the start of an autonomous run
(`Core/VisitLot.cs:157`), `Lot.TimeOfLastVisit` on the way in (`:251`), `LastVisitedSomeone` at the end (`:390`; also
`Abstracts/Door.cs:922`). All three are saved fields (`Actors/Sim.cs:45189`, `:45195`, `Core/Lot.cs:81`). [verified]

### 2.5 Rate formula note

No visit is scheduled and there is no explicit probability. The rate is what the meta pass yields under these limits:
[verified limits; the resulting rate is inferred, I-616-4]

- per visitor: at most one autonomous visit per 3 sim-days, and none within 1 day of any visit;
- per NPC lot: a new autonomous visitor at most every 3 sim-hours;
- **per active-household lot: at most one autonomous visit start per 24 sim-hours**, and only while fewer Sims
  reference the lot than the `VisitPlayer` curve wants at that hour;
- hours: never between 24:00 and 05:00;
- candidates: only Sims that know the lot (met a household member while both had a body) or the 3 neighbours.

For the far model (docs/324 §3.9):

- `ea_ok` should call `Lot.CommonVisitTest` through `VisitLot.Definition.Test` with `isAutonomous: true`, and the
  proposal should respect the lot's 24 h gate (`TimeOfLastVisit`), or the model will propose more visits than EA can
  make. The push itself must be flagged autonomous so `VisitLot.Run` writes `LastAutonomousVisitDate`.
- The household-lot ceiling is about one visit start per sim-day in total, whatever the size of the far friend set.
  `base` is therefore a share of that one slot, not a per-friend rate.
- `decay(T)` and `rel(LTR)` have no EA counterpart. They are our choice, allowed because the model only picks which
  far friend takes the slot EA would have given to someone.
- The `VisitPlayer` / `VisitNeighbor` / `VisitOther` rows (hourly curves, intensity, encouraged days and traits) are
  read from the `MetaAutonomy` tuning resource (`Autonomy/MetaAutonomyManager.cs:104`-`:182` lists the columns). The
  values are not in the script DLLs. ME-10 measures the outcome; reading the resource would give the curve itself.

In-visit timers, for the shadow comparison: waits before giving up 0.25-0.75 h not invited, 1-2 h invited, 2-3 h
foreign visitor (`Situations/VisitSituation.cs:799`-`:819`); minimum stay 1 h, leave check every 0.5 h (`:838`,
`:987`); invite-in needs liking above -20 (`:791`). [verified]

### 2.6 Other visit starters

[verified]

- Phone: the invite calls push `VisitLot` at user-directed priority (`Sims3GameplayObjects/Objects.Electronics/Phone.cs:396`,
  `:2617`, `:3027`, `:3137`, `:3268`, `:8554`).
- Play dates (`ChildAndTeenUpdates/PlayDateManager.cs:274`), jobs at a lot (`Careers/Job.cs:681`-`:708`).
- Welcome wagon: 2-3 neighbours within 150 m after a move-in (`Situations/WelcomeWagonSituation.cs:250`-`:266`,
  created from `PlayFlowModel.cs:922` and `Core/Mailbox.cs:1740`).
- NRaas StoryProgression `ManagerSituation.PushVisit` (`SP/Managers/ManagerSituation.cs:1121`), §3.

## 3. ST-2: NRaas StoryProgression and Overwatch on model-owned Sims

This is the rest after the audit in docs/318 and docs/324 §4. Those facts (two rebuild triggers, the rebuild task
compiled into nine assemblies, the instantiation check, alarm marking) are not repeated.

Size of the surface, regex matches over the 9 StoryProgression projects and Overwatch: [verified counts]

| Kind | StoryProgression (all modules) | Overwatch |
|---|---|---|
| `CreatedSim` uses | 535 | 22 |
| Interaction pushes | 192 | 0 |
| Relationship writes | 69 | 9 |
| Funds writes | 102 | 2 |
| Career writes | 40 | 0 |
| Skill point writes | 40 | 5 |
| Motive writes | 20 | 2 |
| Position / reset / world add-remove | 35 | 13 |
| World-wide enumerations | 62 | 72 |

### 3.1 StoryProgression: site table

| Site | Kind | What it does | Far-model answer | Line |
|---|---|---|---|---|
| `ManagerSim.Instantiate(sim, lot, moveIn)` | build | returns at once when a body exists; else builds off-lot through the shared instantiation helper, then turns meta autonomy on and restores motive decay | T3: no-op. T4: hook by name, route through the one-instance guard (docs/324 §5.5). The post-build "meta autonomy on, decay restored" writes must run before our go-live write, not after | `SP/Managers/ManagerSim.cs:1122`-`:1145` |
| `ManagerSituation.PushVisit` | build + push | builds off-lot itself (not through `ManagerSim.Instantiate`), then pushes a visit | second build entry: needs its own hook, or the hook on the shared helper | `SP/Managers/ManagerSituation.cs:1121`-`:1150` |
| `ManagerSituation.PushInteraction`, `PushGoHere`, `PushGoHome`, `PushMassVisit`, `PushToRabbitHole`, `GreetSimOnLot`, `PushGathering`, `PushMeetUp` | build + push | each ensures a body, then queues an interaction or starts a situation | promote first (on-demand guard), then let it run. These 8 plus `PushVisit` are the choke points for the 192 push sites; scenarios call them, they do not queue directly as a rule [inferred, I-616-5] | `:183`, `:228`, `:249`, `:801`-`:860`, `:894`, `:979`-`:1001`, `:1330`, `:1369`-`:1384` |
| `ManagerHousehold.MoveSim` | repair + build | recreates a body's motives when missing, fixes a null current lot, builds on move-in | promote first; no motive-value write here (docs/324 §4 called it a motive repair: it repairs a missing motive set, not values) | `SP/Managers/ManagerHousehold.cs:246`-`:304` |
| `JobPerformanceScenario` | write (career) | every 10 sim-minutes, for each employed or school Sim that is not selectable **and has a body**: computes a per-hour performance from at-work state and calls `Career.AddPerformance`; clears the promote / demote flags when its career option forbids the Sim | T3: real, same as EA at work (docs/324 §3.4), no drift there. T4: skipped by NRaas (no body), so the seeded career drift is the only writer: no double count | `SP/Scenarios/Careers/JobPerformanceScenario.cs:58`-`:108`, `:135`-`:237` |
| `PromoteScenario`, `DemoteScenario`, `FindJobScenario`, `ManagerCareer` | write (career) | promotions, demotions, hiring through EA's occupation calls | state changes stay with story progression (docs/324 §2.7). Rebuild the plan from the description afterwards | `SP/Scenarios/Careers/` (40 matches) |
| `RelationshipScenario` and the friend / flirt / romance managers | write (LTR) | apply a liking delta with `UpdateLiking`, short-term context and buffs; romance managers force states | same setter family as §1.1. Our drift and theirs both add; neither overwrites. Our drift must read the liking at apply time, never cache it across a NRaas cycle | `SP/Scenarios/Friends/RelationshipScenario.cs:262`-`:308`, `SP/Managers/ManagerRomance.cs:450`-`:775`, `SP/Managers/ManagerFlirt.cs:383` |
| Skill module (`SkillControl`, push scenarios), `ManagerSkill.HandleMoochSkill` | write (skill) | `AddPoints` / level-clamped adds, mostly after pushing a real skill interaction | additive with ours. The per-far-stretch cap (docs/324 §2.7) must count only our own points | `NRaasStoryProgressionSkill/StoryProgressionSpace/Helpers/SkillControl.cs:336`-`:408`, `SP/Managers/ManagerSkill.cs:154`-`:165` |
| `ManagerMoney.AdjustFunds` and the Money module | write (funds) | all household fund changes go through this helper (skips special households) | no conflict: the model writes no funds (docs/324 §3.10) | `SP/Managers/ManagerMoney.cs:320`-`:340` |
| `RestoreDecayScenario` | write (motives) | restores motive decay, **active household only** | none; never touches a model-owned Sim | `SP/Scenarios/Sims/RestoreDecayScenario.cs:46`-`:97` |
| House party situation, NPC party arrival, fire, fight injury, drink scenarios | write (motives) | set motives on Sims inside a running situation | those Sims are live (promoted by the push helpers) | `SP/Situations/HousePartySituation.cs:327`, `:801`-`:825` and 6 single sites |

All rows [verified] by reading the cited lines, except where marked.

### 3.2 Overwatch: site table

| Site | Default | What it does | Far-model answer | Line |
|---|---|---|---|---|
| `StuckCheck` alarm | on (`kStuckCheck`) | walks every actor; for a non-selectable Sim in the world: reset at once when its room is hidden; reset when it has not moved since the last pass and is idle or on the same interaction and its spot is not safely routable; reset when it has not moved for a sim-day on the same interaction | **conflicts with T3** ("kept, hidden and inert", docs/324 §5.2): a parked Sim does not move and holds the same (null) interaction. Either park out of world (the check requires `InWorld`), or hook this alarm and skip model-owned Sims | `OW/Alarms/StuckCheck.cs:181`-`:270`, `OW/PersistedSettings.cs:79` |
| `ResetCheck` alarm | on (`kResetCheck`) | for non-household actors whose age-up is more than a day overdue twice in a row, or stuck in the age-up situation: resets the Sim or forces the age transition | T3 Sims are actors: an age-up the model holds back trips this. Age-ups must promote first and complete (docs/324 §4 "Aging"), never be deferred past a day | `OW/Alarms/ResetCheck.cs:60`-`:160`, `OW/PersistedSettings.cs:87` |
| `RecoverMissingSims` alarm | on | rebuild of missing residents; also moves instantiated Sims with an invalid description or no household to a safe spot | T4: override (docs/324 §5.6, FS-17). T3: no effect | `OW/Alarms/RecoverMissingSims.cs:50`-`:134` |
| `RecoverStrandedToddlers` alarm | - | toddlers and below away from home with no teen-or-older household member on the lot are moved home | a far plan must not leave a small child on a lot without a carer in the plan's physical position | `OW/Alarms/RecoverStrandedToddlers.cs:51`-`:80` |
| `CleanupRelationships` alarm | - | drops relationships with invalid descriptions; recreates a missing partner relationship | runs on descriptions; no conflict with drift (existing pairs only) | `OW/Alarms/CleanupRelationships.cs:60`-`:90` |
| `CleanupSkillModifiers` alarm | - | resets skill-gain modifiers that drifted | changes `ComputeSkillGainMod`, so the drift's applied points depend on when it ran: compute the seeded points before the modifier, or accept EA's modifier as part of "EA's own clamps" | `OW/Alarms/CleanupSkillModifiers.cs:73`-`:106` |
| `CleanupSituations` alarm | - | exits field-trip situations and removes their roles from all actors | none for the model | `OW/Alarms/CleanupSituations.cs:55`-`:100` |
| `InstantationCheck` | on | on the Sim-instantiated event, destroys a body that belongs to a skipped group | already in docs/324 §4; our go-live for such a Sim would be destroyed, so the model must not own those groups | `OW/Actions/InstantationCheck.cs:59`-`:112` |
| `KillAllHomeless`, `CleanseTheDead`, `CleanupGenealogy` | - / manual | remove out-of-world households, dead descriptions, genealogy leftovers | description-level; a model-owned Sim that is removed must drop from our index (description destroy hook) | `OW/Alarms/KillAllHomeless.cs:55`-`:73`, `OW/Actions/CleanupGenealogy.cs` |

All rows [verified] by reading the cited lines. Defaults marked "-" were not looked up.

### 3.3 Hook choice (for FS-14 / FS-17)

- Builds: the shared instantiation helper (each assembly's copy, docs/324 §4) covers `ManagerSim.Instantiate` and
  `ManagerSituation.PushVisit` in one place; EA's `SimDescription.Instantiate` stays the last line.
- Pushes: no hook needed in T3. In T4 the build hook already promotes before the push.
- Overwatch `StuckCheck` and `ResetCheck`: the smallest stand-down is in our own state, not in NRaas: keep T3 Sims
  either out of world or exempt them by a prefix on the two alarm methods found by name at load. If the name is not
  found, fall back to promoting any Sim Overwatch resets (it then behaves as a normal live Sim).
- Drift writers: no hook. Same setters, additive, read-at-apply.

## 4. Open questions

1. `MetaAutonomy` tuning values for the three visit rows (not in the DLLs). ME-10, or read the resource.
2. Whether the shared autonomy scorer applies a distance or route cost to meta candidates after
   `CalculateScore` (I-616-1). Read `Autonomy.CalculateScoreAndAddToCandidates` before FS-3 fixes `decay(T)`.
3. Skill subclasses that override `AddPoints` / `SkillLeveledUp` with effects that need a body (I-616-3).
4. Which Overwatch alarms other than the two defaults read are on by default (table cells marked "-").
5. T3 parking versus `StuckCheck`: does docs/324 §5.2 keep the Sim `InWorld`? If yes, FS-17 needs the exemption.
6. EA 1.69: line numbers here are Steam 1.67 only.
