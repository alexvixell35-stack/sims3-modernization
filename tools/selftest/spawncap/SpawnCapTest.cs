using System;
using Ts3Fixes;

// D1 claim: for any world, "total < cap" implies EA's walk returns true, so D1 (guards && (UnderCap ? true : walk)) equals
// EA (guards && walk) for every input. Checked on fixed cases and random worlds against EA's walk written out.
static class SpawnCapTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; Console.WriteLine("FAIL " + what); } }

    static void Main()
    {
        Check(SpawnCapCore.UnderCap(0, 20) && SpawnCapCore.UnderCap(19, 20), "below the cap");
        Check(!SpawnCapCore.UnderCap(20, 20) && !SpawnCapCore.UnderCap(21, 20), "at or over the cap: EA walks");
        Check(!SpawnCapCore.UnderCap(0, 0) && !SpawnCapCore.UnderCap(0, -1), "cap 0 or less: EA walks");
        Check(!SpawnCapCore.UnderCap(uint.MaxValue, int.MaxValue), "no uint wrap");
        Check(SpawnCapCore.EaWalk(new bool[0], 0), "EA: empty world, cap 0 = true");
        Check(!SpawnCapCore.EaWalk(new[] { true }, 0), "EA: one counted object, cap 0 = false");

        Random r = new Random(1);
        for (int i = 0; i < 20000; i++)
        {
            int n = r.Next(0, 30), cap = r.Next(-2, 25);
            bool[] f = new bool[n];
            for (int k = 0; k < n; k++) f[k] = r.Next(3) != 0;
            bool guards = r.Next(4) != 0;
            bool ea = guards && SpawnCapCore.EaWalk(f, cap);
            bool d1 = guards && (SpawnCapCore.UnderCap((uint)n, cap) || SpawnCapCore.EaWalk(f, cap));
            if (ea != d1) { Check(false, "random world n=" + n + " cap=" + cap); break; }
        }

        Console.WriteLine(sFails == 0 ? "spawncap selftest ok" : "spawncap: " + sFails + " FAIL");
        Environment.Exit(sFails == 0 ? 0 : 1);
    }
}
