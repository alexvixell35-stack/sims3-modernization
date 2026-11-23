# 572: EP logic batch 2 (571 parked leads, University, Island Paradise, Into the Future, Pets) + loop-logic scan

2026-11-15. Everything here is static: the EA code was read in `game-copy/decompiled`, which stays local. Built, proven in game 2026-11-15 (off BROKEN / on FIXED).

Where the leads came from:
- the leads parked in docs/571;
- four read-only lead passes, one each for EP9, EP10, EP11 and EP5 (not routing; another agent owns pet routing);
- a new loop-logic rule set (§16).

I re-checked every fix below against EA's code. Each fix has its own switch, off by default.

**Checks.** Each fix has a pure check, `Ts3Fixes.Fixes.<Name>Check("run"|"result")`. It prints `controls ok=` and `members found=`. With the switch off it reports `verdict: BROKEN`; on, `verdict: FIXED`; anything else is `UNEXPECTED`. Repros are in `tools/harness/repros.json` (ids `<switch>-check`).

**Household outcomes.** They change only where EA's code plainly does something other than what it means to do. These are EA bug fixes under CLAUDE.md, and none of them moves a Sim.

**Code.**

| File | Sections |
|---|---|
| `SupernaturalLogic.cs` | §1-2 |
| `SeasonsLogic.cs` | §3 |
| `UniversityLogic.cs` | §4-5 |
| `IslandParadiseLogic.cs` | §6-7 |
| `FutureLogic.cs` | §9-12 |
| `PetsLogic.cs` | §13-15 |

Each file has a matching `*Checks.cs`.

## Parked docs/571 leads, now fixed

### §1 `lunacysocials` (2604-2606, `LunacySocialsCheck`) [verified]

**Bug.** Buff objects are one per buff type, but `BuffLunacy` keeps the random-social alarm and `mActorSim` in its own fields. The full moon gives Lunacy to every eligible Sim at once, and each addition cancels the previous Sim's alarm. As a result:
- Only the last Sim acts out.
- The first Lunacy to end cancels the alarm for everyone.
- After a load nobody acts out, because the alarm is never persisted.

**Fix.**
- Each lunatic gets its own never-persisted alarm, using EA's `kMinRange`..`kMaxRange`.
- When the alarm fires, EA's `AddRandomSocial` runs for that Sim; EA's own re-arm alarm is dropped.
- Lunatics loaded with the buff are re-armed at world load.

**Test.**
1. On a full moon, put 2+ Insane or Supernatural Fan Sims on the lot.
2. With the switch off, only one acts out. With it on, all of them do.
3. `result` shows "Sims with Lunacy now" equal to "our alarms now".

### §2 `fairyauratimer` (2607-2608, `FairyAuraTimerCheck`) [verified]

**Bug.** `OccultFairy.FixUpOccult` re-adds the projecting aura on load. It then copies the old timer only into `GetElement(FairyAuraProjectingSoothe)`, a copy-paste slip. Creative and Mind auras get their full time back on every load.

**Fix.** For Creative and Mind only, copy the saved timeout, pause flag and start time into the re-added aura after EA's fixup.

**Test.** Start a Creative aura, note the time left, then save and load. With the switch on the time is kept, and 2607 counts 1.

### §3 `npcseasonalparty` (2609-2611, `NpcSeasonalPartyCheck`) [verified]

**Bug.** The "player threw a party" listener in `NpcSeasonalPartyManager` is a static. It is never saved and never re-added. After a holiday save/load, the player's own party no longer cancels the NPC holiday party.

**Fix.**
- At world load on a holiday, when EA's listener is gone, register a listener for the season's party event.
- When the NPC-party alarm fires on a day the player threw their party, the NPC party is not scheduled.

**Test.**
1. Save on the morning of a holiday, then reload.
2. Throw the season's party (costume, house, pool or feast).
3. With the switch on, 2610 is 1 at the NPC party's scheduling hour and no NPC party invitation comes.

**Still parked.** Kissing booth (needs its whole `Run` replaced), Festival Frequenter tickets (changes household income; needs a decision), and the docs/571 inferred leads.

## University Life

### §4 `professorterm` (2612-2613, `ProfessorTermCheck`) [verified]

**Bug.** `Household.OneDayPassedUniversityUpdates` calls `OnTermCompleted` for every Sim on the last trip day, professors included. That call commits credit, may graduate the Sim, and always calls `LeaveJob`, which runs `RemoveProfessor`. Nothing replaces professors: `SetupProfessorAlarm` and `RefreshProfessors` have no caller. After the first term, majors have no professor.

