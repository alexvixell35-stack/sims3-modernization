using System;
using Ts3Fixes;

// Offline self-check of PicnicExitCore (docs/645 §1).
static class PicnicExitTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; if (sFails < 20) Console.WriteLine("FAIL " + what); } }

    static void Main()
    {
        const int Exit = PicnicExitCore.kExit;
        // Decide(exited, cleanUpState, basketOwns, simsWithRole, pendingInteractions)
        Check(PicnicExitCore.Decide(false, true, false, 0, 0) == Exit, "finished, empty, nothing queued: exit");
        Check(PicnicExitCore.Decide(true, true, false, 0, 0) == PicnicExitCore.kExited, "already exited: never a second Exit");
        Check(PicnicExitCore.Decide(true, true, false, 3, 2) == PicnicExitCore.kExited, "already exited wins over everything");
        Check(PicnicExitCore.Decide(false, false, false, 0, 0) == PicnicExitCore.kNotFinished, "still BeAtPicnic: not finished");
        Check(PicnicExitCore.Decide(false, true, true, 0, 0) == PicnicExitCore.kNotFinished, "basket still owns it: not finished");
        Check(PicnicExitCore.Decide(false, true, false, 1, 0) == PicnicExitCore.kSimsPresent, "a Sim with a role: skipped");
        Check(PicnicExitCore.Decide(false, true, false, 0, 1) == PicnicExitCore.kPending, "cleanup interaction queued: skipped (its OnFailure must stay)");
        Check(PicnicExitCore.Decide(false, true, false, 2, 1) == PicnicExitCore.kSimsPresent, "Sims and cleanup: Sims reported first");
        Check(PicnicExitCore.Name(Exit) == "exit" && PicnicExitCore.Name(PicnicExitCore.kPending) == "cleanup pending", "names");
        // only kExit exits
        int exits = 0;
        for (int m = 0; m < 32; m++)
            if (PicnicExitCore.Decide((m & 1) != 0, (m & 2) != 0, (m & 4) != 0, (m & 8) != 0 ? 1 : 0, (m & 16) != 0 ? 1 : 0) == Exit) exits++;
        Check(exits == 1, "exactly one of 32 input combinations exits");

        Console.WriteLine(sFails == 0 ? "picnicexit selftest: ok" : "picnicexit selftest: " + sFails + " FAIL");
        Environment.Exit(sFails == 0 ? 0 : 1);
    }
}
