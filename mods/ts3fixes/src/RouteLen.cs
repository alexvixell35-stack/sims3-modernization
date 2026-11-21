using System;
using Sims3.Gameplay.Actors;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // routelen (diagnostic, off; docs/246 §6 missing counters 1, 3, 4). Observe only, changes nothing.
    //   1: at each successful plan of a Sim at the four plan ICs (RouteSrcEnd, RouteBackoff.cs), the planned length
    //      (Route.GetDistanceRemaining, whole metres) summed per class, seen / unseen (UfWhy == 0 = unseen; household and
    //      selected Sims are never unseen there), plus a >= 40 m count per class: "do unseen picks go farther".
    //   4: of the unseen plans, those whose goal is in view (!UfGoalUnseen): plans that start unseen and end seen.
    //   3: with routelen on, the agent's patches result adds "pick:<definition>" keys (the cumulative G8 push counts per
    //      definition, AutLive.cs sPicks), so the --ab slice snapshots give picks per arm (tools/harness/gate2-report.ts).
    // Counters: [1004] seen plans, [1005] seen metres, [1006] seen >= 40 m, [1007] unseen plans, [1008] unseen metres,
    // [1009] unseen >= 40 m, [1010] unseen plans ending in view, [1011] errors.
    public static partial class Fixes
    {
        static void ApplyRouteLen() { AppDomain.CurrentDomain.SetData("ts3fixes.picks", sPicks); }

        static void RouteLenEnd(ScriptCore.Route r, bool ok)
        {
            if (!ok || !ShortcutOn("routelen")) return;
            int[] c = PatchCounters();
            try
            {
                Sim sim = r.Follower != null ? r.Follower.Target as Sim : null;
                if (sim == null) return;
                float m = r.GetDistanceRemaining();
                bool unseen = UfWhy(sim) == 0;
                int b = unseen ? 1007 : 1004;
                c[b]++;
                c[b + 1] += (int)(m + 0.5f);
                if (m >= 40f) c[b + 2]++;
                if (unseen && !UfGoalUnseen(r.GetApproximateOriginalDestination())) c[1010]++;
            }
            catch (Exception) { c[1011]++; }
        }
    }
}
