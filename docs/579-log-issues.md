# 579: Recurring game issues in the logs so far (log-issues census, 2026-11-15)

Source: `node tools/harness/log-issues.ts --doc <file>` over every soak run's `phase-*/ts3native.log` (`--runs`, default the local runs folder) (with
earlyfix.txt and the leak / alarm census snapshots), the player's own sessions (`userlogs/`) and the NRaas ErrorTrap
reports (`ScriptError_*.xml`). Counts are [verified] by the tool. "Mentioned in docs/NNN" means only that the issue's main token
(interaction, type, holder) appears there; check before treating it as covered. Rows that are mentioned are left out of
the first list (`--all` shows them). Census: 152 logs (11 fixes-on, 139 default, 2 player) + 11 ErrorTrap reports; 2221 issue keys, 856 mentioned in docs.

How to read the kinds: **growth** = a leak / alarm census number that rises from a run's first to its last snapshot (leak
lead, not a proven leak: social_round and spawn phases legitimately add townies). **routefail** = ts3fixes' route-fail
log (household / active Sims). **routejob** = native jobs our deadlines did not cut that still failed (the result is ts3native's raw
plan result code; the codes are not mapped to RoutePlanResultType names here [inferred: same value set]). **syncwait** = social sync waits that failed or timed out.
**rfspam** = one Sim failing the same interaction 5 or more times in a run. "Fixes-on" = 5+ switches turned on by earlyfix.txt.

## Leads (reading of the top rows)

- Event listeners that grow every run (kCaughtCheatingOnPartner 0 -> 149, kCaughtCheatingWithSim, kRelationshipLTRChanged,
  kSawSim): EventTracker listeners added per romance / sighting and not taken off. In fixes-on and default runs: an EA
  leak lead, not caused by us.
