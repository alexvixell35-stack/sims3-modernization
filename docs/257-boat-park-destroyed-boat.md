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

## 7. Second boat error: `Boat.GetOutIntoWater` NRE at `0x1d`, stale posture (2026-11-12, static, design only, ledger §481)

**Symptom** (HANDOFF-2026-11-11, `ip-natural-combined-1111`; sub-agent triage): `NullReferenceException` in `Boat:GetOutIntoWater+0x1d` <- `Boat.GetOut` <- `Boat+GetOutOfBoat.Run`. The Sim's `Posture` is `StandingPosture`, so `a.Posture as SittingInBoat` is null and `.SeatSlot` throws. There are 25-32 sightings since 10-18, all on NPCs, all with ts3fixes loaded. Some predate BoatLegHook (10-22) and unseenboat (11-06). No vanilla run exists, so an EA cause is not proven [verified per that triage].

**Mechanism [verified decompile, writer inferred]:** `Boat.GetOut(Sim, Vector3, bool, bool, Houseboat, Vector3, bool, bool)` first checks `a.Posture is SittingInBoat` with `Container == this`. It then clears `RoutingParent` and calls `a.FadeOut(!bOnReset)`, which yields on the Sim's own task. After that it calls `GetOutIntoWater`, which reads `a.Posture` again with no check. So the posture was replaced by another task during the fade. One EA path does exactly this without first killing the Sim's task: a reset of the boat (or of the Sim) runs `SittingInBoat.OnReset` -> `vehicle.GetOut(mSim, bOnReset: true)` (no fade, so no yield) -> `PopPosture` -> Standing, and then only *schedules* `mSim.SetObjectToReset()`. The own task wakes from the fade before that reset lands, and throws. Who resets a boat or Sim in the window is unknown. Candidates: NRaas Overwatch (Stuck Check resets, CleanupVehicles), EA resets, and our code below. The 11-11 forced repro destroyed 3 boats in the same fade window with 0 script errors (§6 run 2). That is weak evidence (n = 3, get-out side not logged) against a plain `Destroy` being the writer.

**Our code that could replace a boat Sim's posture, ranked (static, 2026-11-12):**

