# 289. Household exemptions we do not need (2026-11-18)

Static audit plus an offline selftest. Built, NOT RUN in game. Ledger: docs/51 §519.

## 1. Question and rule

Rodney, 2026-11-18: turn `woohootywait` on as it is, and find the other fixes that exempt household Sims without
needing to.

The rule (CLAUDE.md "Household Sims stay untouched"): results for controlled Sims must equal vanilla, except that
proven-exact speedups and EA bug fixes stay, and crash or hang guards keep a long hard ceiling. So an exemption for
household, selected and unknown-scope Sims is:

- **not needed** when the path they would take is
  (a) an EA bug fix: vanilla throws, waits or loops for ever, leaks, reads a wrong or dead reference or leaves a stuck
  state, and the fixed result is what EA's code plainly meant;
  (b) proven exact: the vanilla result, only cheaper or earlier, with a verify;
  (c) a hang or crash guard with a long ceiling on something vanilla never ends;
- **needed** for throttles, skips, caps on normal behaviour, teleports, fakes, resets, relaxed paths, unseen-only
  shortcuts, anything that changes timing or outcome of normal play, anything that changes save data, and every
  `[Experimental] HouseholdToo` site (docs/271; those mechanics were left alone).

When the evidence was not conclusive the exemption stayed and the site is listed under "ask".

## 2. Method

- Inventory: every line in `mods/ts3fixes/src` and `native/ts3native` that asks `IsPlayerSim`, `IsNpcSim`,
  `IsPlayerDesc`, `HouseholdExempt`, `IsSelectable`, `Household.ActiveHousehold`, `PlumbBob.SelectedActor`,
  `Sim.ActiveActor`, `IsActiveSim`, `IsNPC`, `UfWhy`, `HhExempt` or the native scope fields: 597 lines in 169 files.
- Left out as a group, all "needed" by the rule: the unseen fallback and trip shortcuts (`Unseen*.cs`, `UfWhy` table),
  the autonomy worker lanes (`Aut*.cs`), and diagnostics, probes and logs, which only label or count.
- The other 295 lines were read site by site: the whole hook, the switch and its default, and the EA method in the
  local decompile (never committed). Lines in `*Checks.cs` that pick repro Sims or label a log line are N/A.
- Claims are marked [verified] (read in our code and the EA method) or [inferred].

## 3. Result

