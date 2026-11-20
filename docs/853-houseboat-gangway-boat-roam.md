# 853. Houseboat gangway rescue, houseboat leg memo, NPC boat roam cap

2026-11-19. Three ts3fixes switches, all ON since the 2026-11-20 import, proven in game on Isla Paradiso (run s128, one load, off arm then on
arm): `houseboatgangwayrescue`, `houseboatlegmemo`, `boatroamcap`. Contributor work (alexvixell35-stack), first built on the
contributor combo branch (where these were docs/642 §6, §7 and §9) and ported here with only what they need.

Counter slots: 1378-1381 `houseboatlegmemo` (was 3870-3873 on the combo branch; 3869-3874 are taken upstream by PR #540 and
3980-3981 by puddlereach), 1382-1391 `houseboatgangwayrescue`, 1392-1399 `boatroamcap` (contributor block 4000-4089, added in
this PR: `kCounterSlots` 4000 -> 4090, docs/registry.md "Contributor ranges").

## 1. Evidence (s128, Baseline-IP, 2026-11-19) [verified, measurement]

One load. Every other fix as the session had it; the three switches (and two combo-only ones, see the caveat) off for the off
arm, then on. Agent results `repro-off.json` / `repro-on.json` (repro `ip-houseboat-gangway-rescue`, mode `docked`) and
`roam-off.json` / `roam-on.json` (repro `ip-boat-roam-cap`).

| | off | on |
|---|---|---|
| **Gangway (§2, §3)**: NPC Rubisel Ichtaca, home Docked houseboat "Fantasea", sent to a coast spot 77.6 m off with no land link, then EA's `MakeSimGoHome` | | |
| EA's boat trip drops the Sim at | 16.5 m from the gangway | 16.5 m from the gangway (same spot) |
| first gangway leg | FailedBecauseNoPathToDestination, 1118 ms | FailedBecauseNoPathToDestination, 1133 ms |
| later plans from that spot | 2 more gangway legs (1115, 1121 ms) and 3 outer plans (1115-1126 ms), all no path | 1 outer replan (1140 ms), then outer plans in 3 ms and 1 ms (gangway legs answered from the memo) |
| reached home within 150 s | no (`lot_after` Civic Center, `reached_home` false) | yes, at 5.4 s (`lot_after` Fantasea, `rescued` true) |
| counters | all +0 | 1382 marked +1, 1383 rescued +1, 1378 recorded +1, 1379 answered +2 (1380: 2278 ms of plans they stand for), errors 1381 / 1385 +0 |
| **Roam (§4)**: NPC driver in an owned speed boat, BoatAround pushed whenever idle at the helm | Alfonso Alto | Lopita Alto |
| BoatAround roams | 28 in under 3 min, still `SittingInBoat` | 2, then EA's ReturnToShore, `StandingPosture` ashore |
| counters | all +0 | 1392 capped +1, 1393 return pushed +1, 1397 shore reached +1, 1394 failed / 1396 errors +0 |

Log lines (on arm, ts3native.log): `houseboatgangwayrescue: marked Rubisel Ichtaca, houseboat Fantasea - 2br, 1ba, gangway leg
1139 ms no path, ... (16.5 m, Docked)`, `houseboatlegmemo: answered, Rubisel Ichtaca, stands for 1139 ms` (twice),
`houseboatgangwayrescue: Rubisel Ichtaca placed at Fantasea - 2br, 1ba, after 1139 ms of failed legs (3 legs ...)`,
`boatroamcap: Lopita Alto back to shore after 2 roams / 2.2 sim-h`. Later in the all-on idle phase (14 min): two more gangway
legs recorded, one rescue refused for a Sim whose home is not a houseboat (`refused ... no houseboat`, the intended refusal),
no exceptions in the log, 0 error counters.

Caveats: the build was the contributor combo branch, so the on arm also had two switches on that are not in this PR
(`boattaximemo`, `unseenboatgenboat`); boattaximemo records Boat followers only and unseenboatgenboat changes the unseen boat
shortcut, which was off for the gangway run, so neither acts on the gangway leg or on BoatAround [inferred from their code].
The combo's native no-path memo was on in both arms alike; the off arm's legs still took ~1.1 s each. The roam arms used
different NPCs (the repro picks the next free one). Each gangway arm's result lists one Sim. One run on the contributor's machine (ts3native
0.36.267-mc1116); per AGENTS.md "Contributor repros count", a maintainer run that does not trigger the bug is N/A.

## 2. `houseboatlegmemo` (HouseboatLegMemo.cs, HouseboatLegMemoCore.cs)

