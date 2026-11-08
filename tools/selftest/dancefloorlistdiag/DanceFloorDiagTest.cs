using System;
using System.Collections.Generic;
using Ts3Fixes;

// Offline self-check of DanceFloorDiagCore (docs/253 §7, §11): each docs/253 §2 case and the order the checks win in; the
// dancefloorlistguard rules (WouldThrow, ResumeAt) against a real List<T> foreach.
static class DanceFloorDiagTest
{
    static int sFails;
    static void Check(int got, int want, string what) { if (got != want) { sFails++; Console.WriteLine("FAIL " + what + ": " + DanceFloorDiagCore.Name(got) + ", want " + DanceFloorDiagCore.Name(want)); } }

    static void Flag(bool ok, string what) { if (!ok) { sFails++; Console.WriteLine("FAIL " + what); } }

    static void Main()
    {
        Check(DanceFloorDiagCore.Classify(true, true, 7, 7, 2, 2, true), DanceFloorDiagCore.kSame, "nothing changed");
        Check(DanceFloorDiagCore.Classify(true, true, 7, 8, 2, 2, true), DanceFloorDiagCore.kWrite, "(c) version moved, same items");
        Check(DanceFloorDiagCore.Classify(true, true, 7, 8, 2, 3, false), DanceFloorDiagCore.kItems, "count changed");
        Check(DanceFloorDiagCore.Classify(true, true, 7, 7, 2, 2, false), DanceFloorDiagCore.kItems, "items changed, version not");
        Check(DanceFloorDiagCore.Classify(true, false, 7, 9, 2, 5, true), DanceFloorDiagCore.kFreed, "(a) weak reference cleared wins over version and count");
        Check(DanceFloorDiagCore.Classify(false, false, 7, 9, 2, 5, false), DanceFloorDiagCore.kFrame, "(b) enumerator words changed wins over all");
        Check(DanceFloorDiagCore.Classify(false, true, 7, 7, 2, 2, true), DanceFloorDiagCore.kFrame, "(b) with the list untouched");
        Flag(!DanceFloorDiagCore.WouldThrow(true, 7, 8), "routed: EA returns before MoveNext");
        Flag(!DanceFloorDiagCore.WouldThrow(false, 7, 7), "version unchanged: MoveNext goes on");
        Flag(DanceFloorDiagCore.WouldThrow(false, 7, 8), "version moved, not routed: MoveNext throws");
        // a list whose version moves during step 1 (EA's case): foreach throws, the guard's snapshot reaches the floor EA meant
        List<int> l = new List<int>(new int[] { 10, 20, 30 });
        int[] snap = l.ToArray();
        int ver0 = 0, steps = 0, eaPick = -1;
        bool threw = false;
        try { foreach (int f in l) { steps++; if (steps == 1) { l.Sort(); ver0 = 1; } if (f == 30) { eaPick = f; break; } } }
        catch (InvalidOperationException) { threw = true; }
        Flag(threw && eaPick < 0 && steps == 1, "premise: List<T> foreach throws after a version move (steps " + steps + ")");
        Flag(DanceFloorDiagCore.WouldThrow(false, 0, ver0), "WouldThrow agrees with the real throw");
        int pick = -1;
        for (int i = DanceFloorDiagCore.ResumeAt(steps); i < snap.Length; i++) if (snap[i] == 30) { pick = snap[i]; break; }
        Flag(pick == 30, "guard resumes at item " + steps + " and finds the floor that routes");
        Console.WriteLine(sFails == 0 ? "dancefloorlistdiag selftest: ok" : "dancefloorlistdiag selftest: " + sFails + " FAIL");
        Environment.Exit(sFails == 0 ? 0 : 1);
    }
}
