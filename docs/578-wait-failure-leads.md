# 578: next wait and failure areas, ranked from the 2026-11-15 wait census (leads, no fix yet)

Date: 2026-11-15. Input:
- `wait-report.ts` (cleanup branch) on `phase-20261115-1146` (all-on) vs `phase-20261115-0523` (all-off), Bridgeport;
- the decompiled scripts.

This is a working list for the next per-group census (ts3agent `sim_health`). Each row says what the code shows and
what is still missing. Rows done in docs/577 are not repeated: bar drink time, the social replan loop
(`socialreplanlog`), busy-partner waits (`npcbusypartner`) and autonomy re-picks (`autonomyinuseblock`).

## Ranking (sim-minutes, all-on | all-off)

| # | Row | 1146 \| 0523 | Status |
|---:|---|---|---|
| 1 | Service queue: `MakeConversation` bartender waits for the customer | 774 min x45 \| 1100 x63 | closed: EA timing, see §1 (1335) |
| 2 | Social sync: `SocialInteractionA` / `B` ok waits | 774 min x295 \| 447 x200 | normal approach routes (1-5 min each); the long tail is docs/577 §2 |
| 3 | `ShowVideos` timeouts | 117 min x23 \| 51 x10 | `socialfinish` (docs/546 §9), proven |
| 4 | `PutDownPet` waits for the carrier | 145 min x6 \| 63 x2 | open, see §2 |
| 5 | Household `AutonomousSplashInPuddle` plan failures | x9 \| x6 | EA bug, fixed: `puddlereach` (§3) |
| 6 | Door lock rows (`DestinationPortalLocked`, BedSleep / UseToilet / ReadBook) | x37 \| x31 | not a wait: see §4 |

## §1 Bartender conversations: the bartender stands 17 sim-min on average for a 5-10 min chat

[verified, decompiled] Customer side, `BarAdvanced.BarInteraction.Run` for `OrderConversation`:
1. it waits for the bartender's Routed;
2. it plays the order state;
3. `RunOrderBehavior` runs `DoTimedLoop(RandomUtil.GetInt(kMinConversationTime 5, kMaxConversationTime 10))`;
4. only then does it set Committed.

Bartender side, `BartenderInteraction.Run` for `MakeConversation`: it routes to the customer, plays "Take Order -
Interact", then waits for Committed with `float.MaxValue` giveup.

Measured, the bartender's Committed wait averages 17.2 sim-min in both runs (774/45, 1100/63), well above the 5-10 min
loop. The customer's own logged waits are short (`Routed ok` 1.3-1.9 min).

Lead [inferred]: the gap sits between the customer's Routed and Committed: the order animation, or the customer's queue
pushing something in between. The bartender's wait has no giveup, so any stall there holds the bartender and every queued
customer behind it (`Target.mInteractions`).

**`barorderlog`** (log only, off, counters 3974-3975, BarOrderLog.cs) gives the customer-side times. It is a
prefix/postfix on `OrderConversation.RunOrderBehavior` and writes two lines:
- `barorder: <customer> -> <bartender>, start, bartender sync L, customer queue N, <time>`;
- `barorder: ..., end ok|fail after M sim-min, exit reason R, <time>`.

Read them with the bartender's syncwait line, whose Committed wait ends when the customer sets Committed:
- customer Routed -> start = the order animation;
- start -> end = the chat loop;
- end -> the end of the bartender's wait = the exit animation.

Capped at 300 lines per session.

**Result (phase-20261115-1335, 114 proven fixes on, barorderlog on) [measured]:** 29 customer chats, median 7.5 and max
10.7 sim-min, all ok, none dropped. That fits EA's `DoTimedLoop(GetInt(5, 10))` plus the order and exit animations. The
17-min mean bartender wait in 1146 therefore includes the customer's walk and order before Committed. It is EA timing,
not a stall. Lead closed. `barorderlog` stays a diagnostic.

## §2 `PutDownPet` waits

**Per-run count done (2026-11-15), result: none of our switches raises it.**

