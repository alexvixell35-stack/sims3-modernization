using System;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;

namespace Ts3Fixes
{
    // Household rule (CLAUDE.md "Household Sims stay untouched", docs/249): every skip, teleport, cap, throttle, deadline,
    // fake or relaxed path needs a household AND selected-Sim exemption, and a Sim of unknown scope counts as household.
    // These two checks are that exemption; call them instead of writing IsSelectable / Household.ActiveHousehold by hand.
    //
    //   IsPlayerSim(s) true = exempt: null, destroyed, no SimDescription, no household, no selected Sim (so no active
    //   household: every Sim's scope is unknown), the selected Sim, any Sim of the active household (IsNeverSelectable
    //   members too), any selectable Sim, or any exception while asking.
    //   IsNpcSim(s) = !IsPlayerSim(s): a live Sim of a known other household; only those may be acted on.
    //
    // Game facts [verified, decompiled Sim.cs / Household.cs]: Household.ActiveHousehold = PlumbBob.SelectedActor?.Household;
    // Sim.Household = SimDescription?.Household; Sim.IsSelectable = selected Sim exists, same household, has a description and
    // is not IsNeverSelectable; Sim.IsInActiveHousehold compares households only, so with no selected Sim it is false for all.
    // Hand-written checks such as `s.Household == Household.ActiveHousehold` therefore treat household-less Sims (or every
    // Sim while no Sim is selected) as NPCs; these helpers do not.
    //
    // Deliberate exceptions keep their own code and call these for the Sim part: a gone Sim's description without a
    // household (DeadSimHolders.GoneOwnerHh), dead descriptions without a household (SaveCleanupSpread.OutsideActive).
    // Plain sim-thread getters only (no allocation), safe on hot paths.
    public static partial class Fixes
    {
        public static bool IsPlayerSim(Sim s)
        {
            try
            {
                if (s == null || s.HasBeenDestroyed) return true;
                SimDescription d = s.SimDescription;
                if (d == null || s == PlumbBob.SelectedActor) return true;
                return IsPlayerDesc(d) || s.IsSelectable;
            }
            catch (Exception) { return true; }
        }

        public static bool IsNpcSim(Sim s) { return !IsPlayerSim(s); }

        // docs/271 [Experimental] HouseholdToo (ts3native.ini, default 0, asked once a session through GetOption2 q 14 as replanlog's
        // q 10; an older ts3native answers false). HouseholdExempt is the shared predicate for the sites where the visible result stays
        // realistic with the flag on (docs/271 rows marked yes: worker answers, exact go-live); true = keep the exemption (vanilla).
        // Teleports, fakes, skips, cuts, resets and save cleanups keep IsPlayerSim and never change. Flag on: a live household,
        // selected or unknown-scope Sim is let through (counter 1057); null, destroyed or description-less Sims stay exempt.
        // New features whose household result stays realistic use this, not IsPlayerSim. Sim thread only (the first ask).
        static int sHhToo = -1;
        public static bool HouseholdTooOn()
        {
            if (sHhToo < 0) { try { sHhToo = HcWould(sYieldProbe, 14) ? 1 : 0; } catch (Exception) { sHhToo = 0; } }
            return sHhToo == 1;
        }

        public static bool HouseholdExempt(Sim s)
        {
            return IsPlayerSim(s) && !HouseholdLift(s);  // HouseholdTooCore.Exempt is its offline model
        }

        // the flag part alone, for a site that already knows the Sim is household scope (UfWhy kHousehold): true = let through, counted
        public static bool HouseholdLift(Sim s)
        {
            try
            {
                if (s == null || s.HasBeenDestroyed || s.SimDescription == null || !HouseholdTooOn()) return false;
            }
            catch (Exception) { return false; }
            PatchCounters()[1057]++;
            return true;
        }

        // The same rule for a SimDescription (a Sim that may not be instantiated): null, no household, no active household,
        // the active household, or a live Sim that is selected or selectable = exempt.
        public static bool IsPlayerDesc(SimDescription d)
        {
            try
            {
                if (d == null) return true;
                Household h = d.Household, act = Household.ActiveHousehold;
                if (h == null || act == null || h == act) return true;
                Sim c = d.CreatedSim;
                return c != null && (c == PlumbBob.SelectedActor || c.IsSelectable);
            }
            catch (Exception) { return true; }
        }
    }
}
