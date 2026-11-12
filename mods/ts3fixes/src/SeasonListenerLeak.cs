using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.EventSystem;

namespace Ts3Fixes
{
    // seasonlistener (tuning kSeasonListener, default off until measured; docs/545 "Motives").
    //
    // EA bug [verified, decompiled Sims3GameplaySystems 1.67]: Motives keeps one kSeasonTransition listener
    // (mSeasonsChangedEventListener, a global listener with no owner). Two paths leave extra copies:
    //  1. Autonomy.RecreateAllMotives (age up / age down, some trait changes, time traveler) builds a new Motives and
    //     drops the old one without removing its listener. The old Motives still has a live Sim, so its
    //     OnSeasonTransition never takes its own "mSim / Autonomy gone" exit; it stays subscribed for the session, and on
    //     every season change its FixupSeasonAppropriatenessMotives calls CreateMotive / RemoveMotive, which add and
    //     remove Desires in the Sim's live InteractionScorer for a Motives the Sim no longer uses [inferred: one extra
    //     season Desire per stale Motives].
    //  2. Motives.RecreateMotives calls CreateMotives again, which re-adds the same listener object
    //     (EventTracker.AddListener(mSeasonsChangedEventListener)); EventTracker's list does not dedupe, so each call adds
    //     one more copy and the handler runs once per copy.
    //  3. Autonomy.Dispose (Sim teardown) leaves the listener too; it is only dropped at the next season change, when
    //     OnSeasonTransition finds Autonomy null, and until then it keeps the destroyed Sim reachable (measured
    //     2026-11-12: season listeners 128 -> 328 while instantiated Sims went 54 -> 89 and back to 75).
    // The pass: (3) after Autonomy.Dispose the listener is removed, as OnSeasonTransition would at the next season;
    // (1) after RecreateAllMotives, the old Motives' listener is removed with EventTracker.RemoveListener and
    // its field nulled (what OnSeasonTransition does when its Sim is gone); (2) after RecreateMotives, a copy the call
    // added to a listener that was already subscribed is removed again, leaving exactly the one copy it had. Lists being
    // dispatched at that moment are skipped. No motive, Desire or Sim is touched directly.
    // Counters: [2290] stale, duplicate or disposed-Sim listeners found (also off), [2291] removed, [2292] errors or skipped.
    public static partial class Fixes
    {
        static void ApplySeasonListener()
        {
            if (sSllListeners == null || sSllActive == null || sSllMotivesListener == null) { AppliedLog().Add("seasonlistener: EventTracker members not found, not patched"); return; }
            Patch(typeof(Autonomy), "RecreateAllMotives", Type.EmptyTypes, typeof(SllAutHook), "RecreateAllHook", "RecreateAllOrig");
            Patch(typeof(Autonomy), "Dispose", Type.EmptyTypes, typeof(SllAutHook), "DisposeHook", "DisposeOrig");
            Patch(typeof(Motives), "RecreateMotives", new Type[] { typeof(Sim) }, typeof(SllMotHook), "RecreateHook", "RecreateOrig");
        }

