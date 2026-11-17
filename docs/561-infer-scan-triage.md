# 561: Infer# + gameplay-scan triage, photo booth fix, code layout (static, built, proven in game)

**In-game status:** `photoboothfunds`, `shelllighting`, `bonfirespice`, `djcleanup`, `vampirehuntglow`, `hottubwoohoofx`, `bribeguard`: proven in game 2026-11-14/15, Bridgeport, off BROKEN / on FIXED; all-on soak clean 2026-11-15. `djcleanup` needs a real club session to confirm end to end.

2026-11-15. Inputs: InferSharp on Sims3GameplaySystems
(`game-copy/analysis/infersharp/sims3gameplaysystems/report.txt`: 501 Null Dereference, 15 Pulse Resource Leak) and
`tools/static/gameplay-scan.sh` (`game-copy/analysis/gameplay-scan/ranked.tsv`).

## 1. Triage and rule tuning

Infer# filtered to gameplay namespaces (interactions, situations, careers, opportunities, autonomy, actor systems,
pets, objects, roles, services, skills), dropping UI, XML, tuning and data-load code: 322 distinct sites. 31 of them
are in a `Run`, `Cleanup`, `Init` or `Test`. Read against the decompiled code, most are "could be null" on paths where
EA guarantees the value (EA's Test re-checks it, or the value comes from the object's own setup). None was confirmed
as a reachable player-visible bug in this pass (leftovers below).

Gameplay-scan hits read: `deref-in-null-branch` (5): all false positives. `EnterStateMachine` sets
`mCurrentStateMachine`, the drafting canvas is set by its creation callback, and the sculpture component is added in
the branch. The rule now skips those shapes (5 → 2; the 2 left are lazy singletons). `flag-left-set-on-exit` (23): read
MurphyBed, PhotoBooth, Seesaw, PingPong (partner flag set in Cleanup), TubeElevator (footprints re-enabled in Cleanup):
all restored on exit, except the photo booth fee path (§2).
`grep-sleep-wait` (127): the partner waits read (PingPong, Seesaw, LitterBox, Shower, Karaoke) are released by the
partner's Cleanup or have a timeout.

`broadcaster-overwrite` (tools/static/ea-script-logic.semgrep.yml): 166 hits, nearly all an interaction's `Run`, a
buff's `OnAddition` (a fresh per-Sim `BuffInstance`), `OnStartup`, `OnLoad` or `Init`, which run once per fresh object.
Now only assignments in a loop or in other methods are reported (166 → about 100). New rule `fee-test-after-enter`
(ea-gameplay-logic): a funds test that returns false after the Sim entered / faded out / joined the object's use list.
It finds exactly the two photo booth sites in Sims3GameplaySystems, Sims3GameplayObjects and Sims3StoreObjects.

## 2. `photoboothfunds` (fixed, off by default)

`PhotoBooth+TakePhoto.Run` and `TakePhotoWith.Run` (master) call `RouteToAndEnterPhotoBooth` first. It adds the Sim
to the booth's use list, plays the enter animation and fades the Sim out. Only then, on a community lot, do they test
`FamilyFunds < kCostToUsePhotoBooth` and `return false`. There is no `RouteOutOfPhotoBooth` (the only fade-in) and no
`StandardEntry` (so Cleanup keeps the use-list entry) [verified, decompiled]. Test checks the funds at queue time, so
funds that drop before the Sim reaches the booth (bills, a purchase, a second photo queued by the family) leave the Sim
invisible and listed as inside. Fix (`PhotoBoothFunds.cs`): when Run returns false with the Sim still in the use list,
EA's own `PhotoBooth.Exit` is pushed next (RequiredNPCBehavior, as EA's `RemoveSims` does). A queued Exit for that
booth is not duplicated. Counters 2479 (taken out) and 2480 (errors). Check `PhotoBoothFundsCheck` (pure decision),
repro `photobooth-funds-stuck`. Exact for every Sim.

