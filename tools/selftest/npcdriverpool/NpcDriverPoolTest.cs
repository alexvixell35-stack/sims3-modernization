using System;
using System.Collections.Generic;
using Ts3Fixes;

// Offline self-check of NpcDriverPoolCore (issue #166 build 37, docs/514): every input combination of Bad, and PruneTop
// on stacks with bad entries on top, below a good one, all bad, and empty.
static class NpcDriverPoolTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; Console.WriteLine("FAIL " + what); } }

    static Stack<int> Make(params int[] bottomToTop) { Stack<int> s = new Stack<int>(); foreach (int v in bottomToTop) s.Push(v); return s; }

    static void Main()
    {
        for (int m = 0; m < 8; m++)
        {
            bool n = (m & 1) != 0, inv = (m & 2) != 0, out_ = (m & 4) != 0;
            Check(NpcDriverPoolCore.Bad(n, inv, out_) == (n || inv || out_), "bad " + m);
        }
        Predicate<int> bad = delegate (int v) { return v < 0; };
        Stack<int> s = Make(1, -2, -3);
        Check(NpcDriverPoolCore.PruneTop(s, bad) == 2 && s.Count == 1 && s.Peek() == 1, "two bad on top of a good one: both popped, the good one stays on top");
        s = Make(-1, 2);
        Check(NpcDriverPoolCore.PruneTop(s, bad) == 0 && s.Count == 2 && s.Peek() == 2, "a good one on top: nothing popped (EA reads only the top)");
        s = Make(-1, -2);
        Check(NpcDriverPoolCore.PruneTop(s, bad) == 2 && s.Count == 0, "all bad: the pool ends empty (EA then makes a new driver)");
        s = new Stack<int>();
        Check(NpcDriverPoolCore.PruneTop(s, bad) == 0, "an empty pool");
        Check(NpcDriverPoolCore.PruneTop<int>(null, bad) == 0, "no pool");
        Console.WriteLine(sFails == 0 ? "npcdriverpool selftest: ok" : "npcdriverpool selftest: " + sFails + " FAIL");
        Environment.Exit(sFails == 0 ? 0 : 1);
    }
}
