// Offline self-check of commodmapfast (mods/ts3fixes/src/CommodMapFastCore.cs, docs/313). The reference is EA's
// RemoveObject rule written against a plain dictionary: for each (commodity, pair) in order, list.Remove(pair) (Mono's
// List<T>.Remove: first element e with pair.Equals(e)), then drop the key when the list is empty. Random maps with
// duplicate pairs, nulls, already empty lists, pairs that are not in the map and shared definitions; the batch plan
// must leave the same keys, the same list objects and the same entries in the same order.
using System;
using System.Collections.Generic;
using Ts3Fixes;

static class CommodMapFastTest
{
    static int fails;
    static void Check(bool ok, string what) { if (!ok) { fails++; Console.WriteLine("FAIL " + what); } }

    static long sEquals;
    // InteractionObjectPair's rule with a definition that keeps object.Equals (reference)
    sealed class Pair
    {
        public readonly object Def, Target;
        public Pair(object d, object t) { Def = d; Target = t; }
        public override bool Equals(object obj)
        {
            sEquals++;
            Pair o = obj as Pair;
            return o != null && Def.Equals(o.Def) && Target == o.Target;
        }
        public override int GetHashCode() { return Def.GetHashCode(); }
    }
    static object KeyA(Pair p) { return p.Def; }
    static object KeyB(Pair p) { return p.Target; }

    static Dictionary<ulong, List<Pair>> Clone(Dictionary<ulong, List<Pair>> d)
    {
        Dictionary<ulong, List<Pair>> c = new Dictionary<ulong, List<Pair>>();
        foreach (KeyValuePair<ulong, List<Pair>> kv in d) c[kv.Key] = new List<Pair>(kv.Value);
        return c;
    }

    static void EaRemove(Dictionary<ulong, List<Pair>> d, List<ulong> cs, List<Pair> ps)
    {
        for (int i = 0; i < cs.Count; i++)
        {
            List<Pair> l;
            if (!d.TryGetValue(cs[i], out l)) continue;
            l.Remove(ps[i]);
            if (l.Count == 0) d.Remove(cs[i]);
        }
    }

    static string Diff(Dictionary<ulong, List<Pair>> a, Dictionary<ulong, List<Pair>> b)
    {
        if (a.Count != b.Count) return "key count " + a.Count + " vs " + b.Count;
        foreach (KeyValuePair<ulong, List<Pair>> kv in a)
        {
            List<Pair> o;
            if (!b.TryGetValue(kv.Key, out o)) return "key " + kv.Key + " missing";
            if (o.Count != kv.Value.Count) return "key " + kv.Key + " count " + kv.Value.Count + " vs " + o.Count;
            for (int i = 0; i < o.Count; i++) if (!ReferenceEquals(o[i], kv.Value[i])) return "key " + kv.Key + " entry " + i;
        }
        return null;
    }

