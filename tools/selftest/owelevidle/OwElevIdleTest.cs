using System;
using Ts3Fixes;

// Offline self-check of OwElevIdleCore (docs/655).
static class OwElevIdleTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; if (sFails < 20) Console.WriteLine("FAIL " + what); } }

    const int NotCase = OwElevIdleCore.kNotCase, Riding = OwElevIdleCore.kRiding, Exempt = OwElevIdleCore.kExempt, Drop = OwElevIdleCore.kDrop;

    // defaults = the logged case: an NPC at home at 05:00, its elevator entry left behind
    static int D(bool present = true, bool sleeping = false, bool task = false, bool routing = false, bool usingIt = false, bool inside = false, bool exempt = false)
    {
        return OwElevIdleCore.Decide(present, sleeping, task, routing, usingIt, inside, exempt);
    }

    static void Main()
    {
        Check(D() == Drop, "NPC with an idle entry: dropped, no Overwatch reset");
        Check(D(present: false) == NotCase && D(present: false, exempt: true) == NotCase, "no created Sim: Overwatch skips it, so do we");
        Check(D(sleeping: true) == Riding, "waiting in OnPortalStart: riding");
        Check(D(task: true) == Riding, "live ride task: riding");
        Check(D(routing: true) == Riding, "on a route (approaching or leaving): riding");
        Check(D(usingIt: true) == Riding, "in the interior's use list: riding");
        Check(D(inside: true) == Riding, "standing inside an elevator: riding");
        Check(D(exempt: true) == Exempt, "household / selected / unknown scope idle entry: left to Overwatch");
        Check(D(sleeping: true, exempt: true) == Riding, "exempt rider: riding, left to Overwatch");
        // every single sign of a ride keeps Overwatch's reset
        for (int m = 1; m < 32; m++)
            Check(D(sleeping: (m & 1) != 0, task: (m & 2) != 0, routing: (m & 4) != 0, usingIt: (m & 8) != 0, inside: (m & 16) != 0) == Riding, "ride sign mask " + m);

        if (sFails > 0) { Console.WriteLine("owelevidle selftest: " + sFails + " FAIL"); Environment.Exit(1); }
        Console.WriteLine("owelevidle selftest: ok");
    }
}
