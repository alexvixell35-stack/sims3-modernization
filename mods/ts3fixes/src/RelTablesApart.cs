using System;
using System.Collections.Generic;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Socializing;

namespace Ts3Fixes
{
    // reltablesapart (docs/535): Relationship.sAllRelationships keeps one table per Sim, and EA always puts one shared
    // Relationship into both Sims' tables. When the two tables have come apart (x's table has no entry for y, but y's
    // table holds one for x), EA's private Relationship.GetInternal(x, y, createIfNone: true, ...) makes a new
    // Relationship, adds it to x's table, and throws ArgumentException "An element with the same key already exists" on
    // y's table [verified in the decompiled game code; seen live 2026-10-22, NRaas MasterController's Reset All ->
    // Career.OnOwnerBecameSelectable -> SetCoworkersAndBoss -> CheckBoss -> Relationship.Get, issue #166 build 14]. x's
    // table is then left with a second relationship object for the pair. Fix: in that state the hook puts y's
    // relationship (its liking and history) into x's table and returns it, so the tables agree again and nothing is
    // created twice. Every other case runs the original. Counter [2065] pairs rejoined (registry fixes-2065). Default off.
    public static partial class Fixes
    {
        public class RelTablesApartHook
        {
            public static Relationship GetHook(SimDescription x, SimDescription y, bool createIfNone, bool checkForAttraction)
            {
                if (createIfNone && x != null && y != null && ShortcutOn("reltablesapart"))
                {
                    Dictionary<SimDescription, Dictionary<SimDescription, Relationship>> all = Relationship.sAllRelationships;
                    Dictionary<SimDescription, Relationship> xs, ys;
                    Relationship r;
                    bool hasX = all.TryGetValue(x, out xs);
                    if ((!hasX || !xs.ContainsKey(y)) && all.TryGetValue(y, out ys) && ys.TryGetValue(x, out r) && r != null)
                    {
                        if (!hasX) { xs = new Dictionary<SimDescription, Relationship>(); all.Add(x, xs); }
                        xs[y] = r;
                        PatchCounters()[2065]++;
                        return r;
                    }
                }
                return GetOrig(x, y, createIfNone, checkForAttraction);
            }
            public static Relationship GetOrig(SimDescription x, SimDescription y, bool createIfNone, bool checkForAttraction) { return null; } // receives Relationship.GetInternal's original body
        }

        // Harness check (repro rel-tables-apart-check): the first pair whose two tables share one Relationship r has r taken
        // out of x's table (the state the reset left), then Relationship.Get(x, y, true) runs once. "expect broken" (switch
        // off) wants EA's ArgumentException; "expect fixed" (switch on) wants r back, in both tables. x's table gets r back
        // in a finally either way (EA's failed call leaves a second relationship there).
        public static object RelTablesApartCheck(string what)
        {
            if (what != "expect broken" && what != "expect fixed") return "reltablesapart <expect broken|expect fixed>";
            SimDescription x = null, y = null;
            Relationship r = null;
            foreach (KeyValuePair<SimDescription, Dictionary<SimDescription, Relationship>> a in Relationship.sAllRelationships)
            {
                if (a.Key == null || a.Value == null || !a.Key.IsValidDescription) continue;
                foreach (KeyValuePair<SimDescription, Relationship> b in a.Value)
                {
                    Dictionary<SimDescription, Relationship> back;
                    Relationship rb;
                    if (b.Key != null && b.Value != null && b.Key.IsValidDescription && b.Key != a.Key && Relationship.sAllRelationships.TryGetValue(b.Key, out back)
                        && back.TryGetValue(a.Key, out rb) && rb == b.Value) { x = a.Key; y = b.Key; r = b.Value; break; }
                }
                if (r != null) break;
            }
            if (r == null) throw new InvalidOperationException("reltablesapart check: no pair with one shared relationship");
            Dictionary<SimDescription, Relationship> xs = Relationship.sAllRelationships[x];
            string threw = "nothing";
            Relationship got = null;
            bool same, inBoth;
            xs.Remove(y);
            try
            {
                try { got = Relationship.Get(x, y, true); } catch (Exception e) { threw = e.GetType().Name; }
                same = got == r;
                inBoth = xs.ContainsKey(y) && xs[y] == r && Relationship.sAllRelationships[y][x] == r;
            }
            finally { xs[y] = r; }
            bool on = ShortcutOn("reltablesapart");
            string line = "threw " + threw + ", returned " + (got == null ? "null" : same ? "the shared relationship" : "a new relationship") + ", in both tables " + inBoth + " (switch " + on + ")";
            bool ok = what == "expect broken" ? !on && threw == "ArgumentException" : on && threw == "nothing" && same && inBoth;
            if (!ok) throw new InvalidOperationException("reltablesapart " + what + ": wrong outcome; " + line);
            return "as expected; " + line;
        }
    }
}