| # | Path | Writes posture? | In the window? | Verdict |
|---|---|---|---|---|
| 1 | `FewerPlans.cs` 3 AM stuck check (EA's check restated; with `stuckcheckasync` it runs one Sim per tick in our task): `sim.SetObjectToReset()` after 3 attempts | through the Sim reset -> `SittingInBoat.OnReset` | possible: its skips (household lot, moved, rabbit hole) do not exclude a Sim in a boat | the only candidate; sighting times against 03:00 sim time will tell |
| 2 | `RouteFixes.cs` stuck watchdog: `SetObjectToReset` / `SetToResetAndSendHome` / `AttemptToPutInSafeLocation` | through the reset | no: queued and re-checked only while `Posture == Standing` (lines 270, 290), so a Sim in a boat is never queued | excluded [verified code] |
| 3 | `VehicleLeaks.cs` `VlRelease` (`Destroy`) and the `vehgetinleak` check reset | through destroy / reset | off by default (`kVeh*`); built 11-10, after the first sightings | excluded for the sightings |
| 4 | `PosturePop.cs` hook on `Sim.set_Posture` | pass-through with `posturepopcycle` off (default) | added 10-29, after the first sightings | excluded |
| 5 | Teleports: `PlaceSim` (unseenboat route goal / houseboat; unseenfb pre, leave, arrive, snap-back; finish the walk) | no: `SetPosition` + `SetForward` only (`Fixes.cs` `PlaceSim`) | `TownieUnseen` / `UfPreSim` need `Standing` | excluded as a posture writer |
| 6 | `boatparkgone`, `BoatLegHook`, `VisitLotHook` | no (Park returns early; the rest are pass-through) | after GetOut / around the route | excluded |
| 7 | `OutfitWait.cs` reset | through the reset | dead Sims in a makeover only | excluded |

**Diag design (`boatgetoutdiag`, not built).** Tuning `kBoatGetOutDiag = false`. It only logs and changes no Sim's behaviour, so it needs no household exemption; household Sims are logged with a flag.
- **Watch:** a new hook on that 8-argument `Boat.GetOut`. Every overload ends there, and it is non-virtual; use the same `Patch` pattern as `BoatParkGoneHook`. With the switch on, the hook pushes `{sim id, boat, Simulator.CurrentTask, entry posture, sim-minute, depth}` into a static array of 8 (when full, count the overflow and do not watch). It then runs the original inside `try { } catch (NullReferenceException) { log; throw; } finally { pop }`. `throw;` keeps the game's error and its ScriptError.
- **Writer:** the existing `PosturePopHook.SetHook` (always installed) gets one new first line: `if (sBgoN != 0) BgoNote(s, cur, value);`. With the switch off, that costs one static compare per posture set. `BgoNote` acts only when all of these hold: `s` is watched, `cur` is the watched `SittingInBoat` of that boat, and the set is not the own task's normal end. The normal end is the own task at depth 1 after the fade; the watch record sets a flag when the fade returns. Once per watch it stores the new posture type; own task, foreign task or nested GetOut (depth > 1); the current task's object type (`Simulator.GetProxy(CurrentTask)`); `PlayTrace.Callers()` widened to 12 frames, with every `Ts3Fixes` frame named as ours; and `MovedSims()[name]` (our last move of this Sim).
- **Lines** (ts3native.log via `AppliedLog`, at most 20 per session):
  - `boatgetout replaced: <Sim> 0x<id> npc|household SittingInBoat-><new> during GetOut of <boat type> 0x<id> (+<sim-min>, water <bool>, houseboat <bool>) by <own|foreign|nested> task <type>; ours: <frames|none>; stack <...>`
  - when the NRE follows: `boatgetout stale: <Sim> NRE in GetOut after <replaced line id>`
- **Counters:** reserve 3 `fixes` slots at build: watched GetOuts, replacements in the window, stale throws.
- **Gate / run:** there is no forced repro; the error is natural, about one per few IP hours. Turn the switch on in natural IP correctness jobs (`ip-natural-combined`, release-verify) and read every `boatgetout stale:` line:
  - A `Ts3Fixes` frame in the writer stack names our path.
  - A foreign task of NRaas type, or a nested `GetOut(bOnReset)` under a `ResetSim`, names the other side.
  - Then decide the fix. The planned NPC-only guard (`GetOutOfBoat.Run` / `GetOut`: posture not `SittingInBoat` -> return) stays as the fallback.
- **Cost:** about 30-45 min to build (ts3fixes only, plus one selftest for the watch push/pop and the "own end" rule). Use this line, not a bisect, to settle row 1 of the table above.

## 8. `boatgetoutdiag` built (2026-11-12, NOT RUN; design §7 on `analysis/route-misc-1112`)

**03:00 check first [measured, `probe-out/2026*/events.jsonl` script errors, ErrorTrap `Sim-Time`]:** there are 32 unique `GetOutIntoWater` sightings since 10-18 (31 IP, 1 Bridgeport), all NPCs. None falls within 02:30-03:30 sim time. The nearest are 03:38 and 03:43, one each. The rest spread over the day, mostly 12:00-19:59 (21 of 32). So the sightings do **not** cluster at the 3 AM stuck check (`FewerPlans.cs` `SetObjectToReset`, §7 table row 1). Row 1 is unlikely as the main writer [inferred from timing; the diag line settles it].

**Built (ts3fixes, switch `boatgetoutdiag`, tuning `kBoatGetOutDiag = false`):**
- `BoatGetOutDiag.cs`: a watch hook on the 8-argument `Boat.GetOut`, patched in `EaFixes.cs` next to `boatparkgone`. With the switch on, a GetOut whose Sim sits in this boat is pushed into `BgoTable` (`BoatGetOutDiagCore.cs`, 8 slots; overflow is logged once and not watched). The original then runs inside `try { } catch (NullReferenceException) { log; throw; } finally { pop }`, so EA's error and its ScriptError are unchanged.
- `PosturePop.cs` `SetHook` gets one first line: `if (sBgo.N != 0) BgoNote(...)`. With nothing watched, that costs one compare per posture set. `BgoNote` ignores the own task's normal end (own task, no other GetOut of that Sim in flight; the own task sleeps through the fade). The first other set per watch logs once: `foreign` or `nested`, the writer task's object type, 12 stack frames with our `Ts3Fixes` frames listed apart, and our last move of the Sim (`MovedSims`).
- Lines in ts3native.log (`DiagLine`, at most 20 per session): `boatgetout replaced #<n>: <Sim> 0x<id> npc|household SittingInBoat-><new> during GetOut of <boat> (+<ticks> ticks, <sim time>, water <b>, houseboat <b>) by nested|foreign task <type>; ours: <frames|none>; last move: <...>`, then `boatgetout stack #<n>: ...`, and on the throw `boatgetout stale: <Sim> NRE in GetOut after replaced #<n>|no replacement seen`.
- Counters 945-947 (agent `boatgetoutdiag_watched`, `_replaced`, `_stale`). Self-check `tools/selftest/boatgetoutdiag` covers push/pop, own end, foreign, nested and overflow.
- Run: turn it on in natural IP correctness jobs (`fixesOn: boatgetoutdiag`) and read every `boatgetout stale:` line as in §7.
