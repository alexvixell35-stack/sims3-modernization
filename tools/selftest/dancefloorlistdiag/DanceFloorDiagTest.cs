using System;
using Ts3Fixes;

// Offline self-check of DanceFloorDiagCore (docs/253 §7): each docs/253 §2 case and the order the checks win in.
static class DanceFloorDiagTest
{
    static int sFails;
    static void Check(int got, int want, string what) { if (got != want) { sFails++; Console.WriteLine("FAIL " + what + ": " + DanceFloorDiagCore.Name(got) + ", want " + DanceFloorDiagCore.Name(want)); } }

    static void Main()
    {
        Check(DanceFloorDiagCore.Classify(true, true, 7, 7, 2, 2, true), DanceFloorDiagCore.kSame, "nothing changed");
        Check(DanceFloorDiagCore.Classify(true, true, 7, 8, 2, 2, true), DanceFloorDiagCore.kWrite, "(c) version moved, same items");
        Check(DanceFloorDiagCore.Classify(true, true, 7, 8, 2, 3, false), DanceFloorDiagCore.kItems, "count changed");
        Check(DanceFloorDiagCore.Classify(true, true, 7, 7, 2, 2, false), DanceFloorDiagCore.kItems, "items changed, version not");
        Check(DanceFloorDiagCore.Classify(true, false, 7, 9, 2, 5, true), DanceFloorDiagCore.kFreed, "(a) weak reference cleared wins over version and count");
        Check(DanceFloorDiagCore.Classify(false, false, 7, 9, 2, 5, false), DanceFloorDiagCore.kFrame, "(b) enumerator words changed wins over all");
        Check(DanceFloorDiagCore.Classify(false, true, 7, 7, 2, 2, true), DanceFloorDiagCore.kFrame, "(b) with the list untouched");
        Console.WriteLine(sFails == 0 ? "dancefloorlistdiag selftest: ok" : "dancefloorlistdiag selftest: " + sFails + " FAIL");
        Environment.Exit(sFails == 0 ? 0 : 1);
    }
}
