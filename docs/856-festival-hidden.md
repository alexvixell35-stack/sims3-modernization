# 856. `festivalhidden`: out-of-season festival objects are not re-advertised to autonomy after a room change (2026-11-14, contributor, default ON since the 2026-11-20 import: fired in two Starlight Shores runs)

Code: `mods/ts3fixes/src/FestivalHidden.cs` (hook on `GameObject.UpdateCommodityInteractionMap`), tunable `kFestivalHidden` (False), counters 1370-1373 (docs/registry.md). Tags: [verified] = decompiled scripts read locally (not committed); [inferred] = reasoning.

## 1. Problem [verified]

`SeasonalLotMarker.SetSeason` and `InitialUpdate` take out-of-season objects out of the world with `GameObject.RemoveFromWorld`: InWorld cleared, the commodity interaction map set to null (autonomy no longer sees the object), the model and footprint hidden. `GameObject.UpdateCommodityInteractionMap` never looks at InWorld, and `InternalOnSetRoomAndLevel` calls it on any lot or room change, which puts the object back into the lot's map. An out-of-season object that gets a room change is then advertised to autonomy while invisible and without a footprint. EA guards the same case for role givers only. Whether this happens in play is not known: logs do not record InWorld.

## 2. Change

With the switch on, for a non-Sim object that is out of the world, on a non-world lot with a room, that is a season item of the lot's marker, the re-attach is skipped and the map stays null. Everything else, and every in-world object (one flag test on the hot path), runs EA's method unchanged. `AddToWorld` sets InWorld first, so the normal show path is untouched. Sims are never matched (a `(object)this is Sim` test, since `this` can be a Sim in the swapped body); household Sims are only no longer offered such objects.

## 3. Evidence

None: one local game ran with it on and the counters were not dumped. Counter 1371 would show the case. Deciding test (not run): one game on a world with seasonal festival lots across a season change; if 1371 never fires, drop the switch.

## 4. Player report 2026-11-21: the hook is not the cost of the 57 s / 13 s hitches (static, ledger §566)

A tester (retail 1.67, ~1070 packages, kit 1120.k1, ts3fixes m1120u, `festivalhidden` on) had ModTime put the two worst
hitches it owned, 57404 ms and 13113 ms (230 and 208 of 256 samples), on `Ts3Fixes: FestivalHiddenHook.UpdateCommodityInteractionMap`.

- The hook's out-of-world branch (`lot.GetObjects<SeasonalLotMarker>()` per call) runs only for objects out of the world
  (counter 1370); every in-world object pays one `InWorld` test and runs EA's body through `CimOrig` [verified, FestivalHidden.cs].
  Two Starlight Shores runs fired 40-44 skips a session, so the branch is dozens of calls, not seconds [inferred I-566-1].
- EA's body is `ChangeCommodityInteractionMap(lot.Map)`: `RemoveObject` / `AddObject` walk every interaction pair of the
  object, and each removal is a `List<InteractionObjectPair>.Remove` (one `InteractionObjectPair.Equals` per element) on the
  lot's per-commodity list [verified, decompiled CommodityInteractionMap.cs + GameObject.cs 5960-6008; the simler90 242 core
  mod has the same bodies]. With many mods injecting interactions on every object, moving many objects (a mod's
  `OnWorldLoadFinished`, an event listener under `EcHook`) costs moved objects x pairs x list length.
- ModTime's inclusive owner is the innermost mod frame, else the innermost ts3fixes frame (docs/298 §2). The swapped
  `UpdateCommodityInteractionMap` keeps its name and gets the hook's class word, so its frame reads as ours, while EA's code
  below it (the stub `CimOrig`, class word GameObject) reads as game. EA time under a pass-through hook is therefore
  reported as ts3fixes [verified, Fixes.cs SwapMethod + modtime.cpp MdtPick]. Ts3Fixes' own share was 0.6% and the hook is
  not in its own-time top 10 (< 0.01%).

Verdict: refuted; the switch stays on (off leaves the same EA work, then labelled game). The real cost is EA's list
removal; the JIT VSlot work (`InteractionObjectPair.Equals` off the interpreter, feature/jit-iopequals-1120) speeds the
Equals chain; an exact faster removal (same list order) is a separate item (docs/74, row `RemoveInteractionForCommodity`): built 2026-11-21 as `commodmapfast` / `commodmapshadow` (docs/313, default off; kit k3 ships shadow).