        static readonly FieldInfo sSllListeners = typeof(EventTracker).GetField("mListeners", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        static readonly FieldInfo sSllActive = typeof(EventTracker).GetField("mActiveList", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        static readonly FieldInfo sSllMotivesListener = typeof(Motives).GetField("mSeasonsChangedEventListener", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        static EventListener SllGet(Motives m) { return m != null && sSllMotivesListener != null ? sSllMotivesListener.GetValue(m) as EventListener : null; }

        static List<EventListener> SllList(EventListener l)
        {
            EventTracker t = EventTracker.Instance;
            if (t == null || l == null || sSllListeners == null) return null;
            Dictionary<ulong, Dictionary<ulong, List<EventListener>>> all = sSllListeners.GetValue(t) as Dictionary<ulong, Dictionary<ulong, List<EventListener>>>;
            Dictionary<ulong, List<EventListener>> byKey;
            List<EventListener> list = null;
            if (all == null || !all.TryGetValue((ulong)l.EventId, out byKey) || !byKey.TryGetValue(l.Key, out list)) return null;
            return list;
        }

        static int SllCount(EventListener l)
        {
            List<EventListener> list = SllList(l);
            if (list == null) return 0;
            int n = 0;
            foreach (EventListener x in list) if (object.ReferenceEquals(x, l)) n++;
            return n;
        }

        static bool SllActive(EventListener l)
        {
            List<EventListener> list = SllList(l);
            EventTracker t = EventTracker.Instance;
            Stack<List<EventListener>> act = t != null && sSllActive != null ? sSllActive.GetValue(t) as Stack<List<EventListener>> : null;
            return list == null || act == null || act.Contains(list);  // unknown state counts as active: skip
        }

        public class SllAutHook
        {
            public void RecreateAllHook()
            {
                Autonomy a = (Autonomy)(object)this;
                Motives old = null;
                try { old = a.Motives; } catch (Exception) { }
                RecreateAllOrig();
                int[] c = PatchCounters();
                try
                {
                    if (old == null || object.ReferenceEquals(old, a.Motives)) return;
                    EventListener l = SllGet(old);
                    if (l == null || SllCount(l) == 0) return;
                    c[2290]++;
                    if (!ShortcutOn("seasonlistener")) return;
                    if (SllActive(l)) { c[2292]++; return; }
                    while (SllCount(l) > 0) EventTracker.RemoveListener(l);
                    sSllMotivesListener.SetValue(old, null);
                    c[2291]++;
                }
                catch (Exception) { c[2292]++; }
            }
            public void RecreateAllOrig() { } // receives Autonomy.RecreateAllMotives()'s original header

            // (3) Sim teardown (Sim.Destroy path: Autonomy.Dispose, then mAutonomy = null): the Motives listener stays
            // until the next season change, where OnSeasonTransition sees Autonomy == null and returns Remove; until
            // then it keeps the destroyed Sim reachable. Removing it here is what that handler does, done earlier.
            public void DisposeHook()
            {
                Autonomy a = (Autonomy)(object)this;
                DisposeOrig();
                int[] c = PatchCounters();
                try
                {
                    Motives m = a.Motives;
                    EventListener l = SllGet(m);
                    if (l == null || SllCount(l) == 0) return;
                    c[2290]++;
                    if (!ShortcutOn("seasonlistener")) return;
                    if (SllActive(l)) { c[2292]++; return; }
                    while (SllCount(l) > 0) EventTracker.RemoveListener(l);
                    sSllMotivesListener.SetValue(m, null);
                    c[2291]++;
                }
                catch (Exception) { c[2292]++; }
            }
            public void DisposeOrig() { } // receives Autonomy.Dispose()'s original header
        }

        public class SllMotHook
        {
            public void RecreateHook(Sim sim)
            {
                Motives m = (Motives)(object)this;
                EventListener l = null; int n0 = 0;
                try { l = SllGet(m); n0 = SllCount(l); } catch (Exception) { l = null; }
                RecreateOrig(sim);
                if (l == null || n0 == 0) return;
                int[] c = PatchCounters();
                try
                {
                    if (!object.ReferenceEquals(SllGet(m), l)) return;
                    int n1 = SllCount(l);
                    if (n1 <= n0) return;
                    c[2290] += n1 - n0;
                    if (!ShortcutOn("seasonlistener")) return;
                    if (SllActive(l)) { c[2292]++; return; }
                    for (int i = n0; i < n1; i++) EventTracker.RemoveListener(l);
                    c[2291] += n1 - n0;
                }
                catch (Exception) { c[2292]++; }
            }
            public void RecreateOrig(Sim sim) { } // receives Motives.RecreateMotives(Sim)'s original header
        }
    }
}
