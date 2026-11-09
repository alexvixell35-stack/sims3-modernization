using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.CelebritySystem;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.EventSystem;
using Sims3.Gameplay.Interfaces;

namespace Ts3Fixes
{
    // celebritydeadsim (issue #40, part 2; runs in errortrapsims' hourly alarm under its own switch; not save data):
    // CelebrityManager.RegisterForCelebrityEvents (from Sim.OnStartup) stores two EventListeners whose ScriptObject is that
    // Sim object; UnregisterCelebrityEvents runs only at the next registration or at SimDescription.Dispose, and Sim.Dispose
    // does not call it. So between lot visits each description keeps its last, destroyed Sim object (~19 KiB plus its
    // graph). The tracker itself no longer lists them (EventTracker.RemoveSimReferences at Sim.Dispose), so they get no
    // events. The pass calls EA's own UnregisterCelebrityEvents (RemoveListener is a no-op for a missing key, then both
    // fields go null) when the listener's Sim is destroyed or is no longer the description's Sim. The next Sim.OnStartup
    // registers again, as EA does. Counter: [2072] managers unregistered.
    public static partial class Fixes
    {
        static FieldInfo sCelebAct, sCelebDis;
        static MethodInfo sCelebUnreg;
        static bool sCelebTried, sCelebThrew;

        static void CelebDeadSimPass()
        {
            try
            {
                if (!ShortcutOn("celebritydeadsim") || Household.sHouseholdList == null) return;
                if (!sCelebTried)
                {
                    sCelebTried = true;
                    const BindingFlags inst = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                    Type t = typeof(CelebrityManager);
                    sCelebAct = t.GetField("mCelebrityActivityEventListener", inst);
                    sCelebDis = t.GetField("mDisgracefulActionEventListener", inst);
                    sCelebUnreg = t.GetMethod("UnregisterCelebrityEvents", inst, null, Type.EmptyTypes, null);
                    if (sCelebAct == null || sCelebDis == null || sCelebUnreg == null) sCelebUnreg = null;
                    AppliedLog().Add("celebritydeadsim: " + (sCelebUnreg != null ? "ok" : "CelebrityManager differs from the known build, off"));
                }
                if (sCelebUnreg == null) return;
                // household rule (CLAUDE.md): never touch the active household's events; no active household = unknown scope, skip all
                Household act = Household.ActiveHousehold;
                if (act == null) return;
                int[] c = PatchCounters();
                foreach (Household h in new List<Household>(Household.sHouseholdList))
                {
                    if (h == null || h == act) continue;
                    foreach (SimDescription d in h.AllSimDescriptions)
                    {
                        CelebrityManager m = d == null ? null : d.CelebrityManager;
                        if (m == null) continue;
                        if (!CelebHoldsDeadSim(d, sCelebAct.GetValue(m) as EventListener) && !CelebHoldsDeadSim(d, sCelebDis.GetValue(m) as EventListener)) continue;
                        sCelebUnreg.Invoke(m, null);
                        c[2072]++;
                    }
                }
            }
            catch (Exception e)
            {
                if (!sCelebThrew) { sCelebThrew = true; AppliedLog().Add("celebritydeadsim: pass threw " + e.Message); }
            }
        }

        static bool CelebHoldsDeadSim(SimDescription d, EventListener l)
        {
            Sim s = l == null ? null : l.ScriptObject as Sim;
            return s != null && (SimGone(s) || d.CreatedSim != s);
        }

        // seasonsdeadsim (issue #40; same alarm, own switch; not save data): Motives.CreateMotives and Motives.OnLoadFixup
        // add a global kSeasonTransition listener (no ScriptObject, so EventTracker.RemoveSimReferences never sees it) whose
        // delegate target is that Motives, which holds mSim. EA removes it only from OnSeasonTransition at the next season
        // change (mSim or its Autonomy gone) or in OnLoadFixup without Seasons. Sim.Dispose does not, and
        // Autonomy.RecreateAllMotives makes a new Motives without removing the old one's listener, which then stays for good
        // (its Sim lives, so OnSeasonTransition keeps it). al331 at ~100 min: 577 such listeners on dead Sims, each pinning
        // that Sim object. The pass removes, with EA's EventTracker.RemoveListener (deferred by EA while the event is being
        // sent), the listeners whose Motives belongs to a gone Sim (errortrapsims' rule: destroyed, or not its description's Sim) [2073] or is no longer its live Sim's Motives [2074],
        // and clears that Motives' field. The Sim's current Motives keeps its own listener.
        static FieldInfo sEvtListeners, sMotSim, sMotSeasonL;
        static bool sSeasTried, sSeasThrew;

