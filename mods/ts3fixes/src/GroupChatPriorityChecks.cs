using System;
using Sims3.Gameplay.Interactions;

namespace Ts3Fixes
{
    // Harness check for GroupChatPriority.cs (docs/638 §3). Pure decision with controls; nothing in the world is touched.
    public static partial class Fixes
    {
        // GroupChatPriorityCheck run: an autonomous group chat's GroupChatB, still queued behind the receiver's autonomous
        //   head, is raised and then cancels that head (EA's RemoveLowerPriorityInteractions rule, as CreateSocialB does).
        //   Controls: a player-ordered head and a MustRun head are kept, an already raised or player-directed receiver is
        //   unchanged, a receiver no longer queued or already the head is unchanged, an unraised receiver keeps EA's wait.
        public static object GroupChatPriorityCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "groupchatpriority: receivers raised 3841=" + c[3841] + ", seen with switch off 3842=" + c[3842] + ", errors 3843=" + c[3843]
                + "; " + GlMembers(sGcpMissing);
            if (what == "result") return counters;
            if (what != "run") return "groupchatpriority <run|result>";
            bool on = ShortcutOn("groupchatpriority");
            InteractionPriorityLevel A = InteractionPriorityLevel.Autonomous, N = InteractionPriorityLevel.NonCriticalNPCBehavior,
                U = InteractionPriorityLevel.UserDirected;
            bool raised = SpRaise(on, true, false, A);
            bool fixedNow = raised && SpCancelsHead(raised ? N : A, A, false);
            bool ctlUserHead = !SpCancelsHead(N, U, false);
            bool ctlMustRun = !SpCancelsHead(N, A, true);
            bool ctlRaised = !SpRaise(on, true, false, N);
            bool ctlUser = !SpRaise(on, true, false, U);
            bool ctlGone = !SpRaise(on, false, false, A);
            bool ctlHead = !SpRaise(on, true, true, A);
            bool ctlVanilla = !SpCancelsHead(A, A, false);
            bool controls = ctlUserHead && ctlMustRun && ctlRaised && ctlUser && ctlGone && ctlHead && ctlVanilla;
            return "groupchatpriority check (switch " + (on ? "on" : "off") + "): queued autonomous GroupChatB raised and cancels an autonomous head=" + fixedNow
                + "; control: player-ordered head kept=" + ctlUserHead + ", MustRun head kept=" + ctlMustRun
                + ", already raised unchanged=" + ctlRaised + ", player-directed unchanged=" + ctlUser + ", no longer queued unchanged=" + ctlGone
                + ", receiver is head unchanged=" + ctlHead + ", unraised receiver keeps EA's wait=" + ctlVanilla + "; controls ok=" + controls
                + "; verdict: " + GlVerdict(on, fixedNow, controls, sGcpMissing.Length == 0) + "; " + counters;
        }
    }
}
