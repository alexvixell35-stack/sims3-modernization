using System;
using System.Collections.Generic;
using Ts3Fixes;

// Offline self-check of OverwatchSpreadCore (docs/145): who runs the nightly cleanups, the upstream marker, slots, slices.
static class OverwatchSpreadTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; Console.WriteLine("FAIL " + what); } }

    // Stand-ins for NRaas.Overwatch with and without the upstream marker.
    class OverwatchWithMarker { public static readonly int StaggeredCleanupVersion = 1; }
    class OverwatchWithoutMarker { public static readonly int kSomethingElse = 1; }
    class OverwatchWrongMarker { public static readonly string StaggeredCleanupVersion = "1"; }
    class OverwatchPrivateMarker { static readonly int StaggeredCleanupVersion = 1; }

    sealed class Item { public int Id; public bool Broken; public int Checks; }

    // Model of our inventory pass with the budget counted in items: a 700-item inventory in 3 stacks plus small ones, items
    // checked last to first, broken ones taken out, stacks left empty removed at the inventory's end. Without changes the
    // result equals the one-shot check; with the game adding and taking items while we sleep, no item is checked twice,
    // an item taken away is never checked after, and items added after the turn are left for the next night.
    static void GiantInventory(bool gameChangesMidSplit)
    {
        List<List<List<Item>>> inventories = new List<List<List<Item>>>(), reference = new List<List<List<Item>>>();
        int[][] shapes = { new int[] { 400, 250, 50 }, new int[] { 3 }, new int[] { 0, 2 } };
        int id = 0;
        foreach (int[] shape in shapes)
        {
            List<List<Item>> inv = new List<List<Item>>(), copy = new List<List<Item>>();
            foreach (int size in shape)
            {
                List<Item> stack = new List<Item>(), refStack = new List<Item>();
                for (int i = 0; i < size; i++)
                {
                    Item it = new Item(); it.Id = id++; it.Broken = it.Id % 5 == 0 || (size == 50); stack.Add(it);
                    Item r = new Item(); r.Id = it.Id; r.Broken = it.Broken; refStack.Add(r);
                }
                inv.Add(stack);
                copy.Add(refStack);
            }
            inventories.Add(inv);
            reference.Add(copy);
        }
        foreach (List<List<Item>> inv in reference) // one-shot reference
        {
            foreach (List<Item> s in inv) for (int i = s.Count - 1; i >= 0; i--) if (s[i].Broken) s.RemoveAt(i);
            inv.RemoveAll(delegate(List<Item> s) { return s.Count == 0; });
        }
        List<Item> taken = new List<Item>(), added = new List<Item>();
        DailyPairPass<List<List<Item>>, SpreadUnit> pass = new DailyPairPass<List<List<Item>>, SpreadUnit>();
        pass.Begin(inventories, delegate(List<List<Item>> inv)
        {
            List<SpreadUnit> u = new List<SpreadUnit>();
            List<List<Item>> stacks = new List<List<Item>>(inv);
            foreach (List<Item> stack in stacks)
            {
                List<Item> s = stack;
                EaSpreadCore.AddItemUnits<Item>(u, delegate { return s.Count; }, delegate(int i) { return s[i]; },
                    delegate(int i) { Check(!taken.Contains(s[i]), "a taken item is never checked"); s[i].Checks++; if (s[i].Broken) s.RemoveAt(i); },
                    delegate { return false; });
            }
            u.Add(delegate { foreach (List<Item> s in stacks) if (s.Count == 0) inv.Remove(s); });
            return u;
        }, delegate(List<List<Item>> inv, SpreadUnit unit) { unit(); return true; }, delegate { });
        int slices = 0, units;
        bool done = false;
        while (!done)
        {
            done = pass.Slice(OverwatchSpreadCore.kMaxItemsPerSlice, 2000, delegate { return 0; }, out units);
            Check(units <= OverwatchSpreadCore.kMaxItemsPerSlice, "slice within the item budget");
            if (++slices == 3 && gameChangesMidSplit)
            {
                List<Item> big = inventories[0][0];
                for (int i = 0; i < 40; i += 4) { taken.Add(big[i]); }
                foreach (Item t in taken) big.Remove(t);                                       // the game takes items
                for (int i = 0; i < 5; i++) { Item a = new Item(); a.Id = 100000 + i; big.Insert(0, a); added.Add(a); } // and adds some
            }
        }
        Check(slices >= 700 / OverwatchSpreadCore.kMaxItemsPerSlice, "the big inventory took >= 5 slices (" + slices + ")");
        foreach (List<List<Item>> inv in inventories)
            foreach (List<Item> s in inv)
                foreach (Item it in s) Check(it.Checks == (added.Contains(it) ? 0 : 1), "item " + it.Id + " checked " + it.Checks + " times");
        if (gameChangesMidSplit) return;
        for (int k = 0; k < inventories.Count; k++)
        {
            Check(inventories[k].Count == reference[k].Count, "inventory " + k + ": same stacks left as the one-shot check");
            for (int s = 0; s < Math.Min(inventories[k].Count, reference[k].Count); s++)
            {
                Check(inventories[k][s].Count == reference[k][s].Count, "inventory " + k + " stack " + s + ": same items left");
                for (int i = 0; i < Math.Min(inventories[k][s].Count, reference[k][s].Count); i++)
                    Check(inventories[k][s][i].Id == reference[k][s][i].Id, "same item order");
            }
        }
    }

    // docs/145 §9: the world scan for inventories (was one tick over every object) runs kMaxScanPerSlice objects per tick;
    // 5,000 objects (every 7th with an inventory) -> ceil(5000/512) slices, each object checked once, same list as one pass.
    static void WorldScanSlices()
    {
        List<int> objects = new List<int>();
        for (int i = 0; i < 5000; i++) objects.Add(i);
        SlicedPass<int> scan = new SlicedPass<int>(objects);
        List<int> found = new List<int>();
        int slices = 0, most = 0;
        while (!scan.Done)
        {
            int n = scan.RunSlice(delegate(int o) { if (o % 7 == 0) found.Add(o); }, OverwatchSpreadCore.kMaxScanPerSlice, long.MaxValue, delegate { return 0; });
            if (n > most) most = n;
            slices++;
        }
        int want = (5000 + OverwatchSpreadCore.kMaxScanPerSlice - 1) / OverwatchSpreadCore.kMaxScanPerSlice;
        Check(slices == want && most <= OverwatchSpreadCore.kMaxScanPerSlice, "world scan: " + slices + " slices (want " + want + "), most " + most);
        Check(found.Count == (5000 + 6) / 7 && found[0] == 0 && found[found.Count - 1] == 4998, "world scan: same inventories as one pass, in order");
        SlicedPass<int> timed = new SlicedPass<int>(objects);
        Check(timed.RunSlice(delegate(int o) { }, OverwatchSpreadCore.kMaxScanPerSlice, EaSpreadCore.kSliceUs, delegate { return EaSpreadCore.kSliceUs; }) == 1, "world scan: time budget spent -> one object, then the tick ends");
    }

    static int Main()
    {
        WorldScanSlices();
        OverwatchSpreadCore.Mode ours = OverwatchSpreadCore.Mode.Ours, theirs = OverwatchSpreadCore.Mode.Theirs, up = OverwatchSpreadCore.Mode.Upstream;
        // overwatchstuck: our per-plan yield stands down only for an Overwatch that sleeps after each check (marker >= 2)
        Check(OverwatchSpreadCore.OurStuckYield(0) && OverwatchSpreadCore.OurStuckYield(1) && OverwatchSpreadCore.OurStuckYield(-1), "no marker, v1, faked -> our stuck yield");
        Check(!OverwatchSpreadCore.OurStuckYield(2) && !OverwatchSpreadCore.OurStuckYield(3), "marker v2+ -> theirs");
        // yield path: the marker (real or faked) wins over everything, also over a switch that is on
        Check(OverwatchSpreadCore.Decide(true, true, true, false) == up, "marker present, switch on -> upstream");
        Check(OverwatchSpreadCore.Decide(true, true, false, true) == up, "faked marker, switch on -> upstream");
        Check(OverwatchSpreadCore.Decide(false, false, true, false) == up, "marker present, hook absent -> upstream");
        Check(OverwatchSpreadCore.Decide(true, true, false, false) == ours, "switch on, hook in -> ours");
        Check(OverwatchSpreadCore.Decide(false, true, false, false) == theirs, "switch off -> theirs");
        Check(OverwatchSpreadCore.Decide(true, false, false, false) == theirs, "hook missing -> theirs");
        // overwatchchain only replaces Overwatch's own one-tick path
        OverwatchSpreadCore.Mode chain = OverwatchSpreadCore.Mode.Chain;
        Check(OverwatchSpreadCore.WithChain(theirs, true, true) == chain, "chain on, spread off -> chain");
        Check(OverwatchSpreadCore.WithChain(theirs, false, true) == theirs, "chain off -> theirs");
        Check(OverwatchSpreadCore.WithChain(theirs, true, false) == theirs, "chain on, hook missing -> theirs");
        Check(OverwatchSpreadCore.WithChain(ours, true, true) == ours, "spread and chain on -> spread");
        Check(OverwatchSpreadCore.WithChain(up, true, true) == up, "marker, chain on -> upstream");
        // chain inside Overwatch's own loop (docs/145 §11): a tick before every type but the first, only where a yield is allowed
        Check(!OverwatchSpreadCore.SleepBefore(0, true), "first type: no tick before it");
        Check(OverwatchSpreadCore.SleepBefore(1, true) && OverwatchSpreadCore.SleepBefore(20, true), "later types: a tick before each");
        Check(!OverwatchSpreadCore.SleepBefore(5, false), "no yield allowed: Overwatch's one-tick loop");
        Check(OverwatchSpreadCore.ChainType(false, true, true) == OverwatchSpreadCore.TypeRun.Theirs, "other types: Overwatch's own code");
        Check(OverwatchSpreadCore.ChainType(false, false, false) == OverwatchSpreadCore.TypeRun.Theirs, "other types, no yield: Overwatch's own code");
        Check(OverwatchSpreadCore.ChainType(true, true, true) == OverwatchSpreadCore.TypeRun.OursInventory, "inventory, setting on: our sliced pass");
        Check(OverwatchSpreadCore.ChainType(true, true, false) == OverwatchSpreadCore.TypeRun.Theirs, "inventory, no yield: Overwatch's own cleanup");
        Check(OverwatchSpreadCore.ChainType(true, false, true) == OverwatchSpreadCore.TypeRun.Nothing, "inventory, setting off: nothing");
        // Stuck Check slices (docs/145 §12): a tick once the stretch reaches the budget; a Sim gone meanwhile takes Overwatch's not-in-world branch
        Check(!OverwatchSpreadCore.SliceDue(0) && !OverwatchSpreadCore.SliceDue(OverwatchSpreadCore.kStuckSliceUs - 1), "stretch under budget: no tick");
        Check(OverwatchSpreadCore.SliceDue(OverwatchSpreadCore.kStuckSliceUs) && OverwatchSpreadCore.SliceDue(300000), "stretch at or over budget: tick");
        Check(OverwatchSpreadCore.kStuckSliceUs < 50000, "budget well under the 50 ms slow-type line");
        Check(!OverwatchSpreadCore.SkipAfterSlice(false, true, true), "Sim still in the world: Overwatch decides");
        Check(OverwatchSpreadCore.SkipAfterSlice(true, true, true), "Sim destroyed during the tick: skipped");
        Check(OverwatchSpreadCore.SkipAfterSlice(false, false, true), "Sim out of the world: skipped");
        Check(OverwatchSpreadCore.SkipAfterSlice(false, true, false), "Sim without proxy: skipped");
        Check(OverwatchSpreadCore.OurStuckSlice(0) && OverwatchSpreadCore.OurStuckSlice(-1) && OverwatchSpreadCore.OurStuckSlice(2), "no marker, faked or v1-v2: our walk ticks");
        Check(!OverwatchSpreadCore.OurStuckSlice(3) && !OverwatchSpreadCore.OurStuckSlice(4), "marker v3+: Overwatch sleeps in its walk, ours only measures");

        Check(OverwatchSpreadCore.HasMarker(typeof(OverwatchWithMarker)), "public static int marker found");
        Check(!OverwatchSpreadCore.HasMarker(typeof(OverwatchWithoutMarker)), "no marker");
        Check(!OverwatchSpreadCore.HasMarker(typeof(OverwatchWrongMarker)), "marker of another type ignored");
        Check(!OverwatchSpreadCore.HasMarker(typeof(OverwatchPrivateMarker)), "non-public marker ignored");
        Check(!OverwatchSpreadCore.HasMarker(null), "no Overwatch type");

        // every cleanup type gets its own slot, gap apart, the first at once
        for (int i = 1; i < 30; i++)
            Check(OverwatchSpreadCore.SlotMinutes(i, OverwatchSpreadCore.kGapMinutes) - OverwatchSpreadCore.SlotMinutes(i - 1, OverwatchSpreadCore.kGapMinutes) == OverwatchSpreadCore.kGapMinutes, "slot " + i + " gap");
        Check(OverwatchSpreadCore.SlotMinutes(0, OverwatchSpreadCore.kGapMinutes) == 0f, "first slot at once");
        Check(OverwatchSpreadCore.SlotMinutes(19, OverwatchSpreadCore.kGapMinutes) < 24 * 60, "20 types fit in one sim-day");

        GiantInventory(false);
        GiantInventory(true);

        Console.WriteLine(sFails == 0 ? "overwatchspread selftest: PASS" : "overwatchspread selftest: " + sFails + " FAIL");
        return sFails == 0 ? 0 : 1;
    }
}
