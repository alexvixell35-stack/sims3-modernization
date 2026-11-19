using System;
using Ts3Fixes;

// Offline self-check of HouseboatLegMemoCore (docs/857 §2).
static class HouseboatLegMemoTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; if (sFails < 20) Console.WriteLine("FAIL " + what); } }

    static void Main()
    {
        HouseboatLegMemoCore m = new HouseboatLegMemoCore();
        HouseboatLegMemoCore.Entry e;
        const long life = 1000;
        Check(!m.Hit(1, 0, 0, 0, 17, 0, 0, 0, out e), "empty: no hit");
        m.Record(1, 0, 0, 0, 17, 0, 0, 100, life, 650);
        Check(m.Hit(1, 0, 0, 0, 17, 0, 0, 100, out e) && e.Ms == 650, "same plan at once: hit, ms kept");
        Check(m.Hit(1, 0.3f, 0, 0.3f, 17.3f, 0, 0, 500, out e), "within 0.5 m: hit");
        Check(!m.Hit(1, 0.6f, 0, 0, 17, 0, 0, 500, out e), "start moved 0.6 m: miss");
        Check(!m.Hit(1, 0, 0, 0, 17, 0, 0.6f, 500, out e), "goal moved 0.6 m (houseboat sailed): miss");
        Check(!m.Hit(2, 0, 0, 0, 17, 0, 0, 500, out e), "another router: miss");
        Check(m.Count == 1, "misses by place keep the entry");
        Check(!m.Hit(1, 0, 0, 0, 17, 0, 0, 1100, out e) && m.Count == 0, "expired: miss, dropped");
        m.Record(1, 0, 0, 0, 17, 0, 0, 100, life, 1);
        Check(!m.Hit(1, 0, 0, 0, 17, 0, 0, 99, out e) && m.Count == 0, "clock went back (another save): miss, dropped");
        m.Record(1, 0, 0, 0, 17, 0, 0, 100, life, 1);
        m.Record(1, 5, 0, 0, 20, 0, 0, 200, life, 2);
        Check(!m.Hit(1, 0, 0, 0, 17, 0, 0, 300, out e), "replaced: old plan misses");
        Check(m.Hit(1, 5, 0, 0, 20, 0, 0, 300, out e) && e.Ms == 2 && m.Count == 1, "replaced: new plan hits, one entry");
        m.Record(3, 0, 0, 0, 1, 0, 0, 0, 0, 5);
        Check(!m.Hit(3, 0, 0, 0, 1, 0, 0, 0, out e), "zero life: not recorded");
        m.Clear();
        for (ulong i = 0; i < (ulong)HouseboatLegMemoCore.kMax; i++) m.Record(i, 0, 0, 0, 1, 0, 0, i < 10 ? 0 : 5000, i < 10 ? 10 : life, 1);
        Check(m.Count == HouseboatLegMemoCore.kMax, "full table");
        m.Record(1000, 0, 0, 0, 1, 0, 0, 5000, life, 1);
        Check(m.Count == HouseboatLegMemoCore.kMax - 10 + 1, "full: expired entries dropped first");
        for (ulong i = 2000; m.Count < HouseboatLegMemoCore.kMax; i++) m.Record(i, 0, 0, 0, 1, 0, 0, 5000, life, 1);
        m.Record(3000, 0, 0, 0, 1, 0, 0, 5000, life, 1);
        Check(m.Count == 1 && m.Hit(3000, 0, 0, 0, 1, 0, 0, 5000, out e), "full of live entries: cleared, new one kept");
        Console.WriteLine(sFails == 0 ? "houseboatlegmemo selftest: ok" : "houseboatlegmemo selftest: " + sFails + " FAIL");
        Environment.Exit(sFails == 0 ? 0 : 1);
    }
}
