using System;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.DreamsAndPromises;
using Sims3.Gameplay.EventSystem;

namespace Ts3Fixes
{
    // dnpdisposelistener + seasondup (tunings kDnpDisposeListener, kSeasonDup; default off until measured; docs/191 "event listeners").
    //
    // dnpdisposelistener, EA bug [verified decompiled]: the DreamsAndPromisesManager(Sim) constructor registers
    // mObjectDestroyedListener on kDnPSubjectDestroyed, the same event as mSimdescriptionDestroyedListener (lines 717-718),
    // where OnLoadFixup registers it on kEventObjectDisposed (line 3112). So a wish manager created this session (a new Sim,
    // aging toddler -> child, becoming selectable, travel return through CreateFromExportData) never hears that a wish's
    // subject object was destroyed: the wish is not failed, and its mPotentialRemovalNodes entry keeps the destroyed object
    // alive, until the next load fixes the listener. The pass (switch on): right after CreateFromExportData /
    // CreateAndIntitForSim, a manager whose object listener is on kDnPSubjectDestroyed gets it moved to kEventObjectDisposed,
    // i.e. the state a reload gives (EA bug fix; affects household wishes the way a reload does).
    //
    // seasondup, EA bug [verified decompiled, Motives.RecreateMotives / OnLoadFixup]: when mSeasonsChangedEventListener
    // already exists they call EventTracker.AddListener(it) again with no ContainsListener check, so a loaded Sim is
    // registered twice (census: 142 Motives.OnSeasonTransition listeners for ~70 Sims) and every further RecreateMotives
    // adds another copy: each season change runs FixupSeasonAppropriatenessMotives once per copy. The pass (exact, switch
    // on): EventTracker.AddListener(EventListener) skips a listener whose delegate belongs to a Motives object when that
    // same listener object is already registered. No other listener is touched.
    // Counters: [2254] managers created (also with the switch off), [2255] object listeners moved, [2256] errors,
    // [2257] duplicate season adds seen (also with the switch off), [2258] skipped, [2259] errors.
    public static partial class Fixes
    {
        static FieldInfo sLfObjListener;
        static MethodInfo sLfObjDelegate;

        static void ApplyListenerFixes()
        {
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            sLfObjListener = typeof(DreamsAndPromisesManager).GetField("mObjectDestroyedListener", inst);
            sLfObjDelegate = typeof(DreamsAndPromisesManager).GetMethod("ObjectDestroyedDelegate", inst, null, new Type[] { typeof(Event) }, null);
            if (sLfObjListener != null && sLfObjDelegate != null)
            {
                Patch(typeof(DreamsAndPromisesManager), "CreateFromExportData", new Type[] { typeof(Sim), typeof(DnPExportData) }, typeof(LfDnp), "ExportHook", "ExportOrig");
                Patch(typeof(DreamsAndPromisesManager), "CreateAndIntitForSim", new Type[] { typeof(Sim) }, typeof(LfDnp), "InitHook", "InitOrig");
            }
            else AppliedLog().Add("dnpdisposelistener: members not found, not patched");
            Patch(typeof(EventTracker), "AddListener", new Type[] { typeof(EventListener) }, typeof(LfSeason), "AddHook", "AddOrig");
        }

        static void LfFixManager(Sim actor)
        {
            int[] c = PatchCounters();
            c[2254]++;
            try
            {
                DreamsAndPromisesManager m = actor == null ? null : actor.DreamsAndPromisesManager;
                if (m == null) return;
                EventListener l = sLfObjListener.GetValue(m) as EventListener;
                if (l == null || l.EventId != EventTypeId.kDnPSubjectDestroyed) return;
                if (!ShortcutOn("dnpdisposelistener")) return;
                EventTracker.RemoveListener(l);
                ProcessEventDelegate d = (ProcessEventDelegate)Delegate.CreateDelegate(typeof(ProcessEventDelegate), m, sLfObjDelegate);
                sLfObjListener.SetValue(m, EventTracker.AddListener(EventTypeId.kEventObjectDisposed, d));
                c[2255]++;
            }
            catch (Exception) { c[2256]++; }
        }

        public static class LfDnp
        {
            public static void ExportHook(Sim actor, DnPExportData data) { ExportOrig(actor, data); LfFixManager(actor); }
            public static void ExportOrig(Sim actor, DnPExportData data) { }
            public static void InitHook(Sim actor) { InitOrig(actor); LfFixManager(actor); }
            public static void InitOrig(Sim actor) { }
        }

        public static class LfSeason
        {
            public static EventListener AddHook(EventListener toadd)
            {
                try
                {
                    DelegateListener dl = toadd as DelegateListener;
                    if (dl != null && dl.Delegate != null && dl.Delegate.Target is Motives && EventTracker.ContainsListener(toadd))
                    {
                        int[] c = PatchCounters();
                        c[2257]++;
                        if (ShortcutOn("seasondup")) { c[2258]++; return toadd; }
                    }
                }
                catch (Exception) { PatchCounters()[2259]++; }
                return AddOrig(toadd);
            }
            public static EventListener AddOrig(EventListener toadd) { return null; }
        }
    }
}
