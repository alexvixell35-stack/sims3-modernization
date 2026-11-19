using System;
using Ts3Fixes;

// Offline self-check of HouseboatGangwayRescueCore (docs/857 §3).
static class HouseboatGangwayRescueTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; if (sFails < 20) Console.WriteLine("FAIL " + what); } }

    static void Main()
    {
        HouseboatGangwayRescueCore g = new HouseboatGangwayRescueCore();
        HouseboatGangwayRescueCore.Mark m;
        const long hour = 1000;
        const int Ok = HouseboatGangwayRescueCore.kOk;

        // marking
        Check(g.Check(1, 7, false, true, false, false, false, 0, hour, out m) == HouseboatGangwayRescueCore.kNotMarked, "unmarked Sim: not marked");
        Check(g.Note(1, 7, false, true, 650, 100, hour) == HouseboatGangwayRescueCore.kMarked && g.IsMarked(1), "first failed leg: marked");
        Check(g.Note(1, 7, false, true, 600, 150, hour) == HouseboatGangwayRescueCore.kAdded, "second leg: added");
        Check(g.Note(1, 7, false, true, 0, 160, hour) == HouseboatGangwayRescueCore.kAdded, "answered leg (0 ms): added");
        Check(g.Note(2, 7, true, true, 650, 100, hour) == HouseboatGangwayRescueCore.kExempt && !g.IsMarked(2), "exempt (household, selected, unknown): never marked");
        Check(g.Note(3, 7, false, false, 650, 100, hour) == HouseboatGangwayRescueCore.kNotDocked && !g.IsMarked(3), "undocked houseboat: not marked");
        Check(g.Note(4, 0, false, true, 650, 100, hour) == HouseboatGangwayRescueCore.kNoBoat && !g.IsMarked(4), "no home houseboat: not marked");

        // rescue check: the mark's sums, exemption at rescue time, busy keeps the mark
        Check(g.Check(1, 7, false, true, false, false, true, 200, hour, out m) == HouseboatGangwayRescueCore.kBusy && g.IsMarked(1), "busy: refused, mark kept");
        Check(g.Check(1, 7, false, true, false, false, false, 200, hour, out m) == Ok && m.Ms == 1250 && m.Legs == 3 && m.From == 100, "ok: ms and legs summed");
        g.Done(1, 200);
        Check(!g.IsMarked(1), "done: mark gone");

        // once per sim-hour
        Check(g.Note(1, 7, false, true, 650, 500, hour) == HouseboatGangwayRescueCore.kHour && !g.IsMarked(1), "within the hour of a rescue: not marked");
        Check(g.Note(1, 7, false, true, 650, 1199, hour) == HouseboatGangwayRescueCore.kHour, "one tick before the hour ends: still refused");
        Check(g.Note(1, 7, false, true, 650, 1200, hour) == HouseboatGangwayRescueCore.kMarked, "hour over: marked again");
        Check(g.Check(1, 7, false, true, false, false, false, 1300, hour, out m) == Ok, "hour over: rescue ok");
        g.Done(1, 1300);
        g.Note(5, 7, false, true, 1, 1300, hour);
        g.Done(5, 1300);
        Check(g.Note(5, 7, false, true, 1, 1250, hour) == HouseboatGangwayRescueCore.kMarked, "clock went back (another save): hour window ends");

        // refusals that drop the mark
        g.Clear();
        g.Note(1, 7, false, true, 1, 0, hour);
        Check(g.Check(1, 7, true, true, false, false, false, 10, hour, out m) == HouseboatGangwayRescueCore.kExempt && !g.IsMarked(1), "exempt at rescue time: refused, dropped");
        g.Note(1, 7, false, true, 1, 0, hour);
        Check(g.Check(1, 8, false, true, false, false, false, 10, hour, out m) == HouseboatGangwayRescueCore.kOtherBoat && !g.IsMarked(1), "GoHome to another houseboat: refused, dropped");
        g.Note(1, 7, false, true, 1, 0, hour);
        Check(g.Check(1, 7, false, false, false, false, false, 10, hour, out m) == HouseboatGangwayRescueCore.kNotDocked, "undocked since: refused");
        g.Note(1, 7, false, true, 1, 0, hour);
        Check(g.Check(1, 7, false, true, true, false, false, 10, hour, out m) == HouseboatGangwayRescueCore.kOnLot && !g.IsMarked(1), "already on the lot: refused, dropped");
        g.Note(1, 7, false, true, 1, 0, hour);
        Check(g.Check(1, 7, false, true, false, true, false, 10, hour, out m) == HouseboatGangwayRescueCore.kFar, "far from the gangway: refused");
        g.Note(1, 7, false, true, 1, 0, hour);
        Check(g.Check(1, 7, false, true, false, false, false, 1000, hour, out m) == HouseboatGangwayRescueCore.kNotMarked && !g.IsMarked(1), "stale mark (an hour old): dropped");
        g.Note(1, 7, false, true, 1, 0, hour);
        Check(g.Note(1, 7, true, true, 1, 10, hour) == HouseboatGangwayRescueCore.kExempt && !g.IsMarked(1), "became exempt (moved into the household): mark dropped");
        g.Note(1, 7, false, true, 5, 0, hour);
        Check(g.Note(1, 9, false, true, 7, 10, hour) == HouseboatGangwayRescueCore.kMarked, "another houseboat: a new mark");
        Check(g.Check(1, 9, false, true, false, false, false, 20, hour, out m) == Ok && m.Ms == 7 && m.Legs == 1, "new mark: sums start over");

        // bounded table
        g.Clear();
        for (ulong i = 0; i < (ulong)HouseboatGangwayRescueCore.kMax; i++) g.Note(i, 7, false, true, 1, i < 10 ? 0 : 5000, hour);
        Check(g.Count == HouseboatGangwayRescueCore.kMax, "full table");
        g.Note(1000, 7, false, true, 1, 5000, hour);
        Check(g.Count == HouseboatGangwayRescueCore.kMax - 10 + 1, "full: stale marks dropped first");
        for (ulong i = 2000; g.Count < HouseboatGangwayRescueCore.kMax; i++) g.Note(i, 7, false, true, 1, 5000, hour);
        g.Note(3000, 7, false, true, 1, 5000, hour);
        Check(g.Count == 1 && g.IsMarked(3000), "full of live marks: cleared, new one kept");

        Console.WriteLine(sFails == 0 ? "houseboatgangwayrescue selftest: ok" : "houseboatgangwayrescue selftest: " + sFails + " FAIL");
        Environment.Exit(sFails == 0 ? 0 : 1);
    }
}
