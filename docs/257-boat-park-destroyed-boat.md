# 257. BoatOwnable.Park NullReferenceException at the end of an NPC boat trip (`boatparkgone`)

2026-11-11, static triage of the release-verify-1111 failure, fix built, not run. Ledger docs/51 §472. Counter [937] (registry `fixes-937`).

## 1. Symptom

Job `ip-natural-combined-1111` (queue `release-verify-1111`, pair rc1111, Baseline-IP, run 2026-11-10 17:37 UTC) failed on one new script error, `ScriptError_*__11-11-2026__01-41-20__64C1DF90.xml`, about 2.5 min after load (docs/data/results/2026-11-10-queue-release-verify-1111.md):

- `System.NullReferenceException` in `BoatOwnable:Park(Lot,Sim,bool)` at mint offset `0x26` (a `callvirt`), called from `BoatOwnable:Park(Lot,Sim)` <- `BoatMultipleOccupancy:OnRouteFinished` <- `BoatScriptAdaptor:PostRoute` <- `FollowRouteAction:PerformRoute` <- `BoatFollowSubPathRouteAction:PerformActionOrig` <- `Ts3Fixes.Fixes+BoatLegHook:PerformAction` <- ... `Sim:DoRouteWithFollowers` <- `VisitLot:RunOrig` <- `Ts3Fixes.Fixes+VisitLotHook:Run`.
- Sim: Mylee Arias, NPC elder of the Arias houseboat household, interaction "Visit the Scott household" (autonomous), position on the world lot.

## 2. Not ours [verified]

- **Same error without ts3fixes.** Two probe-out runs on 2026-10-12 with no `Ts3Fixes` in the ErrorTrap "Loaded Assemblies" list (`probe-out/20261012-174026/events.jsonl` 18:01, `probe-out/20261012-193006/events.jsonl` 19:31) have the same exception, the same offset `0x26`, the same EA frames (`BoatOwnable:Park` x2 <- `BoatMultipleOccupancy:OnRouteFinished` <- ... `VisitLot:Run`), both "Visit the Scott household" on Isla Paradiso. Five more sightings with ts3fixes loaded (10-27 x2, 11-02 x3; `BoatSingleOccupancy` too; `GoToVirtualHome`, `LeaderGoHereWith`, `VisitLot`), all at `0x26`.
- **Our frames are pass-through at the throw.** `BoatLegHook.PerformActionHook` only reads whether the follower is a Sim, calls `PerformActionOrig`, and acts on the result after it returns (`LegEnd`, `RouteSources.cs`); the throw is inside `PerformActionOrig`. `VisitLotHook.RunHook` runs `ShortcutToLot` and then EA's `Run`; the trip still went by boat, so the shortcut did not move her.
- **unseenboat did not act.** The job log shows `unseenboatstart: 0 shortcuts` at 17:41:13 and 17:41:43 UTC, around the throw (17:41:19); `UbTrip` runs at plan time and returns early for anything it does not take.
- The rc1111 build changed none of this path: the hooks named above are the same as in v0.2.10.

## 3. What is null [verified in the decompile, offset match inferred]

`Park(Lot,Sim,bool)` first returns when the boat still has occupants. After that it switches the boat's lights, audio and idle off, and then makes the first call on something that is not the boat itself: `RoutingComponent.UpdateDynamicFootprint`. Every `callvirt` before that offset is on the boat itself, and frame #1 shows the boat was not null. `GameObject.RoutingComponent` is `GetComponent<RoutingComponent>()`, which returns null only when the object's component list is null. `GameObject.Dispose` sets that list to null. So the boat had been destroyed and disposed while its driver was still on the trip. The Mono trace offsets are the interpreter's, not IL: `0x26` against IL `0x2F` matches the ratio of the frame #2 pair, 0x7B for the `call` at IL 0x90 [inferred].

## 4. Who destroyed the boat [inferred, open]

