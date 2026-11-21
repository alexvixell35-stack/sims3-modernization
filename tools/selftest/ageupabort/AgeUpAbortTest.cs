using System;
using Ts3Fixes;

// Offline self-check of AgeUpAbortCore (issue #166 build 32, docs/512): every exception kind x task field x deferred flag x switch.
static class AgeUpAbortTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; Console.WriteLine("FAIL " + what); } }

    static void Main()
    {
        Exception[] es = { new NullReferenceException(), new InvalidOperationException(), new OperationCanceledException(), null };
        bool[] tf = { false, true };
        foreach (Exception e in es)
            foreach (bool taskNull in tf)
                foreach (bool deferred in tf)
                    foreach (bool on in tf)
                    {
                        bool ours = e is NullReferenceException && taskNull && deferred;
                        int want = !ours ? AgeUpAbortCore.kPass : on ? AgeUpAbortCore.kCaught : AgeUpAbortCore.kSeen;
                        Check(AgeUpAbortCore.Decide(e, taskNull, deferred, on) == want,
                            (e != null ? e.GetType().Name : "null") + " taskNull=" + taskNull + " deferred=" + deferred + " on=" + on);
                    }
        Check(AgeUpAbortCore.Decide(new NullReferenceException(), false, true, true) == AgeUpAbortCore.kPass, "a null reference with the builder still there is not ours");
        Check(AgeUpAbortCore.Decide(new NullReferenceException(), true, false, true) == AgeUpAbortCore.kPass, "after the wait (deferred already cleared) it is not ours");
        // 2026-11-12 report (paparazzo role NPC, InstantiateOffScreen, rc1111c): NRE on the Done read after Simulator.Sleep(0),
        // task field null. Decide takes no scope, so an off-screen role NPC decides like any Sim.
        Check(AgeUpAbortCore.Decide(new NullReferenceException(), true, true, true) == AgeUpAbortCore.kCaught, "11-12 paparazzo: aborted during the wait, still deferred, switch on: caught");
        Check(AgeUpAbortCore.Decide(new NullReferenceException(), true, false, true) == AgeUpAbortCore.kPass, "11-12 paparazzo: a second instantiation finished the age-up (deferred cleared): rethrown, else Instantiate makes a duplicate Sim");
        Console.WriteLine(sFails == 0 ? "ageupabort selftest: ok" : "ageupabort selftest: " + sFails + " FAIL");
        Environment.Exit(sFails == 0 ? 0 : 1);
    }
}
