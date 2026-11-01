using System;
using Ts3Fixes;

// Offline self-check of JazzParamCore (issue #166 build 21, docs/511): every exception kind x posture x switch.
static class JazzParamTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; Console.WriteLine("FAIL " + what); } }

    static void Main()
    {
        Exception[] es = { new NullReferenceException(), new InvalidOperationException(), new ArgumentException(), null };
        foreach (Exception e in es)
            foreach (bool noPosture in new bool[] { false, true })
                foreach (bool on in new bool[] { false, true })
                {
                    bool ours = e is NullReferenceException && noPosture;
                    int want = !ours ? JazzParamCore.kPass : on ? JazzParamCore.kCaught : JazzParamCore.kSeen;
                    Check(JazzParamCore.Decide(e, noPosture, on) == want, (e != null ? e.GetType().Name : "null") + " noPosture=" + noPosture + " on=" + on);
                }
        Check(JazzParamCore.Decide(new NullReferenceException(), false, true) == JazzParamCore.kPass, "a null reference with a posture is not ours");
        Console.WriteLine(sFails == 0 ? "jazzparam selftest: ok" : "jazzparam selftest: " + sFails + " FAIL");
        Environment.Exit(sFails == 0 ? 0 : 1);
    }
}
