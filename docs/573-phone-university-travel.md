# 573: the phone around University Life travel (before, during, after)

2026-11-15. Static only. I read the EA code in `game-copy/decompiled`, which stays local. Built, proven in game 2026-11-15 (off BROKEN / on FIXED).

**What was read.** `Phone.cs` (all call interactions, the random-call system, texting, Call Sims lists), `PhoneService`, `PhoneCall`, `PhoneSmart` / `PhoneCell` load and export, `AcademicCareer.EnrollInAcademicCareer`, `TravelUtil` (moving vans to and from university), `ReturnHomeBehaviorInHomeWorld`, `CollegeGraduation`, and `Career.ShowUploadResumeTns`. Two read-only lead passes, one for the phone code and one for enrollment and return, covered these. I re-checked every fix below against EA's code.

**Checks.** Each fix has a pure check, `Ts3Fixes.Fixes.<Name>Check("run"|"result")`. It prints `controls ok=` and `members found=`. With the switch off it reports `verdict: BROKEN`, with it on `verdict: FIXED`, and anything else is `UNEXPECTED`. The repros are in `tools/harness/repros.json` (ids `<switch>-check`).

**Overlap.**
- `bsrefund` covers boarding school only.
- `unigradkeep` hooks `Annex.UniversityGraduationCeremony.Cleanup`. `resumetns` hooks `CollegeGraduation.GraduateInPlace.Run`, which runs after it, and does not change what it queues.
- `EaVacation` covers the Generations free vacation.
- No EA method below is hooked twice (swap-stack audit).

**Household outcomes.** Every fix is an EA bug fix, and no fix moves a Sim. Each switch is off by default.

**Code.** `PhoneLogic.cs` covers §1-6, `UniTravelLogic.cs` covers §7-9 and `SmartPhoneGive.cs` covers §10-11, each with a matching `*Checks.cs`.

| § | Switch | Slots | Check | Status |
|---|---|---|---|---|
| 1 | `phoneloadreset` | 2636-2637 | `PhoneLoadResetCheck` | code verified, trigger inferred |
| 2 | `phonecallschedule` | 2638-2639 | `PhoneCallScheduleCheck` | verified |
| 3 | `phonecallrearm` | 2640-2641 | `PhoneCallRearmCheck` | verified |
| 4 | `woohootywait` | 2642-2643 | `WooHootyWaitCheck` | verified |
| 5 | `invitecallreply` | 2644-2645 | `InviteCallReplyCheck` | verified |
| 6 | `gradvisitors` | 2646-2647 | `GradVisitorsCheck` | list verified, spawn inferred |
| 7 | `resumetns` | 2648-2649 | `ResumeTnsCheck` | verified |
| 8 | `unitravelflag` | 2650-2651 | `UniTravelFlagCheck` | code verified, trigger rare |
| 9 | `returnhomealarms` | 2652-2653 | `ReturnHomeAlarmsCheck` | verified, trigger rare |
| 10 | `phonefixup` | 2668-2669 | `PhoneFixUpCheck` | mechanism verified, trigger inferred |
| 11 | `phonegive` | 2670-2671 | `PhoneGiveCheck` | verified |

## §1 phoneloadreset: a phone saved during a call stays busy

**The bug.**
- `Phone.mIncomingCall` and `HandsetPickedUp` are saved, but interactions are not, so `ReplaceHandset` and `Cleanup` never run for a call that was in progress at save time.
- `Phone.OnLoad` only calls `base.OnLoad()`. `PhoneSmart.OnLoad` recomputes the handset but keeps the call.
- After load, `PlaceCall` refuses that phone (`IsTiedUp`), so every outgoing call from it fails.
- `GetCallValidity` answers LineBusy, and `PhoneService.PlaceCallInternal` aborts the whole incoming call on any LineBusy. So the household loses job calls, invitations and random calls.
- `StopAllCalls` skips calls in progress. Only picking the phone up in buy mode clears it.
- Ways in: a normal save during a call, or the home-world save when part of the household leaves for university while a Sim who stays is on the phone.