**Bug [verified, decompiled + logs]:** after every plan of a Sim's route, EA's `SimRoutingComponent.PostPlanRouteCallback`
calls `CheckAndUpdateForHouseboats`. For a Sim whose destination is a houseboat it plans an extra leg from the route's start:
`EnterHouseboatThroughBoat` (a boat leg to deep water near the houseboat, houseboat not Docked) or `EnterHouseboatThroughGangway`
(a walk to the gangway's routing slot with water and boat planning off, houseboat Docked). When that leg finds no path, EA keeps
the outer route as it was and tries again on the next plan: the same no-path leg (0.6-1.1 s in our logs) again and again from
the same spot. Seen in natural Isla Paradiso play for NPCs living on houseboats (one Sim 18 times in a session).

**Fix:** in `UfPlan` (UnseenFallback.cs), a kind-2 plan that matches one of those two legs, from a non-exempt router, is looked
up by (router, start within 0.5 m, goal within 0.5 m). A leg that ran, was not cut short and found no path with no Sim or
vehicle blocking is recorded for `kHouseboatLegMemoHours` (2) sim-hours; a repeat is answered at once with EA's own
`FailedBecauseNoPathToDestination` (EA reads only `Succeeded()`). Successes are never recorded; answers never extend an entry.
Same result as EA's plan, only without the search.

Counters [1378] recorded, [1379] answered, [1380] ms of the recorded plans the answers stand for, [1381] exempt or errors.
Self-check `node tools/selftest/houseboatlegmemo/run.ts`. Repro `ip-houseboat-leg-memo` (mode `undock`, the boat leg).

## 3. `houseboatgangwayrescue` (HouseboatGangwayRescue.cs, HouseboatGangwayRescueCore.cs)

**Bug [verified, decompiled + s128 off arm]:** EA's boat trip home drops an NPC on shore 15-22 m from its Docked houseboat's
gangway, on a spot with no walking path to it. The gangway leg (§2) fails, `GoHome.Run` tries its own route variants and
returns false, autonomy picks GoHome again; nothing gives up, the Sim never gets home and each try costs seconds of planning.

**Fix:** a gangway leg that found no path (planned, or answered by §2) marks the Sim, when its home lot is a Docked houseboat.
After EA's `GoHome.Run` returned false (`GoHomeHook.HgrRun`, in the Sim's own task, never inside the routing callback), a
marked Sim is placed on the houseboat by EA's own `Houseboat.PlaceSimAtEntrance` (what EA's `RouteOntoHouseboat` does for a Sim
with no boat), and the route options that `RouteOntoHouseboat` clears are cleared too. Only when GoHome's target is that
houseboat's lot, it is still Docked, the Sim is not already on it, is within 40 m of the gangway, standing, not riding,
carried, carrying or parented, not in a tomb or hidden room, its GoHome not cancelled and still its running interaction. At
most once per Sim per sim-hour. GoHome's result is returned unchanged. It works with `houseboatlegmemo` off (the leg is then
only watched, not recorded or answered).

Counters [1382] marked, [1383] rescued, [1384] refused, [1385] errors; refusal reasons [1386] exempt, [1387] no / another /
undocked houseboat, [1388] once-per-hour rule, [1389] busy, [1390] on the lot / far / stale mark, [1391] no spot. Self-check
`node tools/selftest/houseboatgangwayrescue/run.ts`. Repro `ip-houseboat-gangway-rescue` (mode `docked`: EA's Teleport Me Here
to a coast spot with no land link, then EA's `MakeSimGoHome`; the run is seen and the unseen shortcuts are off for it, so the
Sim really rides EA's boat home).

## 4. `boatroamcap` (BoatRoamCap.cs, BoatRoamCapCore.cs)

**Bug [verified, decompiled; IP logs + s128 off arm]:** `Boat.BoatAround` (an NPC driving a boat on a world lot) plans a
roaming path, drives it, plans back and drives that; its test only asks that the Sim drives the boat. Nothing in it ends the
outing, and `Boat.ReturnToShore` (EA's exit) is apparently never picked for these NPCs [inferred: no return follows the logged
streaks]. Natural logs show one NPC boating every ~2 sim-hours overnight; each roam costs a ~150-275 ms plan plus a plan back.
s128 off: 28 roams in under 3 minutes, never ashore.

**Fix:** `Fixes.Patch` swaps `Boat.BoatAround.Run` and `Boat.ReturnToShore.Run` (the latter for bookkeeping only). For a
non-exempt NPC driver, a run is capped after `kBoatRoamCapRoams` (3) roams or `kBoatRoamCapHours` (2) since the streak began,
in the night window 23-6, or with energy or bladder below -50. Capped = ReturnToShore's own preconditions are checked, then
EA's `ReturnToShore` is pushed as the run's continuation and the run returns without roaming; EA drives to shore and gets out.
A return that cannot run, is refused or fails lets EA's roam run, with a 1 sim-hour back-off. Never a teleport, never a deleted
boat. A new streak starts with another boat, a gap over 4 sim-hours, a clock that went back, or a return that reached shore.

Counters [1392] runs capped, [1393] ReturnToShore pushed, [1394] returns failed, [1395] exempt runs, [1396] errors, [1397]
returns that reached the shore, [1398] service / role / alarm-task boating logged, [1399] caps by night or low motive.
Self-check `node tools/selftest/boatroamcap/run.ts`. Repro `ip-boat-roam-cap`.

## 5. Household rule and switches

- All three are NPC-only. Each checks `Fixes.HouseholdExempt` and `IsPlayerSim`: household Sims, the selected Sim and Sims of
  unknown scope (no household, no active household) are exempt; a boat with any household occupant, or none aboard, is exempt.
  The rescue (a placement) and the roam cap (an interaction cut short) are never lifted by `[Experimental] HouseholdToo`
  (docs/271). The rescue checks the exemption again at rescue time; exempt routers are never marked.
- Default ON (import 2026-11-20): `[Tunable] kHouseboatLegMemo`, `kHouseboatGangwayRescue`, `kBoatRoamCap` = true; runtime switches
  `houseboatlegmemo`, `houseboatgangwayrescue`, `boatroamcap` (`ag patches '{"<switch>":true}'`). Listed in
  `tools/harness/sets/proven-fixes.txt`.
- Agent: `houseboat_leg_repro` and `boat_roam_repro` (rows in docs/data/gameplay-preconditions.csv); `patches` reads back
  `houseboatlegmemo_*`, `houseboatgangwayrescue_*`, `boatroamcap_*`.
