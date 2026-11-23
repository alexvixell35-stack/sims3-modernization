using System;
using Ts3Fixes;

// Offline self-check of CasPropagateCore for caspropagateoutfits (docs/658 §6.1) and the shared LineCapCore.
static class CasPropagateOutfitsTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; if (sFails < 20) Console.WriteLine("FAIL " + what); } }

    static void Main()
    {
        const uint human = 318u, horse = 28674u;
        // EA's early return
        Check(CasPropagateCore.Runs(CasPropagateCore.kHuman, 16u, human, horse), "human swimwear: runs");
        Check(!CasPropagateCore.Runs(CasPropagateCore.kHuman, 524288u, human, horse), "human supernatural: EA returns");
        Check(CasPropagateCore.Runs(CasPropagateCore.kHorse, 4096u, human, horse), "horse racing: runs");
        Check(CasPropagateCore.Runs(CasPropagateCore.kOtherPet, 1u, human, horse) && !CasPropagateCore.Runs(CasPropagateCore.kOtherPet, 4u, human, horse), "dog: naked or everyday only");

        // drops: mismatches except the current outfit and the performance Career[0]
        bool[] mm = { true, false, true, true };
        bool[] d = CasPropagateCore.Drops(mm, 3, 0);
        Check(!d[0] && !d[1] && d[2] && !d[3], "protected outfits are kept");
        Check(CasPropagateCore.DropsBelow(d, 3) == 1 && CasPropagateCore.DropsBelow(d, 2) == 0 && CasPropagateCore.DropsBelow(null, 5) == 0, "drops below");
        Check(CasPropagateCore.Drops(null, -1, -1).Length == 0, "null list");

        // realign: by reference first, else old less drops, clamped
        Check(CasPropagateCore.Realign(1, 2, 0, 3) == 1, "found by reference");
        Check(CasPropagateCore.Realign(-1, 3, 1, 3) == 2, "old less drops");
        Check(CasPropagateCore.Realign(-1, 5, 0, 2) == 1, "clamped to the last");
        Check(CasPropagateCore.Realign(-1, 0, 2, 3) == 0, "never below 0");
        Check(CasPropagateCore.Realign(-1, 4, 0, 0) == 0, "empty list");
        // EA's bug in numbers: Everyday [wrong age, a, current] -> EA keeps [a, current'] and still says index 2
        bool[] ev = CasPropagateCore.Drops(new bool[] { true, false, false }, 2, -1);
        Check(CasPropagateCore.Realign(1, 2, CasPropagateCore.DropsBelow(ev, 2), 2) == 1, "the current outfit followed");

        // log cap
        LineCapCore cap = new LineCapCore(2, 1000, 3);
        Check(cap.Take(0) && cap.Take(10) && !cap.Take(20), "2 per window");
        Check(cap.Take(1500) && !cap.Take(5000) && cap.Total == 3, "lifetime ceiling");
        LineCapCore back = new LineCapCore(1, 1000, 10);
        Check(back.Take(5000) && back.Take(100), "tick count going back starts a window");

        Console.WriteLine(sFails == 0 ? "caspropagateoutfits selftest: ok" : "caspropagateoutfits selftest: " + sFails + " FAIL");
        Environment.Exit(sFails == 0 ? 0 : 1);
    }
}
