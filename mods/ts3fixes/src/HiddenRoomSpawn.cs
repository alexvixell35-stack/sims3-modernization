using System;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Services;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // hiddenroomspawn (docs/552 §2 and §10, off by default): prevention of the hidden-room trap at the three EA placements
    // that pick a spot with no room or route check (every other EA placement checks IsPointInLotSafelyRoutable, and the
    // scans measured 0 of ~120 FGL landings in problem rooms getting past that check). NPCs only (PlayerSims.cs IsNpcSim);
    // household and selected Sims keep EA's code. A spot is bad when it lies in a hidden room (Lot.IsRoomHidden) or, with a
    // Sim to ask, in a room it has no allowed way outside from (HiddenRoom.cs HrReachesOutside).
    //   reset  Sim.AttemptToPutInSafeLocation: when EA's own search (AttemptToFindSafeLocation) finds nothing, EA puts the Sim
    //          at Lot.Position (home lot, else the current lot: the lot centre on the ground floor). If that is bad, the
    //          lot's entry point (outside the front door) is used, else EA's service start spot near the lot
    //          (Service.GetPositionInRandomLot: a sidewalk off screen). The success path is EA's code unchanged.
    //   corners Service.GetPositionNearLotCorners (services, burglars, firefighters, role givers, time travellers, virtual
    //          residents' last fallback): its mailbox and Lot.Position fallbacks are unchecked; a bad answer is replaced
    //          the same way.
    //   create Service.CreateSim: EA keeps a level-0 elevator spot whose room it did not accept as public; a service NPC
    //          created in a bad spot is moved to EA's service start spot near the lot before it ever routes.
    // Lot.EntryPoint is left alone: it has no Sim (household Sims use it too) and its Lot.Position fallback needs a lot with
    // neither front door nor mailbox (every Bridgeport problem lot scanned answered entry=outside).
    // Counters: [2387] reset fallbacks re-picked, [2388] corner spots re-picked, [2389] created service NPCs moved, [2390] errors.
    public static partial class Fixes
    {
        static void ApplyHiddenRoomSpawn()
        {
            Patch(typeof(Sim), "AttemptToPutInSafeLocation", typeof(HrResetHook), "PutHook", "PutOrig");
            Patch(typeof(Service), "GetPositionNearLotCorners", typeof(HrCornersHook), "CornersHook", "CornersOrig");
            Patch(typeof(Service), "CreateSim", typeof(HrCreateHook), "CreateHook", "CreateOrig");
        }

        // Is p in a hidden room, or (with a Sim) in a room that Sim cannot leave? Invalid or outdoor spots are not bad.
        internal static bool HrSpotBad(Vector3 p, Sim probe)
        {
            if (p == Vector3.Invalid) return false;
            LotLocation loc = LotLocation.Invalid;
            Lot lot = LotManager.GetLot(World.GetLotLocation(p, ref loc));
            if (lot == null || lot.IsWorldLot) return false;
            int room = World.GetRoomId(p);
            if (LotManager.RoomIdIsOutside(room)) return false;
            if (lot.IsRoomHidden(room)) return true;
            if (probe == null) return false;
            int n;
            return HrReachesOutside(lot, room, probe, out n) == false;
        }

        // The replacement spot for a bad one on `lot`: its entry point if good, else EA's off-screen service start spot.
        internal static Vector3 HrGoodSpot(Lot lot, Sim probe)
        {
            Vector3 e = lot.EntryPoint();
            if (e != Vector3.Invalid && !HrSpotBad(e, probe)) return e;
            return Service.GetPositionInRandomLot(lot);
        }

        // Where the reset puts a Sim when EA's search found nothing: EA's Lot.Position, or with the switch on and a bad
        // Lot.Position the replacement. Shared with HiddenRoomCheck "fallback" (read-only there).
        internal static Vector3 HrResetFallback(Sim s, Lot lot, bool on, out bool repicked)
        {
            repicked = false;
            Vector3 p = lot.Position;
            if (!on || !HrSpotBad(p, s)) return p;
            Vector3 alt = HrGoodSpot(lot, s);
            if (alt == Vector3.Invalid) return p;
            repicked = true;
            return alt;
        }

        public class HrResetHook
        {
            // EA: if AttemptToFindSafeLocation finds a spot, place there (SetPosition, SetForward, out of and back into the
            // world, unhide, opaque, footprint); else Lot.Position of home lot / virtual home / current lot (non-world).
            public bool PutHook(bool sendHome)
            {
                Sim s = (Sim)(object)this;
                bool hr = ShortcutOn("hiddenroomspawn") && IsNpcSim(s);
                // safelocunhide (Batch582iLogic.cs, docs/582 B9 §5): any Sim; the copy below is EA's with the fallback unhide
                if (!hr && !ShortcutOn("safelocunhide")) return PutOrig(sendHome);
                Vector3 pos, fwd;
                bool found;
                try { found = s.AttemptToFindSafeLocation(sendHome, out pos, out fwd); }
                catch (Exception) { PatchCounters()[2390]++; return PutOrig(sendHome); }  // (EA's own call, which throws the same way)
                if (found)
                {
                    s.SetPosition(pos);
                    s.SetForward(fwd);
                    s.RemoveFromWorld();
                    s.AddToWorld();
                    s.SetHiddenFlags(HiddenFlags.Nothing);
                    s.SetOpacity(1f, 0f);
                    s.SimRoutingComponent.ForceUpdateDynamicFootprint();
                    return true;
                }
                Lot lot = s.LotHome ?? s.SimDescription.VirtualLotHome ?? s.LotCurrent;
                if (lot == null || lot.IsWorldLot) return true;
                bool repicked = false;
                Vector3 p = lot.Position;
                if (hr)
                {
                    try { p = HrResetFallback(s, lot, true, out repicked); }
                    catch (Exception) { PatchCounters()[2390]++; }
                }
                if (repicked) { PatchCounters()[2387]++; ShortcutLog(s.FullName + " -> hiddenroomspawn: reset fallback moved off the lot centre (hidden or closed room) on " + lot.Name); }
                s.SetPosition(p);
                SuAfterFallback(s);  // counts [3827] when safelocunhide is off
                s.SimRoutingComponent.ForceUpdateDynamicFootprint();
                return true;
            }
            public bool PutOrig(bool sendHome) { return false; } // receives Sim.AttemptToPutInSafeLocation's original header
        }

        public static class HrCornersHook
        {
            public static Vector3 CornersHook(Lot lot)
            {
                Vector3 p = CornersOrig(lot);
                if (lot == null || !ShortcutOn("hiddenroomspawn")) return p;
                try
                {
                    Sim probe = Sim.ActiveActor;  // EA's own probe for this search
                    if (!HrSpotBad(p, probe)) return p;
                    Vector3 alt = HrGoodSpot(lot, probe);
                    if (alt == Vector3.Invalid) return p;
                    PatchCounters()[2388]++;
                    return alt;
                }
                catch (Exception) { PatchCounters()[2390]++; return p; }
            }
            public static Vector3 CornersOrig(Lot lot) { return Vector3.Invalid; } // receives Service.GetPositionNearLotCorners' original header
        }

        public class HrCreateHook
        {
            public Sim CreateHook(Lot lot, SimDescription d)
            {
                SimDescription rsPrev = RsEnter(d);  // residentspawnslot (docs/582 B5 §8): resident placed by EA's door-slot test
                Sim s;
                try { s = CreateOrig(lot, d); }
                finally { RsLeave(rsPrev); }
                if (s == null || !ShortcutOn("hiddenroomspawn")) return s;
                try
                {
                    if (!IsNpcSim(s) || !HrSpotBad(s.Position, s)) return s;
                    Lot near = (d != null ? d.VirtualLotHome : null) ?? lot;
                    Vector3 alt = Service.GetPositionInRandomLot(near);
                    if (alt == Vector3.Invalid) return s;
                    // EA's placement sequence; the hidden and faded state EA gave the new Sim stays
                    s.SetPosition(alt);
                    s.RemoveFromWorld();
                    s.AddToWorld();
                    s.SimRoutingComponent.ForceUpdateDynamicFootprint();
                    PatchCounters()[2389]++;
                    ShortcutLog(s.FullName + " -> hiddenroomspawn: created service NPC moved from a hidden or closed room to the street near " + (near != null ? near.Name : "?"));
                }
                catch (Exception) { PatchCounters()[2390]++; }
                return s;
            }
            public Sim CreateOrig(Lot lot, SimDescription d) { return null; } // receives Service.CreateSim's original header
        }
    }
}
