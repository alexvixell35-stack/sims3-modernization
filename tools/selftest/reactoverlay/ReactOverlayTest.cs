using System;
using Ts3Fixes;

// Offline checks of ReactOverlayCore (reactoverlay, docs/686 §26).
static class ReactOverlayTest
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
        Check(ReactOverlayCore.LateKind(5, false) && ReactOverlayCore.LateKind(4, false), "NowOrLater and AfterInteraction are late kinds");
        Check(!ReactOverlayCore.LateKind(1, false) && !ReactOverlayCore.LateKind(2, false) && !ReactOverlayCore.LateKind(0xC, false) && !ReactOverlayCore.LateKind(0, false),
            "Immediate, ImmediateWithoutOverlay, CriticalWithRoute, None kept");
        Check(!ReactOverlayCore.LateKind(5, true), "watch until exit kept");
        Check(ReactOverlayCore.PartsFree(0) && ReactOverlayCore.PartsFree(0x1E) && ReactOverlayCore.PartsFree(1), "body and legs held, arms and head free: overlay");
        Check(!ReactOverlayCore.PartsFree(0x20) && !ReactOverlayCore.PartsFree(0x80) && !ReactOverlayCore.PartsFree(0x100) && !ReactOverlayCore.PartsFree(0x1FF), "an arm, the head or the face held: no overlay");
        Check(ReactOverlayCore.Decide(true, ReactOverlayCore.kWhyNone, true, out why) == ReactOverlayCore.kGo && why == ReactOverlayCore.kWhyNone, "on, safe: overlay");
        Check(ReactOverlayCore.Decide(false, ReactOverlayCore.kWhyNone, false, out why) == ReactOverlayCore.kWould, "off: would, household too");
        Check(ReactOverlayCore.Decide(true, ReactOverlayCore.kWhyRouting, true, out why) == ReactOverlayCore.kKeep && why == ReactOverlayCore.kWhyRouting, "routing kept");
        Check(ReactOverlayCore.Decide(false, ReactOverlayCore.kWhyNoClip, true, out why) == ReactOverlayCore.kKeep && why == ReactOverlayCore.kWhyNoClip, "no overlay clip: EA's way, no would");
        Check(ReactOverlayCore.Decide(true, ReactOverlayCore.kWhyNone, false, out why) == ReactOverlayCore.kKeep && why == ReactOverlayCore.kWhyExempt, "household without reactoverlayhh kept");
        Check(ReactOverlayCore.Stale(2000, 2000) && !ReactOverlayCore.Stale(1999, 2000), "stale after the tuned time");
        Check(ReactOverlayCore.StaleMs(0) == ReactOverlayCore.kStaleMinMs && ReactOverlayCore.StaleMs(99999) == ReactOverlayCore.kStaleMaxMs, "tuning clamped");
        Console.WriteLine((sFail == 0 ? "ok " : "FAILED ") + (sRun - sFail) + "/" + sRun);
        return sFail == 0 ? 0 : 1;
    }
}
