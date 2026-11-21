using System;
using Ts3Fixes;

// Offline self-check of CasDefaultSkinCore (docs/658 §6.3).
static class CasDefaultSkinTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; if (sFails < 20) Console.WriteLine("FAIL " + what); } }

    static void Main()
    {
        const ulong H = CasDefaultSkinCore.kHumanSkinTone, alien = 10664257489148320948uL, green = 2751605866008866797uL;
        ulong pick;
        // EA: the last ramp; two package orders give two defaults. The fix gives the same one for both.
        ulong[] a = { H, green, alien }, b = { alien, H, green };
        Check(CasDefaultSkinCore.Pick(a, alien, false, 0, out pick) == CasDefaultSkinCore.kHumanBase && pick == H, "order A: human base");
        Check(CasDefaultSkinCore.Pick(b, green, false, 0, out pick) == CasDefaultSkinCore.kHumanBase && pick == H, "order B: human base");
        // no human base ramp: EA's pick kept
        Check(CasDefaultSkinCore.Pick(new ulong[] { green, alien }, alien, false, 0, out pick) == CasDefaultSkinCore.kKeptEa && pick == alien, "no human ramp: EA's");
        Check(CasDefaultSkinCore.Pick(null, alien, false, 0, out pick) == CasDefaultSkinCore.kKeptEa && pick == alien, "no list: EA's");
        // mid-session (LoadDB for new content): the builder's skin stays the key
        Check(CasDefaultSkinCore.Pick(a, alien, true, green, out pick) == CasDefaultSkinCore.kBuilder && pick == green, "builder open: its skin");
        Check(CasDefaultSkinCore.Pick(a, alien, true, 0, out pick) == CasDefaultSkinCore.kHumanBase, "builder with no skin yet: human base");
        for (int i = 0; i <= 2; i++) Check(CasDefaultSkinCore.Name(i) != "?", "named " + i);

        Console.WriteLine(sFails == 0 ? "casdefaultskin selftest: ok" : "casdefaultskin selftest: " + sFails + " FAIL");
        Environment.Exit(sFails == 0 ? 0 : 1);
    }
}