**The fix.** At world load, every phone registered with `PhoneService` is checked:
- a call that is no longer pending is dropped;
- a lifted handset is put back the way `Phone.StopCall` does it. Smartphones are skipped here because EA's OnLoad sets theirs.

Ringing calls are left to EA's persisted timeout.

## §2 phonecallschedule: no random calls on a day that starts before 7:00

**The bug.**
- `Phone.OnWorldLoaded` calls `ScheduleNextCall(0)`. Before 7:00 the else branch computes `24 - hour + 7` (the `+ 7` is the 7:00 hour).
- So a load or travel arrival at 3:00 gives 28 h, which is 7:00 the next day. The whole first day gets no random chats or invitations.

**The fix.** When the next allowed time is still before today's 7:00, the window runs from today's 7:00 to today's 22:00. This is the same window EA's own branch gives from 7:00 on. Rescheduling after a call is unchanged, because with its 12 h interval this case never arises.

## §3 phonecallrearm: random calls stop for the session

**The bug.**
- While a prom exists, the ask-to-prom branch of `FindCallParticipants` calls `sim.IsRelated(caller.CreatedSim)`. `Sim.IsRelated` dereferences `other.SimDescription`, so it throws for any teen caller who is not instantiated, which is most townie teens.
- The exception leaves `ConsiderGeneratingPhoneCall` before its last line, `ScheduleNextCall`.
- The alarm is never set again. Random chats, invitations, prom calls, bachelor-party calls and boarding-school calls stop until the next world load.

**The fix.** If the method throws, the alarm is set again exactly as its last line would: `ScheduleNextCall(kMinimumCallInterval)`. That call attempt is lost, as it is in EA's code.

## §4 woohootywait: a wait loop with no exit (loop rule)

Default ON since 2026-11-18 (docs/289): an EA hang fix; the 400-poll cap is a long hard ceiling for every Sim, household included.

**The bug.**
- `SendWooHootyText.GoToLotSuccess` runs on the invitee's GoToLot/VisitLot thread.
- It cancels the texter's queue, then runs `while (Actor.CurrentInteraction != null) Simulator.Sleep(5)` with no timeout and no check that either Sim still exists.
- The invitee stays frozen if any of these happen:
  - the texter's current interaction can't be cancelled;
  - autonomy refills the texter's queue between polls;
  - the texter travels away or is destroyed.

**The fix.** EA's same steps, except the wait ends when either Sim is gone or after 400 polls. In that case no WooHoo is pushed; EA never reached that point either. A wait that ends normally runs EA's steps unchanged.

## §5 invitecallreply: the random invitation call

**The bug.** It has two parts.
- **Copy-paste:** `GetQueryResponse` returns `RespondPositively` from both branches, so "No" plays the accept reply.
- **Order:** `OnAnswered` reads `mCaller.Household.LotHome` before its own `mCaller != null` check. A caller who left every household during the ring makes it throw inside the Answer interaction, and the answering household Sim is reset.

**The fix.**
- "No" returns `RespondNegatively`.
- The household-less case runs EA's relationship and event steps, then ends instead of throwing.

**Parked [inferred].** Accepting the "YesOut" variant (the caller has a home) queues no action.

## §6 gradvisitors: the graduation visitor list

**The bug.**
- `Phone.Call.GetAllValidCallees(Sim, float)`, whose only caller is `CallInviteOverVisitorsToGraduation` in the University world, adds every in-world Sim with enough liking. It skips CanCallSim's first checks.
- So dead Sims, babies, toddlers, pets, uncontactable Sims and Sims on a free vacation are offered. The ceremony (`Annex.StartGraduationCeremonyOneShot`) then instantiates the ones picked.
- Foreign Sims go through `CanCallForeignSim`, which lacks the dead check.

**The fix.** It drops those entries with CanCallSim's own rules. A playable ghost stays.

**Not fixed (cosmetic).** The picker's `IsVisitor(x.SimDescription as SimDescription)` gets null for foreign entries, so an already-invited foreign Sim is shown again. `Add` de-duplicates it.

## §7 resumetns: the upload-resume phone notice on return home

