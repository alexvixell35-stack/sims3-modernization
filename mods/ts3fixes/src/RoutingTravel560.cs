using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;

// docs/560 routing / travel flow (script side). Hooks live in existing hook classes (GoHome.Run is already swapped by
// GoHomeHook in Fixes.cs), so this file holds the helpers and the harness check only.
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

        // GoHomeWalkCheck run: the selected Sim gets EA's request (as GoHome.Run makes it, Run walk style), then the step
        //   GoHomeHook takes after Run returns. off BROKEN = the request is still there; on FIXED = gone. The check takes
        //   its own request back in both arms (no lasting change).
        //   result: counters 2477-2478.
        public static object GoHomeWalkCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "gohomewalk: requests released 2477=" + c[2477] + ", errors 2478=" + c[2478];
            if (what == "result") return counters;
            if (what != "run") return "gohomewalk <run|result>";
            try
            {
                Sim sim = Sim.ActiveActor;
                if (sim == null) return "gohomewalk check: no selected Sim (N/A)";
                bool on = ShortcutOn("gohomewalk");
                Sim.WalkStyle st = Sim.WalkStyle.Run;
                int before = GhWalkCount(sim, st);
                if (before < 0 || GhTable(sim) == null) return "gohomewalk check: walk-style table not readable (N/A); " + counters;
                sim.RequestWalkStyle(st);                       // what GoHome.Run does
                if (on) GhWalkRelease(sim, st, before);        // what GoHomeHook does after Run (switch on)
                int after = GhWalkCount(sim, st);
                bool left = after > before;
                if (left) sim.UnrequestWalkStyle(st);           // undo the check's own request
                string verdict = !on ? (left ? "BROKEN" : "UNEXPECTED") : (left ? "UNEXPECTED" : "FIXED");
                return "gohomewalk check (switch " + (on ? "on" : "off") + "): Run requests before " + before + ", after GoHome " + after + "; verdict: " + verdict + "; " + counters;
            }
            catch (Exception e) { return "gohomewalk check: exception " + e.GetType().Name + ": " + e.Message + "; " + counters; }
        }
    }
}
