using System;
using Ts3Fixes;

// Offline self-check of OwnBugsCore (docs/650).
static class OwnBugsTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; Console.WriteLine("FAIL: " + what); } }

    static void Main()
    {
        // §1 EA alarm guard: first frame of a Mono stack trace, capped
        string mono = "  at Sims3.Gameplay.ActiveCareer.ActiveCareers.NPCShowManager.GetRandomNPC (Sims3.Gameplay.Actors.Sim& NPCSim, Sims3.Gameplay.Core.Lot& targetLot, Boolean isPerformForTips, Boolean careerLow) [0x00000]\n"
            + "  at Sims3.Gameplay.ActiveCareer.ActiveCareers.NPCShowManager.CreateNPCShow () [0x00000]\n";
        string f = OwnBugsCore.FirstFrame(mono);
        Check(f.StartsWith("Sims3.Gameplay.ActiveCareer.ActiveCareers.NPCShowManager.GetRandomNPC"), "first frame: " + f);
        Check(f.Length <= OwnBugsCore.kFrameCap, "frame capped");
        Check(OwnBugsCore.FirstFrame(null) == "?" && OwnBugsCore.FirstFrame("\n \n") == "?", "no frame");
        Check(OwnBugsCore.FirstFrame("\nat A.B ()\nat C.D ()") == "A.B ()", "skips blank lines");
        Check(OwnBugsCore.Brief("NullReferenceException", "at A.B ()") == "NullReferenceException at A.B ()", "brief");
        Check(OwnBugsCore.Brief(null, null) == "? at ?", "brief of nothing");
        Check(OwnBugsCore.LogMore(0, 24) && OwnBugsCore.LogMore(23, 24) && !OwnBugsCore.LogMore(24, 24) && !OwnBugsCore.LogMore(-1, 24), "log cap");

        // §2 unseenservice cleanup rules
        Check(OwnBugsCore.SvcStepName(OwnBugsCore.kSvcStepPlace) == "ParentToSlot" && OwnBugsCore.SvcStepName(OwnBugsCore.kSvcStepScope) == "scope or preconditions"
            && OwnBugsCore.SvcStepName(99) == "step 99", "step names");
        Check(OwnBugsCore.SvcEndUpdates(true) && !OwnBugsCore.SvcEndUpdates(false), "end only updates still open");
        Check(OwnBugsCore.SvcDestroyMade(true, false, OwnBugsCore.kSvcStepPlace), "unplaced glass destroyed");
        Check(OwnBugsCore.SvcDestroyMade(true, true, OwnBugsCore.kSvcStepPour), "placed glass whose pour threw destroyed (slot left empty for EA)");
        Check(!OwnBugsCore.SvcDestroyMade(true, true, OwnBugsCore.kSvcStepBehavior), "RunBehavior owns the placed object");
        Check(!OwnBugsCore.SvcDestroyMade(true, true, OwnBugsCore.kSvcStepDone), "served object kept");
        Check(!OwnBugsCore.SvcDestroyMade(false, false, OwnBugsCore.kSvcStepCreate), "nothing made, nothing destroyed");
        Check(OwnBugsCore.SvcDestroyMade(true, false, OwnBugsCore.kSvcStepCommodity), "made, throw before place: destroyed");

        if (sFails > 0) { Console.WriteLine("ownbugs: " + sFails + " failed"); Environment.Exit(1); }
        Console.WriteLine("ownbugs: all checks passed");
    }
}
