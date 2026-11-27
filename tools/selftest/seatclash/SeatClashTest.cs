using System;
using Ts3Fixes;

// Offline checks of SeatClashCore (seatclash, docs/686 §42).
static class SeatClashTest
{
    static int sFail, sRun;

    static void Check(bool ok, string what)
    {
        sRun++;
        if (!ok) { sFail++; Console.WriteLine("FAIL " + what); }
    }

    static int Main()
    {
        const int G = HhSharedCore.kGo, W = HhSharedCore.kWould;
        Check(SeatClashCore.Exit(true, true, true, true) == SeatClashCore.kOk, "seated whatever was seen");
        Check(SeatClashCore.Exit(false, true, true, true) == SeatClashCore.kPushFailed, "push failed wins (the Sim was added to the use list for the wait)");
        Check(SeatClashCore.Exit(false, false, true, true) == SeatClashCore.kTaken, "taken on arrival");
        Check(SeatClashCore.Exit(false, false, false, true) == SeatClashCore.kScoot, "pull-out refused");
        Check(SeatClashCore.Exit(false, false, false, false) == SeatClashCore.kOther, "walk failed / other");
        Check(SeatClashCore.Shrug(SeatClashCore.kTaken, false) && !SeatClashCore.Shrug(SeatClashCore.kTaken, true), "a stool taken on arrival ends without a shrug");
        Check(SeatClashCore.Shrug(SeatClashCore.kPushFailed, true) && SeatClashCore.Shrug(SeatClashCore.kScoot, false) && !SeatClashCore.Shrug(SeatClashCore.kOther, false) && !SeatClashCore.Shrug(SeatClashCore.kOk, false), "shrugs: push and pull-out");
        bool has;
        float[] d = { 1f, 2f, 3f, 4f };
        Check(SeatClashCore.Pick(d, new bool[] { true, true, true, true }, new bool[] { false, true, false, true }, 4, true, out has) == 1 && has, "pick: nearest chair with a free food slot");
        Check(SeatClashCore.Pick(d, new bool[] { true, true, true, true }, new bool[] { false, true, false, true }, 4, false, out has) == 0 && !has, "pick: nearest chair when no food slot is wanted");
        Check(SeatClashCore.Pick(d, new bool[] { false, false, true, true }, new bool[] { true, true, false, false }, 4, true, out has) == 2 && !has, "pick: held chairs are skipped, their food slots too");
        Check(SeatClashCore.Pick(d, new bool[] { false, false, false, false }, new bool[] { true, true, true, true }, 4, true, out has) == -1 && !has, "pick: every free chair held = no chair");
        Check(SeatClashCore.Pick(d, new bool[] { true, true, true, true }, new bool[] { true, true, true, true }, 0, true, out has) == -1, "pick: no chairs");
        Check(SeatClashCore.PushStep(true, false, 0f, 2.5f) == SeatClashCore.kClear, "push: clear at once");
        Check(SeatClashCore.PushStep(false, false, 2.4f, 2.5f) == SeatClashCore.kWait, "push: inside the grace");
        Check(SeatClashCore.PushStep(false, false, 2.5f, 2.5f) == SeatClashCore.kGraceOver, "push: the grace is over");
        Check(SeatClashCore.PushStep(false, true, 0f, 2.5f) == SeatClashCore.kGraceOver, "push: unseen, no grace");
        Check(SeatClashCore.ScootAllow(false, true, true, G), "pull-out: refused only by Sims after the grace: allowed");
        Check(!SeatClashCore.ScootAllow(true, true, true, G) && !SeatClashCore.ScootAllow(false, false, true, G) && !SeatClashCore.ScootAllow(false, true, false, G) && !SeatClashCore.ScootAllow(false, true, true, W), "pull-out: game's yes, no grace, furniture, switch off: unchanged");
        Check(!SeatClashCore.Stale(0f) && !SeatClashCore.Stale(59f) && SeatClashCore.Stale(60f) && SeatClashCore.Stale(-1f) && SeatClashCore.Stale(float.NaN), "stale records");
        Console.WriteLine((sFail == 0 ? "OK" : "FAILED") + " seatclash selftest: " + (sRun - sFail) + "/" + sRun);
        return sFail == 0 ? 0 : 1;
    }
}
