using System;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ObjectComponents;
using Sims3.Gameplay.Routing;

namespace Ts3Fixes
{
    // Route adaptor leak on ocean and houseboat portals (docs/09 §5). PortalAnimateThroughAction.DoPortal calls
    // CreateAdaptors (ScriptAdaptor.Init subscribes Sim.OnExitReasonsAdded) and then skips PerformRoute for the Ocean, a
    // Houseboat, or an IPortalIgnoreStartStopAnim portal refusing the pre-route, so FollowRouteAction.Kill, the only
    // unsubscribe, never runs: each such portal leaves a dead callback on the Sim that keeps its Route, adaptors and action
    // alive and in the save (Isla Paradiso: ~23 per sim day), and that can still eat a Replan exit reason (ForceReplan of
    // the dead action). Counters: [54] callbacks unsubscribed after DoPortal, [55] stale callbacks removed by the opt-in
    // save cleanup (SaveCleanup.cs).
    public static partial class Fixes
    {
        static void ApplyPortalAdaptorLeak()
        {
            Patch(typeof(PortalAnimateThroughAction), "DoPortal", typeof(PortalAdaptorHook), "DoPortalHook", "DoPortalOrig");
        }

        public class PortalAdaptorHook : PortalAnimateThroughAction
        {
            // Once DoPortal returns, the adaptors it created are dead (a new call creates new ones). Where PerformRoute ran,
            // Kill already unsubscribed and this is a no-op; only the skipped route (and a failed RouteStart) left one. Kill
            // itself is not called: it would also add RouteFailed / stop steering, which the game never did here.
            public ActionResult DoPortalHook(ref PortalComponent.LaneInfo laneInfo, bool preferredLaneSelected)
            {
                ScriptAdaptor before = mScriptAdaptor;
                AneStairs(mRoutingSim, mRoute, mPortalObject, mPortalStartPos, ref laneInfo, preferredLaneSelected);  // angleentry (ShareLanes.cs, docs/686 §8), off by default
                ActionResult r = DoPortalOrig(ref laneInfo, preferredLaneSelected);
                ScriptAdaptor a = mScriptAdaptor;
                if (a != null && a != before && ShortcutOn("portaladaptorleak") && Unsubscribe(a.GetAssociatedSim(this), a))
                    PatchCounters()[54]++;
                return r;
            }
            public ActionResult DoPortalOrig(ref PortalComponent.LaneInfo laneInfo, bool preferredLaneSelected) { return ActionResult.Terminate; } // receives DoPortal's original header
        }

        // The event's private backing field (found by type, not by the compiler's name for it).
        static FieldInfo sExitReasonsField;
        static Delegate ExitReasonsHandlers(Sim s)
        {
            if (sExitReasonsField == null)
                foreach (FieldInfo f in typeof(Sim).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                    if (f.FieldType == typeof(Sim.ExitReasonsAddedCallback)) { sExitReasonsField = f; break; }
            return sExitReasonsField != null && s != null ? sExitReasonsField.GetValue(s) as Delegate : null;
        }

        // What ScriptAdaptor.Kill does, minus the RouteFailed exit reason. True when a callback was actually removed.
        static bool Unsubscribe(Sim s, ScriptAdaptor a)
        {
            if (s == null) return false;
            Delegate before = ExitReasonsHandlers(s);
            s.OnExitReasonsAdded -= a.RouteExitReasonsAddedCallback;
            return !ReferenceEquals(before, ExitReasonsHandlers(s));
        }

        static FieldInfo sRouteActionField;
        static FollowRouteAction RouteActionOf(ScriptAdaptor a)
        {
            if (sRouteActionField == null) sRouteActionField = typeof(ScriptAdaptor).GetField("mRouteAction", BindingFlags.Instance | BindingFlags.NonPublic);
            return sRouteActionField != null ? sRouteActionField.GetValue(a) as FollowRouteAction : null;
        }

        // Stale = its route action is in no live route action list (neither the Sim's nor its routing object's, e.g. a
        // boat). The running action is always mRouteActions[0], so it stays. While the Sim routes, only portal actions are
        // judged: they are always top-level list entries, other actions might be run nested by another one.
        static bool StaleAdaptor(Sim s, ScriptAdaptor a)
        {
            FollowRouteAction act = RouteActionOf(a);
            if (act == null || s.RoutingComponent == null) return false;
            if (s.RoutingComponent.ContainsRouteAction(act)) return false;
            GameObject o = act.RoutingObject;
            if (o != null && o != s && o.RoutingComponent != null && o.RoutingComponent.ContainsRouteAction(act)) return false;
            return !s.RoutingComponent.IsRouting || act is PortalAction;
        }

        // (d) of the opt-in save cleanup: stale route callbacks left by the leak above in existing saves. Also on demand
        // for the test agent (reflect_call); same safety checks as the rest of the cleanup.
        public static string RunRouteAdaptorCleanupOnce()
        {
            int[] c = PatchCounters();
            string unsafeWhy = CleanupUnsafe();
            if (unsafeWhy != null) { c[43]++; return "skipped: " + unsafeWhy; }
            string before = RouteAdaptorCounts();
            int removed = 0;
            foreach (Sim s in Sims3.Gameplay.Queries.GetObjects<Sim>()) removed += RouteAdaptorCleanSim(s);
            c[55] += removed;
            string line = "route adaptor cleanup: -" + removed + " callbacks | before " + before + " | after " + RouteAdaptorCounts();
            ShortcutLog(line);
            AppDomain.CurrentDomain.SetData("ts3fixes.routeadaptorcleanup", line);
            return line;
        }

        // Stale route callbacks of one Sim removed; returns how many (also a step of the spread pass, SaveCleanupSpread.cs).
        static int RouteAdaptorCleanSim(Sim s)
        {
            Delegate d = s == null || s.HasBeenDestroyed ? null : ExitReasonsHandlers(s);
            if (d == null) return 0;
            int removed = 0;
            foreach (Delegate h in d.GetInvocationList())
            {
                ScriptAdaptor a = h.Target as ScriptAdaptor;
                if (a != null && StaleAdaptor(s, a) && Unsubscribe(s, a)) removed++;
            }
            return removed;
        }

        // OnExitReasonsAdded callbacks over all Sims in the world: all handlers, ScriptAdaptor ones, stale ones, and the Sim
        // with the most ScriptAdaptor callbacks. For the test agent (reflect_call) and the cleanup's before/after line.
        public static string RouteAdaptorCounts()
        {
            int sims = 0, handlers = 0, adaptors = 0, stale = 0, max = 0;
            string maxSim = "-";
            foreach (Sim s in Sims3.Gameplay.Queries.GetObjects<Sim>())
            {
                sims++;
                Delegate d = ExitReasonsHandlers(s);
                if (d == null) continue;
                int mine = 0;
                foreach (Delegate h in d.GetInvocationList())
                {
                    handlers++;
                    ScriptAdaptor a = h.Target as ScriptAdaptor;
                    if (a == null) continue;
                    mine++;
                    if (StaleAdaptor(s, a)) stale++;
                }
                adaptors += mine;
                if (mine > max) { max = mine; maxSim = s.FullName; }
            }
            return "sims=" + sims + " handlers=" + handlers + " adaptors=" + adaptors + " stale=" + stale + " max=" + max + " (" + maxSim + ")";
        }
    }
}
