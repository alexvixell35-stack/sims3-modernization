using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.EventSystem;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Objects;
using Sims3.Gameplay.Socializing;
using Sims3.Gameplay.ThoughtBalloons;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Agent
{
    // leak_census {top?: 15} (docs/191 "leak_census (2026-11-10)"): read only. One pass over the registries that most often
    // grow in a long session, so a test can diff two calls: alarms, EventTracker listeners, objects, situations,
    // relationships, SimDescriptions (households, relationship keys, urnstones, genealogy, MiniSims), UI wrapper dicts,
    // reaction broadcasters, socials (conversations, SocialComponent and ThoughtBalloonManager per-Sim collections, social
    // interactions in queues and situations) and a list of static collections. Private fields are read by reflection (the
    // FieldInfo is cached per type); each section has its own try/catch, so a failure only puts an "error" string in that
    // section. "gone" below means: null, a GameObject with HasBeenDestroyed, or a SimDescription with !IsValidDescription
    // (SimDescription.Dispose clears that flag [verified decompiled]).
    public static partial class Handlers
    {
        static void RegisterLeakCensus()
        {
            Dispatcher.Register("leak_census", LeakCensus);
        }

        static readonly Dictionary<string, FieldInfo> sLcFields = new Dictionary<string, FieldInfo>();
        static readonly Dictionary<Type, List<FieldInfo>> sLcCollFields = new Dictionary<Type, List<FieldInfo>>();
        static readonly Dictionary<Type, List<FieldInfo>> sLcSimFields = new Dictionary<Type, List<FieldInfo>>();
        static Type sLcUiManager;
        static bool sLcUiSearched;
        const BindingFlags LcAll = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        // field by name on t or a base type, cached (null when missing)
        static FieldInfo LcF(Type t, string name)
        {
            string k = t.FullName + ":" + name;
            FieldInfo f;
            if (sLcFields.TryGetValue(k, out f)) return f;
            for (Type b = t; b != null && f == null; b = b.BaseType) f = b.GetField(name, LcAll);
            sLcFields[k] = f;
            return f;
        }
        static object LcGet(object o, string name)
        {
            if (o == null) return null;
            FieldInfo f = LcF(o.GetType(), name);
            return f == null ? null : f.GetValue(o);
        }
        static object LcStatic(Type t, string name)
        {
            FieldInfo f = LcF(t, name);
            return f == null ? null : f.GetValue(null);
        }

        static int LcCount(object o)
        {
            if (o == null) return 0;
            ICollection c = o as ICollection;
            if (c != null) return c.Count;
            IEnumerable e = o as IEnumerable;
            if (e == null || o is string) return 0;
            int n = 0;
            foreach (object x in e) n++;
            return n;
        }

        static readonly Dictionary<Type, FieldInfo[]> sLcRefFields = new Dictionary<Type, FieldInfo[]>();
        static FieldInfo[] LcRefFields(Type t)
        {
            FieldInfo[] fs;
            if (sLcRefFields.TryGetValue(t, out fs)) return fs;
            List<FieldInfo> l = new List<FieldInfo>();
            for (Type x = t; x != null && x != typeof(object); x = x.BaseType)
                foreach (FieldInfo f in x.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (typeof(GameObject).IsAssignableFrom(f.FieldType) || f.FieldType == typeof(SimDescription)) l.Add(f);
            fs = l.ToArray(); sLcRefFields[t] = fs; return fs;
        }

        static bool LcGone(object o)
        {
            if (o == null) return true;
            try
            {
                GameObject g = o as GameObject;
                if (g != null) return g.HasBeenDestroyed;
                SimDescription sd = o as SimDescription;
                if (sd != null) return !sd.IsValidDescription;
            }
            catch (Exception) { return true; }
            return false;
        }
        static bool LcSimGoneOrOut(Sim s)
        {
            try { return s == null || s.HasBeenDestroyed || !s.InWorld; } catch (Exception) { return true; }
        }

        static void LcBump(Dictionary<string, int> d, string k, int by)
        {
            int n;
            d.TryGetValue(k ?? "null", out n);
            d[k ?? "null"] = n + by;
        }
        static void LcBump(Dictionary<string, int> d, string k) { LcBump(d, k, 1); }

        // the top n entries of a count table, largest first; "(other)" sums the rest
        static Dictionary<string, object> LcTop(Dictionary<string, int> d, int n)
        {
            List<KeyValuePair<string, int>> l = new List<KeyValuePair<string, int>>(d);
            l.Sort(delegate (KeyValuePair<string, int> x, KeyValuePair<string, int> y) { int c = y.Value.CompareTo(x.Value); return c != 0 ? c : string.CompareOrdinal(x.Key, y.Key); });
            Dictionary<string, object> r = Util.Obj();
            int rest = 0;
            for (int i = 0; i < l.Count; i++) { if (i < n) r[l[i].Key] = l[i].Value; else rest += l[i].Value; }
            if (rest > 0) r["(other " + (l.Count - n) + ")"] = rest;
            return r;
        }

        static string LcMethod(Delegate d)
        {
            if (d == null) return "null";
            try
            {
                MethodInfo m = d.Method;
                return (m.DeclaringType != null ? m.DeclaringType.Name : "?") + "." + m.Name;
            }
            catch (Exception) { return "?"; }
        }
        static string LcTypeName(object o) { return o == null ? "null" : o.GetType().Name; }

        // instance fields of t (and bases) holding a collection (not a string), cached
        static List<FieldInfo> LcCollFields(Type t)
        {
            List<FieldInfo> r;
            if (sLcCollFields.TryGetValue(t, out r)) return r;
            r = new List<FieldInfo>();
            for (Type b = t; b != null && b != typeof(object); b = b.BaseType)
                foreach (FieldInfo f in b.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (f.FieldType != typeof(string) && typeof(IEnumerable).IsAssignableFrom(f.FieldType)) r.Add(f);
            sLcCollFields[t] = r;
            return r;
        }

        // instance fields of a situation type that can hold a Sim or SimDescription (directly or as a generic collection), cached
        static List<FieldInfo> LcSimFields(Type t)
        {
            List<FieldInfo> r;
            if (sLcSimFields.TryGetValue(t, out r)) return r;
            r = new List<FieldInfo>();
            for (Type b = t; b != null && b != typeof(object); b = b.BaseType)
                foreach (FieldInfo f in b.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    Type ft = f.FieldType;
                    bool hit = typeof(Sim).IsAssignableFrom(ft) || typeof(SimDescription).IsAssignableFrom(ft);
                    if (!hit && ft.IsGenericType && typeof(IEnumerable).IsAssignableFrom(ft))
                        foreach (Type ga in ft.GetGenericArguments()) if (typeof(Sim).IsAssignableFrom(ga) || typeof(SimDescription).IsAssignableFrom(ga)) hit = true;
                    if (hit) r.Add(f);
                }
            sLcSimFields[t] = r;
            return r;
        }
        static void LcAddSims(List<object> into, object o)
        {
            if (o == null) return;
            if (o is Sim || o is SimDescription) { into.Add(o); return; }
            IDictionary d = o as IDictionary;
            if (d != null) { foreach (object x in d.Keys) LcAddSims(into, x); foreach (object x in d.Values) LcAddSims(into, x); return; }
            IEnumerable e = o as IEnumerable;
            if (e != null && !(o is string)) foreach (object x in e) LcAddSims(into, x);
        }

        static object LeakCensus(Args a)
        {
            RequireLive();
            int top = (int)a.Long("top", 15);
            DateTime t0 = DateTime.UtcNow;
            Dictionary<string, object> r = Util.Obj();
            Dictionary<string, object> ms = Util.Obj();
            r["sim_time"] = SimClock.CurrentTime().ToString();
            Sim[] actors = null;
            try { actors = Sims3.Gameplay.Queries.GetObjects<Sim>(); } catch (Exception) { actors = new Sim[0]; }

            string[] names = { "alarms", "events", "objects", "situations", "relationships", "simdescs", "ui", "broadcasters", "socials", "statics", "inventories", "moods" };
            for (int i = 0; i < names.Length; i++)
            {
                DateTime s0 = DateTime.UtcNow;
                Dictionary<string, object> sec;
                try
                {
                    switch (i)
                    {
                        case 0: sec = LcAlarms(top); break;
                        case 1: sec = LcEvents(top); break;
                        case 2: sec = LcObjects(top); break;
                        case 3: sec = LcSituations(top); break;
                        case 4: sec = LcRelationships(); break;
                        case 5: sec = LcSimDescs(actors); break;
                        case 6: sec = LcUi(); break;
                        case 7: sec = LcBroadcasters(top); break;
                        case 8: sec = LcSocials(actors, top); break;
                        case 9: sec = LcStatics(); break;
                        case 10: sec = LcInventories(top); break;
                        default: sec = LcMoods(actors); break;
                    }
                }
                catch (Exception e) { sec = Util.Obj(); sec["error"] = e.GetType().Name + ": " + e.Message; }
                r[names[i]] = sec;
                ms[names[i]] = (int)(DateTime.UtcNow - s0).TotalMilliseconds;
            }
            r["section_ms"] = ms;
            try
            {
                int[] fc = AppDomain.CurrentDomain.GetData("ts3fixes.counters") as int[];
                Dictionary<string, object> fx = Util.Obj();
                if (fc != null) for (int i = 2000; i < fc.Length; i++) if (fc[i] != 0) fx[i.ToString()] = fc[i];
                r["fix_counters"] = fx;
                r["fix_applied"] = AppDomain.CurrentDomain.GetData("ts3fixes.applied");
            }
            catch (Exception) { }
            r["elapsed_ms"] = (int)(DateTime.UtcNow - t0).TotalMilliseconds;
            return r;
        }

        // 1. alarms: AlarmManager.mTimers (Dictionary<AlarmHandle, List<Timer>>) of the global manager and of every lot that has
        // its own (Lot.AlarmManager falls back to Global) [verified decompiled AlarmManager/Lot]
        static Dictionary<string, object> LcAlarms(int top)
        {
            Dictionary<string, object> r = Util.Obj();
            List<AlarmManager> mgrs = new List<AlarmManager>();
            if (AlarmManager.Global != null) mgrs.Add(AlarmManager.Global);
            int lotMgrs = 0;
            foreach (object o in LotManager.AllLots)
            {
                Lot l = o as Lot;
                if (l == null) continue;
                AlarmManager m = null;
                try { m = l.AlarmManager; } catch (Exception) { }
                if (m != null && !mgrs.Contains(m)) { mgrs.Add(m); lotMgrs++; }
            }
            Dictionary<string, int> owners = new Dictionary<string, int>(), cbs = new Dictionary<string, int>(), goneOwnerTypes = new Dictionary<string, int>();
            int total = 0, nullOwner = 0, goneOwner = 0, goneTarget = 0, handles = 0, indexed = 0, global = 0;
            foreach (AlarmManager m in mgrs)
            {
                IDictionary timers = LcGet(m, "mTimers") as IDictionary;
                ICollection idx = LcGet(m, "mGameObjectIndex") as ICollection;
                if (idx != null) indexed += idx.Count;
                if (timers == null) continue;
                handles += timers.Count;
                foreach (object list in timers.Values)
                {
                    IEnumerable l = list as IEnumerable;
                    if (l == null) continue;
                    foreach (object t in l)
                    {
                        if (t == null) continue;
                        total++;
                        if (m == AlarmManager.Global) global++;
                        object owner = LcGet(t, "ObjectRef");
                        Delegate cb = LcGet(t, "CallBack") as Delegate;
                        LcBump(owners, LcTypeName(owner));
                        LcBump(cbs, LcMethod(cb));
                        if (owner == null) nullOwner++;
                        else if (LcGone(owner)) { goneOwner++; LcBump(goneOwnerTypes, LcTypeName(owner)); }
                        if (cb != null && cb.Target != null && LcGone(cb.Target)) goneTarget++;
                    }
                }
            }
            r["total"] = total; r["global"] = global; r["lot_managers"] = lotMgrs; r["lot_alarms"] = total - global;
            r["handles"] = handles; r["owner_index"] = indexed;
            r["null_owner"] = nullOwner; r["gone_owner"] = goneOwner; r["gone_callback_target"] = goneTarget;
            r["gone_owner_types"] = LcTop(goneOwnerTypes, top);
            r["owner_types"] = LcTop(owners, top);
            r["callbacks"] = LcTop(cbs, top);
            return r;
        }

        // 2. events: EventTracker.mListeners (event id -> listener key -> list) [verified decompiled EventTracker]. The key is the
        // listener's ScriptObject id; key 0 listeners have no owner, so EventTracker.RemoveSimReferences never drops them.
        static Dictionary<string, object> LcEvents(int top)
        {
            Dictionary<string, object> r = Util.Obj();
            EventTracker et = EventTracker.Instance;
            if (et == null) { r["error"] = "no EventTracker"; return r; }
            Dictionary<ulong, Dictionary<ulong, List<EventListener>>> all = LcGet(et, "mListeners") as Dictionary<ulong, Dictionary<ulong, List<EventListener>>>;
            if (all == null) { r["error"] = "mListeners not found"; return r; }
            Dictionary<string, int> byEvent = new Dictionary<string, int>(), byOwner = new Dictionary<string, int>(), byClass = new Dictionary<string, int>();
            Dictionary<string, int> byDelegate = new Dictionary<string, int>(), goneByEvent = new Dictionary<string, int>();
            int total = 0, gone = 0, noHousehold = 0, noOwner = 0, keys = 0, targetHoldsGone = 0;
            Dictionary<string, int> holdsGoneByDelegate = new Dictionary<string, int>();
            // wish listeners (ActiveDreamNode.RunFeedbackFunction grew +1370 in 7 min of active-Sim switching, hunt 2026-11-11):
            // is the node still held by its owner's DreamsAndPromisesManager, or only by the event tracker?
            Dictionary<string, int> dream = new Dictionary<string, int>(), dreamOwners = new Dictionary<string, int>(), dreamOrphans = new Dictionary<string, int>();
            Dictionary<object, Dictionary<object, bool>> dreamMgrs = new Dictionary<object, Dictionary<object, bool>>();
            foreach (KeyValuePair<ulong, Dictionary<ulong, List<EventListener>>> ev in all)
            {
                if (ev.Value == null) continue;
                string evName = ((EventTypeId)ev.Key).ToString();
                foreach (KeyValuePair<ulong, List<EventListener>> kv in ev.Value)
                {
                    keys++;
                    if (kv.Value == null) continue;
                    foreach (EventListener l in kv.Value)
                    {
                        if (l == null) continue;
                        total++;
                        LcBump(byEvent, evName);
                        object so = l.ScriptObject;
                        LcBump(byOwner, LcTypeName(so));
                        LcBump(byClass, l.GetType().Name);
                        DelegateListener dl = l as DelegateListener;
                        if (dl != null) LcBump(byDelegate, LcMethod(dl.Delegate));
                        if (dl != null && dl.Delegate != null && dl.Delegate.Target != null && dl.Delegate.Target.GetType().Name == "ActiveDreamNode")
                            LcDreamListener(dl.Delegate.Target, dream, dreamMgrs, dreamOwners, dreamOrphans);
                        if (so == null) noOwner++;
                        bool g = (so != null && LcGone(so)) || (l.TargetObject != null && l.TargetObject.HasBeenDestroyed);
                        Sim s = so as Sim;
                        if (!g && s != null)
                        {
                            try { if (s.SimDescription == null || s.SimDescription.Household == null) noHousehold++; } catch (Exception) { noHousehold++; }
                        }
                        if (g) { gone++; LcBump(goneByEvent, evName); }
                        // the delegate's target itself, or (one level) a GameObject / SimDescription field of it, gone:
                        // finds listeners like Motives.OnSeasonTransition that keep a removed Sim reachable
                        if (dl != null && dl.Delegate != null && dl.Delegate.Target != null)
                        {
                            object t = dl.Delegate.Target;
                            bool tg = false;
                            if (t is GameObject || t is SimDescription) tg = LcGone(t);
                            else foreach (FieldInfo f in LcRefFields(t.GetType()))
                                {
                                    object v = null;
                                    try { v = f.GetValue(t); } catch (Exception) { }
                                    if (v != null && LcGone(v)) { tg = true; break; }
                                }
                            if (tg) { targetHoldsGone++; LcBump(holdsGoneByDelegate, LcMethod(dl.Delegate)); }
                        }
                    }
                }
            }
            r["total"] = total; r["event_ids"] = all.Count; r["owner_keys"] = keys;
            r["no_owner"] = noOwner; r["gone_owner_or_target"] = gone; r["sim_owner_no_household"] = noHousehold;
            r["pending_add"] = LcCount(LcGet(et, "mAddList")); r["pending_remove"] = LcCount(LcGet(et, "mRemoveList"));
            r["by_event"] = LcTop(byEvent, top);
            r["by_owner_type"] = LcTop(byOwner, top);
            r["by_listener_class"] = LcTop(byClass, top);
            r["by_delegate"] = LcTop(byDelegate, top);
            r["gone_by_event"] = LcTop(goneByEvent, top);
            r["target_holds_gone"] = targetHoldsGone; r["target_holds_gone_by_delegate"] = LcTop(holdsGoneByDelegate, top);
            Dictionary<string, object> dr = Util.Obj();
            dr["state"] = LcTop(dream, 8); dr["by_owner"] = LcTop(dreamOwners, top); dr["orphans_by_node"] = LcTop(dreamOrphans, top);
            dr["managers"] = dreamMgrs.Count;
            r["dream_listeners"] = dr;
            return r;
        }

        // one ActiveDreamNode listener: held by the owner's manager (any of its node collections), or not
        static void LcDreamListener(object node, Dictionary<string, int> state, Dictionary<object, Dictionary<object, bool>> mgrs,
            Dictionary<string, int> owners, Dictionary<string, int> orphans)
        {
            try
            {
                Sim owner = LcGet(node, "mOwner") as Sim;
                if (owner == null) { LcBump(state, "no owner"); return; }
                bool hh = owner.SimDescription != null && owner.SimDescription.Household == Household.ActiveHousehold;
                LcBump(owners, (hh ? "household " : "npc ") + owner.FullName);
                object mgr = LcGet(owner, "mDreamsAndPromisesManager");
                if (mgr == null) { LcBump(state, owner.HasBeenDestroyed ? "owner destroyed, no manager" : "owner has no manager"); return; }
                Dictionary<object, bool> held;
                if (!mgrs.TryGetValue(mgr, out held))
                {
                    held = new Dictionary<object, bool>();
                    foreach (FieldInfo f in mgr.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        object v = null;
                        try { v = f.GetValue(mgr); } catch (Exception) { }
                        if (v == null || v is string) continue;
                        if (v.GetType().Name == "ActiveDreamNode") { held[v] = true; continue; }
                        IDictionary d = v as IDictionary;
                        IEnumerable e = d != null ? d.Values : v as IEnumerable;
                        if (e != null) foreach (object x in e) if (x != null) held[x] = true;
                    }
                    mgrs[mgr] = held;
                }
                if (held.ContainsKey(node)) LcBump(state, hh ? "held (household)" : "held (npc)");
                else
                {
                    LcBump(state, hh ? "orphan (household)" : "orphan (npc)");
                    object ni = LcGet(node, "mNodeInstance");
                    string nm = null;
                    try { PropertyInfo np = ni == null ? null : ni.GetType().GetProperty("Name"); if (np != null) nm = Convert.ToString(np.GetValue(ni, null)); } catch (Exception) { }
                    LcBump(orphans, nm ?? LcTypeName(ni));
                }
            }
            catch (Exception e) { LcBump(state, "error " + e.GetType().Name); }
        }

        // 3. objects: every GameObject by type and by place
        static Dictionary<string, object> LcObjects(int top)
        {
            Dictionary<string, object> r = Util.Obj();
            Dictionary<string, int> byType = new Dictionary<string, int>(), outTypes = new Dictionary<string, int>(), invTypes = new Dictionary<string, int>();
            int total = 0, inWorld = 0, outOfWorld = 0, inInv = 0, destroyed = 0;
            foreach (GameObject g in Sims3.Gameplay.Queries.GetObjects<GameObject>())
            {
                if (g == null) continue;
                total++;
                string tn = g.GetType().Name;
                LcBump(byType, tn);
                try
                {
                    if (g.HasBeenDestroyed) destroyed++;
                    else if (g.InInventory) { inInv++; LcBump(invTypes, tn); }
                    else if (g.InWorld) inWorld++;
                    else { outOfWorld++; LcBump(outTypes, tn); }
                }
                catch (Exception) { destroyed++; }
            }
            r["total"] = total; r["in_world"] = inWorld; r["out_of_world"] = outOfWorld; r["in_inventory"] = inInv; r["destroyed"] = destroyed;
            r["types"] = byType.Count;
            r["by_type"] = LcTop(byType, Math.Max(top, 40));
            r["out_of_world_by_type"] = LcTop(outTypes, top);
            r["in_inventory_by_type"] = LcTop(invTypes, top);
            return r;
        }

        // 4. situations: Situation.sAllSituations, and the Sims each one references (SimsWithInteractions plus its Sim fields)
        static Dictionary<string, object> LcSituations(int top)
        {
            Dictionary<string, object> r = Util.Obj();
            Dictionary<string, int> byType = new Dictionary<string, int>(), goneTypes = new Dictionary<string, int>(), exitedTypes = new Dictionary<string, int>();
            int total = 0, noSims = 0, onlyGone = 0, exited = 0, interactions = 0, fieldErrors = 0;
            foreach (Situation sit in new List<Situation>(Situation.sAllSituations))
            {
                if (sit == null) continue;
                total++;
                string tn = sit.GetType().Name;
                LcBump(byType, tn);
                if (sit.SituationExited) { exited++; LcBump(exitedTypes, tn); }
                interactions += LcCount(LcGet(sit, "mInteractions"));
                List<object> sims = new List<object>();
                try
                {
                    foreach (Sim s in sit.SimsWithInteractions) sims.Add(s);
                    foreach (FieldInfo f in LcSimFields(sit.GetType())) LcAddSims(sims, f.GetValue(sit));
                }
                catch (Exception) { fieldErrors++; }
                bool any = false, live = false;
                foreach (object o in sims)
                {
                    if (o == null) continue;
                    any = true;
                    Sim s = o as Sim;
                    SimDescription sd = o as SimDescription;
                    if (s != null && !LcSimGoneOrOut(s)) live = true;
                    if (sd != null && sd.IsValidDescription && sd.CreatedSim != null && !LcSimGoneOrOut(sd.CreatedSim)) live = true;
                }
                if (!any) noSims++;
                else if (!live) { onlyGone++; LcBump(goneTypes, tn); }
            }
            r["total"] = total; r["no_sims"] = noSims; r["only_gone_or_out_sims"] = onlyGone; r["exited_still_listed"] = exited;
            r["interactions_held"] = interactions;
            if (fieldErrors > 0) r["field_errors"] = fieldErrors;
            r["by_type"] = LcTop(byType, top);
            r["only_gone_by_type"] = LcTop(goneTypes, top);
            r["exited_by_type"] = LcTop(exitedTypes, top);
            return r;
        }

        // 5. relationships: Relationship.sAllRelationships (desc -> desc -> Relationship); one Relationship object can sit under
        // both Sims, so distinct objects are counted
        static Dictionary<string, object> LcRelationships()
        {
            Dictionary<string, object> r = Util.Obj();
            Dictionary<Relationship, bool> seen = new Dictionary<Relationship, bool>();
            int entries = 0, outerGone = 0, outerOrphan = 0, disposed = 0, orphan = 0, memEntries = 0, memStamps = 0;
            FieldInfo memF = LcF(typeof(Relationship), "mSocialActionMemory");
            List<object> goneSamples = new List<object>();
            foreach (KeyValuePair<SimDescription, Dictionary<SimDescription, Relationship>> kv in Relationship.sAllRelationships)
            {
                if (kv.Key == null || !kv.Key.IsValidDescription)
                {
                    outerGone++;
                    if (goneSamples.Count < 10)
                    {
                        Dictionary<string, object> g = Util.Obj();
                        try
                        {
                            g["name"] = kv.Key == null ? null : kv.Key.FullName;
                            g["dead"] = kv.Key != null && kv.Key.IsDead;
                            g["ghost"] = kv.Key != null && kv.Key.IsGhost;
                            g["has_household"] = kv.Key != null && kv.Key.Household != null;
                            g["relationships"] = kv.Value == null ? 0 : kv.Value.Count;
                        }
                        catch (Exception e) { g["error"] = e.Message; }
                        goneSamples.Add(g);
                    }
                }
                else if (kv.Key.Household == null) outerOrphan++;
                if (kv.Value == null) continue;
                foreach (Relationship rel in kv.Value.Values)
                {
                    entries++;
                    if (rel == null || seen.ContainsKey(rel)) continue;
                    seen[rel] = true;
                    SimDescription x = rel.SimDescriptionA, y = rel.SimDescriptionB;
                    if (x == null || y == null || !x.IsValidDescription || !y.IsValidDescription) disposed++;
                    else if (x.Household == null || y.Household == null) orphan++;
                    if (memF != null)
                    {
                        // PairedListDictionary<string, List<DateAndTime>>: action name -> when it was done
                        IEnumerable<KeyValuePair<string, List<DateAndTime>>> mem = memF.GetValue(rel) as IEnumerable<KeyValuePair<string, List<DateAndTime>>>;
                        if (mem != null)
                            foreach (KeyValuePair<string, List<DateAndTime>> e in mem)
                            {
                                memEntries++;
                                if (e.Value != null) memStamps += e.Value.Count;
                            }
                    }
                }
            }
            r["sims_with_relationships"] = Relationship.sAllRelationships.Count;
            r["entries"] = entries; r["distinct"] = seen.Count;
            r["involves_disposed"] = disposed; r["involves_no_household"] = orphan;
            r["outer_keys_disposed_samples"] = goneSamples;
            r["outer_keys_disposed"] = outerGone; r["outer_keys_no_household"] = outerOrphan;
            r["social_action_memory_entries"] = memEntries; r["social_action_memory_stamps"] = memStamps;
            return r;
        }

        // 6. SimDescriptions reachable from households, relationship keys, live Sims, urnstones and genealogy; MiniSims apart
        static Dictionary<string, object> LcSimDescs(Sim[] actors)
        {
            Dictionary<string, object> r = Util.Obj();
            Dictionary<SimDescription, string> all = new Dictionary<SimDescription, string>();
            Dictionary<string, int> bySource = new Dictionary<string, int>();
            int households = 0;
            foreach (Household h in Household.sHouseholdList)
            {
                if (h == null) continue;
                households++;
                if (h.SimDescriptions == null) continue;
                foreach (SimDescription sd in h.SimDescriptions) if (sd != null && !all.ContainsKey(sd)) all[sd] = "household";
            }
            foreach (KeyValuePair<SimDescription, Dictionary<SimDescription, Relationship>> kv in Relationship.sAllRelationships)
            {
                if (kv.Key != null && !all.ContainsKey(kv.Key)) all[kv.Key] = "relationships";
                if (kv.Value != null) foreach (SimDescription sd in kv.Value.Keys) if (sd != null && !all.ContainsKey(sd)) all[sd] = "relationships";
            }
            foreach (Sim s in actors)
            {
                SimDescription sd = null;
                try { sd = s == null ? null : s.SimDescription; } catch (Exception) { }
                if (sd != null && !all.ContainsKey(sd)) all[sd] = "sim_object";
            }
            int urns = 0;
            foreach (Urnstone u in Sims3.Gameplay.Queries.GetObjects<Urnstone>())
            {
                if (u == null) continue;
                urns++;
                SimDescription sd = u.DeadSimsDescription;
                if (sd != null && !all.ContainsKey(sd)) all[sd] = "urnstone";
            }
            // genealogy: walk parents, children, siblings and spouse from every description found so far
            Dictionary<Genealogy, bool> gseen = new Dictionary<Genealogy, bool>();
            Stack<Genealogy> st = new Stack<Genealogy>();
            foreach (SimDescription sd in new List<SimDescription>(all.Keys))
            {
                Genealogy g = null;
                try { g = sd.Genealogy; } catch (Exception) { }
                if (g != null && !gseen.ContainsKey(g)) { gseen[g] = true; st.Push(g); }
            }
            int gNoDesc = 0;
            while (st.Count > 0)
            {
                Genealogy g = st.Pop();
                SimDescription sd = null;
                try { sd = g.SimDescription; } catch (Exception) { }
                if (sd == null) gNoDesc++;
                else if (!all.ContainsKey(sd)) all[sd] = "genealogy";
                List<Genealogy> next = new List<Genealogy>();
                try
                {
                    if (g.Parents != null) next.AddRange(g.Parents);
                    if (g.Children != null) next.AddRange(g.Children);
                    List<Genealogy> sib = LcGet(g, "mSiblings") as List<Genealogy>;  // Siblings is private
                    if (sib != null) next.AddRange(sib);
                    if (g.Spouse != null) next.Add(g.Spouse);
                }
                catch (Exception) { }
                foreach (Genealogy n in next) if (n != null && !gseen.ContainsKey(n)) { gseen[n] = true; st.Push(n); }
            }
            int inHh = 0, orphan = 0, invalid = 0, instantiated = 0;
            foreach (KeyValuePair<SimDescription, string> kv in all)
            {
                LcBump(bySource, kv.Value);
                SimDescription sd = kv.Key;
                if (!sd.IsValidDescription) invalid++;
                else if (sd.Household != null) inHh++;
                else orphan++;
                try { if (sd.CreatedSim != null) instantiated++; } catch (Exception) { }
            }
            r["total"] = all.Count; r["in_household"] = inHh; r["no_household"] = orphan; r["disposed_or_invalid"] = invalid;
            r["instantiated"] = instantiated; r["households"] = households; r["urnstones"] = urns;
            r["by_first_source"] = LcTop(bySource, 10);
            r["genealogy_nodes"] = gseen.Count; r["genealogy_without_desc"] = gNoDesc;
            // docs/191 family data audit: one-way links and duplicate ancestors (EA bugs, measured before any fix)
            int spouseOneWay = 0, sibOneWay = 0, sibUnbuilt = 0, ancDup = 0, ancMax = 0, noMini = 0, mSimNull = 0;
            foreach (Genealogy g in gseen.Keys)
            {
                try
                {
                    if (LcGet(g, "mSim") == null) mSimNull++;
                    if (g.IMiniSimDescription == null) noMini++;
                    Genealogy sp = LcGet(g, "mSpouse") as Genealogy;
                    if (sp != null && !object.ReferenceEquals(LcGet(sp, "mSpouse"), g)) spouseOneWay++;
                    List<Genealogy> sib = LcGet(g, "mSiblings") as List<Genealogy>;
                    if (sib != null)
                        foreach (Genealogy o in sib)
                        {
                            List<Genealogy> back = o == null ? null : LcGet(o, "mSiblings") as List<Genealogy>;
                            // a Sim with parents builds mSiblings lazily (Genealogy.Siblings) and AddChild clears it for the
                            // whole family, so a null or empty list there is "not built yet", not one-way
                            List<Genealogy> op = o == null ? null : LcGet(o, "mNaturalParents") as List<Genealogy>;
                            if (o != null && (back == null || back.Count == 0) && op != null && op.Count > 0) { sibUnbuilt++; continue; }
                            bool found = false;
                            if (back != null) foreach (Genealogy b in back) if (object.ReferenceEquals(b, g)) { found = true; break; }
                            if (!found) sibOneWay++;
                        }
                    List<Genealogy> anc = LcGet(g, "mAncestors") as List<Genealogy>;
                    if (anc != null)
                    {
                        Dictionary<Genealogy, bool> seenA = new Dictionary<Genealogy, bool>(new LcRefEq<Genealogy>());
                        foreach (Genealogy x in anc) { if (x == null) continue; if (seenA.ContainsKey(x)) ancDup++; else seenA[x] = true; }
                        if (anc.Count > ancMax) ancMax = anc.Count;
                    }
                }
                catch (Exception) { }
            }
            r["genealogy_spouse_one_way"] = spouseOneWay; r["genealogy_sibling_one_way"] = sibOneWay; r["genealogy_sibling_list_unbuilt"] = sibUnbuilt;
            r["genealogy_ancestor_dups"] = ancDup; r["genealogy_ancestors_max"] = ancMax;
            r["genealogy_no_mini"] = noMini; r["genealogy_msim_null"] = mSimNull;
            // docs/191 corruption audit (subagent F1/F2): partner links, relationship tables, half-removed Sims
            int partnerOneWay = 0, partnerInvalid = 0, halfRemoved = 0;
            foreach (SimDescription sd in all.Keys)
            {
                try
                {
                    SimDescription p = sd.Partner;
                    if (p != null && !p.IsValidDescription) partnerInvalid++;
                    else if (p != null && !object.ReferenceEquals(p.Partner, sd)) partnerOneWay++;
                    if (sd.IsValidDescription && sd.Household == null && sd.CreatedSim != null) halfRemoved++;
                }
                catch (Exception) { }
            }
            int relOneSided = 0, relSplitObj = 0, relInvalid = 0, relNoTable = 0, relTables = 0, relInvalidKeys = 0;
            List<string> relInvalidSample = new List<string>();
            try
            {
                Dictionary<SimDescription, Dictionary<SimDescription, Sims3.Gameplay.Socializing.Relationship>> rt = Sims3.Gameplay.Socializing.Relationship.sAllRelationships;
                relTables = rt.Count;
                foreach (KeyValuePair<SimDescription, Dictionary<SimDescription, Sims3.Gameplay.Socializing.Relationship>> kv in rt)
                {
                    if (kv.Key != null && !kv.Key.IsValidDescription)
                    {
                        relInvalid++; relInvalidKeys++;
                        if (relInvalidSample.Count < 12)
                        {
                            string who = "?";
                            try
                            {
                                int liveOthers = 0, backRefs = 0;
                                foreach (KeyValuePair<SimDescription, Sims3.Gameplay.Socializing.Relationship> e in kv.Value) if (e.Key != null && e.Key.IsValidDescription) liveOthers++;
                                foreach (KeyValuePair<SimDescription, Dictionary<SimDescription, Sims3.Gameplay.Socializing.Relationship>> o in rt) if (o.Value != null && o.Value.ContainsKey(kv.Key)) backRefs++;
                                who = kv.Key.FullName + " id " + kv.Key.SimDescriptionId + " hh " + (kv.Key.Household != null ? "yes" : "none") + " sim " + (kv.Key.CreatedSim != null ? "yes" : "none")
                                    + " entries " + kv.Value.Count + " (live " + liveOthers + ") held by " + backRefs;
                            }
                            catch (Exception) { }
                            relInvalidSample.Add(who);
                        }
                    }
                    if (kv.Value == null) continue;
                    foreach (KeyValuePair<SimDescription, Sims3.Gameplay.Socializing.Relationship> e in kv.Value)
                    {
                        if (e.Key != null && !e.Key.IsValidDescription) relInvalid++;
                        Dictionary<SimDescription, Sims3.Gameplay.Socializing.Relationship> back;
                        if (e.Key == null || !rt.TryGetValue(e.Key, out back) || back == null) { relNoTable++; continue; }
                        Sims3.Gameplay.Socializing.Relationship b;
                        if (!back.TryGetValue(kv.Key, out b)) relOneSided++;
                        else if (!object.ReferenceEquals(b, e.Value)) relSplitObj++;
                    }
                }
            }
            catch (Exception) { }
            r["partner_one_way"] = partnerOneWay; r["partner_invalid"] = partnerInvalid; r["half_removed"] = halfRemoved;
            r["rel_tables"] = relTables; r["rel_one_sided"] = relOneSided; r["rel_split_objects"] = relSplitObj;
            r["rel_invalid_desc"] = relInvalid; r["rel_invalid_keys"] = relInvalidKeys; r["rel_invalid_sample"] = relInvalidSample; r["rel_partner_no_table"] = relNoTable;
            r["mini_sims"] = LcCount(LcStatic(typeof(MiniSimDescription), "sMiniSims"));
            r["loaded_sim_descriptions"] = LcCount(LcStatic(typeof(SimDescription), "sLoadedSimDescriptions"));
            return r;
        }

        // 7. UI: Sims3.UI's UIManager (UI.dll; found by name, not referenced at compile time) and its static collections
        static Dictionary<string, object> LcUi()
        {
            Dictionary<string, object> r = Util.Obj();
            if (!sLcUiSearched)
            {
                sLcUiSearched = true;
                foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type t = null;
                    try { t = asm.GetType("Sims3.UI.UIManager", false); } catch (Exception) { }
                    if (t != null && t.GetField("mCustomControlInstanceDict", LcAll) != null) { sLcUiManager = t; break; }
                }
                if (sLcUiManager == null)
                    foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        Type[] ts = null;
                        try { ts = asm.GetTypes(); } catch (Exception) { continue; }
                        foreach (Type t in ts)
                            if (t.Name == "UIManager" && t.GetField("mCustomControlInstanceDict", LcAll) != null) { sLcUiManager = t; break; }
                        if (sLcUiManager != null) break;
                    }
            }
            if (sLcUiManager == null) { r["error"] = "no UIManager type with mCustomControlInstanceDict"; return r; }
            r["type"] = sLcUiManager.FullName;
            IDictionary dict = LcStatic(sLcUiManager, "mCustomControlInstanceDict") as IDictionary;
            r["custom_control_instances"] = dict == null ? -1 : dict.Count;
            if (dict != null)
            {
                Dictionary<string, int> byType = new Dictionary<string, int>();
                // a control whose window has no parent is off the UI tree (its panel was destroyed without the Detach that
                // UIManager.OnCustomDetach needs to drop it, hunt 2026-11-11 CustomContentIcon +600 per catalogue browse)
                Dictionary<string, int> orphan = new Dictionary<string, int>(), native = new Dictionary<string, int>();
                foreach (object v in dict.Values)
                {
                    LcBump(byType, LcTypeName(v));
                    if (v == null) continue;
                    PropertyInfo pp = v.GetType().GetProperty("Parent", BindingFlags.Public | BindingFlags.Instance);
                    object par = null;
                    try { if (pp != null) par = pp.GetValue(v, null); } catch (Exception) { par = "error"; }
                    if (pp != null && par == null) LcBump(orphan, LcTypeName(v));
                    // the engine's view of the handle (UIManager.WrapperFromHandle asks the same): a destroyed window should
                    // read class id 0; a candidate test for a purge fix, checked against the controls still on the tree
                    uint cls = LcClassId(dict, v);
                    LcBump(native, (pp != null && par == null ? "no parent, " : "on tree, ") + (cls == 0 ? "class 0" : cls == 0xFFFFFFFF ? "class ? (no manager)" : cls == 0xFFFFFFFE ? "class ? (threw)" : "class set"));
                }
                r["custom_control_native"] = LcTop(native, 6);
                r["custom_control_by_type"] = LcTop(byType, 10);
                r["custom_control_no_parent"] = LcTop(orphan, 10);
            }
            r["static_collections"] = LcStaticColls(sLcUiManager);
            r["scriptcore_uimanager_statics"] = LcStaticColls(typeof(ScriptCore.UIManager));
            // who fills the registries that grew by a fixed step per save load (hunt 2026-11-11): entries grouped by the
            // delegate's declaring type and method (or the value type), and the fields of a few InventoryItemWin controls
            Dictionary<string, object> det = Util.Obj();
            foreach (string f in new string[] { "mTagRegistry", "mCreateTooltipCallbackRegistry", "mEventRegistry" })
                det[f] = LcCollDetail(LcStatic(sLcUiManager, f), 12);
            r["registry_detail"] = det;
            if (dict != null)
            {
                List<object> inv = new List<object>();
                foreach (object v in dict.Values)
                {
                    if (v == null || v.GetType().Name != "InventoryItemWin" || inv.Count >= 12) continue;
                    Dictionary<string, object> one = Util.Obj();
                    foreach (FieldInfo fi in v.GetType().GetFields(LcAll))
                    {
                        if (fi.IsStatic) continue;
                        object fv = null;
                        try { fv = fi.GetValue(v); } catch (Exception) { }
                        one[fi.Name] = fv == null ? "null" : (fv.GetType().IsPrimitive || fv is string) ? (object)fv.ToString() : LcTypeName(fv);
                    }
                    inv.Add(one);
                }
                r["inventory_item_wins"] = inv;
            }
            return r;
        }

        static PropertyInfo sLcWinHandle;
        static uint LcClassId(IDictionary dict, object w)
        {
            try
            {
                Sims3.SimIFace.IUIManager m = LcStatic(sLcUiManager, "gUIMgr") as Sims3.SimIFace.IUIManager;
                Sims3.UI.WindowBase wb = w as Sims3.UI.WindowBase;
                if (m == null || wb == null) return 0xFFFFFFFF;
                if (sLcWinHandle == null) sLcWinHandle = typeof(Sims3.UI.WindowBase).GetProperty("WinHandle", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                return m.GetClassID(Convert.ToUInt32(sLcWinHandle.GetValue(wb, null)));
            }
            catch (Exception) { return 0xFFFFFFFE; }
        }

        static string LcDelegateName(object o)
        {
            Delegate d = o as Delegate;
            if (d == null) return LcTypeName(o);
            string s = (d.Method == null ? "?" : (d.Method.DeclaringType == null ? "?" : d.Method.DeclaringType.Name) + "." + d.Method.Name);
            Delegate[] inv = d.GetInvocationList();
            if (inv.Length > 1) s += " (+" + (inv.Length - 1) + " more)";
            return s;
        }

        // a dictionary or list: entries grouped by key type and by value (delegates by target method, lists expanded)
        static Dictionary<string, object> LcCollDetail(object coll, int top)
        {
            Dictionary<string, object> r = Util.Obj();
            if (coll == null) { r["null"] = true; return r; }
            r["type"] = coll.GetType().Name;
            Dictionary<string, int> keys = new Dictionary<string, int>(), vals = new Dictionary<string, int>();
            List<object> samples = new List<object>();
            Dictionary<string, int> detachedBy = new Dictionary<string, int>();
            int detached = 0;
            Dictionary<string, int> simdescState = new Dictionary<string, int>();
            List<object> chains = new List<object>();
            try
            {
                IDictionary d = coll as IDictionary;
                IEnumerable items = d != null ? (IEnumerable)d.Keys : coll as IEnumerable;
                foreach (object k in items)
                {
                    object v = d != null ? d[k] : k;
                    if (d != null) LcBump(keys, LcTypeName(k));
                    if (d != null && k != null)
                    {
                        // a key window whose Parent is null is detached from the UI tree: an entry nothing removes
                        PropertyInfo pp = k.GetType().GetProperty("Parent", BindingFlags.Public | BindingFlags.Instance);
                        object par = null; bool known = pp != null;
                        try { if (known) par = pp.GetValue(k, null); } catch (Exception) { known = false; }
                        SimDescription sdv = v as SimDescription;
                        if (sdv != null)
                        {
                            SimDescription cur = null;
                            try { cur = SimDescription.Find(sdv.SimDescriptionId); } catch (Exception) { }
                            if (chains.Count < 6)
                            {
                                // the window's parent chain (type#ID) up to the root: names the panel that tagged it
                                string ch = ""; object w = k;
                                for (int i = 0; i < 12 && w != null; i++)
                                {
                                    object id = null;
                                    try { PropertyInfo ip = w.GetType().GetProperty("ID"); if (ip != null) id = ip.GetValue(w, null); } catch (Exception) { }
                                    ch += (ch.Length > 0 ? " < " : "") + w.GetType().Name + "#" + Convert.ToString(id);
                                    try { PropertyInfo q = w.GetType().GetProperty("Parent", BindingFlags.Public | BindingFlags.Instance); w = q == null ? null : q.GetValue(w, null); } catch (Exception) { w = null; }
                                }
                                chains.Add(sdv.FullName + ": " + ch);
                            }
                            LcBump(simdescState, !sdv.IsValidDescription ? "disposed" : cur == sdv ? "current" : cur == null ? "not found" : "stale copy");
                        }
                        if (known && par == null) { detached++; LcBump(detachedBy, (v is Delegate) ? LcDelegateName(v) : LcTypeName(v)); }
                    }
                    IEnumerable sub = (v is string || v is Delegate) ? null : v as IEnumerable;
                    if (sub != null) foreach (object e in sub) LcBump(vals, LcDelegateName(e));
                    else LcBump(vals, LcDelegateName(v));
                    if (samples.Count < 6 && d != null) samples.Add(Convert.ToString(k) + " = " + (sub != null ? "[" + LcCount(sub) + "]" : LcDelegateName(v)));
                }
            }
            catch (Exception e) { r["error"] = e.GetType().Name; }
            r["keys"] = LcTop(keys, top); r["values"] = LcTop(vals, top); r["samples"] = samples;
            r["detached_keys"] = detached; r["detached_by_value"] = LcTop(detachedBy, top);
            r["simdesc_values"] = LcTop(simdescState, 5); r["simdesc_chains"] = chains;
            return r;
        }

        // every static field of t that holds a collection, with its count
        static Dictionary<string, object> LcStaticColls(Type t)
        {
            Dictionary<string, object> r = Util.Obj();
            foreach (FieldInfo f in t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (f.FieldType == typeof(string) || !typeof(IEnumerable).IsAssignableFrom(f.FieldType) || f.IsLiteral) continue;
                try { object v = f.GetValue(null); r[f.Name] = v == null ? -1 : LcCount(v); } catch (Exception) { r[f.Name] = "error"; }
            }
            return r;
        }

        // 8. reaction broadcasters: each lot's mSavedData.mReactions / mBroadcastersWithSims (no global registry) [verified
        // decompiled Lot/ReactionBroadcaster]. A broadcaster stays on the lot it was created on; Dispose removes it from the
        // object's current lot, so one whose object changed lots without MoveReactions is "on_other_lot".
        static Dictionary<string, object> LcBroadcasters(int top)
        {
            Dictionary<string, object> r = Util.Obj();
            Dictionary<string, int> byObj = new Dictionary<string, int>(), goneTypes = new Dictionary<string, int>();
            int total = 0, withSims = 0, gone = 0, otherLot = 0, lots = 0, maxLot = 0;
            foreach (object o in LotManager.AllLots)
            {
                Lot l = o as Lot;
                if (l == null) continue;
                object sd = LcGet(l, "mSavedData");
                List<ReactionBroadcaster> rx = LcGet(sd, "mReactions") as List<ReactionBroadcaster>;
                withSims += LcCount(LcGet(sd, "mBroadcastersWithSims"));
                if (rx == null || rx.Count == 0) continue;
                lots++;
                if (rx.Count > maxLot) maxLot = rx.Count;
                foreach (ReactionBroadcaster b in rx)
                {
                    if (b == null) continue;
                    total++;
                    IGameObject bo = b.BroadcastingObject;
                    LcBump(byObj, LcTypeName(bo));
                    if (bo == null || bo.HasBeenDestroyed) { gone++; LcBump(goneTypes, LcTypeName(bo)); }
                    else if (bo.LotCurrent != null && bo.LotCurrent != l && !(l.IsWorldLot && bo.LotCurrent.IsWorldLot)) otherLot++;
                }
            }
            r["total"] = total; r["active_with_sims"] = withSims; r["gone_object"] = gone; r["on_other_lot"] = otherLot;
            r["lots_with_broadcasters"] = lots; r["max_on_one_lot"] = maxLot;
            r["by_object_type"] = LcTop(byObj, top);
            r["gone_by_object_type"] = LcTop(goneTypes, top);
            return r;
        }

        // 9a. socials: conversations, per-Sim SocialComponent and ThoughtBalloonManager collections, social interactions held
        static Dictionary<string, object> LcSocials(Sim[] actors, int top)
        {
            Dictionary<string, object> r = Util.Obj();
            Dictionary<Conversation, bool> convs = new Dictionary<Conversation, bool>();
            Dictionary<string, int> scField = new Dictionary<string, int>(), scGoneKeys = new Dictionary<string, int>(), tbField = new Dictionary<string, int>();
            Dictionary<string, int> socialTypes = new Dictionary<string, int>();
            List<KeyValuePair<string, int>> perSim = new List<KeyValuePair<string, int>>();
            int sims = 0, queued = 0, queuedSocial = 0, socialGoneTarget = 0, tbSims = 0;
            foreach (Sim s in actors)
            {
                if (s == null || s.HasBeenDestroyed) continue;
                sims++;
                string name = "?";
                try { name = s.FullName; } catch (Exception) { }
                int simTotal = 0;
                SocialComponent sc = null;
                try { sc = s.SocialComponent; } catch (Exception) { }
                if (sc != null)
                {
                    if (sc.Conversation != null) convs[sc.Conversation] = true;
                    foreach (FieldInfo f in LcCollFields(sc.GetType()))
                    {
                        object v = f.GetValue(sc);
                        int n = LcCount(v);
                        simTotal += n;
                        LcBump(scField, f.Name, n);
                        IDictionary d = v as IDictionary;
                        if (d != null)
                            foreach (object k in d.Keys)
                                if (k is Sim && LcGone(k)) LcBump(scGoneKeys, f.Name);
                    }
                }
                ThoughtBalloonManager tb = null;
                try { tb = s.ThoughtBalloonManager; } catch (Exception) { }
                if (tb != null)
                {
                    tbSims++;
                    foreach (FieldInfo f in LcCollFields(tb.GetType()))
                    {
                        int n = LcCount(f.GetValue(tb));
                        simTotal += n;
                        LcBump(tbField, f.Name, n);
                    }
                }
                perSim.Add(new KeyValuePair<string, int>(name, simTotal));
                try
                {
                    if (s.InteractionQueue != null)
                        foreach (InteractionInstance ii in s.InteractionQueue.InteractionList)
                        {
                            queued++;
                            if (!(ii is SocialInteraction)) continue;
                            queuedSocial++;
                            LcBump(socialTypes, ii.GetType().Name);
                            if (ii.Target != null && ii.Target.HasBeenDestroyed) socialGoneTarget++;
                        }
                }
                catch (Exception) { }
            }
            int convTotal = convs.Count, small = 0, goneMember = 0, members = 0, topics = 0, topicalKeys = 0, topicalGone = 0;
            foreach (Conversation c in convs.Keys)
            {
                int n = c.NumMembers;
                members += n;
                if (n <= 1) small++;
                foreach (Sim m in c.Members) if (LcSimGoneOrOut(m)) { goneMember++; break; }
                topics += LcCount(LcGet(c, "mActiveTopics"));
                IDictionary used = LcGet(c, "mTopicalSocialsAlreadyUsed") as IDictionary;
                if (used != null) { topicalKeys += used.Count; foreach (object k in used.Keys) if (LcGone(k)) topicalGone++; }
            }
            // social interactions held by situations (the only static holder of InteractionInstances found)
            int sitSocial = 0, sitSocialGone = 0;
            foreach (Situation sit in new List<Situation>(Situation.sAllSituations))
            {
                IEnumerable ints = LcGet(sit, "mInteractions") as IEnumerable;
                if (ints == null) continue;
                foreach (object o in ints)
                {
                    SocialInteraction si = o as SocialInteraction;
                    if (si == null) continue;
                    sitSocial++;
                    if (LcSimGoneOrOut(si.InstanceActor) || (si.Target != null && si.Target.HasBeenDestroyed)) sitSocialGone++;
                }
            }
            perSim.Sort(delegate (KeyValuePair<string, int> x, KeyValuePair<string, int> y) { return y.Value.CompareTo(x.Value); });
            Dictionary<string, object> top5 = Util.Obj();
            for (int i = 0; i < perSim.Count && i < 5; i++) top5[perSim[i].Key + (top5.ContainsKey(perSim[i].Key) ? " #" + i : "")] = perSim[i].Value;
            Dictionary<string, object> conv = Util.Obj();
            conv["total"] = convTotal; conv["members"] = members; conv["zero_or_one_member"] = small; conv["with_gone_or_out_member"] = goneMember;
            conv["active_topics"] = topics; conv["topical_used_keys"] = topicalKeys; conv["topical_used_gone_keys"] = topicalGone;
            r["sims"] = sims;
            r["conversations"] = conv;
            r["social_component_fields"] = LcTop(scField, 40);
            r["social_component_gone_sim_keys"] = LcTop(scGoneKeys, 40);
            r["thought_balloon_managers"] = tbSims;
            r["thought_balloon_fields"] = LcTop(tbField, 10);
            r["top_sims_by_collection_size"] = top5;
            r["queued_interactions"] = queued; r["queued_social"] = queuedSocial; r["queued_social_gone_target"] = socialGoneTarget;
            r["queued_social_by_type"] = LcTop(socialTypes, top);
            r["situation_held_social"] = sitSocial; r["situation_held_social_gone"] = sitSocialGone;
            Dictionary<string, object> tables = Util.Obj();
            tables["ActionData.sData"] = LcCount(LcStatic(typeof(ActionData), "sData"));
            tables["ThoughtBalloonManager.sBalloons"] = LcCount(ThoughtBalloonManager.sBalloons);
            r["static_tables"] = tables;
            return r;
        }

        // 9b. static collections that can grow during play [verified decompiled: adds found, removes missing or rare]; the
        // reasons are in docs/191 "leak_census". -1 = field is null (not created yet or cleared by the game).
        static readonly object[] sLcStatics = {
            typeof(ScriptCore.Simulator), "mObjHash",                       // every live script task / object proxy (Simulator.AddObject)
            typeof(Sims3.Gameplay.Utilities.ScreenGrabEntity), "sInstanceNameCache", // ObjectGuid cache, only added, cleared by ScreenGrabHelper
            typeof(Sims3.Gameplay.Utilities.ScreenGrabEntity), "sTypeNameCache",
            typeof(Sims3.Gameplay.Objects.Appliances.HotBeverageMachine), "SimDrinkRecipes", // SimDescription key, cleared only on Shutdown
            typeof(Sims3.Gameplay.Objects.RabbitHoles.CollegeOfBusiness), "sCooldownDict",   // per Sim id, never removed
            typeof(Sims3.Gameplay.Objects.Fishing.MountedFish), "sMountedFishReceived",     // Sim key
            typeof(Sims3.Gameplay.Objects.Electronics.SkeeBall), "sPlayerToTableMap",       // Sim key, only Clear
            typeof(Sims3.Gameplay.Objects.CookingObjects.PicnicSituation), "sSimsAtPicnics", // Sim key
            typeof(Sims3.Gameplay.Objects.Electronics.Computer), "ForumPostings",            // ObjectGuid key
            typeof(Sims3.Gameplay.Objects.Register.ConsignmentRegister), "sConsignAttempts", // ObjectGuid key, never removed
            typeof(Sims3.Gameplay.Objects.Register.ConsignmentRegister), "sConsignedObjects",
            typeof(GameObject), "sObjectsPickedUpInBuildBuy",                               // GameObject key, nulled on Build/Buy exit
            typeof(Sims3.SimIFace.CAS.CASUtils), "sClothingSwapDelegates",                  // delegates per swap id
            typeof(Sims3.SimIFace.Animation), "sPMASleepingTasks",
            typeof(Sims3.SimIFace.Route), "AddDestinationTargetCallbacks",                  // delegates per ObjectGuid (pin their targets)
            typeof(Sims3.SimIFace.Route), "IsSlotUnobstructedCallbacks",
            typeof(MetaAutonomyManager), "sLotPopularityMapForPlayer",
            typeof(Household), "sOldIdToNewSimDescriptionMap",
            typeof(ActionData), "sData",                                                    // social tuning table, fixed after load (control)
        };

        static Dictionary<string, object> LcStatics()
        {
            Dictionary<string, object> r = Util.Obj();
            for (int i = 0; i + 1 < sLcStatics.Length; i += 2)
            {
                Type t = (Type)sLcStatics[i];
                string fn = (string)sLcStatics[i + 1];
                string k = t.Name + "." + fn;
                try
                {
                    FieldInfo f = LcF(t, fn);
                    if (f == null) { r[k] = "field not found"; continue; }
                    object v = f.GetValue(null);
                    if (v == null) { r[k] = -1; continue; }
                    IDictionary d = v as IDictionary;
                    int gone = 0;
                    bool refKeys = false;
                    if (d != null)
                        foreach (object key in d.Keys)
                            if (key is GameObject || key is SimDescription) { refKeys = true; if (LcGone(key)) gone++; }
                    if (refKeys) { Dictionary<string, object> e = Util.Obj(); e["count"] = d.Count; e["gone_keys"] = gone; r[k] = e; }
                    else r[k] = LcCount(v);
                }
                catch (Exception ex) { r[k] = "error: " + ex.Message; }
            }
            return r;
        }
        sealed class LcRefEq<T> : IEqualityComparer<T> where T : class
        {
            public bool Equals(T x, T y) { return object.ReferenceEquals(x, y); }
            public int GetHashCode(T o) { return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o); }
        }
    }
}
