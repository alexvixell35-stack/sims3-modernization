using System;
using Ts3Fixes;

// Offline checks of CamSafeCore (camsafe, docs/690): margin, lead, recent-path hold, object hold, jump pause, verdicts.
static class CamSafeTest
{
    static int sFail, sRun;

    static void Check(bool ok, string what)
    {
        sRun++;
        if (!ok) { sFail++; Console.WriteLine("FAIL " + what); }
    }

    static int Main()
    {
        CamSafeCore k = new CamSafeCore();  // defaults: margin 25%, lead 2 s, hold 5 s, jump 25 m, jump hold 3 s
        // a camera at rest, reach 80 m, eye 40 m up
        Check(k.Sample(0, 0, 0, 0, 0, 40, -30, 80), "first sample is a jump (camera found)");
        Check(k.Jumping(2999) && k.Near(1e6f, 0, 0), "jump hold: everything seen");
        Check(!k.Jumping(3001), "jump hold ends after 3 s");
        for (int t = 3000; t <= 9000; t += 33) k.Sample(t, 0, 0, 0, 0, 40, -30, 80);
        Check(!k.Jumping(9000), "settled");
        Check(Math.Abs(k.Reach - 100f) < 0.5f, "at rest: reach 80 x 1.25 = 100 m (" + k.Reach + ")");
        Check(k.Near(95, 0, 0) && !k.Near(105, 0, 0), "margin: 95 m seen, 105 m not");

        // a steady pan at 30 m/s along x (1 m per 33 ms): never a jump, lead 60 m, recent path adds up to 5 s x 30 m/s
        int now = 9000; float x = 0;
        for (int i = 0; i < 300; i++) { now += 33; x += 1f; Check(!k.Sample(now, x, 0, 0, x, 40, -30, 80), "pan sample " + i + " is no jump"); }
        Check(k.Speed > 29f && k.Speed < 31f, "speed ~30 m/s (" + k.Speed + ")");
        Check(k.Reach > 100f + 59f + 140f, "pan: margin + lead + recent path (" + k.Reach + ")");
        Check(k.Near(x - 140f - 90f, 0, 0), "a spot the camera passed 4.6 s ago is still seen");
        Check(k.Near(x + 150f, 0, 0), "a spot 150 m ahead of a 30 m/s pan is seen");
        Check(!k.Near(x + 1000f, 0, 0), "far away still unseen");

        // stop: the speed decays, the recent path ages out after 5 s
        for (int i = 0; i < 400; i++) { now += 33; k.Sample(now, x, 0, 0, x, 40, -30, 80); }
        Check(k.Speed < 1f, "speed decays at rest (" + k.Speed + ")");
        Check(Math.Abs(k.Reach - 100f) < 2f, "reach back to 100 m after the hold (" + k.Reach + ")");

        // zoom out then in: the larger reach holds for 5 s
        now += 33; k.Sample(now, x, 0, 0, x, 50, -30, 100);
        now += 33; k.Sample(now, x, 0, 0, x, 40, -30, 80);
        Check(k.Reach >= 125f, "the zoom's larger reach holds (" + k.Reach + ")");

        // a lot jump: 500 m in one sample
        int jumps = k.Jumps;
        now += 33;
        Check(k.Sample(now, x + 500f, 0, 0, x + 500f, 40, -30, 80) && k.Jumps == jumps + 1, "500 m in one sample: jump");
        Check(k.Reach == float.MaxValue && k.Near(-1e5f, 0, 0), "after a jump everything is seen");
        Check(k.Jumping(now + 2900) && !k.Jumping(now + 3100), "jump hold 3 s");
        // a 30 m reach change (map view) is a jump too
        for (int i = 0; i < 200; i++) { now += 33; k.Sample(now, x + 500f, 0, 0, x + 500f, 40, -30, 80); }
        Check(!k.Jumping(now), "settled again");
        now += 33;
        Check(k.Sample(now, x + 500f, 0, 0, x + 500f, 40, -30, 200), "reach change 120 m: jump");

        // object hold
        k.NoteSeen(7, 1000);
        Check(k.Held(7, 5999) && !k.Held(7, 6001) && !k.Held(8, 1000), "object hold 5 s, per id");
        for (ulong i = 100; i < 1200; i++) k.NoteSeen(i, 7000);  // over the 1024 cap: pruned or restarted, never throws
        Check(k.Held(1199, 7000), "the newest note survives the cap");

        // TickCount wrap
        CamSafeCore w = new CamSafeCore();
        w.Sample(int.MaxValue - 10, 0, 0, 0, 0, 40, -30, 80);
        Check(w.Jumping(unchecked(int.MaxValue + 100)), "jump hold across the TickCount wrap");
        w.NoteSeen(1, int.MaxValue - 10);
        Check(w.Held(1, unchecked(int.MaxValue + 1000)), "object hold across the wrap");

        // verdicts
        Check(CamSafeCore.Verdict(false, true, false, false, false) == CamSafeCore.kUnseen, "unseen both");
        Check(CamSafeCore.Verdict(true, true, false, false, false) == CamSafeCore.kSeenOld, "on screen: seen by the old test");
        Check(CamSafeCore.Verdict(false, false, true, true, true) == CamSafeCore.kSeenOld, "old seen wins the count");
        Check(CamSafeCore.Verdict(false, true, true, true, true) == CamSafeCore.kVetoJump, "jump first");
        Check(CamSafeCore.Verdict(false, true, false, true, true) == CamSafeCore.kVetoHold, "then hold");
        Check(CamSafeCore.Verdict(false, true, false, false, true) == CamSafeCore.kVetoMargin, "then margin");

        Console.WriteLine("camsafe selftest: " + (sRun - sFail) + "/" + sRun + " passed");
        return sFail == 0 ? 0 : 1;
    }
}
