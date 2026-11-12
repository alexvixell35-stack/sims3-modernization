using System;
using System.Collections;
using System.Reflection;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.DreamsAndPromises;

namespace Ts3Fixes
{
    // dnpsubjectcache (issue #40; errortrapsims' hourly alarm, own switch; not save data): every wish and memory names its
    // subject through ActiveNodeBase.NodeSubject, and NodeSubject.sNodeSubjects (static) keeps one canonical copy of each
    // for the whole session. A copy caches its SimDescription in mMiniSimDescriptionCache on first use and the game only
    // empties the table in DreamsAndPromisesManager.PreWorldShutdown (PruneCache). So the description of every Sim that
    // was ever the subject of a wish stays reachable after the Sim died or was removed, with its outfits, traits and
    // relationships (census: SimDescription +315 in 45 min with only story progression running).
    // The pass clears the cached reference of a copy whose SimDescription is no longer valid (SimDescription.Dispose set
    // IsValidDescription false). The id stays, so the game's getter looks the Sim up again by id on the next use, as it
    // does after a save and reload. Live Sims and MiniSimDescriptions are left alone.
    // Counters: [2106] cached descriptions cleared, [2107] entries in the table at the last pass.
    public static partial class Fixes
    {
        static IDictionary sDscTable;
        static FieldInfo sDscCache;
        static bool sDscTried, sDscThrew;

        static void DnpSubjectCachePass()
        {
            try
            {
                if (!ShortcutOn("dnpsubjectcache")) return;
                if (!sDscTried)
                {
                    sDscTried = true;
                    Type t = typeof(ActiveNodeBase).GetNestedType("NodeSubject", BindingFlags.Public | BindingFlags.NonPublic);
                    FieldInfo tbl = t == null ? null : t.GetField("sNodeSubjects", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                    sDscCache = t == null ? null : t.GetField("mMiniSimDescriptionCache", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    sDscTable = tbl == null || sDscCache == null ? null : tbl.GetValue(null) as IDictionary;
                    AppliedLog().Add("dnpsubjectcache: " + (sDscTable != null ? "ok" : "NodeSubject fields missing (pass off)"));
                }
                if (sDscTable == null) return;
                int[] c = PatchCounters();
                int cleared = 0;
                foreach (object key in sDscTable.Keys)  // values are the same objects; no entry is added or removed here
                {
                    SimDescription sd = sDscCache.GetValue(key) as SimDescription;
                    if (sd != null && !sd.IsValidDescription) { sDscCache.SetValue(key, null); cleared++; }
                }
                c[2106] += cleared;
                c[2107] = sDscTable.Count;
            }
            catch (Exception e)
            {
                if (!sDscThrew) { sDscThrew = true; AppliedLog().Add("dnpsubjectcache: pass threw " + e.Message); }
            }
        }
    }
}
