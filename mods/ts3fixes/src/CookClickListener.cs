using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.EventSystem;
using Sims3.Gameplay.Objects.CookingObjects;
using Sims3.Gameplay.Utilities;

namespace Ts3Fixes
{
    // cookclicklistener (tuning kCookClickListener, off by default; docs/546 §17). EA bug fix (leak), exact for every Sim.
    //
    // CookingProcess.AddObjectListeners hooks the clicked appliance (ObjectClickedOn: the fridge, stove, grill, microwave
    // the cook started from): its ObjectDisposed event and a kObjectStateChanged listener whose target is that object
    // [verified, decompiled]. EA calls it in the cooking constructor, on load, and in the private copy constructor that
    // makes every serving (CreateServingContainer, group serving, single serving, single from group: the copy keeps
    // ObjectClickedOn). It removes them only in CompletedMakingARecipe (the original process) or when the appliance is
    // destroyed or changes state (GameObject.ObjectStateChanged: buy-mode move, repair). A serving that is eaten, washed
    // or thrown away is destroyed with its process still registered, so the appliance's listener list and ObjectDisposed
    // event keep one dead process (and through Owner the destroyed plate) per serving until the appliance breaks, moves
    // or the session ends. Measured (leak census): CookingProcess.ObjectClickedOnDestroyed listeners grow in every world,
    // 0 -> 132 in ~45 min (Bridgeport, phase-20261114-1635), 0 -> 50 (Appaloosa Plains, phase-20261114-1113).
    // Same code, second loophole [verified]: the Prepare steps (Pot, FryingPan, BakingDish, FoodTray, Microwave) set
    // ObjectClickedOn = null directly, the kObjectStateChanged handler nulls it and returns Remove without unhooking
    // ObjectDisposed, and FutureFoodSynthesizer re-points it without re-adding: in all three, ClearObjectListeners can no
    // longer unhook ObjectDisposed from the object it was added to, so that handler stays for the appliance's lifetime.
    //
    // The fix (switch on): every process AddObjectListeners hooks is noted with the object it hooked. A few noted entries
    // are checked on each later AddObjectListeners call (no alarm): a process whose Owner has been destroyed for
    // kCcDeadMinutes gets EA's own ClearObjectListeners. After any ClearObjectListeners, and for an entry whose state
    // listener EA already removed, ObjectDisposed is also unhooked from the object it was added to when ObjectClickedOn no
    // longer points there. A dead process's handlers only write its own fields, so removing them changes nothing any Sim
    // does; servings in inventories and fridges are live objects and keep theirs.
    // Counters: [2412] listeners of destroyed servings removed, [2413] ObjectDisposed handlers unhooked from the original
    // object, [2414] errors (registry fixes-2412). Harness check: CookClickCheck (ObjectLeakChecks.cs), repro
    // cook-click-listener-leak.
    public static partial class Fixes
    {
        static FieldInfo sCcListener;
        static MethodInfo sCcClear, sCcOnDisposed;

        sealed class CcEntry { public CookingProcess P; public GameObject Hooked; public long DeadSince; }
        static readonly List<CcEntry> sCcNoted = new List<CcEntry>();  // sim thread only
        static int sCcNext;
        const int kCcNotedMax = 4096, kCcCheckPerAdd = 8;
        const float kCcDeadMinutes = 10f;

        static void ApplyCookClickListener()
        {
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Type cp = typeof(CookingProcess);
            sCcListener = cp.GetField("mStateChangedListener", inst);
            sCcClear = cp.GetMethod("ClearObjectListeners", inst, null, Type.EmptyTypes, null);
            sCcOnDisposed = cp.GetMethod("ObjectClickedOnDestroyed", inst, null, new Type[] { typeof(GameObject) }, null);
            if (sCcListener == null || sCcClear == null || sCcOnDisposed == null) { AppliedLog().Add("cookclicklistener: CookingProcess members not found, not patched"); return; }
            Patch(cp, "AddObjectListeners", typeof(CcHook), "AddHook", "AddOrig");
            Patch(cp, "ClearObjectListeners", typeof(CcHook), "ClearHook", "ClearOrig");
        }

        public class CcHook
        {
            public void AddHook()
            {
                AddOrig();
                try { if (ShortcutOn("cookclicklistener")) CcNote((CookingProcess)(object)this); } catch (Exception) { PatchCounters()[2414]++; }
            }
            public void AddOrig() { } // receives CookingProcess.AddObjectListeners's original body

            public void ClearHook()
            {
                ClearOrig();
                try { if (sCcNoted.Count > 0) CcForget((CookingProcess)(object)this); } catch (Exception) { PatchCounters()[2414]++; }
            }
            public void ClearOrig() { } // receives CookingProcess.ClearObjectListeners's original body
        }

        static void CcNote(CookingProcess p)
        {
            GameObject hooked = p.ObjectClickedOn;
            if (hooked == null) return;  // EA added nothing
            CcEntry e = CcFind(p);
            if (e == null)
            {
                if (sCcNoted.Count >= kCcNotedMax) sCcNoted.RemoveAt(0);  // cap: the oldest stays as EA left it
                e = new CcEntry();
                e.P = p;
                sCcNoted.Add(e);
            }
            e.Hooked = hooked;
            e.DeadSince = 0;
            CcCheck(kCcCheckPerAdd, false);
        }

