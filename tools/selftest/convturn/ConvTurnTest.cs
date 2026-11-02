using System;
using Ts3Fixes;

// Offline self-check of ConvTurnCore (issue #166 build 19, docs/509): every input combination of the decision against its
// rule written out here a second time, plus the named cases.
static class ConvTurnTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; if (sFails < 20) Console.WriteLine("FAIL " + what); } }
    static bool B(int bits, int i) { return ((bits >> i) & 1) != 0; }

    static void Main()
    {
        float[] minutes = { 0f, 14.9f, 15f, 15.1f, 600f };
        for (int m = 0; m < 256; m++)
            foreach (float t in minutes)
            {
                bool hn = B(m, 0), ask = B(m, 1), gone = B(m, 2), an = B(m, 3), local = B(m, 4), qn = B(m, 5), noHead = B(m, 6), soc = B(m, 7);
                bool want = hn || (!ask && (gone || an || !local || qn || (!noHead && !soc) || t > 15f));
                Check(ConvTurnCore.Stuck(hn, ask, gone, an, local, qn, noHead, soc, t) == want, "stuck " + m + " t=" + t);
            }
        Check(ConvTurnCore.Stuck(false, false, false, false, false, false, true, false, 1f), "the holder is an off-screen townie: stuck");
        Check(!ConvTurnCore.Stuck(false, false, false, false, true, false, false, true, 1f), "the holder is local and talking: not stuck");
        Check(!ConvTurnCore.Stuck(false, false, false, false, true, false, true, false, 10f), "local holder idle 10 min: not stuck yet");
        Check(ConvTurnCore.Stuck(false, false, false, false, true, false, true, false, 20f), "local holder idle 20 min: stuck");
        Check(ConvTurnCore.Stuck(false, false, false, false, true, false, false, false, 1f), "the holder is busy with a non-social: stuck");
        Check(!ConvTurnCore.Stuck(false, true, true, true, false, true, false, false, 99f), "the asker holds the turn: never stuck");
        Check(ConvTurnCore.Stuck(true, false, false, false, true, false, true, false, 0f), "nobody holds the turn: stuck");
        Console.WriteLine(sFails == 0 ? "convturn selftest: ok" : "convturn selftest: " + sFails + " FAIL");
        Environment.Exit(sFails == 0 ? 0 : 1);
    }
}
