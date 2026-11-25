using System;
using Ts3Fixes;

// Offline self-check of WeddingCleanupCore.Plan: the guard changes something only where EA's CleanUp would throw.
static class WeddingCleanupTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; Console.WriteLine("FAIL " + what); } }
    // on, archSet, archDestroyed, coupleUsesArch, giverSet, giverQueueGone, receiverSet, receiverQueueGone, audienceQueueGone
    static int P(bool on, bool arch, bool dead, bool uses, bool g, bool gGone, bool r, bool rGone, bool aud)
    { return WeddingCleanupCore.Plan(on, arch, dead, uses, g, gGone, r, rGone, aud); }

    static void Main()
    {
        Check(P(true, true, false, true, true, false, true, false, false) == 0, "live arch, live couple, live audience: EA's body unchanged");
        Check(P(false, true, true, true, true, true, true, true, true) == 0, "switch off: EA's body unchanged");
        Check(P(true, true, false, true, true, true, true, false, false) == WeddingCleanupCore.kHideGiver, "giver's queue gone with the arch set: hide giver");
        Check(P(true, true, false, true, true, false, true, true, false) == WeddingCleanupCore.kHideReceiver, "receiver's queue gone with the arch set: hide receiver");
        Check(P(true, false, false, false, true, true, true, true, false) == 0, "no arch: EA never touches the ring Sims' queues, unchanged");
        Check(P(true, true, true, true, true, false, true, false, false) == WeddingCleanupCore.kArchUseList, "destroyed arch the couple still uses: drop them from its use list");
        Check(P(true, true, true, false, true, false, true, false, false) == 0, "destroyed arch nobody uses: EA skips RemoveFromUseList itself");
        Check(P(true, false, false, false, true, false, true, false, true) == WeddingCleanupCore.kHideAudience, "audience Sim with no queue: hide it, even with no arch");
        Check(P(true, true, true, true, true, true, true, true, true) == (WeddingCleanupCore.kArchUseList | WeddingCleanupCore.kHideGiver | WeddingCleanupCore.kHideReceiver | WeddingCleanupCore.kHideAudience), "everything gone: all four");
        Check(P(true, true, false, false, false, true, false, true, false) == 0, "ring Sim fields null: EA skips them itself");
        Console.WriteLine(sFails == 0 ? "weddingcleanup selftest: ok" : "weddingcleanup selftest: " + sFails + " FAIL");
        Environment.Exit(sFails == 0 ? 0 : 1);
    }
}
