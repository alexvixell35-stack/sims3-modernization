using System;
using Ts3Fixes;

// Offline checks of the pool ladder kind of ShareLanesCore (poolshare, docs/686 §35).
static class PoolShareTest
{
    static int sFail, sRun;

    static void Check(bool ok, string what)
    {
        sRun++;
        if (!ok) { sFail++; Console.WriteLine("FAIL " + what); }
    }

    static int Main()
    {
        const uint outside = 11, inside = 22;
        ShareLanesCore.Holder[] h = new ShareLanesCore.Holder[1];
        h[0].Lane = 0; h[0].StartSlot = outside; h[0].InScope = true;
        Check(ShareLanesCore.EaFree(h, 1, 0, outside), "EA: same end shares (two Sims getting in)");
        Check(!ShareLanesCore.EaFree(h, 1, 0, inside), "EA: the other end is refused (a swimmer getting out waits)");
        Check(ShareLanesCore.Lane(h, 1, 0, inside) == ShareLanesCore.kLaneOpposite, "ours: held from the other end, holder in scope");
        h[0].InScope = false;
        Check(ShareLanesCore.Lane(h, 1, 0, inside) == ShareLanesCore.kLaneClosed, "holder out of scope (household without poolsharehh): closed");
        Check(ShareLanesCore.Decide(true, true, true, true) == ShareLanesCore.kGo, "on, in scope: shared");
        Check(ShareLanesCore.Decide(false, true, true, true) == ShareLanesCore.kWould, "off: would");
        Check(ShareLanesCore.Decide(true, false, true, true) == ShareLanesCore.kExempt, "household without poolsharehh: exempt");
        Check(ShareLanesCore.Decide(true, true, true, false) == ShareLanesCore.kNone, "refused for another reason (pool checks): kept");
        Check(ShareLanesCore.kKindNames.Length == ShareLanesCore.kPoolLadder + 1 && ShareLanesCore.kKindNames[ShareLanesCore.kPoolLadder] == "pool ladder", "kind name");
        Console.WriteLine((sFail == 0 ? "ok " : "FAILED ") + (sRun - sFail) + "/" + sRun);
        return sFail == 0 ? 0 : 1;
    }
}