## 3. Code layout (no behaviour change)

`mods/ts3fixes/src` is flat (build-fixes.ts compiles every `*.cs` in it). The convention, already used by
AptLeave/AptLeaveChecks, HiddenRoom/HiddenRoomChecks and PartyFixes/PartyFixChecks, is one family per file, with the
harness check in a sibling `*Checks.cs`. The grab-bag files are split into that layout:

| was | now |
|---|---|
| GameplayLogic559.cs | BowlingLane.cs, GenieBanish.cs, SpaceSimPartner.cs (+ `*Checks.cs` each) |
| GameplayLogic559b.cs | BandJam.cs, StatueLine.cs, CleanFlag.cs, BirdTvNull.cs (+ `*Checks.cs` each) |
| RoutingTravel560.cs | GoHomeWalk.cs + GoHomeWalkChecks.cs (hook stays in Fixes.cs GoHomeHook) |
| SmallEaFixes555.cs | CourseListener.cs, ObservatoryMsg.cs, BubblePipeGuard.cs (+ `*Checks.cs` each) |
| BuffSharedState.cs, StoreWorldEvents.cs, BirthRoute.cs, PutDownPlace.cs, VipRoomRefresh.cs | check moved to `<name>Checks.cs` |

Each family has its own `Apply<Family>()`, called from Fixes.cs in the old order, so the patch order is unchanged.
Kept as they were: PartyFixes.cs (two families sharing reflection fields; checks already in PartyFixChecks.cs),
CookClickListener.cs and ShuffleboardBroadcast.cs (one family, checks in ObjectLeakChecks.cs), BarStaffCheck.cs
(diagnostics only). Switch names, counters, check names and repro ids are unchanged. File names are updated in
docs/registry.md, tools/harness/repros.json, docs/555, tools/swap-stack-audit.ts (TARGET_VARS) and
docs/data/swap-stack-audit.csv (the ComboOpp hand rows are kept).

Fast paths: the hooks in the moved files were read for their not-applicable path. The hot ones already exit cheaply,
so none was changed:
- `ObsHook` on `Sim.ShowTNSIfSelectable`: an enum compare first.
- `SsSimHook` on `get_RideOver`: EA's value first; false ends the test.
- GoHomeHook's walk-style step: reads the switch only when a walk style was set.
- buffstate hooks: these buffs' own OnAddition / OnRemoval only.
- `PbHook`: success first.
`ShortcutOn` is one dictionary TryGetValue on a string. No hook here calls it per tick, so no cached-switch change is
proposed.

## 4. Leftovers (ranked, not fixed)

1. `School+GraduateInCityHall`-pusher Run: `sGraduatingSims[Actor.SimDescription]` indexes without a key test
   (KeyNotFound when the graduation list was cleared before Run). Needs a check of when the list is cleared.
2. `PrivateEyeInteractions` LookThroughMail.Run: `GetActiveOpportunity(Career)` dereferenced without a null test
   (opportunity ended between Test and Run).
3. `NPCAnimal` Run: `AssignedRole as NPCAnimal` dereferenced (role removed before Run).
4. `Sim` time-statue Run: `GetTimeStatueData(type)` dereferenced (unknown type).
5. The remaining ~290 gameplay Infer# sites outside Run/Cleanup/Test, and ~100 broadcaster-overwrite hits, not read.

## 5. `shelllighting` (fixed, off by default) and the alarm census

`BuildableShell.OnStartup` calls `UpdateLightAlarms`, which schedules the day's lighting changes (Default / Few / Max at
sunrise and sunset offsets from the shell's Tuning) through `AddAlarmWithRandomDelta`. That helper drops every time
already past today (`num < 0`) [verified, decompiled], and nothing applies the lighting that should be showing now.
After a load at 22:00 the towers keep their Default material until the next alarm, hours later. Fix
(`ShellLighting.cs`): after EA's OnStartup, EA's own `DefaultLighting` / `FewLighting` / `MaxLighting` (with EA's
Dystopia handling) is called for the latest lighting time that has passed. That is today's, or yesterday's when none
has passed today yet. EA's alarms are unchanged. Counters 2481 and 2482. Check `ShellLightingCheck` (pick for EA's
default tuning, plus a read-only list of the world's shells), repro `shell-lighting-after-load`.

