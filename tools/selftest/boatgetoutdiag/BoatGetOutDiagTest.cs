using System;
using Ts3Fixes;

// Offline self-check of BgoTable (boatgetoutdiag, docs/257 §7) and the boatgetoutguard decision (§9).
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
        // boatgetoutguard decision (docs/257 §9): only a stale NPC is swallowed
        Check(BgoTable.GuardDecide(false, true, true) == BgoTable.kGuardRethrow, "guard off / Sim not in this boat at entry: EA's error");
        Check(BgoTable.GuardDecide(true, false, true) == BgoTable.kGuardRethrow, "posture still the entry SittingInBoat: another NRE, EA's error");
        Check(BgoTable.GuardDecide(true, true, true) == BgoTable.kGuardSwallow, "stale NPC: swallowed");
        Check(BgoTable.GuardDecide(true, true, false) == BgoTable.kGuardHousehold, "stale household / selected / unknown scope: kept");
        Check(BgoTable.GuardDecide(false, false, false) == BgoTable.kGuardRethrow, "household, not stale: EA's error");
        // the log lines (run.ts tests the queue regexes against these)
        Console.WriteLine("LINE " + BgoTable.GuardLine("Mylee Arias", 0x1a2bUL, "StandingPosture", true));
        Console.WriteLine("LINE " + BgoTable.GuardLine("Lala Dregg", 0x3cUL, "StandingPosture", false));
        Check(BgoTable.GuardLine("A B", 0xffUL, "null", true).StartsWith("boatgetout guard: A B 0xff npc SittingInBoat->null after the fade"), "npc line format");
        Console.WriteLine(sFails == 0 ? "boatgetoutdiag selftest: ok" : "boatgetoutdiag selftest: " + sFails + " FAIL");
        if (sFails != 0) Environment.Exit(1);
    }
}
