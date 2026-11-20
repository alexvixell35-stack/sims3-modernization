# 856. `festivalhidden`: out-of-season festival objects are not re-advertised to autonomy after a room change (2026-11-14, contributor, default ON since the 2026-11-20 import: fired in two Starlight Shores runs)

Code: `mods/ts3fixes/src/FestivalHidden.cs` (hook on `GameObject.UpdateCommodityInteractionMap`), tunable `kFestivalHidden` (False), counters 1370-1373 (docs/registry.md). Tags: [verified] = decompiled scripts read locally (not committed); [inferred] = reasoning.

## 1. Problem [verified]

`SeasonalLotMarker.SetSeason` and `InitialUpdate` take out-of-season objects out of the world with `GameObject.RemoveFromWorld`: InWorld cleared, the commodity interaction map set to null (autonomy no longer sees the object), the model and footprint hidden. `GameObject.UpdateCommodityInteractionMap` never looks at InWorld, and `InternalOnSetRoomAndLevel` calls it on any lot or room change, which puts the object back into the lot's map. An out-of-season object that gets a room change is then advertised to autonomy while invisible and without a footprint. EA guards the same case for role givers only. Whether this happens in play is not known: logs do not record InWorld.

## 2. Change

With the switch on, for a non-Sim object that is out of the world, on a non-world lot with a room, that is a season item of the lot's marker, the re-attach is skipped and the map stays null. Everything else, and every in-world object (one flag test on the hot path), runs EA's method unchanged. `AddToWorld` sets InWorld first, so the normal show path is untouched. Sims are never matched (a `(object)this is Sim` test, since `this` can be a Sim in the swapped body); household Sims are only no longer offered such objects.

## 3. Evidence

None: one local game ran with it on and the counters were not dumped. Counter 1371 would show the case. Deciding test (not run): one game on a world with seasonal festival lots across a season change; if 1371 never fires, drop the switch.