Agent `alarm_audit`: the `duplicate` key now includes the fire time (callback + target + owner + due ticks). The 6
lighting alarms a shell adds every day share a callback but fire at different times, so they no longer count as
duplicates.

## 6. Night batch 1: leftovers re-read, `bonfirespice` (fixed, off by default)

The four ranked leftovers of §4 are ruled out:
- Graduation: the pusher is an ImmediateInteraction whose Test checks `IsSimGraduating` right before it runs.
- PrivateEye mail: `InteractionQueue.RunTest` re-runs Test (`TestStillValid`) before Run.
- NPCAnimal role and time statue data: no reachable null found.

About 40 more Infer# sites were read:
- `CareerLoc` null in StartWorking / CheckForHolidayBonus / SetCoworkersAndBoss: EA repairs locations on build/buy
  exit (`FixUpCareersAndCareerLocations`) and on load.
- XML parse sites: data load only.
- Phone-call `TargetSim`: set when the call is placed.

About 20 broadcaster hits were read:
- Fireplace StartFire and BurnHerb: guarded by `mLit` and `HasHerb`.
- Sprinkler: paired with turn-off.
- FishTank: null-checked.
- BonFire SpiceItUp: real bugs (below).

`BonFire+SpiceItUp.Run` [verified, decompiled]:
- (a) It removes the herb from the inventory before `FindOpenSlotAndRoute`. A failed route returns false with the herb
  neither back nor destroyed, so the player loses it. On success the removed herb is never destroyed (Fireplace's
  BurnHerb destroys it), which leaks one out-of-world herb per spice.
- (b) "Already burning" is tested only in the Definition's Test, and `mHerbToAdd` is one field. Two Sims spicing at
  once (bonfire party guests do it autonomously) both pass. The second `SpiceHitFire` throws in
  `HerbData.Initialize(null)`. A second herb that lands overwrites the haze broadcaster and effect without disposing
  them, so the first haze stays around the bonfire.

Fix (`BonfireSpice.cs`): after Run, a herb this Run removed that is neither in the world nor in an inventory goes
back to the inventory on failure and is destroyed on success. `SpiceHitFire` with no herb to add returns. With a herb
already burning, EA's `RemoveHerb` runs first. Counters 2483-2485. Check `BonfireSpiceCheck`, repro
`bonfire-spice-herb`.

## 7. Late Night batch: `djcleanup`, `vampirehuntglow`, `hottubwoohoofx`, `bribeguard` (fixed, off by default)

Scope: nightclub (velvet ropes, bouncer, DJ booth, bar, dance floor), celebrity (manager, paparazzi, pay-off, disgrace,
free stuff), vampire (hunt, occult add/remove, broadcaster), elevator, subway, hot tub, band code. Method: both semgrep
configs scoped to the 116 Late Night files, the unbounded-wait grep, a Cleanup-touches-shared-state scan, then a read of
each system's Run / Cleanup paths. Bartender (docs/558, other agent), band jam (`bandjam`), apartment farewells
(`aptleave`) and the elevator doors (§ routing pass) are skipped.

