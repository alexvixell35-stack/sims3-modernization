using System;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Objects;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // festivalhidden (contributor, default off). Reported: hidden objects from festivals.
    // Why [verified, decompile]: SeasonalLotMarker.SetSeason (898-913) and InitialUpdate (418-427) take out-of-season objects out of the
    // world with GameObject.RemoveFromWorld (GameObject.cs 4254-4277): InWorld cleared, SetCommodityInteractionMap(null) (autonomy no
    // longer sees the object), hidden flags uint.MaxValue (model and footprint). But GameObject.UpdateCommodityInteractionMap
    // (5960-5971) never looks at InWorld: InternalOnSetRoomAndLevel (5956) calls it on any lot or room change, and it puts the object
    // back into lot.Map. An out-of-season object that gets a room change is then advertised to autonomy again while it is invisible
    // and has no footprint. EA guards the same case for role givers only (Role.cs 407, RoleManager.cs 180). Whether this happens in
    // play is not known (logs do not record InWorld): the counters below show it.
    // With the switch on: for a non-Sim object out of the world, on a non-world lot, that is a season item of the lot's marker, the
    // re-attach is skipped (map stays null). Everything else, and every in-world object, runs EA's method unchanged. AddToWorld sets
    // InWorld first, so the normal show path is untouched. Household Sims are only affected by no longer being offered such objects.
    // Counters 3740 calls looked at (switch on, object out of the world), 3741 re-attach skipped, 3742 not a season item (EA ran),
    // 3743 errors (EA ran).
    public static partial class Fixes
    {
        public partial class FestivalHiddenHook : GameObject
        {
            public void CimHook()
            {
                // this runs on every room or lot change of every object: the in-world case must cost one flag test
                if (InWorld || !ShortcutOn("festivalhidden")) { CimOrig(); return; }
                int[] c = PatchCounters();
                c[3740]++;
                try
                {
                    Lot lot = LotCurrent;
                    // Sims leave the world for rabbit holes and trips: never ours. The world lot is skipped (its GetObjects is a world query).
                    // `(object)this`: the body runs as GameObject.UpdateCommodityInteractionMap, so `this` can be a Sim; a plain
                    // `this is Sim` was folded to false at compile time (CS0184), and the Sim skip never ran.
                    if (((object)this) is Sim || lot == null || lot.IsWorldLot || RoomId == -1) { c[3742]++; CimOrig(); return; }
                    SeasonalLotMarker[] markers = lot.GetObjects<SeasonalLotMarker>();
                    if (markers.Length == 0 || !markers[0].IsSeasonItem(ObjectId)) { c[3742]++; CimOrig(); return; }
                    if (CurrentCommodityInteractionMap != null) SetCommodityInteractionMap(null);  // as RemoveFromWorld left it
                    c[3741]++;
                }
                catch (ResetException) { throw; }
                catch (Exception e) { c[3743]++; CimOrig(); }
            }
            public void CimOrig() { }  // receives GameObject.UpdateCommodityInteractionMap's original header
        }
    }
}
