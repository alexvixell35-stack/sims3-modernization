# 529. Gone-Sim leftovers, batch 2: jobs of Sims deleted at work (`leavejobdispose`)

A static audit looked at what a Sim that is gone, or has no Sim object, leaves behind: bosses, co-workers, beds,
university graduation, prom and rabbit holes.
- One EA bug is fixed here: the ts3fixes switch `leavejobdispose`, on by default (counter 2054, registry `fixes-2053`,
  ledger §933, §2).
- A second EA bug, in double-bed claims, is already fixed in simler90's core, which this project installs by default
  (§1).
- The other leads are in §4.

Claim marks: **[verified]** = read in the game's decompiled code or IL, or seen live; **[inferred]** = our reading, not
yet checked in game.

## 1. Double bed with a partner who has no Sim object: fixed in simler90's core

- In EA's `BedMultiPart.ClaimOwnership(newOwner, entryPart)`, the new owner's half is set first. Then, if the partner
  lives on that lot and the other half is free, it runs `bedData.Owner = newOwner.Partner.CreatedSim;
  bedData.Owner.Household...` with no check that the partner has a Sim object [verified, EA's decompiled code].
- A partner who lives there but is not in the world makes that throw before `newOwner.Bed` is set [inferred].
- simler90's core (base `Core/base/UPDATE242`) adds `newOwner.Partner.CreatedSim != null` to that condition and assigns
  the other half through its own owner fix [verified, decompiled from the core].
- Live, with the player's core: one partner's Sim object was destroyed and `CreatedSim` cleared, then the other claimed
  the bed. No exception; own half set, other half free, `Sim.Bed` set (`bp-bed-partner-check`, 06:17 in the results
  file).
- A ts3fixes fix (`bedpartner`, written for this batch) was dropped before commit, and counter 2053 is unused. The
  audit had wrongly listed this method as unchanged in simler90's core.

## 2. A Sim deleted at work keeps its job (`leavejobdispose`)

- While the Sim is in `WorkInRabbitHole`, `Career.LeaveJob` only notes the leave (`mLeaveJobOnExit`) and sets an exit
  reason [verified]. `WorkInRabbitHole.Cleanup` does the real `LeaveJobOnExitCheck` later, but only
  `if (mCareer != null && Actor.CareerManager != null)` [verified].
- `SimDescription.Dispose(...)` runs in this order [verified]:
  1. clears `mIsValidDescription`;
  2. calls `CareerManager.LeaveAllJobs(kDebug)`;
  3. sets `CareerManager = null`;
  4. destroys the Sim.
- So a Sim deleted while at work never runs `LeaveJobNow` [verified live, §3]. Its description stays in
  `CareerLocation.Workers`, and by the same path in the co-workers' `Coworkers` lists and as other Sims' `Boss`
  [inferred]. The dead `Occupation` stays referenced and is saved with them.
- It happens when a household is deleted while one of its Sims is at work: Edit Town, NRaas tools, story progression
  [inferred].
- **Fix:** after EA's `LeaveJob`, if the description is no longer valid and a leave was noted, the note is cleared and
  `LeaveJobNow` runs at once. That is the call EA makes when the Sim is not at work. A noted career transfer is not
  carried out for a description being disposed. Counter `leavejobdispose_leaves`.
- simler90's core does not change `Career.LeaveJob` or `WorkInRabbitHole.Cleanup`; the off arm below ran with it.

## 3. Test

Repro `leavejob-dispose-check`, queue `tools/harness/queues/gone-sim-batch2.json`, save Baseline-BP, release defaults,
with the player's core mod. The agent check `LeaveJobDisposeCheck`:
- waits for 2 Sims at work, not of the active household (it sends up to 3 townies whose career should be at work now,
  as `go_to_work` does; the load starts at about 11:50, a work hour);
- per arm, deletes one of them as EA's household deletion does (`Household.Remove`, `Dispose`);
- then reads the work place's `Workers`, and the workers' `Coworkers` and `Boss`.

**Result (2026-11-03 06:00, build 94b7eac + this branch, `docs/data/results/2026-11-03-queue-gone-sim-batch2.md`):
`bp-leavejob-dispose-check` PASS.**

| arm | Sim deleted at work | in `Workers` | co-worker lists | boss of | leaves done |
|---|---|---|---|---|---|
| off | Big Hartley (Professional Sports) | True | 0 | 0 | 0 |
| on | Wayne Bumble (Culinary) | False | 0 | 0 | 1 |

The off arm's Sim had no co-worker or boss references to leave behind, so this run shows the `Workers` part only. No
exception came from `Dispose` in either arm.

The results file has two earlier rows that are not about the fix. At 05:32 the jobs did not start: the worktree had no
`ts3-nointro.package`. The `bp-bed-partner-check` rows are the §1 test.

## 4. Audit leads not fixed here

- **`Political.PoliticalTakeARisk`** (the Take a Risk tone's result; the tone is offered below level 10 whatever the
  boss): two of its three outcomes update the boss's liking through `GetRelationship(Boss, true).LTR` with no check
  [verified, simler90's core too]. `Relationship.Get` returns null for a null boss or one whose description is no longer
  valid, so `.LTR` throws after the outcome's notification. The third outcome also loops over the co-workers the same
  way, so a gone co-worker throws too and the rest of that outcome is lost. `leavejobdispose` (§2) takes Sims deleted at
  work out of those references; a career with no boss is still unguarded. Not fixed here.

Not yet checked against simler90's core:
- **`Annex` graduation:**
  - The diploma, trait, buff and `SetRewardsGranted` come only from the ceremony's `InRabbitHole` loop finishing, so a
    graduate who misses the ceremony gets the degree without them.
  - `StartGraduationCeremonyOneShot` instantiates `mGraduatingSims` / `mLocalVisitors` with no validity check.
- **`PromSituation`:**
  - `GetPromDate` checks only `!IsDead`, so a disposed date reaches `InstantiateOffScreen` and then
    `ForceSituationSpecificInteraction`.
  - `PushAllNpcTeensToLocationOneShot` reads `IsSelectable` on a possibly null `InstantiateOffScreen` result.
- **`BedData.Owner`:** holds a `Sim` object, which goes stale on re-instantiate or when the Sim is removed while not
  instantiated.
- **`RabbitHole.sCurrentAdDeals`:** a static `Dictionary<Sim, ...>` that is never pruned.
