using System;
using Ts3Fixes;

// Offline self-check of BgoTable (boatgetoutdiag, docs/257 §7).
static class BoatGetOutDiagTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; Console.WriteLine("FAIL " + what); } }

    static void Main()
    {
        BgoTable t = new BgoTable();
        object sim = new object(), other = new object(), sit = new object(), stand = new object();
        int at;
        Check(t.Classify(sim, sit, 1, out at) == BgoTable.kIgnore && at == -1, "no watch: ignore");

        int a = t.Push(sim, sit, 1);
        Check(a >= 0 && t.N == 1 && t.Depth[a] == 1, "push: slot, N 1, depth 1");
        Check(t.Classify(sim, sit, 1, out at) == BgoTable.kOwnEnd && at == a, "own task, depth 1: normal end");
        Check(t.Classify(sim, sit, 2, out at) == BgoTable.kForeign && at == a, "another task: foreign");
        Check(t.Classify(sim, stand, 2, out at) == BgoTable.kIgnore, "current posture is not the watched SittingInBoat: ignore");
        Check(t.Classify(other, sit, 2, out at) == BgoTable.kIgnore, "another Sim: ignore");

        // reset path: SittingInBoat.OnReset -> GetOut(bOnReset) on the resetting task, same entry posture
        int b = t.Push(sim, sit, 2);
        Check(b >= 0 && b != a && t.Depth[b] == 2 && t.N == 2, "nested push: depth 2");
        Check(t.Classify(sim, sit, 2, out at) == BgoTable.kNested && at == a, "nested GetOut's pop: nested, booked on the outer watch");
        Check(t.Classify(sim, sit, 1, out at) == BgoTable.kNested && at == a, "own task while a nested GetOut is in flight: nested");
        t.Pop(b);
        Check(t.N == 1 && t.Classify(sim, sit, 1, out at) == BgoTable.kOwnEnd, "after the nested pop: own end again");
        t.Pop(b);
        Check(t.N == 1, "double pop is a no-op");
        t.Pop(-1);
        Check(t.N == 1, "pop of -1 is a no-op");
        t.Pop(a);
        Check(t.N == 0 && t.Classify(sim, sit, 2, out at) == BgoTable.kIgnore, "all popped: ignore");

        for (int i = 0; i < BgoTable.kMax; i++) Check(t.Push(new object(), sit, 1) >= 0, "fill " + i);
        Check(t.Push(sim, sit, 1) == -1 && t.Overflow == 1 && t.N == BgoTable.kMax, "full: -1, overflow counted, N unchanged");
        Console.WriteLine(sFails == 0 ? "boatgetoutdiag selftest: ok" : "boatgetoutdiag selftest: " + sFails + " FAIL");
        if (sFails != 0) Environment.Exit(1);
    }
}
