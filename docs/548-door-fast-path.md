# 548. `[DoorFast]`: native answers for the route worker's door asks (2026-11-12)

Built, selftests pass; run in game in shadow and answer mode with 0 mismatches, gain not proven (§8). Default off
(`[DoorFast] Mode=0`), next version. Counters v916 `df*` at 0x62AC, v919 coverage at 0x630C, v920 rules c-g at 0x6384, after
main's v194 with 0x62A8 held for v917 (docs/registry.md; version field stays 912). Source: `native/ts3native/doorfast.cpp`, the door channel in `snap.cpp`, the door stub in
`hostcalls.cpp`; managed side `mods/ts3fixes/src/DoorMirror.cs` + `DoorMirrorCore.cs` (switch `doormirror`). Tests:
`native/ts3native/doorfast-test.h`, the "door channel" part of `selftest.cpp`, `tools/selftest/doormirror`.

Tags: [verified] = read in code or in the decompiled C#; [measured] = a run's numbers; [inferred] = reasoning, needs a run.

## 1. The gap

The async route worker asks script for portal locks at `0x64F4D3` (`call World_FirePortalLockedQuery 0x791C00`, 9 cdecl
dwords: lot id, router object id, portal owner id, two room words, initialLocked low byte) [verified, docs/159 §4]. Each ask is a
proxy round trip to the sim thread (`Lot.OnPortalPathPlan`). A managed shadow predictor (issue #383) answered 77-89% of the asks
with 0 mismatches in 312,754 [measured]. `[DoorFast]` makes that prediction natively, from data ts3fixes pushes before the job
starts, so no game memory and no world query is read per ask.

## 2. Rules

Both rules need: the lot's door table valid in the current epoch, an actor record younger than `ActorMs`, only EA's
`Sim.OnPortalPathPlan` on the lot's `PortalPathPlan` event (lot flag 1), the actor not a tomb mummy and without a Proprietor role.

| Rule | Owner | Further conditions | Answer |
|---|---|---|---|
| a | a CommonDoor of the lot's door records | type does not override `IsAllowedThrough(int, int, Sim)` (kind bit 64), not a barn door (bit 2, EA asks `IsFireOnLot` for those) or single-gender door (bit 32); lock Anybody (3); `mOneWayDestinationRoomId` 36863 (0 is a valid one-way room); initialLocked 0; actor human (not pet, horse or stray); no hidden room on the lot (lot flag 2) | 2 unlocked |
| b | one of the lot's rule-b ids | none | initialLocked ? 1 locked : 2 unlocked |

Rule-b ids: the lot's objects of `Stairs`, `StaircaseSpiral`, `Pool`, `PoolLadder`, `BuildableFountain`, `ElevatorInterior`,
`Platform`, `VelvetRopes`, `Subway` and subclasses (one `Lot.GetObjects<T>()` per type at push time), minus any CommonDoor or
type (or base) with its own rule in `Sim.OnPortalPathPlan`: FirefighterPole, BoxStall, HorseJump, ElevatorDoors, DivingBoard,
Ladder [verified, docs/105 §2]. Anything else gets no prediction and is asked as today.

No-prediction reasons, in this order, counted in `dfNo[0..8]`: lot, actor, handlers, role (mummy / proprietor), owner (no record
and no rule-b id), door (overridden / barn / single gender / lock / one-way), initialLocked (door), animal (door), hidden room
(door). The dispatch is one function per portal family (`DfDoorRules`, `DfPortalRules`): a new rule (elevator doors, gender
doors, ladder, diving board, once the managed shadow proves them) is a new branch there.

## 3. Modes

- `Mode=1` shadow: predict, ask anyway, compare the exact class (`dfMatch`, `dfMisA` .. `dfMisG`, first `LogMismatch` lines
  `door fast mismatch: lot .. owner .. rule .. kind .. initialLocked .. predicted .. real ..`).
- `Mode=2`: as 1, and while `dfAnswer` (+0x62B0, `--ab doorfast`, starts 1) is 1 a prediction is returned without the round
  trip (`dfAnswered`), except: a household-scope actor (`dfHouse`; ts3fixes' `HhScope`, unknown = household), a door predicted
  locked or discouraged (`dfDoorLocked`: `Sim.OnPortalPathPlan` adds every locked door to `LockedDoorsDuringPlan`, which the
  route-fail "door locked" reaction reads), every 16th answerable ask (`dfSelf`, asked and compared), and after any mismatch in
  Mode 2 (`dfFail` = 1, a `FAIL door fast` line: no answers for the rest of the session). The compare runs in both arms of
  `--ab doorfast`.
- Same pass-through as DoorMemo: only the worker inside a job, not a plan nested in a managed call, not a clone run. DoorFast runs
  before DoorMemo / DoorMemoX in the stub; asks they answer are not compared.
- Start needs the stub (`ApplyHostCalls` installs it for `Mode` > 0), `[RouteSnap] Enabled=1` and its World_FireEvent hook and IC
  slots (`snReady & 6`), and ts3fixes `doormirror` on (the pushes). Without them: a `SKIP door fast` line, `dfMode` 0.

Side effects of a skipped ask: the stub returns the same value at the same call site, so the caller's code after the call runs;
managed, only the handler's `PortalPlanEventArgs` allocation is skipped (no door is answered locked natively) [verified,
docs/159 §4.1].

## 4. Data and invalidation

Door channel (GetOption2, `optHi = 0x84000000 | code << 16`) codes 23-28 [built]: 23/24 rule-b id lo/hi and 25 lot flags, inside
an open lot after its doors (each ends the door being described); 26/27 actor object id lo/hi and 28 actor flags (1 human,
2 not tomb mummy, 4 no Proprietor, 8 household scope), outside any lot, sent at each route START (`DoorMirror.SyncRoute`, the
follower Sim). An older ts3native refuses code 23 or 25; ts3fixes then pushes the doors alone and stops sending extras
(`DoorChannel.NoExtras`). Every channel write takes `g_spDoorLock` exclusive; the worker's predictor takes it shared (the tables
reallocate).

