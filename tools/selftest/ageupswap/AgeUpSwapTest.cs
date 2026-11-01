using System;
using Ts3Fixes;

// Offline self-check of AgeUpSwapCore (docs/503): a simulated SimBuilderTask.FinishModelLoad. EA's swap stub refuses a
// category with no age rule or no outfit and, when it accepts, calls back a few ticks later. The task then waits on its
// callback. Off: a refused category waits forever (the bug). On: every builder task finishes, refused swaps go to
// Everyday when Everyday is accepted, and a caller that is not a builder task gets EA's result unchanged.
static class AgeUpSwapTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; if (sFails < 20) Console.WriteLine("FAIL " + what); } }

    // The hook's flow around EA's swap (AgeUpSwap.cs SwapHook). eaAccepts[cat]: EA starts a swap for that category.
    // Returns the swap result the caller sees; startedCat = the category whose swap started (-1 none), released = callback run now.
    static bool Hook(bool[] eaAccepts, int cat, bool on, bool builder, out int startedCat, out bool released)
    {
        const int kEveryday = 1;
        startedCat = -1; released = false;
        bool ok = eaAccepts[cat];
        if (ok) { startedCat = cat; return true; }
        int next = AgeUpSwapCore.Next(false, on, builder, cat == kEveryday);
        if (next == AgeUpSwapCore.kEa) return false;
        if (next == AgeUpSwapCore.kEveryday && eaAccepts[kEveryday]) { startedCat = kEveryday; return true; }
        released = true;
        return true;
    }

    static void Main()
    {
        Random rnd = new Random(503);
        int stuckOff = 0, everydayOn = 0, releasedOn = 0;
        for (int round = 0; round < 20000; round++)
        {
            bool[] acc = new bool[8];
            for (int i = 0; i < acc.Length; i++) acc[i] = rnd.Next(3) > 0;
            if (rnd.Next(4) == 0) acc[1] = false; // sometimes even Everyday is refused (no outfit at all)
            int cat = rnd.Next(acc.Length);
            bool builder = rnd.Next(5) > 0;
            foreach (bool on in new bool[] { false, true })
            {
                int started; bool released;
                bool r = Hook(acc, cat, on, builder, out started, out released);
                // the wait: the callback runs if a swap started (EA calls back) or the hook released the task
                bool waitEnds = started >= 0 || released;
                if (acc[cat]) Check(r && started == cat && !released, "accepted swap changed, round " + round);
                if (!builder) Check(r == acc[cat] && !released && (started == cat) == acc[cat], "non-builder caller changed, round " + round);
                if (!on) Check(r == acc[cat] && !released, "switch off changed EA's result, round " + round);
                if (on && builder) Check(waitEnds, "builder task still waits with the switch on, round " + round);
                if (on && builder && !acc[cat] && cat != 1 && acc[1]) Check(started == 1, "refused swap did not go to Everyday, round " + round);
                if (on && builder && released) Check(!acc[cat] && !(cat != 1 && acc[1]), "released although a swap could start, round " + round);
                if (!on && builder && !waitEnds) stuckOff++;
                if (on && builder && started == 1 && cat != 1) everydayOn++;
                if (on && builder && released) releasedOn++;
            }
        }
        Check(stuckOff > 0, "the off path never reproduced the endless wait");
        Check(everydayOn > 0 && releasedOn > 0, "both on outcomes seen");
        Console.WriteLine("ageupswap self-test: " + (sFails == 0 ? "PASS" : sFails + " FAIL") + " (off: " + stuckOff + " endless waits; on: " + everydayOn + " moved to Everyday, " + releasedOn + " released)");
        if (sFails > 0) Environment.Exit(1);
    }
}
