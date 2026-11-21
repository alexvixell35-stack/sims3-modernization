using System;
using Ts3Fixes;

// Offline self-check of CasPropagateCore for caspropagateextra (docs/658 §6.2).
static class CasPropagateExtraTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; if (sFails < 20) Console.WriteLine("FAIL " + what); } }

    static void Main()
    {
        uint[] yes = { 128u, 512u, 1024u, 2048u, 32768u, 65536u };
        foreach (uint c in yes) Check(CasPropagateCore.IsExtraHuman(c), "extra " + c);
        // primary (EA's own loop), and the categories EA's in-world sweeps leave alone
        uint[] no = { 1u, 2u, 4u, 8u, 16u, 32u, 256u, 262144u, 64u, 131072u, 524288u, 0u };
        foreach (uint c in no) Check(!CasPropagateCore.IsExtraHuman(c), "not extra " + c);

        const uint career = CasPropagateCore.kCareer;
        Check(CasPropagateCore.PerfCareerZero(true, true, career, 0, 2u, 0, false), "performance Career[0]: rebuilt");
        Check(!CasPropagateCore.PerfCareerZero(true, true, career, 1, 2u, 0, false), "Career[1]: EA did it");
        Check(!CasPropagateCore.PerfCareerZero(true, false, career, 0, 2u, 0, false), "no performance career: EA did it");
        Check(!CasPropagateCore.PerfCareerZero(false, true, career, 0, 2u, 0, false), "not Edit in CAS: EA did it");
        Check(!CasPropagateCore.PerfCareerZero(true, true, career, 0, career, 0, false), "current outfit: the builder holds it");
        Check(!CasPropagateCore.PerfCareerZero(true, true, career, 0, 2u, 0, true), "age / gender differ: left");
        Check(!CasPropagateCore.PerfCareerZero(true, true, 2u, 0, 4u, 0, false), "not Career");

        Console.WriteLine(sFails == 0 ? "caspropagateextra selftest: ok" : "caspropagateextra selftest: " + sFails + " FAIL");
        Environment.Exit(sFails == 0 ? 0 : 1);
    }
}
