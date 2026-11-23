using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.ActiveCareer.ActiveCareers;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Careers;
using Sims3.Gameplay.EventSystem;

namespace Ts3Fixes
{
    // perfjobmissedlistener (tuning kPerfJobMissedListener, default off; docs/654 §1). Leak fix.
    // EA bug [verified, decompiled PerformanceCareer / Occupation / ActiveCareer / CareerManager / SimDescription]:
    // PerformanceCareer.SetAttributesForNewJob adds EventTracker.AddListener(kOccupationJobMissed, OnJobMissed) and drops
    // the returned listener; nothing ever removes it. Leaving the career (quit, fired, retired, a new occupation) and a
    // description's Dispose (CareerManager.LeaveAllJobs -> ActiveCareer.LeaveJob -> PerformanceCareer.LeaveJobNow) both
    // run LeaveJobNow, which removes the gig alarms and the steady-gig notifier but not this listener. It then holds the
    // career, and through OwnerDescription the description, for the rest of the session (leak census: 1-7
    // target_holds_gone "OnJobMissed" listeners per run, phase-20261117..19). A quit career rejoined later
    // (CareerManager.TryGetNewCareer reuses the QuitCareers instance) gets SetAttributesForNewJob again: a second listener.
    // Fix: PerformanceCareer.RemoveCareerItems (LeaveJobNow's own step, after base.LeaveJobNow's AssignOccupation(null)) is
    // hooked; after EA's body, every OnJobMissed listener whose career is no longer its owner's occupation, or whose owner
    // description is gone, is removed with EA's EventTracker.RemoveListener (which defers while the list is dispatching).
    // The sweep also catches a listener left by a LeaveJobNow that threw before this step. The two other callers of
    // RemoveCareerItems (a declined hire dialog, CareerSelectionModel before rejoining a quit career) remove nothing that is
    // still current: a listener of a career that is its owner's occupation is always kept (PerfJobMissedCore).
    // Household rule: applies to all Sims. A listener of a career its Sim no longer holds can only (a) charge a missed job
    // to a steady gig of a career the Sim quit (CancelSteadyGig: a TNS about a gig of a career it left) or (b) throw on a
    // disposed owner; a current career is never touched, so a household performer's gigs, misses and TNS are as vanilla.
    // Counters [4160] stale listeners removed, [4161] stale listeners seen with the switch off, [4162] errors,
    // [4163] sweeps (hook runs with the switch on). 4164-4169 spare. Log "perfjobmissedlistener: ...", capped.
    public static partial class Fixes
    {
        const int kPjm = 4160;
        static int sPjmLines;
        static readonly FieldInfo sPjmListeners = typeof(EventTracker).GetField("mListeners", kInstAll);

        static void ApplyPerfJobMissedListener()
        {
            if (sPjmListeners == null) { AppliedLog().Add("perfjobmissedlistener: EventTracker.mListeners not found, not patched"); return; }
            Patch(typeof(PerformanceCareer), "RemoveCareerItems", Type.EmptyTypes, typeof(PjmHook), "RemoveHook", "RemoveOrig");
        }

        public class PjmHook
        {
            public void RemoveHook()
            {
                RemoveOrig();
                try { PjmSweep(); } catch (Exception) { PatchCounters()[kPjm + 2]++; }
            }
            public void RemoveOrig() { } // receives PerformanceCareer.RemoveCareerItems()'s original body
        }

        static void PjmSweep()
        {
            bool on = ShortcutOn("perfjobmissedlistener");
            int[] c = PatchCounters();
            if (on) c[kPjm + 3]++;
            EventTracker t = EventTracker.Instance;
            Dictionary<ulong, Dictionary<ulong, List<EventListener>>> all = t == null ? null : sPjmListeners.GetValue(t) as Dictionary<ulong, Dictionary<ulong, List<EventListener>>>;
            Dictionary<ulong, List<EventListener>> byKey;
            if (all == null || !all.TryGetValue((ulong)EventTypeId.kOccupationJobMissed, out byKey)) return;
            List<EventListener> stale = null;
            int gone = 0, left = 0;
            foreach (List<EventListener> list in byKey.Values)
            {
                foreach (EventListener l in list)
                {
                    DelegateListener dl = l as DelegateListener;
                    PerformanceCareer pc = dl != null && dl.Delegate != null ? dl.Delegate.Target as PerformanceCareer : null;
                    if (pc == null) continue;
                    SimDescription d = pc.OwnerDescription;
                    bool valid = d != null && d.IsValidDescription && d.CareerManager != null;
                    bool current = valid && object.ReferenceEquals(d.CareerManager.Occupation, pc);
                    int v = PerfJobMissedCore.Decide(on, valid, current);
                    if (v == PerfJobMissedCore.kKeep) continue;
                    if (v == PerfJobMissedCore.kSeen) { c[kPjm + 1]++; continue; }
                    (stale ?? (stale = new List<EventListener>())).Add(l);
                    if (valid) left++; else gone++;
                }
            }
            if (stale == null) return;
            foreach (EventListener l in stale)
            {
                EventTracker.RemoveListener(l);
                c[kPjm]++;
                PerformanceCareer pc = ((DelegateListener)l).Delegate.Target as PerformanceCareer;
                sSgRegistered.Remove(pc);  // steadygigmiss's world-load registry (ShowtimeLogic.cs) must not keep it either
            }
            if (sPjmLines++ < 50)
                DiagLine("perfjobmissedlistener: removed " + stale.Count + " stale PerformanceCareer.OnJobMissed listener(s): owner gone " + gone + ", career left " + left);
        }
    }
}