Fixed:
- `DJTurntable+DoPlay.Cleanup` [verified, decompiled] ends the booth owner's set (`DJOwnerSim.AddExitReason`,
  `ExitDJBooth`) and destroys the tip jar whenever `TurnedOn`, with no owner check. Cleanup also runs for a DoPlay
  removed from the queue (cancelled, or re-Test failed because another Sim got to the booth first). So Sim B's
  cancelled or beaten "Play Track" ends Sim A's set. `RequestMusic.Cleanup` sets `RequestedGenre = -1` the same way,
  so a patron's cancelled or unrouted request clears another patron's pending one, and that patron boos a DJ who
  complied. Fix (`DjBoothCleanup.cs`, `djcleanup`, counters 2486-2488): a non-owner DoPlay that would touch the booth skips
  EA's Cleanup body and runs only its per-Sim part (`mOverrideInteractionName = null` and the steps of
  `InteractionInstance.Cleanup`, re-done member by member through reflection, since the base cannot be called
  non-virtually without Reflection.Emit). The booth's `TurnedOn` / `DJOwnerSim` / `TipJar` are never written, so the
  owner's set cannot read as off at any moment, yield or not. If a base member is missing the DoPlay part is not patched
  (logged). A RequestMusic that never passed `DoRouting` keeps the booth's `RequestedGenre`. Check
  `DjBoothCleanupCheck`, repro `dj-cleanup-other-set`.
- `OccultVampire+VampireHunt.LoopDel` [verified, decompiled] drops candidates whose `LotCurrent` changed without
  clearing the `HeatVision` visual override Run gave them; Cleanup only clears the Sims still listed. A patron who
  leaves the club during a hunt glows for the session. Fix (`VampireHuntGlow.cs`, `vampirehuntglow`, counters
  2489-2490): before EA's body, each candidate that left and still exists gets the override set back to None. Check
  `VampireHuntGlowCheck`, repro `vampire-hunt-glow`.

Read and ruled out (or not worth a switch):
- semgrep hits: celebrity listeners (Unregister runs first), vampire broadcaster (lot `UpdateReactions` disposes
  broadcasters of destroyed Sims), hot tub child broadcaster (null-guarded), Subway busking cancel (exit uses the
  origin hole; stale posture hole only), HotTub same-branches / float compare (weights are integers).
- Waits: bar `RunWaitingLogic` (exit-reason checked), `PlaceObjectHelper.Wait` (one-shot), elevator doors (ruled out
  before), TattooChair (UI pause waits).
- Velvet ropes: the sneak-in temp list is cleared nightly (`NightlyCleanUp`).
- Paparazzi role, PayOffPaparazzi, disgrace queue, false accusation (level index guarded by `CantBeDisgraced`),
  free-stuff alarm: no reachable bug found.

- `HotTubBase+CuddleSeatedWooHoo` [verified, decompiled]: animation event 110 calls `StartWoohooFX`, event 111
  `StopWoohooFX`; the only other stop is the tub's `CleanupFromDisposeOrReset`. Cleanup clears `mSimsAreWooHooing` but
  not the effect, so a Sim reset between the two events (queue reset runs before the posture reset, so Cleanup still
  sees the tub) leaves the effect on the tub. Fix (`HotTubWoohooFx.cs`, `hottubwoohoofx`, counters 2491-2492): after
  that Cleanup, with the flag cleared and the effect still on, the tub's `StopWoohooFX` runs. Check
  `HotTubWoohooFxCheck`, repro `hottub-woohoo-fx`.
- `VelvetRopes.ChargeSimForBribe` [verified, decompiled] calls `mBribeDictionary.TryGetValue` with no null check;
  `NightlyCleanUp` (bar closing pass in Bartending, LotManager's nightly pass for other lots) nulls it. A bribe offered
  just before throws in the Success / Failure / Unnecessary social callback. Fix (`BouncerBribeGuard.cs`,
  `bribeguard`, counters 2493-2494): with a null dictionary it returns false, EA's result for a Sim with no recorded
  bribe (no charge, EA's "no money" line). Check `BouncerBribeGuardCheck`, repro `bouncer-bribe-null`.

Leftover (not fixed): the hired DJ's `DoPlay.Run` waits `while (ActorsUsingMe.Count > 0)` with no exit-reason check
(bounded in practice: WaitForRequest patrons leave after 3-7 min).
