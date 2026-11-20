using System;
using System.Collections.Generic;
using Sims3.Gameplay.Objects.Toys;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Harness checks for DollHouseSlots.cs (docs/575 §9). "run": pure decisions with controls. "vacate": on the newest
    // dollhouse in the world, takes a play slot and frees it twice, in the order Play.Run and Play.Cleanup do, then
    // restores the list. "state": every dollhouse's free-slot count (more than 2 = the bug, already in that save).
    public static partial class Fixes
    {
        public static object DollHouseSlotsCheck(string what)
        {
            string miss = EpMiss("DollHouse.mAvailablePlaySlots", sDhSlots, "DollHouse.mBeingSmashed", sDhSmashing, "DollHouse+Smash", sDhSmash);
            string counters = B582Counters("dollhouseslots", 2110, new[] { "duplicate slots skipped or dropped", "smash flags cleared", "errors" }, miss);
            if (what == "result") return counters + "; " + DhState();
            if (what == "state") return DhState();
            if (what == "vacate") return DhVacateTwice();
            if (what != "run") return "dollhouseslots <run|vacate|state|result>";
            bool on = ShortcutOn("dollhouseslots");
            List<Slot> rep = DhRepair(new List<Slot> { Slot.RoutingSlot_2, Slot.RoutingSlot_1, Slot.RoutingSlot_2, Slot.RoutingSlot_2 });
            bool fixedNow = DhSkipVacate(on, true, true) && rep.Count == 2;
            bool controls = !DhSkipVacate(on, true, false) && !DhSkipVacate(on, false, true) && DhRepair(new List<Slot> { Slot.RoutingSlot_1 }).Count == 2;
            return B582Line("dollhouseslots", on, "second free of a slot skipped, saved duplicates dropped", fixedNow,
                "first free of a cut play / Slot.None left to EA, a missing slot restored=" + controls, controls, miss, counters);
        }

        static DollHouse DhNewest()
        {
            DollHouse best = null;
            foreach (DollHouse d in Sims3.Gameplay.Queries.GetObjects<DollHouse>())
                if (d.InWorld && (best == null || d.ObjectId.Value > best.ObjectId.Value)) best = d;
            return best;
        }

        static string DhVacateTwice()
        {
            DollHouse d = DhNewest();
            if (d == null || sDhSlots == null) return "no dollhouse in the world";
            List<Slot> list = sDhSlots.GetValue(d) as List<Slot>;
            if (list == null) return "dollhouse " + d.ObjectId.Value + ": no slot list";
            List<Slot> saved = new List<Slot>(list);
            int before = list.Count;
            bool took = d.AcquireSlot(Slot.RoutingSlot_2);
            d.VacateSlot(Slot.RoutingSlot_2);   // Play.Run's end
            d.VacateSlot(Slot.RoutingSlot_2);   // Play.Cleanup
            int after = ((List<Slot>)sDhSlots.GetValue(d)).Count;
            sDhSlots.SetValue(d, saved);
            return "dollhouse " + d.ObjectId.Value + ": took slot=" + took + ", free slots " + before + " -> " + after + " after Run + Cleanup"
                + (after > before ? " (duplicate: BROKEN)" : " (no duplicate: FIXED)") + "; list restored";
        }

        static string DhState()
        {
            if (sDhSlots == null) return "members missing";
            int n = 0, over = 0, max = 0, smashing = 0;
            foreach (DollHouse d in Sims3.Gameplay.Queries.GetObjects<DollHouse>())
            {
                List<Slot> list = sDhSlots.GetValue(d) as List<Slot>;
                int c = list == null ? 0 : list.Count;
                n++; if (c > 2) over++; if (c > max) max = c;
                if (sDhSmashing != null && (bool)sDhSmashing.GetValue(d)) smashing++;
            }
            return "world: dollhouses " + n + ", with more than 2 free slots " + over + " (largest list " + max + "), smash flag set " + smashing;
        }
    }
}
