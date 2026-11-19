using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Socializing;
using Sims3.UI.CAS;

namespace Ts3Fixes
{
    // docs/665 algo sweep (data-ranked managed hot spots; pure cores in AlgoSweepCore.cs).
    //
    // minirelset (tuning kMiniRelSet, default off; docs/665 §3.1). EA's Relationship.GetMiniRelationships(SimDescription)
    // walks the Sim's relationship dictionary and, per entry, does "if (!list.Contains(dictionary2[key])) list.Add(...)":
    // a second dictionary lookup plus a linear search of the list built so far, O(n*n) in the Sim's relationship count
    // (hundreds for a long-lived townie). callcensus (phase-20261119-*, 29 runs, 699 sim-h): 54,109 calls, 12.5-32.8 us
    // mean per call by run, 1,077 ms in all. Our path walks the same dictionary once by KeyValuePair (same order as
    // Keys: one Dictionary enumeration), adds each value once through a reference set (RefSet665), then runs EA's
    // MiniSimDescription loop word for word. Why it is the same list: Relationship overrides neither Equals nor
    // GetHashCode and has no subclass in EA's assemblies [verified, decompiled], so List.Contains is a reference compare;
    // the check below confirms at runtime that Equals is still Object's, and any value of another runtime type (a mod
    // subclass) sends the whole call to EA's body. All our reads have no side effect, so a fallback after a partial walk
    // is EA's call as if ours never ran. Exceptions EA would throw (null Sim, null dictionary) go to EA's body, which
    // throws them. Rides the callcensus hook on the same method (CcStatic.Mini), so no new swap and the census times
    // both arms. Worker thread (docs/128 G-M3): EA's body, no switch read, no counters.
    // Below AlgoSweepCore.kSetMin entries our path keeps EA's linear Contains (no set allocated), still one walk.
    // Counters [4233] calls answered by our path, [4234] calls sent to EA's body while on (null Sim or table, a value of
    // another type, Equals overridden), [4235] errors.
    public static partial class Fixes
    {
        static readonly int kSwMiniRelSet = SwSlot("minirelset");
        static bool sMrsTried, sMrsTypeOk;

        // null = run EA's body
        static List<IMiniRelationship> MiniRelSet(SimDescription s)
        {
            if (OnWorker() || !SwOn(kSwMiniRelSet)) return null;
            int[] c = PatchCounters();
            try
            {
                List<IMiniRelationship> r = MiniRelSetBody(s);
                c[r != null ? 4233 : 4234]++;
                return r;
            }
            catch (Exception) { c[4235]++; return null; }
        }

        // The exact path, switch ignored (the check calls it directly).
        static List<IMiniRelationship> MiniRelSetBody(SimDescription s)
        {
            if (!sMrsTried)
            {
                sMrsTried = true;
                MethodInfo eq = typeof(Relationship).GetMethod("Equals", new Type[] { typeof(object) });
                MethodInfo hc = typeof(Relationship).GetMethod("GetHashCode", Type.EmptyTypes);
                sMrsTypeOk = eq != null && eq.DeclaringType == typeof(object) && hc != null && hc.DeclaringType == typeof(object);
                AppliedLog().Add("minirelset: " + (sMrsTypeOk ? "ok" : "Relationship overrides Equals, EA's body kept"));
            }
            Dictionary<SimDescription, Dictionary<SimDescription, Relationship>> all = Relationship.sAllRelationships;
            if (!sMrsTypeOk || s == null || all == null) return null;  // EA's body (it throws for a null Sim or table)
            List<IMiniRelationship> list = new List<IMiniRelationship>();
            Dictionary<ulong, bool> ids = new Dictionary<ulong, bool>();
            Dictionary<SimDescription, Relationship> d2;
            if (all.TryGetValue(s, out d2))  // EA: ContainsKey then the indexer, the same entry
            {
                if (d2 == null) return null;
                RefSet665 seen = d2.Count >= AlgoSweepCore.kSetMin ? new RefSet665(d2.Count) : null;
                foreach (KeyValuePair<SimDescription, Relationship> kv in d2)
                {
                    SimDescription key = kv.Key;
                    ulong id = key.SimDescriptionId;  // a null key cannot be in a Dictionary
                    if (!ids.ContainsKey(id)) ids.Add(id, true);
                    Relationship v = kv.Value;
                    if (v != null && v.GetType() != typeof(Relationship)) return null;  // a subclass may override Equals
                    if (seen != null ? seen.Add(v) : !list.Contains(v)) list.Add(v);
                }
            }
            MiniSimDescription m = MiniSimDescription.Find(s.SimDescriptionId);
            if (m != null)
                foreach (MiniRelationship r in m.MiniRelationships)
                    if (!ids.ContainsKey(r.SimDescriptionId)) list.Add(r);
            return list;
        }

        // Forced live check (repro minirelset-check, agent reflect_call). run: for up to 400 Sims with relationships, EA's
        // body against our path (same count, same object at every index), each timed; then one call through the hooked
        // method, which must be answered by our path with the switch on. Off = the O(n*n) path in use (BROKEN by the check
        // convention for a speedup), on = FIXED.
        public static object MiniRelSetCheck(string what)
        {
            string counters = B582Counters("minirelset", 4233, new[] { "calls answered by our path", "calls to EA's body while on", "errors" }, "");
            if (what == "result") return counters;
            if (what != "run") return "minirelset <run|result>";
            bool on = ShortcutOn("minirelset");
            string missing = "";
            int sims = 0, diff = 0, maxN = 0, ours = 0;
            uint eaUs = 0, ourUs = 0;
            bool viaHook = false;
            try
            {
                Dictionary<SimDescription, Dictionary<SimDescription, Relationship>> all = Relationship.sAllRelationships;
                if (all == null) missing = "Relationship.sAllRelationships (no world)";
                else
                {
                    List<SimDescription> keys = new List<SimDescription>(all.Keys);
                    SimDescription biggest = null;
                    foreach (SimDescription s in keys)
                    {
                        if (s == null || sims >= 400) continue;
                        sims++;
                        uint t0 = SrcNowUs();
                        List<IMiniRelationship> ea = CcStatic.MiniOrig(s);
                        uint t1 = SrcNowUs();
                        List<IMiniRelationship> mine = MiniRelSetBody(s);
                        uint t2 = SrcNowUs();
                        eaUs += unchecked(t1 - t0); ourUs += unchecked(t2 - t1);
                        if (mine == null) { diff++; continue; }
                        ours++;
                        bool same = ea != null && ea.Count == mine.Count;
                        for (int i = 0; same && i < ea.Count; i++) same = ReferenceEquals(ea[i], mine[i]);
                        if (!same) diff++;
                        if (ea != null && ea.Count > maxN) { maxN = ea.Count; biggest = s; }
                    }
                    if (biggest != null)
                    {
                        int[] c = PatchCounters();
                        int before = c[4233];
                        Relationship.GetMiniRelationships(biggest);
                        viaHook = c[4233] > before;
                    }
                }
            }
            catch (Exception e) { missing = EpJoin(missing, "threw " + e.GetType().Name); }
            bool controls = missing.Length == 0 && diff == 0 && sims > 0;
            bool fixedNow = on && controls && viaHook;
            string ctl = "Sims " + sims + ", answered by our path " + ours + ", lists that differ " + diff + ", most relationships " + maxN
                + ", EA " + eaUs + " us, ours " + ourUs + " us, hooked call via our path=" + viaHook;
            return B582Line("minirelset", on, "GetMiniRelationships answered by the O(n) path, same list as EA", fixedNow, ctl, controls, missing, counters);
        }
    }
}
