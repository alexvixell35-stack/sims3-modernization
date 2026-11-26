using System;
using Ts3Fixes;

// Offline checks of TermLenGuardCore (termlenguard, docs/692 §2): which indices EA's academic multiplier tables reject and
// the factor the guard gives instead (EA's guarded copy: skip the factor, i.e. 1).
static class TermLenGuardTest
{
    static int sFail, sRun;

    static void Check(bool ok, string what)
    {
        sRun++;
        if (!ok) { sFail++; Console.WriteLine("FAIL " + what); }
    }

    static int Main()
    {
        float[] term = { 1f, 0.5f };                  // EA: one-week, two-week term
        float[] load = { 1.25f, 1f, 0.75f, 0.5f };    // EA: 1-4 courses a day
        Check(TermLenGuardCore.IndexBad(0, 2), "kInvalid term length (0) is out of range");
        Check(!TermLenGuardCore.IndexBad(1, 2) && !TermLenGuardCore.IndexBad(2, 2), "one- and two-week terms are in range");
        Check(TermLenGuardCore.IndexBad(3, 2) && TermLenGuardCore.IndexBad(4, 2), "three- and four-week terms are out of range");
        Check(TermLenGuardCore.IndexBad(0, 4), "0 courses a day is out of range");
        for (uint n = 1; n <= 4; n++) Check(!TermLenGuardCore.IndexBad(n, 4), n + " courses a day are in range");
        Check(TermLenGuardCore.IndexBad(5, 4), "5 courses a day are out of range");
        Check(TermLenGuardCore.IndexBad(uint.MaxValue, 4), "uint max is out of range");
        Check(TermLenGuardCore.Factor(term, 1) == 1f && TermLenGuardCore.Factor(term, 2) == 0.5f, "valid indices keep EA's entry");
        Check(TermLenGuardCore.Factor(term, 0) == 1f && TermLenGuardCore.Factor(term, 3) == 1f, "out of range gives 1");
        Check(TermLenGuardCore.Factor(load, 3) == 0.75f && TermLenGuardCore.Factor(load, 0) == 1f, "course load: entry or 1");
        Check(TermLenGuardCore.Factor(null, 1) == 1f, "no table gives 1");
        float[] one = { 1f };                          // what the guard swaps in, with the index source set to 1
        Check(one[TermLenGuardCore.IndexBad(1, one.Length) ? 0 : 0u] == TermLenGuardCore.Factor(term, 0), "the swapped table reads the guarded factor");
        Console.WriteLine((sFail == 0 ? "OK" : "FAILED") + " termlenguard selftest: " + (sRun - sFail) + "/" + sRun);
        return sFail == 0 ? 0 : 1;
    }
}
