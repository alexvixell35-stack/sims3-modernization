# 552. Hidden rooms: Sims that cannot leave (apartments)

**In-game status:** `hiddenroomnpc`, `hiddenroomspawn`: proven in game 2026-11-14/15, Bridgeport, off BROKEN / on FIXED; all-on soak clean 2026-11-15.

2026-11-14, static audit of the 1.67 script code (Sims3GameplaySystems, decompiled locally), check and fix built (in-game status above).
Player report (a project dev): "if there are hidden rooms on the ground floor, Sims tend to get stuck. The game likes to
spawn Sims on the ground floor, and if the spot is in a hidden room they cannot figure out how to use the doors to get
out. Check apartments." Switch `hiddenroomnpc` (default off, `mods/ts3fixes/src/HiddenRoom.cs`, counters 2379-2383),
harness check `HiddenRoomCheck` (`HiddenRoomChecks.cs`), repro `hidden-room-trap`.

## 1. What a hidden room is and why nobody gets out

- A hidden room is a room that holds a world-builder `HiddenRoomMarker` (a `TombRoomMarker` subclass). `Lot.UpdateRoomMarkers`
  copies the marker rooms into the lot's saved hidden-room list; `Lot.IsRoomHidden(room)` reads it. No other writer found:
  the list changes only when the lot loads or is rebuilt [verified, `Lot.cs`; also docs/105 §4a].
- Late Night apartment buildings use them for the NPC neighbours' units: a residential lot with a `PublicRoomMarker`
  (lobby, corridors) and hidden rooms behind "NPC doors" (`Door.mIsNPCDoor`, `CanGoThrough` false); virtual residents
  appear and vanish at those doors (`Lot.GetAssociatedNPCDoor`) [verified, `Door.cs` MakeNPCDoor, `Service.CreateSim`,
  `SimDescription` virtual-home instantiation]. World Adventures tombs use them for fogged rooms behind secret doors.
- **`CommonDoor.IsAllowedThrough(src, dest, sim)` returns `DoorRestriction` when either side is a hidden room, as its very
  first test**, before the service exemption (firefighters, police ...), `SimFlags.IgnoreDoorLocks` and the lock types
  [verified, `CommonDoor.cs` ~1149]. `Sim.OnPortalPathPlan` turns that into a locked portal for the route planner and adds
  the door to `LockedDoorsDuringPlan` [verified, `Sim.cs` ~54341]. So every door of a hidden room is locked in both
  directions for every Sim: a Sim inside can only plan routes inside the room; anything outside fails as
  `FailedBecauseDestinationPortalLocked` or no path, and the Sim plays route failures ("can't figure out the doors").
  Stairs and elevators have no hidden-room test [verified, `Stairs.cs` ~733, `ElevatorDoors.cs` ~1448], so a hidden room
  with stairs is escapable; a ground-floor unit box with only a door is not.
- Lock types (for context): `tLock` Anybody, Nobody, SelectedActor, SelectedHousehold, Pets, OwnerList, VIPRoom; strays
  not greeted on the lot, one-way doors, horses vs non-barn doors are also `DoorRestriction` [verified]. Apartment unit
  doors of the player's household are ordinary doors; neighbour units are hidden rooms, not locks.
- FGL (`World.FindGoodLocation`) has no room-connectivity flag: `StayInRoom` keeps the start room, `RequiredRoomID` pins a
  room, `Routable` only asks for a routable surface [verified, `FindGoodLocationBooleans.cs`, `World.cs` ~979]. EA guards
  Sim placements with `Sim.IsPointInLotSafelyRoutable` (a route plan from the point to the lot corners and edge
  midpoints, with the Sim's own portal rules), which rejects a hidden-room spot [verified code; inferred that the plan
  fails there, since every portal out is locked].

## 2. Where EA places a Sim on a lot, and which places skip the check