Counted the syncwait `in PutDownPet` lines over the 56 Bridgeport runs, with each run's switches taken from
`earlyfix.txt` / `phase.txt`:
- `phase-20261114-1737` (only `synctime`, logging): 7 waits, 195 min.
- `-1835` (4 switches): 6 waits, 137 min.
- `1146` (88 switches): 6 waits, 145 min.
- `0523` (only `synctime`): 2 waits.

Our pet switches (`littercap`, `wildhorsegender`, `horsetraitscore`) were on in only 2 runs, so there is too little data
to call them, and the synctime-only runs already reach the same count. The spread follows how many pets the harness
socials happen to pick up.

What remains is EA behaviour [measured, not yet read in code]: the pet waits at Started 28-45 min while its carrier is
still in `PutDownPet`, routing (`Sassy(LittleDog) -> Aria ... timeout 45.1/45, partner ... (linked running, routing)`).
RE result below.

**Root cause [verified, decompiled].** `Sim.PutDownPet` runs on both Sims as a linked pair:
- The pet's half comes from its `BeingCarriedPetPosture` standing transition, or is linked from the carrier's half.
- The pet calls `StartSync(false)`. Its pre-sync loop ends as soon as the carrier's current interaction is the linked
  `PutDownPet`. The pet then waits in `WaitForSynchronizationLevelWithSim(carrier, Started, 30)`.
- The carrier's half places a jig and routes to it first. It returns false without `FinishLinkedInteraction` when
  `FindGoodLocationNearby` fails, when `DoRoute` fails, or when `SafeToSync` fails. `SocialInteraction.Cleanup` does not
  end the linked half either.
- The pet's wait loop never checks that its partner's half still exists. So a carrier whose route fails leaves the pet in
  its arms, waiting the full 30 sim-min.
- If the carrier's own queue holds the pet's request behind something long, the pet waits up to 15 more minutes in
  pre-sync (45 in total).
- The timeouts in the log (`partner ... (linked running, routing)`) are a carrier routing for that whole time. That case
  is EA waiting for a real route, and the fix leaves it alone.

**Fix `petputdownwait`** (off, 2703-2705, PetPutDownWait.cs; check `PetPutDownWaitCheck`, repro
`petputdownwait-check`):
- The pet-side Started wait of `PutDownPet`, which passes no loop callback of its own, gets one through EA's
  `notifySimMinutes` mechanism, polled every sim-minute.
- The callback returns false, ending the wait through EA's own failure path, once the carrier no longer has the linked
  `PutDownPet` running or queued and is not syncing with the pet.
- While the carrier's half is alive the callback returns true, so EA's wait is unchanged.
- It applies to every Sim, household included. This is an EA bug fix with an unambiguous intent: a linked pair cannot
  sync once one half is gone, so only the dead minutes go.
- Counter 3977 counts the case with the switch off.
- The wiring is one call in the shared wait hook (SocialReceiveFail.cs).

## §3 `AutonomousSplashInPuddle`: Test accepts puddles that Run can never reach (EA bug, low impact)

[verified, decompiled] `Sim.AutonomousSplashInPuddle.Definition.Test` passes when
`World.HasAnyValidRainPuddlesOnLot(lot)`. `Run` then plans to the 3 nearest outdoor-valid puddles and returns false when
none plans: no exit reason, and the target is the Sim itself, so there is no block.

Measured: Matty's goals sit at y 37.1 while she stands at 34.1, a puddle one level up (roof or balcony):
`FailedBecauseNoDestinationSpecified`, 3 plans per try, re-picked x9 in one run.

`splashroof` (docs/546 §16) already drops roof puddles from NPC runs, but only for NPCs, and Run still fails when only
roof puddles are left, so Test keeps offering the splash.

Fix: `puddlereach` (PuddleReach.cs, off by default, counters 3979 withheld / 2707 seen with switch off / 2708 errors).
After a Run that returns false while the lot still has rain puddles, the Sim gets a memo: lot, puddle count, sim-minute.
With the switch on, Test returns false for that Sim on that lot while the puddle count is the same and less than 30 sim-min
have passed. A new or dried puddle, a different lot or the expiry clears the memo, and Run decides again.

