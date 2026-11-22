using System;
using Ts3Fixes;

// Offline self-check of mods/ts3fixes/src/FireDoorKeepCore.cs (docs/864): FdkCore.Restore for the bug layouts and controls.
static class FireDoorKeepTest
{
    static int sFail;
    static void Check(bool ok, string what) { if (!ok) { sFail++; Console.WriteLine("FAIL " + what); } }

    static int Main()
    {
        const int N = FdkState.Nobody, A = FdkState.Anybody, R = FdkState.NoRoom, HH = 2, PETS = 4, OWNERS = 5, VIP = 6;
        object sim = new object(), other = new object();
        FdkState bareN = new FdkState(N, null, R), bareA = new FdkState(A, null, R), hh = new FdkState(HH, sim, R);
        FdkState playerN = new FdkState(N, sim, R), oneWay = new FdkState(A, null, 5), unlockedBy = new FdkState(A, sim, R);

        // bug (1): a large fire's cleanup sets every locked door to bare Anybody; each lock the job never sets comes back
        foreach (FdkState s in new FdkState[] { hh, playerN, new FdkState(PETS, null, R), new FdkState(OWNERS, null, R), new FdkState(VIP, other, R), new FdkState(N, null, 7) })
        {
            Check(FdkCore.Restore(s, bareA, null, false) == s, "large fire, no record: lock type " + s.Type + " kept");
            Check(FdkCore.Restore(s, bareA, bareA, false) == s, "large fire, with a record: lock type " + s.Type + " kept");
        }
        // a door the job locked: back to its state before the job (one-way room, owner of the unlock)
        Check(FdkCore.Restore(bareN, bareA, oneWay, true) == oneWay, "large fire: one-way room back");
        Check(FdkCore.Restore(bareN, bareA, unlockedBy, true) == unlockedBy, "large fire: unlock owner back");
        Check(FdkCore.Restore(bareN, bareA, bareA, true) == null, "large fire: plain job lock, EA's unlock already right");
        // a bare Nobody door from before the job (lot designer, apartment NPC door) stays locked
        Check(FdkCore.Restore(bareN, bareA, bareN, false) == bareN, "large fire: old bare Nobody kept");
        // bug (2): another job type leaves its lock; undone
        Check(FdkCore.Restore(bareN, bareN, bareA, true) == bareA, "other job: job lock undone");
        Check(FdkCore.Restore(bareN, bareN, oneWay, true) == oneWay, "other job: one-way door back");
        Check(FdkCore.Restore(bareN, bareN, bareN, false) == null, "other job: old bare Nobody untouched");

        // controls: EA's outcome
        Check(FdkCore.Restore(hh, hh, hh, false) == null, "unchanged lock");
        Check(FdkCore.Restore(bareA, bareA, bareA, false) == null, "unlocked before and after");
        Check(FdkCore.Restore(bareA, bareA, bareA, true) == null, "job lock the firefighter broke down");
        Check(FdkCore.Restore(oneWay, oneWay, oneWay, false) == null, "one-way door the job never locked");
        Check(FdkCore.Restore(bareN, hh, bareA, true) == null, "left in a state EA's job never sets (household lock with owner)");
        Check(FdkCore.Restore(hh, new FdkState(HH, other, R), hh, false) == null, "owner changed by someone else");
        Check(FdkCore.Restore(bareN, bareA, null, false) == null, "bare Nobody, no record (loaded mid-job): EA's unlock");
        Check(FdkCore.Restore(bareN, bareN, null, true) == null, "bare Nobody, no record, other job: EA's lock stays");
        Check(FdkCore.Restore(bareN, bareA, hh, false) == null, "bare Nobody now, locked otherwise before, not by the job: EA's outcome");
        Check(FdkCore.Restore(bareN, bareA, null, false, true) == bareN, "large fire, apartment NPC door, no record: kept locked");
        Check(FdkCore.Restore(bareN, bareN, null, false, true) == null, "other job, apartment NPC door unchanged");

        // FdkState.Same / Bare
        Check(bareN.Same(new FdkState(N, null, R)) && !bareN.Same(playerN) && !bareN.Same(null) && !bareA.Same(oneWay), "Same");
        Check(bareN.Bare(N) && !playerN.Bare(N) && !oneWay.Bare(A) && !bareA.Bare(N), "Bare");

        Console.WriteLine(sFail == 0 ? "firedoorkeep selftest: all passed" : "firedoorkeep selftest: " + sFail + " FAILED");
        return sFail == 0 ? 0 : 1;
    }
}
