# 508. Events batch 1: photo booth sessions, egg hunt eggs, graduation waits (`photosession`, `eggspot`, `graduationtable`)

Contributor doc (range 500-599). Three ts3fixes switches (off by default), counters 2026-2030, ledger §906. They port three
fixes of a player's core mod (Builds 16 + 97, 73 and 18) to whole-method wraps, so EA's code runs as it is. Parts of those
builds that need a change inside an EA method are not ported (§4, candidates for the docs/506 IL patcher). Status: static,
self-test and repros written; game run in §5.

## 1. Photo booth sessions (`photosession`, `mods/ts3fixes/src/PhotoSession.cs`)

**The bug.** A player reported that at the Seasons festival photo booth "only the sims inside after a certain amount of time
take the pic, but when they leave it is left unavailable". How EA's code gets there **[verified, IL]**:

- A greeting-card photo session (`GreetingCardTakePhotoHelper`) belongs to the booth, or to City Hall and the other
  greeting-card rabbit holes, through their `mPhotoHelper` field. The booth reads as in use while that field is set.
- `GetPhotoTakenWith.Run` puts every Sim the player picked in the session's `mSimsExpected`, also a Sim whose `TakePhoto`
  interaction could not be queued.
- Only the `SimDoneWithPhoto` call that empties that list ends the session (`Reset`; the caller then clears `mPhotoHelper`).
  `SimDoneWithPhoto` removes the calling Sim only while the session waits for Sims or is done, not while the others are
  ready for the photo.
- So a Sim that never came (never queued, reset, deleted), or one that left while the others were ready, stays listed. The
  Sims inside never see "inside == expected", give up after the wait loop's timeout and leave, and the session stays.

The session is saved with its owner (the field has no `Persistable(false)`), so the booth stays unavailable after a reload too
**[inferred]**.

**Prior fixes checked:** none of the installed mods (NRaas included) touches the photo booth; a web search found only player
reports on the EA forums.

**The fix.** Hooks on `GreetingCardTakePhotoHelper.SimDoneWithPhoto` and `SimMadeItInside`, plus a sweep at world load:

- `SimDoneWithPhoto`: after EA's call, if the session did not end and is not taking the photo, the calling Sim is dropped, and
  so is every expected Sim with no interaction (running or queued) on the owner. A toddler or baby stays while an older Sim of
  the session stays, since a carried baby has no interaction of its own. If the list is empty then, EA's own `Reset` ends the
  session and the call returns true, as EA's does when its own removal empties the list.
- `SimMadeItInside`: the same drop before EA's call, never down to an empty list, so EA's "inside == expected" can come true
  for the Sims that are there.
- World load: a session on a booth or greeting-card rabbit hole none of whose expected Sims has an interaction on it is ended.
- Counters: 2026 expected Sims dropped, 2027 sessions ended by the hook, 2028 sessions ended at world load.
- The drop rule is in `PhotoSessionCore.Drop` (no game types), so the self-test runs it offline.

## 2. Egg hunt eggs off the hunt lot (`eggspot`, `mods/ts3fixes/src/EggSpot.cs`)

**The bug [verified, IL].** `EggHuntController.PlaceEgg` picks random start points within half the lot's size around
`EggHuntLot.Position`, then lets `GlobalFunctions.PlaceAtGoodLocation` move the egg, with `IsValidEggPosition` as its check.
That check asks only for an outdoor, routable point, never for a point on `EggHuntLot`. So some eggs land on the street or a
neighbour lot. Such an egg stays in the controller's `mPlacedEggs`, but its lot has no `CurrEggHuntController`, so picking it
up throws in `EasterEgg.PickUpEgg.Run`, and the hunt never sees its last egg go **[inferred from IL: no live hunt watched]**.

**Prior fixes checked:** ErrorTrap has `DerefEggHuntController` (it clears dead references, not egg placement); the wexxam
SpringGrowth mod adds egg hunts and keeps EA's placement.

**The fix.** A hook on `EggHuntController.IsValidEggPosition(float, float, float, float)`: after EA's check says yes, a point
whose `LotManager.GetLotAtPoint` is not `EggHuntLot` is refused (counter 2029). `PlaceEgg` keeps its tries, and the
good-location search keeps looking near the start point. A controller with no `EggHuntLot` gets EA's answer.

## 3. Graduation waits with no graduation (`graduationtable`, `mods/ts3fixes/src/GraduationTable.cs`)

