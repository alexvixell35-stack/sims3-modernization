using System;
using System.Collections.Generic;
using Sims3.Gameplay.CAS;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Sim name cache (docs/54 §5 item 2, PLAN fix round item 30), switch "namecache" (tuning kSimNameCache, default off).
    // SimDescriptionCore.get_FirstName / get_LastName pass the stored name through the localized-string lookup on every
    // read (HasLocalizationString, then LocalizeString: a native FNV-64 of the key, the table walk and a new string), and
    // get_FullName formats the two with "Ui/Global:FullName"; 22 of the lookup's 51 managed samples (~0.14% managed) are
    // these getters, mostly Sim.get_Name from the HUD.
    // The result of each getter depends only on the stored strings (mFirstName, mLastName), the robot occult check for the
    // full name, and the loaded string tables. So answers are cached by the stored strings themselves, not by Sim: a
    // rename (property setter, direct field write in SimDescription's constructor or CAS, a mod using reflection) changes
    // the stored string and therefore the key, so a stale name can never come back for it, and no SimDescription is kept
    // alive by the cache. The robot check is made on every FullName read, as the game does. The whole cache is cleared
    // when the string tables could have changed: at every world load (new world or travel loads its own STBLs) and when
    // StringTable.GetLocale() changes (checked at most every kLocaleCheckMs). Size-capped (cleared when full).
    // Counters: [78] answers from the cache, [79] answers computed by the game's getter and stored (FullName's own
    // misses also count the inner FirstName/LastName reads).
    public static partial class Fixes
    {
        static bool sNameCacheReady;
        static bool sNameCacheOn;
        static int sNameSwitchStamp, sLocaleStamp;
        static string sLocale;
        const int kNameSwitchMs = 250, kLocaleCheckMs = 1000, kMaxNames = 8192;
        static readonly Dictionary<string, string> sNames = new Dictionary<string, string>();
        static readonly Dictionary<NamePair, string> sFullNames = new Dictionary<NamePair, string>(new NamePairComparer());

        static void ApplyNameCache()
        {
            bool first = Patch(typeof(SimDescriptionCore), "get_FirstName", null, typeof(NameCacheHook), "FirstHook", "FirstOrig");
            bool last = Patch(typeof(SimDescriptionCore), "get_LastName", null, typeof(NameCacheHook), "LastHook", "LastOrig");
            bool full = Patch(typeof(SimDescriptionCore), "get_FullName", null, typeof(NameCacheHook), "FullHook", "FullOrig");
            sNameCacheReady = first || last || full; // each hook stands alone
        }

        // Called at every world load finished (Instantiator).
        public static void ClearNameCache()
        {
            sNames.Clear();
            sFullNames.Clear();
            sLocale = null;
        }

        // Switch read at most every kNameSwitchMs (the getters run thousands of times a second; ShortcutOn hashes the name).
        static bool NameCacheOn()
        {
            int now = System.Environment.TickCount;
            if (now - sNameSwitchStamp > kNameSwitchMs || now < sNameSwitchStamp)
            {
                sNameSwitchStamp = now;
                sNameCacheOn = sNameCacheReady && ShortcutOn("namecache");
            }
            if (!sNameCacheOn) return false;
            if (sLocale == null || now - sLocaleStamp > kLocaleCheckMs || now < sLocaleStamp)
            {
                sLocaleStamp = now;
                string loc = StringTable.GetLocale() ?? "";
                if (loc != sLocale) { sNames.Clear(); sFullNames.Clear(); sLocale = loc; }
            }
            return true;
        }

        struct NamePair { public string First, Last; }
        class NamePairComparer : IEqualityComparer<NamePair> // explicit: the default one boxes structs in this Mono
        {
            public bool Equals(NamePair a, NamePair b) { return string.Equals(a.First, b.First) && string.Equals(a.Last, b.Last); }
            public int GetHashCode(NamePair k) { return (k.First == null ? 0 : k.First.GetHashCode()) ^ (k.Last == null ? 0 : k.Last.GetHashCode() * 31); }
        }

        public class NameCacheHook : SimDescriptionCore
        {
            public string FirstHook()
            {
                string raw = mFirstName;
                if (raw == null || !NameCacheOn()) return FirstOrig();
                string v;
                if (CachedName(raw, out v)) return v;
                return StoreName(raw, FirstOrig());
            }
            public string FirstOrig() { return null; } // receives SimDescriptionCore.get_FirstName's original header

            public string LastHook()
            {
                string raw = mLastName;
                if (raw == null || !NameCacheOn()) return LastOrig();
                string v;
                if (CachedName(raw, out v)) return v;
                return StoreName(raw, LastOrig());
            }
            public string LastOrig() { return null; } // receives SimDescriptionCore.get_LastName's original header

            // get_FirstName and get_LastName compute the same function of the stored string, so they share one table.
            static bool CachedName(string raw, out string v)
            {
                if (sNames.TryGetValue(raw, out v)) { PatchCounters()[78]++; return true; }
                PatchCounters()[79]++;
                return false;
            }
            static string StoreName(string raw, string v)
            {
                if (sNames.Count >= kMaxNames) sNames.Clear();
                sNames[raw] = v;
                return v;
            }

            public string FullHook()
            {
                if (!NameCacheOn()) return FullOrig();
                if (mSupernaturalData != null && mSupernaturalData.OccultType == Sims3.UI.Hud.OccultTypes.Robot) return FullOrig(); // FirstName alone, cached there
                NamePair k;
                k.First = FirstName; // our hooks: cached
                k.Last = LastName;
                int[] c = PatchCounters();
                string v;
                if (sFullNames.TryGetValue(k, out v)) { c[78]++; return v; }
                c[79]++;
                v = FullOrig();
                if (sFullNames.Count >= kMaxNames) sFullNames.Clear();
                sFullNames[k] = v;
                return v;
            }
            public string FullOrig() { return null; } // receives SimDescriptionCore.get_FullName's original header
        }

        // Self-check, run from the agent (reflect_call Ts3Fixes.Fixes.NameCacheCheck): every Sim's FirstName, LastName and
        // FullName with the cache off, then twice with it on (fill, then hits), must be the same strings. The switch is
        // restored afterwards. Returns a summary; "mismatches 0" is the pass.
        public static string NameCacheCheck()
        {
            Dictionary<string, bool> sw = AppDomain.CurrentDomain.GetData("ts3fixes.switches") as Dictionary<string, bool>;
            if (sw == null) { ShortcutOn("namecache"); sw = sSwitches; }
            bool had, prev;
            had = sw.TryGetValue("namecache", out prev);
            int[] c = PatchCounters();
            int sims = 0, bad = 0, hits0 = c[78], miss0 = c[79];
            string firstBad = null;
            try
            {
                foreach (Sims3.Gameplay.Actors.Sim sim in Sims3.Gameplay.Queries.GetObjects<Sims3.Gameplay.Actors.Sim>())
                {
                    SimDescription d = sim != null ? sim.SimDescription : null;
                    if (d == null) continue;
                    sims++;
                    SetNameSwitch(sw, false);
                    string f0 = d.FirstName, l0 = d.LastName, n0 = d.FullName;
                    for (int pass = 0; pass < 2; pass++)
                    {
                        SetNameSwitch(sw, true);
                        string f = d.FirstName, l = d.LastName, n = d.FullName;
                        if (f != f0 || l != l0 || n != n0)
                        {
                            bad++;
                            if (firstBad == null) firstBad = "'" + n0 + "' vs '" + n + "'";
                        }
                    }
                }
            }
            finally
            {
                if (had) sw["namecache"] = prev; else sw.Remove("namecache");
                SwitchesChanged();
                sNameSwitchStamp = System.Environment.TickCount - kNameSwitchMs - 1; // re-read at the next getter
            }
            return "ready " + sNameCacheReady + ", sims " + sims + ", mismatches " + bad + (firstBad != null ? " (first " + firstBad + ")" : "")
                + ", hits " + (c[78] - hits0) + ", misses " + (c[79] - miss0) + ", cached names " + sNames.Count + ", full names " + sFullNames.Count;
        }

        static void SetNameSwitch(Dictionary<string, bool> sw, bool on)
        {
            sw["namecache"] = on;
            SwitchesChanged();
            sNameSwitchStamp = System.Environment.TickCount - kNameSwitchMs - 1; // force NameCacheOn to re-read now
        }
    }
}
