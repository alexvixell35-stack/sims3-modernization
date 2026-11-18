# 634. Unseen townie loops, one switch per family (`unseenpianist`, `unseenmusicwatch`, `unseenjam`, `unseenshow`)

2026-11-17, static, r91-combo, NOT RUN. Default off. Slots 3750-3779. Code: `mods/ts3fixes/src/LoopSkip632.cs` (the docs/632 machinery, gated per family). Repros `<switch>-check`.

## 1. Targets

The s85 census (`unseenloopskip` Check, "unlisted asks by class", one ask = one low-LOD autonomous DoLoop pass) ranks the unseen townie loops after VelvetRopes+WatchDoor (done, docs/633). Per-pass cost [verified, ilspycmd]:

| class | asks | Run / loop | per-pass cost | choice |
|---|---|---|---|---|
| BarAdvanced+IdleTend | 2060 | RouteToBarAsBartender once, DoLoop(CheckForNextInteraction) | one `InteractionQueue.GetNextInteraction` poll; the poll is how a queued order ends the idle | skipped: nothing to save beyond the animation wait, and a longer step delays the next customer (job loop) |
| Piano+PianistPerform | 1444 | PlayBandInstrument.DoPlayLoop: DoLoop(PlayLoop) | one `IncrementalEvent(kPlayedPiano, mDeltaTime)` | (b) step, job loop never ended: `unseenpianist` |
| Piano+Watch | 430 | MusicalInstrument.WatchBase.Run: DoLoop(WatchLoopBase / TippingLoop) | a pending sweet-move reaction; tips mode rolls by lifetime and re-routes per song (ExitAndTip) | (c) end at the cap (leisure): `unseenmusicwatch` |
| Guitar+Jam | 338 | JamWithInstrument: DoLoop(PlayLoop) | two delta events (`kPlayedGuitar`, `kSimJammed` TimePassedOnLotEvent) | (b) step, not ended (the jam controller holds partners): `unseenjam` |
| ShowVenue+AutonomouslyAttendShow | 297 | MetaRabbitHoleInteraction.Run: inside the venue, DoLoop(Default), no callback; EA's alarm `TimerFinished` ends it | DoLoop's own pass only | (b) step: `unseenshow` |

No family re-routes or searches per pass (Piano+Watch only per song in tips mode), so (a) placement and (d) search skips do not apply.

## 2. Fix

Each family class is on the docs/632 allow list with its kind, but behind its own switch instead of `unseenloopskip`: with the switch off the class is EA's (and counts as unlisted under `unseenloopskip`). Scope and tiers are docs/632's: the actor (and a Sim target) not household / selectable / selected, low LOD, autonomous, on its own task; tier 1 UfWhy 0 (unseen), tier 2 off screen but near via `ZoomHidden` with `unseenloopskip_offscreen`; re-checked at every getter ask and every step. Active lot and held Sims keep EA.

- step families (`unseenpianist`, `unseenjam`, `unseenshow`): DoLoop's 1-minute wait becomes `kUnseenLoopSkipStepMin` (10); the callbacks take `mDeltaTime`, so the events carry the same total. Exit reasons (role end, EA's visit alarm, a queued interaction's cancel) end the wait on the same tick. Needs `kUnseenLoopSkipStep` (the WaitForExitReason hook is patched only at startup). Never ended early.
- end family (`unseenmusicwatch`): the low-LOD getter answers `min(EA, kUnseenLoopSkipCapMin)`; EA's own post-loop runs (RemoveAlarm, RemoveWatcher, SongFinished(stop), listeners off, EndCommodityUpdates). Never stepped (tips mode rolls by lifetime).

## 3. Counters and Checks

6 slots per family at 3750 (pianist), 3756 (music watch), 3762 (jam), 3768 (show): [+0] unseen tier, [+1] off-screen tier (stepped waits, or capped instances), [+2] out of scope, [+3] Check mismatches (must stay 0), +4/+5 spare; 3774-3779 spare. The out-of-scope reason split stays in 3709-3722.

`Ts3Fixes.Fixes.UnseenPianistCheck` / `UnseenMusicWatchCheck` / `UnseenJamCheck` / `UnseenShowCheck` `run|result`: the family decision for an unseen low-LOD autonomous NPC (off: EA's 1-minute pass or EA's end, BROKEN; on: stepped or capped, FIXED); controls: off, household, high LOD, user-directed, seen, off-screen tier without its switch, active lot, held keep EA; a step family is never ended, an end family never stepped. Live: class found, step hook in, loops of the class now by tier.

## 4. Follow-ups

The census's MetaRabbitHoleInteraction visits (AutonomouslyGoForMealInRestaurant 834, Hospital+AutonomouslyVisitHospital 743, ShoppingRabbitHole+AutonomouslyShopInRabbitHole 347) look like the ShowVenue shape; check each for its own Run before adding them to `unseenshow`.