        static void SeasonsDeadSimPass()
        {
            try
            {
                if (!ShortcutOn("seasonsdeadsim")) return;
                if (!sSeasTried)
                {
                    sSeasTried = true;
                    const BindingFlags inst = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                    Type mt = typeof(Sims3.Gameplay.Autonomy.Motives);
                    FieldInfo f = typeof(EventTracker).GetField("mListeners", inst);
                    sMotSim = mt.GetField("mSim", inst);
                    sMotSeasonL = mt.GetField("mSeasonsChangedEventListener", inst);
                    sEvtListeners = f != null && sMotSim != null && sMotSeasonL != null && typeof(IDictionary).IsAssignableFrom(f.FieldType) ? f : null;
                    AppliedLog().Add("seasonsdeadsim: " + (sEvtListeners != null ? "ok" : "EventTracker or Motives differs from the known build, off"));
                }
                EventTracker et = EventTracker.Instance;
                IDictionary byEvent = sEvtListeners == null || et == null ? null : sEvtListeners.GetValue(et) as IDictionary;
                object k = unchecked((ulong)(long)EventTypeId.kSeasonTransition);  // EA's key: (ulong)(long)event id
                IDictionary byKey = byEvent == null || !byEvent.Contains(k) ? null : byEvent[k] as IDictionary;
                if (byKey == null) return;
                int[] c = PatchCounters();
                List<EventListener> drop = new List<EventListener>();
                List<Sims3.Gameplay.Autonomy.Motives> owners = new List<Sims3.Gameplay.Autonomy.Motives>();
                int dead = 0, stale = 0;
                foreach (object list in byKey.Values)
                {
                    IList ls = list as IList;
                    if (ls == null) continue;
                    foreach (object o in ls)
                    {
                        DelegateListener dl = o as DelegateListener;
                        Sims3.Gameplay.Autonomy.Motives m = dl == null || dl.Delegate == null ? null : dl.Delegate.Target as Sims3.Gameplay.Autonomy.Motives;
                        if (m == null) continue;
                        Sim s = sMotSim.GetValue(m) as Sim;
                        if (s == null || EtSimGone(s)) dead++;
                        else if (s.Autonomy != null && s.Motives != m) stale++;
                        else continue;
                        drop.Add(dl);
                        owners.Add(m);
                    }
                }
                for (int i = 0; i < drop.Count; i++)  // after the walk: RemoveListener changes these lists
                {
                    EventTracker.RemoveListener(drop[i]);
                    if (sMotSeasonL.GetValue(owners[i]) == drop[i]) sMotSeasonL.SetValue(owners[i], null);
                }
                c[2073] += dead;
                c[2074] += stale;
            }
            catch (Exception e)
            {
                if (!sSeasThrew) { sSeasThrew = true; AppliedLog().Add("seasonsdeadsim: pass threw " + e.Message); }
            }
        }

        // ghostreactdeadsim (issue #40; errortrapsims' hourly alarm, own switch; not save data): Urnstone.GhostSetup gives a
        // ghost Sim a new SimsReactedToGhost list, Sim.GhostReaction.Run adds every Sim that reacts to it (its Test is
        // Contains, so each Sim object reacts once), and only Urnstone.GhostCleanup drops the list. A Sim that stays a ghost
        // keeps every Sim object that ever reacted, destroyed ones too (al331: dead Sims held this way by live ghosts). The
        // pass removes the destroyed ones (errortrapsims' rule). No change in play: Contains compares objects, and a Sim
        // instantiated again is a new object. Counter: [2075] Sim objects removed.
        static bool sGhostOk, sGhostThrew;

