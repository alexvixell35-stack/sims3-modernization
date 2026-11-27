using System;
using Ts3Fixes;

// Offline checks of MopClaimCore (mopclaim, docs/686 §43).
static class MopClaimTest
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
        Check(MopClaimCore.Decide(false, false, G) == MopClaimCore.kKeep && MopClaimCore.Decide(false, true, W) == MopClaimCore.kKeep, "free puddle: EA's pick");
        Check(MopClaimCore.Decide(true, true, G) == MopClaimCore.kOrdered && MopClaimCore.Decide(true, true, W) == MopClaimCore.kOrdered, "an ordered mop is never moved");
        Check(MopClaimCore.Decide(true, false, G) == MopClaimCore.kMove, "own pick on a held puddle: another puddle");
        Check(MopClaimCore.Decide(true, false, W) == MopClaimCore.kWould && MopClaimCore.Decide(true, false, X) == MopClaimCore.kExempt, "off: would; out of scope: exempt");
        Check(MopClaimCore.Key(5, 0, 10, 20) == MopClaimCore.Key(5, 0, 10, 20) && MopClaimCore.Key(5, 0, 10, 20) != MopClaimCore.Key(5, 0, 20, 10)
            && MopClaimCore.Key(5, 0, 10, 20) != MopClaimCore.Key(5, 1, 10, 20) && MopClaimCore.Key(5, 0, 10, 20) != MopClaimCore.Key(6, 0, 10, 20), "key: lot, level, x, z");
        Check((MopClaimCore.Key(5, -1, 10, 20) >> 63) == 1 && (MopClaimCore.Key(ulong.MaxValue, 127, 65535, 65535) >> 63) == 1, "key: bit 63 set, never an object id");
        Check(MopClaimCore.Nearest(new float[] { 1f, 4f, 9f }, new bool[] { true, true, true }, 3) == 0, "nearest");
        Check(MopClaimCore.Nearest(new float[] { 1f, 4f, 9f }, new bool[] { false, true, true }, 3) == 1, "nearest unheld");
        Check(MopClaimCore.Nearest(new float[] { 1f, 4f }, new bool[] { false, false }, 2) == -1 && MopClaimCore.Nearest(new float[0], new bool[0], 0) == -1, "all held or none: ends quietly");
        Check(MopClaimCore.HoldMinutes(5f, 5f) == 10f && MopClaimCore.HoldMinutes(-1f, 0f) == 0.5f, "hold life: mop time plus the claim minutes");
        Console.WriteLine((sFail == 0 ? "OK" : "FAILED") + " mopclaim selftest: " + (sRun - sFail) + "/" + sRun);
        return sFail == 0 ? 0 : 1;
    }
}
