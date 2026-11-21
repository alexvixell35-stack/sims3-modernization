using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.Interactions;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // commodmapfast harness check (docs/313 §4). Throwaway CommodityInteractionMaps (disposed at the end) filled with the real
    // pairs of world objects; no object, lot map or Sim changes.
    public static partial class Fixes
    {
        // CommodMapFastCheck "run [objects] [copies] [every]": objects = world objects with interactions (default 150); copies =
        // extra Clone()s of each pair (a mod-heavy save: more pairs per object and equal duplicates, default 4); every = remove
        // each every-th object (default 2: half, so the lists that stay can show the order). Map A: EA's rule per pair
        // (RemoveInteractionForObject = RemoveObject's body); map B: the batch plan. Then the hook path: maps C / D with the
        // objects' own pairs, C by EA's rule, D through RemoveObject (the switch mode in force: off = EA, shadow, fast).
        // Verdict EXACT when A == B and C == D (keys, entries, order); times are per removal, EA vs batch.
        public static object CommodMapFastCheck(string what, int nObj, int copies, int every)
        {
            int[] c = PatchCounters();
            string counters = "commodmapfast: calls 1500=" + c[1500] + ", batch 1501=" + c[1501] + ", own Equals 1502=" + c[1502] + ", errors 1503=" + c[1503]
                + ", shadow compares 1504=" + c[1504] + ", mismatches 1505=" + c[1505] + ", EA us 1506=" + c[1506] + ", batch us 1507=" + c[1507] + (sCmfOff ? ", OFF for the session" : "");
            if (what == "result") return counters;
            if (what != "run") return "commodmapfast <run [objects] [copies] [every]|result>";
            if (nObj <= 0) nObj = 150;
            if (copies < 0) copies = 4;
            if (every <= 0) every = 2;
            if (sCmfTable == null) sCmfTable = typeof(CommodityInteractionMap).GetField("mTable", BindingFlags.Instance | BindingFlags.NonPublic);
            if (sCmfTable == null) return "commodmapfast check: CommodityInteractionMap.mTable not found; " + counters;
            List<GameObject> objs = new List<GameObject>();
            foreach (GameObject o in Sims3.Gameplay.Queries.GetObjects<GameObject>())
            {
                if (objs.Count >= nObj) break;
                if (o != null && o.Interactions != null && o.Interactions.Count > 0) objs.Add(o);
            }
            List<List<InteractionObjectPair>> pairs = new List<List<InteractionObjectPair>>();
            int nPairs = 0;
            foreach (GameObject o in objs)
            {
                List<InteractionObjectPair> l = new List<InteractionObjectPair>(o.Interactions);
                int own = l.Count;
                for (int k = 0; k < copies; k++)
                    for (int i = 0; i < own; i++) l.Add((InteractionObjectPair)o.Interactions[i].Clone());
                pairs.Add(l);
                nPairs += l.Count;
            }
            CommodityInteractionMap a = new CommodityInteractionMap("cmfcheck", "a"), b = new CommodityInteractionMap("cmfcheck", "b");
            CommodityInteractionMap mc = new CommodityInteractionMap("cmfcheck", "c"), md = new CommodityInteractionMap("cmfcheck", "d");
            try
            {
                foreach (List<InteractionObjectPair> l in pairs)
                    foreach (InteractionObjectPair p in l) { a.AddInteractionsForObject(p); b.AddInteractionsForObject(p); }
                int longest = 0;
                foreach (List<InteractionObjectPair> l in a.Interactions) longest = Math.Max(longest, l.Count);
                uint eaUs = 0, eaMax = 0, fastUs = 0, fastMax = 0;
                int removed = 0, fallbacks = 0;
                for (int i = 0; i < pairs.Count; i += every)
                {
                    removed++;
                    uint t0 = CmfNowUs();
                    foreach (InteractionObjectPair p in pairs[i]) a.RemoveInteractionForObject(p);
                    uint t1 = CmfNowUs();
                    int why;
                    CmfPlan<InteractionObjectPair> plan = CmfMakePlan(b, pairs[i], out why);
                    if (plan == null) { fallbacks++; foreach (InteractionObjectPair p in pairs[i]) b.RemoveInteractionForObject(p); }
                    else CmfApply(b, plan);
                    uint t2 = CmfNowUs();
                    eaUs += t1 - t0; eaMax = Math.Max(eaMax, t1 - t0);
                    fastUs += t2 - t1; fastMax = Math.Max(fastMax, t2 - t1);
                }
                string ab = CmfMapDiff(a, b);
                // hook path on the objects' own pairs
                foreach (GameObject o in objs) { mc.AddObject(o); md.AddObject(o); }
                int before = c[1504] + c[1501];
                for (int i = 0; i < objs.Count; i += every)
                {
                    foreach (InteractionObjectPair p in objs[i].Interactions) mc.RemoveInteractionForObject(p);
                    md.RemoveObject(objs[i]);
                }
                string cd = CmfMapDiff(mc, md);
                string mode = sCmfOff ? "off (session)" : ShortcutOn("commodmapshadow") ? "shadow" : ShortcutOn("commodmapfast") ? "fast" : "off";
                return "commodmapfast check: " + objs.Count + " objects, " + nPairs + " pairs (" + copies + " copies), longest list " + longest + ", " + removed + " removed; EA "
                    + eaUs / 1000 + " ms (max " + eaMax / 1000 + " ms per object), batch " + fastUs / 1000 + " ms (max " + fastMax / 1000 + " ms), "
                    + fallbacks + " fallbacks; batch vs EA: " + (ab ?? "same") + "; hook path (" + mode + ", " + (c[1504] + c[1501] - before) + " calls): " + (cd ?? "same")
                    + "; verdict: " + (ab == null && cd == null ? "EXACT" : "MISMATCH") + "; " + counters;
            }
            finally { a.Dispose(); b.Dispose(); mc.Dispose(); md.Dispose(); }
        }

        // null when both maps hold the same keys and the same entries in the same order
        static string CmfMapDiff(CommodityInteractionMap x, CommodityInteractionMap y)
        {
            if (x.CommoditiesSatisfied.Count != y.CommoditiesSatisfied.Count) return "key count " + x.CommoditiesSatisfied.Count + " vs " + y.CommoditiesSatisfied.Count;
            foreach (ulong k in x.CommoditiesSatisfied)
            {
                List<InteractionObjectPair> lx = x.InteractionsForCommodity((CommodityKind)k), ly = y.InteractionsForCommodity((CommodityKind)k);
                if (ly == null) return "key " + k + " missing";
                if (lx.Count != ly.Count) return "key " + k + ": " + lx.Count + " vs " + ly.Count + " entries";
                for (int i = 0; i < lx.Count; i++) if (!ReferenceEquals(lx[i], ly[i])) return "key " + k + ": entry " + i + " differs";
            }
            return null;
        }
    }
}
