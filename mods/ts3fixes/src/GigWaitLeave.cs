using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.ActiveCareer.ActiveCareers;

namespace Ts3Fixes
{
    // gigwaitleave (tuning kGigWaitLeave, off by default; docs/568 §2). EA bug fix in the gig customer wait pulse of
    // Stylist.MakeoverSituation.WaitAround and SingerCareer.WaitForSingagramSituation.WaitAround (both private).
    //
    // [verified, decompiled] PushWaitIfNecessary is a 5-minute repeating alarm. When the customer has waited too long
    // (Stylist: not on the makeover lot within kMaxGetToLotHours = 4 h; both: kMaxWaitHours = 20 h) it calls
    // ForceLeaveJob() - "sorry, I have to cancel" TNS, Job.LeaveJob, which exits the situation - but has no return
    // after it. The same pulse then still force-pushes a CriticalNPCBehavior WaitForSim on the customer for the
    // cancelled job: the customer routes to the makeover lot / follows the singer, and its WaitBehavior then runs
    // ForceLeaveJob a second time (second cancel TNS; Stylist.Makeover.ShutDown counts the job twice in
    // mNumJobsCreatedToday) or Exit() on the finished situation before going home. WaitBehavior itself already stops
    // after its own ForceLeaveJob (break), so only the pulse lacks it.
    // Fix: when the switch is on and EA's own WaitedTooLong() is true, the pulse calls EA's ForceLeaveJob() and returns,
    // exactly the first two statements of EA's body; otherwise EA's body runs unchanged.
    //
    // Counters: [2559] Stylist pulses stopped after leave, [2560] Singer pulses stopped after leave, [2561] errors.
    // Check: GigWaitLeaveChecks.cs.
    public static partial class Fixes
    {
        static readonly Type sGwStylist = typeof(Stylist.MakeoverSituation).GetNestedType("WaitAround", BindingFlags.NonPublic | BindingFlags.Public);
        static readonly Type sGwSinger = typeof(SingerCareer.WaitForSingagramSituation).GetNestedType("WaitAround", BindingFlags.NonPublic | BindingFlags.Public);

        static void ApplyGigWaitLeave()
        {
            if (sGwStylist == null) AppliedLog().Add("gigwaitleave: Stylist.MakeoverSituation.WaitAround not found, not patched");
            else Patch(sGwStylist, "PushWaitIfNecessary", typeof(GwStylistHook), "PushHook", "PushOrig");
            if (sGwSinger == null) AppliedLog().Add("gigwaitleave: SingerCareer.WaitForSingagramSituation.WaitAround not found, not patched");
            else Patch(sGwSinger, "PushWaitIfNecessary", typeof(GwSingerHook), "PushHook", "PushOrig");
        }

        // ---- decision (also used by the check): stop the pulse after EA's ForceLeaveJob? ----
        internal static bool GwStop(bool on, bool waitedTooLong)
        {
            return on && waitedTooLong;
        }

        // Runs EA's WaitedTooLong() on the state; when it says so, EA's Parent.ForceLeaveJob() and true.
        // The members are looked up once per state / parent type (two state types, a few parent types).
        static readonly Dictionary<Type, MethodInfo> sGwWaited = new Dictionary<Type, MethodInfo>(), sGwLeave = new Dictionary<Type, MethodInfo>();
        static readonly Dictionary<Type, FieldInfo> sGwParent = new Dictionary<Type, FieldInfo>();

        static bool GwLeaveIfDue(object state)
        {
            Type t = state.GetType();
            MethodInfo waited;
            FieldInfo parentField;
            if (!sGwWaited.TryGetValue(t, out waited)) sGwWaited[t] = waited = t.GetMethod("WaitedTooLong", kInstAll);
            if (!sGwParent.TryGetValue(t, out parentField)) sGwParent[t] = parentField = t.GetField("Parent", kInstAll);
            if (waited == null || parentField == null) return false;
            if (!GwStop(true, (bool)waited.Invoke(state, null))) return false;
            object parent = parentField.GetValue(state);
            if (parent == null) return false;
            Type pt = parent.GetType();
            MethodInfo leave;
            if (!sGwLeave.TryGetValue(pt, out leave)) sGwLeave[pt] = leave = pt.GetMethod("ForceLeaveJob", kInstAll);
            if (leave == null) return false;
            leave.Invoke(parent, null);
            return true;
        }

        static bool GwRun(object state, int slot)
        {
            if (!ShortcutOn("gigwaitleave")) return false;
            try
            {
                if (!GwLeaveIfDue(state)) return false;
                PatchCounters()[slot]++;
                return true;
            }
            catch (Exception) { PatchCounters()[2561]++; return false; }
        }

        public class GwStylistHook
        {
            public void PushHook()
            {
                if (GwRun(this, 2559)) return;
                PushOrig();
            }
            public void PushOrig() { } // receives Stylist.MakeoverSituation.WaitAround.PushWaitIfNecessary's original body
        }

        public class GwSingerHook
        {
            public void PushHook()
            {
                if (GwRun(this, 2560)) return;
                PushOrig();
            }
            public void PushOrig() { } // receives SingerCareer.WaitForSingagramSituation.WaitAround.PushWaitIfNecessary's original body
        }
    }
}
