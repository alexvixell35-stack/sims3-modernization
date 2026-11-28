using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.EventSystem;
using Sims3.Gameplay.Objects.Vehicles;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // streetparkinglisten (tuning kStreetParkingListen, default off; docs/651 §4). EA bug, Bridgeport street parking.
    // [verified, decompiled StreetParking] GetParkingSpotForCar runs EventTracker.AddListener(kEventObjectDisposed,
    // CarDeletedEventDelegate, car) on every grant but stores the listener in mObjectIdToEventListenerDictionary only when
    // the car has no entry yet, so a car granted again leaks one listener per grant (23 -> 112 CarDeletedEventDelegate in
    // phase-20261116-0612); FreeParkingSpotForCar removes only the stored one. CarDeletedEventDelegate frees
    // `e.TargetObject as CarNpc`, so a deleted car of any other type (CarOwnable, service cars) keeps its spots held; with
    // all ten held, EA hands out spot 0 to everyone and cars stack there.
    // Fix (switch on): around EA's grant, a car's stored listener is taken out of the dictionary, so EA stores the new one;
    // afterwards the old one is removed from EventTracker when EA stored a new one (repeat grant) or freed the car, and put
    // back otherwise (no grant: EA's spot-0 answer). Net: exactly one stored listener per car with spots, as EA meant.
    // The delete handler frees the spots of any deleted Car. The spot pick itself stays EA's (household cars included).
    // Counters: [4117] duplicate listeners not kept, [4118] spots freed for a deleted car that is not a CarNpc, [4119] errors.
    public static partial class Fixes
    {
        static readonly FieldInfo sSplDict = typeof(StreetParking).GetField("mObjectIdToEventListenerDictionary", kInstAll);

        static void ApplyStreetParkingListen()
        {
            if (sSplDict == null) { AppliedLog().Add("streetparkinglisten: StreetParking.mObjectIdToEventListenerDictionary not found"); return; }
            Patch(typeof(StreetParking), "CarDeletedEventDelegate", typeof(SplDelHook), "DelHook", "DelOrig");
        }

        // Before EA's grant: take the car's stored listener out of the dictionary (switch on, car known). True when taken.
        internal static bool SplTake(object streetParking, Car car, out Dictionary<ObjectGuid, EventListener> d, out EventListener old)
        {
            d = null; old = null;
            if (car == null || sSplDict == null || !ShortcutOn("streetparkinglisten")) return false;
            try
            {
                d = sSplDict.GetValue(streetParking) as Dictionary<ObjectGuid, EventListener>;
                if (d == null || !d.TryGetValue(car.ObjectId, out old)) return false;
                d.Remove(car.ObjectId);
                return true;
            }
            catch (Exception) { PatchCounters()[4119]++; return false; }
        }

        // After EA's grant (or its throw): drop or restore the old listener.
        internal static void SplSettle(Dictionary<ObjectGuid, EventListener> d, Car car, EventListener old, bool done, bool result)
        {
            try
            {
                int a = OwnBugsCore.SplAfter(done, d.ContainsKey(car.ObjectId), result);
                if (a == OwnBugsCore.kSplRestore) d[car.ObjectId] = old;
                else { EventTracker.RemoveListener(old); PatchCounters()[4117]++; }
            }
            catch (Exception) { PatchCounters()[4119]++; }
        }

        public class SplDelHook
        {
            public ListenerAction DelHook(Event e)
            {
                if (ShortcutOn("streetparkinglisten"))
                {
                    try
                    {
                        Car car = e == null ? null : e.TargetObject as Car;
                        if (OwnBugsCore.SplFrees(true, car != null, car is CarNpc))
                        {
                            if (!(car is CarNpc)) PatchCounters()[4118]++;
                            ((StreetParking)(object)this).FreeParkingSpotForCar(car);
                        }
                        return ListenerAction.Keep;   // EA's answer
                    }
                    catch (Exception) { PatchCounters()[4119]++; }
                }
                return DelOrig(e);
            }
            public ListenerAction DelOrig(Event e) { return ListenerAction.Keep; } // receives StreetParking.CarDeletedEventDelegate
        }
    }
}
