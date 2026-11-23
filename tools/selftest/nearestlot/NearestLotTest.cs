using System;
using Ts3Fixes;

// Offline self-check of NearestLotCore (docs/677): the lazy pick returns the item EA's loop returns, for random lists
// with equal distances, NaN, infinity and float.MaxValue, and never tests more items than EA's loop.
static class NearestLotTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; if (sFails < 20) Console.WriteLine("FAIL " + what); } }

    class Case
    {
        public bool[] Ok;
        public int Calls;
        public bool Valid(int i) { Calls++; return Ok[i]; }
    }

    static int Main()
    {
        Random r = new Random(677);
        float[] odd = new float[] { float.NaN, float.PositiveInfinity, float.MaxValue, 0f, -0f, float.Epsilon };
        int cases = 0, lazyTests = 0, eaTests = 0;
        for (int k = 0; k < 20000; k++)
        {
            int n = r.Next(0, 40);
            float[] d = new float[n];
            Case c = new Case();
            c.Ok = new bool[n];
            int validPct = r.Next(0, 101);
            for (int i = 0; i < n; i++)
            {
                int kind = r.Next(0, 10);
                d[i] = kind == 0 ? odd[r.Next(odd.Length)] : kind < 5 ? r.Next(0, 6) : (float)(r.NextDouble() * 1000.0);
                c.Ok[i] = r.Next(0, 100) < validPct;
            }
            int a, b;
            int ea = NearestLotCore.EaPick(d, c.Valid, out a);
            c.Calls = 0;
            int mine = NearestLotCore.LazyPick(d, c.Valid, out b);
            Check(ea == mine, "case " + k + ": EA " + ea + " lazy " + mine);
            Check(b == c.Calls && b <= a, "case " + k + ": tests " + b + " calls " + c.Calls + " EA " + a);
            cases++; lazyTests += b; eaTests += a;
        }
        // fixed shapes: the nearest valid one after two nearer invalid ones; equal distances keep list order; nothing valid
        Case f = new Case();
        int t;
        f.Ok = new bool[] { true, false, false, true };
        Check(NearestLotCore.LazyPick(new float[] { 9f, 1f, 2f, 5f }, f.Valid, out t) == 3 && t == 3, "nearer invalid lots");
        f.Ok = new bool[] { true, true, true };
        Check(NearestLotCore.LazyPick(new float[] { 4f, 4f, 4f }, f.Valid, out t) == 0 && t == 1, "equal distances");
        f.Ok = new bool[] { false, false };
        Check(NearestLotCore.LazyPick(new float[] { 1f, 2f }, f.Valid, out t) == -1 && t == 2, "nothing valid");
        f.Ok = new bool[] { true };
        Check(NearestLotCore.LazyPick(new float[] { float.MaxValue }, f.Valid, out t) == -1 && t == 0, "float.MaxValue is never taken");
        Console.WriteLine("nearestlot selftest: " + cases + " random lists, lazy tests " + lazyTests + " against EA's " + eaTests + ", fails " + sFails);
        return sFails == 0 ? 0 : 1;
    }
}
