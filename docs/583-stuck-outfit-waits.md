# 583. Stuck Sims in the needs run: outfit waits that never end (Bridgeport, 2026-11-16)

Run `phase-20261116-1503` (Bridgeport, ts3native 0.36.264-mono1, ts3fixes a4fa63d7, every Sim's needs set to -40 every
8 min; `health.jsonl` = one `sim_health` census per real minute, 48 rows, about 32 sim-minutes apart). Three findings.
Line numbers are in our local decompile (`game-copy/decompiled`, not in git).

| # | Sims | Verdict | Fix |
|---|---|---|---|
| 1 | Reuben Littler, "Meet Up" 687 sim-min | EA bug (same root as #2) | `outfitwaitend` (off) |
| 2 | Ace Wilde, "Switch To Outfit" 1068 sim-min | EA bug | `outfitwaitend` (off) |
| 3 | Lola / Jupiter Belle, Morrigan Hemlock, Deidre Littler, Jett Atkins, Elvira Slayer idle at home | EA design | none |

## §1 Outfit waits that never end (findings 1 and 2)

**Census [verified, health.jsonl].**
- Ace Wilde (NPC, home lot Bridgeport Armory): "Switch To Outfit" (`Sim+SwitchToOutfit`) from row 12 to row 43
  (1068 sim-min), never moved, then no interaction. Hunger 5.1 and energy 80 did not change for those 18 sim-hours:
  his task never left the interaction, so `Motives` were not updated.
- Reuben Littler (NPC, home lot Raised Eyebrows): went home at row 22, at row 27 got "Meet Up"
  (`FollowerMeetUpAtPoint`) with Renee Littler. Renee's ended within two rows (situation over); Reuben's ran to the end
  of the run (687 sim-min) with `routing=True`, posture `HoldingUmbrella` (raining), never moved, energy -60 and
  hunger 54.2 frozen from row 28. `unseen_why 7` (kHeld) comes from the umbrella posture (UfCarrying, below).
- `fix_counters` 2003 (`outfitwaitpoll` stale waits, cause `own`) grew 3 -> 4 -> 6 -> 8 over the run (leak-2..6), and
  the steps line up with Ace's wait (stale ~row 14) and Reuben's (stale ~row 29). No stale wait ended (682 not in the
  >= 2000 dump; none of the 8 Sims moved).

**Why [verified, decompile + docs/224 §5].**
- `Sim.SwitchToOutfit.Run` -> `Sim.SwitchToOutfitWithSpin(spin, mirrored)`, Sim.cs:62088 `spin.Wait(idleIfNecessary: true)`.
- `SwitchOutfitHelper.Wait`, Sim.cs:44852: `while (!mReady) Simulator.Sleep(1u);` with no exit. Only the native
  swap-ready callback sets `mReady`.
- The native swap (`CAS_PostOutfitSwapMsg 0x5ADD40`, docs/224 §5) re-posts itself until the Sim has an Animation
  instance. A low-LOD Sim off screen has none, so the callback never comes while the Sim stays unseen.
- Reuben's case is the route-time change: `FollowerMeetUpAtPoint` (via `GoHereWithHoldingInteraction.Run`, :292
  `Actor.DoRoute(route)`) -> `SimScriptAdaptor.OnAboutToStartFollowingRoute` (:224) ->
  `CheckForClothingChangesBeforeRouting` -> :265 `SwitchToOutfitWithSpin(GoingOffLot)` (meeting point off his lot).
  The route has started (`IsRouting` true), the task sits in the same `Wait` [inferred from routing=True + frozen
  motives + the 2003 step; the stale-wait log line is in-game only].
- EA's exits cannot reach it: `GoHereWithSituation.TimeoutTimerCallback` (60 sim-min) -> `Exit` -> `CleanUp` sets
  `Exiting` on the follower interactions (GoHereWithSituation.cs:1867), but the follower only reads `Exiting` in its
  loop (GoHereWithHoldingInteraction.cs:169), which it never gets back to.

