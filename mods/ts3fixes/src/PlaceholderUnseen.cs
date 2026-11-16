using System;
using Sims3.Gameplay;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems.Children;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // placeholderunseen (contributor, default off; NPC households only): EA's CaregiverRoutingMonitor [ld-SGS 669127-669460]
    // turns an NPC household's babies and toddlers into CaregiverRoutingMonitor.ChildPlaceholder objects (DematerializeChildren:
    // the Sim is destroyed, the description stays) when its alarm finds no caregiver left on the home lot (sim.IsNPC branch,
    // caregiverCount 0, Lot == LotHome). The placeholder's 5 sim-minute alarm, CheckForCaregiver, rematerializes the child
    // (Instantiate + SwitchToOutfitWithoutSpin: a full Sim build) as soon as any teen-or-older Sim of the household stands on
    // the lot. The monitor restarts whenever such a Sim starts a route with a toddler on its lot (SimScriptAdaptor.
    // OnAboutToStartFollowingRoute, ld-SGS 659999) and dematerializes again when the last one leaves, so every adult round trip rebuilds every
    // child (jj1 census by:Placeholder.Rematerialize<ChildPlaceholder.Rematerialize: IP 76 builds of 4 children, TB 30, BP 23).
    // The two tests also differ: the monitor does not count a Sim running an IInteractionProhibitsChildCare interaction
    // (teleports), an EP11 bot without the caregiver chip, or an apartment Sim heading from a public room to a private one;
    // CheckForCaregiver counts any teen-or-older household Sim on the lot, so such a Sim on the lot flips the child both ways.
    // With the switch: CheckForCaregiver keeps the placeholder while the home lot is unseen (not UfLotSeen, and the lot circle
    // plus kPhMarginM out of view) and no active household or selected Sim is on it; EA's own loop runs when the lot is seen
    // or near, so the child comes back within one 5-minute alarm of the camera coming close.
    // What needs the real Sim, from EA's code: moving (TryInstantiateSim calls Rematerialize directly), the social worker
    // (GatherPlaceholderKids moves the description and destroys the placeholder), the household becoming active
    // (PrepareToBecomeActiveHousehold instantiates every description without a Sim), all untouched (they do not go through
    // CheckForCaregiver). Aging needs no Sim: AgeUp.Execute ages an uninstantiated description (AgeTransitionWhileUninstantiated);
    // the placeholder object and its alarm are EA's own saved state. While kept, the caregivers' child care, the child's motives,
    // skills and socials do not run, as in EA's placeholder state when the caregivers are away.
    // Not taken (EA runs): the switch off, not live, travelling, world going away, an invalid description (EA destroys the
    // placeholder), a description already instantiated, the active household (or no household), a world-lot placeholder, or
    // no teen-or-older household Sim on the lot (EA does nothing then either).
    // Counters: [3728] asked (EA would rematerialize now), [3729] kept, [3730] let EA rematerialize (seen, near or household on
    // the lot), [3731] the checks threw (EA's loop ran).
    // Cleanup: no loop of our own beyond the one GetSims pass EA also does, no retry. EA's CheckForCaregiver (not changed):
    // after Destroy() on an invalid description it does not return, so it still walks the lot and may call Rematerialize on a
    // destroyed placeholder (Rematerialize then only calls Destroy again). Sim.Placeholder.DoDematerialize loops
    // FinishInteractionsOnLot + Sleep(60) with no cap while the child keeps a current interaction.
    public static partial class Fixes
    {
        static void ApplyPlaceholderUnseen()
        {
            Patch(typeof(CaregiverRoutingMonitor.ChildPlaceholder), "CheckForCaregiver", typeof(PhCheckHook), "CheckHook", "CheckOrig");
        }

        public class PhCheckHook
        {
            public void CheckHook()
            {
                if (ShortcutOn("placeholderunseen") && PhKeep((Sim.Placeholder)(object)this)) return;
                CheckOrig();
            }
            public void CheckOrig() { } // receives CaregiverRoutingMonitor.ChildPlaceholder.CheckForCaregiver's original header
        }

        const float kPhMarginM = 40f;

        static bool PhKeep(Sim.Placeholder p)
        {
            int[] c = PatchCounters();
            try
            {
                if (p == null || !GameStates.IsLiveState || GameStates.IsTravelling || WorldGone()) return false;
                SimDescription sd = p.SimDescription;
                if (sd == null || !sd.IsValidDescription || sd.CreatedSim != null) return false;
                Household h = sd.Household;
                if (h == null || h == Household.ActiveHousehold) return false;
                Lot lot = p.LotCurrent;
                if (lot == null || lot.IsWorldLot) return false;
                bool caregiver = false;  // EA's own test in CheckForCaregiver
                foreach (Sim s in lot.GetSims())
                    if (s != null && s.Household == h && s.SimDescription != null && s.SimDescription.TeenOrAbove) { caregiver = true; break; }
                if (!caregiver) return false;
                c[3728]++;
                Vector3 ctr;
                float r;
                UfLotShape(lot, SimClock.CurrentTicks, out ctr, out r);
                if (FmsHhOn(lot) || UfLotSeen(lot) || !UfPosUnseen(ctr, r + kPhMarginM)) { c[3730]++; return false; }
                c[3729]++;
                return true;
            }
            catch (ResetException) { throw; }
            catch (Exception) { c[3731]++; return false; }
        }
    }
}
