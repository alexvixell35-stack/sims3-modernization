# 571: Seasons / Generations / Supernatural / Showtime logic batch

2026-11-15, static (EA code read in `game-copy/decompiled`, local only). Built, proven in game 2026-11-15 (off BROKEN / on FIXED). Leads came from four read-only
lead passes (one per expansion pack), and every fix below was checked again against the EA code. Not covered: pets
routing (another agent), and the areas already handled in docs/555-570.
Each fix has its own switch, off by default. Code: `mods/ts3fixes/src/GenerationsLogic.cs`, `SeasonsLogic.cs`,
`SupernaturalLogic.cs`, `ShowtimeLogic.cs`, plus a `*Checks.cs` file for each.

Every check is a pure `Ts3Fixes.Fixes.<Name>Check("run"|"result")`, with explicit controls that print `controls ok=`
and a `members found=` line naming any missing members. Expected results:

- Switch off: `verdict: BROKEN`.
- Switch on: `verdict: FIXED`.
- A failed control or a missing member: `UNEXPECTED`.

The repros are in `tools/harness/repros.json`.

Household outcomes change only where EA's code does something it plainly does not mean. These are EA bug fixes under
CLAUDE.md, and none of them moves a Sim.

## Generations

### §1 `birthdayagers` (2577-2579, `BirthdayAgersCheck`) [verified]

**Bug:** the `kSimAgeTransition` listener of `BirthdayParty.OnAgeTransition` records the ager and returns `Remove`. Only
the first guest or host who ages up is recorded. `DidSimAgeUpAtParty` then reads false for every later ager, which
affects the wish "age up at a party" and the challenge feedback (`DreamsAndPromisesDelegateFunctions`,
`ChallengeDelegateFunctions`). This shows up with twins, or with several birthdays at one party. EA also never removes
the party's listeners.

**Fix:**
- The answer `Remove` becomes `Keep`.
- `HouseParty.CleanUp` removes a `BirthdayParty`'s listeners (`mEventListeners`).

**Test:** run a party with two Sims due to age up and use Have Birthday on both. Every ager is recorded, and 2577 counts
the later ones.

### §2 `sneakoutlistener` (2580-2583, `SneakOutListenerCheck`) [verified]

`Punishment.SneakOut` adds a `kEventSimMovedToLot` listener, `SneakOutSuccess`. Two things go wrong:

1. **Left-over sneak listener.** If the teen is caught (`SneakOutFailure`) or the outing ends on the home lot, nothing
   removes the listener. The teen's next lot change then applies Snuck Out and Fight the Power, even when that trip is
   the school carpool or comes after the grounding is over.
2. **Copy-paste slip.** `ApplySnuckOutMoodlets` tests `mSneakingOnHomeLotListener` but removes
   `mSneakingFromHomeLotListener`, so return-home listeners pile up.

**Fix:**
- `SneakOutSuccess` applies only while the teen is still grounded, is not going to school, work or travel, and is not in
  a carpool. These are EA's own `ShouldApplySnuckOutMoodlet` tests. Otherwise the fix only stops the sneak walk.
- `ApplySnuckOutMoodlets` first drops an older return-home listener and any left-over sneak listener.

**Test:**
1. Ground a teen.
2. Order the teen off the lot and let a parent catch them.
3. Let the grounding run out, then send the teen to a community lot. Snuck Out appears with the switch off and does not
   appear with it on.

### §3 `promcarpool` (2584-2585, `PromCarpoolCheck`) [verified]

**Bug:** `PromSituation.AddAlarmsAndCarpool` schedules a white limo carpool for the prom day, and `CleanUp` never calls
`RemoveCarpool`. The schedule is set with `AddAlarmDay`, which repeats weekly. The rider list is every teen on the active
home lot. The result: every week on the prom weekday, until the game is restarted, a limo takes the household's teens to
the old prom venue. The carpool is never persisted, so it does not survive a save and reload.

**Fix:** `CleanUp` calls `CarpoolManager.Singleton.RemoveCarpool(mCarpool, null)` while the carpool is still scheduled.

**Test:** attend a prom, then skip a week without saving. With the switch off the limo comes back at prom time. With it
on, 2584 is 1.

### §4 `curfewalarm` (2586-2587, `CurfewAlarmCheck`) [verified]