**Fix.** `OnTermCompleted(float)` does nothing for a professor.

**Test.** Finish a term in a university save, then return. Professor socials and lectures still have a professor.

### §5 `protestlastday` (2614-2615, `ProtestLastDayCheck`) [verified]

**Bug.** `ImprovedProtestSituation.mLastDayHadProtest` (the NPC protest cooldown) is a plain static. It survives a world quit. If the next save's clock is behind that value, NPC protests are suppressed.

**Fix.** Reset it to 0 on quit, the same value a fresh start has.

## Island Paradise

### §6 `seasickpaused` (2616-2617, `SeasickPausedCheck`) [verified]

**Bug.** The seasickness alarm adds Seasick already paused. Only `Houseboat.SetStatus` unpauses it, and only for the Sims aboard at that moment. `Sim.LotChanged` never unpauses on leaving. It also adds the alarm on a houseboat that is StationaryAtSea. The result is a paused Seasick buff, with its vomit alarm, that never ends.

**Fix.** After EA's `LotChanged`:
- Leaving a houseboat lot unpauses Seasick.
- Arriving on a houseboat that is not sailing drops the alarm (StationaryAtSea) and unpauses.

**Test.**
1. Sail the houseboat until a Sim gets Seasick.
2. Have that Sim leave by the ladder.
3. With the switch off the moodlet timer is frozen. With it on, it counts down.

### §7 `resortweddingalarm` (2618-2619, `ResortWeddingAlarmCheck`) [verified]

**Bug.** `WeddingCallback` clears `mWeddingArchAlarm` only when the wedding succeeds. A failure at or after the latest hour, or an exception, leaves the handle set, and that resort then never schedules another NPC wedding.

**Fix.** When the callback ends without setting a new alarm, the handle goes back to invalid.

### §8 (dropped) RefreshVIPRooms collection-modified throw

Already fixed and proven in game by `viproomrefresh` (VipRoomRefresh.cs, docs/555 §3). The loop rule found the same bug again;
the duplicate switch was removed and its slots 2620-2621 released.

## Into the Future

### §9 `plumbotfacelistener` (2622-2623, `PlumbotFaceListenerCheck`) [verified]

**Bug.** `OccultRobot` listens to `kLeftConversation` with no actor filter. Every bot reacts to every conversation end in town: it re-adds an alarm and resets its face VFX.

**Fix.** The bot reacts only when it is the Sim who left or the Sim who was left.

### §10 `efficientchip` (2624-2625, `EfficientChipCheck`) [verified]

**Bug.** The robot learning modifier (Ability to Learn + Efficient) is recomputed only when Ability to Learn changes.

**Fix.** Recompute it as `SetLearningModifier` does, after `SkillManager.RobotSkillsChanged` (which runs after every chip change) and at world load.

### §11 `sentiencename` (2626-2627, `SentienceNameCheck`) [verified]

**Bug.** On install, the Sentience chip saves `OwnerSim.Name`, which is the full name. Removing the chip sets FirstName to it, so "Bob Smith" becomes "Bob Smith Smith".

**Fix.** Before removal, strip a trailing " <last name>" from the saved name.

### §12 `jetpackflyingbuff` (2628-2629, `JetpackFlyingBuffCheck`) [verified]

**Bug.** Fly Around adds Free Flying paused and unpauses it only on a normal end. If the flight is reset or fails, the buff (and its death chance) stays paused.

**Fix.** `FlyAround.Cleanup` unpauses the buff for a living Sim.

**Not fixed.** The show-off flight's second fail roll (needs `Run` replaced).

## Pets (no routing)

### §13 `horsetraitscore` (2630-2631, `HorseTraitScoreCheck`) [verified]

**Bug.** `EnterEquestrianCompetition.AddScoreForTraits` tests `Actor.HasTrait`, which is the rider, for the horse traits Fast, Agile, Lazy and Hates Jumping. The bonus is always 0.

**Fix.** Run the same loop over the horse's traits, using EA's `GetScoreForTrait`.

### §14 `wildhorsegender` (2632-2633, `WildHorseGenderCheck`) [verified]

**Bug.** `WildHorseCreator.DecideGender` returns Female when `!RandomChance(kFemalePercent)`. That gives 20% mares, where the tuning says 80%.

**Fix.** When the lead-horse rule did not decide, swap the rolled gender. This affects NPC herds only.

### §15 `littercap` (2634-2635, `LitterCapCheck`) [verified]

**Bug.** `PetAdoption.CanAddPetInHouse` caps a neighbour litter at 6 − pets and ignores the 10-member cap.

**Fix.** The cap is also limited to 10 − (Sims + pets), counted the way `CanAddSpeciesToHousehold` counts.

