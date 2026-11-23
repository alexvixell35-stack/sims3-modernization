using System;
using Ts3Fixes;

// Offline self-check of RouteRelaxCore (docs/675): the one scan returns the spot EA's stage order returns, the margin
// rule of fglnnearspot, and the follower try count.
static class RouteRelaxTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; if (sFails < 20) Console.WriteLine("FAIL " + what); } }

    // the spots (class, metres out) through the one scan; returns the index taken, -1 none
    static int Scan(int stages, int[] cls, float[] dist, float margin, out bool stopped, out int offered)
    {
        FglnPick pick = new FglnPick(stages, margin);
        offered = 0;
        for (int i = 0; i < cls.Length; i++)
        {
            offered++;
            if (pick.Offer(cls[i], i, 0f, 0f, dist != null ? dist[i] : i)) break;
        }
        stopped = pick.Stopped;
        return pick.Rank >= 0 ? (int)pick.X : -1;
    }

    static void Main()
    {
        const int I = RouteRelaxCore.kInside, O = RouteRelaxCore.kOutside, X = RouteRelaxCore.kOther;
        Check(RouteRelaxCore.SpotClass(5) == I && RouteRelaxCore.SpotClass(0) == O && RouteRelaxCore.SpotClass(-1) == X, "spot classes");
        Check(RouteRelaxCore.Stages(0x7F, true, 3) == 7, "all stages from a room");
        Check(RouteRelaxCore.Stages(0x7F, true, 0) == 6, "no indoors stage from outdoors");
        Check(RouteRelaxCore.Stages(0x7F, true, -1) == 6, "no indoors stage from an unknown room");
        Check(RouteRelaxCore.Stages(0x7F, false, 3) == 4, "off a lot: the same-lot stage only");
        Check(RouteRelaxCore.Stages(RouteRelaxCore.kEarly, true, 3) == 0, "early stages only");
        Check(RouteRelaxCore.StageCount(7) == 3 && RouteRelaxCore.StageCount(5) == 2 && RouteRelaxCore.StageCount(4) == 1 && RouteRelaxCore.StageCount(0) == 0, "stage count");
        Check(RouteRelaxCore.Rank(7, I) == 0 && RouteRelaxCore.Rank(7, O) == 1 && RouteRelaxCore.Rank(7, X) == 2, "ranks, all stages");
        Check(RouteRelaxCore.Rank(6, I) == 2 && RouteRelaxCore.Rank(3, X) == -1 && RouteRelaxCore.Rank(5, O) == 2, "ranks, stages missing");
        Check(RouteRelaxCore.BestRank(7) == 0 && RouteRelaxCore.BestRank(6) == 1 && RouteRelaxCore.BestRank(4) == 2 && RouteRelaxCore.BestRank(0) == -1, "best rank");

        // every stage set with two or more stages, every sequence of up to 6 spots of the three classes: EA's spot
        int cases = 0;
        int[] sets = new int[] { 3, 5, 6, 7 };
        foreach (int st in sets)
            for (int len = 0; len <= 6; len++)
            {
                int n = 1;
                for (int k = 0; k < len; k++) n *= 3;
                for (int code = 0; code < n; code++)
                {
                    int[] cls = new int[len];
                    int c = code;
                    for (int k = 0; k < len; k++) { cls[k] = c % 3; c /= 3; }
                    int scans, offered;
                    bool stopped;
                    int ea = RouteRelaxCore.EaPick(st, cls, out scans);
                    int mine = Scan(st, cls, null, -1f, out stopped, out offered);
                    cases++;
                    Check(ea == mine && !stopped, "one scan = EA's stages, set " + st + " code " + code + " len " + len);
                    // with a margin: a spot is taken exactly when EA takes one, never further out than EA's, and at most
                    // the margin past the nearest spot any stage takes
                    int near = Scan(st, cls, null, 2f, out stopped, out offered);
                    int first = -1;
                    for (int k = 0; k < len && first < 0; k++) if (RouteRelaxCore.Rank(st, cls[k]) >= 0) first = k;
                    Check((near < 0) == (first < 0) && (near < 0 || (near >= first && near <= first + 2)), "margin: within 2 of the nearest, set " + st + " code " + code + " len " + len);
                    Check((near < 0) == (ea < 0) && near <= ea, "margin: never further out than EA's spot, set " + st + " code " + code + " len " + len);
                }
            }
        Check(cases == 4 * 1093, "case count");

        // the s154 guitar jig: start in a room, outdoor spots only: EA scans twice, the same spot
        int s2, off;
        bool stop;
        int[] jig = new int[] { O, O, O };
        Check(RouteRelaxCore.EaPick(7, jig, out s2) == 0 && s2 == 2, "EA: indoors scan fails, outdoors scan finds the first");
        Check(Scan(7, jig, null, -1f, out stop, out off) == 0 && off == 3, "one scan: walks on for an indoor spot, keeps the first outdoor one");
        Check(Scan(7, jig, new float[] { 17f, 18f, 22f }, 4f, out stop, out off) == 0 && stop && off == 3, "margin 4: ends at the spot 5 m further out");
        Check(Scan(7, new int[] { O, I }, new float[] { 3f, 30f }, 4f, out stop, out off) == 0 && stop, "margin: a far indoor spot loses");
        Check(Scan(7, new int[] { O, I }, new float[] { 3f, 7f }, 4f, out stop, out off) == 1 && !stop, "margin: an indoor spot at the margin wins");
        Check(Scan(7, new int[] { X, O }, new float[] { 3f, 5f }, 4f, out stop, out off) == 1, "margin: a better later stage inside the margin wins");
        Check(Scan(3, new int[] { X, X }, null, 0f, out stop, out off) == -1 && !stop, "spots no stage takes are passed over");

        // follower tries
        FlState st0 = new FlState();
        int[] want = new int[] { 1, 1, 2, 2, 3, 3 };
        for (int i = 0; i < 6; i++) Check(RouteRelaxCore.FlNote(st0, 10f + 0.45f * (i / 2), 20f, 100 + i, 1000) == want[i], "try of call " + i);
        Check(RouteRelaxCore.FlWiden(3, 3) && !RouteRelaxCore.FlWiden(2, 3) && RouteRelaxCore.FlWiden(5, 3) && !RouteRelaxCore.FlWiden(1, 1) && !RouteRelaxCore.FlWiden(3, 0), "last try and later");
        Check(RouteRelaxCore.FlNote(st0, 10.9f, 20f, 107, 1000) == 4 && st0.Calls == 1, "the same point after both arcs: one more try");
        Check(RouteRelaxCore.FlNote(st0, 10.9f, 20f, 108, 1000) == 4 && st0.Calls == 2, "its second arc");
        Check(RouteRelaxCore.FlNote(st0, 14f, 20f, 110, 1000) == 1, "another meeting point starts over");
        Check(RouteRelaxCore.FlNote(st0, 14.4f, 20f, 2000, 1000) == 1, "an old try does not count");
        Check(RouteRelaxCore.FlNote(st0, 14.4f, 20.5f, 1990, 1000) == 1, "the clock went back: start over");

        Console.WriteLine(sFails == 0 ? "routerelax selftest: PASS (" + cases + " scan shapes)" : "routerelax selftest: FAIL " + sFails);
        Environment.Exit(sFails == 0 ? 0 : 1);
    }
}