- `broadcasters.max_on_one_lot` 12 -> 35 and `socials.queued_interactions` 16 -> 41 in nearly every run: reaction
  broadcasters and queued socials piling up on one lot (the party / social phases' venue).
- School work-day alarms (RegularWorkDayStartAlarmHandler, ...TwoHourBefore...) 65 -> 110: one pair per enrolled child, and the
  count only goes up. Check whether children who left the world keep theirs.
- DisposeOutside -> TrashPile, result 16 with 0 nodes in 0 plans (unseen NPCs, also in the player's session): the trash-pile
  goal is not routable at all. Same shape for GetInPool -> Pool (result 10, 311 lines, NPC) and PlaceWelcomeKit -> Sim.
- The household test lot's ToiletExpensive: PortalLocked / NoPath / result 18, also as per-Sim spam (the same Sim 5+
  times): a bathroom door that locks the household out on the Bridgeport test home. A lot / privacy issue, check whether it
  is a game bug or the lot.
- Social sync failures with the partner busy or asleep (TakeCellPhonePhotoPaired with the receiver "sleeping",
  PillowFight, AskForAutograph, ZombifiedSocials): the initiator starts the social while the partner cannot take it.
- CustomContentIcon UI controls 0 -> 1202 (no parent): default runs only, the CAS / buy browse phases; already handled where
  the fix-on runs show 0.

## Issues not yet covered (top 25)


1. **growth**: `leak.simdescs.mini_sims` [verified] 6217 in 11 fixes-on / 79 default / 0 player runs
   e.g. `leak.simdescs.mini_sims: 5 -> 97`
2. **growth**: `leak.broadcasters.max_on_one_lot` [verified] 1666 in 11 fixes-on / 74 default / 0 player runs
   e.g. `leak.broadcasters.max_on_one_lot: 12 -> 35`
3. **growth**: `leak.socials.queued_interactions` [verified] 2061 in 6 fixes-on / 58 default / 0 player runs
   e.g. `leak.socials.queued_interactions: 16 -> 41`
4. **growth**: `leak.events.by_event.kRelationshipLTRChanged` [verified] 7780 in 9 fixes-on / 30 default / 0 player runs
   e.g. `leak.events.by_event.kRelationshipLTRChanged: 214 -> 380`
5. **growth**: `leak.objects.in_inventory_by_type.UniversityDiploma` [verified] 783 in 10 fixes-on / 38 default / 0 player runs
   e.g. `leak.objects.in_inventory_by_type.UniversityDiploma: 0 -> 10`
6. **growth**: `leak.events.by_event.kCaughtCheatingOnPartner` [verified] 4092 in 9 fixes-on / 29 default / 0 player runs
   e.g. `leak.events.by_event.kCaughtCheatingOnPartner: 0 -> 149`
7. **routejob**: `DisposeOutside target TrashPile: result 16` [verified] 142 in 10 fixes-on / 42 default / 1 player runs
   e.g. `routefail job: route 2578EF8F, DisposeOutside target TrashPile, job seq 2624 0 ms result 16, uncut, kind own Plan deadline 220 ms, follower 48EE00355D3C26E0, plan 0 host 0 other 0 ms, capAt -1 tail -1 ms, nodes 0 in 0 plans, unseen`
8. **routefail**: `UseToilet target ToiletExpensive: DestinationPortalLocked` [verified] 208 in 10 fixes-on / 31 default / 0 player runs
   e.g. `ts3fixes: routefail: Apollo Bloom 0x14BC00F559664B60 YoungAdult active, UseToilet target ToiletExpensive, PlanFromPoint sync FailedBecauseDestinationPortalLocked blocker None, goal 571.0,34.8,1668.3, at 497.2,34.1,1551.5, lot 0xF5900033BA9E8C10 room 3, 11 ms, `
9. **routefail**: `UseToilet target ToiletExpensive: NoPathToDestination` [verified] 130 in 9 fixes-on / 31 default / 0 player runs
   e.g. `ts3fixes: routefail: Kirby Wise 0x14BC00F559664B50 YoungAdult active, UseToilet target ToiletExpensive, PlanFromPoint sync FailedBecauseNoPathToDestination blocker None, goal 493.4,34.1,1554.6, at 491.9,34.1,1555.1, lot 0xF5900033BA9E8C10 room 13, 2 ms, seen, `
10. **routejob**: `UseToilet target ToiletExpensive: result 18` [verified] 133 in 9 fixes-on / 29 default / 0 player runs
   e.g. `routefail job: route 2556A0F9, UseToilet target ToiletExpensive, job seq 28120 52 ms result 18, uncut (exempt), kind own Plan deadline 10000 ms, follower 48EE0034EA1BE040, plan 0 host 47 other 4 ms, capAt -1 tail -1 ms, nodes 0 in 0 plans, seen`
11. **routejob**: `PlaceWelcomeKit target Sim: result 10` [verified] 40 in 8 fixes-on / 31 default / 1 player runs
   e.g. `routefail job: route 25B82328, PlaceWelcomeKit target Sim, job seq 653 1 ms result 10, uncut (exempt), kind own Plan deadline 2000 ms, follower 14BC00F559667CB0, plan 0 host 0 other 1 ms, capAt -1 tail -1 ms, nodes 0 in 0 plans, seen npc`
12. **syncwait**: `PillowFight / partner SocialInteractionB: failed` [verified] 59 in 6 fixes-on / 24 default / 0 player runs
   e.g. `ts3fixes: syncwait: Kenji -> Gracie, Started, failed, 0.0/45 min, checks 0 0 ms, in PillowFight, partner in SocialInteractionB (linked running), partner sync Started/Receiver on me, own Started/Initiator own flags 1000 partner flags 0000, day 0 hour 22.12`
13. **routefail**: `Fridge_Have target FridgeModerate: NoPathToDestination` [verified] 56 in 7 fixes-on / 20 default / 0 player runs
   e.g. `ts3fixes: routefail: Suzy Strummer 0x14BC00F559664B70 YoungAdult active, Fridge_Have target FridgeModerate, PlanFromPoint sync FailedBecauseNoPathToDestination blocker None, goal 493.4,34.1,1541.4, at 491.7,34.1,1539.2, lot 0xF5900033BA9E8C10 room 6, 3 ms, see`
14. **routefail**: `ServingContainerGroup_GrabAServing target Plate: NoPathToDestination` [verified] 57 in 8 fixes-on / 15 default / 0 player runs
   e.g. `ts3fixes: routefail: Kirby Wise 0x14BC00F559BAE080 YoungAdult active, ServingContainerGroup_GrabAServing target Plate, PlanFromPoint sync FailedBecauseNoPathToDestination blocker None, goal 497.5,34.1,1561.5, at 496.5,34.1,1559.2, lot 0xF5900033BA9E8C10 room 1`
15. **routefail**: `ServingContainer_CleanUp target Plate: DestinationPortalLocked` [verified] 76 in 8 fixes-on / 12 default / 0 player runs
   e.g. `ts3fixes: routefail: Suzy Strummer 0x14BC00F559664B70 YoungAdult active, ServingContainer_CleanUp target Plate, Plan async FailedBecauseDestinationPortalLocked blocker None, goal 497.5,34.1,1537.5, at 494.6,34.1,1560.0, lot 0xF5900033BA9E8C10 room 14, 64 ms, s`
16. **routejob**: `ServingContainer_CleanUp target Plate: result 18` [verified] 60 in 8 fixes-on / 11 default / 0 player runs
   e.g. `routefail job: route 25E6AD6E, ServingContainer_CleanUp target Plate, job seq 2772 13 ms result 18, uncut (exempt), kind own Plan deadline 10000 ms, follower 48EE0034EA1BE0E0, plan 0 host 11 other 1 ms, capAt -1 tail -1 ms, nodes 0 in 0 plans, seen`
17. **routefail**: `ServingContainer_CleanUp target Plate: NoPathToDestination` [verified] 27 in 7 fixes-on / 12 default / 0 player runs
   e.g. `ts3fixes: routefail: Matty Crewe 0x14BC00F559664B20 YoungAdult active, ServingContainer_CleanUp target Plate, Replan sync FailedBecauseNoPathToDestination blocker Sim 0x48EE0034EA1BE0C0 Sim at 499.8,34.1,1560.5, goal 499.8,34.1,1559.5, at 499.8,34.1,1560.1, lo`
18. **syncwait**: `TakeCellPhonePhotoPaired / partner SocialInteractionB: failed` [verified] 33 in 6 fixes-on / 11 default / 0 player runs
   e.g. `ts3fixes: syncwait: Gracie -> Honey, Committed, failed, 0.0/30 min, checks 0 0 ms, in TakeCellPhonePhotoPaired, partner in SocialInteractionB (linked running), partner sync Completed/Receiver on me sleeping, own Committed/Initiator own flags 1101 partner flags`
19. **routejob**: `GetInPool target Pool: result 10` [verified] 311 in 3 fixes-on / 8 default / 0 player runs
   e.g. `routefail job: route 25E8E99D, GetInPool target Pool, job seq 5487 0 ms result 10, uncut (exempt), kind own Plan deadline 2000 ms, follower 2B99003623895C00, plan 0 host 0 other 0 ms, capAt -1 tail -1 ms, nodes 0 in 0 plans, seen npc`
20. **routejob**: `RouteAway target Terrain: result 16` [verified] 76 in 4 fixes-on / 9 default / 0 player runs
   e.g. `routefail job: route 25E7C2A4, RouteAway target Terrain, job seq 46610 34 ms result 16, uncut, kind own Plan deadline 150 ms, follower 14BC00F5597F2300, plan 1 host 28 other 4 ms, capAt -1 tail -1 ms, nodes 0 in 1 plans, unseen`
21. **syncwait**: `ZombifiedSocials / partner SocialInteractionB: failed` [verified] 15 in 4 fixes-on / 6 default / 0 player runs
   e.g. `ts3fixes: syncwait: Sugar -> Mrs, Committed, failed, 0.0/30 min, checks 0 0 ms, in ZombifiedSocials, partner in SocialInteractionB (linked running), partner sync Committed/Receiver on me sleeping, own Committed/Initiator own flags 1101 partner flags 0010, part`
22. **syncwait**: `AskForAutograph / partner SocialInteractionB: failed` [verified] 18 in 4 fixes-on / 5 default / 0 player runs
   e.g. `ts3fixes: syncwait: Ace -> Richie, Routed, failed, 23.5/30 min, checks 88 1 ms, in AskForAutograph, partner in SocialInteractionB (linked running), partner sync Started/Receiver on me, own Started/Initiator own flags 1101 partner flags 0010, partner step recei`
23. **syncwait**: `TakeCellPhonePhotoPaired / partner none: failed` [verified] 9 in 4 fixes-on / 5 default / 0 player runs
   e.g. `ts3fixes: syncwait: Johnny -> Richie, Started, failed, 0.0/45 min, checks 1 0 ms, in TakeCellPhonePhotoPaired, partner in none (linked queued), partner sync Completed/None on none, own Started/Initiator own flags 1001, partner step receive<ok 295.5 min ago, pa`
24. **rfspam**: `UseToilet: DestinationPortalLocked` [verified] 9 in 4 fixes-on / 3 default / 0 player runs
   e.g. `Katelyn Missoni: UseToilet: DestinationPortalLocked x5`
25. **rising**: `ts3fixes: goalhh:` [verified] 6 in 4 fixes-on / 2 default / 0 player runs
   e.g. `ts3fixes: goalhh: first half 0, second half 92`


## Interaction locks

What the logs carry (2026-11-15): [HitchAttr] "lock holders:" waits (package lock waiter 0x004A94EC / holder 0x004A8E22,
routing-snapshot lock 0x0072BD3A / 0x0072C493), joined to the interaction named by the nearest earlier routefail / syncwait /
go_here line (an approximation: the lines carry no Sim id), ts3fixes unseen walk-time holds and "kd cam recheck would
hold" lines (joined to the route's interaction when a routefail job / retry line named it), object reservations (PortalLocked
and "blocked by <object>" route results), and mutual sync waits: **0 lines [verified]**, every logged syncwait says "partner
not in a sync wait". Interactions that hold a Sim for hours need `health.jsonl` (sim_health census); no run has one yet.
Cancel attempts that do not take and the autonomyinuseblock counter (2694) are not in the logs.

