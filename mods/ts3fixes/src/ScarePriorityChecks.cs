using System;
using Sims3.Gameplay.Interactions;

namespace Ts3Fixes
{
    // Harness check for ScarePriority.cs (docs/580 §14). Pure decision with controls; nothing in the world is touched.
    public static partial class Fixes
    {
        // ScarePriorityCheck run: an autonomous scare's receiver, still queued behind the target's autonomous head, is
        //   raised and then cancels that head (EA's RemoveLowerPriorityInteractions rule).
        //   Controls: a player-ordered (UserDirected) head is not cancelled by the raised receiver, a MustRun head is not
        //   cancelled, an already raised receiver (NonCriticalNPCBehavior) and a player-directed one (UserDirected) are
        //   unchanged, a receiver no longer queued or already the head is unchanged.
        public static object ScarePriorityCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "scarepriority: receivers raised 1106=" + c[3541] + ", seen with switch off 1107=" + c[3542] + ", errors 1108=" + c[3543]
                + "; " + GlMembers(sSpMissing);
            if (what == "result") return counters;
            if (what != "run") return "scarepriority <run|result>";
            bool on = ShortcutOn("scarepriority");
            InteractionPriorityLevel A = InteractionPriorityLevel.Autonomous, N = InteractionPriorityLevel.NonCriticalNPCBehavior,
                U = InteractionPriorityLevel.UserDirected;
            bool raised = SpRaise(on, true, false, A);
            InteractionPriorityLevel after = raised ? N : A;
            bool fixedNow = raised && SpCancelsHead(after, A, false);
            bool ctlUserHead = !SpCancelsHead(N, U, false);
            bool ctlMustRun = !SpCancelsHead(N, A, true);
            bool ctlRaised = !SpRaise(on, true, false, N);
            bool ctlUser = !SpRaise(on, true, false, U);
            bool ctlGone = !SpRaise(on, false, false, A);
            bool ctlHead = !SpRaise(on, true, true, A);
            bool ctlVanilla = !SpCancelsHead(A, A, false);
            bool controls = ctlUserHead && ctlMustRun && ctlRaised && ctlUser && ctlGone && ctlHead && ctlVanilla;
            return "scarepriority check (switch " + (on ? "on" : "off") + "): queued autonomous receiver raised and cancels an autonomous head=" + fixedNow
                + "; control: player-ordered head kept=" + ctlUserHead + ", MustRun head kept=" + ctlMustRun
                + ", already raised unchanged=" + ctlRaised + ", player-directed unchanged=" + ctlUser + ", no longer queued unchanged=" + ctlGone
                + ", receiver is head unchanged=" + ctlHead + ", unraised receiver keeps EA's wait=" + ctlVanilla + "; controls ok=" + controls
                + "; verdict: " + GlVerdict(on, fixedNow, controls, sSpMissing.Length == 0) + "; " + counters;
        }
    }
}
