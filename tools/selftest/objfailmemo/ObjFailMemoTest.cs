using System;
using Ts3Fixes;

// Offline self-check of ObjFailMemoCore (docs/650).
static class ObjFailMemoTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; if (sFails < 20) Console.WriteLine("FAIL " + what); } }

    const long Hour = 1000, Win = 2 * Hour, Block = Hour;
    const int K = 2;
    const int NoDest = ObjFailMemoCore.kNoDestination, NoPath = ObjFailMemoCore.kNoPath, Locked = ObjFailMemoCore.kPortalLocked;

    static int Rec(ObjFailMemoCore m, ulong sim, ulong obj, bool exempt, int res, int attempt, long now)
    {
        int f;
        return m.Record(sim, obj, exempt, res, attempt, now, Win, Block, K, out f);
    }

    static void Main()
    {
        ObjFailMemoCore m = new ObjFailMemoCore();

        // which results count (RoutePlanResultType: 1 Succeeded, 3 PlannerAborted, 10 NoDestinationSpecified, 16 NoPath, 17 blocked by Sim, 18 PortalLocked)
        Check(ObjFailMemoCore.Fails(NoDest) && ObjFailMemoCore.Fails(NoPath) && ObjFailMemoCore.Fails(Locked), "no destination, no path, locked portal count");
        Check(!ObjFailMemoCore.Fails(1) && !ObjFailMemoCore.Fails(3) && !ObjFailMemoCore.Fails(17), "success, aborted, Sim blocker: not counted");
        Check(Rec(m, 1, 9, false, 1, 1, 100) == ObjFailMemoCore.kNotRecorded && m.Count == 0, "success never recorded");
        Check(Rec(m, 1, 9, false, 17, 1, 100) == ObjFailMemoCore.kNotRecorded && m.Count == 0, "transient Sim blocker never recorded");
        Check(Rec(m, 2, 9, true, NoPath, 1, 100) == ObjFailMemoCore.kExempt && m.Count == 0, "exempt Sim never recorded");
        Check(Rec(m, 0, 9, false, NoPath, 1, 100) == ObjFailMemoCore.kNotRecorded && Rec(m, 1, 0, false, NoPath, 1, 100) == ObjFailMemoCore.kNotRecorded, "no ids: not recorded");
        int f;
        Check(m.Record(1, 9, false, NoPath, 1, 100, Win, Block, 0, out f) == ObjFailMemoCore.kNotRecorded, "K 0: not recorded");
        Check(m.Record(1, 9, false, NoPath, 1, 100, Win, 0, K, out f) == ObjFailMemoCore.kNotRecorded, "zero block: not recorded");

        // K fails of separate attempts within the window block; replans of one attempt count once
        Check(Rec(m, 1, 9, false, NoPath, 11, 100) == ObjFailMemoCore.kCounted && m.Count == 1, "first fail counted");
        Check(m.Test(1, 9, true, false, 110) == ObjFailMemoCore.kPass, "one fail: not refused yet");
        Check(Rec(m, 1, 9, false, NoPath, 11, 120) == ObjFailMemoCore.kSameAttempt, "replan of the same attempt: counted once");
        Check(m.BlockedCount == 0, "no block after one attempt");
        Check(m.Record(1, 9, false, NoDest, 12, 300, Win, Block, K, out f) == ObjFailMemoCore.kBlocked && f == 2 && m.BlockedCount == 1, "second attempt: blocked");

        // refusal: autonomous only, the pair only
        Check(m.Test(1, 9, true, false, 350) == ObjFailMemoCore.kRefuse, "autonomous pick of the pair: refused");
        Check(m.Test(1, 9, false, false, 350) == ObjFailMemoCore.kPass, "user-directed / pushed pick: never refused");
        Check(m.Test(1, 8, true, false, 350) == ObjFailMemoCore.kPass, "another object: passes");
        Check(m.Test(3, 9, true, false, 350) == ObjFailMemoCore.kPass, "another Sim: passes");
        Check(m.Test(1, 9, true, true, 350) == ObjFailMemoCore.kPassExempt, "Sim turned exempt: passes");

        // a fail during the block does not extend it; block is [from, from + Block)
        Check(Rec(m, 1, 9, false, NoPath, 13, 900) == ObjFailMemoCore.kNotRecorded, "fail while blocked: ignored");
        Check(m.Test(1, 9, true, false, 300 + Block - 1) == ObjFailMemoCore.kRefuse, "last tick of the block: refused");
        Check(m.Test(1, 9, true, false, 300 + Block) == ObjFailMemoCore.kPass && m.Count == 0 && m.BlockedCount == 0, "after the block: passes, entry gone");

        // window: fails further apart than the window do not add up
        Check(Rec(m, 1, 9, false, NoPath, 21, 5000) == ObjFailMemoCore.kCounted, "new window: first fail");
        Check(Rec(m, 1, 9, false, NoPath, 22, 5000 + Win) == ObjFailMemoCore.kCounted, "fail after the window: starts again");
        Check(Rec(m, 1, 9, false, NoPath, 23, 5000 + Win + 10) == ObjFailMemoCore.kBlocked, "two inside the new window: blocked");
        // a clock that went back (another save) drops the block
        Check(m.Test(1, 9, true, false, 100) == ObjFailMemoCore.kPass && m.BlockedCount == 0, "clock went back: dropped");
        // attempt key 0 (unknown) always counts
        Rec(m, 4, 9, false, NoPath, 0, 100);
        Check(Rec(m, 4, 9, false, NoPath, 0, 110) == ObjFailMemoCore.kBlocked, "unknown attempt keys count each time");
        // a block that ended is restarted by new fails, not refused
        Check(Rec(m, 4, 9, false, NoPath, 31, 110 + Block) == ObjFailMemoCore.kCounted && m.Test(4, 9, true, false, 110 + Block) == ObjFailMemoCore.kPass, "ended block: counts again from zero");

        // bounded table: stale windows pruned; a table of live blocks is wiped, never grows
        m.Clear();
        for (ulong s = 1; s <= ObjFailMemoCore.kMax; s++) Rec(m, s, 7, false, NoPath, 1, 100);
        Check(m.Count == ObjFailMemoCore.kMax, "table full");
        Rec(m, 9999, 7, false, NoPath, 1, 100 + Win);
        Check(m.Count == 1, "full table of stale windows pruned");
        m.Clear();
        for (ulong s = 1; s <= ObjFailMemoCore.kMax; s++) { Rec(m, s, 7, false, NoPath, 0, 100); Rec(m, s, 7, false, NoPath, 0, 101); }
        Check(m.Count == ObjFailMemoCore.kMax && m.BlockedCount == ObjFailMemoCore.kMax, "table of live blocks");
        Rec(m, 9999, 7, false, NoPath, 1, 102);
        Check(m.Count == 1 && m.BlockedCount == 0, "full table of live blocks wiped, counts kept consistent");

        if (sFails > 0) { Console.WriteLine("objfailmemo selftest: " + sFails + " FAIL"); Environment.Exit(1); }
        Console.WriteLine("objfailmemo selftest: ok");
    }
}