**The bug.**
- `ReturnHomeBehaviorInHomeWorld.TriggerDegreeUpdates` calls `Career.ShowUploadResumeTns` before it queues `CollegeGraduation.GraduateInPlace` for a degree finished without a ceremony.
- `ShowUploadResumeTns` returns at once without the `IGraduatedFromUniversity` buff, and only `GraduateInPlace` adds that buff, later.
- So these graduates never get the smartphone notice. Ceremony graduates, who already have the buff, do get it.

**The fix.** After `GraduateInPlace` grants the rewards outside the University world, it runs EA's `ShowUploadResumeTns`. Sims whose rewards were already granted are skipped, so nobody gets the notice twice.

## §8 unitravelflag: an enrollment that queues no van

**The bug.**
- `AcademicCareer.EnrollInAcademicCareer` (used by phone, computer and school) sets `TravelUtil.PlayerMadeTravelRequest` before travel is set up. Only the moving van's Run and Cleanup clear it.
- `TriggerTravelToUniversityWorld` can return or throw without queuing a van:
  - no enrolling row had a degree manager;
  - there is no lot;
  - the queue refused the van;
  - a traveller is not instantiated (throws).
- Then saving is refused, and age-up, build/buy and all travel stay blocked until the next world load.

**The fix.** After that method, a flag that was set and is still set, with no van in any traveller's queue, is cleared. If the method threw, the exception goes on.

## §9 returnhomealarms: stale return-home statics

**The bug.**
- `ReturnHomeBehaviorInHomeWorld.mRideInCarAlarm` and `mDegreeUpdatesAlarm` are plain statics, reset only when their 1-minute alarm fires.
- If the player quits within that minute, they stay set into the next world.
- The next return home in that session then skips both alarms. The travellers stay out of the world, with no arrival route, motive reset or degree update.

**The fix.** Both handles are reset at world quit. The alarms themselves end with the world.

## §10-11: the University Life smartphone itself (giving, replacing, losing)

**What I traced** [verified in the decompiled game code]:
- `Sim.GrantCellPhone` adds a phone only when the Sim has none, or when asked to destroy the current one. It is called on age-up (`AgingState.AgeTransitionCompleted` -> `GrantSpecialObjects`), on instantiation (`Sim.OnStartup`), from `AddInitialObjects`, on bin import, and from the Social Networking and Rebel skill unlocks.
- `Sim.GivePhoneOfType` is the only place a phone is created or replaced. Its callers:
  - the Phone Settings smart/future switch;
  - the FutureSim trait;
  - plumbot creation;
  - the debug switch.
- `Sim.FixUpCellPhone` is the type check:
  - toddlers, babies and pets get no phone;
  - with University Life installed, a Sim gets the smartphone;
  - with Into the Future installed, a future Sim or plumbot gets the future phone.
  It runs only from `Sim.PostWorldLoadInit`, for Sims who are in the world at load time.
- Travel and bin import go through `Inventory.ImportContent`. It keeps one phone, destroys duplicates, and skips the phone's own `ImportContent`. Skin, broken state and ringtone come back through `GameStates.StashHousehold` / `UnstashHousehold`. The list of unlocked skins is not stashed; the skill unlocks re-grant it at the next `GrantSpecialObjects`.
- Age-up keeps the phone; aging down to a toddler removes it. A Sim who isn't instantiated gets a phone when instantiated. Repair is EA's own `RepairPhone`, and travel keeps the broken state. Silencing is global (`PhoneService.PhonesSilenced`).

**What breaks without the right phone:**
- With no phone, the phone interactions on the Sim are missing, including relationship-panel calls and texting.
- With a basic cell phone and University Life installed, the Sim has none of the smartphone features: the apps (`SmartPhoneBaseModel`), Social Networking phone interactions, smartphone photos, and the Phone Junkie buff.

### §10 phonefixup (`kPhoneFixUp`, 2668-2669, `PhoneFixUpCheck`) [mechanism verified, trigger inferred]
**The bug.** The phone-type check runs only at world load. Some Sims get their objects later:
- a household placed from the bin or library that was saved before University Life;
- a Sim instantiated or aged up after the load.

These Sims run `GrantCellPhone`, which does nothing when any phone is present. An old basic cell phone therefore stays until the next load.

