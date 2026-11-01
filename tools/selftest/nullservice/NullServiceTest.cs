using System;
using System.Collections.Generic;
using Ts3Fixes;

// Offline self-check of NullServiceCore (docs/504): every placement of nulls in short lists against the rule written out
// here a second time, plus the enumerator case the getter hook relies on.
static class NullServiceTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; if (sFails < 20) Console.WriteLine("FAIL " + what); } }

    static void Main()
    {
        for (int len = 0; len <= 6; len++)
            for (int m = 0; m < (1 << len); m++)
            {
                List<string> l = new List<string>(), want = new List<string>();
                for (int i = 0; i < len; i++)
                {
                    bool isNull = ((m >> i) & 1) != 0;
                    l.Add(isNull ? null : "s" + i);
                    if (!isNull) want.Add("s" + i);
                }
                int n = NullServiceCore.DropNulls(l);
                Check(n == len - want.Count, "count " + len + "/" + m);
                Check(l.Count == want.Count, "length " + len + "/" + m);
                for (int i = 0; i < want.Count && i < l.Count; i++) Check(l[i] == want[i], "order " + len + "/" + m + " at " + i);
            }
        Check(NullServiceCore.DropNulls<string>(null) == 0, "null list");

        // no null: the list is not changed, so an enumeration that reads the getter again inside its loop keeps going
        List<string> clean = new List<string>(new string[] { "maid", "mail", "repair" });
        int seen = 0;
        try { foreach (string s in clean) { NullServiceCore.DropNulls(clean); seen++; } }
        catch (InvalidOperationException) { Check(false, "enumerator broken by a drop with no null"); }
        Check(seen == 3, "enumeration over a clean list");

        Console.WriteLine(sFails == 0 ? "nullservice selftest: ok" : "nullservice selftest: " + sFails + " FAIL");
        Environment.Exit(sFails == 0 ? 0 : 1);
    }
}
