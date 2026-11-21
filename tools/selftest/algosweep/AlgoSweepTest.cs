using System;
using System.Collections.Generic;
using Ts3Fixes;

// Offline self-check of AlgoSweepCore (docs/665): RefSet665 dedup against EA's "if (!list.Contains(v)) list.Add(v)" on
// random sequences (repeated references, nulls, short and long), membership against List.Contains, and the counter.
// Node models EA's Relationship / ActiveNodeBase / Sim: a class without an Equals override, so List.Contains compares
// references; two Nodes with the same Id are different objects and must both be kept.
static class AlgoSweepTest
{
    sealed class Node { public int Id; public Node(int id) { Id = id; } }

    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; Console.WriteLine("FAIL " + what); } }

    static bool SameList(List<Node> a, List<Node> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++) if (!ReferenceEquals(a[i], b[i])) return false;
        return true;
    }

    static void Main()
    {
        Random rnd = new Random(665);
        for (int round = 0; round < 500; round++)
        {
            int pool = 1 + rnd.Next(60), len = rnd.Next(300);
            Node[] objs = new Node[pool];
            for (int i = 0; i < pool; i++) objs[i] = new Node(i % 7);  // equal ids on different objects
            List<Node> seq = new List<Node>();
            for (int i = 0; i < len; i++) seq.Add(rnd.Next(10) == 0 ? null : objs[rnd.Next(pool)]);

            List<Node> ea = new List<Node>();
            AlgoSweepCore.AppendDistinctNaive(seq, ea);
            List<Node> ours = new List<Node>();
            RefSet665 set = new RefSet665(seq.Count);
            AlgoSweepCore.AppendDistinct(seq, ours, set);
            Check(SameList(ea, ours), "round " + round + ": same members in the same order (" + ea.Count + " vs " + ours.Count + ")");
            Check(set.Count == ea.Count, "round " + round + ": set count is the list count");
            foreach (Node o in objs) Check(set.Contains(o) == ea.Contains(o), "round " + round + ": membership of a pool object");
            Check(set.Contains(null) == ea.Contains(null), "round " + round + ": membership of null");
            Check(!set.Contains(new Node(0)), "round " + round + ": a fresh object with an equal id is not a member");
        }

        RefSet665 s = new RefSet665();
        Node a = new Node(1), b = new Node(1);
        Check(s.Add(a) && !s.Add(a) && s.Add(b) && s.Add(null) && !s.Add(null), "Add answers new / seen, null once");
        Check(s.Count == 3, "count 3 (a, b, null)");
        List<Node> l = new List<Node>(); l.Add(a); l.Add(null);
        RefSet665 t = new RefSet665(); t.AddAll(l);
        Check(t.Contains(a) && t.Contains(null) && !t.Contains(b), "AddAll then Contains");
        Check(AlgoSweepCore.kSetMin >= 1, "kSetMin positive");

        Console.WriteLine(sFails == 0 ? "algosweep selftest: ok" : "algosweep selftest: " + sFails + " FAIL");
        Environment.Exit(sFails == 0 ? 0 : 1);
    }
}
