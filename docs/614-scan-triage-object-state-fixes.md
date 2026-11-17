# 614: scan triage real hits: bar put-down wait, put-down surface use, swipe, unicorn spawn, data discs

2026-11-17, static (ilspycmd decompiles of game-copy/assemblies), r72; all five fixes proven in game by their force checks (§8).

Input: the 2026-11-17 static scan triage (top 5 "real" hits). Each was confirmed in the decompiled IL before building;
all five held. Every switch is default off, has a pure Check (off BROKEN / on FIXED with controls), a repro in
`tools/harness/repros.json`, an EpLive row and a swap-stack-audit row. Code: `mods/ts3fixes/src/Scan84.cs`.

## 1. barputdownwait (off, 3461-3463, repro `barputdownwait-check`)

[verified, `BarAdvanced+PutDownCarriedObjectHelper`] `Wait()` is `while (!mHasExecuted) Simulator.Sleep(0)`. Only
`HandlerInternal` sets `mHasExecuted`, and it runs only from `Handler` (anim events during "Exit - Put Down ..."), which
queues a `OneShotFunction`. [verified, `BarAdvanced.PutDownCarriedObject`] `Wait` is called after the requests and after
`stateMachineClient.Dispose()`, so no event can come later: with `mHandler` still null the bartender's Run never returns.
Fix as EA does it in `CarrySystem.FinishPutDown` (when the event did not fire, do the step inline): with no handler queued
and none run, the hook runs `HandlerInternal`'s steps (ParentToSlot to the destination; on success ExitCarry and leave the
use list) and returns. A queued handler keeps EA's wait (it runs next tick). All Sims: the alternative is a Sim frozen
for good, no intended effect.

## 2. putdownsurfaceuse (off, 3464-3466, repro `putdownsurfaceuse-check`)

[verified, `PutDown.TryToPlaceOnSurface(bool)`] `surface.AddToUseList(Actor)` and `Actor.Inventory.SetInUse(Target)`, a
yielding swipe animation, then `if (!Target.ParentToSlot(...)) return false;` with neither released (Cleanup only
destroys the jig). The surface is then InUse and refuses later put-downs. Fix: the hook picks the same surface EA's body
picks (`FindNearestSurfaceOfType(Actor, Normal, Target)`, same tick, before any yield), notes what was held before, and
after a false return releases only what this call took. All Sims: EA's own success path releases both.

## 3. swiperestore (off, 3467-3469, repro `swiperestore-check`)

[verified, `TraitFunctions+SwipeSomething.Run`] the object is faded out, taken off the victim's use list and
`RemoveFromWorld()`'d (`mStartedFadeOutNotFinished` already false, so Cleanup does not redo it), then
`if (!SharedFamilyInventory.Inventory.TryToAdd(mFinalStolenObject)) return false;`: no ReplenishObject alarm, the clone is
in no inventory, the victim's object is gone. Fix (the steal did not happen): AddToWorld, back to its saved parent slot,
opacity 1, destroy a clone that is not the original. Only on a false Run with the original out of the world, alive, and
the stolen object in no inventory. All households.

## 4. unicornspawnfail (off, 3470-3472, repro `unicornspawnfail-check`)

[verified, `UnicornPool.SpawnNPCUnicorn`] the NPC unicorn is instantiated off screen at opacity 0 and gets
`OccultUnicorn+UnicornSpawn` at High. [verified, `UnicornSpawn.Run`] FadeOut, then `return false` when FindGoodLocation
finds no spot (its only false path; no Cleanup override): an invisible unicorn Sim stays. Fix as EA ends an NPC unicorn
(`UnicornDespawn`: `FadeOut(false, true, 0.15f)`, destroyed on the fade alarm); the description stays in the pool for
the next spawn. Household rule: `IsPlayerSim` keeps EA's path.

## 5. datadiscputback (off, 3473-3475, repro `datadiscputback-check`)

[verified, `DataDiscStorage+AddAllDataDiscs.Run` / `+DumpAllDataDiscs.Run`] each disc is `TryToRemove`'d from one
inventory, then `if (!other.TryToAdd(disc)) return false;`: that disc is in neither inventory and the rest stay. Fix (a
move, never a loss): a refused disc goes back where it came from and the loop goes on; Run still returns false when any
disc stayed behind; a disc EA cannot remove still ends the move. No yield in either Run. All households.

## 6. Leads and verdicts

| lead (scan triage 1117) | IL check | verdict |
|---|---|---|
| BarAdvanced PutDownCarriedObjectHelper.Wait, bartender hang | Wait after Dispose, only the anim handler ends it | real, `barputdownwait` |
| PutDown.TryToPlaceOnSurface, counter left in use | false return holds surface use + item in-use | real, `putdownsurfaceuse` |
| SwipeSomething, stolen object lost | removed from world before a failable add | real, `swiperestore` |
| UnicornSpawn, unicorn left invisible | FadeOut before the only false return | real, `unicornspawnfail` |
| DataDiscStorage, disc lost on failed move | removed before a failable add | real, `datadiscputback` |

## 7. r71 empty checks

r71 loaded (build 32ac68f8, 460 switches off), but the game crashed before the phase ran: native ACCESS_VIOLATION at
0x370F8B72 (sim thread; virtual call through a heap vtable at 0x91AD01), so every agent reply was empty, not only the two
docs/613 checks. Cause not proven; per AGENTS rules it counts as ours until a rerun shows otherwise. r72 trims the docs/613
hooks' off arm to EA's body only (no extra game calls with the switch off). r72 ran clean (§8).

## 8. In-game result

r72 (`phase-20261117-0857`), one load, force checks: all five off BROKEN / on FIXED, controls ok, members found, error
counters 0.
- `barputdownwait`: with the switch on, the live replay's Wait returned although the handler never fired (success False).
  With it off, the replay is skipped, because EA's Wait would never return.
- `putdownsurfaceuse`, `swiperestore`, `unicornspawnfail`: pure checks with controls; no failing put-down, swipe or spawn
  happened in play.
- `datadiscputback`: one refused disc: EA loses 1 and moves 1; ours moves 2 and loses none. "All accepted" is the same as
  EA, and an unremovable disc stops the move.