| | Sites |
|---|---|
| Exemption dropped (or never had one, switch now ON) | 7 (Rodney 2026-11-18: hiddenroomspawn both sites, residentspawnslot, deadrefconvo, ghostreactdeadsim) |
| Ask Rodney | 0 |
| Kept (needed) | 58 rows (native mechanisms grouped) + the unseen / worker lanes left out in §2 |
| N/A (check code, labels, copies of EA's own rule) | 37 |

### 3.1 Changed

| Site | Switch | Before | Now | Why the exemption is not needed |
|---|---|---|---|---|
| `PhoneLogic.cs` `WooHootyWaitHook` | `woohootywait` | default off, because the 400-poll cap has no household exemption | **default ON**, code unchanged | EA's `SendWooHootyText.GoToLotSuccess` waits in `while (Actor.CurrentInteraction != null) Sleep(5)` with no exit [verified]. The "Sim gone" exit only replaces an endless wait. The cap (400 polls of 5 ticks) is a long hard ceiling on a wait vanilla never ends (rule c). A wait that ends as EA's does runs EA's steps unchanged. |
| `Batch582Logic.cs` `BSsHook.CleanHook` | `serviceshowalarm` (on) | a worker of the active household was skipped | no scope test | EA's `ServiceSituation.CleanUp` removes the pending show alarm, so a worker it does not destroy stays hidden, non-autonomous and unsociable for good [verified]. The fix calls EA's own `MakeServiceSimVisible` (rule a). Workers come from `CreateNpcSim`, so a household worker is not expected in play [inferred]; the exemption only left that case broken. |
| `ExitCbSweep.cs` | `exitcbsweep` (on) | household, selected and unknown-scope Sims were skipped | they lose the callbacks of route actions with a kill type set; a replaced adaptor on a live action still stays for them | The only removal of `RouteExitReasonsAddedCallback` is `ScriptAdaptor.Kill`, which `FollowRouteAction.Kill` skips once a kill type is set [verified]. The left-over callback of a dead action pins the route's Sims and can take the replan exit reason before the live route sees it, and `ForceReplan` on a dead action does nothing (rule a). Removing it is what EA's `Kill` does. The replaced-adaptor case can carry another reason mask, so it is not proven equal to vanilla and stays townie-only. |

Decisions are in `HhAuditCore.cs` (pure) and checked by `tools/selftest/hhaudit` (PASS): household takes the fixed
path, and the guards that stay still hold (wait under the cap = EA, a live route's own callback stays for every Sim, a
worker that `CleanUp` destroys is left alone, switch off = EA).
| `HiddenRoomSpawn.cs:79` (reset fallback) and `:146` (new Sim) | `hiddenroomspawn` (on) | NPCs only (`IsNpcSim`) | every Sim, household and unknown scope too (Rodney 2026-11-18, both sites) | EA bug fix (a): vanilla puts a reset Sim whose safe-spot searches all failed at `Lot.Position`, and EA places a new Sim at a spot with no room check; on apartment lots that can be inside a hidden NPC unit where every door is locked, so the Sim is trapped for good (EA's nightly stuck check skips the active lot) [verified, docs/552]. The hook acts only when the spot is bad (`HrSpotBad`: hidden room or no allowed way out); a good spot is EA's. Decision `HhAuditCore.HrActs` (no scope input), selftest `tools/selftest/hhaudit`. Check: `HiddenRoomCheck "fallback"`. |
| `ResidentSpawnSlot.cs:200` | `residentspawnslot` (on) | active household skipped | household too (Rodney 2026-11-18) | EA bug fix (a): EA tests the resident's door slot against the caller's lot instead of `VirtualLotHome` (wrong reference) [verified]. Where a household Sim appears can change in the rare case a household Sim has a `VirtualLotHome` [inferred]. Decision `HhAuditCore.RsUseHome`. |
| `DeadRefs.cs:1019` | `deadrefconvo` (ON since 2026-11-18 with `deadrefsweep`) | a conversation with a household member was left | cleaned for household too (Rodney 2026-11-18); default ON in the save-matrix bundle (§6) | removes gone Sims from conversations (EA leaves a destroyed Sim that throws in `Conversation.RemoveSim`). **Changes saved data for household Sims: save matrix required** (queue job `bp-hh-exempt-save-matrix`, RC list). Counter [996] unused now. |
| `DeadSimHolders.cs:159-163` | `ghostreactdeadsim` (on) | household ghosts, selected Sim and unknown scope skipped | household too (Rodney 2026-11-18) | play result exact (the list is only read by a reference `Contains`) [verified]; the list is a saved field, so a household ghost saves a smaller list [inferred]. **Save matrix required** (same queue job). Only the "no active household yet" load guard stays. |

### 3.2 Ask Rodney

| Site | Switch | What dropping the exemption would do | Why it is not decided here |
|---|---|---|---|

### 3.3 Kept (needed)

| Site | Switch | What the exemption keeps household Sims out of |
|---|---|---|
| `Fixes.cs:2764`, `3292`, `3377` | `gohome`, trip shortcuts, `unseenfbtrip` | teleports |
| `Fixes.cs:3066` | `spotasync` | an answer that arrives a tick later |
| `HiddenRoom.cs:144` | `hiddenroomnpc` | a teleport out of a hidden room (the player can act) |
| `NpcBusyPartner.cs:47` | `npcbusypartner` | a shorter cap on a wait that EA bounds itself (45 min) |
| `OutfitWait.cs:173`, `259` | `outfitwaitpoll`, `outfitwaitend` | a throttle; an instant switch (fake) |
| `PutDownPlace.cs:104` | `putdownplace` | a relaxed put-down in place |
| `RhEntry627.cs:159` | `rhentryrelax` | a relaxed entry / teleport |
| `RouteFixes.cs:313`, `394`, `412` | `stuckwatchdog`, `watchdogasync` | a reset; an async verdict |
| `RouteBackoff.cs:448` | `routableasync` | an answer that arrives later |
| `Responsive631.cs:133`, `LoopSkip632.cs:193` | `zoomtier`, `unseenloopskip` | unseen-only shortcut; a cap on normal loops |
| `EnvListExact.cs:237` | `envlistmemoexact` | the memo after a verify difference |
| `WakePark.cs:114-142` | `wakepark` | parked tasks |
| `AptLeave.cs:258` | `aptleave` | not an exemption: household gets the stricter test |
| `BirthRoute.cs:214` | `birthroute` | the NPC retry waits and hospital placement (the bug fixes already apply to all) |
| `DeadSimHolders.cs:324` | `dreamnodedeadsim`, `overwatchdeadsim` | listener removal and timer reset not proven equal |
| `DisposeSpread.cs:64` | `disposespread` | a deferred Destroy (timing) |
| `DnpRemovalKeys.cs:55` | `dnppetkeys`, `dnpobjkeys` | a drop that is not exact and is saved |
| `EventRouteRetry.cs:195-355` | `eventrouteretry`, `eventcrowdring` | placements, pushes, step-aside |
| `SimRefsDeadSim.cs:60-66` | `simrefsdeadsim` | cleared references the HUD and socials read |
| `CasTrimKeep.cs:58-65`, native `castrim.cpp` | CasTrim | erasing outfits the player owns |
| `Compaction.cs:322-325` | Compaction | not an exemption: household state is restored |
| `SeasonImposter.cs:97` | `seasonimposter` | a delayed rebuild of the home lot |
| `Scan84.cs:322` | `unicornspawnfail` | disposing a Sim |
| `SplashRoof.cs:75` | `splashroof` | a different puddle pick (no hang in vanilla) |
| `StuckJobs83.cs:69`, `189` | `rhexitunseen`, `tipsnpcend` | unseen-only skip and cut |
| `VehicleLeaks.cs:159`, `328`, `445` | `vehgetinleak`, `vehseatdestroy` | vehicles released, destroyed or moved |
| `VisitTooLate.cs:68` | `visittoolate` | being sent home (the wait ends by morning in vanilla) |
| `FightStayCap.cs:63` | `fightstaycap` | a cap on normal play |
| `Batch593Logic.cs:75` | `nullsituation` | being sent home |
| `ResidentSpawnSlot.cs:158` | `residentspawnwalk` | our own room-walk model |
| `SaveCleanupSpread.cs:204-208`, `302` | `savecleanupaggressive` | save trims |
| `StairDup82.cs:75`, `NewCar598.cs:314`, native `hostcalls.cpp:217`, `ts3native.cpp:5745` | `stairwaitend`, `newcartest`, DoorMemoX, async worker gate | HouseholdToo sites, untouched |
| native `plandeadline.cpp` | plan deadlines | route cuts (household keeps the one long ceiling) |
| native `unseen*.cpp`, `a10.cpp`, `autshadow.cpp` | unseen rate, fallbacks, own-route worker, shadow gate | unseen-only skips and worker answers |
| `Unseen*.cs`, `Aut*.cs` (group) | unseen fallback, autonomy worker | teleports, fakes, worker answers |

N/A rows (37): check and repro helpers that pick a townie or label a line (`*Checks.cs`, `AgeUpAbort.cs:118`,
`DatingProfile.cs:71`, `EaFixes.cs:196`, `LeaveJobDispose.cs:60/77`, `OverwatchNotice.cs:163`, ...), lines that use
the active household as a key or include it (`Responsive631.cs:35`, `Hitch622.cs`, `DetailPrio603.cs`), and copies of
EA's own rule that must stay for vanilla equality (`FewerPlans.cs:170`, `EaSpread.cs:608`, `Batch591Logic.cs:700`).

## 4. Found in passing (not changed)

- `VisitTooLate.cs:68` tests `IsSelectable` by hand, so an unknown-scope or never-selectable household guest counts as
  an NPC. It should use `IsPlayerSim`.
- `DnpRemovalKeys.cs:55` (townies): the pass drops sleeping nodes from the removal table, so EA does not remove them
  at the subject's death; a sleeping wish could wake with a dead subject [inferred, not checked further].
- The `HiddenRoomSpawn.cs` header still says "off"; `kHiddenRoomSpawn` is true.
- `tools/registry.ts reserve doc` printed `docs/NaN` because five older reservation rows have no range end; fixed here.

## 5. Build, queue, next

- Build `ts3fixes.package.hh-exempt-audit-1118b` (adds hiddenroomspawn, residentspawnslot, deadrefconvo, ghostreactdeadsim for every Sim; the queue `hh-exempt-audit-correct-1118` pins it; `hidden-room-trap` puts an NPC in the room, so there is no household arm: the household path is covered by the offline selftest and `HiddenRoomCheck fallback`). Earlier build `ts3fixes.package.hh-exempt-audit-1118` (docs/data/builds.json), on the master native set. No new counters,
  no ini change, no native change.
- Queue `tools/harness/queues/hh-exempt-audit-correct-1118.json` (correctness, not launched; Thursday RC repro day):
  BP 10 min with `woohootywait` read back ON, the `woohootywait-check` and `serviceshowalarm` repros, counters
  2642-2643, 2818-2820, 2120-2121 reported, `scriptErrorsMax: 0`.
- Rule review (ts3-rule-reviewer on the diff): PASS; note: the 400-poll ceiling is 2000 simulator ticks, kept as it is on Rodney's call.
- No timing queue: none of the three is a speed change.
- Risk: `exitcbsweep` now touches household Sims once an hour; a household route that needed a callback of a killed
  action would be a new fault (none found: `mKillType` is never reset and a killed action does not run again [verified, `FollowRouteAction`, second read by the rule review]).

## 6. Save-matrix bundle ON (Rodney 2026-11-18)

**Save-matrix bundle ON (Rodney 2026-11-18, build 1118b; tested as one bundle on the RC day, a FAIL turns the culprit off, per-switch only after a FAIL):** deadrefsweep, deadrefconvo, deadrefcooking, sarcmummynpc, kSpouseUnlink, kRelRemoveSafe, kFamilyInvGhost, kAttractionAlarm (+ ghostreactdeadsim, already ON). None of them has a scope test (no household exemption); deadrefconvo / ghostreactdeadsim lost theirs. **Kept OFF, another reason too:** kSaveCleanup / kSaveCleanupAggressive (drop NPC relationships, household tiers, own gain test), kPreferredService (hooks cut by the v0.2.6 hotfix), kShowStageThrowables, kFirePitChair (built, never run, no repro), kMidlifeWishSave (not a save blocker: no gain test). The queue `hh-exempt-audit-correct-1118.json` (jobs 2-6, not launched) runs the matrix on release defaults: BP, SV, IP, BigSave-LR, cc-2k-v2 mods; travel save and byte compare vs off by hand (docs/205).
