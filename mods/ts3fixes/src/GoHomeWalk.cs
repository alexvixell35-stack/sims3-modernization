using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;

// gohomewalk (docs/560 §2), off by default. No swap of its own: GoHome.Run is already swapped by GoHomeHook (Fixes.cs),
// whose RunHook calls GhWalkCount before EA's Run and GhWalkRelease after it. Harness check: GoHomeWalkChecks.cs.
namespace Ts3Fixes
{
    public static partial class Fixes
    {
        // ---------- gohomewalk ----------
        // EA's GoHome.Run calls Sim.RequestWalkStyle(WalkStyle) (a per-Sim request count, SimRoutingComponent) and never
        // calls UnrequestWalkStyle: a Sim sent home with Run (every busted party: teen, kegger, LAN, bonfire, tailgate,
        // masquerade, victory) keeps running for as long as it stays instantiated. Switch on: after Run returns (arrived,
        // failed or cancelled), the one request Run added is taken back. Nothing else changes.
        static FieldInfo sGhReq;

        static System.Collections.Generic.IDictionary<Sim.WalkStyle, int> GhTable(Sim sim)
        {
            if (sim == null || sim.SimRoutingComponent == null) return null;
            if (sGhReq == null) sGhReq = typeof(SimRoutingComponent).GetField("mWalkStyleRequests", BindingFlags.Instance | BindingFlags.NonPublic);
            return sGhReq == null ? null : sGhReq.GetValue(sim.SimRoutingComponent) as IDictionary<Sim.WalkStyle, int>;
        }

        static int GhWalkCount(Sim sim, Sim.WalkStyle style)
        {
            try
            {
                IDictionary<Sim.WalkStyle, int> t = GhTable(sim);
                int n;
                return t != null && t.TryGetValue(style, out n) ? n : 0;
            }
            catch (Exception) { PatchCounters()[2478]++; return -1; }
        }

        // Releases the request GoHome.Run added (count went up during Run). before < 0 = could not read: leave as is.
        static void GhWalkRelease(Sim sim, Sim.WalkStyle style, int before)
        {
            try
            {
                if (before < 0 || sim == null || sim.HasBeenDestroyed) return;
                if (GhWalkCount(sim, style) <= before) return;
                sim.UnrequestWalkStyle(style);
                PatchCounters()[2477]++;
            }
            catch (Exception) { PatchCounters()[2478]++; }
        }
    }
}