The routing-snapshot lock is the one with long single waits (max 452 ms, 13.7 s in 158 waits); the package lock waits are
short (max 56 ms) but frequent (about 5,200 waits).


1. **held**: `kd cam recheck would hold, in view: interaction unknown` [verified] 2151 in 8 fixes-on / 87 default / 0 player runs
   e.g. `kd cam recheck: route 2513A0C1 would hold, in view (switch off), job seq 3 3992 ms result 16, cut (UnseenFallback, mask 2), kind own Plan deadline 220 ms, follower 374A00542A64D770, plan 3969 host 3956 other -3933 ms, capAt 3992 tail 0 ms, nodes 0 in 1 plans, `
2. **lock**: `package lock (waiter at 0x004A94EC, holder main from 0x004A8E22) during SocialInteractionB` [verified] 851, wait 19.4 s total, max 56 ms in 10 fixes-on / 49 default / 1 player runs; mentioned in docs/546
   e.g. `lock holders: sim waited 20 ms on lock 3052C9E0 (at 0x004A94EC), held by main 232 from 0x004A8E22 (wait #8)`
3. **lock**: `routing-snapshot lock (waiter at 0x0072BD3A, holder main from 0x0072C493) during unknown interaction` [verified] 158, wait 13.7 s total, max 452 ms in 9 fixes-on / 46 default / 1 player runs
   e.g. `lock holders: sim waited 123 ms on lock 36567B40 (at 0x0072BD3A), held by main 232 from 0x0072C493 (wait #1)`