| Placement | Room/route check | Can land in a hidden room |
|---|---|---|
| Virtual resident at home (`SimDescription` virtual-home instantiation): NPC-door lane slots | skips hidden slots, `IsPointInLotSafelyRoutable`, public-residential test (on the `lot` argument, not `VirtualLotHome`: a small EA slip, harmless for hidden rooms) | no [verified] |
| same, elevator branch | public room, FGL `StayInRoom`, routable check | no [verified] |
| same, last fallback `Service.GetPositionNearLotCorners` | corners: FGL `StayInRoom` + routable; mailbox: FGL, **no routable check**; then `mailbox.Position`, then **`lot.Position`** | only via the last two fallbacks [verified code; how often: check] |
| `Service.CreateSim` (virtual residents working a service) | NPC door as above; **elevator branch assigns the level-0 elevator spot before testing that its room is public, so with no public one the last elevator's spot is kept and used** (the Sim is created hidden there) | yes, when that slot's room is hidden or private [verified code, EA bug] |
| Services otherwise (`GetPositionInRandomLot`, burglar/firefighter `GetPositionNearLotCorners`) | off-lot road spot / corners | no (corner/mailbox fallbacks as above) |
| `Lot.EntryPoint` (household Sims instantiated at home, `Household` member fix-up, `SimDescription` send-home path) | front-door slot on the outside side, else mailbox slot, else **`lot.Position`** at terrain height | only via the last fallback [verified] |
| Reset: `Sim.DoReset` -> `AttemptToPutInSafeLocation` -> `AttemptToFindSafeLocation` | `IsInSafeLocation` (routable check), FGL from the Sim + routable check, FGL none + routable, `FindRoutablePointInsideNearFrontDoor` (placement check = routable) | the final fallback is **`SetPosition(lot.Position)`** with no check [verified] |
| `PassportPickupReturnedSimSituation` | `SetPosition(LotHome.Position)` | yes, if the lot centre is a hidden room [verified code] |