The lot tables become invalid (no prediction until ts3fixes pushes again at the next route START) on:
- the door lock event IC (slot 24, `World_DoorLockStateChangedImpl`) and build/buy exit (slot 22); slot 24 was not handled
  before this change although docs/105 and DoorMirrorCore.cs counted on it;
- world events 5, 7, 9, 0x0A-0x10, now at the event (before: only at the next snapshot cut, which is skipped while a job runs);
- ts3fixes: `CommonDoor.ImportContent` (postfix), and prefixes on `Lot.add_PortalPathPlan` / `remove_PortalPathPlan` that
  invalidate the lot before a PrivacySituation or mod handler comes or goes (lot flag 1 is set only while both prefixes are in);
- invalidate-all cleared the actor table too until v919; since v919 only a world event or epoch does (actor flags do not depend
  on lot or door state; `dfInvAll[3]` counts the sources). Hidden rooms change only at world load and lot rebuild
  (`Lot.UpdateRoomMarkers`, world-builder markers) [verified, call graph].

## 5. Open

- Written before the first run: that the ask's router id is the follower Sim's `ObjectId` (else `dfNo[1]` takes every ask),
  that the ask's lot id equals the managed `LotId` the table is keyed by (else `dfNo[0]`), and the share of asks on the start and
  goal lots (transit lots are not pushed, `dfNo[0]`). df1 answered the first two: 77-78% of IP / SV asks and 51% of BP asks
  matched a prediction, with `dfNo[1]` 1-3% of the asks [measured, §8]. §6 covers the rest.