4. **lock**: `package lock (waiter at 0x004A94EC, holder main from 0x004A8E22) during GoHome` [verified] 402, wait 9.2 s total, max 49 ms in 10 fixes-on / 45 default / 1 player runs; mentioned in docs/registry
   e.g. `lock holders: sim waited 23 ms on lock 3052C9E0 (at 0x004A94EC), held by main 232 from 0x004A8E22 (wait #27)`
5. **lock**: `package lock (waiter at 0x004A94EC, holder main from 0x004A8E22) during VisitCommunityLot` [verified] 275, wait 6.2 s total, max 41 ms in 10 fixes-on / 37 default / 0 player runs; mentioned in docs/546
   e.g. `lock holders: sim waited 26 ms on lock 3052C9E0 (at 0x004A94EC), held by main 232 from 0x004A8E22 (wait #56)`
6. **lock**: `package lock (waiter at 0x004A94EC, holder main from 0x004A8E22) during SocialInteractionA` [verified] 261, wait 5.9 s total, max 53 ms in 10 fixes-on / 44 default / 0 player runs; mentioned in docs/546
   e.g. `lock holders: sim waited 20 ms on lock 3052C9E0 (at 0x004A94EC), held by main 232 from 0x004A8E22 (wait #35)`
7. **lock**: `package lock (waiter at 0x004A94EC, holder main from 0x004A8E22) during VisitLot` [verified] 253, wait 5.8 s total, max 49 ms in 10 fixes-on / 41 default / 0 player runs; mentioned in docs/546
   e.g. `lock holders: sim waited 20 ms on lock 3052C9E0 (at 0x004A94EC), held by main 232 from 0x004A8E22 (wait #5)`
