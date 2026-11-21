using System;
using Ts3Fixes;

// Offline self-check of PoolFailMemoCore (docs/645 §2).
static class PoolFailMemoTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; if (sFails < 20) Console.WriteLine("FAIL " + what); } }

    static void Main()
    {
        PoolFailMemoCore m = new PoolFailMemoCore();
        const long hour = 1000;
        const int NoDest = PoolFailMemoCore.kNoDestination, NoPath = PoolFailMemoCore.kNoPath;

        // which results are recorded (RoutePlanResultType: 1 Succeeded, 10 NoDestinationSpecified, 16 NoPath, 17 blocked by Sim, 18 PortalLocked)
        Check(PoolFailMemoCore.Fails(NoDest) && PoolFailMemoCore.Fails(NoPath), "no destination and no path fail");
        Check(!PoolFailMemoCore.Fails(1) && !PoolFailMemoCore.Fails(17) && !PoolFailMemoCore.Fails(18), "success, Sim blocker, locked door: not recorded");
        Check(m.Record(1, 9, false, 1, 100, hour) == PoolFailMemoCore.kNotRecorded && m.Count == 0, "success never recorded");
        Check(m.Record(1, 9, false, 17, 100, hour) == PoolFailMemoCore.kNotRecorded && m.Count == 0, "transient (Sim blocker) never recorded");
        Check(m.Record(2, 9, true, NoDest, 100, hour) == PoolFailMemoCore.kExempt && m.Count == 0, "exempt Sim never recorded");
        Check(m.Record(0, 9, false, NoDest, 100, hour) == PoolFailMemoCore.kNotRecorded, "no Sim id: not recorded");
        Check(m.Record(1, 9, false, NoDest, 100, hour) == PoolFailMemoCore.kRecorded && m.Count == 1, "NPC no destination: recorded");

        // refusal: autonomous only, same pair only
        Check(m.Test(1, 9, true, false, 150) == PoolFailMemoCore.kRefuse, "autonomous pick of the pair: refused");
        Check(m.Test(1, 9, false, false, 150) == PoolFailMemoCore.kPass, "user-directed pick: never refused");
        Check(m.Test(1, 8, true, false, 150) == PoolFailMemoCore.kPass, "another pool: passes");
        Check(m.Test(3, 9, true, false, 150) == PoolFailMemoCore.kPass, "another Sim: passes");
        Check(m.Test(1, 9, true, true, 150) == PoolFailMemoCore.kPassExempt, "Sim turned exempt (moved in, selected): passes");

        // life: [From, Until), refusals do not extend it, a clock that went back drops it
        Check(m.Test(1, 9, true, false, 100 + hour - 1) == PoolFailMemoCore.kRefuse, "last tick of the hour: refused");
        Check(m.Test(1, 9, true, false, 100 + hour) == PoolFailMemoCore.kPass && m.Count == 0, "after the hour: passes, entry gone");
        m.Record(1, 9, false, NoPath, 500, hour);
        Check(m.Test(1, 9, true, false, 400) == PoolFailMemoCore.kPass && m.Count == 0, "clock went back (other save): dropped");
        m.Record(1, 9, false, NoPath, 500, hour);
        m.Record(1, 9, false, NoPath, 900, hour);
        Check(m.Test(1, 9, true, false, 1800) == PoolFailMemoCore.kRefuse, "a new failure renews the entry");
        Check(m.Record(1, 9, false, NoPath, 900, 0) == PoolFailMemoCore.kNotRecorded, "zero life: not recorded");

        // bounded table: expired entries go first, then a full clear
        m.Clear();
        for (ulong i = 1; i <= (ulong)PoolFailMemoCore.kMax; i++) m.Record(i, 9, false, NoPath, i <= 10 ? 0 : 5000, hour);
        Check(m.Count == PoolFailMemoCore.kMax, "full table");
        m.Record(1000, 9, false, NoPath, 5000, hour);
        Check(m.Count == PoolFailMemoCore.kMax - 10 + 1, "full: expired entries dropped first");
        for (ulong i = 2000; m.Count < PoolFailMemoCore.kMax; i++) m.Record(i, 9, false, NoPath, 5000, hour);
        m.Record(3000, 9, false, NoPath, 5000, hour);
        Check(m.Count == 1 && m.Live(3000, 9, 5000), "full of live entries: cleared, new one kept");

        Console.WriteLine(sFails == 0 ? "poolfailmemo selftest: ok" : "poolfailmemo selftest: " + sFails + " FAIL");
        Environment.Exit(sFails == 0 ? 0 : 1);
    }
}
