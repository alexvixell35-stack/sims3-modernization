using System;
using System.Reflection;
using Sims3.Gameplay.ActiveCareer.ActiveCareers;

namespace Ts3Fixes
{
    // fireactivejob harness check (docs/570 §1). Read-only: ActiveFireFighter.sActiveJob is not changed.
    public static partial class Fixes
    {
        // FireActiveJobCheck run: a FireFightingJob that no Sim of this world holds (built without its constructor, never
        // registered anywhere) stands in for the value left from the previous world, and goes through the drop decision.
        //   Controls (printed): a job a Sim holds -> kept (decision, plus a real held FireFightingJob when the world has one);
        //   no value -> nothing dropped.
        //   off BROKEN = the stale value is kept; on FIXED = dropped.
        //   result  counters 3953-3955, the current value and whether it is live
        public static object FireActiveJobCheck(string what)
        {
            int[] c = PatchCounters();
            FireFightingJob cur = ActiveFireFighter.sActiveJob;
            bool curLive = cur != null && FajLiveJobs().Contains(cur);
            string counters = "fireactivejob: cleared at quit 2570=" + c[3953] + ", dropped at load 2571=" + c[3954] + ", errors 2572=" + c[3955]
                + "; sActiveJob " + (cur == null ? "null" : curLive ? "a live job" : "STALE") + "; members found=" + (sFajMapTag != null && sFajMinute != null);
            if (what == "result") return counters;
            if (what != "run") return "fireactivejob <run|result>";
            bool on = ShortcutOn("fireactivejob");
            System.Collections.Generic.List<FireFightingJob> live = FajLiveJobs();
            FireFightingJob ghost = (FireFightingJob)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(FireFightingJob));
            bool ghostLive = live.Contains(ghost);
            bool drop = FajDrop(on, true, ghostLive);
            // controls: a job a Sim holds is kept (always evaluated as the decision; also on a real held job when the world
            // has one), and no value means nothing to drop
            bool liveKept = !FajDrop(on, true, true);
            FireFightingJob real = live.Count > 0 ? live[0] : null;
            bool realKept = real == null || !FajDrop(on, true, live.Contains(real));
            bool nullKept = !FajDrop(on, false, false);
            bool control = !liveKept || !realKept || !nullKept;
            string verdict = ghostLive || control ? "UNEXPECTED" : !on ? (drop ? "UNEXPECTED" : "BROKEN") : (drop ? "FIXED" : "UNEXPECTED");
            return "fireactivejob check (switch " + (on ? "on" : "off") + "): stale value dropped=" + drop + " (stale job held by a Sim=" + ghostLive + ")"
                + "; control: live job kept=" + liveKept + ", real held job " + (real == null ? "none in this world (decision control only)" : "kept=" + realKept)
                + ", no value kept=" + nullKept + "; verdict: " + verdict + "; " + counters;
        }
    }
}
