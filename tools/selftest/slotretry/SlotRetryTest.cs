using System;
using Ts3Fixes;

// Offline checks of SlotRetryCore (slotretry, docs/686 §24).
static class SlotRetryTest
{
    static int sFail, sRun;

    static void Check(bool ok, string what)
    {
        sRun++;
        if (!ok) { sFail++; Console.WriteLine("FAIL " + what); }
    }

    static int Main()
    {
        int why;
        Check(SlotRetryCore.Decide(true, true, false, true, false, 2, out why) == SlotRetryCore.kGo && why == SlotRetryCore.kWhyNone, "on, free slots: retry");
        Check(SlotRetryCore.Decide(false, true, false, true, false, 2, out why) == SlotRetryCore.kWould, "off: would");
        Check(SlotRetryCore.Decide(true, false, false, true, false, 2, out why) == SlotRetryCore.kKeep && why == SlotRetryCore.kWhyNotHuman, "pets and riders kept");
        Check(SlotRetryCore.Decide(true, true, true, true, false, 2, out why) == SlotRetryCore.kKeep && why == SlotRetryCore.kWhyExit, "cancelled kept");
        Check(SlotRetryCore.Decide(true, true, false, false, false, 2, out why) == SlotRetryCore.kKeep && why == SlotRetryCore.kWhyObject, "object gone kept");
        Check(SlotRetryCore.Decide(true, true, false, true, true, 2, out why) == SlotRetryCore.kKeep && why == SlotRetryCore.kWhyInUse, "in use by another Sim kept");
        Check(SlotRetryCore.Decide(true, true, false, true, false, 0, out why) == SlotRetryCore.kKeep && why == SlotRetryCore.kWhyNoFree, "no free slot kept");
        Check(SlotRetryCore.Drop(true, 1, 3) && !SlotRetryCore.Drop(true, -1, 3) && !SlotRetryCore.Drop(false, 1, 3) && !SlotRetryCore.Drop(true, 0, 1), "drop the walked-to slot only after a walk and with others left");
        Check(SlotRetryCore.Wait(false) == SlotRetryCore.kWaitTicks && SlotRetryCore.Wait(true) == 0, "wait only from the same spot");
        Check(SlotRetryCore.Quiet(1) && !SlotRetryCore.Quiet(SlotRetryCore.kTries), "the last retry keeps the game's route-fail behaviour");
        Console.WriteLine((sFail == 0 ? "ok " : "FAILED ") + (sRun - sFail) + "/" + sRun);
        return sFail == 0 ? 0 : 1;
    }
}
