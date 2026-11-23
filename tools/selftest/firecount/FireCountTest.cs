using System;
using Ts3Fixes;

// Offline self-check of mods/ts3fixes/src/FireCountCore.cs (docs/868): the rescue and death decisions and the pending
// counts they leave, EA's (off) and the fix's (on), for the bug layouts and controls.
static class FireCountTest
{
    static int sFail;
    static void Check(bool ok, string what) { if (!ok) { sFail++; Console.WriteLine("FAIL " + what); } }

    static int Main()
    {
        // FcCore.Rescue
        Check(FcCore.Rescue(false, false, false) == FcCore.Ea && FcCore.Rescue(false, true, false) == FcCore.Ea, "unplaced completion (firetrapcount / fireleftover drop): EA's");
        Check(FcCore.Rescue(true, true, false) == FcCore.Count, "listed Sim's first rescue counts");
        Check(FcCore.Rescue(true, false, false) == FcCore.Skip, "passer-by (not listed) skipped");
        Check(FcCore.Rescue(true, true, true) == FcCore.Skip, "re-trapped / second pile / dropped Sim's pile skipped");
        Check(FcCore.Rescue(true, false, true) == FcCore.Skip, "not listed and accounted skipped");

        // FcCore.DeathDrops: not accounted = still trapped, or freed by a rescue that never reached the job
        Check(FcCore.DeathDrops(false), "not accounted Sim's death drops one");
        Check(!FcCore.DeathDrops(true), "counted (freed or re-trapped) Sim's death drops none");

        bool done;
        // bug (1): a passer-by's rescue with two victims trapped
        Check(FcCore.AfterRescue(false, 2, FcCore.Rescue(true, false, false)) == 1, "off: passer-by takes a task");
        Check(FcCore.AfterRescue(true, 2, FcCore.Rescue(true, false, false)) == 2, "on: passer-by takes none");
        // bug (2): a second pile of a counted rubble Sim
        Check(FcCore.AfterRescue(false, 1, FcCore.Rescue(true, true, true)) == 0, "off: second pile takes a task");
        Check(FcCore.AfterRescue(true, 1, FcCore.Rescue(true, true, true)) == 1, "on: second pile takes none");
        // bug (3a): a freed Sim's death while another is trapped
        Check(FcCore.AfterDeath(false, 1, true, out done) == 0 && !done, "off: freed Sim's death takes the last task, job not completed");
        Check(FcCore.AfterDeath(true, 1, true, out done) == 1 && !done, "on: freed Sim's death takes none");
        // bug (3b): the last trapped Sim's death
        Check(FcCore.AfterDeath(false, 1, false, out done) == 0 && !done, "off: last death leaves the job at 0, not completed");
        Check(FcCore.AfterDeath(true, 1, false, out done) == 0 && done, "on: last death completes the job");

        // controls: the same in both arms
        foreach (bool on in new bool[] { false, true })
        {
            Check(FcCore.AfterRescue(on, 2, FcCore.Rescue(true, true, false)) == 1, "listed Sim's rescue takes one (on=" + on + ")");
            Check(FcCore.AfterRescue(on, 1, FcCore.Rescue(false, false, false)) == 0, "unplaced completion takes one (on=" + on + ")");
            Check(FcCore.AfterDeath(on, 2, false, out done) == 1 && !done, "still-trapped death with another left takes one (on=" + on + ")");
            Check(FcCore.AfterRescue(on, 0, FcCore.Count) == 0 && FcCore.AfterDeath(on, 0, false, out done) == 0 && !done, "nothing pending stays 0 (on=" + on + ")");
        }

        Console.WriteLine(sFail == 0 ? "firecount selftest: all passed" : "firecount selftest: " + sFail + " FAILED");
        return sFail == 0 ? 0 : 1;
    }
}
