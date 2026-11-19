using System;
using Ts3Fixes;

// Offline self-check of CasOccultLogCore (docs/658 §6.4) and the 10 a minute / 60 per game cap casoccultlog uses.
static class CasOccultLogTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; if (sFails < 20) Console.WriteLine("FAIL " + what); } }

    static void Main()
    {
        Check(!CasOccultLogCore.WwThrew(true, false), "outfit saved: ran through");
        Check(CasOccultLogCore.WwThrew(false, false), "never reached the save: threw in an OutfitUtils step");
        Check(!CasOccultLogCore.WwThrew(false, true), "a wrapped callee already logged it: no second line");
        Check(!CasOccultLogCore.WwThrew(true, true), "threw after the save (hair colours): logged once");

        LineCapCore cap = new LineCapCore(10, 60000, 60);
        int n = 0;
        for (int t = 0; t < 10 * 60000; t += 1000) if (cap.Take(t)) n++;
        Check(n == 60 && cap.Total == 60, "a throw every second for 10 minutes: 60 lines, got " + n);

        Console.WriteLine(sFails == 0 ? "casoccultlog selftest: ok" : "casoccultlog selftest: " + sFails + " FAIL");
        Environment.Exit(sFails == 0 ? 0 : 1);
    }
}
