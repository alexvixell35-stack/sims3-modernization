using System;
using Ts3Fixes;

// Offline self-check of LotGameplayCore (docs/661): barcloseorphan window, venueintensity swap, visitwalkfinish reasons.
static class LotGameplayTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; if (sFails < 30) Console.WriteLine("FAIL " + what); } }

    static void Main()
    {
        // EA's IsTimeBetweenTimes branches
        Check(LotGameplayCore.Between(10f, 9f, 17f) && !LotGameplayCore.Between(18f, 9f, 17f), "same-day window");
        Check(LotGameplayCore.Between(23f, 20f, 2f) && LotGameplayCore.Between(1f, 20f, 2f) && !LotGameplayCore.Between(12f, 20f, 2f), "wrapped window");

        // a club open 16:00-02:00, staggered exit from 00:00: strictly open 16:00 up to (not at) 00:00
        Check(LotGameplayCore.StrictlyOpen(16f, 16f, 2f, 2f), "at open");
        Check(LotGameplayCore.StrictlyOpen(23.5f, 16f, 2f, 2f), "late evening");
        Check(!LotGameplayCore.StrictlyOpen(0f, 16f, 2f, 2f), "staggered exit begins at 00:00 (EA's own pushes)");
        Check(!LotGameplayCore.StrictlyOpen(1f, 16f, 2f, 2f), "inside the staggered exit");
        Check(!LotGameplayCore.StrictlyOpen(3f, 16f, 2f, 2f) && !LotGameplayCore.StrictlyOpen(15.9f, 16f, 2f, 2f), "closed");

        // close at 01:00: close - stagger = -1 -> 23:00 (EA's AddAlarmDay drops a negative hour; the window still wraps right here)
        Check(LotGameplayCore.StrictlyOpen(22f, 18f, 1f, 2f) && !LotGameplayCore.StrictlyOpen(23.5f, 18f, 1f, 2f), "close 01:00 wraps");
        // same-day bar 10:00-18:00
        Check(LotGameplayCore.StrictlyOpen(12f, 10f, 18f, 2f) && !LotGameplayCore.StrictlyOpen(17f, 10f, 18f, 2f), "day bar");
        // bad hours / window shorter than the stagger: never skip
        Check(!LotGameplayCore.StrictlyOpen(12f, -1f, 18f, 2f) && !LotGameplayCore.StrictlyOpen(12f, 10f, -1f, 2f), "no hours");
        Check(!LotGameplayCore.StrictlyOpen(11f, 10f, 11.5f, 2f), "window shorter than the stagger");
        Check(!LotGameplayCore.StrictlyOpen(12f, 10f, 18f, -1f), "bad stagger");
        Check(Math.Abs(LotGameplayCore.Hour24(-1f) - 23f) < 1e-4 && Math.Abs(LotGameplayCore.Hour24(25f) - 1f) < 1e-4, "Hour24");

        // venueintensity: EA reads Intensity in downtown, Intensity_Downtown elsewhere
        Check(LotGameplayCore.ShouldSwap(false, 1f, 0.5f), "not downtown, both set, differ: swap");
        Check(!LotGameplayCore.ShouldSwap(false, 1f, 1f), "equal: nothing");
        Check(!LotGameplayCore.ShouldSwap(false, 0f, 0.5f), "right column (Intensity) unset: EA's value");
        Check(LotGameplayCore.ShouldSwap(false, 1f, 0f), "Downtown column empty outside downtown: EA reads 0, Intensity set: swap");
        Check(LotGameplayCore.ShouldSwap(true, 1f, 2f), "downtown, differ: swap");
        Check(!LotGameplayCore.ShouldSwap(true, 1f, 0f), "downtown, Downtown column unset: EA's Intensity kept");

        // visitwalkfinish reasons
        Check(LotGameplayCore.VwfLotReason(true, true, true, true) == LotGameplayCore.kVwfLotOther, "lot first");
        Check(LotGameplayCore.VwfLotReason(false, true, true, false) == LotGameplayCore.kVwfResortDive, "resort / dive");
        Check(LotGameplayCore.VwfLotReason(false, false, true, true) == LotGameplayCore.kVwfPets, "pets");
        Check(LotGameplayCore.VwfLotReason(false, false, false, true) == LotGameplayCore.kVwfGroup, "group");
        Check(LotGameplayCore.VwfLotReason(false, false, false, false) == -1, "ok");
        for (int d = 0; d <= 9; d++) Check(LotGameplayCore.VwfName(d) != "?", "named " + d);
        Check(LotGameplayCore.kVwfBase + LotGameplayCore.kVwfDropped == 4229, "slots end at 4229");

        Console.WriteLine(sFails == 0 ? "lotgameplay selftest: ok" : "lotgameplay selftest: " + sFails + " FAIL");
        Environment.Exit(sFails == 0 ? 0 : 1);
    }
}
