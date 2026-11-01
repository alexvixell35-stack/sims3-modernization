using System;
using Ts3Fixes;

// Offline self-check of UniformBackupCore (issue #101): every input combination of the decision against its rule written
// out here a second time, plus the named cases from the issue.
static class UniformBackupTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; if (sFails < 20) Console.WriteLine("FAIL " + what); } }
    static bool B(int bits, int i) { return ((bits >> i) & 1) != 0; }

    static void Main()
    {
        for (int m = 0; m < 16; m++)
        {
            bool had = B(m, 0), same = B(m, 1), worn = B(m, 2), replaced = B(m, 3);
            Check(UniformBackupCore.ClearAfter(had, same, worn, replaced) == (had && same && !worn && replaced), "clear " + m);
        }
        Check(UniformBackupCore.ClearAfter(true, true, false, true), "re-uniform in Everyday: EA uncached the backup, clear it");
        Check(!UniformBackupCore.ClearAfter(true, false, false, true), "re-uniform in Career: EA stored a new backup, keep it");
        Check(!UniformBackupCore.ClearAfter(true, true, true, true), "the Sim still wears the backup: EA did not uncache it");
        Check(!UniformBackupCore.ClearAfter(true, true, false, false), "early return (no change): EA did not uncache it");
        Check(!UniformBackupCore.ClearAfter(false, true, false, true), "no backup");
        Console.WriteLine(sFails == 0 ? "uniformbackup selftest: ok" : "uniformbackup selftest: " + sFails + " FAIL");
        Environment.Exit(sFails == 0 ? 0 : 1);
    }
}
