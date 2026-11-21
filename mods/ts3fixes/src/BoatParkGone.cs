using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;

namespace Ts3Fixes
{
    // boatparkgone (docs/257, EA bug fix, tuning kBoatParkGone, on by default: shipped without repro, Rodney 11-11, static review): BoatOwnable.Park(Lot, Sim, bool) calls
    // RoutingComponent.UpdateDynamicFootprint with no null check. When the boat was destroyed while its driver was still on the
    // way (its components are disposed, GameObject.GetComponent returns null), the driver's BoatMultipleOccupancy /
    // BoatSingleOccupancy.OnRouteFinished -> Park throws a NullReferenceException at mint offset 0x26, the trip's PostRoute and
    // the visit end with a script error. Seen without ts3fixes loaded (2026-10-12, Baseline-IP) [verified, probe-out events].
    // Fix: Park returns at once for a boat with no RoutingComponent: EA would throw on that line, and a destroyed boat has nothing
    // to park, moor or put in an inventory. Every other call runs EA's Park unchanged, so no Sim (household or not) changes
    // except that the throw is gone. Virtual target: SwapMethod keeps flags and slot (docs/536), WindSurfboard's override still
    // dispatches. Counter [937] skipped Park calls.
    public static partial class Fixes
    {
        public class BoatParkGoneHook
        {
            public void ParkHook(Lot targetlot, Sim driver, bool onReset)
            {
                if (BoatGone((object)this as GameObject)) return;
                ParkOrig(targetlot, driver, onReset);
            }
            public void ParkOrig(Lot targetlot, Sim driver, bool onReset) { } // receives BoatOwnable.Park(Lot, Sim, bool)'s original header
        }

        static bool BoatGone(GameObject boat)
        {
            if (boat == null || boat.RoutingComponent != null || !ShortcutOn("boatparkgone")) return false;
            PatchCounters()[937]++;
            return true;
        }
    }
}
