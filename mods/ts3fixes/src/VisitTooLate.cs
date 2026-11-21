using System;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Situations;

namespace Ts3Fixes
{
    // visittoolate (tuning kVisitTooLate, off by default; docs/565 §5). EA bug fix: NPC visitors told to leave late at
    // night stand in the yard for good.
    //
    // [verified, decompiled] VisitSituation.CheckIfItsTimeToLeave sets TimeForGuestToLeave(TooLate) once
    // IsTooLateToVisit() (also used for motive distress / host to work / misbehaving). Both host paths only move the
    // guest's household to the yard: a selectable host through MakeGuestHouseholdExitToYard, an NPC host through
    // TurnToFaceAndAskToLeave (GoWhere.LeaveHouse -> the same call). The state's 15-minute RemindGuestsToLeave then does:
    // all outside and not too late -> ArrivedOnLot (may be asked in again); all outside and too late -> nothing. While
    // the state is TimeForGuestToLeave, CheckIfItsTimeToLeave returns at once, and CheckForArrivingAndLeaving ends the
    // visit only once the guest is off the lot. Nothing in the visit ever sends the guest home, and the visit's OutsideLot
    // check scores everything off the lot down, so the NPC loiters in the yard all night (until morning, when
    // RemindGuestsToLeave moves the visit back to ArrivedOnLot).
    //
    // Fix: after EA's RemindGuestsToLeave, while that state is still the visit's current state, if every member of the
    // guest's household on the lot is outside and it is too late to visit, an NPC guest (not selectable, not a service
    // Sim) is sent home with EA's Sim.MakeSimGoHome, the same step EA's GuestLeavesAfterWaitingTooLong takes. A guest
    // already going home is left alone. Selectable guests are never touched.
    //
    // Counters: [2534] NPC guests sent home, [2535] errors. Check: VisitTooLateChecks.cs.
    public static partial class Fixes
    {
        static readonly Type sVtlState = typeof(VisitSituation).GetNestedType("TimeForGuestToLeave", BindingFlags.NonPublic | BindingFlags.Public);

        static void ApplyVisitTooLate()
        {
            if (sVtlState == null) { AppliedLog().Add("visittoolate: VisitSituation.TimeForGuestToLeave not found, not patched"); return; }
            Patch(sVtlState, "RemindGuestsToLeave", typeof(VtlHook), "RemindHook", "RemindOrig");
        }

        // ---- decision (also used by the check) ----
        internal static bool VtlSendHome(bool on, bool stateCurrent, bool guestNpc, bool isService, bool allOutside, bool tooLate, bool onLot, bool alreadyGoingHome)
        {
            return on && stateCurrent && guestNpc && !isService && allOutside && tooLate && onLot && !alreadyGoingHome;
        }

        // EA's TimeForGuestToLeave.AllGuestsHouseholdMembersOutside
        static bool VtlAllOutside(Sim guest, Lot lot)
        {
            if (guest == null || guest.Household == null) return true;
            foreach (Sim s in guest.Household.Sims)
                if (s.LotCurrent == lot && !s.IsOutside && (!s.SimDescription.IsGhost || s.SimDescription.IsPlayableGhost)) return false;
            return true;
        }

        public class VtlHook
        {
            public void RemindHook()
            {
                RemindOrig();
                if (!ShortcutOn("visittoolate")) return;  // fast path: switch off
                try
                {
                    object st = this;
                    VisitSituation visit = ((ChildSituation<VisitSituation>)st).Parent;
                    if (visit == null) return;
                    Sim guest = visit.Guest;
                    if (guest == null || guest.HasBeenDestroyed) return;
                    bool current = !visit.SituationExited && object.ReferenceEquals(visit.Child, st);
                    if (!VtlSendHome(true, current, !guest.IsSelectable, guest.Service != null, VtlAllOutside(guest, visit.Lot), VisitSituation.IsTooLateToVisit(),
                                     guest.LotCurrent == visit.Lot, guest.InteractionQueue == null || guest.InteractionQueue.HasInteractionOfType(GoHome.Singleton))) return;
                    PatchCounters()[2534]++;
                    Sim.MakeSimGoHome(guest, false);
                }
                catch (Exception) { PatchCounters()[2535]++; }
            }
            public void RemindOrig() { } // receives VisitSituation.TimeForGuestToLeave.RemindGuestsToLeave's original body
        }
    }
}
