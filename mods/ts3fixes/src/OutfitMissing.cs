using System;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.SimIFace;
using Sims3.SimIFace.CAS;

namespace Ts3Fixes
{
    // outfitmissing (local 2026-11-18, default off).
    // [verified, decompiled] Sim.SwitchOutfitHelper.Init(Sim, OutfitCategories, int) reads
    // sim.SimDescription.GetOutfit(category, index) and compares its Key with the current outfit's: a missing outfit
    // (GetOutfit returns null) throws a NullReferenceException there, the interaction ends in ScriptProxy's error path.
    // Natural case, 6 of 6 pregnant Sims snorkeling at Bella Vista Beach (Isla Paradiso, 2026-11-17/18): for a pregnant Sim
    // (SimDescriptionCore.IsUsingMaternityOutfits) AddSpecialOutfit takes its index from GetOutfitCount(Special), which
    // counts the maternity list, and stores the outfit in the maternity list (AddOutfitInternal bMaternity), but
    // GetOutfit(Special, i) reads the normal list (AllowOutfitCategoryForPregnancy(Special) is false). The snorkel outfit
    // made by Sim.SwitchToOutfitWithSwimmingSpin("SnorkelOutfit", ...) is therefore not found and the helper throws.
    // Fix: when Init throws a NullReferenceException and the outfit (or the Sim's current outfit) is really missing, the
    // helper is marked no-change (mNoChange, as Init does for an outfit the Sim already wears): the Sim keeps its current
    // outfit and the interaction goes on. Fields Init sets before the throw (mInitMethod, mSim, mCategory, mIndex) are
    // already set. Any other exception, and a NullReferenceException with the outfit present, is rethrown. No cost on the
    // normal path (one try block). An EA bug fix: applies to every Sim (EA throws for household Sims too).
    // Not fixed: a pregnant Sim whose normal Special list is not empty gets the wrong special outfit at that index (no throw).
    // Counters [3985] missing outfits guarded, [3986] missing outfit with the switch off (EA throws), [3987] errors in the
    // guard (the original exception is rethrown).
    public static partial class Fixes
    {
        public class OutfitMissingHook
        {
            public void InitHook(Sim sim, OutfitCategories category, int index)
            {
                try { InitOrig(sim, category, index); }
                catch (NullReferenceException)
                {
                    if (!OmMissing(sim, category, index)) throw;
                    if (!ShortcutOn("outfitmissing")) { PatchCounters()[3986]++; throw; }
                    object h = this;  // `this` is really a Sim.SwitchOutfitHelper
                    try { sSohNoChange.SetValue(h, true); PatchCounters()[3985]++; }
                    catch (Exception) { PatchCounters()[3987]++; throw; }
                    if (sOmLog < 5) { sOmLog++; ShortcutLog("outfitmissing: " + sim.FullName + " " + category + " #" + index + (sim.SimDescription.IsUsingMaternityOutfits ? " (maternity outfits)" : "") + ": no outfit, kept the current one"); }
                }
            }
            public void InitOrig(Sim sim, OutfitCategories category, int index) { } // receives SwitchOutfitHelper.Init(Sim, OutfitCategories, int)'s original body
        }
        static int sOmLog;

        static bool OmMissing(Sim sim, OutfitCategories category, int index)
        {
            try
            {
                if (sSohNoChange == null || sim == null) return false;
                SimDescription sd = sim.SimDescription;
                return sd != null && (sd.GetOutfit(category, index) == null || sim.CurrentOutfit == null);
            }
            catch (Exception) { PatchCounters()[3987]++; return false; }
        }

        // Harness check (repro outfitmissing-check; reflect_call Ts3Fixes.Fixes.OutfitMissingCheck "expect broken"|"expect
        // fixed"): a SwitchOutfitHelper for a Special outfit index the active Sim does not have (the same null GetOutfit as
        // the maternity case). Off: EA throws the NullReferenceException. On: no throw, the helper reports no change, and it
        // is disposed without touching the Sim.
        public static object OutfitMissingCheck(string what)
        {
            Sim sim = Sim.ActiveActor;
            if (sim == null || sSohNoChange == null) throw new InvalidOperationException("outfitmissing check: no active Sim or mNoChange");
            int index = sim.SimDescription.GetOutfitCount(OutfitCategories.Special) + 50;
            int fixedN = PatchCounters()[3985], seen = PatchCounters()[3986], err = PatchCounters()[3987];
            string thrown = "none"; bool willChange = true;
            try
            {
                Sim.SwitchOutfitHelper h = new Sim.SwitchOutfitHelper(sim, OutfitCategories.Special, index);
                willChange = h.WillChange;
                h.Dispose();
            }
            catch (Exception e) { thrown = e.GetType().Name; }
            bool on = ShortcutOn("outfitmissing");
            int dFixed = PatchCounters()[3985] - fixedN, dSeen = PatchCounters()[3986] - seen, dErr = PatchCounters()[3987] - err;
            bool ok = what == "expect broken" ? !on && thrown == "NullReferenceException" && dSeen == 1 && dFixed == 0
                : on && thrown == "none" && !willChange && dFixed == 1 && dSeen == 0 && dErr == 0;
            string r = (ok ? "as expected (" : "WRONG (") + what + "); switch " + on + ", thrown " + thrown + ", will change " + willChange
                + ", guarded " + dFixed + ", EA throws (off) " + dSeen + ", errors " + dErr + ", Sim " + sim.FullName + ", index " + index;
            if (!ok) throw new InvalidOperationException("outfitmissing check: " + r);
            return r;
        }
    }
}
