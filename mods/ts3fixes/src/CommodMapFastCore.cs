using System;
using System.Collections.Generic;

namespace Ts3Fixes
{
    public delegate object CmfKeyFn<T>(T e);
    public delegate List<T> CmfListFn<T>(ulong commodity);
    public delegate void CmfDropFn(ulong commodity);

    // The removals of one CommodityInteractionMap.RemoveObject call, grouped per commodity list (first-seen order).
    public sealed class CmfPlan<T> where T : class
    {
        public readonly List<ulong> Keys = new List<ulong>();
        public readonly List<List<T>> Ops = new List<List<T>>();    // the pairs to remove from that list, in EA's order
        public readonly List<List<T>> Lists = new List<List<T>>();  // the list as found (null: no list, EA does nothing)
        public readonly List<List<int>> Remove = new List<List<int>>();  // indices into Lists[g], ascending
        public int Compared;  // list elements looked at (EA compares at least this many, usually k times more)
    }

    // Game-independent part of commodmapfast (CommodMapFast.cs, docs/313), checked offline by tools/selftest/commodmapfast.
    //
    // EA: for each pair p and each valid commodity c of p, in order, list(c).Remove(p), then the key goes when list(c) is empty.
    // List<T>.Remove calls EqualityComparer<T>.Default.Equals(p, e) = p.Equals(e) on each element from the start until the first
    // true (Mono 2 mscorlib Array.IndexOf<T> + DefaultComparer [verified, decompiled mscorlib]). The caller only plans when
    // p.Equals(e) is the plain InteractionObjectPair rule with a reference-equality definition, i.e.
    //   p.Equals(e) == (e != null && KeyA(e) == KeyA(p) && KeyB(e) == KeyB(p))  (references).
    // Then the pairs of one list with key K remove exactly the first n(K) elements with key K of the list as found (n(K) = pairs
    // with that key): elements with another key never decide which K element is first. One pass in list order does that and
    // keeps every other element in its order; it stops once every pair found its element, the point past which EA compares
    // nothing either. Lists of different commodities are separate objects, so the per-list grouping changes nothing.
    public static class CommodMapFastCore
    {
        // pairs in EA's order: cs[i] = commodity, ps[i] = pair. Nothing is changed.
        public static CmfPlan<T> Plan<T>(List<ulong> cs, List<T> ps, CmfListFn<T> lookup, CmfKeyFn<T> keyA, CmfKeyFn<T> keyB) where T : class
        {
            CmfPlan<T> plan = new CmfPlan<T>();
            for (int i = 0; i < cs.Count; i++)
            {
                int g = plan.Keys.IndexOf(cs[i]);
                if (g < 0)
                {
                    g = plan.Keys.Count;
                    plan.Keys.Add(cs[i]);
                    plan.Ops.Add(new List<T>());
                    plan.Lists.Add(lookup(cs[i]));
                    plan.Remove.Add(new List<int>());
                }
                plan.Ops[g].Add(ps[i]);
            }
            for (int g = 0; g < plan.Keys.Count; g++)
                plan.Compared += PlanList(plan.Lists[g], plan.Ops[g], keyA, keyB, plan.Remove[g]);
            return plan;
        }

        // One list: fills remove (ascending) and returns the elements looked at.
        public static int PlanList<T>(List<T> list, List<T> ops, CmfKeyFn<T> keyA, CmfKeyFn<T> keyB, List<int> remove) where T : class
        {
            remove.Clear();
            int k = ops.Count;
            if (list == null || k == 0) return 0;
            object[] ka = new object[k], kb = new object[k];
            int[] need = new int[k];
            int nk = 0;
            for (int i = 0; i < k; i++)
            {
                object a = keyA(ops[i]), b = keyB(ops[i]);
                int j = 0;
                while (j < nk && (ka[j] != a || kb[j] != b)) j++;
                if (j == nk) { ka[nk] = a; kb[nk] = b; nk++; }
                need[j]++;
            }
            object t0 = kb[0];
            bool oneTarget = true;
            for (int j = 1; j < nk; j++) if (kb[j] != t0) { oneTarget = false; break; }
            int left = k, n = list.Count, at = 0;
            for (; at < n && left > 0; at++)
            {
                T e = list[at];
                if (e == null) continue;  // p.Equals(null) is false
                object eb = keyB(e);
                if (oneTarget && eb != t0) continue;  // the usual case: every pair of the object targets the object
                object ea = keyA(e);
                for (int j = 0; j < nk; j++)
                    if (need[j] > 0 && ka[j] == ea && kb[j] == eb) { remove.Add(at); need[j]--; left--; break; }
            }
            return at;
        }

        // Applies the plan: same removals as EA, then the key goes for each planned list left empty (EA drops the key after any
        // Remove that leaves the list empty, also an already empty one).
        public static void Apply<T>(CmfPlan<T> plan, CmfDropFn drop) where T : class
        {
            for (int g = 0; g < plan.Keys.Count; g++)
            {
                List<T> list = plan.Lists[g];
                if (list == null) continue;
                List<int> rm = plan.Remove[g];
                for (int r = rm.Count - 1; r >= 0; r--) list.RemoveAt(rm[r]);
                if (list.Count == 0) drop(plan.Keys[g]);
            }
        }

        // Shadow: what each planned list must hold after EA's run (null = key gone). Call before EA runs.
        public static List<T[]> Expected<T>(CmfPlan<T> plan) where T : class
        {
            List<T[]> exp = new List<T[]>(plan.Keys.Count);
            for (int g = 0; g < plan.Keys.Count; g++)
            {
                List<T> list = plan.Lists[g];
                if (list == null) { exp.Add(null); continue; }
                List<int> rm = plan.Remove[g];
                T[] e = new T[list.Count - rm.Count];
                int r = 0, o = 0;
                for (int i = 0; i < list.Count; i++)
                {
                    if (r < rm.Count && rm[r] == i) { r++; continue; }
                    e[o++] = list[i];
                }
                exp.Add(e.Length == 0 ? null : e);
            }
            return exp;
        }

        // Shadow: null when the map after EA's run matches the plan, else what differs.
        public static string Compare<T>(CmfPlan<T> plan, List<T[]> exp, CmfListFn<T> lookup) where T : class
        {
            for (int g = 0; g < plan.Keys.Count; g++)
            {
                List<T> now = lookup(plan.Keys[g]);
                T[] e = exp[g];
                if (e == null)
                {
                    if (now != null) return "commodity " + plan.Keys[g] + ": key kept with " + now.Count + " entries, plan dropped it";
                    continue;
                }
                if (now == null) return "commodity " + plan.Keys[g] + ": key dropped, plan kept " + e.Length;
                if (!ReferenceEquals(now, plan.Lists[g])) return "commodity " + plan.Keys[g] + ": another list object";
                if (now.Count != e.Length) return "commodity " + plan.Keys[g] + ": " + now.Count + " entries, plan " + e.Length;
                for (int i = 0; i < e.Length; i++)
                    if (!ReferenceEquals(now[i], e[i])) return "commodity " + plan.Keys[g] + ": entry " + i + " differs";
            }
            return null;
        }
    }
}