- Open after the runs: the gain (§8: the hitch ratio's interval crosses 1); BP door-heavy plans have no repro of their own.

## 6. Coverage (v919, 2026-11-13)

Shadow run df1 (asi-dfp-1112, Mode=1): 0 mismatches on IP / BP / SV; matches 114,549 / 57,022 / 84,518 of 148,582 / 112,754 /
108,937 asks. No-prediction asks (`dfNo`), IP / BP / SV: lot 20,881 / 5,445 / 19,659; actor 4,323 / 1,181 / 2,565; handlers
54 / 163 / 136; owner 143 / 8,919 / 0; door 5,522 / 10,066 / 0; initialLocked 0 / 0 / 28; animal 3,110 / 3,289 / 2,031; hidden
0 / 26,669 / 0. v919 makes the table cover the lot, actor, owner and hidden asks; it adds no answer rule (rules a and b only).

| `dfNo` bucket | why it missed [inferred from code, unless marked] | v919 change [built] | counters |
|---|---|---|---|
| lot | a START pushes only its start lot and approximate goal lot; a route crossing other lots (transit lots, the sidewalk and street lots between) asks about them with no table; also a table invalidated (lock event, build/buy exit, world event) and not pushed again yet, or of an older epoch | native notes each such lot id in a 16-entry ring (`SpMissNote`); ts3fixes reads up to 4 per START over the channel (code 30 stages one, code 31 answers one bit of its id, 64 calls) and pushes them like the start lot; a lot is handed out at most every 5 s, dropped once valid or after 60 s without an ask | `dfLotNo[4]` (lot id 0, never pushed, invalid, old epoch), `dfMissRec`, `dfMissHand` |
| actor | no record: the follower is no Sim (no record was pushed; EA's `PortalLockedCallback` then passes a null Actor), the 64-slot FIFO evicted it in a crowd, every invalidate-all wiped the actors, or the plan's START never went through `Route.Plan` / Replan; too old: the job ran more than `ActorMs` (10 s) after its START | a follower that is no Sim gets a record (flag 16, with household scope); 256 slots, least recently pushed evicted; only a world event or epoch drops the actors | `dfActNo[2]` (no record, too old), `dfActEvict`, `dfInvAll[3]`, `dfNo2[0]` |
| owner | portal owners neither a door record nor a rule-b id. BP 8,919: most likely `ElevatorDoors` (apartment and high-rise elevators; `ElevatorInterior` is rule b), maybe `FirefighterPole`; single-gender doors are `CommonDoor`s, so they land in "door" (kind bit 32), not here. IP 143: `Ladder`, `DivingBoard`, `WaterSlide`, `Houseboat` / `HouseboatJig` | typed records (codes 32-34: a sticky kind, then lo / hi ids) for `ElevatorDoors` 0, `FirefighterPole` 1, `IBoxStall` 2, `IHorseJump` 3, `DivingBoard` 4, `Ladder` 5 (each with its own step in `Sim.OnPortalPathPlan`) and `TubeElevator` 6, `WaterSlide` 7, `Houseboat` 8, `HouseboatJig` 9 (none); an ask on one is "no rule yet", counted per type | `dfNo2[1]`, `dfType[10]` |
| hidden | the lot bit (any hidden room on the lot) blocked every door of the lot; EA tests only the asked pair (`CommonDoor.IsAllowedThrough`: `IsRoomHidden(from) \|\| IsRoomHidden(to)`) | the lot's hidden room ids go over the channel (code 35); a door is not predicted only when a room of the asked pair is hidden (the short room id widened to int, as `LotManager.PortalLockedCallback` passes it); lot bit 2 now means the list is unreadable | `dfHidN[2]` (pair hidden, list unknown) |
| handlers, door, initialLocked, animal | rules outside a / b (another handler; locked, one-way, overridden, barn or single-gender door; initialLocked on a door; a pet, horse or stray) | none | `dfNo` as before |

Shadow guesses, never answered: where EA's answer is the initial class (a null Actor: `Sim.OnPortalPathPlan` returns at once,
`Sim.cs` 52022; a typed owner of kinds 6-9: no step of its own), native compares "initialLocked ? locked : unlocked" with the
live answer (`dfGuessOk[2]` / `dfGuessBad[2]`: [0] router no Sim, [1] typed kinds 6-9; the first `LogMismatch` differences are
logged). They are the data for a later rule c; a guess mismatch never sets `dfFail`.

Channel codes 29-35: 29 CAPS (answers 1; an older ts3native refuses it, once a session, and ts3fixes then pushes as before:
hidden rooms as the lot bit, no typed ids, no missed lots, no non-Sim actors); 30 MISS_NEXT; 31 MISS_BIT; 32 T_KIND; 33 / 34 T
lo / hi; 35 HIDDEN. Typed and hidden messages come inside an open lot after its rule-b ids, before the lot flags. Checks: the
native selftest (`door fast (v916 + v919)` and the `door channel` vectors) and `tools/selftest/doormirror` (the same vectors,
coverage round trips, a v916 ts3native).

Next run (Mode=1 shadow) answers: how many `dfNo[0]` asks the missed-lot push removes (`dfLotNo[1]` vs `dfMissHand`), whether
`dfNo[1]` is mostly not-Sim routers (`dfNo2[0]`) or age (`dfActNo[1]`), which types BP's owner asks are (`dfType`), and how many
BP door asks the per-pair hidden test frees (`dfHidN[0]` vs the 26,669).

## 7. Rules c-g (v920, 2026-11-13)

The managed shadow (dp12 / dp13, rx1, 4 games IP + BP) found rules a-f exact: 1,148,924 predicted asks, 0 mismatches (BP
elevator rule c 32,865, gender rule d 21,732 on IP + BP); null-actor asks 213,398 (IP 178,761 in one game), every one with one
handler equal to initialLocked. v920 makes them native rules, counted, compared and answered like a / b: `dfPredC`-`dfPredG`
(+0x6384) and `dfMisC`-`dfMisG` (+0x6398), the same Mode 2 self-check (1 in 16) and `dfFail` latch, household scope always asked.

| rule | owner | needs | answer | EA source |
|---|---|---|---|---|
| c | `ElevatorDoors`, exact type (typed kind 0) | human actor, not ToddlerOrBelow, the lot has no virtual residential slots | initialLocked ? locked : unlocked | `ElevatorDoors.IsAllowedThrough` (non-virtual): restricted for ToddlerOrBelow, horse, wild animal, or `LotCurrent.HasVirtualResidentialSlots && !CanTravelInElevator` |
| d | door whose `IsAllowedThrough(int, int, Sim)` is declared on `DoorSingleGender` | human actor, its `Gender` equal to the door's `mGender`, initialLocked 0, the lot not `kEP10_Resort` | unlocked | `DoorSingleGender.IsAllowedThrough` does not call `CommonDoor`'s: lock, one-way and hidden rooms do not count; a human of the door's gender gets None on every branch |
| e | `Ladder`, exact type (kind 5) | any species | initialLocked ? locked : avoids ladders ? discouraged : unlocked | `Ladder.DiscourageSimFromUsing`: `Elder` or a trait in `kTraitsThatAvoidLadders` (CouchPotato, Coward); EA's class is locked before discouraged |
| f | `DivingBoard`, exact type (kind 4) | any species | initialLocked, ToddlerOrBelow or IsVisuallyPregnant ? locked : unlocked | `Sim.OnPortalPathPlan`: `if (!Locked && is DivingBoard) Locked = ToddlerOrBelow \|\| IsVisuallyPregnant` |
| g | any portal | the router is no Sim (actor flag 16) or router id 0 | initialLocked ? locked : unlocked | `Sim.OnPortalPathPlan` does nothing when `args.Actor == null` |

c-f also need the one handler, no tomb mummy and no Proprietor role (the order: handlers, router no Sim, role, then the owner's
family). g needs only the one handler. Doors: a door predicted locked is still asked (LockedDoorsDuringPlan), except under rule g:
with a null Actor nothing goes into that list, so a rule g locked door may be answered. Rule d predicts unlocked only.

What ts3fixes pushes for them (`DoorMirror.cs`, `DoorMirrorCore.cs`; no new channel code):

- actor flags 32 ToddlerOrBelow, 64 IsVisuallyPregnant, 128 avoids ladders (`Elder`, or `TraitManager.HasAnyElement` of the
  reflected `Ladder.kTraitsThatAvoidLadders`; a null `TraitManager` throws inside EA's handler after the class is set, so it
  counts as not discouraged; an unreadable field makes the actor unclassified), 256 Male, 512 Female (`SimDescription.Gender`).
- a router that is no Sim: household scope unless the running script task (`Simulator.CurrentTask`) is a Sim outside it (the
  driver's interaction plans its vehicle's route); no task Sim or an error: household. Router id 0 counts as household natively.
- door kind bits 128 `IsAllowedThrough` declared on `DoorSingleGender`, 256 / 512 its reflected `mGender` Male / Female (no bit:
  no prediction).
- lot flags 4 `HasVirtualResidentialSlots`, 8 `CommercialLotSubType == kEP10_Resort`. A lot type change is not known to
  invalidate the table: a stale apartment bit only stops rule c, a missing one would need a lot with saved virtual slots made
  residential (the self-check then latches `dfFail`); the resort bit cannot change rule d's answer (EA's resort branch also
  returns None).
