using System;
using System.Collections.Generic;
using Ts3Fixes;

// Offline self-check of mods/ts3fixes/src/VenueMemoCore.cs (docs/122 §14.24): TuningMemo against a dictionary model.
static class VenueMemoTest
{
    static int sFail;
    static void Check(bool ok, string what) { if (!ok) { sFail++; Console.WriteLine("FAIL " + what); } }

    static int Main()
    {
        const int types = 40;
        TuningMemo m = new TuningMemo().Size(types);
        Dictionary<int, object> model = new Dictionary<int, object>();
        object[] answers = { new object(), new object(), new object(), null };
        Random r = new Random(1234);
        int world = 1, wtype = 1;
        Check(m.World(world, wtype) == false, "first world on an empty table is not a clear");
        for (int i = 0; i < 20000; i++)
        {
            int op = r.Next(10);
            int t = r.Next(-3, types + 3);
            if (op < 4)
            {
                object a = answers[r.Next(answers.Length)];
                m.Store(t, a);
                if (a != null && t >= 0 && t < types) model[t] = a;
            }
            else if (op < 9)
            {
                object got, want;
                bool hit = m.TryGet(t, out got);
                bool mhit = model.TryGetValue(t, out want);
                Check(hit == mhit && ReferenceEquals(got, mhit ? want : null), "get " + t + " at op " + i);
            }
            else
            {
                int nw = r.Next(3), nt = r.Next(2);
                bool change = nw != world || nt != wtype;
                bool cleared = m.World(nw, nt);
                Check(cleared == (change && model.Count > 0), "world change clear flag at op " + i);
                if (change) model.Clear();
                world = nw; wtype = nt;
            }
            Check(m.Count == model.Count, "count at op " + i);
        }
        m.Store(5, answers[0]);
        Check(m.Clear() && !m.Clear(), "Clear reports kept answers once");
        Check(!m.World(world, wtype), "same world after Clear is no change");
        m.Store(5, null);
        object o;
        Check(!m.TryGet(5, out o), "null answers are never kept");
        TuningMemo unsized = new TuningMemo();
        unsized.Store(0, answers[0]);
        Check(!unsized.TryGet(0, out o) && !unsized.Clear(), "unsized table keeps nothing");

        Check(TuningMemo.SameFloat(1.5f, 1.5f), "same float");
        Check(TuningMemo.SameFloat(float.NaN, float.NaN), "NaN equals NaN");
        Check(!TuningMemo.SameFloat(0f, -0f), "+0 and -0 differ by bits");
        Check(!TuningMemo.SameFloat(1f, 1.0000001f), "one ulp differs");

        Console.WriteLine(sFail == 0 ? "venuememo ok" : "venuememo FAILED: " + sFail);
        return sFail == 0 ? 0 : 1;
    }
}