        static void GhostReactDeadSimPass()
        {
            try
            {
                if (!ShortcutOn("ghostreactdeadsim") || LotManager.Actors == null) return;
                if (!sGhostOk) { sGhostOk = true; AppliedLog().Add("ghostreactdeadsim: ok"); }
                // household rule (CLAUDE.md): household ghosts, the selected Sim and unknown scope (HhScope) keep their lists
                if (Household.ActiveHousehold == null) return;
                IActor sel = PlumbBob.SelectedActor;
                int[] c = PatchCounters();
                foreach (Sim s in new List<Sim>(LotManager.Actors))
                {
                    if (HhScope(s) || s == sel) continue;
                    List<Sim> l = s.SimsReactedToGhost;
                    if (l == null || l.Count == 0) continue;
                    c[2075] += l.RemoveAll(delegate (Sim x) { return x == null || EtSimGone(x); });
                }
            }
            catch (Exception e)
            {
                if (!sGhostThrew) { sGhostThrew = true; AppliedLog().Add("ghostreactdeadsim: pass threw " + e.Message); }
            }
        }
        // dreamnodedeadsim (issue #40; errortrapsims' hourly alarm, own switch; not save data): ActiveDreamNode.ResetListener
        // registers the node's trigger listener with no owner Sim when its primitive can be satisfied by a third party (a
        // child born or adopted, a Sim put out of a fire, social features), so EventTracker.RemoveSimReferences at Sim.Dispose
        // never sees it, and the listener's delegate target is the node, whose mOwner is that Sim object. Sim.Dispose ->
        // NullDnPManager -> ClearActiveNodeListeners removes the listeners of the nodes in mActiveNodes only. al334 (all leak
        // switches on): 56 such global listeners on gone Sim objects at 75 min, 91 at 140 min; at 148 min the root of 10 of
        // 40 sampled dead Sim objects (path: Sim <- ActiveDreamNode.mOwner <- ProcessEventDelegate <- DelegateListener). The
        // pass removes, with EA's ActiveNodeBase.RemoveListenerFromTracker (the tracker defers a removal while it sends that
        // event), the tracker's listeners whose node's Owner is gone (errortrapsims' rule), unless the description's current
        // Sim's manager still has that node in mActiveNodes. Counter: [2079] listeners removed.
        static FieldInfo sDnpEvt, sDnpActive, sSimDnp;
        static bool sDnpTried, sDnpThrew;

        static void DreamNodeDeadSimPass()
        {
            try
            {
                if (!ShortcutOn("dreamnodedeadsim")) return;
                const BindingFlags inst = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                if (!sDnpTried)
                {
                    sDnpTried = true;
                    FieldInfo f = typeof(EventTracker).GetField("mListeners", inst);
                    sDnpEvt = f != null && typeof(IDictionary).IsAssignableFrom(f.FieldType) ? f : null;
                    sDnpActive = typeof(Sims3.Gameplay.DreamsAndPromises.DreamsAndPromisesManager).GetField("mActiveNodes", inst);
                    sSimDnp = typeof(Sim).GetField("mDreamsAndPromisesManager", inst);
                    AppliedLog().Add("dreamnodedeadsim: " + (sDnpEvt != null && sDnpActive != null && sSimDnp != null ? "ok" : "EventTracker or DreamsAndPromisesManager differs from the known build, off"));
                }
                if (sDnpEvt == null || sDnpActive == null || sSimDnp == null) return;
                EventTracker et = EventTracker.Instance;
                IDictionary byEvent = et == null ? null : sDnpEvt.GetValue(et) as IDictionary;
                if (byEvent == null) return;
                List<Sims3.Gameplay.DreamsAndPromises.ActiveNodeBase> drop = new List<Sims3.Gameplay.DreamsAndPromises.ActiveNodeBase>();
                foreach (object byKey in byEvent.Values)
                {
                    IDictionary d = byKey as IDictionary;
                    if (d == null) continue;
                    foreach (object list in d.Values)
                    {
                        IList ls = list as IList;
                        if (ls == null) continue;
                        foreach (object o in ls)
                        {
                            DelegateListener dl = o as DelegateListener;
                            Sims3.Gameplay.DreamsAndPromises.ActiveNodeBase n = dl == null || dl.Delegate == null ? null : dl.Delegate.Target as Sims3.Gameplay.DreamsAndPromises.ActiveNodeBase;
                            if (n == null || drop.Contains(n)) continue;
                            Sim s = n.Owner;
                            if (s == null || !EtSimGone(s)) continue;
                            Sim now = s.SimDescription == null ? null : s.SimDescription.CreatedSim;
                            object mgr = now == null || now == s ? null : sSimDnp.GetValue(now);
                            IList act = mgr == null ? null : sDnpActive.GetValue(mgr) as IList;
                            if (act != null && act.Contains(n)) continue;  // a live manager still runs this node
                            drop.Add(n);
                        }
                    }
                }
                foreach (Sims3.Gameplay.DreamsAndPromises.ActiveNodeBase n in drop) n.RemoveListenerFromTracker();
                PatchCounters()[2079] += drop.Count;
            }
            catch (Exception e)
            {
                if (!sDnpThrew) { sDnpThrew = true; AppliedLog().Add("dreamnodedeadsim: pass threw " + e.Message); }
            }
        }
        // overwatchdeadsim (issue #40; errortrapsims' hourly alarm, own switch; not save data): NRaas Overwatch's stuck check
        // (StuckCheck.PrivatePerformAction) keeps a StuckSimData per SimDescriptionId in the static tables sData and
        // sRouteData, and removes one only when it looks at that Sim again. A Sim that died or left the world is never looked
        // at again, so its entry stays for the session and its mLastInteraction keeps the interaction and its InstanceActor,
        // the gone Sim object. al334 at 170 min: the root of 14 of 60 sampled dead Sim objects (path: Sim <-
        // GoToVirtualHomeInternal.mInstanceActor <- StuckSimData.mLastInteraction <- sData). The pass removes the entries whose
        // last interaction's Sim is gone (errortrapsims' rule); Overwatch adds a fresh entry if it sees that Sim's description
        // in the world again. Counter: [2080] entries removed.
        static FieldInfo[] sOwTables;
        static FieldInfo sOwLastIa;
        static bool sOwTried, sOwThrew;

