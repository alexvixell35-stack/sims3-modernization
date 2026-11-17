using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.ActiveCareer.ActiveCareers;
using Sims3.Gameplay.CAS;

namespace Ts3Fixes
{
    // daycarequit (tuning kDaycareQuit, off by default; docs/562 §2). EA bug fix, exact for every Sim.
    //
    // Daycare [verified, decompiled]: every enrolled child has a DaycareChildManager with two global listeners
    // (kSimAgeTransition -> ChildAgedUp, kSimDied -> ChildDied). Only RemoveDaycareChild runs its Cleanup. LeaveJobNow
    // (quit, fired, retire) removes them only through EndDaycareDay(IncompleteAndRemoveAllChildren), which it schedules
    // only while a workday situation runs (mDaycareSituation != null). Quit outside work hours and every manager stays,
    // with its listeners and the old Daycare: when a former client ages up, ChildAgedUp -> ParentWithdrawChildIfAgedUp-
    // ToIneligible shows the ex-owner "child withdrew from your daycare" (and throws if the ex-owner is not
    // instantiated: OwnerDescription.CreatedSim); a former client's death runs ChildDied for a career that is gone.
    // Fix: in CleanupDaycareOwner (private, called only as LeaveJobNow's first step), when no workday situation runs and
    // no toddler transport situation is active (EA keeps babysitters for that case), each manager gets EA's own Cleanup
    // and the table is emptied; LeaveJobNow then reads the same state (no situation, so its babysitter branch disposes
    // them as EA does with no transport). LeaveJobNow itself is not swapped: LeaveJobDispose.cs invokes it through
    // reflection, which a swap would break (tools/selftest/swapinvoke).
    // Counters: [2497] managers cleaned, [2498] errors. Check: DaycareQuitChecks.cs.
    public static partial class Fixes
    {
        static readonly FieldInfo sDqSituation = typeof(Daycare).GetField("mDaycareSituation", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        static readonly FieldInfo sDqManagers = typeof(Daycare).GetField("mDaycareChildManagers", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        static void ApplyDaycareQuit()
        {
            if (sDqSituation == null || sDqManagers == null) { AppliedLog().Add("daycarequit: Daycare fields not found, not patched"); return; }
            Patch(typeof(Daycare), "CleanupDaycareOwner", typeof(DqHook), "OwnerHook", "OwnerOrig");
        }

        // The decision (also used by the check): managers left after a quit with no workday and no toddler transport.
        internal static bool DqClean(bool on, bool hadWorkday, bool transportActive, int managers)
        {
            return on && !hadWorkday && !transportActive && managers > 0;
        }

        public class DqHook
        {
            public void OwnerHook()
            {
                OwnerOrig();
                if (!ShortcutOn("daycarequit")) return;  // fast path: switch off
                try
                {
                    bool hadWorkday = sDqSituation.GetValue((object)this) != null;
                    Dictionary<ulong, DaycareChildManager> m = sDqManagers.GetValue((object)this) as Dictionary<ulong, DaycareChildManager>;
                    if (m == null || !DqClean(true, hadWorkday, DqTransportActive(m), m.Count)) return;
                    foreach (DaycareChildManager cm in new List<DaycareChildManager>(m.Values))
                    {
                        if (cm != null) cm.Cleanup();
                        PatchCounters()[2497]++;
                    }
                    m.Clear();
                }
                catch (Exception) { PatchCounters()[2498]++; }
            }
            public void OwnerOrig() { } // receives Daycare.CleanupDaycareOwner's original body
        }

        // EA's own test in LeaveJobNow: a listed child currently in a DaycareToddlerTransportSituation.
        static bool DqTransportActive(Dictionary<ulong, DaycareChildManager> m)
        {
            foreach (ulong id in m.Keys)
            {
                SimDescription sd = SimDescription.Find(id);
                if (sd != null && sd.CreatedSim != null && DaycareSituation.GetDaycareSituationForChild(sd.CreatedSim) is DaycareToddlerTransportSituation) return true;
            }
            return false;
        }
    }
}
