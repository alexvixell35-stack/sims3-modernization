using System;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Objects.Vehicles;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // boatroutenull (tuning kBoatRouteNull, off by default). Boat.RouteBoatToLandOnWayToPosition starts with
    // boat.CreateRoute() and calls route.SetOption2 on the result at once. CreateRoute is GameObject.CreateRoute, which returns
    // RoutingComponent?.CreateRoute(), so a boat with no RoutingComponent gives a null route and a NullReferenceException.
    // EA's other boat code treats that state as a plain failure (BoardBoat / GetOutOfBoat tests: "routingComponent == null"
    // returns false), and GetOutOfBoat.Run reads a false from this call as "no way to land". [Isla Paradiso 2026-11-17: NRE
    // in RouteBoatToLandOnWayToPosition from GetOutOfBoat.Run for a household Sim; the missing RoutingComponent is inferred,
    // it is the only null that method meets after GetOutOfBoat.Run has checked the posture and the boat.]
    // Fix: with a null RoutingComponent the call returns false, EA's own failure result, for every Sim (an exception becomes
    // the result EA chose for a failed plan). Counters [3982] returned false, [3983] seen with the switch off, [3984] errors.
    public static partial class Fixes
    {
        public class BoatRouteNullHook
        {
            public bool RouteHook(Sim a, Vector3 pos)
            {
                try
                {
                    Boat b = a != null && a.Posture != null ? a.Posture.Container as Boat : null;
                    if (b != null && b.RoutingComponent == null)
                    {
                        if (!ShortcutOn("boatroutenull")) PatchCounters()[3983]++;
                        else { PatchCounters()[3982]++; return false; }
                    }
                }
                catch (Exception) { PatchCounters()[3984]++; }
                return RouteOrig(a, pos);
            }
            public bool RouteOrig(Sim a, Vector3 pos) { return false; } // receives Boat.RouteBoatToLandOnWayToPosition's original body
        }
    }
}
