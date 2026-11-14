using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // errortrapsims (opt-in until measured): NRaas ErrorTrap's CheckSim.PrePerform adds every Sim object it checks to
    // CheckSim.mInited (Dictionary<Sim, bool>) and returns true whether or not the key was there. Nothing else reads or
    // removes a key (NRaasErrorTrap IL: PrePerform and the constructor are its only users), so the entry changes nothing
    // ErrorTrap does. It does keep every destroyed Sim object, and the object graph behind it (queue, motives, autonomy,
    // interactions), alive for the whole session. A townie gets a new Sim object at each lot visit, so they pile up:
    // al331 (180 min soak, ~50 min in) had 1,521 of its 1,587 dead Sim objects as keys there, and with that one link left
    // out of the trace only 10 of 60 sampled dead Sims still reached a GC root (59 of 60 with it).
    // Once per sim hour the keys of dead Sim objects are dropped: destroyed, or the description now names another Sim
    // object. The CheckSim instance is the one NRaas.Common+DerivativeSearch caches in sItems. ErrorTrap's checks are not
    // saved, so this is not a save change. Counters: [2067] keys dropped, [2068] keys left after the last pass.
    public static partial class Fixes
    {
        static AlarmHandle sEtSimsAlarm = AlarmHandle.kInvalidHandle;
        static FieldInfo sEtItems, sEtInited;  // NRaas.Common+DerivativeSearch.sItems (static), CheckSim.mInited
        static Type sEtCheckSim;
        static bool sEtSimsTried, sEtSimsThrew;

        // Called on every world load (Instantiator). NeverPersisted: not written into the save.
        public static void StartErrorTrapSims()
        {
            if (AlarmManager.Global == null) return;
            if (sEtSimsAlarm != AlarmHandle.kInvalidHandle) AlarmManager.Global.RemoveAlarm(sEtSimsAlarm);
            sEtSimsAlarm = AlarmManager.Global.AddAlarmRepeating(1f, TimeUnit.Hours, HourlyPasses, 1f, TimeUnit.Hours, "Ts3Fixes.ErrorTrapSims", AlarmType.NeverPersisted, null);
        }

        // The hourly sweep passes, in their old order. With the alarmtime test switch on, each pass that takes >= 3 ms is
        // logged with its time (alarmtime itself only sees the whole callback: ~24 ms, max 32 ms an hour, phase-20261114-0032).
        static readonly AlarmTimerCallback[] sHourlyPasses = { ErrorTrapSimsPass, CelebDeadSimPass, SeasonsDeadSimPass, GhostReactDeadSimPass, CasUiHandlersPass, ErrorTrapLookupPass, ErrorTrapCtlPass, DreamNodeDeadSimPass, DnpSubjectCachePass, OverwatchDeadSimPass, SimRefsDeadSimPass, OutfitWaitSavedPass, DeadSimCountPass, TinyUiEventRegPass, CustomCtlSweepPass, ExitCbSweepPass, DnpRemovalKeysPass };
        static int sHourlyLogged;

        // Each pass runs in its own try: AlarmManager.Simulate removes a repeating alarm for good when its callback throws
        // [verified, decompiled: catch (Exception) { if (repeating) InternalRemoveAlarm }], so one throwing pass used to stop
        // all 17 sweeps until the game restarts, silently. A throwing pass is now logged once (fix_applied log) and skipped.
        static Dictionary<string, bool> sHourlyThrew = new Dictionary<string, bool>();

        static void HourlyRun(AlarmTimerCallback pass)
        {
            try { pass(); }
            catch (ResetException) { throw; }
            catch (Exception e)
            {
                string n = pass.Method.Name;
                if (!sHourlyThrew.ContainsKey(n)) { sHourlyThrew[n] = true; AppliedLog().Add("hourly sweep: " + n + " threw " + e.GetType().Name + ": " + e.Message); }
            }
        }

        static void HourlyPasses()
        {
            bool timed = ShortcutOn("alarmtime") && sHourlyLogged < 400;
            foreach (AlarmTimerCallback pass in sHourlyPasses)
            {
                if (!timed) { HourlyRun(pass); continue; }
                DateTime t0 = DateTime.UtcNow;
                HourlyRun(pass);
                double ms = (DateTime.UtcNow - t0).TotalMilliseconds;
                if (ms >= 3) { sHourlyLogged++; HcLine("hourlypass: " + pass.Method.Name + " " + (int)ms + " ms, " + HcSimTime()); }
            }
        }

        static void ErrorTrapSimsPass()
        {
            try
            {
                if (!ShortcutOn("errortrapsims") || !EtSimsFind()) return;
                int[] c = PatchCounters();
                int left = 0;
                List<object> seen = new List<object>();  // one check can sit in several cached lists
                foreach (DictionaryEntry kv in (IDictionary)sEtItems.GetValue(null))
                {
                    IList items = kv.Value as IList;
                    if (items == null) continue;
                    foreach (object o in items)
                    {
                        if (o == null || o.GetType() != sEtCheckSim || seen.Contains(o)) continue;
                        seen.Add(o);
                        IDictionary inited = sEtInited.GetValue(o) as IDictionary;
                        if (inited == null) continue;
                        List<object> drop = new List<object>();
                        foreach (object k in inited.Keys) if (EtSimGone(k as Sim)) drop.Add(k);
                        foreach (object k in drop) inited.Remove(k);
                        c[2067] += drop.Count;
                        left += inited.Count;
                    }
                }
                c[2068] = left;
            }
            catch (Exception e)
            {
                if (!sEtSimsThrew) { sEtSimsThrew = true; AppliedLog().Add("errortrapsims: pass threw " + e.Message); }
            }
        }

        // errortraplookup (issue #40; this hourly alarm, own switch; not save data): NRaas ErrorTrap's
        // ObjectLookup.GetReference caches its answer per object in the static sLookup (object -> GameObjectReference, which
        // holds a parent object). ObjectLookup.Clear(), run after each object-graph scan, empties sRawList (what the answers
        // are built from) and sPostDisposed but not sLookup, so the cache keeps every object it was ever asked about, dead Sims
        // and their parents included, with answers from an old scan. al334 (58 min, the four leak switches on): the root of 30
        // of 56 sampled dead Sim objects. The pass empties sLookup as Clear() would have; the next GetReference builds a fresh
        // answer from the current sRawList. Counter: [2078] cache entries dropped.
        static FieldInfo sEtLookup;
        static bool sEtLookupTried, sEtLookupThrew;

        static void ErrorTrapLookupPass()
        {
            try
            {
                if (!ShortcutOn("errortraplookup")) return;
                if (!sEtLookupTried)
                {
                    sEtLookupTried = true;
                    Type ol = null;
                    foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                        if (a.GetName().Name == "NRaasErrorTrap") { ol = a.GetType("NRaas.ErrorTrapSpace.ObjectLookup"); break; }
                    FieldInfo f = ol == null ? null : ol.GetField("sLookup", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    sEtLookup = f != null && typeof(IDictionary).IsAssignableFrom(f.FieldType) ? f : null;
                    AppliedLog().Add("errortraplookup: " + (ol == null ? "ErrorTrap not loaded, off" : sEtLookup != null ? "ok" : "ErrorTrap's ObjectLookup differs from the known build, off"));
                }
                if (sEtLookup == null) return;
                IDictionary d = sEtLookup.GetValue(null) as IDictionary;
                if (d == null || d.Count == 0) return;
                PatchCounters()[2078] += d.Count;
                d.Clear();
            }
            catch (Exception e)
            {
                if (!sEtLookupThrew) { sEtLookupThrew = true; AppliedLog().Add("errortraplookup: pass threw " + e.Message); }
            }
        }

        // errortrapctl (issue #40; this hourly alarm, own switch; not save data): NRaas ErrorTrap's dereference scan, run once
        // per world load (OnDelayedWorldLoadFinished), hands each IDereferenceController the per-type ObjectLookup.Item lists
        // of the whole object graph (child, parent, field). The controllers are the instances DerivativeSearch.sItems caches,
        // and they drop those lists only in Clear(), which ErrorTrap calls when the next scan starts, never when one ends.
        // So every object alive at the last world load, and every parent that pointed at it, stays alive until the next
        // load: al341 (63 min, no travel, the leak switches on) had 228 of 288 Sim objects dead, 24 of 40 sampled rooted
        // DerivativeSearch.sItems > Pair[] > List > Item.mParent; al352 (3 trips to France) 36-42 of 60 sampled. The pass
        // calls each cached controller's Clear() (drops mList, and DereferenceSimDescription.mSims and the FollowRouteAction
        // and GameObject tables; nothing else), only while ObjectLookup.sDisposedEvent is null: that listener is added at
        // OnWorldLoadFinished and removed by ObjectLookup.Clear() when the scan ends, so no scan is pending or running.
        // It then empties mList itself: DereferenceSimDescription.Clear() overrides the base without base.Clear() (it only
        // nulls mSims), so its mList, the SimDescription item lists, was never emptied by ErrorTrap or by Clear() and gained
        // one load's lists at every world load (al357 window 1, 3 sim hours after trip 1 home: 12 of 30 sampled dead Sims
        // via DereferenceSimDescription.mList > Pair[] > List > Item.mParent, 10 of them through nothing else).
        // Counter: [2081] item lists dropped.
        static FieldInfo sEtDisposedEvent;
        static Type sEtCtl, sEtCtlBase;  // IDereferenceController, DereferenceController<T> (declares mList)
        static MethodInfo sEtCtlClear;
        delegate void EtCtlClearFn();
        static bool sEtCtlTried, sEtCtlThrew;

        static void ErrorTrapCtlPass()
        {
            try
            {
                if (!ShortcutOn("errortrapctl")) return;
                if (!sEtCtlTried)
                {
                    sEtCtlTried = true;
                    Assembly et = null;
                    foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                        if (a.GetName().Name == "NRaasErrorTrap") { et = a; break; }
                    const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic;
                    Type ol = et == null ? null : et.GetType("NRaas.ErrorTrapSpace.ObjectLookup");
                    Type ds = et == null ? null : et.GetType("NRaas.Common+DerivativeSearch");
                    sEtCtl = et == null ? null : et.GetType("NRaas.ErrorTrapSpace.Dereferences.Controllers.IDereferenceController");
                    sEtCtlBase = et == null ? null : et.GetType("NRaas.ErrorTrapSpace.Dereferences.Controllers.DereferenceController`1");
                    sEtDisposedEvent = ol == null ? null : ol.GetField("sDisposedEvent", any | BindingFlags.Static);
                    if (sEtItems == null && ds != null) sEtItems = ds.GetField("sItems", any | BindingFlags.Static);
                    sEtCtlClear = sEtCtl == null ? null : sEtCtl.GetMethod("Clear", Type.EmptyTypes);
                    bool ok = sEtDisposedEvent != null && sEtItems != null && sEtCtlClear != null && sEtCtlBase != null
                        && sEtCtlBase.GetField("mList", any | BindingFlags.Instance) != null && typeof(IDictionary).IsAssignableFrom(sEtItems.FieldType);
                    if (!ok) sEtCtlClear = null;
                    AppliedLog().Add("errortrapctl: " + (et == null ? "ErrorTrap not loaded, off" : ok ? "ok" : "ErrorTrap's dereference controllers differ from the known build, off"));
                }
                if (sEtCtlClear == null || sEtDisposedEvent.GetValue(null) != null) return;  // a scan is pending or running
                int dropped = 0;
                List<object> seen = new List<object>();  // one controller can sit in several cached lists
                foreach (DictionaryEntry kv in (IDictionary)sEtItems.GetValue(null))
                {
                    IList items = kv.Value as IList;
                    if (items == null) continue;
                    foreach (object o in items)
                    {
                        if (o == null || !sEtCtl.IsInstanceOfType(o) || seen.Contains(o)) continue;
                        seen.Add(o);
                        IList l = CtlList(o);
                        if (l == null || l.Count == 0) continue;
                        dropped += l.Count;
                        // a delegate, not MethodInfo.Invoke: swapinvoke flags any Invoke on a method named Clear (RefTable.cs swaps
                        // EA's); bound to the controller's own Clear so no interface dispatch is needed (EaFixes.cs PrefRemoveFn)
                        MethodInfo own = o.GetType().GetMethod("Clear", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                        ((EtCtlClearFn)Delegate.CreateDelegate(typeof(EtCtlClearFn), o, own ?? sEtCtlClear))();
                        l.Clear();  // the base Clear() is only mList.Clear(); DereferenceSimDescription skips it
                    }
                }
                PatchCounters()[2081] += dropped;
            }
            catch (Exception e)
            {
                if (!sEtCtlThrew) { sEtCtlThrew = true; AppliedLog().Add("errortrapctl: pass threw " + e.Message); }
            }
        }

        // A controller's mList, read on its closed DereferenceController<T> base type (a private field of a generic base).
        static IList CtlList(object ctl)
        {
            for (Type t = ctl.GetType(); t != null; t = t.BaseType)
                if (t.IsGenericType && t.GetGenericTypeDefinition() == sEtCtlBase)
                {
                    FieldInfo f = t.GetField("mList", BindingFlags.NonPublic | BindingFlags.Instance);
                    return f == null ? null : f.GetValue(ctl) as IList;
                }
            return null;
        }

        static bool EtSimGone(Sim s) { return SimGone(s) || s.SimDescription == null || s.SimDescription.CreatedSim != s; }

        // First pass with the switch on: find ErrorTrap's cache and CheckSim's field (all script assemblies are loaded by then).
        static bool EtSimsFind()
        {
            if (sEtSimsTried) return sEtInited != null;
            sEtSimsTried = true;
            Assembly et = null;
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                if (a.GetName().Name == "NRaasErrorTrap") { et = a; break; }
            const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic;
            Type ds = et == null ? null : et.GetType("NRaas.Common+DerivativeSearch");
            sEtCheckSim = et == null ? null : et.GetType("NRaas.ErrorTrapSpace.Checks.CheckSim");
            sEtItems = ds == null ? null : ds.GetField("sItems", any | BindingFlags.Static);
            FieldInfo f = sEtCheckSim == null ? null : sEtCheckSim.GetField("mInited", any | BindingFlags.Instance);
            bool ok = sEtItems != null && f != null && typeof(IDictionary).IsAssignableFrom(sEtItems.FieldType) && typeof(IDictionary).IsAssignableFrom(f.FieldType);
            sEtInited = ok ? f : null;
            AppliedLog().Add("errortrapsims: " + (et == null ? "ErrorTrap not loaded, off" : ok ? "ok" : "ErrorTrap's CheckSim differs from the known build, off"));
            return ok;
        }
    }
}
