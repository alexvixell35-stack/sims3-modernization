using System;
using Ts3Fixes;

// Offline self-check of EaVacationCore (issue #97): every input combination of each decision against its rule written out
// here a second time, plus the named cases from the issue (dead elder, moved-out adult, tick-0 expiry, stuck pickup).
static class VacationTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; if (sFails < 20) Console.WriteLine("FAIL " + what); } }
    static bool B(int bits, int i) { return ((bits >> i) & 1) != 0; }

    static void Main()
    {
        for (int m = 0; m < 32; m++)
        {
            bool desc = B(m, 0), sim = B(m, 1), bot = B(m, 2), dead = B(m, 3), same = B(m, 4);
            int v = EaVacationCore.AdultVerdict(desc, sim, bot, dead, same);
            int want = (!desc || sim || bot) ? EaVacationCore.kEaSkips : (dead || !same) ? EaVacationCore.kWeSkip : EaVacationCore.kEaMakes;
            Check(v == want, "verdict " + m);
            Check(EaVacationCore.BringBackAtExit(desc, sim, bot, dead, same) == (want == EaVacationCore.kEaMakes), "exit " + m);
        }
        Check(EaVacationCore.AdultVerdict(true, false, false, true, false) == EaVacationCore.kWeSkip, "dead elder, no household");
        Check(EaVacationCore.AdultVerdict(true, false, false, false, false) == EaVacationCore.kWeSkip, "moved out");
        Check(EaVacationCore.AdultVerdict(true, false, false, false, true) == EaVacationCore.kEaMakes, "normal adult");
        Check(EaVacationCore.AdultVerdict(true, true, false, false, true) == EaVacationCore.kEaSkips, "still has a Sim");

        for (int m = 0; m < 64; m++)
        {
            bool idv = B(m, 0), world = B(m, 1), live = B(m, 2), found = B(m, 3), isSim = B(m, 4), that = B(m, 5);
            bool want = idv && world && live && (!found || (isSim && !that));
            Check(EaVacationCore.RefreshId(idv, world, live, found, isSim, that) == want, "refresh " + m);
        }
        Check(EaVacationCore.RefreshId(true, true, true, false, false, false), "stale id after TryResumeOpportunity");
        Check(!EaVacationCore.RefreshId(true, true, true, true, false, false), "id finds a live object target: kept");
        Check(!EaVacationCore.RefreshId(true, false, true, false, false, false), "other world: EA's own path");

        Check(!EaVacationCore.ExpiryApplies(0), "tick 0 = no time-based timeout");
        Check(EaVacationCore.ExpiryApplies(1) && EaVacationCore.ExpiryApplies(long.MaxValue), "real deadlines checked");

        for (int m = 0; m < 16; m++)
            for (int teens = 0; teens < 3; teens++)
            {
                bool picked = B(m, 0), alive = B(m, 1), all = B(m, 2), alarm = B(m, 3);
                int want = picked ? (alive ? EaVacationCore.kRepairNone : all ? EaVacationCore.kRepairEnd : EaVacationCore.kRepairBringBack)
                    : teens == 0 || !alarm ? EaVacationCore.kRepairBringBack : EaVacationCore.kRepairNone;
                Check(EaVacationCore.Repair(picked, alive, all, alarm, teens) == want, "repair " + m + "/" + teens);
            }
        Check(EaVacationCore.Repair(true, false, true, false, 1) == EaVacationCore.kRepairEnd, "NRE in pickup Dispose before Destroy: trip ended");
        Check(EaVacationCore.Repair(false, false, false, true, 1) == EaVacationCore.kRepairNone, "healthy trip untouched");
        Check(EaVacationCore.Repair(false, false, false, true, 0) == EaVacationCore.kRepairBringBack, "no teen left");

        Console.WriteLine(sFails == 0 ? "vacation: PASS" : "vacation: " + sFails + " FAIL");
        Environment.Exit(sFails == 0 ? 0 : 1);
    }
}
