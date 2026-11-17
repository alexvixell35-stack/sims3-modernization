using System;
using System.IO;
using Ts3Fixes;

// tools/selftest/switchcache/run.ts: the game-independent ShortcutOn cache (SwitchCacheCore.cs, docs/605 §2).
static class SwitchCacheTest
{
    static int sFail;
    static void Check(bool ok, string what) { if (!ok) { sFail++; Console.WriteLine("FAIL " + what); } }

    static int Main(string[] args)
    {
        SwitchCache t = new SwitchCache(7, 3);
        Check(t.Gen == 7 && t.Count == 3, "gen and count kept");
        Check(t.Get(null) == -1 && t.Get("") == -1, "null / empty never stored");
        t.Put(null, SwitchCache.On); t.Put("", SwitchCache.On);
        Check(t.Used == 0, "null / empty not stored");
        string a = "gohome";
        Check(t.Get(a) == -1, "miss before put");
        t.Put(a, SwitchCache.On);
        Check(t.Get(a) == 1, "hit after put (on)");
        string b = "broadcast";
        t.Put(b, SwitchCache.Off);
        Check(t.Get(b) == 0, "hit after put (off)");
        string same = new string("gohome".ToCharArray());  // same text, another object (a name built at run time)
        Check(t.Get(same) == -1, "same text, other object: slow path");
        t.Put(a, SwitchCache.Off);
        Check(t.Get(a) == 1 && t.Used == 2, "second put of a stored name keeps the first answer");
        string live = "detailprio";
        t.Put(live, SwitchCache.Live);
        Check(t.Get(live) == SwitchCache.Live, "live entry");
        // cap: never more than half full, so a probe run always ends at an empty slot
        SwitchCache c = new SwitchCache(0, 0);
        for (int i = 0; i < SwitchCache.Size; i++) c.Put("n" + i, i % 3);
        Check(c.Used == SwitchCache.Size / 2, "cap at half full: " + c.Used);
        Check(c.Get("absent-name") == -1, "miss in a full table ends");
        // every real switch name
        string[] names = File.ReadAllLines(args[0]);
        int n = 0;
        SwitchCache r = new SwitchCache(1, 0);
        foreach (string s0 in names)
        {
            if (s0.Length == 0) continue;
            string s = string.Intern(s0);
            r.Put(s, s.Length % 3);
            n++;
        }
        int bad = 0;
        foreach (string s0 in names)
        {
            if (s0.Length == 0) continue;
            string s = string.Intern(s0);
            if (r.Get(s) != s.Length % 3) bad++;
        }
        Check(bad == 0, "real names: " + bad + " wrong answers");
        Check(r.Used == n || n > SwitchCache.Size / 2, "real names stored " + r.Used + " of " + n);
        int worst = r.MaxProbe();
        Check(worst <= 12, "longest probe " + worst + " (hash spread)");
        Check(r.Keys().Length == n, "keys listed");
        Console.WriteLine("switchcache: " + n + " real names, longest probe " + worst + (sFail == 0 ? ", PASS" : ", " + sFail + " FAIL"));
        return sFail == 0 ? 0 : 1;
    }
}
