using System;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Socializing;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // standwaitsocial (docs/686 §30, default off). A social's walk to its jig spot that cannot be planned without people
    // either stands and waits to the timeout.
    //
    // [verified, storm logs s543a, s545a] Bridgeport, upper floor of the Wise / Girl / Missoni / Crewe lot: a receiver
    // (SocialInteractionB) stood up from the computer chair at 502.5,34.1,1535.2 to 501.7-501.9,34.1,1535.4 with its starter
    // 0.2-0.3 m away and its jig spot 0.4 m away. Every replan failed at once (no path, 0 plans), standwaitfix's
    // people-free passes failed too (4 plans), and the wait ran 20.8-21.6 sim-minutes to its timeout; socialreach then let
    // it talk from where it stood. The jig was placed while the receiver sat; the stand-up lands it where the spot cannot
    // be reached. EA's code, made frequent by partnerpull (which brings a seated receiver over at once); never seen in the
    // all-off replay s544a or in ~150 storms without partnerpull.
    // Fix, in standwaitfix's blocked cycle (StandWait.cs SwfCycle): after kStandWaitSocialPasses (2) failed people-free
    // passes of a waiter whose running interaction is SocialInteractionA or B, the wait ends the way the timeout ends it
    // (the handler answers "not elected to exit", the route fails), as standwaitlocked does for a privacy lock. Waits
    // blocked by people only (the pass works) are not touched. Needs standwaitfix on (the passes are its plans).
    // Scope: standwaitsocial for NPCs, standwaitsocialhh for household and selected Sims. Off = compare mode (would).
    // Counters 4687-4691. Check: StandWaitSocialCheck. No swap (StandWait.cs's cycle hook).
    public static partial class Fixes
    {
        const int kSwsWaits = 4687, kSwsGaveUp = 4688, kSwsWould = 4689, kSwsExempt = 4690, kSwsErrors = 4691;

        static bool SwsSocial(Sim sim)
        {
            InteractionInstance ia = sim.CurrentInteraction;
            return ia is SocialInteractionA || ia is SocialInteractionB;
        }

        // A people-free pass of this wait failed: does the wait end now? true = end it the way the timeout does.
        static bool SwsPassFailed(SwfState st, Sim sim, bool yieldDue, int[] c)
        {
            try
            {
                if (!SwsSocial(sim)) return false;
                st.SocFails++;
                if (st.SocFails == 1) c[kSwsWaits]++;
                bool fixOn = ShortcutOn("standwaitsocial");
                bool inScope = fixOn && SwfInScope(sim, ShortcutOn("standwaitsocialhh"));
                switch (StandWaitSocialCore.Decide(true, st.SocFails, Instantiator.kStandWaitSocialPasses, fixOn, inScope, yieldDue))
                {
                    case StandWaitSocialCore.kWould:
                        if (st.SocWould == 0) { st.SocWould = st.Blocked; c[kSwsWould]++; }
                        break;
                    case StandWaitSocialCore.kExempt:
                        if (!st.SocExempt) { st.SocExempt = true; c[kSwsExempt]++; }
                        break;
                    case StandWaitSocialCore.kGiveUp:
                        st.SocGaveUp = st.Blocked;
                        c[kSwsGaveUp]++;
                        return true;
                }
            }
            catch (ResetException) { throw; }
            catch (Exception) { c[kSwsErrors]++; }
            return false;
        }

        static string SwsEnd(SwfState st)
        {
            if (st.SocFails == 0) return "";
            return ", social: " + st.SocFails + " failed people-free passes, " + (st.SocGaveUp > 0 ? "gave up cycle " + st.SocGaveUp
                : st.SocExempt ? "exempt (household)" : st.SocWould > 0 ? "would give up cycle " + st.SocWould : "kept (under " + Instantiator.kStandWaitSocialPasses + " passes)");
        }

        // StandWaitSocialCheck result: the counters and the last wait's line. run: the rules.
        public static object StandWaitSocialCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "standwaitsocial: social waits with a failed people-free pass " + c[kSwsWaits] + "; on: gave up " + c[kSwsGaveUp] + ", exempt " + c[kSwsExempt]
                + "; off: would give up " + c[kSwsWould] + "; errors " + c[kSwsErrors] + "; give up after " + StandWaitSocialCore.Passes(Instantiator.kStandWaitSocialPasses) + " failed passes";
            if (what == "result") return counters + (sSwfLast.Length > 0 ? "; last standwait: " + sSwfLast : "");
            if (what != "run") return "standwaitsocial <run|result>";
            bool ok = StandWaitSocialCore.Decide(true, 2, 2, true, true, false) == StandWaitSocialCore.kGiveUp
                && StandWaitSocialCore.Decide(true, 1, 2, true, true, false) == StandWaitSocialCore.kKeep
                && StandWaitSocialCore.Decide(false, 9, 2, true, true, false) == StandWaitSocialCore.kKeep;
            return "standwaitsocial: rules " + (ok ? "ok" : "FAILED") + ", switch " + (ShortcutOn("standwaitsocial") ? "on" : "off") + (ShortcutOn("standwaitsocialhh") ? " (+hh)" : "")
                + ", standwaitfix " + (ShortcutOn("standwaitfix") ? "on" : "off (no passes, nothing to count)") + "; " + counters;
        }
    }
}
