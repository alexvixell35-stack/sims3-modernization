using System;
using Ts3Fixes;

// Offline self-check of CasTintResetCore (docs/658 §6.6).
static class CasTintResetTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; if (sFails < 20) Console.WriteLine("FAIL " + what); } }

    static void Main()
    {
        Check(CasTintResetCore.ShouldClear(true, 0x2A01u), "switch on, stale pick id: cleared");
        Check(!CasTintResetCore.ShouldClear(true, 0u), "already EA's reset value: nothing to do");
        Check(!CasTintResetCore.ShouldClear(false, 0x2A01u), "switch off: EA's behaviour");
        Console.WriteLine(sFails == 0 ? "castintreset selftest: ok" : "castintreset selftest: " + sFails + " FAIL");
        Environment.Exit(sFails == 0 ? 0 : 1);
    }
}
