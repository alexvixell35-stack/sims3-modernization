using System;
using Ts3Fixes;

// Offline self-check of mods/ts3fixes/src/AptJobTargetsCore.cs (docs/863): every decision against its truth table.
static class AptJobTargetsTest
{
    static int sFail;
    static void Check(bool ok, string what) { if (!ok) { sFail++; Console.WriteLine("FAIL " + what); } }

    static int Main()
    {
        // aptgasleak: only the full case drops (switch on, apartment lot, leaking, hidden, task pending)
        for (int m = 0; m < 32; m++)
        {
            bool on = (m & 1) != 0, vr = (m & 2) != 0, leak = (m & 4) != 0, hid = (m & 8) != 0, task = (m & 16) != 0;
            Check(AptCore.GasDrop(on, vr, leak, hid, task) == (m == 31), "GasDrop mask " + m);
        }
        // aptpossess: EA's target is kept unless switch on, apartment lot and hidden
        for (int m = 0; m < 8; m++)
        {
            bool on = (m & 1) != 0, vr = (m & 2) != 0, hid = (m & 4) != 0;
            Check(AptCore.KeepHop(on, vr, hid) == (m != 7), "KeepHop mask " + m);
        }
        // re-pick candidates: EA's rule (same level, distance <= range inclusive) and not hidden
        Check(AptCore.HopCandidate(true, 0f, 8f, false), "same level, distance 0");
        Check(AptCore.HopCandidate(true, 8f, 8f, false), "distance == range is in range (EA's <=)");
        Check(!AptCore.HopCandidate(true, 8.01f, 8f, false), "beyond range");
        Check(!AptCore.HopCandidate(false, 1f, 8f, false), "other level");
        Check(!AptCore.HopCandidate(true, 1f, 8f, true), "hidden room");
        Console.WriteLine(sFail == 0 ? "aptjobtargets selftest: all passed" : "aptjobtargets selftest: " + sFail + " FAILED");
        return sFail == 0 ? 0 : 1;
    }
}
