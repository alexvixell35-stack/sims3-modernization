using System;
using Ts3Fixes;

// Offline checks of CleanDeferCore (cleandefer, docs/686 §45).
static class CleanDeferTest
{
    static int sFail, sRun;

    static void Check(bool ok, string what)
    {
        sRun++;
        if (!ok) { sFail++; Console.WriteLine("FAIL " + what); }
    }

    static int Main()
    {
        const int G = HhSharedCore.kGo, W = HhSharedCore.kWould, X = HhSharedCore.kExempt;
        Check(CleanDeferCore.Decide(false, 0, 0f, G) == CleanDeferCore.kClean && CleanDeferCore.Decide(true, 0, 0f, G) == CleanDeferCore.kClean, "nobody waits: the clean runs");
        Check(CleanDeferCore.Decide(false, 1, 0f, G) == CleanDeferCore.kDefer && CleanDeferCore.Decide(false, 3, 60f, G) == CleanDeferCore.kDefer, "own pick while Sims wait: the clean waits");
        Check(CleanDeferCore.Decide(true, 2, 0f, G) == CleanDeferCore.kOrdered && CleanDeferCore.Decide(true, 2, 0f, W) == CleanDeferCore.kOrdered, "an ordered clean is never deferred");
        Check(CleanDeferCore.Decide(false, 1, CleanDeferCore.kCapMinutes, G) == CleanDeferCore.kCap, "deferred too long: cleaned anyway");
        Check(CleanDeferCore.Decide(false, 1, 0f, W) == CleanDeferCore.kWould && CleanDeferCore.Decide(false, 1, 0f, X) == CleanDeferCore.kExempt, "off: would; out of scope: exempt");
        Console.WriteLine((sFail == 0 ? "OK" : "FAILED") + " cleandefer selftest: " + (sRun - sFail) + "/" + sRun);
        return sFail == 0 ? 0 : 1;
    }
}
