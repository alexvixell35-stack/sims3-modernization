using System;
using System.Collections.Generic;
using Ts3Fixes;

// Offline self-check of EventCostCore (docs/545 §5): a flat call, a call with two nested calls (one of them nested
// again), a negative time, and Reset.
static class EventCostTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; Console.WriteLine("FAIL " + what); } }
    static long[] Row(Dictionary<ulong, long[]> t, ulong id) { long[] r; return t.TryGetValue(id, out r) ? r : new long[4]; }

    static void Main()
    {
        Dictionary<ulong, long[]> t = new Dictionary<ulong, long[]>();
        long s = EventCostCore.Enter(); EventCostCore.Exit(t, 1, 100, s);
        Check(Row(t, 1)[0] == 1 && Row(t, 1)[1] == 100 && Row(t, 1)[2] == 100 && Row(t, 1)[3] == 100, "flat call");

        // id 2 (500 us) runs id 3 (200 us, which runs id 4 for 50 us) and id 4 (30 us)
        long s2 = EventCostCore.Enter();
        long s3 = EventCostCore.Enter();
        long s4 = EventCostCore.Enter(); EventCostCore.Exit(t, 4, 50, s4);
        EventCostCore.Exit(t, 3, 200, s3);
        long s4b = EventCostCore.Enter(); EventCostCore.Exit(t, 4, 30, s4b);
        EventCostCore.Exit(t, 2, 500, s2);
        Check(Row(t, 2)[1] == 500 && Row(t, 2)[2] == 270, "outer: inclusive 500, exclusive 500 - 200 - 30");
        Check(Row(t, 3)[1] == 200 && Row(t, 3)[2] == 150, "middle: exclusive 200 - 50");
        Check(Row(t, 4)[0] == 2 && Row(t, 4)[1] == 80 && Row(t, 4)[2] == 80 && Row(t, 4)[3] == 50, "inner twice: 50 + 30, max 50");

        // the next top-level call starts from zero child time
        long s5 = EventCostCore.Enter(); EventCostCore.Exit(t, 5, 40, s5);
        Check(Row(t, 5)[2] == 40, "top-level after nesting: no leftover child time");

        long s6 = EventCostCore.Enter(); EventCostCore.Exit(t, 6, -7, s6);
        Check(Row(t, 6)[0] == 1 && Row(t, 6)[1] == 0 && Row(t, 6)[2] == 0, "negative time counts the call as 0 us");

        EventCostCore.Reset(t);
        Check(t.Count == 0, "reset empties the table");
        Console.WriteLine(sFails == 0 ? "eventcost selftest: ok" : "eventcost selftest: " + sFails + " FAIL");
        Environment.Exit(sFails == 0 ? 0 : 1);
    }
}