8. **held**: `unseen walk-time hold: GoHome` [verified] 312, 21018 sim-min total, max 120 in 0 fixes-on / 48 default / 0 player runs; mentioned in docs/registry
   e.g. `ts3fixes: hold: Rubisel Ichtaca [GoHome] start, 59 sim-min, live True`
9. **lock**: `package lock (waiter at 0x004A94EC, holder main from 0x004A8E22) during GetInCarpool` [verified] 216, wait 4.9 s total, max 42 ms in 10 fixes-on / 35 default / 0 player runs
   e.g. `lock holders: sim waited 21 ms on lock 3052C9E0 (at 0x004A94EC), held by main 232 from 0x004A8E22 (wait #40)`
10. **lock**: `package lock (waiter at 0x004A94EC, holder main from 0x004A8E22) during MakeConversation` [verified] 206, wait 4.7 s total, max 52 ms in 10 fixes-on / 30 default / 0 player runs; mentioned in docs/577
   e.g. `lock holders: sim waited 21 ms on lock 3052C9E0 (at 0x004A94EC), held by main 232 from 0x004A8E22 (wait #51)`
11. **lock**: `package lock (waiter at 0x004A94EC, holder main from 0x004A8E22) during WorkInRabbitHole` [verified] 166, wait 3.8 s total, max 49 ms in 10 fixes-on / 31 default / 0 player runs; mentioned in docs/529
   e.g. `lock holders: sim waited 21 ms on lock 3052C9E0 (at 0x004A94EC), held by main 232 from 0x004A8E22 (wait #67)`
12. **lock**: `package lock (waiter at 0x004A94EC, holder main from 0x004A8E22) during OrderDrink` [verified] 164, wait 3.7 s total, max 39 ms in 10 fixes-on / 33 default / 0 player runs; mentioned in docs/546
   e.g. `lock holders: sim waited 21 ms on lock 3052C9E0 (at 0x004A94EC), held by main 232 from 0x004A8E22 (wait #122)`
13. **reserved**: `DestinationPortalLocked: UseToilet target ToiletExpensive` [verified] 208 in 10 fixes-on / 31 default / 0 player runs
   e.g. `ts3fixes: routefail: Apollo Bloom 0x14BC00F559664B60 YoungAdult active, UseToilet target ToiletExpensive, PlanFromPoint sync FailedBecauseDestinationPortalLocked blocker None, goal 571.0,34.8,1668.3, at 497.2,34.1,1551.5, lot 0xF5900033BA9E8C10 room 3, 11 ms, `
14. **lock**: `package lock (waiter at 0x004A94EC, holder main from 0x004A8E22) during OrderConversation` [verified] 212, wait 4.8 s total, max 48 ms in 10 fixes-on / 30 default / 0 player runs; mentioned in docs/546
   e.g. `lock holders: sim waited 22 ms on lock 3052C9E0 (at 0x004A94EC), held by main 232 from 0x004A8E22 (wait #93)`
15. **lock**: `package lock (waiter at 0x004A94EC, holder main from 0x004A8E22) during GoToVirtualHomeInternal` [verified] 159, wait 3.6 s total, max 48 ms in 9 fixes-on / 29 default / 0 player runs; mentioned in docs/546
   e.g. `lock holders: sim waited 21 ms on lock 3052C9E0 (at 0x004A94EC), held by main 232 from 0x004A8E22 (wait #13)`