    static int Main()
    {
        Random rnd = new Random(1121);
        long eaEq = 0, fastCmp = 0;
        int cases = 0;
        for (int iter = 0; iter < 4000; iter++)
        {
            int nDefs = 1 + rnd.Next(6), nTargets = 1 + rnd.Next(5), nComm = 1 + rnd.Next(4);
            object[] defs = new object[nDefs], targets = new object[nTargets];
            for (int i = 0; i < nDefs; i++) defs[i] = new object();
            for (int i = 0; i < nTargets; i++) targets[i] = rnd.Next(8) == 0 ? null : new object();
            List<Pair> pool = new List<Pair>();
            for (int i = 0; i < 12; i++) pool.Add(new Pair(defs[rnd.Next(nDefs)], targets[rnd.Next(nTargets)]));
            Dictionary<ulong, List<Pair>> map = new Dictionary<ulong, List<Pair>>();
            for (int c = 0; c < nComm; c++)
            {
                if (rnd.Next(5) == 0) continue;  // no list
                List<Pair> l = new List<Pair>();
                int len = rnd.Next(5) == 0 ? 0 : rnd.Next(40);  // empty list as StreamOut leaves it
                for (int i = 0; i < len; i++)
                {
                    int r = rnd.Next(20);
                    // duplicates: the same object again, or an equal copy (same def and target)
                    l.Add(r == 0 ? null : r < 12 ? pool[rnd.Next(pool.Count)] : r < 16 && l.Count > 0 && l[l.Count - 1] != null ? new Pair(l[l.Count - 1].Def, l[l.Count - 1].Target) : new Pair(defs[rnd.Next(nDefs)], targets[rnd.Next(nTargets)]));
                }
                map[(ulong)c] = l;
            }
            List<ulong> cs = new List<ulong>();
            List<Pair> ps = new List<Pair>();
            int nOps = rnd.Next(25);
            for (int i = 0; i < nOps; i++)
            {
                cs.Add((ulong)rnd.Next(nComm + 1));  // nComm: a commodity with no list at all
                int r = rnd.Next(4);
                ps.Add(r == 0 ? new Pair(defs[rnd.Next(nDefs)], targets[rnd.Next(nTargets)]) : r == 1 ? new Pair(new object(), targets[0]) : pool[rnd.Next(pool.Count)]);
            }
            Dictionary<ulong, List<Pair>> ea = Clone(map), fast = Clone(map), shadow = Clone(map);
            Dictionary<ulong, List<Pair>> fastLists = new Dictionary<ulong, List<Pair>>(fast);  // list objects before
            sEquals = 0;
            EaRemove(ea, cs, ps);
            eaEq += sEquals;
            CmfListFn<Pair> lookFast = delegate(ulong c) { List<Pair> l; return fast.TryGetValue(c, out l) ? l : null; };
            sEquals = 0;
            CmfPlan<Pair> plan = CommodMapFastCore.Plan(cs, ps, lookFast, KeyA, KeyB);
            Check(sEquals == 0, "plan calls no Equals");
            fastCmp += plan.Compared;
            CommodMapFastCore.Apply(plan, delegate(ulong c) { fast.Remove(c); });
            string d = Diff(ea, fast);
            if (d != null) { Check(false, "iter " + iter + ": " + d); continue; }
            foreach (KeyValuePair<ulong, List<Pair>> kv in fast) Check(ReferenceEquals(kv.Value, fastLists[kv.Key]), "iter " + iter + ": same list object");
            // shadow: plan, EA's rule runs, compare = no mismatch
            CmfListFn<Pair> lookSh = delegate(ulong c) { List<Pair> l; return shadow.TryGetValue(c, out l) ? l : null; };
            CmfPlan<Pair> sp = CommodMapFastCore.Plan(cs, ps, lookSh, KeyA, KeyB);
            List<Pair[]> exp = CommodMapFastCore.Expected(sp);
            EaRemove(shadow, cs, ps);
            string m = CommodMapFastCore.Compare(sp, exp, lookSh);
            Check(m == null, "iter " + iter + ": shadow mismatch on the exact rule: " + m);
            cases++;
        }

        // shadow must see a wrong result: drop a different element than EA would
        {
            object d1 = new object(), t1 = new object();
            Pair a = new Pair(d1, t1), b = new Pair(d1, t1), x = new Pair(new object(), t1);
            Dictionary<ulong, List<Pair>> m = new Dictionary<ulong, List<Pair>>();
            m[7] = new List<Pair>(new Pair[] { x, a, b });
            CmfListFn<Pair> look = delegate(ulong c) { List<Pair> l; return m.TryGetValue(c, out l) ? l : null; };
            CmfPlan<Pair> p = CommodMapFastCore.Plan(new List<ulong>(new ulong[] { 7 }), new List<Pair>(new Pair[] { b }), look, KeyA, KeyB);
            Check(p.Remove[0].Count == 1 && p.Remove[0][0] == 1, "duplicate: the first equal element (a, index 1) goes, not b itself");
            List<Pair[]> exp = CommodMapFastCore.Expected(p);
            m[7].RemoveAt(2);  // a wrong engine: removed b
            Check(CommodMapFastCore.Compare(p, exp, look) != null, "shadow reports a wrong removal");
            m[7] = new List<Pair>(new Pair[] { x, b });
            Check(CommodMapFastCore.Compare(p, exp, look) != null, "shadow reports a replaced list object");
            m.Remove(7);
            Check(CommodMapFastCore.Compare(p, exp, look) != null, "shadow reports a dropped key");
        }
        // the scan stops at the last needed element (EA compares nothing past it either)
        {
            object d1 = new object(), t1 = new object();
            List<Pair> l = new List<Pair>();
            for (int i = 0; i < 100; i++) l.Add(new Pair(new object(), new object()));
            Pair a = new Pair(d1, t1);
            l.Insert(10, a);
            List<int> rm = new List<int>();
            int n = CommodMapFastCore.PlanList(l, new List<Pair>(new Pair[] { a }), KeyA, KeyB, rm);
            Check(n == 11 && rm.Count == 1 && rm[0] == 10, "stop after the match (looked at " + n + ")");
        }

        Console.WriteLine("commodmapfast: " + cases + " random maps exact; EA Equals calls " + eaEq + ", batch elements looked at " + fastCmp);
        Console.WriteLine(fails == 0 ? "commodmapfast selftest: ok" : "commodmapfast selftest: " + fails + " failed");
        return fails == 0 ? 0 : 1;
    }
}