- typed kinds 0, 4 and 5 for the exact EA type only (a subclass may override what the rule mirrors; it gets no record of that
  kind).

Rule g replaces the v919 router-no-Sim guess: `dfNo2[0]`, `dfGuessOk[0]` and `dfGuessBad[0]` stay 0 from v920 (kinds 6-9 keep
`dfGuessOk[1]` / `dfGuessBad[1]`). A rule c failure (pet, toddler, apartment) still counts in `dfType[0]`. Checks: the native
selftest (`door fast (v916 + v919 + v920)`, 38 predictor cases plus counting, compare, Mode 2 answers and the latch) and
`tools/selftest/doormirror`.

## 8. Results (2026-11-13)

Correctness is proven: no run had a mismatch, in shadow or in answer mode. A gain is not proven: the hitch ratio is below 1
in both answer runs, but both intervals include 1. Default stays off.

| run | build, mode | games | result [measured] |
|---|---|---|---|
| df1 | asi-dfp-1112, Mode=1 shadow, rules a / b | IP, BP, SV | 0 mismatches; asks 148,582 / 112,754 / 108,937, of them matched a prediction 114,549 / 57,022 / 84,518 (77% / 51% / 78%) |
| df2 | Mode=2, `--ab doorfast`, rules a / b | 4 games | 0 mismatches, `dfFail` 0; about 195k asks answered, about 13k self-check asks all equal; IP hitches >= 300 ms 315 -> 256 per 10 min, ratio 0.81 [0.63, 1.04]; BP no such hitch in either arm |
| ds3 | asi-ff3-1113, Mode=1 shadow, rules a-g | IP, BP, SV | every `dfMisA` .. `dfMisG` 0, `dfFail` 0; rule c 5,864 predictions on BP, rule d 1,071-3,626 per game, rule e 169, rule g 14,594 on IP |
| df3 | asi-ff3-1113, Mode=2, `--ab doorfast` | IP, 2 seeds, both orders | 0 mismatches; 461k asks answered, about 30.7k self-check asks all equal; hitches >= 300 ms 103 -> 84 per 10 min, ratio 0.82 [0.55, 1.23] |

What this shows and what it does not:

- The predictor and the household rule hold [measured]: about 44k self-check asks in Mode 2 (each asked anyway and compared)
  and every shadow compare gave the live answer. Household-scope asks were always sent (`dfHouse`).
- The answered asks skip a round trip to the sim thread each [verified, §1]. That this lowers the >= 300 ms hitches is
  [inferred]: the two answer runs agree (0.81, 0.82), but neither interval excludes 1, and natural-play A/Bs drift.
- Not measured: the time saved per answered ask, and BP, which had no >= 300 ms hitch to compare.
- Next, if a gain is wanted: a door-heavy repro that triggers more asks per minute, then an in-load `--ab doorfast` pair in
  both orders.
