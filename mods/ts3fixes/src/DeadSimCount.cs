using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Utilities;

namespace Ts3Fixes
{
    // deadsimcount (issue #40; errortrapsims' hourly alarm, own switch; read-only, not save data): how many destroyed Sim
    // objects are still in memory, without a heap scan. Each pass keeps a WeakReference to every Sim in LotManager.Actors
    // it has not seen before (one per Sim object, by ObjectId), so the reference never keeps the Sim alive. A destroyed Sim
    // whose reference still has a target is held by something (a mod's list, a handler, a frame); one whose target is gone
    // was collected. The first pass that sees it destroyed stamps the sim time, so the age buckets say how long it has
    // been held. A collection can lag a destroy by a few seconds, so only the age buckets count as held for long.
    // Counters: [2088] destroyed Sims still in memory now, [2089] of them destroyed 6+ sim hours ago, [2090] collected
    // since the session began, [2091] Sim objects tracked now.
    // shortcut: a Sim spawned and destroyed between two hourly passes is never tracked; hook Sim creation if the count needs it.
    //
    // deadsimhollow (own switch, same pass and tracking; Rodney's idea in #40, user 11-09: "other lazy modders might keep
    // those references"): a Sim destroyed 6+ sim hours ago and still in memory is held by something nobody released, often
    // an unknown mod's list. The Sim object cannot be freed while that holder points at it, but what it owns can: the pass
    // clears the Sim's own reference fields (those declared in Sims3GameplaySystems classes: its components, interaction
    // queue, autonomy, buffs, posture, managers, event handlers, lists), so their graphs and any other dead Sims they reach
    // can be collected. Kept: the SimDescription, Household and Lot links and any live (not destroyed) world object, which
    // free nothing and are what a holder most likely reads; collections become empty ones of the same type, not null.
    // Never: Sims of the active household or of a household played before, a Sim without a description (unknown scope), a
    // Sim its description still names as CreatedSim, a Sim still in LotManager.Actors, with no active household, or during travel. Destroyed Sims are not in the object table, so
    // saves do not write them, except through a saved script stack that holds one. [2092] Sims hollowed, [2093] fields cleared.
    public static partial class Fixes
    {
        sealed class DsEntry { public WeakReference Ref; public float DeadAt = -1f; public bool Hollowed; }
        static Dictionary<ulong, DsEntry> sDsSeen = new Dictionary<ulong, DsEntry>();
        static bool sDsOk, sDsThrew, sDhOk;
        static readonly Dictionary<Type, List<FieldInfo>> sDhFields = new Dictionary<Type, List<FieldInfo>>();
        static int sDhLogged;