`Lot.Position` is the average of the four lot corners at terrain height: the ground floor at the lot centre [verified,
`Lot.SetPositionFromCorners`]. On a large apartment building the lot centre is inside the building, so these fallbacks
land in whatever ground-floor room covers the centre. That matches "the game likes to spawn Sims on the ground floor"
[inferred; the check's `centre=` field shows the room per lot].

## 3. EA's recovery does not cover it

- `LotManager.RunStuckCheckHelper` (started nightly for every actor, `LotManager` nightly cleanup): after 20-60 sim-minutes,
  **returns at once if the Sim is on the active household's lot or its position changed at all**; else it tries
  `Sim.kNumberOfPlanAttemptsForStuckSim` (10) routes to random outside spots and re-arms, resetting only on the 3rd
  attempt [verified, `LotManager.cs` ~95-145]. A Sim pacing a hidden room on the player's apartment lot is never reset.
- `HostedSituation.mNumRouteFailures` (parties, foreign visitors) only counts arrival failures [verified]. No route-fail
  counter in `SimRoutingComponent` triggers a reset [verified: none found].
- Our StuckWatchdog (`watchdogidle`, `RouteFixes.cs`, default on) has a hidden-room case [47], but under the stillness
  rule (same spot within 1 m and same interaction for 60 sim-minutes if unseen, a whole sim-day if seen), and the
  route-fail case [49] (>= 10 failed plans over 30 sim-minutes within 1 m, cleared by any successful plan, also inside the
  room). A trapped NPC that walks around the room and keeps trying new interactions on the active lot can miss both
  [inferred from the code; the check's Sims lines show `routeFails`]. NRaas Overwatch resets hidden-room Sims nightly
  (docs/07) [community].

Verdict: a real EA bug, in two parts: (a) placements that fall back to `Lot.Position` (and `Service.CreateSim`'s kept
elevator spot) with no room check, while every other EA placement checks; (b) no recovery for a Sim in a room that is
closed to everyone. The fix acts on the outcome (b), which covers every cause in (a) and saves already trapped.

## 4. Fix `hiddenroomnpc` (default off)

Every 10 sim-minutes (alarm, NeverPersisted): for each NPC (`IsNpcSim`, PlayerSims.cs) standing in a hidden room on a
non-world lot, not carried, not in a rabbit hole, not a tomb mummy, with **no allowed portal path to the outside**
(`HrReachesOutside`: EA's own allowed-room flood from `Sim.RecalculateAllowedRooms` with `IsAllowedThrough(from, to, sim)
== None`, read-only, unknown on any exception = no action), seen so on two passes in a row, or on the first pass once one of
its route plans has failed (`sRouteFails`, RouteFixes.cs; kept while `watchdog` + `watchdogidle` are on): EA's own placement moves it
(`AttemptToPutInSafeLocation(false)` if its queue is empty, else `SetObjectToReset`); a second try that sim-day uses
`SetToResetAndSendHome`. At most two tries per Sim per sim-day, then left and counted. Household and selected Sims are
counted once and logged (`recent`), never moved. A hidden room the Sim can leave (a secret tomb door, stairs) is not
touched. Counters: [2379] first tries, [2380] second tries, [2381] household Sims seen trapped, [2382] left after two
tries, [2383] errors. Not a hook: nothing in EA's code changes, no swap-stack row.

## 5. Check `HiddenRoomCheck`

`reflect_call Ts3Fixes.Fixes.HiddenRoomCheck ["apt"]` (or `"all"`, `"lot <hex|active>"`, `"place <hex|active> <n>"`,
`"trap <hex|active|hidden>"`, `"who"`). Default modes are read-only (FGL queries and route plans). Output, one line per lot with a
floor-0 problem:

```
lot <id> "<name>" res apt hh=<household> probes npc=<NPC> res=<resident> | floor0: r<room>(<tags> portals=<n> quads=<n> npc=out|closed res=out|closed) ... | hidden markers other floors <n>
  spots: entry=<room> centre=<room> corners=<room> svcElevator=<room ...> fglCentre=<room safe|unsafe> fglRandom <k>/<n> in problem rooms, EA-accepted <a>
sim <name> npc|household lot <id> "<name>" room <r> level <l> hidden exit=closed routeFails=<n> doing <interaction>
summary: lots <n>, with floor-0 problems <n>, floor-0 rooms hidden <n> sealed <n> closed-to-npc <n> closed-to-resident <n>, FGL landings in problem rooms <n> (EA routable check accepts <n>), Sims in hidden/closed rooms <n>, hiddenroomnpc on|off, counters 2379-2383: ...
```

Tags: H hidden, S sealed (no portal object: also builders' wall voids, see `quads`), N closed to a non-resident NPC,
R closed to a resident. A spot tag `(H)` on `centre=` means the reset / entry-point / corner fallbacks put Sims into a
hidden room on that lot; `svcElevator=... NOT public` is the `Service.CreateSim` bug's spot; `EA-accepted` > 0 would mean
EA's routable check fails to reject a hidden room (expected 0). `trap` places one adult NPC directly into the lot's first hidden
room (repro step); `who` and the end of `lot` give each trapped Sim's status. Sealed rooms the player built (walled-in Sims) are reported, never acted on.

## 6. Bridgeport lots to look at

The apartment / penthouse lots with households (docs/12): Bridgeport Arms, The Tidewater, The Simerset, One Leo
Center, Hieroglyph Condos, Bachelor Tower, Maloney Tower, Glenview Manor; plus any lot `apt` lists with hidden markers.
Which of them have a hidden room under the lot centre is the first thing the check answers [not yet measured].

## 7. First in-game scan (Bridgeport, phase-20261114-1923)

- `HiddenRoomCheck all`: 36 lots, all 36 with floor-0 problem rooms; floor-0 rooms hidden 40, sealed 123, closed to the
  NPC probe 144, closed to a resident 88; FGL landings in problem rooms 117, **of which EA's `IsPointInLotSafelyRoutable`
  accepted 0**; no Sim in a hidden or closed room at that time [verified in game]. So FGL itself happily returns spots in
  hidden rooms, but every EA placement that follows FGL with the routable check rejects them: the risk is only the
  placements with no check (the `Lot.Position` fallbacks and `Service.CreateSim`'s kept elevator spot, §2).
- Founders Keep - 1br, 1ba (`F59000345CBEAE40`): `entry=outside centre=r28(HSNR) corners=outside svcElevator=r27 public
  fglCentre=r28(HSNR) unsafe`: **the lot centre is inside hidden room 28 on the ground floor**, so a reset that runs out of
  safe spots, or an entry point with no front door and no mailbox, puts the Sim straight into it [verified in game for the
  spot; the fallback being reached there: not seen yet].
- The first trap (EA's Teleport Me Here, queued) never landed during the off arm: the NPC was seen nowhere in the room,
  and the on arm counted one move (2379 = 1), so the queued teleport most likely ran later, behind the NPC's current
  interaction, and the fix then moved it [inferred]. `trap` now places the NPC directly (EA's `AttemptToPutInSafeLocation`
  sequence: SetPosition, SetForward, RemoveFromWorld, AddToWorld) and returns its RoomId and lot at once; `lot` ends with
  one status line per trapped Sim (`still in room` / `moved by fix (how)` / `left (not by the fix)`), and `who` lists
  every Sim in a hidden or closed room with the time it has been there and its route-fail count. A FIXED verdict needs
  `moved by fix` (the fix's own record) or counter 2379, which counts move actions, never passes.

## 8. Second run (phase-20261114-1937): who leaves, and where the bug bites

- Founders Keep's hidden room 28 (1956 quads, the whole ground floor of the building) has **no portal at all**, and most
  other floor-0 rooms of Bridgeport buildings are sealed wall voids (S tag, 15 on Founders Keep, 123 world-wide): a Sim
  there cannot walk out even past the hidden-room rule; only a reset or teleport moves it [verified in game: portals=0].
- The trap placed Vladimir Schlick (a **resident** of Founders Keep: the picker did not exclude residents) in room 28 on a
  non-active lot; 150 s at ultra (~1150 sim-minutes) later he stood in room 27 (the lobby, the `svcElevator` room) doing
  nothing, with hiddenroomnpc off and its counters 0. A room with no portal cannot be walked out of, so a reset placed him
  there: most likely our StuckWatchdog [47] (on by default: an unseen Sim still for 60 sim-minutes in a hidden room ->
  `SetToResetAndSendHome`, EA's placement -> the lobby), else EA's nightly `RunStuckCheckHelper` (non-active lot, not
  moving, third attempt) [inferred: the run logged neither the moved-Sims entry nor counter 47]. When within the 1150
  minutes is not in the log; the new event watcher records it (below).
- Natural cases: Lenny Shutter and Jun Xu (NPCs, `VisitLot`) stood in hidden room 14 of One Leo Center at the moment of
  a check, exit closed, route fails 0-1; neither was there at the next check. How they got in is not known yet [open]:
  EA's virtual-resident paths fade them out at their NPC door (`GoToVirtualHomeInternal`) and spawn them on the
  non-hidden side [verified code], so the candidates are the unchecked `Lot.Position` / mailbox fallbacks (§2) or a
  room-id definition mismatch at a door slot. The watcher's `in` lines (previous room, interaction, moved-Sims entry)
  answer it.
- Where the bug bites: on a **non-active lot** a trapped Sim is reset within about an hour by watchdogidle [47] (unseen)
  or within a night by EA's stuck check, so it is self-healing. On the **active household's lot** EA's stuck check never
  runs and watchdogidle needs a whole sim-day of stillness in the same interaction for a seen Sim, so a visitor or service
  NPC that lands in a hidden room of the player's apartment building can stay for a day or more [inferred from the code].
  `hiddenroomnpc` has no lot exclusion (only the household rule), so it covers the active lot's NPCs after two 10-minute
  passes [verified code].
- Check changes: `trap` now picks a non-resident (LotHome and VirtualLotHome not that lot), and its N/A line lists the
  lots with a floor-0 hidden room and their households; `trap` and `who` start a test-only watcher (every sim-minute) that
  logs `event in/out` lines: when a Sim entered or left a hidden room, from/to which room, its interaction, and what moved
  it (the watchdog or hiddenroomnpc entry of the moved-Sims log, or "walked/other"). The repro now traps on the active lot.

## 10. Third run (phase-20261114-2022): who moved the trapped NPC, and prevention

**How Vampire ClanHead left (inferred from code + log, consistent timing).** Trapped at 4:05 PM in hidden r16 of the
active lot Trawler Lodge (centre in r16), he failed every plan (VisitLot, then AutonomousSplashInPuddle: native result
10, 0 nodes; the same follower object id before and after, so he was moved, not destroyed and re-created). Out at 4:39 PM
to r13 on lot F59000345CBEA810. EA's stuck check cannot be it (`RunStuckCheck` returns at once on
`Household.ActiveHouseholdLot` and resets only on its 3rd attempt, 60+ min; verified). VisitSituation's force-outside
reset (120/240 min) skips the active household's lot (verified). What fits is our StuckWatchdog **[49] route plans
failing** (RouteFixes.cs: 10+ failed plans spanning more than 30 sim-min within 1 m, checked on 10-minute passes), whose
`SetObjectToReset` runs EA's placement: `AttemptToFindSafeLocation` rejects the hidden spot (not safely routable) and
picks a spot inside near a front door (lot F59000345CBEA810). **34 min is not a constant**: it is 30 min after the
first failed plan plus up to one 10-minute pass (window 30-40 min). The watcher said "walked/other" because of a watcher
bug: the watchdog writes its moved-Sims entry when it decides, while the Sim is still inside, and the watcher took that
entry as the baseline on the next in-room tick. Fixed: the exit now compares with the entry at the time the Sim went in.
The trap status now follows the Sim through its description and says where it is when it is not in the world.

**Rescue sooner.** `hiddenroomnpc` now acts on the first pass when the Sim has a failed route plan (≤ 10 sim-min after
it tries to leave), still at most two tries per sim-day; the 2-pass rule stays for a Sim with no recorded failure.

**Prevention: `hiddenroomspawn` (HiddenRoomSpawn.cs, default off, NPCs only; counters [2387-2390]).** A second switch,
not part of `hiddenroomnpc`: it changes EA placement code (three hooks, swap-stack rows added) where `hiddenroomnpc` only
sweeps, so each can be A/B-tested and turned off alone. A spot is bad when it is in a hidden room or (with a Sim) in a
room it has no allowed way out of (`HrReachesOutside`).
- Reset's last step (`Sim.AttemptToPutInSafeLocation`, when `AttemptToFindSafeLocation` found nothing): EA's
  `Lot.Position` is replaced, when bad, by `Lot.EntryPoint()` (outside the front door) if good, else EA's off-screen
  service start spot `Service.GetPositionInRandomLot` [2387]. The rest of the method is EA's (replayed line for line).
- `Service.GetPositionNearLotCorners`: a bad answer (its unchecked mailbox / `Lot.Position` fallbacks) is replaced the
  same way [2388].
- `Service.CreateSim`: a created NPC standing in a bad spot (the kept level-0 elevator spot) is moved to
  `GetPositionInRandomLot` of its virtual home or the target lot before it routes [2389]. Errors [2390].
- `Lot.EntryPoint` itself is not hooked: it has no Sim (household Sims use it too), its own `Lot.Position` fallback needs
  a lot with neither front door nor mailbox, and every Bridgeport problem lot scanned answered `entry=outside`.
  `GetPositionInRandomLot(lot)` starts from `lot.EntryPoint()` too (verified), so the "else" option differs only when the
  entry point is itself bad.

Household and selected Sims keep EA's code in all three. Repro `hidden-room-spawn`: `HiddenRoomCheck fallback <lot>`
computes the reset fallback (EA vs the hook's own function), calls the hooked corners search, and replays CreateSim's
elevator spot, off then on, read-only (expected on Trawler Lodge: off `-> BROKEN`, on `-> FIXED`). Repro
`hidden-room-trap` now traps in each arm at normal speed and checks ~20 sim-min later, before watchdog [49]'s window
(off: still in room; on: moved by fix after ≤ 10 sim-min plus the reset tick).

**`sim <id>`:** in phase-20261114-2022 every field did print (report.txt: lot Butterfly Esplanade, room 0, GoHome,
carrying Sebastian Striker); the phase line shows only the first line. Each line is now built on its own (a throw shows as
`(line throws ...)`), a `where:` line says in the world / rabbit hole / out of the world (posture, parent, interaction,
home, last move), a description with no created Sim gets the same, and room/hidden print even when the exit is unknown.

## 9. Next

Run `hidden-room-trap` (off then on) with the active household living in a Bridgeport apartment with a floor-0 hidden room (trap's N/A line names candidates); run `HiddenRoomCheck apt`
once in Bridgeport and record which lots show `centre=(H)` or `svcElevator NOT public`. If `centre=(H)` is common, a
second fix re-picks the `Lot.Position` fallbacks: built as `hiddenroomspawn` (§10). Run `hidden-room-spawn` and
`hidden-room-trap` (both off then on).

## 11. ErrorTrap "Destroyed Object Found" doors at Bridgeport load: not the hidden-room code (2026-11-14) [verified by reading + logs; cause open]

**What ErrorTrap does [verified, NRaas source `DereferenceGameObject.Perform` / `DereferenceManager.Perform`].** It destroys
nothing: one sim-minute after a load its scan finds script objects whose simulator proxy is gone (`HasBeenDestroyed`) but
which some field still references, clears those references (and resets a live holder object), and writes
`Destroyed Object Found: <type>`. The ScriptError XML names only the type; the holders appear only with ErrorTrap
debugging on. So four `DoorSingleColonialContemporary` objects were already destroyed and still held by something.

**When [measured].** `ScriptError_*` with 4x that door in every Bridgeport new-game load from `phase-20261114-1911`
(19:12:57, ~9 s after live mode, before any phase) through `-2125` (9 of 9); none in `-1635`, `-1737`, `-1835`
(same world file, new game each time; no save persists between runs).

**What changed between `-1835` (clean) and `-1911` (first) [measured: applied-hook lists in the leak census, settings and
ini read-backs in ts3native.log].** Not the ini: `[HitchAttr] Holders=1` / `FbApplyLog=1` were already active in `-1835`;
`[CrashForensics] Light=1` came at ~20:40. The ts3fixes package went from `ts3fixes.bp2` (built with `--on
kHitchCauseHooks`, test switches `alarmtime`, `owresettime`, `boatreplanlog`) to `ts3fixes.hr` (18:53). The only hook-set
difference: `hr` adds the `HiddenRoomNpc.HrPass` alarm and drops the 19 HitchCause / synctime-hook patches
(`Lot.UpdateDoorPrivacy`, `GlobalFunctions.CreateObjectWithOverrides`, `PlaceAtGoodLocation`,
`ObjectSpawner.TryPlaceSpawnedObjectInWorld`, `Timer.get_YieldRequired`, Overwatch reset tasks, ...). Every Bridgeport
run before 19:11 had those hooks on; every one after has them off (the release state).

**Hidden-room code ruled out [verified by reading].** With the switches off: `StartHiddenRoomNpc` only clears dictionaries
and starts an alarm whose `HrPass` returns at the switch test; `HiddenRoomSpawn` hooks run EA's original first thing; the
check code (`HiddenRoomCheck` apt / who / trap, its 1-minute watcher, `IsAllowedThrough` floods) runs only when the
harness calls it, after the base snapshot, and is read-only on doors; no ts3fixes file holds a static door reference or
calls `Destroy` on a door. The first ScriptError came before any check ran in that game.

**Open.** Either vanilla Bridgeport (release state, hooks off) leaves four destroyed doors referenced and the HitchCause
test hooks hid it (timing of the one-sim-minute scan, or a hooked path), or the ts3agent build changed at the same time.
**Logging added:** `etderefdiag` (`ErrorTrapDerefDiag.cs`, log only, default on, counters 2415-2416): a read-only hook on
ErrorTrap's `DereferenceManager.Perform` writes one `etderef:` line per destroyed object (type, id, each holder's type and
field, and the holder's lot and position). **Next:** one Bridgeport new-game load with `ts3fixes.leak1114.package` (reads
the holders), then the same load with a `--on kHitchCauseHooks` build of the same source (A/B: hooks vs not).

**Result (in game, `phase-20261114-2151` hooks off and `-2208` kHitchCauseHooks on, `etderef:` lines) [measured].** Same 12
objects in both runs, so the HitchCause hooks are not the cause, and `-2208` wrote the same ScriptError. All have
`ObjectId 0` and no live holder (no holder printed a lot / position): four `DoorSingleColonialContemporary` held only by
their own components (`DoorPortalComponent.OwnerDoor` / `mScriptObject`, `UpgradableComponent`) and `Door[]` arrays;
`ComboCityhallPoliceMilitary`, `ComboBusinessRestaurant`, `ComboBookstoreDaySpa` held by their own and their pieces'
`RabbitHoleProxy`; five `MovieSet` held by their own proxy field and `AlarmManager+Timer.ObjectRef` (alarms of a dead
MovieSet). These are object graphs that never got, or lost, a simulator object at world load: closed among themselves,
not the live buildings (Bridgeport's live combos and doors are separate objects with ids; the combo checks run on them).
ErrorTrap clears them: benign, and the same in vanilla. **Why "only since 19:11":** the ScriptError folder keeps only the
newest ~11 files (by 22:10 the 19:12-19:39 files are gone as well), so the earlier loads' files were rotated out; the
correlation with the hr packages was an artifact. No ts3fixes change; `etderefdiag` stays (holder lines now also mark
destroyed holders with their id). Open, minor: the dead `MovieSet` alarms (EA leaves its alarms when the object is
dropped at load; AlarmManager drops a timer whose object is gone).
