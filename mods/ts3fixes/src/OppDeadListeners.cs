using System;
using System.Reflection;
using Sims3.Gameplay.EventSystem;
using Sims3.Gameplay.Opportunities;

namespace Ts3Fixes
{
    // oppdeadlisteners (tuning kOppDeadListeners, off by default; docs/562 §1). EA bug fix, exact for every Sim.
    //
    // Opportunity [verified, decompiled]: AddTargetDeletedListener, for an opportunity whose TargetObject is a Sim, adds
    // two global listeners, mSimDiedListener (kSimDied) and mSimDescriptionDisposedListener (kSimDescriptionDisposed),
    // both SimDiedDelegate, which returns Keep for every other Sim. TrySuspendOpportunity (target Sim uninstantiated) adds
    // a third, mSimCreatedListener (kSimInstantiated, SimCreated, Keep until that Sim comes back). Cleanup(bSuccess) ->
    // RemoveAllIncompleteListeners removes the event, completion, loss, timeout and target-deleted listeners but none of
    // these three, and nothing else ever removes them. So every Sim-target opportunity that ends (completed, failed,
    // cancelled, timed out, or rejected in the accept dialog, which also runs Cleanup) leaves two global listeners holding
    // the dead opportunity (and its Actor) for the rest of the session, run on every Sim death and every SimDescription
    // disposal. One that ended while its target was uninstantiated also keeps a kSimInstantiated listener, run on every
    // Sim instantiation; when that Sim comes back, TryResumeOpportunity re-registers the dead opportunity's suspended
    // event listeners (its completion listener among them) and target-deleted listener.
    // Fix: after EA's RemoveAllIncompleteListeners, the three are removed (when still registered) and cleared.
    // Counters: [2495] listeners removed, [2496] errors. Check: OppDeadListenersChecks.cs.
    public static partial class Fixes
    {
        static readonly FieldInfo[] sOdlFields = new FieldInfo[]
        {
            typeof(Opportunity).GetField("mSimDiedListener", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            typeof(Opportunity).GetField("mSimDescriptionDisposedListener", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            typeof(Opportunity).GetField("mSimCreatedListener", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
        };

        static void ApplyOppDeadListeners()
        {
            foreach (FieldInfo f in sOdlFields)
                if (f == null) { AppliedLog().Add("oppdeadlisteners: Opportunity listener fields not found, not patched"); return; }
            Patch(typeof(Opportunity), "RemoveAllIncompleteListeners", typeof(OdlHook), "RemoveHook", "RemoveOrig");
        }

        // The decision (also used by the check): a listener set on the ending opportunity and still registered.
        internal static bool OdlRemove(bool on, bool set, bool completed)
        {
            return on && set && !completed;
        }

        public class OdlHook
        {
            public void RemoveHook()
            {
                RemoveOrig();
                if (!ShortcutOn("oppdeadlisteners")) return;  // fast path: switch off
                try
                {
                    object op = this;
                    foreach (FieldInfo f in sOdlFields)
                    {
                        EventListener l = f.GetValue(op) as EventListener;
                        if (l == null) continue;
                        if (OdlRemove(true, true, l.IsCompleted)) { EventTracker.RemoveListener(l); PatchCounters()[2495]++; }
                        f.SetValue(op, null);
                    }
                }
                catch (Exception) { PatchCounters()[2496]++; }
            }
            public void RemoveOrig() { } // receives Opportunity.RemoveAllIncompleteListeners's original body
        }
    }
}