        static void DeadSimCountPass()
        {
            try
            {
                bool count = ShortcutOn("deadsimcount"), hollow = ShortcutOn("deadsimhollow");
                if ((!count && !hollow) || LotManager.Actors == null) return;
                if (!sDsOk) { sDsOk = true; AppliedLog().Add("deadsimcount: ok" + (hollow ? ", deadsimhollow on" : "")); }
                List<Sim> actors = new List<Sim>(LotManager.Actors);
                foreach (Sim s in actors)
                {
                    if (s == null) continue;
                    ulong id = s.ObjectId.Value;
                    if (!sDsSeen.ContainsKey(id)) { DsEntry e = new DsEntry(); e.Ref = new WeakReference(s); sDsSeen[id] = e; }
                }
                float now = SimClock.ElapsedTime(TimeUnit.Hours);
                int alive = 0, old = 0, gone = 0;
                Dictionary<ulong, DsEntry> keep = new Dictionary<ulong, DsEntry>();
                // entries, not Sims: a destroyed Sim's ObjectId is kBadObjectGuid (ScriptCore ScriptProxy.Dispose), so
                // sDsSeen[s.ObjectId.Value] threw for every candidate and nothing was hollowed (al372, al374: 0 of 1,303)
                List<KeyValuePair<ulong, DsEntry>> toHollow = new List<KeyValuePair<ulong, DsEntry>>();
                foreach (KeyValuePair<ulong, DsEntry> kv in sDsSeen)
                {
                    Sim s = kv.Value.Ref.Target as Sim;
                    if (s == null) { gone++; continue; }
                    keep[kv.Key] = kv.Value;
                    if (!s.HasBeenDestroyed) continue;
                    if (kv.Value.DeadAt < 0f) kv.Value.DeadAt = now;
                    alive++;
                    if (now - kv.Value.DeadAt < 6f) continue;
                    old++;
                    if (hollow && !kv.Value.Hollowed && !actors.Contains(s)) toHollow.Add(kv);
                }
                // rebuilt, not Remove: al364 read tracked 24 < alive 182 from the Remove version, which cannot be
                sDsSeen = keep;
                int[] c = PatchCounters();
                c[2088] = alive;
                c[2089] = old;
                c[2090] += gone;
                c[2091] = sDsSeen.Count;
                if (toHollow.Count == 0 || GameStates.IsTravelling) return;
                // household rule (CLAUDE.md): no active household = unknown scope for every Sim, so the hollowing waits
                Household act = Household.ActiveHousehold;
                if (act == null) return;
                if (!sDhOk) { sDhOk = true; AppliedLog().Add("deadsimhollow: ok"); }
                IActor sel = PlumbBob.SelectedActor;
                foreach (KeyValuePair<ulong, DsEntry> h in toHollow)
                {
                    Sim s = h.Value.Ref.Target as Sim;
                    if (s == null) continue;
                    try  // one Sim that throws does not stop the rest
                    {
                        h.Value.Hollowed = true;  // marked here, not when picked: a pass that waits retries them
                        SimDescription sd = s.SimDescription;
                        if (sd == null || sd.CreatedSim == s) continue;
                        // gone-Sim household rule (DeadSimHolders.cs GoneOwnerHh): the description's live Sim is household,
                        // selected or of unknown scope, or the description is the active household's or the selected Sim's
                        if (GoneOwnerHh(s, act, sel)) continue;
                        // player households (active or played before) keep their Sims as they are
                        if (sd.Household != null && (sd.Household == act || sd.Household.IsActive || HouseholdPlayed(sd.Household))) continue;
                        int n = HollowSim(s, h.Key);
                        if (n > 0) { c[2092]++; c[2093] += n; }
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception e)
            {
                if (!sDsThrew) { sDsThrew = true; AppliedLog().Add("deadsimcount: pass threw " + e.Message); }
            }
        }

        // the instance reference fields declared in Sims3GameplaySystems classes of t and its bases (not ScriptCore's
        // ScriptObject: object id, proxy and destroyed flag stay as they are)
        static List<FieldInfo> DhFields(Type t)
        {
            List<FieldInfo> l;
            if (sDhFields.TryGetValue(t, out l)) return l;
            l = new List<FieldInfo>();
            Assembly gp = typeof(Sim).Assembly;
            for (Type b = t; b != null && b.Assembly == gp; b = b.BaseType)
                foreach (FieldInfo f in b.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (!f.FieldType.IsValueType && f.FieldType != typeof(string) && !f.IsInitOnly) l.Add(f);
            sDhFields[t] = l;
            return l;
        }

        static int HollowSim(Sim s, ulong id)  // id: the ObjectId the Sim had alive (its own reads kBadObjectGuid now)
        {
            int n = 0;
            List<string> names = sDhLogged < 3 ? new List<string>() : null;
            foreach (FieldInfo f in DhFields(s.GetType()))
            {
                try
                {
                    object v = f.GetValue(s);
                    if (v == null || v is SimDescription || v is MiniSimDescription || v is Household || v is Lot) continue;
                    GameObject go = v as GameObject;
                    if (go != null && !go.HasBeenDestroyed) continue;
                    object empty = null;
                    if (v is Array) empty = Array.CreateInstance(v.GetType().GetElementType(), 0);
                    // generic-only collections (HashSet<T>) have no non-generic ICollection; a mod reading one gets an empty one, not null
                    else if ((v is ICollection || v.GetType().GetInterface("ICollection`1") != null) && v.GetType().GetConstructor(Type.EmptyTypes) != null) empty = Activator.CreateInstance(v.GetType());
                    if (empty != null && !f.FieldType.IsInstanceOfType(empty)) empty = null;
                    f.SetValue(s, empty);
                    n++;
                    if (names != null && names.Count < 60) names.Add(f.Name);
                }
                catch (Exception) { }  // one field that refuses does not stop the rest
            }
            if (names != null) { sDhLogged++; ShortcutLog("deadsimhollow: " + id.ToString("X") + " cleared " + n + ": " + string.Join(" ", names.ToArray())); }
            return n;
        }
    }
}