This applies to every Sim, household Sims included. It only withholds an option whose Run just failed against the same
puddles, so the outcome is vanilla's minus the failed tries. Run keeps its single hook: SplashRoofRunHook now passes the
result to `PrRunDone`. Check: `PuddleReachCheck` (pure, with controls: new puddle, other lot, expired, no memo, puddles
gone, not autonomous). Repro: `puddlereach-check`. Value is small: about 9 failures and 27 plans per run, one household Sim.

## §3b Mopping: one unreachable puddle ends the run and is re-picked (EA bug, household-visible; s58)

s58 (Bridgeport): MopPuddle #3 by route-plan time (96 s), routefails NoPathToDestination / DestinationPortalLocked for household Sims.

[verified, decompiled] `Terrain.Mopping.Run` loops `GetClosestPuddle; MopTile; if (HasExitReason) break`. `MopTile` adds
RouteFailed when `RouteToPuddle` fails or `PuddleManager.CanMopPuddle` names a blocker, so the run ends on that puddle and
returns true. `GetClosestPuddle` only looks at the start puddle's room and level and has no way to exclude a tile. Autonomy
reaches it through `Sim.MopPuddles` / `MopBurntTiles` (`MopFloor`): Test passes while some puddle passes
`IsLocationMoppableBySim` and `CanMopPuddle`; Run pushes Mopping on the *first listed* puddle passing
`IsLocationMoppableBySim` (no `CanMopPuddle`, no reachability), so the same unreachable puddle comes back every time.
`RouteToPuddle` itself is fine (max radius -1 means none; the retry without footprint checks is for mops only).

Fix `mopunreachable` (MopUnreachable.cs, off, [2766-2771]): a copy of Mopping.Run with a per-run skip list. A route that fails
without an exit reason, or a blocked tile, is skipped and the next closest tile is tried; a cancel ends the run as in EA. Only
the first failed route plays the route-fail reaction. The run ends RouteFailed (showing the last blocker) only when it mopped
nothing. A tile mopped at but still dirty without an exit reason is not retried (EA would loop on it). The skipped locations go
into a per-Sim memo (lot, puddle + burnt count, sim-minute) with puddlereach's rules (other lot, changed count or 30
sim-minutes drop it); while it holds, `MopFloor.IsLocationMoppableBySim` answers false for them, so Test stops offering a mop
whose only puddles are unreachable and Run picks a reachable one. Check `MopUnreachableCheck`, repro `mopunreachable-check`.

Other callers re-read: `DrinkPuddle.Run` and `PlayInPuddle.Run` (pets) route to `GetClosestPuddle(lot, Hit)` but remove or
spread the puddle at Hit's tile. Fix `petpuddletile` (off, [2772-2774]): Hit becomes the closest puddle before EA's Run
(GetClosestPuddle from that point returns it again), so the tile removed is the tile used. Check `PetPuddleTileCheck`.
`SplashInPuddle` is puddlereach / splashroof. Lead, not built: `AbsorbPuddle` (plant Sims) has Mopping's loop.

## §4 Door-lock rows are a probe, not a Sim waiting

The household `PlanFromPoint` `DestinationPortalLocked` rows (goal 815.1,29.4,1658.2, the same point for ReadBook,
SocialInteractionA and BedSleep) are the `Sim.IsPointInLotSafelyRoutable` probe that `Service.CreateSim` and the virtual
resident code run on `PlumbBob.SelectedActor` (identified in docs/546 §16 row 5). The interaction named is just whatever
the selected Sim was doing. No Sim waits on them. `wait-report.ts` (cleanup branch) now puts a
`PlanFromPoint` with a goal more than 100 m away in its own `probe` row. In run 1146 that moves 24 of the 37 door-lock
rows (14 of 31 in 0523).

## Next

1. After the next soak, read the per-group census.
2. Rank by household minutes first, then NPC minutes.
3. Turn on `socialreplanlog`, and leave `autonomyinuseblock` / `npcbusypartner` off so their off-arm counters
   (2694, 2697) measure the frequency.
4. Pick the top two rows for RE.