**Bug:** `SimUpdate.sCurfewCheckPoliceAlarmHandle` is a static marked `Persistable(false)`, and nothing resets it. If the
player quits during a teen's curfew, the next world sees a "valid" handle:
- No police curfew check is scheduled that night.
- When curfew ends, `RemoveAlarm` runs on an old ID, which may belong to an unrelated alarm.

**Fix:** reset the handle on world quit.

**Test:** with a teen out after curfew, load the save again. 2586 counts 1.

## Seasons

### §5 `trickortreatalarm` (2588-2590, `TrickOrTreatAlarmCheck`) [verified]

**Bug:** `HolidayManager.OnHolidayStarted` adds the 19:00 NPC trick-or-treat alarm with `AddAlarmDay`. That alarm is
weekly and always persisted. Its handle lives in the plain static `TrickOrTreatSituation.NPCTrickOrTreatAlarm`, and
`SendNPCTrickOrTreaters` removes the alarm only through that handle. Two failure modes:
- **Save and reload on Spooky Day before 19:00.** The alarm comes back but the handle does not, so NPC children go
  trick-or-treating every week on that weekday in every season.
- **Quit that day.** The handle stays stale, so the next world's Spooky Day adds no alarm and gets no NPC
  trick-or-treaters.

**Fix:**
- `SendNPCTrickOrTreaters` does nothing unless today is the Fall holiday. This also neutralises orphaned alarms already in
  saves.
- The static is reset on world quit.

**Test:** on a Spooky Day save from before 19:00, reload and skip to the same weekday one week later. With the switch off
NPC trick-or-treaters appear. With it on, 2589 counts the skipped run.

### §6 `snowfightjoinwait` (2591-2592, `SnowFightJoinWaitCheck`) [verified]

**Bug:** `SnowWaterFight.ControllerCallback` ends a game once `mWaitForJoinsCounter` reaches 40, counting the minutes
with one player while invitees are still walking over. Only `DoReset` clears the counter. The festival arena is
permanent, and its `DoEndOfGame` neither clears the counter nor calls the base. The count builds up over the arena's life,
so later games end the first minute only one player is present. `TargetedPlayer` and `mTargetedThrows` are also left
over.

**Fix:** after the arena's `DoEndOfGame`, clear the counter and the targeted-player state, as `DoReset` does.

**Check:** works on a stand-in arena built without its constructor.

### §7 `hauntedumbrella` (2593-2594, `HauntedUmbrellaCheck`) [verified]

**Bug:** `HauntedHouse.Enter.CanStartUsingUmbrella` returns `!mCanRouteUsingUmbrella`. The flag starts true and is set
false only for the panic, vomit and wet-self exits. So Sims queuing for the house or leaving it calmly in rain never open
an umbrella. `CloudRay` implements the same interface correctly.

**Fix:** return the flag.

**Check:** calls EA's method, through the swapped body, on a stand-in `Enter`.

## Supernatural

### §8 `permazombiemoon` (2595-2596, `PermaZombieMoonCheck`) [verified]

**Bug:** a PermaZombie is meant to be cured at the next new-moon moonrise. `BuffInstancePermaZombie.AddMoonListener`
registers only `if (mListener == null)`, and `mListener` is saved (`EventListener` is `[Persistable]`).
`EventTracker.PreWorldStartup` starts every world with an empty tracker, and nothing registers the saved listener again.
After any load the Sim stays a zombie through every new moon until cured with an item.

**Fix:** in `BuffTransformationInstance.OnLoadFixup`, for PermaZombie instances only:
1. Before EA's fixup, null the saved listener.
2. After it, if the buff was kept, register through EA's `AddMoonListener`.

**Test:** get a PermaZombie, then save and load. Advance to a new-moon moonrise (lunar cycle options). With the switch off
the zombie stays; with it on it is cured. 2595 is at least 1 after the load.

## Showtime

### §9 `steadygigmiss` (2597-2599, `SteadyGigMissCheck`) [verified]

**Bug:** `PerformanceCareer.SetAttributesForNewJob` registers `OnJobMissed` for `kOccupationJobMissed` with no actor
filter. Two misses of today's steady gig cancel it. But the event is sent for any Sim's missed gig, job or class
(`Gig.OnJobTimeout`, `Job.OnJobTimeout`, `AcademicCourse`). So performers lose steady gigs because of other Sims' misses.
After a load the listener is gone, because the tracker starts empty, and real misses never count.

**Fix:**
- `OnJobMissed` counts only a missed job of the same career.
- At world load, each performance career's listener is registered once per world.

**Known gap (not fixed):** a Sim who joins a performance career twice in one session gets two listeners, as in vanilla.

