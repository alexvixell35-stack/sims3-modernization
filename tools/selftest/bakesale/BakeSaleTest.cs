using System;
using Ts3Fixes;

// Offline self-check of BakeSaleCore (issue #102): every input combination of both decisions against its rule written out
// here a second time, plus the named cases from the issue (deleted seller, reset but alive seller, orphaned NPC table).
static class BakeSaleTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; if (sFails < 20) Console.WriteLine("FAIL " + what); } }
    static bool B(int bits, int i) { return ((bits >> i) & 1) != 0; }

    static void Main()
    {
        for (int m = 0; m < 8; m++)
        {
            bool has = B(m, 0), destroyed = B(m, 1), queue = B(m, 2);
            Check(BakeSaleCore.SellerGone(has, destroyed, queue) == (has && (destroyed || !queue)), "seller " + m);
            bool npc = B(m, 0), alarm = B(m, 1), gone = B(m, 2);
            Check(BakeSaleCore.NeedsRearm(npc, alarm, gone) == (npc && !alarm && !gone), "rearm " + m);
        }
        Check(BakeSaleCore.SellerGone(true, true, false), "deleted seller (the #102 table)");
        Check(!BakeSaleCore.SellerGone(true, false, true), "reset but alive seller stays the seller (EA)");
        Check(!BakeSaleCore.SellerGone(false, false, false), "no seller");
        Check(BakeSaleCore.NeedsRearm(true, false, false), "orphaned NPC table");
        Check(!BakeSaleCore.NeedsRearm(true, true, false), "NPC table with its own alarm");
        Check(!BakeSaleCore.NeedsRearm(false, false, false), "player table (never has the alarm)");
        Console.WriteLine(sFails == 0 ? "bakesale selftest: ok" : "bakesale selftest: " + sFails + " FAIL");
        Environment.Exit(sFails == 0 ? 0 : 1);
    }
}
