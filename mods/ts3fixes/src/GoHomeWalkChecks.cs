using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;

// gohomewalk harness check (docs/560 §2). Changes only the selected Sim's walk-style count and takes it back.
namespace Ts3Fixes
{
    public static partial class Fixes
    {
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
