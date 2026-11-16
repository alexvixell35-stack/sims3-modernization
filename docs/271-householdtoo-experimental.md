# 271. [Experimental] HouseholdToo: one opt-in switch for the realistic household exemptions

Status 2026-11-16: built (branch `feature/householdtoo-1116`), native selftest and ts3fixes offline test pass, NOT RUN in game. Default 0 (off). Never a release default.

## 1. What it is

Rodney 2026-11-16: an experimental switch under which our "no" answers for household Sims also work for household Sims. Scope correction the same day: "Of course they should not work on teleports or non realistic behaviors." So the switch only lifts household exemptions where the visible result stays realistic: the same plan or outcome computed faster or cached (memo answers, worker answers, exact go-live). Anything a player could see as unrealistic stays exempt with the switch on: teleports, fake or skipped routes, fake arrivals or results, warped positions, time skips, interactions that finish without the Sim doing them, frozen or skipped animations, relaxed paths through walls or doors, route cuts and deadlines that end in a failed route, resets, and anything that changes save data. If we were not sure about a site, it stays exempt.

- Switch: `ts3native.ini` `[Experimental] HouseholdToo=0|1` (default 0), read once at game start (`ts3native.cpp`, next to `ReadRouteAsync`). A log line starting with `EXPERIMENTAL HouseholdToo=1` shows when it is on.
- Native -> managed: GetOption2 "would" query q 14 (`ArWould`, the same channel as replanlog's q 10). ts3fixes asks it once per session on the sim thread (`PlayerSims.cs` `HouseholdTooOn`); an older ts3native answers false. Nothing goes into saves.
- Shared predicates:
  - native `HhExempt(bool hh)` (`ts3native.cpp`): true = keep the exemption (vanilla); with the flag on it returns false for a household / selected / unknown-scope ask and counts it.
  - managed `Fixes.HouseholdExempt(Sim)` = `IsPlayerSim(s) && !HouseholdLift(s)` (`PlayerSims.cs`); `HouseholdLift(Sim)` is the flag part for a site that already knows the scope is household (UfWhy `kHousehold`). Null, destroyed or description-less Sims stay exempt with the flag on. Offline model: `HouseholdTooCore.cs`.
  - Rule for new code: a household-exempt site whose household result stays realistic calls these predicates; a site that teleports, fakes, cuts, skips, resets or changes save data keeps `IsPlayerSim` / its own scope test and never reads the switch.
- Counters: native v204 `hhTooLift` (+0x6534, asks `HhExempt` let through); ts3fixes slot 1057 (asks `HouseholdLift` let through). Both stay 0 with the flag off.
- Crash guards and hard ceilings are untouched: `KdUncutCeilMs` (every scope already), watchdog guards, EA crash fixes.

## 2. Inventory

Legend for "realistic with flag on": yes = routed through the shared predicate; no = always exempt (reason given). Line numbers are this branch.

### 2.1 Routed (yes)

| # | Site | File:line | Feature | What the flag changes | Realistic with flag on |
|---|---|---|---|---|---|
| Y1 | DoorMemoX acting gate | `native/ts3native/hostcalls.cpp:215` | `[RouteSnap] DoorMemoX=1/3` cross-job door answer memo | Scope 0/1 jobs (household, selected, unknown) take a memo answer like scope 2/3 jobs; the 1-in-8 re-ask self-check and the session self-disable stay | yes: the same door answer, cached and invalidated by the managed door generation (docs/585); the route is a normal plan |
| Y2 | AutShadow tier-1 request scope `q.Hh` | `mods/ts3fixes/src/AutShadow.cs:933` | `[AutShadow]` worker rows P5/P6 (ActorScope, `AutShadow.cs:1139`, `:1392`, native `autshadow.cpp:1486`), offload items riding request 0 (`AutOffload.cs:886`, `:897`) | Household actors get the worker's row answers and offload items instead of an abort | yes: the answers are the game's own scoring values computed on the worker; the Sim picks an ordinary action |
| Y3 | AutShadow tier-2 request scope `q.T2Hh` | `mods/ts3fixes/src/AutShadow.cs:1916` | tier-2 IC answers (`T2Cut`, drag, P1/P2 aborts, `T2DoorMemo` `:2169`, pet call site `:2290`) | Household actors no longer abort these rows | yes: same values from the game's IC bodies, worker-side; a stale row still aborts to the game |
| Y4 | AutLive seen-exact go-live | `mods/ts3fixes/src/AutLive.cs:472`, `:481`, `:483`, `:527` | `autseengame` + `autliveseen` exact path (docs/124 §17.12) | A live household actor (UfWhy `kHousehold`) takes the seen-NPC exact path; the post and resume tripwires (`kCSeenHhPost`, `kCSeenHhResume`) let it through | yes: the exact path pushes every score live through the game's own C1, the same pick as vanilla. The unseen direct path is never reached: `AtPost` / `Stay` read UfWhy, which stays `kHousehold` |

### 2.2 Left exempt (no)

| # | Site | File:line | Feature | Why it stays exempt |
|---|---|---|---|---|
| N1 | UfWhy household reason | `mods/ts3fixes/src/UnseenFallback.cs:133` (also `:71`, `:92` vehicle occupants, `:152`, `:971` social targets) | central unseen scope for every unseen* fix | Lifting it would make household Sims "unseen": teleports, skipped trips, held exits, fake arrivals. Never |
| N2 | KindDeadlines exempt | `native/ts3native/plandeadline.cpp:110` (`KdExempt`) | `[RouteAsync] KindDeadlines` cuts (250/500 ms) | A cut ends in a failed route for a player command (docs/249 rank 1, player report 2026-11-07). Not realistic |
| N3 | Seen ceilings | `plandeadline.cpp:115` (`KdCeilUs`), `:203` (`KdSyncCeilUs`) | household 10 s vs seen NPC 2 s | Cutting a household plan at 2 s ends in a failed route; the 10 s household ceiling stays |
| N4 | ReplanDedupe | `plandeadline.cpp:213` (`KdKnownUnseen`) | repeat after footprint writes | Not proven exact: a stale route could pass a newly placed object |
| N5 | FbJobCapMs / PlanDeadlineMs / SyncCapMs / unseenfb deadline | `plandeadline.cpp:110`, `native/ts3native/unseenfb.cpp:84` | foreign job cap, plan deadlines | Same as N2: the result is a failed or cut route |
| N6 | Queued-removal plan | `native/ts3native/ts3native.cpp:5189` | docs/89 §18 plan past a queued removal | Relaxed path through an object being removed: not realistic |
| N7 | UnseenAnim held objects | `native/ts3native/unseenanim.cpp:321` | `[UnseenAnim]` skipped animation updates | Frozen or skipped animation |
| N8 | Active-household viewer table | `native/ts3native/unseen.cpp:366` (`UtViewerMsg`), `unseentable.cpp`, `unseenrm.cpp`, `autshadow.cpp:2654` | household Sims as viewers for UnseenThrottle / UnseenTable / UnseenRootMotion / tier 2 | Not a household exemption: it protects NPCs near household Sims from throttles and frozen motion the player would see |
| N9 | UnseenThrottle | `mods/ts3fixes/src/UnseenThrottle.cs:75`, `:199` | held / deferred locomotion updates | Frozen or delayed movement |
| N10 | UnseenTeleportGoal, TownieUnseen trips, unseenboat, unseenpark, virtualhome | `UnseenTeleportGoal.cs:80`, `Fixes.cs:1303`, `UnseenBoat.cs:149` | teleports, shortcut trips, fades | Teleports and skipped routes |
| N11 | Watchdog route-fail reset (case 49), watchdog A1 | `RouteFixes.cs:277`, `:335` | stuck watchdog | Resets a Sim; the A1 verdict only feeds that reset |
| N12 | AutLive unseen direct path, autunseen | `AutLive.cs` `AtPost` / `Stay` (`AutLiveCore.cs:284`) | worker ranking used directly | Not exact; only for unseen Sims |
| N13 | AutLive go-home null-pick count | `AutLive.cs:1172` | counter only | No behaviour |
| N14 | Save and data cleanups | `SaveCleanup.cs:332`, `SaveCleanupSpread.cs:207`, `CasTrimKeep.cs:65`, `DnpRemovalKeys.cs:56`, `ExitCbSweep.cs:50`, `DeadSimHolders.cs:161`, `:324`, `SimRefsDeadSim.cs:66`, `DisposeSpread.cs:64`, `LeaveJobDispose.cs:60`, `:77`, `DeadRefs.cs:1018`, `:1104`, `EaFixes.cs:191`, `VehicleLeaks.cs:328`, `:346`, `OutfitWait.cs:331`, native `castrim.cpp` keep list | trims, disposals, reference cleanups | Change save data or dispose objects the player owns; a mistake corrupts the player's household |
| N15 | NPC-only EA bug fixes | `SplashRoof.cs:71`, `HiddenRoom.cs:133`, `HiddenRoomSpawn.cs:79`, `EventRouteRetry.cs:195`, `:285`, `BirthRoute.cs:215`, `PutDownPlace.cs:105`, `AptLeave.cs:258`, `MoveInPartner.cs:68`, `BookCarry.cs:254`, `TakeARiskGuard.cs:77`, `OverwatchNotice.cs:165`, `BoatGetOutDiag.cs:135` | moves, re-placements, retries, swallowed errors | They move or re-place Sims, change interactions or swallow errors; for household Sims the visible effect is not proven realistic (unsure = no). These are fixes, not "no answers" |
| N16 | Logs and diagnostics | `RouteFailLog.cs`, `ReplanLog.cs`, `hitchattr.cpp:157`, `*Checks.cs` scope tags | log scope tags | No behaviour |
| N17 | Not a Sim decision | `autraces.cpp`, `audiodup.cpp`, `pkgprobe.cpp`, `pkglock.cpp`, `played.cpp`, `thumbstall.cpp`, `eafix.cpp` | process globals, resources, thumbnails | Already apply to everyone or have no household gate |
| N18 | Crash guards / hard ceilings | `plandeadline.cpp:226` (`UcCapUs`, KdUncutCeilMs), EA crash fixes | never-freeze ceiling | Already every scope; unchanged |

Older audit of the same rule: docs/249 (`docs/data/household-audit.csv`), whose rows 1-38 map onto N1-N18.

## 3. Tests

- Native `asklent-test.h` (selftest): flag off = household, selected and unknown asks live, `doorgen.exempt` counted, `hhTooLift` 0; flag on = the same three take the memo (no live call), `hhTooLift` +3, seen-NPC asks not counted, `HhExempt` back to vanilla when off.
- ts3fixes `tools/selftest/householdtoo`: `HouseholdTooCore.Exempt` off/on truth table (gone Sims stay exempt), `SeenExactOk` equals `SeenNpc` for every reason with the flag off and changes only `kHousehold` with it on, household never on the unseen direct path.

## 4. What needs a game run

Correctness only (no timing claim): one load with `HouseholdToo=1`, DoorMemoX=3 and the go-live shadow switches, household Sims routing through doors and taking autonomous actions; `hhTooLift` and slot 1057 above 0, `dmx` self-check mismatches 0, no new exceptions or stuck household Sims. A flag-off load must show both counters at 0.
