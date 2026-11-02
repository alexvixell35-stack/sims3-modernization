using System;
using Ts3Fixes;

// Offline self-check of SaveCleanupCore (docs/14 §8).
static class SaveCleanupTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; Console.WriteLine("FAIL " + what); } }

    static void Main()
    {
        // Sims3.SimIFace.CAS.OutfitCategories: Naked 1, Everyday 2, Special 131072, Supernatural 524288 must stay.
        foreach (uint keep in new uint[] { 1u, 2u, 131072u, 524288u })
            Check(!SaveCleanupCore.DeadOutfitRemovable(keep), "kept category " + keep);
        uint seen = 0;
        foreach (uint c in SaveCleanupCore.kDeadRemovableOutfits)
        {
            Check(c != 0 && (c & (c - 1)) == 0, "single flag " + c);         // one category each, no masks
            Check((c & 1048575u) == c, "inside CategoryMask " + c);
            Check((seen & c) == 0, "listed once " + c);
            seen |= c;
            Check(SaveCleanupCore.DeadOutfitRemovable(c), "removable " + c);
        }
        Check(SaveCleanupCore.kDeadRemovableOutfits.Length == 13, "13 categories");

        Check(SaveCleanupCore.QuietFor(false, 56, 0, 56), "talked 56 days ago");
        Check(!SaveCleanupCore.QuietFor(false, 55.9, 999, 56), "talked recently, old state");
        Check(SaveCleanupCore.QuietFor(true, 0, 60, 56), "never talked, old state");
        Check(!SaveCleanupCore.QuietFor(true, 999, 10, 56), "never talked, new state");
        Check(!SaveCleanupCore.QuietFor(false, -5, 999, 56), "clock went back");
        Check(!SaveCleanupCore.QuietFor(false, 999, 999, 0), "rule off at 0 days");

        // played-household sidecar key (native played.cpp PlKeyOk: 1-100 of [A-Za-z0-9._-], no leading '.')
        Check(SaveCleanupCore.PlayedKey("MultiGen-Test-BP.sims3", "BP") == "MultiGen-Test-BP.sims3", "plain key");
        Check(SaveCleanupCore.PlayedKey("C:\\Users\\<user>\\Saves\\My Town!.sims3", "BP") == "My_Town_.sims3", "folders cut, bad chars");
        Check(SaveCleanupCore.PlayedKey(null, "SunsetValley") == "unsaved_SunsetValley", "no save yet");
        Check(SaveCleanupCore.PlayedKey("  ", null) == "unsaved_world", "blank save, no world");
        Check(SaveCleanupCore.PlayedKey("..\\..\\evil", "BP") == "evil", "no path escape");
        Check(SaveCleanupCore.PlayedKey(".hidden", "BP") == "_hidden", "no leading dot");
        Check(SaveCleanupCore.PlayedKey(new string('a', 150), "BP").Length == 100, "capped at 100");
        foreach (string id in new string[] { "a/b\\c", "x y z", "\u00e9t\u00e9", ".", "a:b" })
        {
            string k = SaveCleanupCore.PlayedKey(id, "W");
            bool ok = k.Length >= 1 && k.Length <= 100 && k[0] != '.';
            foreach (char c in k) ok &= (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '.' || c == '_' || c == '-';
            Check(ok, "valid key for " + id + " -> " + k);
        }

        Console.WriteLine(sFails == 0 ? "savecleanup selftest: ok" : "savecleanup selftest: " + sFails + " failures");
        Environment.Exit(sFails == 0 ? 0 : 1);
    }
}