So this is one EA bug: a script wait with no exit on an engine path that, by design, defers the swap while the Sim
is not drawn. EA's game does have a path for changes it does not animate: `Sim.SwitchToOutfitWithoutSpin(category,
index)` (instant, `CASUtils.SetOutfitInGameObject`), used e.g. for children in `SwitchToOutfitWithSpin`.

**Fix `outfitwaitend`** (`kOutfitWaitEnd`, off; `kOutfitWaitEndMinutes` 30; `mods/ts3fixes/src/OutfitWait.cs`, in the
existing `SwitchOutfitHelper.Wait` swap, which runs when `outfitwaitpoll` or `outfitwaitend` is on). Once per
sim-minute past the mark, `OutfitWaitCore.EndNow` ends the wait only when all of these hold:
- the Sim is a live NPC (household, selected, selectable and unknown scope keep EA's loop);
- it is not on screen;
- the cause is `own` (the Sim's pending outfit is still this helper's);
- the wait is not inside the helper's own `ChangeOutfit`, whose tail runs `DoOutfitDelayedSwap` (that would set the
  visible outfit to none after an abort).

The end is EA's own calls, in `ChangeOutfit`'s order:
1. `helper.Abort()` (EA's cancel: the active helper, the pending outfit and the native delayed swap are cleared);
2. `mNoChange = true`, so the caller's spin and `ChangeOutfit` do nothing (EA's state for "no change");
3. `Sim.SwitchToOutfitWithoutSpin(category, index)`, then `LaundryManager.OnClothesChanged` and
   `DoCarriedToddlerSpinIfNeeded`.

A re-`Start` re-inits to "no change", because the Sim now wears the category. Counters 2124 ended, 2125 would end
(switch off), 2126 kept past the mark, 2127 errors; up to 10 `outfitwaitend:` lines. Check
`Fixes.OutfitWaitEndCheck` (pure `EndNow` + 7 controls + the live path: the `Wait` swap in place and the helper handles, `CheckVerdict.cs` `LiveAdd` / `EpLive`), repro
`outfitwaitend-check`.

Reuben's meet-up then routes on; the situation has gone, so the follower loop ends on `!parent.IsRunning`.

The hooked method was read again in full:
- `OutfitWaitCause` now shares `OutfitWaitCauseCode`, so the stale class and the end rule cannot drift apart. An
  unreadable cause returns "cause unread" instead of indexing the counters with -1.
- The loop has no other defects: one switch read per tick before the mark; the poll path keeps the issue #40 dead-Sim
  end.

**Ours, not changed.** `UfCarrying` (UnseenFallback.cs:394) counts a `HoldingUmbrella` posture as carrying, so a Sim
with an umbrella is `kHeld` (seen-like) to the unseen fallbacks. That is the documented "carrying stays held" rule.
It did not cause the hang, but it hides umbrella Sims from the unseen watchdogs. It is a candidate for a separate
change.

## §2 NPCs idle at home with fine needs (finding 3) — EA design

**Census [verified].**
- Morrigan Hemlock, Elvira Slayer (vampires), Jett Atkins, Deidre Littler and Jupiter Belle went home between rows 19
  and 28 (21:00-24:00 sim time) and stood there until the run ended (~10:30). Lola Belle did nothing after row 3.
- All of them had `unseen_why 0`, `uf_hold False`, holds 0, queue 0 and no route fails.
- Their needs jump up while they stand still. Lola's hunger went -49 -> 98 (row 19 -> 20) and her energy -30 -> 67
  (row 36 -> 37).

**Why [verified, decompile].**
- `Autonomy.IsSufficientlyOnScreenForHighLODSimulation` (Autonomy.cs:472-532): a Sim is high LOD only when one of
  these holds:
  - it is in the active household;
  - it is on the active lot or on the active Sim's lot;
  - it is a guest of a selectable host, at daycare, or a resort worker;
  - it is outside and on screen.
  An NPC at home off screen is low LOD.
- `ShouldRunLocalAutonomy` is then false. `ScoreInteractionsOnObjects` (:2098) scores only meta autonomy (lots,
  rabbit holes), and with nothing picked, `MakeSimGoHome` (:1143) does nothing at home.
- `Motive.Update` (Motive.cs:930-937): for a low-LOD Sim it calls `AutoSatisfyMotive` instead of decaying. That is the
  jump.
- So standing at home overnight, with needs auto-filled, is EA's low-LOD simulation.

**Ours ruled out.**
- `unseenpark` and `unseenmotives` were off in this run (neither `kUnseenPark` nor `kUnseenMotiveThrottle` was in the
  settings-on lines).
- No `uf_hold` or holds were recorded.
- AutLive / autunseen (on by ini) change only how a meta search is scored, and its Dry compares are exact (docs/222).

Lola's full day at home is the one open point: `last_search` was off, so the run cannot show what her meta searches
returned. A rerun with `autlastsearch` on would show it. No fix.

## Verdicts

- #1, #2: EA bug, fixed by `outfitwaitend` (default off, proven in game, below).
- #3: by design.
- Gates (2026-11-16): `build-fixes.ts --on kHitchCauseHooks`, `fix-repro-check`, `reflect-members-check`,
  `refs-resolve-check`, `swap-stack-audit` all pass.

## In-game result (s66, Bridgeport, 2026-11-16, run phase-20261116-2018)

- [verified] `OutfitWaitEndCheck` switch off: `verdict: BROKEN` (controls ok, members found); switch on: `verdict: FIXED`
  (controls ok, members found).
- [verified] With the switch off, the hook counted 3 natural waits that the rule would end (2125 = 3, slot 1978 in the
  local build): off-screen NPC own-cause waits past 30 sim-minutes that EA's loop kept.
- [inferred] The end itself in a live wait: the on arm ran right after the switch flip, so no wait reached the mark
  with the switch on (2124 = 0). The end path is EA's own calls (Abort, `SwitchToOutfitWithoutSpin`).
