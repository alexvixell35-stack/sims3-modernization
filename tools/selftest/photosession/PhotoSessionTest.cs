using System;
using Ts3Fixes;

// Offline self-check of PhotoSessionCore.Drop (docs/508): which expected Sims a greeting-card photo session drops.
static class PhotoSessionTest
{
    static int sFails;
    static string Show(bool[] d) { string s = ""; foreach (bool b in d) s += b ? "x" : "."; return s; }
    static void Case(string what, bool[] active, bool[] young, bool keepOne, string want)
    {
        string got = Show(PhotoSessionCore.Drop(active, young, keepOne));
        if (got != want) { sFails++; Console.WriteLine("FAIL " + what + ": got " + got + ", want " + want); }
    }
    static bool[] B(string s) { bool[] r = new bool[s.Length]; for (int i = 0; i < s.Length; i++) r[i] = s[i] == '1'; return r; }

    static int Main()
    {
        // active, young, keepOne -> drop mask (x = dropped)
        Case("a phantom next to an active Sim goes", B("10"), B("00"), false, ".x");
        Case("all phantoms go when the list may empty", B("00"), B("00"), false, "xx");
        Case("all phantoms stay when one must stay", B("00"), B("00"), true, "..");
        Case("a baby stays with its active adult", B("10"), B("01"), false, "..");
        Case("a baby goes with its phantom adult", B("00"), B("01"), false, "xx");
        Case("an active toddler stays, the phantom adult goes", B("01"), B("01"), false, "x.");
        Case("an inactive toddler stays with an active adult", B("01"), B("10"), false, "..");
        Case("a lone inactive toddler goes when the list may empty", B("0"), B("1"), false, "x");
        Case("every Sim active: nothing goes", B("111"), B("010"), false, "...");
        Case("empty list", B(""), B(""), false, "");
        Console.WriteLine(sFails == 0 ? "photosession selftest: PASS" : "photosession selftest: " + sFails + " FAIL");
        return sFails == 0 ? 0 : 1;
    }
}