        static void OverwatchDeadSimPass()
        {
            try
            {
                if (!ShortcutOn("overwatchdeadsim")) return;
                if (!sOwTried)
                {
                    sOwTried = true;
                    Type sc = null, sd = null;
                    foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                        if (a.GetName().Name == "NRaasOverwatch")
                        {
                            sc = a.GetType("NRaas.OverwatchSpace.Alarms.StuckCheck");
                            sd = a.GetType("NRaas.OverwatchSpace.Alarms.StuckCheck+StuckSimData");
                            break;
                        }
                    const BindingFlags st = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                    FieldInfo t1 = sc == null ? null : sc.GetField("sData", st), t2 = sc == null ? null : sc.GetField("sRouteData", st);
                    FieldInfo li = sd == null ? null : sd.GetField("mLastInteraction", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    bool ok = t1 != null && t2 != null && li != null && typeof(IDictionary).IsAssignableFrom(t1.FieldType) && typeof(IDictionary).IsAssignableFrom(t2.FieldType);
                    if (ok) { sOwTables = new FieldInfo[] { t1, t2 }; sOwLastIa = li; }
                    AppliedLog().Add("overwatchdeadsim: " + (sc == null ? "Overwatch not loaded, off" : ok ? "ok" : "Overwatch's StuckCheck differs from the known build, off"));
                }
                if (sOwTables == null) return;
                int n = 0;
                foreach (FieldInfo f in sOwTables)
                {
                    IDictionary d = f.GetValue(null) as IDictionary;
                    if (d == null || d.Count == 0) continue;
                    List<object> drop = new List<object>();
                    foreach (DictionaryEntry e in d)
                    {
                        Sims3.Gameplay.Interactions.InteractionInstance ia = e.Value == null ? null : sOwLastIa.GetValue(e.Value) as Sims3.Gameplay.Interactions.InteractionInstance;
                        Sim s = ia == null ? null : ia.InstanceActor;
                        if (s != null && EtSimGone(s)) drop.Add(e.Key);
                    }
                    foreach (object k in drop) d.Remove(k);
                    n += drop.Count;
                }
                PatchCounters()[2080] += n;
            }
            catch (Exception e)
            {
                if (!sOwThrew) { sOwThrew = true; AppliedLog().Add("overwatchdeadsim: pass threw " + e.Message); }
            }
        }
    }
}