16. **lock**: `package lock (waiter at 0x004A94EC, holder main from 0x004A8E22) during DisposeOutside` [verified] 115, wait 2.6 s total, max 41 ms in 9 fixes-on / 29 default / 1 player runs
   e.g. `lock holders: sim waited 21 ms on lock 3052C9E0 (at 0x004A94EC), held by main 232 from 0x004A8E22 (wait #23)`
17. **lock**: `package lock (waiter at 0x004A94EC, holder main from 0x004A8E22) during WatchDoor` [verified] 103, wait 2.4 s total, max 48 ms in 8 fixes-on / 25 default / 0 player runs; mentioned in docs/576
   e.g. `lock holders: sim waited 48 ms on lock 3052C9E0 (at 0x004A94EC), held by main 232 from 0x004A8E22 (wait #118)`
18. **lock**: `package lock (waiter at 0x004A94EC, holder main from 0x004A8E22) during BeScared` [verified] 102, wait 2.3 s total, max 29 ms in 7 fixes-on / 28 default / 0 player runs; mentioned in docs/546
   e.g. `lock holders: sim waited 21 ms on lock 3052C9E0 (at 0x004A94EC), held by main 232 from 0x004A8E22 (wait #6)`
19. **held**: `kd cam recheck would hold, in view: GoHome` [verified] 99 in 10 fixes-on / 24 default / 0 player runs; mentioned in docs/registry
   e.g. `kd cam recheck: route 25735EF4 would hold, in view (switch off), job seq 6623 185 ms result 16, cut (UnseenFallback, mask 2), kind own Plan deadline 150 ms, follower 48EE0034E98D4D60, plan 0 host 181 other 3 ms, capAt 185 tail 0 ms, nodes 0 in 1 plans, unseen`
20. **held**: `unseen walk-time hold: AutonomouslyAttendShow` [verified] 49, 3009 sim-min total, max 88 in 0 fixes-on / 37 default / 0 player runs
   e.g. `ts3fixes: hold: Aislara Alvarez [AutonomouslyAttendShow] start, 63 sim-min, live True`
21. **held**: `kd cam recheck would hold, in view: VisitCommunityLot` [verified] 82 in 10 fixes-on / 23 default / 0 player runs; mentioned in docs/546
   e.g. `kd cam recheck: route 250899BB would hold, in view (switch off), job seq 4675 265 ms result 16, cut (UnseenFallback, mask 2), kind own Plan deadline 218 ms, follower 48EE0034E1D3CDA0, plan 1 host 247 other 17 ms, capAt 265 tail 0 ms, nodes 0 in 1 plans, unseen`
22. **reserved**: `blocked by Sim (SocialInteractionB target Sim)` [verified] 491 in 6 fixes-on / 18 default / 0 player runs
   e.g. `ts3fixes: routefail: William Fangmann 0x14BC00F559664B40 YoungAdult active, SocialInteractionB target Sim, Replan sync FailedBecauseNoPathToDestination blocker Object 0x48EE0034EA1BE0E0 Sim at 501.9,34.1,1535.6, goal 502.0,34.1,1535.1, at 501.9,34.1,1535.5, lo`
23. **lock**: `package lock (waiter at 0x004A94EC, holder main from 0x004A8E22) during FollowerMeetUpAtPoint` [verified] 102, wait 2.3 s total, max 53 ms in 10 fixes-on / 19 default / 0 player runs; mentioned in docs/577
   e.g. `lock holders: sim waited 20 ms on lock 33067A40 (at 0x004A94EC), held by main 232 from 0x004A8E22 (wait #7)`
24. **lock**: `package lock (waiter at 0x004A94EC, holder main from 0x004A8E22) during OrderFood` [verified] 87, wait 2.0 s total, max 37 ms in 10 fixes-on / 16 default / 0 player runs; mentioned in docs/546
   e.g. `lock holders: sim waited 24 ms on lock 3052C9E0 (at 0x004A94EC), held by main 232 from 0x004A8E22 (wait #94)`
25. **lock**: `package lock (waiter at 0x004A94EC, holder main from 0x004A8E22) during PillowFight` [verified] 84, wait 1.9 s total, max 45 ms in 6 fixes-on / 26 default / 0 player runs
   e.g. `lock holders: sim waited 20 ms on lock 33067A40 (at 0x004A94EC), held by main 232 from 0x004A8E22 (wait #11)`
