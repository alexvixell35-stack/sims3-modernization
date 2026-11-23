using System;
using System.Collections.Generic;

namespace Ts3Fixes
{
    // Game-independent part of deadrefsweep (DeadRefs.cs, issue #91): dropping gone entries from the holders' lists,
    // arrays and dictionaries, and the guard that refuses a pass that looks like a load-time mistake (every description
    // read from a save is invalid until SimDescription.Fixup runs). Self-check: tools/selftest/deadrefs.
    public static class DeadRefsCore
    {
        // Entries for which gone() is true, removed in place; returns how many.
        public static int DropAll<T>(List<T> list, Predicate<T> gone)
        {
            if (list == null) return 0;
            return list.RemoveAll(gone);
        }

        // A copy without the gone entries, or null when nothing is gone (the caller keeps the old array).
        public static T[] Without<T>(T[] items, Predicate<T> gone, out int removed)
        {
            removed = 0;
            if (items == null) return null;
            List<T> keep = new List<T>(items.Length);
            foreach (T t in items) { if (gone(t)) removed++; else keep.Add(t); }
            return removed == 0 ? null : keep.ToArray();
        }

        // Keys for which gone() is true, removed; returns how many.
        public static int DropKeys<K, V>(Dictionary<K, V> d, Predicate<K> gone)
        {
            if (d == null) return 0;
            List<K> drop = new List<K>();
            foreach (K k in d.Keys) if (gone(k)) drop.Add(k);
            foreach (K k in drop) d.Remove(k);
            return drop.Count;
        }

        // True when a pass should not act: more than half of a large holder gone is what a sweep run before
        // SimDescription.Fixup sees (every household member invalid), not real leftovers.
        public static bool LooksLikeLoad(int gone, int total)
        {
            return total >= 16 && gone * 2 > total;
        }

        // Repro conversation-gone-check, one seeded conversation (host + one destroyed member). "pass" / "fail" / "na".
        // left (expectLeft, switches off): the gone member still listed = pass; gone 0 with the host gone from it = na (it left
        // before the check, EA's exit or the #166 throw the queue reports); gone 0 with the host still there = fail.
        // cleaned (switches on): no gone member and the host still listed (members 1) = pass; an empty list = pass only with this
        // conversation's own phase-15 removal (removedHere > 0: the host left afterwards through EA's exit); empty without it and
        // the host left = na (EA's exit ran before the phase reached it); a gone member left while the host is still there = fail;
        // a gone member left after a removal = fail; the host left with the gone member still listed = na. Switches in the wrong
        // state = fail.
        public static string ConvSampleVerdict(bool expectLeft, bool on, int gone, int members, int removedHere, bool hostLeft)
        {
            if (expectLeft)
            {
                if (on) return "fail";
                if (gone >= 1) return "pass";
                return hostLeft ? "na" : "fail";
            }
            if (!on) return "fail";
            if (gone == 0)
            {
                if (members == 1 && !hostLeft) return "pass";
                if (members == 0) return removedHere > 0 ? "pass" : (hostLeft ? "na" : "fail");
                return hostLeft ? "na" : "fail";
            }
            if (removedHere > 0) return "fail";
            return hostLeft ? "na" : "fail";
        }

        // Repro "seed clean" (docs/330 §8.3): runs phase 15's step right after the seed only when the sweep is on and the pass's
        // own unsafe-state check passes; returns the reason it does not run, or null.
        public static string ConvSeedCleanGate(bool sweepOn, string unsafeWhy)
        {
            if (!sweepOn) return "deadrefsweep off";
            return unsafeWhy;
        }

        // Arm verdict over the samples: any fail = "fail"; fewer than 3 conclusive (pass) samples = "inconclusive"; else "pass".
        public static string ConvArmVerdict(int pass, int fail, int na)
        {
            if (fail > 0) return "fail";
            return pass >= 3 ? "pass" : "inconclusive";
        }
    }
}
