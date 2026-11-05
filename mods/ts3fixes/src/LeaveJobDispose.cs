using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Careers;

namespace Ts3Fixes
{
    // leavejobdispose (docs/529 §2): EA's Career.LeaveJob only notes the leave (mLeaveJobOnExit) when the Sim is inside
    // WorkInRabbitHole; WorkInRabbitHole.Cleanup does the real LeaveJobNow later, but only while Actor.CareerManager is
    // not null. SimDescription.Dispose clears mIsValidDescription, calls CareerManager.LeaveAllJobs, sets CareerManager
    // to null and then destroys the Sim [verified in the decompiled game code], so a Sim deleted at work never runs
    // LeaveJobNow: its description stays in CareerLocation.Workers and in the co-workers' Coworkers lists, other Sims keep
    // it as Boss until their next StartWorking, and the dead Occupation stays referenced and saved. Fix, default off:
    // after EA's LeaveJob, a description that is no longer valid with a noted leave gets LeaveJobNow at once (the call EA
    // makes when the Sim is not at work). The noted transfer is not carried out for it. Counter [2054] leaves done.
    public static partial class Fixes
    {
        static readonly FieldInfo sLeaveOnExit = typeof(Career).GetField("mLeaveJobOnExit", kInstAll);
        static readonly MethodInfo sLeaveJobNow = typeof(Career).GetMethod("LeaveJobNow", kInstAll, null, new Type[] { typeof(Career.LeaveJobReason) }, null);

        public class LeaveJobDisposeHook
        {
            public void LeaveHook(bool showTNS, Career.LeaveJobReason reason)
            {
                LeaveOrig(showTNS, reason);
                if (sLeaveOnExit == null || sLeaveJobNow == null || !ShortcutOn("leavejobdispose")) return;
                Career c = (Career)(object)this;
                SimDescription d = c.OwnerDescription;
                if (d == null || d.IsValidDescription) return;
                Career.LeaveJobReason noted = (Career.LeaveJobReason)sLeaveOnExit.GetValue(c);
                if (noted == Career.LeaveJobReason.kNone) return;
                sLeaveOnExit.SetValue(c, Career.LeaveJobReason.kNone);
                try
                {
                    sLeaveJobNow.Invoke(c, new object[] { noted });
                    PatchCounters()[2054]++;
                }
                catch (Exception e) { ShortcutLog("leavejobdispose: LeaveJobNow threw " + (e.InnerException ?? e).GetType().Name); }
            }
            public void LeaveOrig(bool showTNS, Career.LeaveJobReason reason) { } // receives Career.LeaveJob's original header
        }

        // Harness check (repro leavejob-dispose-check). "seed": Sims at work in a rabbit hole (WorkInRabbitHole), not of the
        // active household; when fewer than 2, up to 3 instantiated townies whose career should be at work now are sent
        // (EA's work interaction, as go_to_work). "state": {ready} when 2 or more are at work. "expect broken" / "expect
        // fixed": one Sim at work is deleted as EA's household deletion does (Household.Remove, Dispose); then the work
        // place's Workers list and its workers' Coworkers and Boss are read for the deleted description. Off: still in
        // Workers (EA's deferred leave never ran). On: in none of them.
        public class LjdState { public bool ready; public int atWork; public int sent; }
        static int sLjdSent;
        static Career LjdJob(SimDescription d) { return d.CareerManager != null ? d.CareerManager.OccupationAsCareer : null; }

        static List<Sim> LjdAtWork()
        {
            List<Sim> r = new List<Sim>();
            Household active = Household.ActiveHousehold;
            foreach (Sim s in Sims3.Gameplay.Queries.GetObjects<Sim>())
            {
                if (s == null || s.HasBeenDestroyed || s.Household == active || s.SimDescription == null || !s.SimDescription.IsValidDescription) continue;
                Career c = LjdJob(s.SimDescription);
                if (c != null && !(c is School) && c.CareerLoc != null && s.CurrentInteraction is WorkInRabbitHole) r.Add(s);
            }
            return r;
        }

        public static object LeaveJobDisposeCheck(string what)
        {
            if (what == "seed" || what == "state")
            {
                List<Sim> at = LjdAtWork();
                if (what == "seed" && at.Count < 2)
                {
                    Household active = Household.ActiveHousehold;
                    foreach (Sim s in Sims3.Gameplay.Queries.GetObjects<Sim>())
                    {
                        if (sLjdSent >= 3) break;
                        if (s == null || s.HasBeenDestroyed || s.Household == active || s.SimDescription == null || s.InteractionQueue == null) continue;
                        Career c = LjdJob(s.SimDescription);
                        if (c == null || c is School || c.IsAtWork || c.CareerLoc == null || c.CareerLoc.Owner == null || !c.ShouldBeAtWork()) continue;
                        if (s.InteractionQueue.Add(c.CreateWorkInteractionInstance())) sLjdSent++;
                    }
                }
                LjdState st = new LjdState();
                st.atWork = at.Count; st.sent = sLjdSent; st.ready = at.Count >= 2;
                return st;
            }
            if (what != "expect broken" && what != "expect fixed") return "leavejobdispose <seed|state|expect broken|expect fixed>";
            List<Sim> now = LjdAtWork();
            if (now.Count == 0) throw new InvalidOperationException("leavejobdispose " + what + ": no Sim at work");
            Sim v = now[0];
            SimDescription d = v.SimDescription;
            Career job = LjdJob(d);
            CareerLocation loc = job.CareerLoc;
            string name = d.FullName + " (" + job.CareerName + ")";
            bool on = ShortcutOn("leavejobdispose");
            int done0 = PatchCounters()[2054];
            string threw = "nothing";
            try
            {
                if (d.Household != null) d.Household.Remove(d);
                d.Dispose(true, false, true);
            }
            catch (Exception e) { threw = e.GetType().Name + ": " + e.Message; }
            bool inWorkers = loc.Workers.Contains(d);
            int coRefs = 0, bossRefs = 0;
            foreach (SimDescription w in loc.Workers)
            {
                if (w == null || w == d || w.CareerManager == null) continue;
                Occupation o = w.Occupation;
                if (o == null) continue;
                if (o.Coworkers != null && o.Coworkers.Contains(d)) coRefs++;
                if (o.Boss == d) bossRefs++;
            }
            int done = PatchCounters()[2054] - done0;
            string line = "in Workers " + inWorkers + ", co-worker lists " + coRefs + ", boss of " + bossRefs + ", leaves done " + done + ", threw " + threw + "; " + name + " (switch " + on + ")";
            bool ok = threw == "nothing" && (what == "expect broken" ? !on && inWorkers && done == 0 : on && !inWorkers && coRefs == 0 && bossRefs == 0 && done == 1);
            if (!ok) throw new InvalidOperationException("leavejobdispose " + what + ": wrong outcome; " + line);
            return "as expected; " + line;
        }
    }
}
