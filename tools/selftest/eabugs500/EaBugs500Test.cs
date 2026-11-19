using System;
using Ts3Fixes;

// Offline self-check of mods/ts3fixes/src/EaBugs500Core.cs (docs/500): every decision against its truth table.
static class EaBugs500Test
{
    static int sFail;
    static void Check(bool ok, string what) { if (!ok) { sFail++; Console.WriteLine("FAIL " + what); } }

    static int Main()
    {
        // playpenflags: a mark is cleared only when it names this run's toddler / adult, or this run set it
        Check(Eb500Core.PpClearId(42, 42), "pick-up mark of this toddler cleared");
        Check(!Eb500Core.PpClearId(42, 43), "another toddler's pick-up mark left");
        Check(!Eb500Core.PpClearId(0, 0), "no mark: nothing to clear (id 0 is no Sim)");
        Check(!Eb500Core.PpClearId(0, 42), "clear mark stays clear");
        Check(Eb500Core.PpClearOutside(true, 7, 7), "outside play of this adult cleared");
        Check(!Eb500Core.PpClearOutside(true, 7, 8), "another adult's outside play left");
        Check(!Eb500Core.PpClearOutside(false, 7, 7), "an old outside id with play off is left (EA keeps mOutsideSim after a normal end)");
        Check(!Eb500Core.PpClearOutside(true, 0, 0), "adult id 0 never matches");
        Check(Eb500Core.PpClearSync(false, true), "sync set during this run cleared");
        Check(!Eb500Core.PpClearSync(true, true), "sync set before this run (a follower) left");
        Check(!Eb500Core.PpClearSync(false, false) && !Eb500Core.PpClearSync(true, false), "sync already clear: nothing");
        // voodoobindwait
        Check(Eb500Core.VbStop(true, false) && Eb500Core.VbStop(false, true) && Eb500Core.VbStop(true, true), "cancel or a gone sleeper ends the wait");
        Check(!Eb500Core.VbStop(false, false), "a plain sleep keeps EA's wait");
        // lotentrypet: all 16 rows of LeUnvalidate; only (entry false, now true, not human, no spot) undoes the validation
        int hits = 0;
        for (int m = 0; m < 16; m++)
        {
            bool atEntry = (m & 1) != 0, now = (m & 2) != 0, human = (m & 4) != 0, invalid = (m & 8) != 0;
            bool want = !atEntry && now && !human && invalid;
            if (Eb500Core.LeUnvalidate(atEntry, now, human, invalid) != want) Check(false, "LeUnvalidate row " + m);
            if (want) hits++;
        }
        Check(hits == 1, "LeUnvalidate fires on exactly one row");
        Check(Eb500Core.LeUnvalidate(false, true, false, true), "a horse's failed door plan is undone");
        Check(!Eb500Core.LeUnvalidate(false, true, true, true), "a human's failed door plan stays vanilla");
        Check(!Eb500Core.LeUnvalidate(false, true, false, false), "a pet that found a spot keeps the cache");
        Check(Eb500Core.LeRepair(true, true) && !Eb500Core.LeRepair(true, false) && !Eb500Core.LeRepair(false, true) && !Eb500Core.LeRepair(false, false), "load repair only for validated with no spot");
        // horselotentry: all 128 boolean rows of HlDoomed (levels 0 / 1); only the one all-true row (player false) skips
        hits = 0;
        for (int m = 0; m < 128; m++)
        {
            bool horse = (m & 1) != 0, player = (m & 2) != 0, own = (m & 4) != 0, ground = (m & 8) != 0, upper = (m & 16) != 0, mail = (m & 32) != 0, refuse = (m & 64) != 0;
            bool want = horse && !player && own && ground && upper && mail && refuse;
            if (Eb500Core.HlDoomed(horse, player, own, ground ? 0 : 1, upper ? 1 : 0, mail, refuse) != want) Check(false, "HlDoomed row " + m);
            if (want) hits++;
        }
        Check(hits == 1, "HlDoomed fires on exactly one row");
        Check(Eb500Core.HlDoomed(true, false, true, 0, -1, true, true), "a basement door spot (level -1) is off the ground too");
        Check(!Eb500Core.HlDoomed(true, false, true, 1, 1, true, true), "a horse already on level 1 is left to EA");
        Check(!Eb500Core.HlDoomed(true, false, true, -1, 1, true, true), "a horse below the ground is left to EA");
        Check(Eb500Core.HlOneLevel(new[] { 0, 0 }) && Eb500Core.HlOneLevel(new[] { 0 }) && Eb500Core.HlOneLevel(new[] { 1, 1, 1 }), "one-level portals (fence gate, ground door, upstairs door or arch) need no test");
        Check(!Eb500Core.HlOneLevel(new[] { 0, 1 }) && !Eb500Core.HlOneLevel(new[] { 1, 2 }) && !Eb500Core.HlOneLevel(new[] { -1, 0 }) && !Eb500Core.HlOneLevel(new[] { 1, 1, 0 }), "stairs, upper stairs and basement stairs must refuse");
        Check(!Eb500Core.HlOneLevel(new int[0]) && !Eb500Core.HlOneLevel(null), "a portal with no openings is not known to be one-level");
        Check(Eb500Core.HlMispredict(2, true, false, 0, false), "site 2: the spot kept means EA's door plan succeeded");
        Check(!Eb500Core.HlMispredict(1, true, true, 0, true), "site 1 keeps the spot anyway; a ground destination is the mailbox");
        Check(Eb500Core.HlMispredict(1, true, true, 1, true), "a route ending on level 1 of the lot is a wrong prediction");
        Check(!Eb500Core.HlMispredict(1, true, true, 1, false) && !Eb500Core.HlMispredict(1, true, false, 1, true), "another lot, or a failed route, is not");
        Check(!Eb500Core.HlMispredict(2, false, true, 0, true), "site 2 with the spot reset and a ground mailbox route is the doomed case");
        Console.WriteLine(sFail == 0 ? "eabugs500 selftest: PASS" : "eabugs500 selftest: " + sFail + " FAIL");
        return sFail == 0 ? 0 : 1;
    }
}
