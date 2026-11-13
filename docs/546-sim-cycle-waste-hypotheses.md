# 546. Sim-cycle waste: work the game repeats without checking it can pay off

**Status: hypothesis list, nothing measured yet unless a row says so (2026-11-13).** Each row is a kind of work EA's
scripts may do every cycle (autonomy pass, event dispatch, alarm tick, sim update) even when it cannot change anything.
The plan for each is the same as for the leak fixes (docs/545): count it first with the test agent's census
(read only, no behaviour change), and only then build a fix behind a switch and run it off / on.
Claim marks as in CLAUDE.md: **[verified]** read in the code, **[measured]** seen in a run, **[inferred]** our reading.

## 1. The list

| # | What may be wasted | Why it could be expensive | How to measure (census, read only) | Status |
|---|---|---|---|---|
| 1 | Exceptions thrown and caught quietly | EA wraps many tests and callbacks in try/catch; in the game's Mono 1.2 interpreter (docs/04 F6) a thrown exception costs far more than a normal call [inferred]. One test that throws for every Sim on every autonomy pass would cost a lot unseen | Exceptions per second by throw site (method of the throwing frame) and by catching method | todo |
| 2 | Interactions that can never pass | Autonomy scores the interactions of the objects near each Sim; an interaction that fails its test for everyone (broken object, missing pack, object off the lot) still costs a test call each pass [inferred] | Per object type and interaction: tests run, passed, failed, time in the test; flag rows with many tests and 0 passes | todo |
| 3 | Objects autonomy should not consider | Objects with no lot, at an invalid position, destroyed but still listed, or left by deleted Sims can stay in the candidate lists [inferred] | Candidate objects per autonomy pass that are invalid by those rules, by type | todo |
| 4 | Listeners that filter inside the handler | Many listeners take every event of a kind and check "is this about me?" inside, so the cost is listeners x events. The census shows ~4,900 wish listeners (ActiveDreamNode) and growing kSocialInteraction listeners [measured, docs/545 runs] | Handler calls per event id, and how many returned without acting (no state change) | todo |
| 5 | Alarms that return at once | Repeating alarms whose owner is gone or whose condition no longer holds still fire and return [inferred] | Alarm callbacks per minute by callback, and their time; owners gone at fire time | todo |
| 6 | Reaction broadcasters | Fire, smells, dirty objects and others check every Sim in range each tick; a broadcaster left on a deleted or moved object keeps checking [inferred] | Live broadcasters by type, their owner's state, checks per second | todo |
| 7 | Situations that never end | Entries in Situation.sAllSituations whose participants are gone keep their alarms and listeners [inferred] | Situations by type and age, with gone participants | todo |
| 8 | Full world scans every tick | Script calls such as GetObjects<T>() or loops over LotManager.Actors in per-tick code walk every object [inferred] | Calls per second of the world-scan APIs by caller, and objects visited | todo |
| 9 | Relationships of Sims who never met | Relationship entries grow roughly with Sims squared; story progression updates them for townies [inferred]. Entries went 366 -> 670 in one soak [measured, docs/545] | Entries by kind (met / never met, both alive), updates per minute | todo |
| 10 | Retries of an unreachable target | A Sim that cannot reach an object retries the route and the interaction [measured in part: "townie retries" in every phase run] | Retries by Sim scope, object type and interaction, and route time spent on them | partly covered |

Order: 1 and 2 first (cheap census, most likely to show a large single cost), then 4 and 8, then the rest.

## 2. Related finding: slow world saves on the test machine

[measured, Steam Deck under Wine, Isla Paradiso test world, ts3native 0.36.240 and 0.36.247] The game's own world
save step (ts3native save log phase "write: world save (lots, script state)") took **122-158 s** in every logged save
since 2026-11-11 (13 run logs), and **8.7 s** once, in a save made 2 minutes after load. The same world and the same
memory picture: 43-58 MB free address space, 15 MB largest free block. The GC count did not change during a 122 s
save, so it is not GC thrashing [measured]. No route job was running and no ts3native gate was held [measured].
So the save time grows with play time, not with the release (v0.2.12 is not slower than 0.36.240).

This may explain the v0.2.12 known issue "save stays at Saving... after editing windows or doors" (a save that takes
minutes looks stuck) [inferred, not reproduced: the door-edit test could not place doors, the test lot has no free
4 x 4 area]. Next: save right after load vs after idling 5 and 10 min in one session; the same without ts3fixes
(running 2026-11-13); then sample what the sim thread does during the slow save.
