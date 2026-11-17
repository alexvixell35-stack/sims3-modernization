using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Academics;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Careers;
using Sims3.Gameplay.EventSystem;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Objects.Miscellaneous;
using Sims3.SimIFace;
using Sims3.UI;

namespace Ts3Fixes
{
    // bubblepipeguard harness check (docs/555 §7). Throwaway objects; nothing in the world changes.
    public static partial class Fixes
    {
        // BubblePipeCheck run: a throwaway BlowBubbles (uninitialized, never queued) in the failed-pipe state (state machine
        // set, pipe null). Off BROKEN = the guard declines (EA's NRE would escape Run); on FIXED = EA's exit taken; with a
        // pipe present the guard always declines (other NREs rethrown).
        public static object BubblePipeCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "bubblepipeguard: exits 2426=" + c[2426] + ", errors 2427=" + c[2427];
            if (what == "result") return counters;
            if (what != "run") return "bubblepipe <run|result>";
            if (sBbBlow == null) return "bubblepipe check: not patched (N/A)";
            InteractionInstance ia = (InteractionInstance)FormatterServices.GetUninitializedObject(sBbBlow);
            StateMachineClient smc = (StateMachineClient)FormatterServices.GetUninitializedObject(typeof(StateMachineClient));
            GC.SuppressFinalize(smc);  // never had a driver: its finalizer must not run
            ia.mCurrentStateMachine = smc;
            bool failCase = BbPipeFailed(ia);
            ia.mCurrentStateMachine = null;
            bool beforeEntry = BbPipeFailed(ia);
            string verdict = beforeEntry ? "UNEXPECTED" : failCase ? "FIXED" : "BROKEN";
            return "bubblepipe check (switch " + (ShortcutOn("bubblepipeguard") ? "on" : "off") + "): pipe-failure exit " + failCase + ", NRE before the state machine handled " + beforeEntry + "; verdict: " + verdict + "; " + counters;
        }
    }
}
