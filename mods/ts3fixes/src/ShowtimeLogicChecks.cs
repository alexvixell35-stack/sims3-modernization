using System;
using System.Runtime.Serialization;
using Sims3.Gameplay.ActiveCareer.ActiveCareers;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Careers;
using Sims3.Gameplay.Core;

namespace Ts3Fixes
{
    // Harness checks for ShowtimeLogic.cs (docs/571 §9-11). Read-only: decisions with controls; the gig check uses a
    // stand-in Gig built without its constructor (no alarm, never scheduled) plus a real held gig when the world has one.
    public static partial class Fixes
    {
        // SteadyGigMissCheck run: another Sim misses a job while this performer has a steady gig today.
        //   Controls: the performer's own missed gig still counts (both states).
        public static object SteadyGigMissCheck(string what)
        {
            int[] c = PatchCounters();
            string missing = sFtcSpec == null ? "Job.Specification" : "";
            string counters = "steadygigmiss: other Sims' misses ignored 2597=" + c[2597] + ", listeners registered at load 2598=" + c[2598] + ", errors 2599=" + c[2599]
                + "; registered this world=" + sSgRegistered.Count + "; " + GlMembers(missing);
            if (what == "result") return counters;
            if (what != "run") return "steadygigmiss <run|result>";
            bool on = ShortcutOn("steadygigmiss");
            bool ignored = !SgCount(on, false);
            bool ctlOwn = SgCount(on, true);
            return "steadygigmiss check (switch " + (on ? "on" : "off") + "): another Sim's missed job ignored=" + ignored
                + "; control: own missed gig counted=" + ctlOwn + "; controls ok=" + ctlOwn
                + "; verdict: " + GlVerdict(on, ignored, ctlOwn, missing.Length == 0) + "; " + counters;
        }

        // NpcShowStuckCheck run: the NPC show flag is still set 30 hours after its show's start time (its gig was removed).
        //   Controls: a show later today (-5 h) and one that started 2 h ago are kept; no flag -> nothing to clear.
        public static object NpcShowStuckCheck(string what)
        {
            int[] c = PatchCounters();
            string flag = "?";
            try { flag = NPCShowManager.isNPCShowScheduled ? "set" : "clear"; } catch (Exception) { }
            string counters = "npcshowstuck: stale flags cleared 2600=" + c[2600] + ", errors 2601=" + c[2601] + "; flag now " + flag + "; " + GlMembers("");
            if (what == "result") return counters;
            if (what != "run") return "npcshowstuck <run|result>";
            bool on = ShortcutOn("npcshowstuck");
            bool cleared = NsStale(on, true, true, 30f);
            bool ctlFuture = !NsStale(on, true, true, -5f);
            bool ctlRecent = !NsStale(on, true, true, 2f);
            bool ctlNoFlag = !NsStale(on, false, true, 30f);
            bool controls = ctlFuture && ctlRecent && ctlNoFlag;
            return "npcshowstuck check (switch " + (on ? "on" : "off") + "): stale NPC show flag cleared=" + cleared
                + "; control: upcoming show kept=" + ctlFuture + ", show started 2 h ago kept=" + ctlRecent + ", no flag untouched=" + ctlNoFlag
                + "; controls ok=" + controls + "; verdict: " + GlVerdict(on, cleared, controls, true) + "; " + counters;
        }

        // GigPullAudienceCheck run: the PullAudience alarm fires for a stand-in gig that no career holds.
        //   Controls: a real gig a performer holds (when the world has one) is not skipped; switch off never skips.
        public static object GigPullAudienceCheck(string what)
        {
            int[] c = PatchCounters();
            string missing = sFtcSpec == null ? "Job.Specification" : "";
            string counters = "gigpullaudience: stale pulls skipped 2602=" + c[2602] + ", errors 2603=" + c[2603] + "; " + GlMembers(missing);
            if (what == "result") return counters;
            if (what != "run") return "gigpullaudience <run|result>";
            bool on = ShortcutOn("gigpullaudience");
            if (missing.Length > 0) return "gigpullaudience check: verdict: UNEXPECTED; " + counters;
            Gig ghost = (Gig)FormatterServices.GetUninitializedObject(typeof(Gig));
            bool ghostLive = GpLive(ghost);
            bool skipped = GpSkip(on, ghostLive);
            Gig real = null;
            foreach (SimDescription d in Household.EveryHumanSimDescription())
            {
                PerformanceCareer pc = d == null ? null : d.Occupation as PerformanceCareer;
                if (pc == null || pc.Jobs == null) continue;
                foreach (Sims3.Gameplay.Careers.Job j in pc.Jobs) { real = j as Gig; if (real != null) break; }
                if (real != null) break;
            }
            bool realKept = real == null || !GpSkip(on, GpLive(real));
            bool offNever = !GpSkip(false, false);
            bool controls = !ghostLive && realKept && offNever;
            return "gigpullaudience check (switch " + (on ? "on" : "off") + "): pull for a gig no career holds skipped=" + skipped + " (stand-in held=" + ghostLive + ")"
                + "; control: real held gig " + (real == null ? "none in this world (decision control only)" : "kept=" + realKept) + ", switch off never skips=" + offNever
                + "; controls ok=" + controls + "; verdict: " + GlVerdict(on, skipped, controls, true) + "; " + counters;
        }
    }
}