NPC boats are made per trip (`GeneratedOwnableForNpc`, LotHome = the Sim's home lot). EA's own disposal of such a boat is `Park` -> `driver.Inventory.TryToAdd` -> the NPC inventory purge (`Inventory.AddInternal` destroys anything a non-household Sim gets that is not `INonPurgeableFromNPCInventory`), and that runs inside the same `Park`. Something destroyed the boat earlier. The likeliest candidate is NRaas Overwatch `CleanupVehicles` (loaded in every sighting, also on 10-12). It destroys a generated NPC boat that has a live driver whenever the driver's `LotCurrent` is not the boat's `LotCurrent`. Its `InUse` / `ActorsUsingMe` test does not cover a boat's driver. On Isla Paradiso a houseboat Sim's `LotCurrent` can be the houseboat lot (which sits at z ~2060), far from where the boat shows (docs/248). Not checked live. `fp.houseboatfix` was also loaded in every sighting. Owner: EA (the missing null check), with a third-party trigger likely.

## 5. Fix (`boatparkgone`, BoatParkGone.cs)

The hook on `BoatOwnable.Park(Lot, Sim, bool)` returns at once when the boat has no `RoutingComponent`, the exact condition EA throws on. Every other call runs EA's `Park` unchanged. A destroyed boat has nothing to park, moor or put in an inventory. After the hook returns, `OnRouteFinished` goes on (`NotifySelectVehicleInteractionOnRouteFinished`), the route ends, and the visit carries on as when the boat survives.

- Household rule: there is no skip, cap or deadline. Results change only where EA would throw, for any Sim, so no exemption is needed. EA bug fixes may apply to controlled Sims.
- Virtual target: `WindSurfboard` overrides this `Park`. `SwapMethod` keeps the flags and the slot (docs/536), so dispatch is unchanged. Swap-stack table row: yes, stub remap (`docs/data/swap-stack-audit.csv`).
- Tuning `kBoatParkGone`, default **on**: shipped ON without repro (Rodney 11-11, static review; §6). Agent switch `boatparkgone`, counter `boatparkgone_skipped` [937].
- Not done: a known-script-errors entry. `known-script-errors.ts` never matches a stack with a ts3fixes frame, by design, and here our pass-through hooks are on the stack.

## 6. Repro

Forced repro `ip-boat-gone` (repros.json), queue `tools/harness/queues/boatparkgone-1111.json`: two arms, OFF then ON, on pair rc1111 with the branch ts3fixes package pinned (`game-copy/ts3fixes.package.boatparkgone`). The ts3agent action `boat_gone_watch` (BoatGoneHandlers.cs) destroys a driving NPC's generated boat the way Overwatch does (UnParent + Destroy). It only touches a Sim of a known household that is not the active one and is not selectable or selected; a Sim with no household is skipped.

**Run 1, 2026-11-11 02:14 UTC (`when: route`, destroy after 6 samples of routing; docs/data/results/2026-11-11-queue-boatparkgone-1111.md):**
- `ip-boatgone-off-1111`, 12.3 min, row PASS: 3 boats destroyed mid-route (a BoatWaterScooter of Craig Foster, Neiman household; a BoatSailBoat of Leandro La Rosa; a BoatWaterScooter of Lora La Rosa), and `REPORT: 0 script errors matching BoatOwnable:Park`. The one new ScriptError xml was a correction-only report from load. **The OFF arm did not reproduce the EA error** [verified].
- `ip-boatgone-on-1111` did not run: the queue paused on AI usage (7d 92%).
- Reading [inferred]: when the boat is destroyed mid-route, the route ends without reaching `OnRouteFinished`'s `Park`. The natural case needs the boat destroyed after the route has ended and before `Park`. The only yield in that window is `Boat.GetOut`'s `FadeOut`, after GetOut has cleared the driver's `RoutingParent`. An Overwatch alarm that runs in that yield fits.

**v2 trigger:** `boat_gone_watch {when: "getout"}` (now the default) samples every tick. It destroys the boat once a driver that was seen with `RoutingParent` = the boat still sits in it as Driver but has no `RoutingParent`, which is the GetOut fade window.

**Run 2, 2026-11-11 02:48 UTC (`when: getout`, build e448d352, `--no-publish`):**
- `ip-boatgone-off-1111`, 11.9 min, row PASS: 3 NPC boats destroyed in the getout window, `REPORT: 0 script errors matching BoatOwnable:Park`. **The OFF arm did not reproduce** [verified].
- `ip-boatgone-on-1111`, 12 min, row FAIL only on `boatparkgone_skipped=0 (want >=1)`: 3 boats destroyed, 0 new script errors (the one new ScriptError xml has no Exception, correction-only), no crash [verified]. The fix never fired because the error never came up.
- Reading [inferred]: `Park` did not run on a destroyed boat in either arm, so the exact natural order (boat destroyed after GetOut, before `OnRouteFinished`) is still not hit by the forced trigger.

**Decision: shipped ON without repro: Rodney 11-11, static review.** The hook only returns where EA would throw (null `RoutingComponent`), and both runs show it adds no new error or crash. `kBoatParkGone` defaults to true from merge onwards; the release ts3fixes package `game-copy/ts3fixes.package.rc1111b` carries it. The queue keeps its `fixesRequire`, so a future run that does hit the case proves it.