        static CcEntry CcFind(CookingProcess p)
        {
            foreach (CcEntry e in sCcNoted) if (ReferenceEquals(e.P, p)) return e;
            return null;
        }

        // After EA's ClearObjectListeners on p (its own paths or ours): drop the note and unhook ObjectDisposed from the
        // object it was added to when EA could not (ObjectClickedOn nulled or re-pointed since).
        static void CcForget(CookingProcess p)
        {
            for (int i = 0; i < sCcNoted.Count; i++)
            {
                if (!ReferenceEquals(sCcNoted[i].P, p)) continue;
                CcEntry e = sCcNoted[i];
                sCcNoted.RemoveAt(i);
                if (i < sCcNext) sCcNext--;
                CcUnhookDisposed(e);
                return;
            }
        }

        static void CcUnhookDisposed(CcEntry e)
        {
            if (e.Hooked == null || ReferenceEquals(e.Hooked, e.P.ObjectClickedOn)) return;
            e.Hooked.ObjectDisposed -= (GameObject.ObjectDisposedEvent)Delegate.CreateDelegate(typeof(GameObject.ObjectDisposedEvent), e.P, sCcOnDisposed);
            e.Hooked = null;
            PatchCounters()[2413]++;
        }

        // Checks up to n noted processes (round robin). force: no kCcDeadMinutes wait (harness check only). Returns the
        // number of listeners removed.
        internal static int CcCheck(int n, bool force)
        {
            long now = SimClock.CurrentTicks, wait = SimClock.ConvertToTicks(kCcDeadMinutes, TimeUnit.Minutes);
            List<CcEntry> clear = null, gone = null;
            for (int k = 0; k < n && sCcNoted.Count > 0; k++)
            {
                if (sCcNext >= sCcNoted.Count) sCcNext = 0;
                CcEntry e = sCcNoted[sCcNext++];
                if (sCcListener.GetValue(e.P) == null)
                {
                    // EA removed the state listener itself (kObjectStateChanged handler) without unhooking ObjectDisposed
                    if (gone == null) gone = new List<CcEntry>();
                    if (!gone.Contains(e)) gone.Add(e);
                    continue;
                }
                GameObject owner = e.P.Owner as GameObject;
                if (owner == null || !owner.HasBeenDestroyed) { e.DeadSince = 0; continue; }  // no owner yet / live serving
                if (e.DeadSince == 0) e.DeadSince = now;
                if (!force && now - e.DeadSince < wait) continue;
                if (clear == null) clear = new List<CcEntry>();
                if (!clear.Contains(e)) clear.Add(e);
            }
            if (gone != null)
                foreach (CcEntry e in gone)
                {
                    int i = sCcNoted.IndexOf(e);
                    if (i < 0) continue;
                    sCcNoted.RemoveAt(i);
                    if (i < sCcNext) sCcNext--;
                    CcUnhookDisposed(e);
                }
            int removed = 0;
            if (clear != null)
                foreach (CcEntry e in clear)
                {
                    CcClear(e.P);  // EA's ClearObjectListeners (hooked: CcForget drops the note)
                    PatchCounters()[2412]++;
                    removed++;
                }
            return removed;
        }

        // kObjectStateChanged listeners whose handler belongs to a CookingProcess, split by the process's Owner: destroyed,
        // none, live. Read-only; used by CookClickCheck.
        internal static int[] CcCensus()
        {
            int[] r = new int[3];
            Dictionary<ulong, List<EventListener>> byKey = CcStateListeners();
            if (byKey == null) return r;
            foreach (List<EventListener> list in byKey.Values)
            {
                if (list == null) continue;
                foreach (EventListener l in list)
                {
                    DelegateListener dl = l as DelegateListener;
                    CookingProcess p = dl != null && dl.Delegate != null ? dl.Delegate.Target as CookingProcess : null;
                    if (p == null) continue;
                    GameObject owner = p.Owner as GameObject;
                    if (owner == null) r[1]++;
                    else if (owner.HasBeenDestroyed) r[0]++;
                    else r[2]++;
                }
            }
            return r;
        }

        // A swapped method cannot be called through MethodInfo.Invoke with a target (TargetException, tools/selftest/swapinvoke):
        // EA's ClearObjectListeners is called through a delegate bound to the process.
        delegate void CcClearFn();
        internal static void CcClear(CookingProcess p)
        {
            ((CcClearFn)Delegate.CreateDelegate(typeof(CcClearFn), p, sCcClear))();
        }

        internal static bool CcHasStateListener(CookingProcess p)
        {
            return sCcListener != null && sCcListener.GetValue(p) != null;
        }

        static Dictionary<ulong, List<EventListener>> CcStateListeners()
        {
            EventTracker et = EventTracker.Instance;
            FieldInfo f = typeof(EventTracker).GetField("mListeners", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Dictionary<ulong, Dictionary<ulong, List<EventListener>>> all = et == null || f == null ? null : f.GetValue(et) as Dictionary<ulong, Dictionary<ulong, List<EventListener>>>;
            Dictionary<ulong, List<EventListener>> byKey;
            return all != null && all.TryGetValue((ulong)EventTypeId.kObjectStateChanged, out byKey) ? byKey : null;
        }
    }
}