## §16 Loop-logic scan (`tools/static/ea-loop-logic.semgrep.yml`, run by `tools/static/gameplay-scan.sh`)

The rule set has these rules:

| Rule | What it looks for |
|---|---|
| `foreach-mutates-own-list` | the loop changes the list it walks; tuned to skip a mutation followed by break/return in the same branch |
| `for-removeat-skips` | `RemoveAt` in a loop that then skips an element |
| `for-back-from-count` | a backward loop starting at Count |
| `while-true-no-exit` | `while (true)` with no exit |
| `sleep-wait-no-exit` | a `Simulator.Sleep` wait whose condition has no exit-reason, destroyed, time or counter test |
| `retry-no-cap` | a retry loop with no cap |
| `foreach-returns-first` | the loop always returns on the first item; tuned to constant returns, because the first version had 1541 hits on lookups |
| `listener-records-then-removes` | the birthdayagers shape |
| `inner-break-outer-continues` | a break that only exits the inner loop |
| `getobjects-in-loop` | O(n²) world queries inside a loop |

Hit counts after tuning:

| Rule | Hits |
|---|---|
| sleep-wait-no-exit | 101 |
| retry-no-cap | 22 (most test `HasExitReason`) |
| while-true-no-exit | 10 (long-running script tasks: SimUpdate, Camera, watchdogs, by design) |
| inner-break-outer-continues | 5 |
| foreach-returns-first | 4 (all false: `continue`-guarded) |
| foreach-mutates-own-list | 2 |
| for-removeat-skips | 1 |
| listener-records-then-removes | 1 |
| getobjects-in-loop | 1 |

What the top hits turned out to be:
- **foreach-mutates-own-list.** `ResortManager.RefreshVIPRooms` is a real bug, already fixed by `viproomrefresh` (docs/555 §3). OnlineDatingManager breaks right after the change, so it is safe. Of the 26 pre-tuning hits, 25 break or return right after the change.
- **listener-records-then-removes.** `BirthdayParty.OnAgeTransition`, already fixed as docs/571 §1.
- **for-removeat-skips.** `RoutingComponent` push wait at line 1585 increments `i` twice. It is routing code and was handed to the routing owner, not fixed here.
- **retry-no-cap and sleep-wait, inferred hangs, parked.** None has a single-method fix; each needs its `Run` replaced.
  - `MagicianCareer` waits for the volunteer's queue to empty.
  - `SocialWorkerChildAbuseSituation` waits on a unicorn's queue.
  - `DrillHole` (time-travel hole) waits on `CanTravelTo`, with the Sim hidden.
- **Already covered.** `BowlingLane.ReadyToStart` (docs/559).

## Leads not fixed (parked or ruled out)

| Lead | Status |
|---|---|
| Annex graduation stuck "initialized" when the 12:00 alarm fires late (EP9) | the fix needs the private ceremony cleanup and GraduateInPlace pushed; parked |
| RecentlyGotUniversityOpportunity trait sticks (EP9) | inferred |
| Course rabbit-hole listener lost on load (EP9) | inferred effect; same listener as `courselistener` (docs/555 §4), extend that fix if confirmed |
| Technology graduation uniform missing (EP9) | maybe a missing asset; inferred |
| Protest victory derefs CreatedSim without a null check (EP9) | inferred |
| NPC resort wedding can take a household Sim as groom, and the `>= 0` tower check (EP10) | verified, but the fix touches who gets married; parked for a decision |
| `ResortManager.UpdateCheckins` shared warning flag and NRE (EP10) | needs the body replaced; parked |
| Resort maintenance staff pulled back from leaving (EP10) | inferred |
| Houseboat route-depth statics never reset (EP10) | routing; handed to the routing owner |
| Descendant wealth notice compares two measures; bot workbench left "on" (cosmetic); TraitChipManager `slot <=` off-by-one; portal intro stopping for the session (EP11) | parked |
| Jetpack show-off double fail roll (EP11) | needs `Run`; parked |
| Neighbour-litter adoption NRE; wild-herd lead pick and stale-ID crash (EP5) | parked; the lead pick is inferred intent |
| Unicorn pool handle mix-up (EP5) | dead code: no callers |
| Herd destination off-by-one (EP5) | no effect with shipped tuning |
| Clean, no bug found | CauseEffectService, TimePortal statics, time statues, ServoBotArena, InfluenceSkill counters, protest scheduler, ResortWorker motives, lifeguard, scuba, mermaids, PetPoolManager, StrayPets, PetAdoption alarms, equestrian alarms, breeding and selling |
