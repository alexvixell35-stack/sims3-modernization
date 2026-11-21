using System;
using Ts3Fixes;

// Offline self-check of MiniHomeLotCore (docs/266): every input of the sync rule against the rule written out a second
// time, and the named cases from the doc.
static class MiniHomeLotTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; if (sFails < 20) Console.WriteLine("FAIL " + what); } }

    static void Main()
    {
        ulong[] lots = { 0, 1, 7, 0xFFFFFFFFFFFFFFFF };
        for (int m = 0; m < 4; m++)
            foreach (ulong h in lots)
                foreach (ulong mini in lots)
                {
                    bool travelling = (m & 1) != 0, home = (m & 2) != 0;
                    Check(MiniHomeLotCore.ShouldSync(travelling, home, h, mini) == (!travelling && home && h != 0 && mini != h), "rule " + m + " " + h + " " + mini);
                }
        Check(MiniHomeLotCore.ShouldSync(false, true, 7, 1), "household moved 1 -> 7, mini still 1: synced");
        Check(!MiniHomeLotCore.ShouldSync(false, true, 7, 7), "mini already right: left alone");
        Check(!MiniHomeLotCore.ShouldSync(true, true, 7, 1), "changing worlds: left alone");
        Check(!MiniHomeLotCore.ShouldSync(false, false, 7, 1), "Sim from another world: left alone");
        Check(!MiniHomeLotCore.ShouldSync(false, true, 0, 1), "no lot (homeless): left alone");
        Console.WriteLine(sFails == 0 ? "minihomelot selftest: ok" : "minihomelot selftest: " + sFails + " FAIL");
        Environment.Exit(sFails == 0 ? 0 : 1);
    }
}
