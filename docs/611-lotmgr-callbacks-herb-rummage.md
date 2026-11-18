# 611: LotManager world callbacks, fireplace herb, trash rummage broadcaster

2026-11-17, static, r68, s80, NOT RUN. Three default-off ts3fixes switches in `mods/ts3fixes/src/LotCallbacks80.cs`, from the
IP / wish / listener triage (233 hits, 3 real). RE with tools only: `ilspycmd` on `game-copy/assemblies/*.dll`
(`LotManager`, `World`, both `LoadSaveManager`s, `Fireplace`, `BonFire`, `TrashcanOutside`), a script diff of every
`+=` / `Delegate.Combine` in `PreWorldLoad` against every `-=` / `Delegate.Remove` in `Shutdown`, and grep over the
decompiled tree for callers (`AddHerb`, `RummageBroadcaster*`, `IOutdoorAcceptsGarbage` implementers).

Household rule: each fix drops only what EA itself stopped using (a duplicate callback copy, a broadcaster or effect nothing
references). No exemption, as `heartbrokenbcast` (docs/609 §7). `fireplaceherb` gives the second herb its own burnout
alarm (EA's RemoveHerb then AddHerb), the same EA bug fix `bonfirespice` ships for BonFire.

| switch | slots | hook (swap) | Check / repro |
|---|---|---|---|
| `lotmgrcallbacks` | 3435-3438 | `LotManager.PreWorldLoad`, `LotManager.Shutdown` (LmcLoadHook) | `LotMgrCallbacksCheck`, `lotmgrcallbacks-check` |
| `fireplaceherb` | 3439-3441 | `Fireplace.AddHerb(Herb)` (FphAddHook) | `FireplaceHerbCheck`, `fireplaceherb-check` |
| `rummagebcast` | 3442-3445 | `TrashcanOutside+Rummage.Cleanup` (RmbCleanupHook), `TrashcanOutside.RummageBroadcasterCreate` (RmbCreateHook) | `RummageBcastCheck`, `rummagebcast-check` |

## 1. Triage

Lead file: the s80 IP triage notes (local scratch). Item 2's BonFire half (`SpiceHitFire` overwrite) was already fixed by
`bonfirespice` (docs/561 §6, default on: no herb returns, a burning herb is removed first); only the Fireplace twin was open.

## 2. lotmgrcallbacks [verified, ilspycmd LotManager + World + LoadSaveManager]

`PreWorldLoad` adds 55 handlers; `Shutdown` removes 49. The 6 added and never removed:

| list | kind | handler | per extra copy |
|---|---|---|---|
| `World.OnPortalLockedCallback` | event over private static `mOnPortalLocked_Delegate`, re-registered with the engine on each change | `PortalLockedCallback` | `Lot.OnPortalPathPlan` once more per portal query (docs/115: thousands a minute while routing) |
| `World.ShouldDoorBeVisibleInLiveModeCallback` | public static field | `ShouldDoorBeVisibleInLiveMode` | door query once more |
| `World.OnWallsChangedCallback` | public static field | `OnWallsChanged` | every glass wall's `OnWallsChanged` once more |
| `World.HandToolGetObjectIdForCloneCallback` | public static field | `HandToolGetObjectIdForClone` | query once more |
| `World.HandToolCloneObjectCallback` | public static field | `HandToolCloneObject` | `IOverrideHandToolCloneObject.HandToolCloneObject` once more: an orphan clone [inferred, not traced] |
| `LoadSaveManager.ObjectGroupSaving` | event on the ScriptCore `LoadSaveManager` instance (`gLoadSaveManager`) | `LoadSaveManager_ObjectGroupSaving` | `Lot.AttachSaveGroup` once more per saved group |

Statics live for the session (docs/192 [inferred]; EA's own removals in `Shutdown` assume it), so after k world loads each
list holds k copies. Fix as EA intended: after EA's `Shutdown` every copy is removed; before EA's `PreWorldLoad` every copy
already there is removed (EA then adds one). Only entries whose method is LotManager's own static handler are touched.
The events are changed through their own remove accessors (the portal one re-registers the engine callback); the
ObjectGroupSaving count is read by reflection on the ScriptCore instance (when unreadable one remove is sent, harmless).
Counters: 3435 stacked copies removed at load, 3436 stacked copies seen with the switch off, 3437 copies removed at
shutdown, 3438 errors.

Check: pure strip-then-add model on a private `EventHandler` list (controls: another handler kept, list without the
handler returned as is, empty stays empty), then a replay of a second `PreWorldLoad` on copies of the six live lists
(delegates are immutable; nothing in the world changes): off 2+ copies, on 1. Live counts are printed; after a travel with
the switch off they read 2+.

## 3. fireplaceherb [verified, ilspycmd Fireplace]

`BurnHerbInteraction` tests `HasHerb` in the definition and again in `Run` before the animation; the herb lands in the
event-101 callback (`AddHerbCallback` -> `AddHerb`). Two Sims burning at once both pass. The second `AddHerb` overwrites
`mAddedHerb`, `mHerbFX` and `mHerbEffectBroadcast` without stopping or disposing them and keeps the first burnout alarm
(only added when none is set). `RemoveHerb` frees only the second set: the first fire VFX and haze broadcaster leak until
reload. Fix: `AddHerb` with `HasHerb` or a live broadcaster runs EA's `RemoveHerb` first. `ServingContainer.AddHerb` is a
different method. Counters: 3439 removed first, 3440 left with the switch off, 3441 errors.

Check: pure decision plus a live replay on a world fireplace without a herb (broadcaster A, our step, EA's overwrite with
B; off A still registered, on A disposed; both disposed and the field cleared after).

## 4. rummagebcast [verified, ilspycmd TrashcanOutside]

`Rummage.Run` creates the can's broadcaster before `DoLoop` and destroys it only on the normal path. `Cleanup` destroys the
lid prop only, so a reset mid-loop leaves the broadcaster registered. `RummagingSim` is never cleared, so
`RummageReactionCallback` keeps queueing `RummageReaction` (insult STC) on Sims passing the can. The next rummage overwrites
the field, and `RummageBroadcasterDestroy` has no null check. `RecycleBinOutdoor`'s methods are empty. The tuning's
`TimeDuration` is printed by the Check (> 0 would make the leak expire; not checked statically).
Fix: `Cleanup` disposes it (null-safe, field cleared) when this interaction's Sim is the can's rummager;
`RummageBroadcasterCreate` disposes a stale one first. Counters: 3442 at cleanup, 3443 stale at create, 3444 left with the
switch off, 3445 errors.

Check: pure decision (controls: another Sim's rummage, no broadcaster) plus a live replay on a world can (EA's create, then
the reset Cleanup step); also counts cans holding a broadcaster whose rummager is not rummaging them now.

## 5. Swap audit

`PreWorldLoad`, `Shutdown` and `Rummage.Cleanup` can reach a yield: stubbed (SavedStacks remap). `AddHerb` and
`RummageBroadcasterCreate` cannot.

## 6. r68 in game, r69 Check fix

r68 (`phase-20261117-0145`): `lotmgrcallbacks` off BROKEN / on FIXED. `fireplaceherb` and `rummagebcast` came out UNEXPECTED
with the switch off. Cause: the two Checks passed `eaBug` (true) as the "fixed now" value in the off arm, so off read as
fixed. The swaps are gated by the switch and the off replay did keep the leaked broadcaster [verified, source]. r69 passes
`fixedPure && liveOk` as heartbrokenbcast does (off: false, so BROKEN). Live counters were 0 because no real overlapping herb
or reset rummage happened, and the replay restores the counters on purpose. Both stay pending until r69 runs.