**The bug [verified, IL].** The waiting loops of `School.GraduateInCityHall` and `School.AttendGraduation` (`WaitingLoopDel`)
read the private static `School.sGraduatingSims` without a null check. The table is null until a graduation is scheduled and
again after the ceremony's clean-up. A Sim still in the waiting stage then throws `NullReferenceException`, and ErrorTrap ends
the interaction (seen by the player: an attendee whose graduate was dropped) **[community-reported]**. EA's own
`WatchingLoopDel` of the same classes checks the table.

**The fix.** Hooks on both `WaitingLoopDel` methods. With the switch on and a null table, nothing can be on its way, so the
loop takes EA's own "nothing to wait for" exit: the actor gets `ExitReason.StageComplete` and EA's body does not run (counter
2030). With a table, EA's body runs as it is.

## 4. Not ported (mid-method, docs/506)

- Build 16's `>=` checks in the photo session (in place of `==`) and its bounded wait for the camera.
- Build 72's catch in `EasterEgg.PickUpEgg.Run` for an egg already off the hunt.
- Build 18's check of the `ActiveStage` cast in `AttendGraduation`.

## 5. Checks

- **Self-test** (`node tools/selftest/photosession/run.ts`) runs `PhotoSessionCore.Drop`: phantoms go, babies and toddlers stay
  with an active older Sim, nothing goes when the list must keep one, an all-active list keeps everyone.
- **Repros** (`tools/harness/repros.json`), one queue `events-batch-1.json`, job `bp-events-batch-1-check` (5 min each, one load,
  Baseline-BP). Each check throws on a wrong outcome:
  - `photo-session-check` (`Ts3Fixes.Fixes.PhotoSessionCheck`): on the first greeting-card rabbit hole with no session, with two
    Sims that have no interaction on it. Three cases: waiting for Sims, ready for the photo, a stale session at world load.
    Off: EA keeps the second Sim listed in cases 1 and 2 and keeps the stale session. On: cases 1 and 2 end with the list
    cleared, case 3 is ended, and the counters go up by 3, 2 and 1.
  - `egg-spot-check` (`EggSpotCheck`): an egg hunt controller object (not constructed) gets a hunt lot, and EA's check runs on an
    outdoor Sim's position, first with the Sim's own lot, then with another lot. Off: the point off the hunt lot is accepted.
    On: it is refused, the on-lot point is still accepted, counter 2029 +1.
  - `graduation-table-check` (`GraduationTableCheck`): needs no graduation (it throws if a table exists). Both interaction
    types get a Sim as actor and their `WaitingLoopDel` is called. Off: both throw `NullReferenceException`. On: both return,
    the actor has `StageComplete` (removed again after), counter 2030 +2.
- **Needs EA's code:** the player's core mod has Builds 16/97, 18 and 73, so the off arms would fail with it. The run uses
  simler90's original core.
- **Not covered by the repros:** a real festival, egg hunt or graduation. The checks call EA's methods directly.

## 6. Results

2026-11-01, Baseline-BP, simler90's original UPDATE242 core, the player release v0.2.4 asi, 5 min each [verified]. Rows:
`docs/data/results/2026-11-01-queue-events-batch-1.md` and `...-events-batch-1-rest.md`.

| Repro | Off (switch false) | On (switch true) | Result |
|---|---|---|---|
| `photo-session-check` (ScienceLab, two Sims) | `SimDoneWithPhoto` returns false: case 1 leaves 1 Sim listed, case 2 leaves 2; the stale session is kept | both return true with the list cleared; the stale session is ended; counters 2026/2027/2028 +3/+2/+1 | PASS |
| `egg-spot-check` | the point off the hunt lot is accepted (True), the on-lot point too | off-lot point refused (False), on-lot point accepted, counter 2029 +1 | PASS |
| `graduation-table-check` | both `WaitingLoopDel` calls throw `NullReferenceException` | both return, the actor has `StageComplete`, counter 2030 +2 | PASS |

The runs before the passes failed for test reasons, not the fixes:
- The queue's early gate fired after the check reloaded the save. Each repro now names its own counter
  (`fixesRequire`).
- The egg and graduation checks called the swapped methods with `MethodInfo.Invoke`, which throws "Unable to invoke an
  invalid target" for a swapped method. They now use a delegate, as the other ts3fixes checks do.
- The graduation check's test interaction had `Actor` but not `InstanceActor` (which `Init` sets and the fix reads). The
  check now sets both.

Written with Claude Code (AI help).
