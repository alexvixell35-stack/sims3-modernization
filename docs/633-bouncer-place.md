# 633. Unseen bouncer placed on its slot (`bouncerplace`)

2026-11-17, static, r90-combo, NOT RUN. Default off. Slots 3740-3749. Code: `mods/ts3fixes/src/RouteSources.cs` (next to `bouncerskip`, docs/151 D3).

## 1. Problem

[verified, ilspycmd VelvetRopes+WatchDoor] `Run` calls `Actor.RouteToSlot(Target, RoutingSlot_2)`, enters the "Watch Door" state machine, then `DoLoop(Default, LoopDel, sm, 5f)`; `LoopDel` calls the same `RouteToSlot` every 5 sim-minutes and ignores the result. `bouncerskip` skips the loop route only when the bouncer already stands on the slot facing its way. The phase-2026111* ts3native.log files hold 871 `routefail job: ... WatchDoor target VelvetRopes` lines (failed or cut bouncer routes; 250+ in the recent runs).

## 2. Fix

With `bouncerplace` on, a bouncer that is unseen (`TownieUnseen`: not a player Sim, standing, alone, off screen, far from viewers), not the selected Sim and has no parent, whose rope is off screen and whose RoutingSlot_2 is far from viewers, is put on RoutingSlot_2 facing the slot's forward (`PlaceSim`, as rhentryrelax's route-less arrival, docs/627 §4), and:

- `LoopDel`: EA's route is skipped (`bouncerskip` still runs first when the bouncer is already on the slot);
- `Run`: EA's `Run` follows unchanged; its `RouteToSlot` starts on the slot (zero-length route).

Seen, household, selected and parented bouncers route as EA. Placement is the slot EA's own route ends on.

## 3. Counters and Check

[3740] loop routes replaced, [3741] Run arrivals placed, [3742] out of scope, [3743] slot seen, [3744] errors, [3745] Check mismatches (must stay 0), 3746-3749 spare.

`Ts3Fixes.Fixes.BouncerPlaceCheck run|result` (repro `bouncerplace-check`): pure placement decision for an unseen bouncer with an unseen slot (off: EA routes, BROKEN; on: placed, FIXED); controls: player / selected / seen / parented bouncer and a seen slot keep EA's route, the off rule never places. Live: bouncers on WatchDoor now, unseen / seen. In a live run 3740 grows with the switch on and the WatchDoor routefail lines should drop.
