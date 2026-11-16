# 584: Unseen NPC trips: the F1 teleport goal check (`tpdoorrights`, `tpaptgoal`)

2026-11-15/16, Bridgeport (Baseline-BP). Both switches default off; off = upstream's rule unchanged. Issue #434.
Only unseen NPCs reach the code (UfTeleport's UfWhy and TownieUnseen); household, selected and unknown-scope Sims are never
teleported. Sections 1-6 and 9+ of this doc number are other leads, not part of this PR; §7-8 keep their numbers so the code
comments and counters match.

## 7. s56 recovery drop: F1's lot test closes every lot with a door (`tpdoorrights`)

s56 (phase-20261115-2218) against 2043: route retry after a cut 92.2% -> 69.1%. 161 of 235 sampled failed retries are
GoHome plans that fail in 1-2 ms with result 10 (FailedBecauseNoDestinationSpecified, 0 plans) right after an unseen cut
(2043: 5); GoHome cuts barely moved (275 vs 281), census "unseenfb route goal" placements fell 99 -> 11. The new part of
s56 is upstream's F1 goal check (`UnseenTeleportGoal.cs`, docs/267, shipped inside `unseenfbteleport`, no switch of its own).

Cause (verified in EA's code, inferred at run time until s57's counters): F1 asks `UfLotOpen`, which closes the lot when any
`CommonDoor` has `OneWayRoomDestinationId != 0`. EA's "not one-way" value is 0x8FFF (36863): the field initializer and
`SetLockTypeAndOwner(lock, owner)` both set it, and `CommonDoor.IsAllowedThrough` tests `!= 36863`. So every lot with a door
counts as closed, every indoor goal is refused (counter 1041, "lot closed"), and only outdoor-ground goals still teleport.
The refused cut then falls through to EA's own fallbacks (front door slots, mailbox, PlanToLotEx), whose first plan the
native retry line records as the failed retry. The run's door dump (doors.json) shows nearly all doors "Anybody", so the
lock test itself is not what closes them. The same `!= 0` test is in `UfLotOpen`'s other caller `UfPreLot` (docs/202 §3.5 D)
and in `UfLotLocked` (`UrCore.DoorRefuses`), both older (since 2026-10-27); `tpdoorrights` fixes them too (`UfDoorOneWay`,
`UfPreCore.OneWay`): `UfPreLot` (the pre-plan teleport, docs/202 §3.5 D) then accepts lots with only unlocked ordinary doors,
which it refused all along; `UfLotLocked` gets the right value, but `DoorRefuses` ignores one-way since §3.9.7b (whose "one-way
doors refused every beach / park goal" was this sentinel), so it does not change. No other `== 0` / `!= 0` one-way test exists
in the tree or in upstream/main (DoorMirror, snap.cpp and p4door.h only copy the value; HiddenRoom and DoorMirrorCore use 36863).

`tpdoorrights` (default off; off = upstream's rule unchanged): F1 judges the lot with the teleporting Sim's own door rights
(`UfPreCore.TpDoor`, pure, self-check in tools/selftest/unseenfb): special lots stay closed as before; a door with no lock and
no real one-way room is free; "nobody" and a real one-way room close; any other lock passes when EA's
`IsAllowedThrough(sim)` does; on the Sim's home lot a lock owned by its household (owner or owner list) passes too, so a
resident going home is not refused by a family member's locked room. The room flood (`HrReachesOutside`) still uses the
Sim's own rights, so it never lands in a room it cannot leave. Only unseen NPCs reach it (UfTeleport's UfWhy and TownieUnseen).
Counters 2739 lots opened that upstream closes, 2740 of those only by household access, 2741 lots closed by a door the
Sim cannot pass. Once a sim-hour, when they moved, ts3native.log gets `ts3fixes: unseen tp goal: ...` with 1040-1045, the
teleport count (307) and 2739-2741, so s57 can confirm the reason with the switch off and on.

## 8. Apartment and shell lots: F1 still refuses GoHome in Bridgeport (`tpaptgoal`)

The merge regression (3024b2d6 -> b22f48e3, "retry after a cut" 95-96% -> ~70%, same engine and switches) is all GoHome:
the cuts did not grow (good 150 / 176 GoHome cuts, bad 133 / 136), but cuts followed by EA's own replan on the same route
(`GoHome.Run`'s second `PlanRouteIntoLot`, result 10 in ~1 ms, 0 plans) went from 5 / 4 to 98 / 108. Distinct
"unseenfb route goal" placements in the census fell from 76 / 74 to 4 / 7, and with `unseenfbteleport` off (0612) to 0
with the same replan pattern (102 failed of 147 cuts): the cut teleport is what recovered GoHome, and F1 refuses it.
§7's `tpdoorrights` fixes the sentinel but recovers only part: s57 (0104) logged lot closed 61 with "closed by a door 1",
so almost every refusal comes before the door loop: `UfTpLotOpen` (and `UfLotOpen`) close `IsApartmentLot` and
`BuildableShells` lots outright, and Bridgeport's NPCs largely live in apartments [inferred: the log does not name the lot
type; an off-lot, non-ground goal also counts as "lot closed"].

That close is deliberate (NPCs trapped in Late Night hidden apartment rooms, the hiddenroom* switches). `tpaptgoal` (default
off) opens only the unseen NPC's own home lot (`Household.LotHome`, what GoHome targets) and only by the goal room:
`UfPreCore.TpAptLot` / `TpAptOpen` (pure, `tools/selftest/unseenfb`, with hidden-room, no-way-out, raised, unknown-flood and
visitor controls), then `TpGoal` as before (tomb or hidden room refused first) with a room flood (`UfTpAptReach`) that asks
EA's `IsAllowedThrough(from, to, sim)` per portal plus `TpDoor` per door (real one-way rooms and "nobody" locks close, no
household access: other units' doors locked to other households block). Other apartment lots, houseboats, resorts and
diving lots stay closed; household, selected and unknown-scope Sims never get the rule. Counters 2753 allowed, 2754
refused as tomb or hidden, 2755 refused otherwise (counted in UfTeleport only), also on the hourly `unseen tp goal:` line.
Also fixed: `UfLotOpen`'s one-sim-hour lot cache kept a verdict across a runtime `tpdoorrights` toggle (the one-way reading
changed under it). Check: `UnseenTpCheck apt <hex id|any>` (repro `unseen-tp-apt-goal`). A/B: tpdoorrights on, tpaptgoal
off vs on, same save; expect GoHome same-route replans after a cut to fall back toward 3024b2d6's handful.

### 8.1 Result (Bridgeport, same engine and package in both arms, `tpdoorrights` on)

| | upstream b22f48e3 (F1 as shipped) | `tpaptgoal` + `tpdoorrights` on |
|---|---|---|
| unseen route-fail recovery after a cut | ~70% (earlier runs; 96% before the merge) | **94.4%** (522 recovered, 31 failed) |
| Sims trapped in a room they cannot leave | - | 0 |

The fix-off arm of this pair was stopped early: the regressed number (~70%) comes from the earlier runs on the same setup
(s56, s57 and the 3024b2d6 -> b22f48e3 comparison above). Hidden-room markers and visitors stay refused with the switch on
(repro `unseen-tp-apt-goal`, pure checks in `tools/selftest/unseenfb`).
