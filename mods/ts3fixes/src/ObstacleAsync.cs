using System;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Routing;

namespace Ts3Fixes
{
    public static partial class Fixes
    {
        // ---- obstacleasync (default off, townies only; needs ts3native [RouteAsync] ObstacleKind1=1; docs/803) ----
        // A walking Sim blocked by another Sim or object runs EA's ObstacleEncounteredRouteInterruptAction: it stands and waits
        // (StandAndWaitController, one animation per cycle, up to 50 sim-minutes) and replans the interrupted route on the sim thread
        // (the initial forced replan in PerformAction and the stand-and-wait cycle handler). On Isla Paradiso two games spent 5.7-6.6 s
        // per 10 min of hitch time in that replan, about half of it waiting on the route lock behind a route job. With the switch
        // a townie's Replan made while its current route action is exactly that class carries a tag (RouteBackoff.cs AsyncKindStart,
        // probe channel bits 10 + 11, a label only) and ts3native runs it as a job; the Sim, already stopped, sleeps a tick at a time
        // (AsyncWait). Household, selected and no-household Sims, other action classes (a mod's own subclass) and every route whose
        // scope ts3native does not know as non-household keep EA's sync replan. Counters [3440] tags sent, [3441] started, [3442] refused.
        //
        // ---- interruptasync (default off, townies only; same ts3native key) ----
        // The same tag for EA's three other route interrupt actions that replan their interrupted route from DoRoute's loop:
        // DestinationObstructed, ChangeDestination and Replan. Each OnEnqueue takes mInterruptedPath = the FollowRouteAction's
        // RouteToBeFollowed and kills that action with a stop; PerformAction (or the stand-and-wait cycle) then calls
        // mInterruptedPath.Replan() on the Sim's own task while the Sim stands: the shape of the obstacle wait. ts3native still refuses
        // a route being followed (Active), a nested native call (NoYield) and an unknown or household scope. ChangeDestination switches
        // the Sim's footprint on for its replan, so during the sleep other plans see the standing Sim, as during EA's sync replan.
        // Exact types only. Counters [3443] tags sent, [3444] started, [3445] refused.
        // Cleanup: no loop or retry; a failed type lookup leaves the switch without effect.
        static readonly Type sOerType = typeof(RouteInterruptAction).Assembly.GetType("Sims3.Gameplay.Routing.ObstacleEncounteredRouteInterruptAction");
        static readonly Type[] sIraTypes = IraTypes();
        static Type[] IraTypes()
        {
            Assembly a = typeof(RouteInterruptAction).Assembly;
            return new Type[] { a.GetType("Sims3.Gameplay.Routing.DestinationObstructedRouteInterruptAction"),
                a.GetType("Sims3.Gameplay.Routing.ChangeDestinationRouteInterruptAction"), a.GetType("Sims3.Gameplay.Routing.ReplanRouteInterruptAction") };
        }

        // the route's follower is a townie whose current route action is exactly EA's obstacle wait
        internal static bool OerTownieRoute(Sims3.SimIFace.IScriptProxy follower)
        {
            Sim s = follower != null ? follower.Target as Sim : null;
            if (sOerType == null || !StepTownie(s) || s.SimRoutingComponent == null) return false;
            object a = s.SimRoutingComponent.GetCurrentRouteAction();
            return a != null && a.GetType() == sOerType;
        }

        // ... or exactly one of the three other interrupt actions
        internal static bool IraTownieRoute(Sims3.SimIFace.IScriptProxy follower)
        {
            Sim s = follower != null ? follower.Target as Sim : null;
            if (!StepTownie(s) || s.SimRoutingComponent == null) return false;
            object a = s.SimRoutingComponent.GetCurrentRouteAction();
            if (a == null) return false;
            Type t = a.GetType();
            foreach (Type x in sIraTypes) if (x != null && t == x) return true;
            return false;
        }
    }
}
