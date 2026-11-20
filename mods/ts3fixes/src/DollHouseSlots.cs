using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Objects.Toys;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // dollhouseslots (kDollHouseSlots, docs/575 §9) [verified in the scripts]: two dollhouse (toys) bugs.
    //   - DollHouse.Play frees its play slot twice: Run ends with Target.VacateSlot(mPlaySlot), and Cleanup calls
    //     VacateSlot(mPlaySlot) again (mPlaySlot is never set back to Slot.None). VacateSlot is a plain Add to the saved
    //     ([Persistable]) free-slot list mAvailablePlaySlots, which only OnCreation fills (RoutingSlot_2, RoutingSlot_1).
    //     So every finished play adds a duplicate: IsFull (Count == 0) stops working, more than two children play at once
    //     and two can take the same slot (stacked on one spot), IsEmpty (Count >= 2) is always true (Smash allowed while
    //     children play, "Play" instead of "Join"), and the list grows with every play for good.
    //   - DollHouse.Smash sets mBeingSmashed (StartSmashing) and clears it (FinishSmashing) only when the loop ends
    //     normally or the smash animation event 301 already changed the geometry. A cancel before that event, or a
    //     reset, leaves it set: IsAvailableToPlay and IsAvailableToSmash then refuse, so the dollhouse is greyed out.
    //     Smash has no Cleanup and DollHouse has no DoReset.
    //   Fix: VacateSlot does nothing when the slot is already free; after Smash.Run (normal end or reset) the flag is
    //   cleared (only one Sim can smash at a time); at world load, an idle dollhouse gets its two free slots back
    //   (duplicates dropped) and its smash flag cleared. Objects only; no Sim is touched.
    //   Counters [2110] duplicate slots skipped or dropped, [2111] smash flags cleared, [2112] errors.
    //   Checks: DollHouseSlotsChecks.cs.
    public static partial class Fixes
    {
        const BindingFlags kDhAll = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        static readonly FieldInfo sDhSlots = typeof(DollHouse).GetField("mAvailablePlaySlots", kDhAll);
        static readonly FieldInfo sDhSmashing = typeof(DollHouse).GetField("mBeingSmashed", kDhAll);
        static readonly Type sDhSmash = typeof(DollHouse).GetNestedType("Smash", BindingFlags.NonPublic | BindingFlags.Public);

        // Pure: does VacateSlot skip? Only a real slot that is already in the free list.
        internal static bool DhSkipVacate(bool on, bool realSlot, bool alreadyFree)
        {
            return on && realSlot && alreadyFree;
        }

        // Pure: the free list an idle dollhouse gets at load: its distinct entries, plus the two play slots when missing.
        internal static List<Slot> DhRepair(List<Slot> list)
        {
            List<Slot> r = new List<Slot>();
            if (list != null) foreach (Slot s in list) if (s != Slot.None && !r.Contains(s)) r.Add(s);
            if (!r.Contains(Slot.RoutingSlot_2)) r.Add(Slot.RoutingSlot_2);
            if (!r.Contains(Slot.RoutingSlot_1)) r.Add(Slot.RoutingSlot_1);
            return r;
        }

        static void ApplyDollHouseSlots()
        {
            Patch(typeof(DollHouse), "VacateSlot", new Type[] { typeof(Slot) }, typeof(DhHook), "VacateHook", "VacateOrig");
            if (sDhSmash != null) Patch(sDhSmash, "Run", Type.EmptyTypes, typeof(DhSmashHook), "RunHook", "RunOrig");
            else AppliedLog().Add("dollhouseslots: not found: DollHouse+Smash");
        }

        public class DhHook
        {
            public void VacateHook(Slot slot)
            {
                try
                {
                    List<Slot> list = sDhSlots == null ? null : sDhSlots.GetValue(this) as List<Slot>;
                    if (list != null && DhSkipVacate(ShortcutOn("dollhouseslots"), slot != Slot.None, list.Contains(slot)))
                    {
                        PatchCounters()[2110]++;
                        return;
                    }
                }
                catch (ResetException) { throw; }
                catch (Exception) { PatchCounters()[2112]++; }
                VacateOrig(slot);
            }
            public void VacateOrig(Slot slot) { } // DollHouse.VacateSlot
        }

        public class DhSmashHook
        {
            public bool RunHook()
            {
                try { return RunOrig(); }
                finally
                {
                    try
                    {
                        DollHouse d = ((Sims3.Gameplay.Interactions.InteractionInstance)(object)this).Target as DollHouse;
                        if (d != null && sDhSmashing != null && ShortcutOn("dollhouseslots") && (bool)sDhSmashing.GetValue(d))
                        {
                            sDhSmashing.SetValue(d, false);
                            PatchCounters()[2111]++;
                        }
                    }
                    catch (Exception) { PatchCounters()[2112]++; }
                }
            }
            public bool RunOrig() { return false; } // DollHouse+Smash.Run
        }

        // World load finished (Instantiator): idle dollhouses saved with duplicate or missing free slots or a set smash flag.
        public static void DollHouseSlotsWorldLoad()
        {
            try
            {
                if (!ShortcutOn("dollhouseslots") || sDhSlots == null) return;
                foreach (DollHouse d in Sims3.Gameplay.Queries.GetObjects<DollHouse>())
                {
                    try
                    {
                        if (d == null || d.UseCount > 0) continue;
                        List<Slot> list = sDhSlots.GetValue(d) as List<Slot>;
                        List<Slot> r = DhRepair(list);
                        if (list == null || list.Count != r.Count)
                        {
                            PatchCounters()[2110] += Math.Max(1, (list == null ? 0 : list.Count) - r.Count);
                            sDhSlots.SetValue(d, r);
                        }
                        if (sDhSmashing != null && (bool)sDhSmashing.GetValue(d)) { sDhSmashing.SetValue(d, false); PatchCounters()[2111]++; }
                    }
                    catch (Exception) { PatchCounters()[2112]++; }
                }
            }
            catch (Exception) { PatchCounters()[2112]++; }
        }
    }
}
