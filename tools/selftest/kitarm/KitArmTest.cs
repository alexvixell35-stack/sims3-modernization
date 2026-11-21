// Offline self-check of the tester kit's A/B session counters (mods/ts3fixes/src/KitArmCore.cs, docs/314).
using System;
using Ts3Fixes;

static class KitArmTest
{
    static int fails;
    static void Check(bool ok, string what) { if (!ok) { fails++; Console.WriteLine("FAIL " + what); } }

    static int Main()
    {
        var k = new KitStats();
        int t = 1000;
        k.Restart(t);
        int median, low;
        k.Rates(out median, out low);
        Check(median == -1 && low == -1 && k.Secs == 0, "no whole second yet: -1 / -1");
        // 10 s at 30 updates a second (33 ms gaps; 30 x 33 = 990, the 31st gap closes each second)
        for (int i = 0; i < 300; i++) { t += 33; k.Gap(33, false, t); }
        Check(k.H250 == 0 && k.MaxMs == 33 && k.LiveMs == 9900, "smooth play: no hitch, longest 33 ms");
        k.Rates(out median, out low);
        Check(k.Secs == 9 && median >= 29 && median <= 31, "smooth play: ~30 updates a second, got " + median + " over " + k.Secs + " s");
        // one 3.2 s hitch: one hitch >= 250 ms and >= 1 s, 3 seconds with 0 or 1 update
        t += 3200; k.Gap(3200, false, t);
        Check(k.H250 == 1 && k.H1000 == 1 && k.MaxMs == 3200, "3.2 s hitch counted once in both classes");
        k.Rates(out median, out low);
        Check(low <= 1 && median >= 29, "hitch seconds: lowest " + low + " (0 or 1), median still ~30 (" + median + ")");
        // a 300 ms hitch: >= 250 only
        t += 300; k.Gap(300, false, t);
        Check(k.H250 == 2 && k.H1000 == 1, "300 ms hitch: >= 250 ms only");
        // a 9 s save pause: counted apart, no live time, no second closed, no hitch
        int secs = k.Secs, live = k.LiveMs;
        t += 9000; k.Gap(9000, true, t);
        Check(k.SaveGaps == 1 && k.SaveMaxMs == 9000 && k.H1000 == 1 && k.MaxMs == 3200 && k.Secs == secs && k.LiveMs == live, "save pause counted apart");
        t += 33; k.Gap(33, false, t);
        Check(k.Secs == secs, "first gap after the save closes no stale second");
        k.Gap(-5, false, t);
        Check(k.LiveMs == live + 33, "a negative gap (clock wrap) counts as 0");
        // updates above 120 a second land in the top bucket
        var f = new KitStats(); int u = 0; f.Restart(u);
        for (int i = 0; i < 500; i++) { u += 2; f.Gap(2, false, u); }
        Check(f.Ups[120] == 1 && f.Secs == 1, "500 updates in a second: top bucket");
        Console.WriteLine(fails == 0 ? "kitarm selftest: 0 FAIL" : "kitarm selftest: " + fails + " FAIL");
        return fails == 0 ? 0 : 1;
    }
}