**Test:** in a loaded save, 2598 counts the listeners registered at load (switch on). To see the cross-Sim miss, the
performer's career must have started in this session, because vanilla has no listener after a load. Then give the
performer a steady gig today and let another Sim miss a work shift. With the switch off, `TimesMissed` goes up; with it
on, 2597 counts the ignored miss.

### §10 `npcshowstuck` (2600-2601, `NpcShowStuckCheck`) [verified]

**Bug:** the daily 1:00 `NPCShowManager.CreateNPCShow` returns at once while the `[PersistableStatic]`
`isNPCShowScheduled` is set. Only `PerformanceCareer.ForceStartGig` clears the flag, and it returns early when the NPC's
gig is gone. The gig can be gone because:
- a household performer booked the same venue and day, and `CreateGigFromSpec` removes the NPC gig;
- the gig was cancelled;
- the townie was culled.

After that the save gets no more NPC shows or NPC tip performers. The newspaper and the map tag also keep the old show.

**Fix:** at 1:00, if the flag's show started more than 12 sim hours ago (or has no time), clear the flag with EA's
`ClearShowTags`, then let EA run.

**Not fixed:** the booked NPC's `IncrementAutonomyDisabled` is never undone. EA keeps no reference to that Sim, and
guessing which NPC it was is not safe. That NPC stays idle.

**Test:** read the newspaper's NPC show and book a household gig at that venue on that day. On the following days, with
the switch off there are no NPC shows. With it on, 2600 is 1 at the next 1:00 after the show day and shows resume.

### §11 `gigpullaudience` (2602-2603, `GigPullAudienceCheck`) [verified]

**Bug:** the `Gig` constructor adds the always-persisted "PullAudience" alarm, set one hour before start, without keeping
a handle. `ShutDown` does not remove it. When a gig is removed or never scheduled, the alarm still fires. Cases:
- a booking replaced an NPC gig;
- a steady gig was cancelled;
- the performer quit the career;
- `CreateGigFromSpec` failed after the constructor ran.

When it fires, it resets the venue's show floors, spawns crowd monsters and routes NPC audiences to a venue with no show.

**Fix:** `PrepareVenueForShow` does nothing for a gig that is not among its career's jobs, or whose career its Sim no
longer has.

**Test:** book a gig, then quit the career more than an hour before the start. With the switch off, NPCs gather at start
minus one hour. With it on, 2602 is 1.

## Leads read and not fixed (parked or ruled out)

| Lead | Status |
|---|---|
| NpcSeasonalPartyManager: the "player threw a party" listener (a static) is lost on load, and a stale alarm handle remains | fixed in docs/572 §3 (`npcseasonalparty`) |
| Eating contest: losers who "didn't finish" get no consolation tickets (timer comparison) | the timing claim is inferred, so it is parked |
| Kissing booth: the route retry loop is inverted (`while (flag2 && ...)`), and a footprint stays disabled on an early return | verified. Fixing it needs the whole `Run` replaced, so it is parked. |
| Festival Frequenter: the ticket x2 applies on every inventory add | verified exploit. The fix would change the household's ticket income, so it is parked for a decision. |
| SeasonalLotMarker `AddUnclaimedToCommonInteraction` tests `Target` instead of `gameObject` | build mode only, very low impact |
| Lunacy keeps per-Sim alarm state on the shared Buff object | fixed in docs/572 §1 (`lunacysocials`) |
| Fairy aura reload copies the old timer only into the Soothe aura (`OccultFairy.FixUpOccult`) | fixed in docs/572 §2 (`fairyauratimer`) |
| Pack hunt followers never end if the leader is cancelled | inferred |
| Spellcasting duel: `y:Age` is set from the challenger, `mNumRoundsX` is used for Y, and the null-wand early return | animation only, or inferred |
| Full-moon zombie spawn left invisible | inferred |
| BoardingSchool pickup failure leaves the student enrolled with no school | inferred |
| PerformTheShow cleanup on reset, and the Big Show "remind guests to leave" alarm being overwritten | stuck path or timing inferred, so parked |
| Gig null `LotCurrent` in `ForceStartGig` and the reminder notifications; steady notifier leak | low impact, so parked |
| Imaginary friend, midlife crisis, daycare transport, HolidayManager's own alarms, snowman / snow angel, umbrella static map, apple bobbing, skating rink, sing-a-gram, SimFest sort, tips | read; no bug found |