**The fix.** After `GrantSpecialObjects`, EA's own `FixUpCellPhone` runs for that Sim (human, child or older). It changes nothing when the phone is already the right type.

### §11 phonegive (`kPhoneGive`, 2670-2671, `PhoneGiveCheck`) [verified]
**The bug.** `GivePhoneOfType` has three problems:
- It removes only the first phone, then gives up while any phone remains. A Sim holding two phones keeps the wrong one, so the settings switch, the FutureSim trait and plumbot creation silently do nothing.
- When the inventory refuses the new phone, that phone is left out of the world and the Sim has no phone.
- The replacement starts with the default ringtone and only the default skin unlocked, until the next `GrantSpecialObjects`.

**The fix.** The same steps, with these changes:
- every old phone is removed;
- the ringtone and unlocked skins carry over to the new phone;
- a refused phone is destroyed.

The broken state is not carried over; the new phone works, as in EA's own code.

**Not fixed.**
- The toddler branch of `GrantCellPhone` destroys only the first phone. Duplicates come from mods and old saves [inferred]; two phones of the right type are left as they are, and §11 removes both only when a phone is replaced.
- Travel restores the selected skin even if the travelling phone has not unlocked it. This is cosmetic.

## Parked leads (not fixed here)

| Lead | Why parked |
|---|---|
| Tuition lost when `SitInMovingVanToTriggerTravelToUniversity.Run` is reset or throws after `ModifyFunds(-TravelCost)` (only the failed-boarding branch refunds) | [verified code, trigger inferred]. A body swap of a yielding Run; needs a design (refund in Cleanup when travel never started) |
| "Leave University" (phone or computer) drops the Sim out at once; a failed home van then leaves them in the University world with `IsDroppingOut` and greyed-out social calls until the term ends | [verified code, route failure inferred]. Needs a retry of the van, which moves the Sim; next batch |
| `ReturnHomeBehaviorInHomeWorld.SetupReturnHomeInteractions`: "Attended University" topic given to every traveller but the first, on every kind of return | cosmetic |
| `Career.ShowUploadResumeTns` notice index has 4 slots per degree but 5 grades, so an F picks the next degree's notice | reachable only through cheats or mods |
| Save and load inside the 1-minute return-home alarm: `mCarTravelers` is not persisted, so the alarm throws on load | rare; §9 does not cover it |
| `GetAllValidCalleesForParty` ignores its LTR argument and loops household × all Sims | no gameplay effect (all callers pass the same tuning value); O(H·N) only when the picker opens |

## Loop-logic pass (standing rule)

- **Real EA case, now fixed:** `SendWooHootyText.GoToLotSuccess` (§4).
- **Checked clean:**
  - the `StopCall` -> `Abort` -> `StopCall` re-entry in `PhoneService` (it ends, because Missed is set first);
  - `PhoneService` register and unregister, place call and stop-all loops (none modifies the list it walks);
  - `CallChat.DoChat` (has proper exit reasons);
  - `ScatterEarlyDepartureSim` (capped at 10 tries);
  - the ring timeout alarm (checks `IsPending`).
- **Our own code:**
  - the new world-load walk copies `PhoneService.mPhones` before calling object code;
  - the visitor filter removes entries backwards;
  - no reflection in a hot path (all FieldInfo, MethodInfo and delegates are cached; the delegates bind once).
- **Not applicable:** kids' carpool and school phone calls while parents are at university. The home world is frozen during the trip.
- **Checked, no bug:**
  - cell-phone export and import carry only the ringtone and the silenced flag, so no call crosses travel;
  - `PhoneSmart.OnLoad` handles its handset;
  - `CanCallSim` handles future-world, free-vacation and other-world Sims;
  - the Mailbox `PreReturnHome` listener is registered again on load;
  - `ClearTravelStatics` resets `PlayerMadeTravelRequest` at world load.

## Tests (combined run, off then on)

All checks are pure and work in any world. In-game end-to-end, where possible:
- **§1:** save during a phone chat, reload, call someone from that phone.
- **§2:** load a save at 3:00 and watch for a random call that day (counter 2638).
- **§7:** let a degree finish without attending the ceremony, return home, and look for the notice.
