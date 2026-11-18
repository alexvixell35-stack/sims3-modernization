using System;
using Ts3Fixes;

// Offline self-check of EnvListExactCore.Why (docs/274 §11.8): the envlistmemoexact stand-down rule.
static class EnvListExactTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; Console.WriteLine("FAIL " + what); } }

    static void Main()
    {
        string[] none = { }, conf = { "OtherEnvMod" };
        string[] plain = { "mscorlib", "ScriptCore", "Sims3GameplaySystems", "ts3fixes", "NRaasOverwatch" };
        string[] withConf = { "mscorlib", "Sims3GameplaySystems", "OtherEnvMod" };
        string[] eaOurs = { "Sims3GameplaySystems", "ts3fixes" };

        Check(EnvListExactCore.Why(conf, plain, null, null, eaOurs) == null, "vanilla + other mods, EA and our caches: stays on");
        Check(EnvListExactCore.Why(conf, null, null, null, null) == null, "nothing checked this call: stays on");
        Check(EnvListExactCore.Why(none, withConf, null, null, null) == null, "empty conflict list: a mod name alone does not stand down");
        string w = EnvListExactCore.Why(conf, withConf, null, null, null);
        Check(w != null && w.StartsWith("OtherEnvMod "), "known conflicting assembly loaded: stands down, names it (" + w + ")");
        w = EnvListExactCore.Why(conf, plain, "ModA", null, null);
        Check(w != null && w.StartsWith("ModA ") && w.Contains("before"), "header swapped before ours: stands down (" + w + ")");
        w = EnvListExactCore.Why(conf, plain, null, "ModB", null);
        Check(w != null && w.StartsWith("ModB ") && w.Contains("after"), "header swapped after ours: stands down (" + w + ")");
        w = EnvListExactCore.Why(conf, null, null, null, new string[] { "Sims3GameplaySystems", "ModC" });
        Check(w != null && w.StartsWith("ModC "), "foreign cache object in a lot's table: stands down (" + w + ")");
        Check(EnvListExactCore.Why(conf, null, null, null, none) == null, "empty cache table: stays on");
        w = EnvListExactCore.Why(conf, withConf, "ModA", "ModB", new string[] { "ModC" });
        Check(w != null && w.StartsWith("OtherEnvMod "), "several reasons: the loaded mod is named first");

        Console.WriteLine(sFails == 0 ? "envlistexact selftest: ok" : "envlistexact selftest: " + sFails + " FAIL");
        Environment.Exit(sFails == 0 ? 0 : 1);
    }
}
