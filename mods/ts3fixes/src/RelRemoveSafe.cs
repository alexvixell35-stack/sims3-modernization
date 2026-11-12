using System;
using System.Collections.Generic;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Socializing;

namespace Ts3Fixes
{
    // relremovesafe (tuning kRelRemoveSafe, default off until measured; docs/191 "relationship removal", docs/535).
    //
    // EA bug: Relationship.RemoveSimDescriptionRelationships(x) [verified IL idx 0-46] walks only x's own table
    // (Relationship.Get(x)) and, for each other Sim y in it, removes x from y's table with
    // sAllRelationships[y].Remove(x) (idx 27-31): a plain indexer, which throws KeyNotFoundException when y has no table.
    // It never looks for x in tables it did not walk, and it returns early, before removing x's own table, when Get(x)
    // returns null (idx 10-12). This is the first real step of SimDescription.Dispose (and of PostLoadFixUp's cleanup of
    // invalid descriptions), so a throw there skips the rest of Dispose: the description stays "valid", in its household,
    // with its Sim object, i.e. a half-removed Sim; and an entry for x left in a table x did not list goes into the save
    // pointing at a disposed description (tables that come apart, docs/535).
    //
    // The pass (switch on): the same removal, done safely: x's Sim object gets EA's RemoveReferencesToSim, x is removed
    // from the table of every Sim it lists (skipping Sims with no table), then from every other table that still holds it,
    // then x's own table is removed. For a consistent pair this is exactly EA's result; the only differences are the
    // entries EA would have left behind or thrown on. Sims that are not being removed keep every other relationship.
    // With the switch off the original runs, and the hook only counts. Counters: [2235] removals where EA would throw
    // (a listed Sim with no table), [2236] one-sided entries for x in tables x did not list (left by EA / removed by
    // the pass), [2237] removals seen, [2238] errors.
    public static partial class Fixes
    {
        static void ApplyRelRemoveSafe()
        {
            Patch(typeof(Relationship), "RemoveSimDescriptionRelationships", new Type[] { typeof(SimDescription) }, typeof(RrsHook), "RemoveHook", "RemoveOrig");
        }

        public class RrsHook
        {
            public static void RemoveHook(SimDescription x)
            {
                int[] c = PatchCounters();
                c[2237]++;
                Dictionary<SimDescription, Dictionary<SimDescription, Relationship>> all = Relationship.sAllRelationships;
                bool on = ShortcutOn("relremovesafe");
                int wouldThrow = 0, oneSided = 0;
                List<SimDescription> listed = new List<SimDescription>();
                try
                {
                    Dictionary<SimDescription, Relationship> own;
                    if (x != null && all != null && all.TryGetValue(x, out own) && own != null)
                        foreach (KeyValuePair<SimDescription, Relationship> kv in own)
                        {
                            SimDescription y = kv.Value != null ? kv.Value.GetOtherSimDescription(x) : kv.Key;
                            if (y == null || object.ReferenceEquals(y, x)) continue;
                            listed.Add(y);
                            if (!all.ContainsKey(y)) wouldThrow++;
                        }
                    if (x != null && all != null)
                        foreach (KeyValuePair<SimDescription, Dictionary<SimDescription, Relationship>> t in all)
                            if (!object.ReferenceEquals(t.Key, x) && t.Value != null && t.Value.ContainsKey(x) && !listed.Contains(t.Key)) oneSided++;
                }
                catch (Exception) { c[2238]++; }
                if (wouldThrow > 0) c[2235]++;
                c[2236] += oneSided;
                if (!on || x == null || all == null) { RemoveOrig(x); return; }
                try
                {
                    if (x.CreatedSim != null) Relationship.RemoveReferencesToSim(x.CreatedSim);
                    Dictionary<SimDescription, Relationship> t;
                    foreach (SimDescription y in listed)
                        if (all.TryGetValue(y, out t) && t != null) t.Remove(x);
                    if (oneSided > 0)
                    {
                        List<Dictionary<SimDescription, Relationship>> holders = new List<Dictionary<SimDescription, Relationship>>();
                        foreach (KeyValuePair<SimDescription, Dictionary<SimDescription, Relationship>> kv in all)
                            if (!object.ReferenceEquals(kv.Key, x) && kv.Value != null && kv.Value.ContainsKey(x)) holders.Add(kv.Value);
                        foreach (Dictionary<SimDescription, Relationship> h in holders) h.Remove(x);
                    }
                    all.Remove(x);
                }
                catch (Exception) { c[2238]++; }
            }
            public static void RemoveOrig(SimDescription x) { }
        }
    }
}
